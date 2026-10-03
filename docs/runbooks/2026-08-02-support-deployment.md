# Runbook: deploying the Help & Support Center

**Applies to:** the `/support` page, its four Postgres tables, and the Cloudflare Access
identity surface shipped by [`docs/specs/2026-08-02-support-settings-rebrand.md`](../specs/2026-08-02-support-settings-rebrand.md).

**Do steps 1–4 in order, before or with the release — not after.** Step 1 is the *entire*
signature verification for this feature; there is deliberately none in Rust (spec §4.1). Shipping
the code without step 1 leaves every mutating `/support` route accepting any self-minted JWT from
anywhere the origin is reachable.

**Rollback:** steps 1–3 are reversible in seconds (turn Access back off, delete the bucket, unset
the variable). Step 4 creates new tables and touches nothing existing, so a code rollback needs no
DB rollback — leave the tables in place.

---

## 1. Enable "Protect with Access" on the tunnel

This is what performs the RS256 signature check and the `aud` check before any request reaches
Axum. Without it the header the backend trusts is just a header.

### 1a. Get the AUD tag

1. Cloudflare dashboard → **Zero Trust** → **Access controls** → **Applications**.
2. **Configure** on this app's application.
3. **Additional settings** → copy **Application Audience (AUD) Tag**.

The AUD tag is a 64-character hex string. It never changes unless the Access application is deleted
and recreated. Also note your **team name** (the `<team>` in `https://<team>.cloudflareaccess.com`).

### 1b. Turn it on for the public hostname

**If the tunnel is managed in the dashboard** (Zero Trust → **Networks** → **Tunnels** → this
tunnel → **Public Hostname** → **Edit** on this app's hostname):
open **Additional application settings** → **Access** → toggle **Protect with Access** on, and
paste the team name and AUD tag into the fields that appear. **Save**.

**If the tunnel runs from a local `cloudflared` config file**, add the `access` block to this app's
ingress rule:

```yaml
ingress:
  - hostname: <app-hostname>
    service: http://localhost:3000
    originRequest:
      access:
        required: true
        teamName: <team>
        audTag:
          - <Application Audience (AUD) Tag>
  - service: http_status:404
```

Then reload:

```bash
cloudflared tunnel ingress validate     # must print "OK"
sudo systemctl restart cloudflared      # or: docker restart <cloudflared-container>
```

### 1c. Verify

From a machine **outside** the LAN, with no Access session:

```bash
curl -sS -o /dev/null -w '%{http_code} %{redirect_url}\n' https://<app-hostname>/support
```

**Expected:** `302 https://<team>.cloudflareaccess.com/cdn-cgi/access/login/...`
(a redirect into the Access login flow).

**If you get `200` instead:** Access is not attached to that hostname. Re-check that you edited the
*public hostname* entry for this app and not a different one, and that you saved. Do not proceed —
every later step assumes this is on.

Then, signed in through Access in a browser, confirm identity is reaching the app:

```
https://<app-hostname>/cdn-cgi/access/get-identity
```

**Expected:** a JSON body containing `email` (and usually `name`). The sidebar footer reads this.
A `404` with an HTML body means you are not behind Access on that hostname.

> **Access must cover GET routes too.** `GET /support/tickets/{id}` and
> `GET /support/attachments/{id}/media` take no identity extractor by design (spec §5) — the
> tunnel is the only thing gating reads. Do not add a bypass/exclude rule for `/support` paths.

---

## 2. Create the MinIO bucket `support`

The bucket name is **hardcoded** in the backend (`src/api/support.rs`, `state.bucket("support")`).
It is not read from `MINIO_BUCKET` or any other variable. It must be exactly `support`.

```bash
mc alias set prod http://<minio-host>:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"
mc mb prod/support
mc ls prod
```

**Expected:** `Bucket created successfully 'prod/support'.`, and `support/` in the `mc ls` output.

If MinIO runs under this repo's compose file, `mc` is inside the container:

```bash
docker compose -f docker/compose.minio.yml exec minio \
  mc alias set local http://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"
docker compose -f docker/compose.minio.yml exec minio mc mb local/support
```

**Failure — `mc mb` returns `Bucket already exists`:** fine, skip.
**Failure — `Access Denied`:** you are using the service key from `MINIO_ACCESS_KEY`, which may not
have bucket-creation rights. Use the root credentials (`MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`
from `docker/.env`) for this one command.
**Consequence of skipping this step:** ticket filing and commenting still work; every attachment
upload fails with a 500 and the file is lost. The failure is not visible until a user tries.

### `MINIO_ENDPOINT` is a hostname, not a URL

`src/main.rs:24-26` builds the endpoint as `format!("http://{}:{}", MINIO_ENDPOINT, MINIO_PORT)`.
Setting `MINIO_ENDPOINT=http://minio:9000` yields `http://http://minio:9000:9000` and every upload
dies with:

```
internal error: MinIO upload failed: hyper: error trying to connect: dns error:
failed to lookup address information: nodename nor servname provided, or not known
```

Correct: `MINIO_ENDPOINT=minio` (or the bare host/IP) plus `MINIO_PORT=9000`. `docker/.env.example`
already has the right shape — copy it, don't invent one. This misconfiguration is silent until the
first attachment upload, so verify it as part of step 5, check 2.

---

## 3. Confirm `ACCESS_AUTH` is unset in production

The extractor is default-secure: `ACCESS_AUTH` unset — or set to anything other than the exact
string `dev_bypass` — means a request with no `Cf-Access-Jwt-Assertion` header is rejected with
403 (`src/auth.rs:50-64`). Only `ACCESS_AUTH=dev_bypass` opens the headerless path, and it
fabricates a `dev@localhost` identity that would then be written into `reporter_email`.

```bash
grep -n ACCESS_AUTH docker/.env
docker compose -f docker/compose.yml exec backend printenv ACCESS_AUTH
```

**Expected:** the `grep` prints nothing (exit 1), and `printenv` prints nothing and exits 1.

**If either prints `dev_bypass`:** remove the line from `docker/.env` and recreate the container
(`docker compose -f docker/compose.yml up -d --force-recreate backend`). Re-run step 5, check 1
before declaring the deploy done.

---

## 4. Apply the migration

**File:** `backend/migrations/20260803120000_support_tickets.sql`
**Creates:** 4 tables (`support_tickets`, `support_ticket_comments`, `support_ticket_events`,
`support_ticket_attachments`) + 6 indexes. It contains no `ALTER` against any existing table and
touches no existing row.

> **Do not use `sqlx migrate run`.** It is blocked repo-wide: production ran its migrations from a
> CRLF checkout, so every stored checksum mismatches an LF working tree and the command aborts
> before applying anything. For the same reason `_sqlx_migrations` is **not** a reliable record of
> what is applied to a given database — check the schema itself, not that table.

Apply by hand:

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 \
  -f backend/migrations/20260803120000_support_tickets.sql
```

If Postgres runs in the compose stack and is not reachable from the deploy host directly:

```bash
docker compose -f docker/compose.db.yml exec -T db \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 \
  < backend/migrations/20260803120000_support_tickets.sql
```

**Expected output:** `CREATE TABLE` ×4 and `CREATE INDEX` ×6, in that interleaved order, with no
`ERROR`.

**The file is fully idempotent** — every statement is `CREATE TABLE IF NOT EXISTS` or
`CREATE INDEX IF NOT EXISTS`. A re-run is safe and prints `NOTICE: relation "…" already exists,
skipping` instead of `CREATE …`. If you are unsure whether it ran, **run it again**; that is
cheaper than guessing.

### Verify the schema, not the migrations table

```bash
psql "$DATABASE_URL" -c '\d support_tickets'
psql "$DATABASE_URL" -c "\dt support_ticket*"
```

**Expected:** four tables listed, and `support_tickets` showing the constraint

```
"support_tickets_close_reason_chk" CHECK (
  status = 'open'::text AND close_reason IS NULL
  OR status = 'closed'::text AND close_reason IS NOT NULL
     AND close_reason = ANY (ARRAY['completed'::text, 'not_planned'::text, 'duplicate'::text]))
```

The `close_reason IS NOT NULL` must be present. Without it a closed ticket with a NULL reason
passes the check (three-valued logic: `FALSE OR NULL` is NULL, and CHECK rejects only FALSE). If
your database shows the constraint *without* `IS NOT NULL`, it was created from the old spec text —
drop and recreate just that constraint:

```sql
ALTER TABLE public.support_tickets DROP CONSTRAINT support_tickets_close_reason_chk;
ALTER TABLE public.support_tickets ADD CONSTRAINT support_tickets_close_reason_chk CHECK (
    (status = 'open'   AND close_reason IS NULL) OR
    (status = 'closed' AND close_reason IS NOT NULL
                       AND close_reason IN ('completed','not_planned','duplicate'))
);
```

**Failure — `permission denied for schema public`:** the `DATABASE_URL` role cannot create tables.
Re-run as the `POSTGRES_USER` superuser from the compose form above.
**Failure — anything else:** `ON_ERROR_STOP=1` means nothing partial was left behind for that
statement; fix the cause and re-run the whole file.

---

## 5. Post-deploy smoke

Spec §7. Run these against the deployed stack, signed in through Access in a real browser except
where noted. Tick every line before calling the deploy good.

**1. Origin rejects unauthenticated writes (§7.3) — do this one first, from the LAN, with curl.**

```bash
curl -i -X POST http://<origin-host>:8080/support/tickets \
  -H 'content-type: application/json' \
  -d '{"title":"forged","category":"bug"}'
```

**Expected:** `HTTP/1.1 403 Forbidden`.
**If you get `201`:** `ACCESS_AUTH=dev_bypass` is live in production. Stop, redo step 3, and
delete the ticket row that curl just created.
Repeat with `PATCH /support/tickets/1`, `POST /support/tickets/1/close`,
`POST /support/tickets/1/reopen`, `POST /support/tickets/1/comments`,
`PATCH /support/comments/1`, `POST /support/tickets/1/attachments` — all must be 403.
`GET` routes returning 200 here is expected and correct (spec §5).

**2. File a ticket (§7.1).** `/support` → **New ticket**. Title, category, body, a related parcel
picked from the typeahead, one image **and** one `.xlsx` attached. Save.
**Expected:** the ticket appears in the **Open** tab with the open dot glyph, its category chip, and
attachment/comment counts on the row.

**3. Identity is taken from the JWT, not the browser (§7.2).**

```bash
psql "$DATABASE_URL" -c \
  "SELECT id, reporter_email, reporter_name FROM support_tickets ORDER BY id DESC LIMIT 1;"
```

**Expected:** `reporter_email` is the email of the signed-in Access user.
**If it is `dev@localhost`:** step 3 was not done — `ACCESS_AUTH=dev_bypass` is set.

**4. Close and reopen (§7.4).** From the ticket's sidebar Status card, close with a reason, then
reopen.
**Expected:** two new event rows in the thread ("closed this as …", "reopened this"), and

```bash
psql "$DATABASE_URL" -c "SELECT status, close_reason FROM support_tickets WHERE id = <id>;"
# → open | (null)
```

**5. Author-only edit (§7.5).** As a **second** Access user, try to edit the first user's ticket
and comment.
**Expected:** refused (403 surfaced in the UI). As the original author the same edit succeeds and
the item shows "(edited)".

**6. Parcel mention (§7.6).** Type `#678` (or any run present in your data) in a composer.
**Expected:** at most 5 rows, matched against tracking **or** order number, matched run
highlighted, real platform logos on the rows. ↑/↓ + Enter inserts the plain tracking number.

**7. Linkified parcel (§7.7).** Save text containing a tracking number.
**Expected:** it renders as a mono brand link; clicking opens the Order Timeline modal.

**8. Sidebar (§7.8).** **Expected:** no "System" group; a "Help" group containing **Support**; the
footer shows the signed-in name and email.

**9. Settings modal (§7.9).** Footer identity row → **Settings**.
**Expected:** the modal opens; the icon segmented control changes theme; the choice survives a
reload; navigating to `/settings` lands on `/`.

**10. Off-tunnel fallback (§7.10).** Load the dashboard directly on the LAN
(`http://<origin-host>:3000`), bypassing the tunnel.
**Expected:** the sidebar shows the "Not signed in" fallback, the browser console is clean, and the
rest of the dashboard works. The user menu still opens, Settings still works, **Log out** is hidden.

---

## 6. Recorded accepted risk — read before signing off

**This feature is verified at the tunnel, trusted at the origin.** Never describe the origin as
"verified".

`cloudflared` checks the RS256 signature and the `aud` claim. Axum does not: `src/auth.rs`
base64-decodes the JWT payload and reads `email`/`sub` without checking the signature, the issuer,
or expiry — deliberately, and documented in the module header. What the origin *does* add is the
thing the tunnel cannot: a request carrying **no** `Cf-Access-Jwt-Assertion` header at all is
rejected outright with 403, which is what closes the casual LAN path.

**The residual, accepted v1 risk:** a deliberate forger on the warehouse LAN who posts a self-minted
JWT straight to the origin's `:8080` is **not** stopped. They can file, comment on, close, reopen,
and edit tickets under any email they choose, and the audit trail will record that email as fact.
Accepted for a firewalled LAN with a handful of staff.

Consequences to keep in mind when reading support data:

- `reporter_email` / `author_email` / `actor_email` are trustworthy **only** for traffic that came
  through the tunnel. They are attribution, not authentication.
- The author-only edit gate (§7.5) is an attribution guard, not a security boundary — it compares
  the caller's claimed email to the stored one.
- Do not extend this trust model to anything with real authorization consequences (money, deletion,
  role changes) without adding real JWT verification in Rust first. Re-open
  [#93](https://github.com/nongmelt/naff-warehouse-application/issues/93) before weakening any of §4.

**Closing this risk later** means adding signature + `aud` + `exp` verification in `src/auth.rs`
against `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`. The crate survey and a working
Axum shape are already written up in
[`docs/research/2026-08-02-cloudflare-access-identity.md`](../research/2026-08-02-cloudflare-access-identity.md) §2.
