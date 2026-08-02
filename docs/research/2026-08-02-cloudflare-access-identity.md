# Cloudflare Access identity for the NAFF dashboard + Rust backend

**Date:** 2026-08-02
**Deployment under study:** Next.js dashboard + Rust/Axum backend behind a *named* Cloudflare Tunnel, with a Cloudflare Access self-hosted application in front, Google as the IdP. The app has no auth of its own today.
**Question driving the research:** can we show the signed-in user in the sidebar by calling `/cdn-cgi/access/get-identity` from the browser, and do backend writes (support tickets + comments) need server-side identity verification?

**Source policy:** every factual claim below links to a Cloudflare-owned page (`developers.cloudflare.com`, `blog.cloudflare.com`), a Cloudflare-published package artifact, crates.io / docs.rs, or a named crate's own source repository. Where a claim is *inference* rather than documentation, it is labelled **[INFERENCE]** inline. Nothing here is sourced from Medium, Stack Overflow, or third-party tutorials.

> **Ground truth from the live deployment** (given, not re-verified here): `GET /cdn-cgi/access/get-identity` through the tunnel returns JSON with top-level fields `id, name, email, idp{id,type}, geo{country}, user_uuid, account_id, iat, ip, auth_status, common_name, service_token_status, is_warp, is_gateway, version`, with `idp.type = "google"`, and `iat = 0`, `ip = ""`, `auth_status = "NONE"`, `version = 0`. There is no avatar/picture field.

---

## Answers at a glance

| # | Question | Short answer |
|---|---|---|
| 1 | `Cf-Access-Jwt-Assertion` | Access injects it on **every** authenticated request to the origin; browsers additionally get it as the `CF_Authorization` cookie, but Cloudflare says to **prefer the header** because the cookie "is not guaranteed to be passed". JWKS at `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`, **RS256** only. Documented claims: `aud` (array), `email`, `exp`, `iat`, `nbf`, `iss`, `type`, `identity_nonce`, `sub`, `country`, plus optional `custom`. Verify: match `kid` → key from `public_certs`/`keys`, check signature, check `iss` == team domain, check `aud` contains your **Application Audience (AUD) Tag** (Zero Trust → Access controls → Applications → Configure → Additional settings). ([source](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)) |
| 2 | Rust crates | One Access-specific crate exists: **`cf-access` 0.2.0** — tiny (1,329 all-time downloads, last release 2025-03-30, single-author). Generic path is **`jsonwebtoken` 11.0.0** (162M downloads, released 2026-07-24) — it ships a `jwk` module and `DecodingKey::from_jwk`, but does **not** fetch JWKS over HTTP; pair it with `reqwest` (~15 lines) or the `jwks` crate. Recommendation: `jsonwebtoken` + `reqwest` + your own cache. |
| 3 | `/cdn-cgi/access/logout` | Documented. Revokes the session **across all applications** (no per-app logout exists). Two URLs: app domain and team domain — the only difference is which domain the cookie is deleted from. Tokens stop being accepted in 20–30 s. Cloudflare explicitly says you may use these URLs "to create custom logout buttons or links directly within your application". **No redirect query parameter is documented**, and Cloudflare's own SDK builds the logout URL with zero parameters (login has `redirect_url`; logout does not). Whether it ends the Google session is **not documented**. |
| 4 | `/cdn-cgi/access/get-identity` | Documented on the **Application token** page, with a field table. Empirically-observed `id` and `name` are **not** in that table. Documented-but-absent here: `devicePosture`, `service_token_id`, `gateway_account_id`, `device_id`, `device_sessions`. Cloudflare documents the endpoint **only on the team domain**, not the app domain. For `http://localhost:3000` / `http://192.168.1.112`: **Cloudflare documents nothing** — the conclusion (request never reaches Cloudflare, so your own Next.js router answers and 404s) is **[INFERENCE]** from `/cdn-cgi/` being a Cloudflare-managed edge path. |
| 5 | Trust vs verify | Cloudflare is unambiguous: *"To secure your origin, you must validate the application token issued by Cloudflare Access. Token validation ensures that any requests which bypass Cloudflare Access (for example, due to a network misconfiguration) are rejected."* It offers two ways: let `cloudflared` do it (**Protect with Access**, `access: {required, teamName, audTag}`), or validate in the origin. It also states validation of the header alone is insufficient — "the JWT and signature must be confirmed to avoid identity spoofing." |
| — | **Recommendation** | **Turn on `cloudflared` "Protect with Access"** (config-only, no Rust). Have the sidebar call `get-identity` for display. For v1 of support tickets, **do not** hand-roll JWT verification in Axum — but **do** stop trusting a client-supplied author field: read `Cf-Access-Jwt-Assertion` in Axum, decode the `email`/`sub` claims *without* signature verification for attribution, and reject requests with no header. See [Recommendation](#recommendation-for-this-deployment) for why that is defensible and exactly what would upgrade it. |

---

## 1. `Cf-Access-Jwt-Assertion`, the JWKS endpoint, and the claim set

### What it is and when Access injects it

> "When Cloudflare sends a request to your origin, the request will include an application token as a `Cf-Access-Jwt-Assertion` request header. Requests made through a browser will also pass the token as a `CF_Authorization` cookie."
> — [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

So the header is present on **every** request Access forwards to the origin — API calls and page loads alike — while the cookie only exists for browser navigations.

> "We recommend validating the `Cf-Access-Jwt-Assertion` header instead of the `CF_Authorization` cookie, since the cookie is not guaranteed to be passed."
> — [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

The [Authorization cookie](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/) page explains there are in fact **two** `CF_Authorization` cookies: a **global session token** set on the team domain (`<team>.cloudflareaccess.com`) that enables SSO across apps, and an **application token** set on each protected domain that "may be used to validate requests on your origin". The application-domain cookie's `HttpOnly` flag is **admin choice, default None** — i.e. by default it is *not* HttpOnly (same page, "Access cookies" table). That does not matter for the sidebar use case, because a same-origin `fetch('/cdn-cgi/access/get-identity')` sends the cookie automatically regardless.

Access also sets `CF_Binding`, `CF_Session`, `CF_AppSession` and `CF_Device` cookies — all documented in the same table.

### JWKS / public key endpoint

> "The public key for the signing key pair is located at `https://<your-team-name>.cloudflareaccess.com/cdn-cgi/access/certs`, where `<your-team-name>` is your Cloudflare One team name."
> — [Validate JWTs § Access signing keys](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

**Confirmed:** the URL format in the task prompt is exactly right. The response body contains three parallel representations:

- `keys` — both keys in JWK format (each with `kid`, `kty: "RSA"`, `alg: "RS256"`, `use: "sig"`, `e`, `n`)
- `public_cert` — current key in PEM
- `public_certs` — both keys in PEM

Rotation policy, from the same page:

> "By default, Access rotates the signing key every 6 weeks. This means you will need to programmatically or manually update your keys as they rotate. Previous keys remain valid for 7 days after rotation to allow time for you to make the update."

And a caution worth obeying:

> "Validate tokens using the external endpoint rather than saving the public key as a hard-coded value."
> "Do not fetch the current key from `public_cert`, since your origin may inadvertently read an expired value from an outdated cache. Instead, match the `kid` value in the JWT to the corresponding certificate in `public_certs`."

Keys can also be rotated on demand via the [Access keys rotate API](https://developers.cloudflare.com/api/resources/zero_trust/subresources/access/subresources/keys/methods/rotate/).

**`/cdn-cgi/access/certs` vs `/cdn-cgi/access/get-identity`** — these are unrelated endpoints on the same reserved path prefix. `certs` is the public JWKS (no credentials required, served from the team domain). `get-identity` is a per-user identity lookup that requires the `CF_Authorization` cookie ([Application token § User identity](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/)). Do not conflate them. Cloudflare's own Pages plugin hits `/cdn-cgi/access/certs` for verification and `/cdn-cgi/access/get-identity` for profile data, as two separate calls ([published plugin source](https://unpkg.com/@cloudflare/pages-plugin-cloudflare-access@1.0.5/dist/src/api/index.js)).

### Signing algorithm

**RS256, and only RS256.**

> "Cloudflare generates the signature by signing the encoded header and payload using the SHA-256 algorithm (RS256). In RS256, a private key signs the JWTs and a separate public key verifies the signature."
> — [Application token § Signature](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/)

The documented header is:

```json
{ "alg": "RS256", "kid": "9338abe1...69b8", "typ": "JWT" }
```

Cloudflare's own Pages middleware hard-rejects anything else — its published bundle contains `if (alg !== "RS256")` and `if (jwk.kty !== "RSA" || jwk.alg !== "RS256")` guards ([published plugin source](https://unpkg.com/@cloudflare/pages-plugin-cloudflare-access@1.0.5/dist/functions/index.js)). Pin `algorithms = [RS256]` in any verifier; do not accept `alg` from the token.

### Full documented claim set (identity-based auth)

From [Application token § Identity-based authentication](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/):

```json
{
  "aud": ["32eafc7626e974616deaf0dc3ce63d7bcbed58a2731e84d06bc3cdf1b53c4228"],
  "email": "user@example.com",
  "exp": 1659474457,
  "iat": 1659474397,
  "nbf": 1659474397,
  "iss": "https://yourteam.cloudflareaccess.com",
  "type": "app",
  "identity_nonce": "6ei69kawdKzMIAPF",
  "sub": "7335d417-61da-459d-899c-0a01c76a2f94",
  "country": "US"
}
```

| Claim | Documented meaning |
|---|---|
| `aud` | Application Audience (AUD) tag of the Access application. **Note: it is a JSON array, not a string.** |
| `email` | "The email address of the authenticated user, verified by the identity provider." |
| `exp` | Expiration timestamp (Unix time). |
| `iat` | Issuance timestamp (Unix time). |
| `nbf` | Not-before timestamp (Unix time), "used to check if the token was received before it should be used". |
| `iss` | "The Cloudflare Access domain URL for the application" — i.e. `https://<team>.cloudflareaccess.com`. |
| `type` | `app` for an application token, `org` for a global session token. |
| `identity_nonce` | "A cache key used to get the user's identity." |
| `sub` | User ID, "unique to an email address per account". Changes if the user is removed and re-added, or logs into a different org. |
| `country` | "The country where the user authenticated from." |

**On `nonce` vs `identity_nonce`:** only **`identity_nonce`** is documented. There is no documented top-level `nonce` claim. Treat any code expecting `nonce` as wrong against current docs.

**Custom claims** — [same page](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/#custom-saml-attributes-and-oidc-claims): custom SAML attributes and OIDC claims land in a `custom` claim, and Cloudflare explicitly warns they are **best-effort and get trimmed**:

> "Access trims custom attributes and claims when the serialized `custom` claim exceeds roughly 1 KB (1,000 bytes), dropping configured values from the end of the list first. […] Do not rely on custom claims in the JWT for authorization decisions when they may grow large."

Also relevant to a Google-IdP deployment:

> "Identity provider groups are only included in the token when you explicitly configure `groups` as a custom SAML attribute or OIDC claim. Access does not add them automatically."

**Service-token tokens** have a different shape (`common_name` = the Client ID, `sub` = empty string, no `email`). If a service token ever reaches this origin, an `email`-keyed author field would be empty — worth a guard.

### Documented verification procedure

**Manual** ([Validate JWTs § Verify the JWT manually](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)):

1. Copy the JWT from the `Cf-Access-Jwt-Assertion` request header.
2. Go to [jwt.io](https://jwt.io/).
3. Select the RS256 algorithm.
4. Paste the JWT into the **Encoded** box.
5. In the **Payload** box, ensure `iss` points to your team domain — "`jwt.io` uses the `iss` value to fetch the public key for token validation."
6. Ensure the page says **Signature Verified**.

**Programmatic** — the documented shape across Cloudflare's Workers / Go / Python / Node examples on the same page is:

1. Read the token from the `Cf-Access-Jwt-Assertion` header (fall back to the `CF_Authorization` cookie only if you must).
2. Fetch the JWKS from `${TEAM_DOMAIN}/cdn-cgi/access/certs` — from the live endpoint, not a hard-coded key.
3. Select the key whose `kid` matches the JWT header's `kid` (Cloudflare's Workers/Node examples delegate this to `jose.createRemoteJWKSet`; the Go example delegates to `oidc.NewRemoteKeySet`).
4. Verify the RS256 signature.
5. Validate `issuer` == `https://<team>.cloudflareaccess.com`.
6. Validate `audience` contains your AUD tag.
7. On any failure return **403** (Cloudflare's Workers, Python and Node examples all use 403; the Go example uses 401).

Cloudflare's canonical Workers example, verbatim from the docs:

```ts
import { jwtVerify, createRemoteJWKSet } from "jose";

const token = request.headers.get("cf-access-jwt-assertion");
const JWKS = createRemoteJWKSet(new URL(`${env.TEAM_DOMAIN}/cdn-cgi/access/certs`));
const { payload } = await jwtVerify(token, JWKS, {
  issuer: env.TEAM_DOMAIN,
  audience: env.POLICY_AUD,
});
```

### Where the AUD tag lives

> "Cloudflare Access assigns a unique AUD tag to each application. The `aud` claim in the token payload specifies which application the JWT is valid for.
> To get the AUD tag:
> 1. In the Cloudflare dashboard, go to **Zero Trust** > **Access controls** > **Applications**.
> 2. Select **Configure** for your application.
> 3. From **Additional settings**, copy the **Application Audience (AUD) Tag**.
> […] The AUD tag will never change unless you delete or recreate the Access application."
> — [Validate JWTs § Get your AUD tag](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/#get-your-aud-tag)

Checking `aud` is what stops a *valid* token minted for a **different** Access application in the same Zero Trust org from being replayed against this one. `iss` alone does not — every app in the org shares an issuer.

### Convenience headers (`Cf-Access-Authenticated-User-Email`)

Cloudflare's own tutorial [Create custom headers for Cloudflare Access-protected origins with Workers](https://developers.cloudflare.com/cloudflare-one/tutorials/access-workers/) reads the user email out of the request in a Worker and forwards it downstream. However:

- I could **not** find `Cf-Access-Authenticated-User-Email` in any Cloudflare **reference** page — it does not appear in the [Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/) or [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/) pages, which are the pages that own the "what Access sends your origin" contract.
- The tutorial that does use it carries **no** warning about trusting it versus verifying the JWT.
- Every reference page routes you to the JWT instead.

**Treat `Cf-Access-Authenticated-User-Email` as effectively undocumented and do not build on it.** A plain header is trivially spoofable by anything that can reach the origin directly — which is exactly the bypass case Cloudflare tells you to defend against (see §5). The JWT is the contract. This is flagged again in [Open / undocumented](#open--undocumented).

---

## 2. Rust crates for verification

All figures pulled from the crates.io API on 2026-08-02.

### Cloudflare-Access-specific

| Crate | Version | Total downloads | 90-day | Last release | Repo | Verdict |
|---|---|---|---|---|---|---|
| [`cf-access`](https://crates.io/crates/cf-access) | 0.2.0 | 1,329 | 14 | 2025-03-30 | [j-tai/cf-access](https://github.com/j-tai/cf-access) | Correct-looking but **tiny and dormant** |

`cf-access` is the only crate on crates.io that names Cloudflare Access JWT validation as its purpose ("Super simple library for validating Cloudflare Access JWTs"). Reading [its source](https://github.com/j-tai/cf-access/blob/master/rust/src/lib.rs), it is ~105 lines wrapping [`jwtk`](https://crates.io/crates/jwtk)'s `RemoteJwksVerifier`:

- `Validator::new(team_name, audience)` builds the certs URL and a `RemoteJwksVerifier` with `CACHE_DURATION = Duration::from_secs(60 * 60 * 24 * 3)` — a **3-day** JWKS cache.
- `Validator::from_env()` reads config from the environment.
- `Validator::validate(&self, jwt) -> Result<Claims, Error>`, where `Claims` is an enum over `IdentityClaims` (IdP login) and `ServiceClaims` (service token) — matching the two documented token shapes.
- MIT licensed; re-exports `jwtk`, `reqwest`, `uuid`.

The 3-day cache sits safely inside Cloudflare's documented 7-day grace for rotated keys, so it is a defensible default. But: two releases, both on the same day 16 months ago, 14 downloads in 90 days, one author. Fine to read for reference; **do not put it on the critical path of a production auth check** you cannot patch yourself.

### Generic JWT / JWKS crates

| Crate | Version | Total downloads | 90-day | Last release | Maintained? | Notes |
|---|---|---|---|---|---|---|
| [`jsonwebtoken`](https://crates.io/crates/jsonwebtoken) | **11.0.0** | 161,959,156 | 39,462,135 | **2026-07-24** | **Yes — actively** | The de-facto standard. Ships a `jwk` module + `DecodingKey::from_jwk`. **Does not do HTTP.** |
| [`jwt-simple`](https://crates.io/crates/jwt-simple) | 0.13.0 | 5,616,794 | 805,901 | 2026-07-30 | Yes | Opinionated, safe-by-default API. No built-in JWKS fetch. |
| [`jwtk`](https://crates.io/crates/jwtk) | 0.5.0 | 731,720 | — | 2026-03-11 | Yes | "first class JWK and JWK Set (JWKS) support"; has `RemoteJwksVerifier` with a TTL cache. What `cf-access` uses. |
| [`jwks_client_rs`](https://crates.io/crates/jwks_client_rs) | 0.6.1 | 308,513 | 54,782 | 2026-06-23 | Yes | From Prima; "JWKS-sync client implementation for Auth0" — generic enough for any JWKS. |
| [`jwks`](https://crates.io/crates/jwks) | 0.5.3 | 138,560 | 22,785 | 2026-02-23 | Yes | Thin JWKS fetch/parse on top of `jsonwebtoken` + `reqwest`. `Jwks::from_jwks_url(url)`, `Jwks::from_oidc_url(url)`. |
| [`jwt-authorizer`](https://crates.io/crates/jwt-authorizer) | 0.15.0 | 481,204 | 178,267 | 2024-08-27 | **Stale (~2 yr)** | "jwt authorizer middleware for axum and tonic" — the most Axum-native option, but no release in two years. |
| [`axum-jwks`](https://crates.io/crates/axum-jwks) | 0.12.0 | 23,367 | 1,966 | 2025-06-06 | Marginal | "Use a JSON Web Key Set (JWKS) to verify JWTs in Axum." Small user base. |
| [`alcoholic_jwt`](https://crates.io/crates/alcoholic_jwt) | 4091.0.0 | 691,644 | — | 2022-05-16 | **Abandoned** | RS256 validation only; no release in 4 years. Avoid. |
| [`jwks-client`](https://crates.io/crates/jwks-client) | 0.2.0 | 46,683 | 2,476 | 2020-01-13 | **Abandoned** | Last release 2020. Avoid. |

**Verified `jsonwebtoken` 11 API surface** (from docs.rs):

- [`jsonwebtoken::jwk::JwkSet`](https://docs.rs/jsonwebtoken/latest/jsonwebtoken/jwk/struct.JwkSet.html) — `pub struct JwkSet { pub keys: Vec<Jwk> }`, with `pub fn find(&self, kid: &str) -> Option<&Jwk>`. It derives `Deserialize`, so `serde_json::from_str::<JwkSet>(body)` parses Cloudflare's `keys` array directly.
- [`DecodingKey::from_jwk(jwk: &Jwk) -> Result<Self>`](https://docs.rs/jsonwebtoken/latest/jsonwebtoken/struct.DecodingKey.html) — no `use_pem` feature needed for the RSA-components path.
- [`Validation`](https://docs.rs/jsonwebtoken/latest/jsonwebtoken/struct.Validation.html) — public fields `algorithms` (default `vec![HS256]` — **you must override**), `aud`, `iss`, `sub`, `required_spec_claims` (default `{"exp"}`), `validate_exp` (default `true`), `validate_nbf` (default `false`), `validate_aud` (default `true`), `leeway` (default 60 s). Setters: `set_audience(&[T])`, `set_issuer(&[T])`.
- The crate's `jwk` module is documented as "types only for working JWK and JWK Sets […] only meant to be used to deal with public JWK". **It performs no network I/O** — you supply the fetch.

### Minimal verification shape for Axum

Dependencies: `jsonwebtoken = "11"`, `reqwest = { version = "0.12", features = ["json"] }`, `tokio`, `serde`, `axum`.

```rust
use std::{sync::Arc, time::{Duration, Instant}};
use axum::{extract::FromRequestParts, http::{request::Parts, StatusCode}};
use jsonwebtoken::{decode, decode_header, jwk::JwkSet, Algorithm, DecodingKey, Validation};
use serde::Deserialize;
use tokio::sync::RwLock;

/// Documented identity-token claims. `aud` is an ARRAY.
#[derive(Debug, Deserialize)]
pub struct AccessClaims {
    pub aud: Vec<String>,
    pub email: Option<String>,   // absent for service-token JWTs
    pub sub: String,             // "" for service-token JWTs
    pub iss: String,
    pub exp: usize,
    pub iat: usize,
    #[serde(rename = "type")]
    pub token_type: String,      // "app" | "org"
    pub country: Option<String>,
    pub identity_nonce: Option<String>,
}

pub struct AccessVerifier {
    team_domain: String,   // "https://<team>.cloudflareaccess.com"
    aud_tag: String,       // Application Audience (AUD) Tag from the dashboard
    http: reqwest::Client,
    cache: RwLock<Option<(JwkSet, Instant)>>,
}

// Well under Cloudflare's 7-day grace for rotated keys.
const JWKS_TTL: Duration = Duration::from_secs(60 * 60 * 6);

impl AccessVerifier {
    async fn jwks(&self, force: bool) -> anyhow::Result<JwkSet> {
        if !force {
            if let Some((set, at)) = self.cache.read().await.as_ref() {
                if at.elapsed() < JWKS_TTL {
                    return Ok(set.clone());
                }
            }
        }
        // Documented endpoint: https://<team>.cloudflareaccess.com/cdn-cgi/access/certs
        let url = format!("{}/cdn-cgi/access/certs", self.team_domain);
        let set: JwkSet = self.http.get(&url).send().await?.error_for_status()?.json().await?;
        *self.cache.write().await = Some((set.clone(), Instant::now()));
        Ok(set)
    }

    pub async fn verify(&self, token: &str) -> anyhow::Result<AccessClaims> {
        let header = decode_header(token)?;
        let kid = header.kid.ok_or_else(|| anyhow::anyhow!("no kid"))?;

        // Unknown kid => key just rotated => refetch once, then give up.
        let mut set = self.jwks(false).await?;
        let jwk = match set.find(&kid) {
            Some(k) => k.clone(),
            None => {
                set = self.jwks(true).await?;
                set.find(&kid).cloned().ok_or_else(|| anyhow::anyhow!("unknown kid"))?
            }
        };

        let key = DecodingKey::from_jwk(&jwk)?;
        let mut v = Validation::new(Algorithm::RS256); // never trust header `alg`
        v.set_issuer(&[self.team_domain.as_str()]);
        v.set_audience(&[self.aud_tag.as_str()]);      // handles aud-as-array
        v.validate_nbf = true;

        Ok(decode::<AccessClaims>(token, &key, &v)?.claims)
    }
}

/// Axum extractor. Put `Arc<AccessVerifier>` in your app state.
pub struct AccessUser(pub AccessClaims);

impl<S> FromRequestParts<S> for AccessUser
where
    Arc<AccessVerifier>: axum::extract::FromRef<S>,
    S: Send + Sync,
{
    type Rejection = (StatusCode, &'static str);

    async fn from_request_parts(parts: &mut Parts, state: &S) -> Result<Self, Self::Rejection> {
        let verifier = <Arc<AccessVerifier> as axum::extract::FromRef<S>>::from_ref(state);
        let token = parts
            .headers
            .get("cf-access-jwt-assertion")           // documented: prefer header over cookie
            .and_then(|v| v.to_str().ok())
            .ok_or((StatusCode::FORBIDDEN, "missing Cf-Access-Jwt-Assertion"))?;
        verifier
            .verify(token)
            .await
            .map(AccessUser)
            .map_err(|_| (StatusCode::FORBIDDEN, "invalid Access token"))
    }
}
```

**Caching considerations**

- **Never fetch JWKS per request.** Cloudflare's own examples all use a caching remote key set (`jose.createRemoteJWKSet`, `oidc.NewRemoteKeySet`); the Python example is the exception and refetches every request, which is not a pattern to copy.
- **TTL upper bound is 7 days** — Cloudflare's documented grace window for a rotated-out key. `cf-access` picks 3 days; the snippet above picks 6 hours. Anything from minutes to a couple of days is safe.
- **Refetch on unknown `kid`, once, with a floor.** This is what makes a 6-week rotation invisible. Rate-limit it (or debounce) so a flood of garbage tokens with random `kid`s cannot turn into a fetch storm against the certs endpoint.
- **Match by `kid` against `keys`/`public_certs`, not `public_cert`** — Cloudflare explicitly calls out reading `public_cert` from a stale cache as a failure mode.
- **Fail closed on fetch failure.** If the certs endpoint is unreachable and the cache is cold, reject rather than admit.

---

## 3. `/cdn-cgi/access/logout`

Everything documented lives in [Session management § Log out as a user](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/#log-out-as-a-user).

**The two URLs:**

- `<your-application-domain>/cdn-cgi/access/logout`
- `<your-team-name>.cloudflareaccess.com/cdn-cgi/access/logout`

**What it clears** — verbatim:

> "This action revokes the user's session across all applications. Access will immediately clear the authorization cookie from the user's browser, and all previously issued tokens will stop being accepted in 20-30 seconds. The only difference between these two URLs is which domain the authorization cookie is deleted from. For example, going to `<your-application-domain>/cdn-cgi/access/logout` will remove the application cookie and make the logout action feel more instantaneous."

So, precisely:

- **Session revocation is global across all Access applications**, from *either* URL. There is no scoping choice.
- The **only** difference is *which browser cookie gets deleted synchronously* — app domain deletes the app cookie (feels instant on that app), team domain deletes the global cookie.
- **Yes, there is a team-domain-wide logout URL:** `<team>.cloudflareaccess.com/cdn-cgi/access/logout`.
- There is a **20–30 second window** where already-issued tokens still validate. A logout button is not an instant kill switch at the origin.

**Per-application logout does not exist:**

> "At this time, end users cannot log themselves out on a per-application basis."

**Cloudflare explicitly blesses in-app logout links:**

> "You can use these URLs to create custom logout buttons or links directly within your application."
> — [Session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/#log-out-as-a-user)

That is the direct answer to "does Cloudflare document this as the way to offer a Log out link inside an application's own UI" — **yes**.

**Redirect target via query parameter: not documented, and Cloudflare's own SDK does not support one.**

The [Cloudflare Access Pages Plugin](https://developers.cloudflare.com/pages/functions/plugins/cloudflare-access/) — a Cloudflare-authored, Cloudflare-published package — exposes both helpers. Their published implementations ([`dist/src/api/index.js`](https://unpkg.com/@cloudflare/pages-plugin-cloudflare-access@1.0.5/dist/src/api/index.js)):

```js
var generateLoginURL = ({ redirectURL: redirectURLInit, domain, aud }) => {
  const redirectURL = typeof redirectURLInit === "string" ? new URL(redirectURLInit) : redirectURLInit;
  const { hostname } = redirectURL;
  const loginPathname = `/cdn-cgi/access/login/${hostname}?`;
  const searchParams = new URLSearchParams({
    kid: aud,
    redirect_url: redirectURL.pathname + redirectURL.search
  });
  return new URL(loginPathname + searchParams.toString(), domain).toString();
};

var generateLogoutURL = ({ domain }) => new URL(`/cdn-cgi/access/logout`, domain).toString();
```

The **login** path takes `kid` and `redirect_url` query parameters. The **logout** path takes **none** — Cloudflare's own helper accepts only `domain` and emits a bare `/cdn-cgi/access/logout`. Combined with the absence of any documented parameter on the Session management page, the conclusion is: **there is no supported way to specify where the user lands after logout.** [INFERENCE — from the absence of documentation plus Cloudflare's own SDK signature. An undocumented parameter could exist; do not depend on one.]

**Does logging out of Access log the user out of Google? Not documented.**

Neither [Session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/) nor the [Identity FAQ](https://developers.cloudflare.com/cloudflare-one/faq/authentication-faq/) addresses IdP session termination. Every documented effect is scoped to Access's own session and cookies.

**[INFERENCE]** Access logout ends the *Access* session only; the user's Google session is untouched, so clicking "Log out" and then reloading will silently re-authenticate through Google without a password prompt. This matters for UX: on a **shared warehouse workstation**, an Access-only logout will look like it did nothing. If shared-terminal logout is a real requirement, that needs its own design (and probably a browser-profile or kiosk-level answer), not this URL. Flagged in [Open / undocumented](#open--undocumented).

---

## 4. `/cdn-cgi/access/get-identity`

### Official documentation page

There is **no dedicated page**. The endpoint is documented as a subsection of the Application token reference: [Application token § User identity](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/#user-identity).

> "Due to cookie size limits and bandwidth considerations, the application token only contains a subset of the user's identity. To get the user's full identity, send the `CF_Authorization` cookie to `https://<your-team-name>.cloudflareaccess.com/cdn-cgi/access/get-identity`."

```sh
curl -H 'cookie: CF_Authorization=<user-token>' https://<your-team-name>.cloudflareaccess.com/cdn-cgi/access/get-identity
```

It is also documented as a helper in the [Cloudflare Access Pages Plugin](https://developers.cloudflare.com/pages/functions/plugins/cloudflare-access/), whose `getIdentity` "returns a `Promise` of the object returned by the `/cdn-cgi/access/get-identity` endpoint."

### Documented payload fields

| Field | Documented description | Seen on this deployment? |
|---|---|---|
| `email` | The email address of the user. | Yes |
| `idp` | Data from your identity provider. | Yes (`{id, type}`; **sub-fields are not documented**) |
| `geo` | The country where the user authenticated from. | Yes (`{country}`; **sub-field not documented**) |
| `user_uuid` | The ID of the user. | Yes |
| `devicePosture` | The device posture attributes. | **No** |
| `account_id` | The account ID for your organization. | Yes |
| `iat` | The timestamp indicating when the user logged in. | Yes, but `0` |
| `ip` | The IP address of the user. | Yes, but `""` |
| `auth_status` | The status if authenticating with mTLS. | Yes, `"NONE"` |
| `common_name` | The common name on the mTLS client certificate. | Yes |
| `service_token_id` | The Client ID of the service token used for authentication. | **No** |
| `service_token_status` | True if authentication was through a service token instead of an IdP. | Yes |
| `is_warp` | True if the user enabled WARP. | Yes |
| `is_gateway` | True if the user enabled the Cloudflare One Client and authenticated to a Zero Trust team. | Yes |
| `gateway_account_id` | An ID generated by the Cloudflare One Client when authenticated to a Zero Trust team. | **No** |
| `device_id` | The ID of the device used for authentication. | **No** |
| `version` | The version of the `get-identity` object. | Yes, but `0` |
| `device_sessions` | A list of all sessions initiated by the user. | **No** |

— all from the [Application token field table](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/#user-identity).

### Observed fields that are NOT documented

- **`id`** — not in the documented table. `user_uuid` is the documented user identifier.
- **`name`** — **not in the documented table.** This is the field the sidebar most wants. It is real on this deployment and Google-sourced, but Cloudflare does not commit to it. **Do not make it load-bearing:** render `name` if present, fall back to `email`, and never key any record on it.
- Sub-fields `idp.id`, `idp.type`, `geo.country` — the parent objects are documented; their shapes are not.
- The **absence of an avatar/picture field is consistent with the docs** — no such field is documented, so nothing is missing. A sidebar avatar must be a generated initial or a static glyph.
- `auth_status: "NONE"`, `common_name: ""`, `iat: 0`, `ip: ""`, `version: 0` — these are documented *fields* whose *empty/zero values* are undocumented. `auth_status` and `common_name` are documented as mTLS-related, so `"NONE"`/empty is coherent for a Google-IdP login. `iat: 0` and `ip: ""` contradict their documented descriptions ("when the user logged in", "the IP address of the user") and have no documented explanation. **Do not display `iat` or `ip` in the UI.**

### Team domain vs application domain

Cloudflare documents this endpoint **only on the team domain** (`https://<team>.cloudflareaccess.com/cdn-cgi/access/get-identity`), and Cloudflare's own plugin builds it that way: `new URL("/cdn-cgi/access/get-identity", domain)` where `domain` is the team domain ([published source](https://unpkg.com/@cloudflare/pages-plugin-cloudflare-access@1.0.5/dist/src/api/index.js)).

This deployment observes it working on the **application domain** through the tunnel. That is convenient (same-origin `fetch`, no CORS, cookie sent automatically) but **undocumented**. [INFERENCE] It works because `/cdn-cgi/*` is a Cloudflare-reserved path served on every proxied hostname, so Access can answer it at the app's edge using the app-domain `CF_Authorization` cookie. Flagged in [Open / undocumented](#open--undocumented) — it is behaviour that could change without a docs entry.

### What happens off the Access path (`localhost:3000`, `192.168.1.112`)?

**Cloudflare documents nothing about this. Say so plainly.** There is no page describing the behaviour of `/cdn-cgi/access/get-identity` for a request that never traverses a Cloudflare edge, because from Cloudflare's perspective such a request does not exist.

The conclusion is **[INFERENCE]**, and it follows from two documented facts:

1. `/cdn-cgi/` is Cloudflare's own reserved path. Per [The `/cdn-cgi/` endpoint](https://developers.cloudflare.com/fundamentals/reference/cdn-cgi-endpoint/): *"This endpoint is managed and served by Cloudflare. It cannot be modified or customized."* It is added automatically when a domain is onboarded to Cloudflare. (Notably, that page's list of `/cdn-cgi/` paths — `trace`, `challenge-platform`, `image`, `l/email-protection`, `rum` — does **not** include any `/cdn-cgi/access/*` path, so even the reserved-path inventory is incomplete.)
2. With Cloudflare Tunnel, *"cloudflared initiates an outbound connection through your firewall from the origin to the Cloudflare global network"* ([Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/)). Traffic reaches the origin only after passing through Cloudflare.

Therefore: a browser at `http://localhost:3000/cdn-cgi/access/get-identity` or `http://192.168.1.112/cdn-cgi/access/get-identity` sends that request straight to your Next.js server. Cloudflare is not in the path and cannot intercept it. Next.js has no route matching `/cdn-cgi/access/get-identity`, so it returns its own **404** — an HTML error page, not JSON.

**Practical consequence for the sidebar:** the identity fetch must be written to fail gracefully. `fetch('/cdn-cgi/access/get-identity')` in local dev will resolve with `res.ok === false` (404) and an HTML body — so `res.json()` will throw on a *successful* HTTP round-trip. Guard on `res.ok` **and** wrap the parse. Cloudflare's own plugin does exactly this: `if (response.ok) return await response.json();` and otherwise returns `undefined` ([published source](https://unpkg.com/@cloudflare/pages-plugin-cloudflare-access@1.0.5/dist/src/api/index.js)). Copy that shape, and render an anonymous/"Local dev" sidebar state when identity is `undefined`.

---

## 5. Client-supplied identity vs server-side JWT verification

### Cloudflare's position: the origin must verify

This is the most important quotation in this document, from Cloudflare's own partial that is rendered into the self-hosted-application setup guide:

> "To secure your origin, you must validate the application token issued by Cloudflare Access. Token validation ensures that any requests which bypass Cloudflare Access (for example, due to a network misconfiguration) are rejected."
> — [Publish a self-hosted application to the Internet § Validate the Access token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/) ([source partial](https://github.com/cloudflare/cloudflare-docs/blob/production/src/content/partials/cloudflare-one/access/secure-tunnel-with-access.mdx))

And, on the header-vs-signature distinction:

> "Unless your application is connected to Access through Cloudflare Tunnel, your application must validate the token to ensure the security of your origin. **Validation of the header alone is not sufficient — the JWT and signature must be confirmed to avoid identity spoofing.**"
> — [Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/) (emphasis added)

That sentence does two things at once. It says signature verification is mandatory — *and* it carves out an explicit exception for **Cloudflare Tunnel**. That carve-out is the crux of the recommendation below.

> "You should validate the token with your public key to ensure that the request came from Access and not a malicious third party."
> — [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

### Cloudflare's stated threat model: can the origin be reached bypassing Access?

Cloudflare's threat model is **network-level bypass**, not browser-level tampering. The named scenarios are:

- **The origin is publicly routable and someone finds its IP.** The self-hosted setup guide states: *"If your application is already publicly routable, a tunnel is not strictly required. However, you will then need to protect your origin IP using [other methods](https://developers.cloudflare.com/fundamentals/security/protect-your-origin-server/)."*
- **Network misconfiguration** — the explicit example in the "you must validate" partial above.
- **Accidental exposure of a new hostname.** [Require Access protection](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/require-access-protection/): *"When this setting is turned on, traffic to any hostname without a matching Access application is automatically blocked. This deny-by-default approach prevents accidental exposure of internal resources to the public Internet. Without this setting, a developer could deploy a new application or create a DNS record and inadvertently expose the resource before configuring an Access application."*

### What Tunnel changes

> "Cloudflare Tunnel provides you with a secure way to connect your resources to Cloudflare without a publicly routable IP address."
> "cloudflared initiates an outbound connection through your firewall from the origin to the Cloudflare global network."
> "You can then configure your firewall to allow only these outbound connections and block all inbound traffic, effectively blocking access to your origin from anything other than Cloudflare."
> — [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/)

A named tunnel with no inbound ports open removes the *public* bypass vector entirely — that is why the Application token page grants tunnel-connected apps an exception. It does **not** remove the *LAN* vector: anything on the warehouse network that can reach `192.168.1.112:8080` reaches the Rust backend directly, with no Access header at all. That is a real, in-scope path for this deployment.

### The `cloudflared` option: "Protect with Access"

Cloudflare's first-listed remedy is config, not code:

> "One option is to configure the Cloudflare Tunnel daemon, `cloudflared`, to validate the token on your behalf. This is done by enabling **Protect with Access** in your Cloudflare Tunnel settings."
> — [Publish a self-hosted application § Validate the Access token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/)

Per [Origin parameters § Access](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/#access):

> "Requires `cloudflared` to validate the Cloudflare Access JWT prior to proxying traffic to your origin."
> "You can enforce this check on public hostname services that are protected by an Access application. For all L7 requests to these hostnames, Access will send the JWT to `cloudflared` as a `Cf-Access-Jwt-Assertion` request header."

Config keys: `required` (bool), `teamName`, `audTag` (list — accepts multiple AUD tags). **Default is off (`""`).**

This gives you Cloudflare's mandated signature verification with **zero Rust code**, performed by Cloudflare's own daemon on your own machine, before any request touches Axum.

### On trusting browser-supplied identity

Cloudflare publishes **no** page saying "do not trust identity from the browser" in those words. But the guidance is unambiguous in effect:

- Every reference page routes identity to **the JWT**, and says the signature must be confirmed "to avoid identity spoofing" ([Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/)).
- `get-identity` is documented purely as a way to **enrich** identity for an already-authenticated session — *"User identity is useful for checking application permissions"*, *"the application token only contains a subset of the user's identity"* ([Application token § User identity](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/#user-identity)). It is never presented as an authentication mechanism.
- Cloudflare's own Pages plugin **validates the JWT in middleware** and separately offers `getIdentity` for profile data — two distinct concerns, in Cloudflare's own reference implementation ([plugin docs](https://developers.cloudflare.com/pages/functions/plugins/cloudflare-access/), [source](https://unpkg.com/@cloudflare/pages-plugin-cloudflare-access@1.0.5/dist/functions/index.js)).

**[INFERENCE]** A `get-identity` result read by JavaScript and then POSTed to your API as `{ author: "..." }` carries **zero** authentication weight. The browser is the attacker-controlled side of that boundary; anyone who can call your API can put any string in that field. The only trustworthy identity at the origin is the one Cloudflare put in the request itself.

---

## Recommendation for this deployment

**Scope:** v1 of a support-ticket feature — create tickets, create comments — used by roughly a handful of warehouse staff, all behind Access with Google as IdP, backend reachable on the LAN at `192.168.1.112`.

**Position: verify — but buy the verification with configuration, not with Rust.**

Three concrete steps, in priority order.

### 1. Turn on **Protect with Access** in the tunnel (do this first)

Set `access: { required: true, teamName: <team>, audTag: [<AUD tag>] }` on the public hostname service for this app ([Origin parameters § Access](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/#access)).

This is Cloudflare's own first-listed way to satisfy "you must validate the application token", it needs no application code, and it puts a real RS256 signature + `aud` check in front of every L7 request. It is the highest security-per-effort action available and it should not wait for v1 of the ticket feature — it is a deployment hardening task with its own justification.

Caveat, stated plainly: this hardens the **tunnel** path. It does nothing for a request sent straight to `192.168.1.112:8080`. Which brings us to step 2.

### 2. Stop trusting a client-supplied author field — read the header in Axum

Whatever else you do, **do not let the browser tell the backend who is writing the ticket.** Add a small Axum extractor that:

- reads `Cf-Access-Jwt-Assertion`;
- **rejects the request with 403 if the header is absent** (this is the part that closes the LAN path, and it costs nothing);
- decodes the payload and takes `email` and `sub` for attribution.

For v1, decoding **without** signature verification is a defensible position *given step 1 is done*, because `cloudflared` has already verified the signature and `aud` before the request reached Axum — re-verifying in Axum is verifying the same token twice on the same machine. What the header check adds on top is the thing `cloudflared` cannot give you: an unauthenticated LAN request has no header at all, so it is rejected outright.

Store `sub` as the stable author key and `email` for display. Per the docs, `sub` is *"unique to an email address per account"* but **changes if a user is removed and re-added** ([Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/)) — so keep `email` alongside it, and treat neither as a foreign key into a users table you do not yet have.

Also handle the service-token shape: if `type` is `app` but `email` is absent and `sub` is `""`, that is a service token — reject it for ticket authoring rather than writing an empty author.

### 3. Sidebar identity: call `get-identity`, display only

Fetch `/cdn-cgi/access/get-identity` same-origin from the dashboard. Rules:

- Guard on `res.ok` **and** wrap the JSON parse — in local dev this endpoint 404s with HTML (see §4).
- Render `name`, fall back to `email`. `name` is **undocumented**; never let a missing `name` break the sidebar.
- No avatar — no picture field exists. Use initials from `name`/`email`.
- Do not display `iat` or `ip`; they are `0` and `""` on this deployment with no documented explanation.
- **Never send the result to the backend as identity.** It is presentation data only. The backend's identity comes from the header, full stop.

Add a "Log out" link to `<app-domain>/cdn-cgi/access/logout` — Cloudflare documents this exact use ([Session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/#log-out-as-a-user)). Set expectations in the UI copy: it logs out of *this app suite*, takes ~20–30 s to fully propagate, cannot redirect anywhere you choose, and (undocumented but near-certain) leaves the Google session intact so a reload will sign straight back in.

### What would move this to full in-origin verification

Do the `jsonwebtoken` work from §2 when any of these becomes true:

- The backend becomes reachable from anywhere you do not fully control (a second site, a VPN, a cloud move) — at that point step 1's guarantee stops covering the whole surface.
- Ticket data becomes something you would have to answer for in an audit, or acquires a destructive operation (delete, resolve-on-behalf-of, admin actions).
- You add a second Access application in the same Zero Trust org. Then `aud` checking starts doing real work — a valid token from the other app would otherwise be replayable, and only an `aud` check catches that. `cloudflared`'s `audTag` covers the tunnel path, but a per-origin check is the belt-and-braces version.
- You add any authorization decision beyond "is a logged-in staff member" — roles, per-station scoping, anything where the *identity* of the caller changes what they may do rather than just what gets logged.

The snippet in §2 is ~60 lines and has no exotic dependencies. It is a small job whenever you decide to do it; the reason to defer is not difficulty, it is that step 1 already buys the documented guarantee for the documented threat, and step 2 buys the LAN case. Deferring is a schedule choice, not a security shortcut — provided steps 1 and 2 actually ship together with the feature.

---

## Open / undocumented

Things I could **not** confirm from a primary source. Each is either an inference clearly derived from documented behaviour, or a genuine gap.

1. **`name` in the `get-identity` response is undocumented.** It is not in Cloudflare's [field table](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/#user-identity). It works here and is Google-sourced, but Cloudflare makes no commitment. Treat as best-effort; always have an `email` fallback.
2. **`id` in the `get-identity` response is undocumented.** Only `user_uuid` is documented as "the ID of the user". Do not key anything on `id`.
3. **Sub-field shapes of `idp` and `geo` are undocumented.** The parent fields are documented as "Data from your identity provider" and "The country where the user authenticated from"; `idp.id`, `idp.type`, `geo.country` are not specified anywhere I could find.
4. **`iat: 0` and `ip: ""` contradict their own documented descriptions** with no documented explanation. Do not surface them.
5. **`auth_status: "NONE"` and `version: 0` are undocumented values** of documented fields. `auth_status` is documented as mTLS-related so `"NONE"` is coherent, but the enumeration of possible values is not published anywhere I found.
6. **`get-identity` on the *application* domain is undocumented.** Cloudflare documents and implements it only against the team domain. This deployment relies on the app-domain path. It works; it is not a published contract.
7. **Behaviour of `/cdn-cgi/access/*` for requests that never traverse Cloudflare is entirely undocumented.** The "your own router answers and 404s" conclusion is inference from [the `/cdn-cgi/` endpoint page](https://developers.cloudflare.com/fundamentals/reference/cdn-cgi-endpoint/) plus [tunnel architecture](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/). Cloudflare says nothing about it. Note also that the `/cdn-cgi/` reference page does not even list `/cdn-cgi/access/*` among its reserved paths, so that inventory is itself incomplete.
8. **No logout redirect parameter.** Not documented on [Session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/#log-out-as-a-user), and Cloudflare's own `generateLogoutURL({ domain })` takes no redirect argument while `generateLoginURL` takes `redirect_url`. Concluding "none exists" is inference from that asymmetry, not a documented denial.
9. **Whether Access logout terminates the Google/IdP session is not documented anywhere I could find.** Neither [Session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/) nor the [Identity FAQ](https://developers.cloudflare.com/cloudflare-one/faq/authentication-faq/) addresses IdP session propagation or single-logout. All documented effects are Access-scoped. My inference is that the Google session survives; **this should be empirically tested on a shared workstation before any shared-terminal logout is promised to users.**
10. **`Cf-Access-Authenticated-User-Email` appears only in a tutorial, never in a reference page.** [Create custom headers for Access-protected origins with Workers](https://developers.cloudflare.com/cloudflare-one/tutorials/access-workers/) uses it; the [Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/) and [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/) reference pages do not mention it. That tutorial carries no warning about trusting it. I could not find a specification of when it is set, whether it is set for service tokens, or whether it is stripped from client-supplied requests. **Do not build on it.**
11. **No Cloudflare page states in words "do not trust identity supplied by the browser."** The position is derived from the "identity spoofing" and "requests which bypass Cloudflare Access […] are rejected" statements plus Cloudflare's own reference implementation separating JWT validation from `getIdentity`. The synthesis is mine.
12. **JWKS caching TTL has no documented recommendation.** The 7-day rotated-key grace is documented; a specific cache TTL is not. The 6-hour figure in my snippet and the 3-day figure in `cf-access` are both engineering judgement inside that window.
13. **`cf-access` crate maintenance status is not declared.** No archived flag, no deprecation notice — but two releases on one day 16 months ago and 14 downloads in 90 days. "Dormant" is my read of the crates.io signal, not a statement by the author.
14. **Cloudflare publishes no Rust example** for Access JWT validation. The [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/) page ships Workers/TypeScript, Go, Python and Node examples only. The Rust snippet in §2 is my translation of the documented procedure; every API call in it is verified against docs.rs, but the composition is not Cloudflare-blessed and has not been compiled or run.
