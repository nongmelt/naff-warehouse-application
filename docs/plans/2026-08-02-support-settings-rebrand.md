# Help & Support Center + Settings Rebrand — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Spec:** [`docs/specs/2026-08-02-support-settings-rebrand.md`](../specs/2026-08-02-support-settings-rebrand.md) — read it before Task 1. Every "why" lives there; this document is the "how".

**Goal:** Ship a `/support` ticketing page backed by new Postgres tables and Axum CRUD, replace the "System" sidebar group with a Help group plus a Cloudflare-Access identity footer, and move settings into a Claude.ai-style modal.

**Architecture:** Four new Postgres tables (`support_tickets` + comments/events/attachments) shaped after the existing `issue_reports` family; a new `backend/src/api/support.rs` handler module whose mutating routes take a `Cf-Access-Jwt-Assertion` extractor from a new `backend/src/auth.rs`; a new `frontend/app/support` route reusing the Delivery Issues chrome verbatim; sidebar and settings changes confined to `Sidebar.tsx` plus two new components.

**Tech Stack:** Rust / Axum / SQLx / PostgreSQL / MinIO (`rust-s3`) on the backend; Next.js 16 App Router / React / Tailwind v4 / Vitest + jsdom on the frontend. No component library — bespoke primitives only.

## Global Constraints

- Work spans two git submodules. `backend/` and `frontend/` each need their own commits; this plan and the spec live in the **root** repo. Use `.worktrees/<topic>` inside each submodule — do not switch the main checkout's branch.
- Backend models use `#[serde(rename_all = "camelCase")]`. Handlers return `Result<_, AppError>`. 404 is `AppError::NotFound`, 403 is `AppError::Forbidden(String)`, 400 is `AppError::BadRequest(String)`.
- SQLx is compile-time checked. Either point `DATABASE_URL` at the dev DB or regenerate `.sqlx/` and build with `SQLX_OFFLINE=true`.
- The shared dev DB has an orphan `20260703220000` migration row with no file. `sqlx migrate run` there needs `--ignore-missing`. **Do not delete that row.**
- Never edit an already-applied migration. Prod ran every file from a CRLF checkout, so its recorded checksums cannot survive an in-place edit. Stack a new file instead.
- Domain vocabulary is fixed: table `support_tickets`, routes under `/support/…`, UI heading **"Help & Support Center"**, sidebar item **"Support"** in a group labelled **"Help"**.
- Status is `open` / `closed` only. Close reasons are `completed` / `not_planned` / `duplicate`. Categories are `bug` / `feature_request` / `question` / `data_problem` / `other`. Event kinds are `filed` / `edited` / `closed` / `reopened`. Nothing else.
- Identity for writes comes from the `Cf-Access-Jwt-Assertion` header. A body-supplied email is ignored. A body-supplied *name* is allowed as a display hint only.
- Logout copy is exactly: menu item **"Log out"**, hint line **"Ends your dashboard session. You may still be signed in to Google."**
- Mention/typeahead match semantics: contiguous character run starting anywhere, matched against tracking **and** order number, matched run highlighted, max 5 rows in the mention popup.
- Mention rows use the real `PlatformBadge` / `PlatformGlyph` logos from `app/lib/platform.tsx`. The mockup's letter squares are placeholders.
- Visual reference for everything: `docs/mockups/2026-08-02-support-settings-rebrand.html` (root repo, commit `36fe699`). Open it in a browser before any UI task.

---

## File Structure

**Backend (`backend/`)**

| File | Responsibility |
|---|---|
| `migrations/20260803120000_support_tickets.sql` | Create the four tables (Task 1) |
| `src/auth.rs` | `AccessIdentity` extractor (Task 2) |
| `src/api/support.rs` | All `/support/*` handlers (Tasks 3–7) |
| `src/api/mod.rs` | Route registration (Tasks 3–8) |
| `src/api/packing.rs` | `limit` param on the existing suggest endpoint (Task 8) |
| `tests/support_tickets.rs` | Integration tests for Tasks 1–7 |

**Frontend (`frontend/app/`)**

| File | Responsibility |
|---|---|
| `types.ts` | Ticket/comment/event/attachment/identity interfaces (Task 9) |
| `hooks/useAccessIdentity.ts` | get-identity fetch + fallback (Task 9) |
| `hooks/useSupportTickets.ts` | List, summary, filters (Task 9) |
| `hooks/useSupportTicket.ts` | Detail + mutations (Task 9) |
| `hooks/useParcelSuggest.ts` | Debounced parcel suggest (Task 9) |
| `components/Sidebar.tsx` | Group changes + footer identity mount (Tasks 10, 11) |
| `components/Sidebar.rail.test.tsx` | Updated group titles and hrefs (Task 10) |
| `components/SidebarUserMenu.tsx` | Identity row + popover menu (Task 11) |
| `components/SettingsModal.tsx` | Settings modal (Task 12) |
| `settings/page.tsx` | Redirect to `/` (Task 12) |
| `lib/matchHighlight.tsx` | Contiguous-run match + highlight (Task 13) |
| `lib/parcelLinkify.tsx` | Render-side parcel auto-linking (Task 13) |
| `support/page.tsx` | Route shell (Task 14) |
| `components/support/supportUi.tsx` | Status glyphs, category chips (Task 14) |
| `components/support/SupportDashboard.tsx` | List view (Task 14) |
| `components/support/ParcelTypeahead.tsx` | Related-parcel field (Task 15) |
| `components/support/NewTicketModal.tsx` | Filing modal (Task 15) |
| `components/support/SupportTicketDetail.tsx` | Detail view + thread (Task 16) |
| `components/support/ParcelMentionPopup.tsx` | `#` caret popup (Task 17) |
| `components/support/AttachmentPicker.tsx` | File/image chips + upload (Task 18) |

---

## Task 1: Schema migration

**Files:**
- Create: `backend/migrations/20260803120000_support_tickets.sql`
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: nothing.
- Produces: tables `support_tickets`, `support_ticket_comments`, `support_ticket_events`, `support_ticket_attachments` with the exact column names every later backend task binds to.

- [ ] **Step 1: Check the timestamp is free**

Run: `ls backend/migrations/ | tail -3`
If any file sorts after `20260803120000`, rename the new migration to the next free `YYYYMMDDHHMMSS` and use that name for the rest of this task.

- [ ] **Step 2: Write the migration**

Copy §3 of the spec verbatim into `backend/migrations/20260803120000_support_tickets.sql`. It is complete DDL — four `CREATE TABLE IF NOT EXISTS` blocks with their `CREATE INDEX IF NOT EXISTS` statements, no trigger, no `pg_notify`.

- [ ] **Step 3: Write the failing test**

Create `backend/tests/support_tickets.rs`. Copy the `spawn_app()` helper verbatim from `backend/tests/issues.rs` lines 1–30 (same imports, same bucket names — the support bucket is only touched in Task 7), then add:

```rust
#[tokio::test]
async fn support_tickets_table_enforces_its_checks() {
    let (_base, pool) = spawn_app().await;

    let id: (i32,) = sqlx::query_as(
        "INSERT INTO support_tickets (title, category, reporter_email)
         VALUES ('Scanner freezes on QC', 'bug', 'a@example.com') RETURNING id",
    )
    .fetch_one(&pool).await.unwrap();
    assert!(id.0 > 0);

    let bad_category = sqlx::query(
        "INSERT INTO support_tickets (title, category, reporter_email)
         VALUES ('x', 'not_a_category', 'a@example.com')",
    ).execute(&pool).await;
    assert!(bad_category.is_err(), "category CHECK must reject unknown values");

    let open_with_reason = sqlx::query(
        "INSERT INTO support_tickets (title, category, reporter_email, status, close_reason)
         VALUES ('x', 'bug', 'a@example.com', 'open', 'completed')",
    ).execute(&pool).await;
    assert!(open_with_reason.is_err(), "an open ticket must not carry a close_reason");

    let closed_without_reason = sqlx::query(
        "INSERT INTO support_tickets (title, category, reporter_email, status)
         VALUES ('x', 'bug', 'a@example.com', 'closed')",
    ).execute(&pool).await;
    assert!(closed_without_reason.is_err(), "a closed ticket must carry a close_reason");

    let bad_kind = sqlx::query(
        "INSERT INTO support_ticket_events (ticket_id, kind) VALUES ($1, 'commented')",
    ).bind(id.0).execute(&pool).await;
    assert!(bad_kind.is_err(), "event kind set is filed/edited/closed/reopened only");

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id.0)
        .execute(&pool).await.unwrap();
}

#[tokio::test]
async fn deleting_a_ticket_cascades_to_children() {
    let (_base, pool) = spawn_app().await;
    let t: (i32,) = sqlx::query_as(
        "INSERT INTO support_tickets (title, category, reporter_email)
         VALUES ('cascade', 'question', 'a@example.com') RETURNING id",
    ).fetch_one(&pool).await.unwrap();
    let c: (i32,) = sqlx::query_as(
        "INSERT INTO support_ticket_comments (ticket_id, body, author_email)
         VALUES ($1, 'hi', 'a@example.com') RETURNING id",
    ).bind(t.0).fetch_one(&pool).await.unwrap();

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(t.0)
        .execute(&pool).await.unwrap();

    let left: (i64,) = sqlx::query_as("SELECT count(*) FROM support_ticket_comments WHERE id = $1")
        .bind(c.0).fetch_one(&pool).await.unwrap();
    assert_eq!(left.0, 0);
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `cd backend && cargo test --test support_tickets`
Expected: FAIL — `relation "support_tickets" does not exist`.

- [ ] **Step 5: Apply the migration**

Run: `cd backend && sqlx migrate run --ignore-missing`
Expected: the new migration is listed as applied.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets`
Expected: 2 passed.

- [ ] **Step 7: Commit**

```bash
cd backend
git add migrations/20260803120000_support_tickets.sql tests/support_tickets.rs
git commit -m "feat(support): support ticket tables"
```

---

## Task 2: Access identity extractor

**Files:**
- Create: `backend/src/auth.rs`
- Modify: `backend/src/lib.rs` (add `pub mod auth;` beside the existing module declarations)
- Test: inline `#[cfg(test)]` module in `backend/src/auth.rs`

**Interfaces:**
- Consumes: `AppError` from `crate::error`.
- Produces: `pub struct AccessIdentity { pub sub: String, pub email: String }` implementing `axum::extract::FromRequestParts<S>` with `Rejection = AppError`, plus `pub const ACCESS_HEADER: &str = "cf-access-jwt-assertion";`. Later tasks add `identity: AccessIdentity` as a handler argument and read `identity.email` / `identity.sub`.

- [ ] **Step 1: Write the failing test**

Create `backend/src/auth.rs` containing only this test module for now:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    fn token(payload: &serde_json::Value) -> String {
        use base64::Engine;
        let b64 = base64::engine::general_purpose::URL_SAFE_NO_PAD;
        format!(
            "{}.{}.sig",
            b64.encode(br#"{"alg":"RS256","kid":"k"}"#),
            b64.encode(payload.to_string().as_bytes())
        )
    }

    #[test]
    fn decodes_email_and_sub_from_the_payload() {
        let t = token(&serde_json::json!({ "email": "a@example.com", "sub": "u-1" }));
        let id = decode_identity(&t).unwrap();
        assert_eq!(id.email, "a@example.com");
        assert_eq!(id.sub, "u-1");
    }

    #[test]
    fn rejects_a_service_token() {
        // Service tokens carry common_name, an empty sub and no email.
        let t = token(&serde_json::json!({ "common_name": "svc", "sub": "" }));
        assert!(decode_identity(&t).is_err(), "service tokens must not author tickets");
    }

    #[test]
    fn rejects_a_malformed_token() {
        assert!(decode_identity("not-a-jwt").is_err());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd backend && cargo test auth::`
Expected: FAIL — `cannot find function decode_identity`.

- [ ] **Step 3: Implement the extractor**

Add above the test module in `backend/src/auth.rs`:

```rust
//! Cloudflare Access identity.
//!
//! Signature verification happens in `cloudflared` ("Protect with Access"),
//! not here — see docs/specs/2026-08-02-support-settings-rebrand.md §4. What
//! this module adds on top is the thing cloudflared cannot: an unauthenticated
//! LAN request carries no header at all, and is rejected outright.

use axum::extract::FromRequestParts;
use axum::http::request::Parts;
use base64::Engine;

use crate::error::AppError;

pub const ACCESS_HEADER: &str = "cf-access-jwt-assertion";

#[derive(Debug, Clone)]
pub struct AccessIdentity {
    pub sub: String,
    pub email: String,
}

pub fn decode_identity(token: &str) -> Result<AccessIdentity, AppError> {
    let payload = token
        .split('.')
        .nth(1)
        .ok_or_else(|| AppError::Forbidden("malformed Access token".into()))?;
    let raw = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(payload)
        .map_err(|_| AppError::Forbidden("malformed Access token".into()))?;
    let claims: serde_json::Value = serde_json::from_slice(&raw)
        .map_err(|_| AppError::Forbidden("malformed Access token".into()))?;

    let email = claims.get("email").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let sub = claims.get("sub").and_then(|v| v.as_str()).unwrap_or("").to_string();
    if email.is_empty() || sub.is_empty() {
        // Service-token shape: common_name set, sub empty, no email.
        return Err(AppError::Forbidden("Access token carries no user identity".into()));
    }
    Ok(AccessIdentity { sub, email })
}

impl<S: Send + Sync> FromRequestParts<S> for AccessIdentity {
    type Rejection = AppError;

    async fn from_request_parts(parts: &mut Parts, _state: &S) -> Result<Self, Self::Rejection> {
        match parts.headers.get(ACCESS_HEADER).and_then(|v| v.to_str().ok()) {
            Some(token) => decode_identity(token),
            None => {
                // Default is secure: a deployment that forgets the env var still rejects.
                if std::env::var("ACCESS_AUTH").as_deref() == Ok("dev_bypass") {
                    return Ok(AccessIdentity {
                        sub: "dev".to_string(),
                        email: "dev@localhost".to_string(),
                    });
                }
                Err(AppError::Forbidden("missing Cf-Access-Jwt-Assertion".into()))
            }
        }
    }
}
```

Add `base64` to `[dependencies]` in `backend/Cargo.toml` if `cargo tree -p base64` shows it is not already a direct dependency.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd backend && cargo test auth::`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
cd backend
git add src/auth.rs src/lib.rs Cargo.toml Cargo.lock
git commit -m "feat(support): Cloudflare Access identity extractor"
```

---

## Task 3: Create, list, and summarise tickets

**Files:**
- Create: `backend/src/api/support.rs`
- Modify: `backend/src/api/mod.rs` (add `mod support;` and three routes)
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: `AccessIdentity` (Task 2), the tables from Task 1.
- Produces: `support::create`, `support::list`, `support::summary`; the wire structs `SupportTicket`, `CreateTicketRequest`, `ListResponse { tickets, total }`, `Summary { open, closed }`. Task 4 reuses `SupportTicket` and adds `load_ticket`.

- [ ] **Step 1: Write the failing test**

Append to `backend/tests/support_tickets.rs`:

```rust
fn access_header(email: &str) -> (&'static str, String) {
    use base64::Engine;
    let b64 = base64::engine::general_purpose::URL_SAFE_NO_PAD;
    let payload = serde_json::json!({ "email": email, "sub": format!("u-{email}") });
    (
        "Cf-Access-Jwt-Assertion",
        format!(
            "{}.{}.sig",
            b64.encode(br#"{"alg":"RS256"}"#),
            b64.encode(payload.to_string().as_bytes())
        ),
    )
}

#[tokio::test]
async fn create_requires_the_access_header() {
    let (base, _pool) = spawn_app().await;
    let res = reqwest::Client::new()
        .post(format!("{base}/support/tickets"))
        .json(&serde_json::json!({ "title": "no header", "category": "bug" }))
        .send().await.unwrap();
    assert_eq!(res.status(), 403);
}

#[tokio::test]
async fn create_takes_the_reporter_email_from_the_jwt_not_the_body() {
    let (base, pool) = spawn_app().await;
    let (h, v) = access_header("real@example.com");
    let created: serde_json::Value = reqwest::Client::new()
        .post(format!("{base}/support/tickets"))
        .header(h, v)
        .json(&serde_json::json!({
            "title": "Scanner freezes on QC",
            "body": "Happens on station 3",
            "category": "bug",
            "reporterEmail": "spoofed@example.com",
            "reporterName": "Real Person",
            "trackingNumber": "TH-SUP-1"
        }))
        .send().await.unwrap()
        .json().await.unwrap();

    assert_eq!(created["reporterEmail"], "real@example.com");
    assert_eq!(created["reporterName"], "Real Person");
    assert_eq!(created["status"], "open");
    assert!(created["closeReason"].is_null());

    let id = created["id"].as_i64().unwrap() as i32;
    let kinds: Vec<(String,)> = sqlx::query_as(
        "SELECT kind FROM support_ticket_events WHERE ticket_id = $1",
    ).bind(id).fetch_all(&pool).await.unwrap();
    assert_eq!(kinds, vec![("filed".to_string(),)], "creating a ticket writes one filed event");

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id)
        .execute(&pool).await.unwrap();
}

#[tokio::test]
async fn create_rejects_an_empty_or_overlong_title() {
    let (base, _pool) = spawn_app().await;
    let client = reqwest::Client::new();
    for title in ["", &"x".repeat(121)] {
        let (h, v) = access_header("a@example.com");
        let res = client.post(format!("{base}/support/tickets"))
            .header(h, v)
            .json(&serde_json::json!({ "title": title, "category": "bug" }))
            .send().await.unwrap();
        assert_eq!(res.status(), 400, "title {:?} must be rejected", title.len());
    }
}

#[tokio::test]
async fn list_filters_by_status_and_search_and_reports_counts() {
    let (base, pool) = spawn_app().await;
    let client = reqwest::Client::new();
    let (h, v) = access_header("a@example.com");
    let created: serde_json::Value = client.post(format!("{base}/support/tickets"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "Zebra printer jams", "category": "bug" }))
        .send().await.unwrap().json().await.unwrap();
    let id = created["id"].as_i64().unwrap() as i32;

    let found: serde_json::Value = client
        .get(format!("{base}/support/tickets?status=open&search=Zebra"))
        .send().await.unwrap().json().await.unwrap();
    assert!(found["tickets"].as_array().unwrap().iter()
        .any(|t| t["id"].as_i64() == Some(id as i64)));

    let closed_only: serde_json::Value = client
        .get(format!("{base}/support/tickets?status=closed&search=Zebra"))
        .send().await.unwrap().json().await.unwrap();
    assert!(closed_only["tickets"].as_array().unwrap().is_empty());

    let summary: serde_json::Value = client
        .get(format!("{base}/support/tickets/summary"))
        .send().await.unwrap().json().await.unwrap();
    assert!(summary["open"].as_i64().unwrap() >= 1);

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id)
        .execute(&pool).await.unwrap();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && cargo test --test support_tickets`
Expected: FAIL — 404 responses, because `/support/tickets` is not routed.

- [ ] **Step 3: Implement the handlers**

Create `backend/src/api/support.rs`:

```rust
use axum::extract::{Json, Path, Query, State};
use serde::{Deserialize, Serialize};
use sqlx::PgPool;

use crate::auth::AccessIdentity;
use crate::error::AppError;

#[derive(Debug, Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
pub struct SupportTicket {
    pub id: i32,
    pub title: String,
    pub body: Option<String>,
    pub category: String,
    pub category_note: Option<String>,
    pub status: String,
    pub close_reason: Option<String>,
    pub tracking_number: Option<String>,
    pub order_number: Option<String>,
    pub reporter_email: String,
    pub reporter_name: Option<String>,
    pub created_at: chrono::DateTime<chrono::Utc>,
    pub updated_at: chrono::DateTime<chrono::Utc>,
    pub edited_at: Option<chrono::DateTime<chrono::Utc>>,
    pub closed_at: Option<chrono::DateTime<chrono::Utc>>,
    pub closed_by: Option<String>,
}

const TICKET_COLS: &str = "id, title, body, category, category_note, status, close_reason,
    tracking_number, order_number, reporter_email, reporter_name,
    created_at, updated_at, edited_at, closed_at, closed_by";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateTicketRequest {
    pub title: String,
    pub body: Option<String>,
    pub category: String,
    pub category_note: Option<String>,
    pub tracking_number: Option<String>,
    pub order_number: Option<String>,
    /// Display hint only. The email always comes from the JWT.
    pub reporter_name: Option<String>,
}

pub async fn create(
    State(pool): State<PgPool>,
    identity: AccessIdentity,
    Json(req): Json<CreateTicketRequest>,
) -> Result<Json<SupportTicket>, AppError> {
    let title = req.title.trim();
    if title.is_empty() || title.chars().count() > 120 {
        return Err(AppError::BadRequest("title must be 1–120 characters".into()));
    }

    let mut tx = pool.begin().await?;
    let ticket: SupportTicket = sqlx::query_as(&format!(
        "INSERT INTO support_tickets
           (title, body, category, category_note, tracking_number, order_number,
            reporter_email, reporter_name)
         VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
         RETURNING {TICKET_COLS}"
    ))
    .bind(title)
    .bind(&req.body)
    .bind(&req.category)
    .bind(&req.category_note)
    .bind(&req.tracking_number)
    .bind(&req.order_number)
    .bind(&identity.email)
    .bind(&req.reporter_name)
    .fetch_one(&mut *tx)
    .await?;

    sqlx::query(
        "INSERT INTO support_ticket_events (ticket_id, kind, actor_email, actor_name)
         VALUES ($1, 'filed', $2, $3)",
    )
    .bind(ticket.id)
    .bind(&identity.email)
    .bind(&req.reporter_name)
    .execute(&mut *tx)
    .await?;
    tx.commit().await?;

    Ok(Json(ticket))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListQuery {
    pub status: Option<String>,
    pub category: Option<String>,
    pub search: Option<String>,
    pub page: Option<i64>,
    pub page_size: Option<i64>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListResponse {
    pub tickets: Vec<SupportTicket>,
    pub total: i64,
}

pub async fn list(
    State(pool): State<PgPool>,
    Query(q): Query<ListQuery>,
) -> Result<Json<ListResponse>, AppError> {
    let status = q.status.unwrap_or_else(|| "open".to_string());
    let status_filter = if status == "all" { None } else { Some(status) };
    let search = q.search.filter(|s| !s.trim().is_empty()).map(|s| format!("%{}%", s.trim()));
    let page_size = q.page_size.unwrap_or(50).clamp(1, 200);
    let offset = q.page.unwrap_or(0).max(0) * page_size;

    let where_sql = "WHERE ($1::text IS NULL OR status = $1)
                       AND ($2::text IS NULL OR category = $2)
                       AND ($3::text IS NULL OR title ILIKE $3 OR body ILIKE $3)";

    let tickets: Vec<SupportTicket> = sqlx::query_as(&format!(
        "SELECT {TICKET_COLS} FROM support_tickets {where_sql}
         ORDER BY created_at DESC LIMIT $4 OFFSET $5"
    ))
    .bind(&status_filter).bind(&q.category).bind(&search)
    .bind(page_size).bind(offset)
    .fetch_all(&pool).await?;

    let total: (i64,) = sqlx::query_as(&format!(
        "SELECT count(*) FROM support_tickets {where_sql}"
    ))
    .bind(&status_filter).bind(&q.category).bind(&search)
    .fetch_one(&pool).await?;

    Ok(Json(ListResponse { tickets, total: total.0 }))
}

#[derive(Debug, Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
pub struct Summary {
    pub open: i64,
    pub closed: i64,
}

pub async fn summary(State(pool): State<PgPool>) -> Result<Json<Summary>, AppError> {
    let s: Summary = sqlx::query_as(
        "SELECT count(*) FILTER (WHERE status = 'open')   AS open,
                count(*) FILTER (WHERE status = 'closed') AS closed
         FROM support_tickets",
    ).fetch_one(&pool).await?;
    Ok(Json(s))
}
```

In `backend/src/api/mod.rs`: add `mod support;` to the module list (alphabetically, after `mod stations;`) and register the routes immediately before the `// Returns mode` comment:

```rust
        // Help & Support Center
        .route("/support/tickets", get(support::list).post(support::create))
        .route("/support/tickets/summary", get(support::summary))
```

`/support/tickets/summary` must be registered **after** `/support/tickets` but **before** any `/support/tickets/{id}` route added in Task 4, so the literal path is not shadowed by the parameterised one.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets`
Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
cd backend
git add src/api/support.rs src/api/mod.rs tests/support_tickets.rs
git commit -m "feat(support): create, list and summarise tickets"
```

---

## Task 4: Ticket detail and owner-gated edit

**Files:**
- Modify: `backend/src/api/support.rs`, `backend/src/api/mod.rs`
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: Task 3's `SupportTicket`, `TICKET_COLS`.
- Produces: `support::get_one`, `support::update`, `pub async fn load_ticket(pool: &PgPool, id: i32) -> Result<SupportTicket, AppError>`, and the response struct `TicketDetail { ticket, comments, events, attachments }` plus row structs `SupportComment`, `SupportEvent`, `SupportAttachment`. Tasks 5–7 fill the comments/events/attachments vectors.

- [ ] **Step 1: Write the failing test**

Append to `backend/tests/support_tickets.rs`:

```rust
#[tokio::test]
async fn only_the_author_may_edit_a_ticket() {
    let (base, pool) = spawn_app().await;
    let client = reqwest::Client::new();
    let (h, v) = access_header("author@example.com");
    let created: serde_json::Value = client.post(format!("{base}/support/tickets"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "Original title", "category": "question" }))
        .send().await.unwrap().json().await.unwrap();
    let id = created["id"].as_i64().unwrap() as i32;

    let (h, v) = access_header("someone-else@example.com");
    let denied = client.patch(format!("{base}/support/tickets/{id}"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "Hijacked" }))
        .send().await.unwrap();
    assert_eq!(denied.status(), 403);

    let (h, v) = access_header("author@example.com");
    let edited: serde_json::Value = client.patch(format!("{base}/support/tickets/{id}"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "Edited title" }))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(edited["title"], "Edited title");
    assert!(!edited["editedAt"].is_null(), "editing stamps editedAt");

    let detail: serde_json::Value = client.get(format!("{base}/support/tickets/{id}"))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(detail["ticket"]["title"], "Edited title");
    let kinds: Vec<&str> = detail["events"].as_array().unwrap().iter()
        .map(|e| e["kind"].as_str().unwrap()).collect();
    assert_eq!(kinds, vec!["filed", "edited"]);
    assert!(detail["comments"].as_array().unwrap().is_empty());
    assert!(detail["attachments"].as_array().unwrap().is_empty());

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id)
        .execute(&pool).await.unwrap();
}

#[tokio::test]
async fn detail_404s_for_an_unknown_ticket() {
    let (base, _pool) = spawn_app().await;
    let res = reqwest::get(format!("{base}/support/tickets/99999999")).await.unwrap();
    assert_eq!(res.status(), 404);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && cargo test --test support_tickets`
Expected: FAIL — 404/405 on the detail and patch routes.

- [ ] **Step 3: Implement**

Append to `backend/src/api/support.rs`:

```rust
#[derive(Debug, Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
pub struct SupportComment {
    pub id: i32,
    pub ticket_id: i32,
    pub body: String,
    pub author_email: String,
    pub author_name: Option<String>,
    pub created_at: chrono::DateTime<chrono::Utc>,
    pub edited_at: Option<chrono::DateTime<chrono::Utc>>,
}

#[derive(Debug, Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
pub struct SupportEvent {
    pub id: i32,
    pub kind: String,
    pub actor_email: Option<String>,
    pub actor_name: Option<String>,
    pub detail: Option<serde_json::Value>,
    pub at: chrono::DateTime<chrono::Utc>,
}

#[derive(Debug, Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
pub struct SupportAttachment {
    pub id: i32,
    pub ticket_id: i32,
    pub comment_id: Option<i32>,
    pub path: String,
    pub file_name: String,
    pub content_type: String,
    pub size_bytes: i64,
    pub uploaded_at: chrono::DateTime<chrono::Utc>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TicketDetail {
    pub ticket: SupportTicket,
    pub comments: Vec<SupportComment>,
    pub events: Vec<SupportEvent>,
    pub attachments: Vec<SupportAttachment>,
}

pub async fn load_ticket(pool: &PgPool, id: i32) -> Result<SupportTicket, AppError> {
    sqlx::query_as(&format!("SELECT {TICKET_COLS} FROM support_tickets WHERE id = $1"))
        .bind(id)
        .fetch_optional(pool)
        .await?
        .ok_or(AppError::NotFound)
}

pub async fn get_one(
    State(pool): State<PgPool>,
    Path(id): Path<i32>,
) -> Result<Json<TicketDetail>, AppError> {
    let ticket = load_ticket(&pool, id).await?;
    let comments = sqlx::query_as(
        "SELECT id, ticket_id, body, author_email, author_name, created_at, edited_at
         FROM support_ticket_comments WHERE ticket_id = $1 ORDER BY created_at",
    ).bind(id).fetch_all(&pool).await?;
    let events = sqlx::query_as(
        "SELECT id, kind, actor_email, actor_name, detail, at
         FROM support_ticket_events WHERE ticket_id = $1 ORDER BY at, id",
    ).bind(id).fetch_all(&pool).await?;
    let attachments = sqlx::query_as(
        "SELECT id, ticket_id, comment_id, path, file_name, content_type, size_bytes, uploaded_at
         FROM support_ticket_attachments WHERE ticket_id = $1 ORDER BY uploaded_at",
    ).bind(id).fetch_all(&pool).await?;
    Ok(Json(TicketDetail { ticket, comments, events, attachments }))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateTicketRequest {
    pub title: Option<String>,
    pub body: Option<String>,
    pub category: Option<String>,
    pub category_note: Option<String>,
    pub tracking_number: Option<String>,
    pub order_number: Option<String>,
}

pub async fn update(
    State(pool): State<PgPool>,
    Path(id): Path<i32>,
    identity: AccessIdentity,
    Json(req): Json<UpdateTicketRequest>,
) -> Result<Json<SupportTicket>, AppError> {
    let existing = load_ticket(&pool, id).await?;
    if existing.reporter_email != identity.email {
        return Err(AppError::Forbidden("only the author may edit this ticket".into()));
    }
    if let Some(t) = &req.title {
        let t = t.trim();
        if t.is_empty() || t.chars().count() > 120 {
            return Err(AppError::BadRequest("title must be 1–120 characters".into()));
        }
    }

    let mut changed: Vec<&str> = Vec::new();
    for (name, present) in [
        ("title", req.title.is_some()),
        ("body", req.body.is_some()),
        ("category", req.category.is_some()),
        ("categoryNote", req.category_note.is_some()),
        ("trackingNumber", req.tracking_number.is_some()),
        ("orderNumber", req.order_number.is_some()),
    ] {
        if present { changed.push(name); }
    }
    if changed.is_empty() {
        return Ok(Json(existing));
    }

    let mut tx = pool.begin().await?;
    let ticket: SupportTicket = sqlx::query_as(&format!(
        "UPDATE support_tickets SET
            title           = COALESCE($2, title),
            body            = COALESCE($3, body),
            category        = COALESCE($4, category),
            category_note   = COALESCE($5, category_note),
            tracking_number = COALESCE($6, tracking_number),
            order_number    = COALESCE($7, order_number),
            edited_at       = now(),
            updated_at      = now()
         WHERE id = $1 RETURNING {TICKET_COLS}"
    ))
    .bind(id)
    .bind(req.title.as_deref().map(str::trim))
    .bind(&req.body).bind(&req.category).bind(&req.category_note)
    .bind(&req.tracking_number).bind(&req.order_number)
    .fetch_one(&mut *tx).await?;

    sqlx::query(
        "INSERT INTO support_ticket_events (ticket_id, kind, actor_email, detail)
         VALUES ($1, 'edited', $2, $3)",
    )
    .bind(id).bind(&identity.email)
    .bind(serde_json::json!({ "fields": changed }))
    .execute(&mut *tx).await?;
    tx.commit().await?;

    Ok(Json(ticket))
}
```

Register in `backend/src/api/mod.rs`, after the summary route:

```rust
        .route("/support/tickets/{id}", get(support::get_one).patch(support::update))
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets`
Expected: 8 passed.

- [ ] **Step 5: Commit**

```bash
cd backend
git add src/api/support.rs src/api/mod.rs tests/support_tickets.rs
git commit -m "feat(support): ticket detail and owner-gated edit"
```

---

## Task 5: Close and reopen

**Files:**
- Modify: `backend/src/api/support.rs`, `backend/src/api/mod.rs`
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: `load_ticket`, `SupportTicket`, `TICKET_COLS`.
- Produces: `support::close`, `support::reopen`, `CloseRequest { reason: String }`.

- [ ] **Step 1: Write the failing test**

Append to `backend/tests/support_tickets.rs`:

```rust
#[tokio::test]
async fn anyone_can_close_and_reopen_and_both_land_in_the_thread() {
    let (base, pool) = spawn_app().await;
    let client = reqwest::Client::new();
    let (h, v) = access_header("author@example.com");
    let created: serde_json::Value = client.post(format!("{base}/support/tickets"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "Closable", "category": "bug" }))
        .send().await.unwrap().json().await.unwrap();
    let id = created["id"].as_i64().unwrap() as i32;

    let (h, v) = access_header("triager@example.com");
    let closed: serde_json::Value = client.post(format!("{base}/support/tickets/{id}/close"))
        .header(h, v)
        .json(&serde_json::json!({ "reason": "completed" }))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(closed["status"], "closed");
    assert_eq!(closed["closeReason"], "completed");
    assert_eq!(closed["closedBy"], "triager@example.com");

    let (h, v) = access_header("triager@example.com");
    let bad = client.post(format!("{base}/support/tickets/{id}/close"))
        .header(h, v)
        .json(&serde_json::json!({ "reason": "because" }))
        .send().await.unwrap();
    assert_eq!(bad.status(), 400, "close reason must be one of the three");

    let (h, v) = access_header("author@example.com");
    let reopened: serde_json::Value = client.post(format!("{base}/support/tickets/{id}/reopen"))
        .header(h, v).send().await.unwrap().json().await.unwrap();
    assert_eq!(reopened["status"], "open");
    assert!(reopened["closeReason"].is_null());
    assert!(reopened["closedAt"].is_null());

    let detail: serde_json::Value = client.get(format!("{base}/support/tickets/{id}"))
        .send().await.unwrap().json().await.unwrap();
    let kinds: Vec<&str> = detail["events"].as_array().unwrap().iter()
        .map(|e| e["kind"].as_str().unwrap()).collect();
    assert_eq!(kinds, vec!["filed", "closed", "reopened"]);
    assert_eq!(detail["events"][1]["detail"]["reason"], "completed");

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id)
        .execute(&pool).await.unwrap();
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd backend && cargo test --test support_tickets close_and_reopen`
Expected: FAIL — 404 on `/close`.

- [ ] **Step 3: Implement**

Append to `backend/src/api/support.rs`:

```rust
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloseRequest {
    pub reason: String,
}

pub async fn close(
    State(pool): State<PgPool>,
    Path(id): Path<i32>,
    identity: AccessIdentity,
    Json(req): Json<CloseRequest>,
) -> Result<Json<SupportTicket>, AppError> {
    if !matches!(req.reason.as_str(), "completed" | "not_planned" | "duplicate") {
        return Err(AppError::BadRequest(
            "reason must be completed, not_planned or duplicate".into(),
        ));
    }
    load_ticket(&pool, id).await?;

    let mut tx = pool.begin().await?;
    let ticket: SupportTicket = sqlx::query_as(&format!(
        "UPDATE support_tickets
         SET status = 'closed', close_reason = $2, closed_at = now(),
             closed_by = $3, updated_at = now()
         WHERE id = $1 RETURNING {TICKET_COLS}"
    ))
    .bind(id).bind(&req.reason).bind(&identity.email)
    .fetch_one(&mut *tx).await?;

    sqlx::query(
        "INSERT INTO support_ticket_events (ticket_id, kind, actor_email, detail)
         VALUES ($1, 'closed', $2, $3)",
    )
    .bind(id).bind(&identity.email)
    .bind(serde_json::json!({ "reason": req.reason }))
    .execute(&mut *tx).await?;
    tx.commit().await?;

    Ok(Json(ticket))
}

pub async fn reopen(
    State(pool): State<PgPool>,
    Path(id): Path<i32>,
    identity: AccessIdentity,
) -> Result<Json<SupportTicket>, AppError> {
    load_ticket(&pool, id).await?;

    let mut tx = pool.begin().await?;
    let ticket: SupportTicket = sqlx::query_as(&format!(
        "UPDATE support_tickets
         SET status = 'open', close_reason = NULL, closed_at = NULL,
             closed_by = NULL, updated_at = now()
         WHERE id = $1 RETURNING {TICKET_COLS}"
    ))
    .bind(id).fetch_one(&mut *tx).await?;

    sqlx::query(
        "INSERT INTO support_ticket_events (ticket_id, kind, actor_email)
         VALUES ($1, 'reopened', $2)",
    )
    .bind(id).bind(&identity.email).execute(&mut *tx).await?;
    tx.commit().await?;

    Ok(Json(ticket))
}
```

Register in `backend/src/api/mod.rs`:

```rust
        .route("/support/tickets/{id}/close", post(support::close))
        .route("/support/tickets/{id}/reopen", post(support::reopen))
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets`
Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
cd backend
git add src/api/support.rs src/api/mod.rs tests/support_tickets.rs
git commit -m "feat(support): close and reopen with thread events"
```

---

## Task 6: Comments

**Files:**
- Modify: `backend/src/api/support.rs`, `backend/src/api/mod.rs`
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: `SupportComment` (Task 4), `load_ticket`.
- Produces: `support::create_comment`, `support::update_comment`, `CreateCommentRequest { body, author_name }`, `UpdateCommentRequest { body }`.

- [ ] **Step 1: Write the failing test**

Append to `backend/tests/support_tickets.rs`:

```rust
#[tokio::test]
async fn anyone_comments_but_only_the_author_edits_their_comment() {
    let (base, pool) = spawn_app().await;
    let client = reqwest::Client::new();
    let (h, v) = access_header("author@example.com");
    let created: serde_json::Value = client.post(format!("{base}/support/tickets"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "Commentable", "category": "question" }))
        .send().await.unwrap().json().await.unwrap();
    let id = created["id"].as_i64().unwrap() as i32;

    let (h, v) = access_header("helper@example.com");
    let comment: serde_json::Value = client.post(format!("{base}/support/tickets/{id}/comments"))
        .header(h, v)
        .json(&serde_json::json!({ "body": "Try restarting the station", "authorName": "Helper" }))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(comment["authorEmail"], "helper@example.com");
    assert_eq!(comment["authorName"], "Helper");
    let cid = comment["id"].as_i64().unwrap() as i32;

    let (h, v) = access_header("author@example.com");
    let denied = client.patch(format!("{base}/support/comments/{cid}"))
        .header(h, v)
        .json(&serde_json::json!({ "body": "not mine" }))
        .send().await.unwrap();
    assert_eq!(denied.status(), 403);

    let (h, v) = access_header("helper@example.com");
    let edited: serde_json::Value = client.patch(format!("{base}/support/comments/{cid}"))
        .header(h, v)
        .json(&serde_json::json!({ "body": "Try restarting the station twice" }))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(edited["body"], "Try restarting the station twice");
    assert!(!edited["editedAt"].is_null());

    let (h, v) = access_header("helper@example.com");
    let empty = client.post(format!("{base}/support/tickets/{id}/comments"))
        .header(h, v)
        .json(&serde_json::json!({ "body": "   " }))
        .send().await.unwrap();
    assert_eq!(empty.status(), 400);

    let detail: serde_json::Value = client.get(format!("{base}/support/tickets/{id}"))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(detail["comments"].as_array().unwrap().len(), 1);
    let kinds: Vec<&str> = detail["events"].as_array().unwrap().iter()
        .map(|e| e["kind"].as_str().unwrap()).collect();
    assert_eq!(kinds, vec!["filed"], "comments are not events");

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id)
        .execute(&pool).await.unwrap();
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd backend && cargo test --test support_tickets comments`
Expected: FAIL — 404 on the comments route.

- [ ] **Step 3: Implement**

Append to `backend/src/api/support.rs`:

```rust
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateCommentRequest {
    pub body: String,
    /// Display hint only. The email always comes from the JWT.
    pub author_name: Option<String>,
}

const COMMENT_COLS: &str =
    "id, ticket_id, body, author_email, author_name, created_at, edited_at";

pub async fn create_comment(
    State(pool): State<PgPool>,
    Path(id): Path<i32>,
    identity: AccessIdentity,
    Json(req): Json<CreateCommentRequest>,
) -> Result<Json<SupportComment>, AppError> {
    let body = req.body.trim();
    if body.is_empty() {
        return Err(AppError::BadRequest("comment body must not be empty".into()));
    }
    load_ticket(&pool, id).await?;

    let comment: SupportComment = sqlx::query_as(&format!(
        "INSERT INTO support_ticket_comments (ticket_id, body, author_email, author_name)
         VALUES ($1, $2, $3, $4) RETURNING {COMMENT_COLS}"
    ))
    .bind(id).bind(body).bind(&identity.email).bind(&req.author_name)
    .fetch_one(&pool).await?;

    sqlx::query("UPDATE support_tickets SET updated_at = now() WHERE id = $1")
        .bind(id).execute(&pool).await?;

    Ok(Json(comment))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCommentRequest {
    pub body: String,
}

pub async fn update_comment(
    State(pool): State<PgPool>,
    Path(comment_id): Path<i32>,
    identity: AccessIdentity,
    Json(req): Json<UpdateCommentRequest>,
) -> Result<Json<SupportComment>, AppError> {
    let body = req.body.trim();
    if body.is_empty() {
        return Err(AppError::BadRequest("comment body must not be empty".into()));
    }
    let owner: Option<(String,)> =
        sqlx::query_as("SELECT author_email FROM support_ticket_comments WHERE id = $1")
            .bind(comment_id).fetch_optional(&pool).await?;
    let owner = owner.ok_or(AppError::NotFound)?;
    if owner.0 != identity.email {
        return Err(AppError::Forbidden("only the author may edit this comment".into()));
    }

    let comment: SupportComment = sqlx::query_as(&format!(
        "UPDATE support_ticket_comments SET body = $2, edited_at = now()
         WHERE id = $1 RETURNING {COMMENT_COLS}"
    ))
    .bind(comment_id).bind(body).fetch_one(&pool).await?;
    Ok(Json(comment))
}
```

Register in `backend/src/api/mod.rs`:

```rust
        .route("/support/tickets/{id}/comments", post(support::create_comment))
        .route("/support/comments/{id}", patch(support::update_comment))
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets`
Expected: 10 passed.

- [ ] **Step 5: Commit**

```bash
cd backend
git add src/api/support.rs src/api/mod.rs tests/support_tickets.rs
git commit -m "feat(support): ticket comments with author-gated edit"
```

---

## Task 7: Attachments (files and images)

**Files:**
- Modify: `backend/src/api/support.rs`, `backend/src/api/mod.rs`
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: `SupportAttachment` (Task 4), `AppState::bucket`.
- Produces: `support::upload_attachment`, `support::download_attachment`. Object key format `support/{ticket_id}/{attachment_id}.{ext}` in the MinIO bucket named `support`.

- [ ] **Step 1: Create the bucket in the dev MinIO**

Run: `docker compose -f docker/compose.yml exec minio mc mb --ignore-existing local/support`
If `mc` is unavailable in the container, create the `support` bucket through the MinIO console at `http://localhost:9001`. Note it in the Task 19 deployment checklist either way.

- [ ] **Step 2: Write the failing test**

Append to `backend/tests/support_tickets.rs`:

```rust
#[tokio::test]
async fn attachments_accept_both_an_image_and_a_spreadsheet() {
    let (base, pool) = spawn_app().await;
    let client = reqwest::Client::new();
    let (h, v) = access_header("author@example.com");
    let created: serde_json::Value = client.post(format!("{base}/support/tickets"))
        .header(h, v)
        .json(&serde_json::json!({ "title": "With evidence", "category": "data_problem" }))
        .send().await.unwrap().json().await.unwrap();
    let id = created["id"].as_i64().unwrap() as i32;

    let png = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    let (h, v) = access_header("author@example.com");
    let img: serde_json::Value = client.post(format!("{base}/support/tickets/{id}/attachments"))
        .header(h, v)
        .multipart(reqwest::multipart::Form::new().part(
            "file",
            reqwest::multipart::Part::bytes(png).file_name("shot.png").mime_str("image/png").unwrap(),
        ))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(img["fileName"], "shot.png");
    assert_eq!(img["contentType"], "image/png");
    assert!(img["path"].as_str().unwrap().starts_with(&format!("support/{id}/")));

    let (h, v) = access_header("author@example.com");
    let sheet: serde_json::Value = client.post(format!("{base}/support/tickets/{id}/attachments"))
        .header(h, v)
        .multipart(reqwest::multipart::Form::new().part(
            "file",
            reqwest::multipart::Part::bytes(b"PK\x03\x04junk".to_vec())
                .file_name("orders.xlsx")
                .mime_str("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
                .unwrap(),
        ))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(sheet["fileName"], "orders.xlsx", "non-image files are accepted");
    let aid = sheet["id"].as_i64().unwrap() as i32;

    let media = client.get(format!("{base}/support/attachments/{aid}/media"))
        .send().await.unwrap();
    assert_eq!(media.status(), 200);

    let detail: serde_json::Value = client.get(format!("{base}/support/tickets/{id}"))
        .send().await.unwrap().json().await.unwrap();
    assert_eq!(detail["attachments"].as_array().unwrap().len(), 2);

    sqlx::query("DELETE FROM support_tickets WHERE id = $1").bind(id)
        .execute(&pool).await.unwrap();
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd backend && cargo test --test support_tickets attachments`
Expected: FAIL — 404 on the attachments route.

- [ ] **Step 4: Implement**

Append to `backend/src/api/support.rs`. Change the two imports at the top of the file to include multipart and the app state:

```rust
use axum::extract::Multipart;
use axum::response::{IntoResponse, Response};
use crate::state::AppState;
```

```rust
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AttachmentQuery {
    pub comment_id: Option<i32>,
}

/// Extension from the original file name; falls back to the content type's
/// subtype. Only used to build the object key, never to gate the upload —
/// #96 widened attachments to files as well as images.
fn ext_for(file_name: &str, content_type: &str) -> String {
    file_name
        .rsplit_once('.')
        .map(|(_, e)| e.to_ascii_lowercase())
        .filter(|e| !e.is_empty() && e.chars().all(|c| c.is_ascii_alphanumeric()) && e.len() <= 8)
        .unwrap_or_else(|| {
            content_type.rsplit('/').next().unwrap_or("bin")
                .chars().filter(|c| c.is_ascii_alphanumeric()).take(8).collect()
        })
}

pub async fn upload_attachment(
    State(state): State<AppState>,
    Path(id): Path<i32>,
    Query(q): Query<AttachmentQuery>,
    _identity: AccessIdentity,
    mut multipart: Multipart,
) -> Result<Json<SupportAttachment>, AppError> {
    // 404 before reading the body if the ticket is gone.
    load_ticket(&state.pool, id).await?;

    let field = multipart.next_field().await
        .map_err(|e| AppError::BadRequest(format!("invalid multipart: {e}")))?
        .ok_or_else(|| AppError::BadRequest("missing 'file' field".to_string()))?;
    let content_type = field.content_type().unwrap_or("application/octet-stream").to_string();
    let file_name = field.file_name().unwrap_or("attachment").to_string();
    let data = field.bytes().await
        .map_err(|e| AppError::BadRequest(format!("failed to read file: {e}")))?;
    let ext = ext_for(&file_name, &content_type);

    // Reserve the row first so the object key has a stable id (issues::upload_photo pattern).
    let reserved: (i32,) = sqlx::query_as(
        "INSERT INTO support_ticket_attachments
           (ticket_id, comment_id, path, file_name, content_type, size_bytes)
         VALUES ($1, $2, '', $3, $4, $5) RETURNING id",
    )
    .bind(id).bind(q.comment_id).bind(&file_name).bind(&content_type)
    .bind(data.len() as i64)
    .fetch_one(&state.pool).await?;

    let object_key = format!("support/{id}/{}.{ext}", reserved.0);
    let bucket = state.bucket("support")?;
    let cleanup = |pool: sqlx::PgPool, aid: i32| {
        tokio::spawn(async move {
            sqlx::query("DELETE FROM support_ticket_attachments WHERE id = $1")
                .bind(aid).execute(&pool).await.ok();
        });
    };
    let resp = bucket
        .put_object_with_content_type(&object_key, &data, &content_type)
        .await
        .map_err(|e| {
            cleanup(state.pool.clone(), reserved.0);
            AppError::Internal(format!("MinIO upload failed: {e}"))
        })?;
    if resp.status_code() != 200 {
        cleanup(state.pool.clone(), reserved.0);
        return Err(AppError::Internal(format!("MinIO returned {}", resp.status_code())));
    }

    let saved: SupportAttachment = sqlx::query_as(
        "UPDATE support_ticket_attachments SET path = $2 WHERE id = $1
         RETURNING id, ticket_id, comment_id, path, file_name, content_type, size_bytes, uploaded_at",
    ).bind(reserved.0).bind(&object_key).fetch_one(&state.pool).await?;
    Ok(Json(saved))
}

pub async fn download_attachment(
    State(state): State<AppState>,
    Path(attachment_id): Path<i32>,
) -> Result<Response, AppError> {
    let row: Option<(String, String, String)> = sqlx::query_as(
        "SELECT path, file_name, content_type FROM support_ticket_attachments WHERE id = $1",
    ).bind(attachment_id).fetch_optional(&state.pool).await?;
    let (path, file_name, content_type) = row.ok_or(AppError::NotFound)?;

    let bucket = state.bucket("support")?;
    let obj = bucket.get_object(&path).await
        .map_err(|e| AppError::Internal(format!("MinIO fetch failed: {e}")))?;
    if obj.status_code() != 200 {
        return Err(AppError::NotFound);
    }
    Ok((
        [
            (axum::http::header::CONTENT_TYPE, content_type),
            (
                axum::http::header::CONTENT_DISPOSITION,
                format!("inline; filename=\"{file_name}\""),
            ),
        ],
        obj.to_vec(),
    ).into_response())
}
```

Register in `backend/src/api/mod.rs`:

```rust
        .route(
            "/support/tickets/{id}/attachments",
            post(support::upload_attachment).layer(DefaultBodyLimit::max(16 * 1024 * 1024)),
        )
        .route("/support/attachments/{id}/media", get(support::download_attachment))
```

Handlers in this task take `State<AppState>` while Tasks 3–6 take `State<PgPool>`; both work in the same router because of the `FromRef` impls in `src/state.rs`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets`
Expected: 11 passed.

- [ ] **Step 6: Commit**

```bash
cd backend
git add src/api/support.rs src/api/mod.rs tests/support_tickets.rs
git commit -m "feat(support): file and image attachments via MinIO"
```

---

## Task 8: `limit` on the existing parcel suggest endpoint

**Files:**
- Modify: `backend/src/api/packing.rs` (the `SuggestQuery` struct and `suggest` handler)
- Test: `backend/tests/support_tickets.rs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `GET /packing-lists/suggest?q=…&limit=5`, clamped to 1..=25, default 8. The frontend's `useParcelSuggest` (Task 9) calls it with `limit=5`.

There is **no new suggest endpoint**. This endpoint already does contiguous-substring `ILIKE` matching against tracking **and** order number and returns platform, packing status and `updated_at` — exactly what a mention row renders.

- [ ] **Step 1: Write the failing test**

Append to `backend/tests/support_tickets.rs`:

```rust
#[tokio::test]
async fn suggest_honours_an_explicit_limit() {
    let (base, pool) = spawn_app().await;
    for n in 0..7 {
        sqlx::query(
            "INSERT INTO packing_lists (tracking_number, order_number)
             VALUES ($1, $2) ON CONFLICT (tracking_number) DO NOTHING",
        )
        .bind(format!("THSUGGEST{n:03}"))
        .bind(format!("ORDSUGGEST{n:03}"))
        .execute(&pool).await.unwrap();
    }

    let five: serde_json::Value = reqwest::get(
        format!("{base}/packing-lists/suggest?q=THSUGGEST&limit=5")
    ).await.unwrap().json().await.unwrap();
    assert_eq!(five.as_array().unwrap().len(), 5);

    // Order numbers match too — the mention popup searches both columns.
    let by_order: serde_json::Value = reqwest::get(
        format!("{base}/packing-lists/suggest?q=SUGGEST00&limit=5")
    ).await.unwrap().json().await.unwrap();
    assert!(!by_order.as_array().unwrap().is_empty());

    sqlx::query("DELETE FROM packing_lists WHERE tracking_number LIKE 'THSUGGEST%'")
        .execute(&pool).await.unwrap();
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd backend && cargo test --test support_tickets suggest`
Expected: FAIL — 7 rows returned (the hard-coded `LIMIT 8` ignores the param).

- [ ] **Step 3: Implement**

In `backend/src/api/packing.rs`, add the field to `SuggestQuery`:

```rust
    pub limit: Option<i64>,
```

and change the query in `suggest` to bind it:

```rust
    let limit = q.limit.unwrap_or(8).clamp(1, 25);
    let rows = sqlx::query_as::<_, PackingSuggestion>(
        "SELECT tracking_number, order_number, platform, packing_status, updated_at
         FROM packing_lists
         WHERE tracking_number ILIKE $1 OR order_number ILIKE $1
         ORDER BY updated_at DESC NULLS LAST, created_at DESC
         LIMIT $2",
    )
    .bind(&like)
    .bind(limit)
    .fetch_all(&pool)
    .await?;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && cargo test --test support_tickets && cargo test`
Expected: all support tests pass and no existing test regresses (callers that omit `limit` still get 8).

- [ ] **Step 5: Commit**

```bash
cd backend
git add src/api/packing.rs tests/support_tickets.rs
git commit -m "feat(packing): optional limit on the suggest endpoint"
```

---

## Task 9: Frontend types and data hooks

**Files:**
- Modify: `frontend/app/types.ts`
- Create: `frontend/app/hooks/useAccessIdentity.ts`, `frontend/app/hooks/useSupportTickets.ts`, `frontend/app/hooks/useSupportTicket.ts`, `frontend/app/hooks/useParcelSuggest.ts`
- Test: `frontend/app/hooks/useAccessIdentity.test.ts`

**Interfaces:**
- Consumes: the API surface from Tasks 3–8.
- Produces:
  - `SupportTicket`, `SupportComment`, `SupportEvent`, `SupportAttachment`, `SupportTicketDetail`, `SupportSummary`, `AccessIdentity`, `ParcelSuggestion` in `types.ts`.
  - `useAccessIdentity(): { identity: AccessIdentity | null; loading: boolean; signedIn: boolean }`.
  - `useSupportTickets(): { tickets, total, summary, loading, status, setStatus, category, setCategory, searchInput, setSearchInput, page, setPage, refresh }`.
  - `useSupportTicket(id): { detail, loading, refresh, addComment, editComment, editTicket, close, reopen, uploadAttachment }`.
  - `useParcelSuggest(): { term, setTerm, results, loading }`.

- [ ] **Step 1: Add the types**

Append to `frontend/app/types.ts`:

```ts
// ---------------------------------------------------------------- support
export interface AccessIdentity {
  name?: string;   // undocumented in Cloudflare's payload — never rely on it
  email: string;
}

export type SupportStatus = "open" | "closed";
export type SupportCloseReason = "completed" | "not_planned" | "duplicate";
export type SupportCategory =
  | "bug" | "feature_request" | "question" | "data_problem" | "other";

export interface SupportTicket {
  id: number;
  title: string;
  body: string | null;
  category: SupportCategory;
  categoryNote: string | null;
  status: SupportStatus;
  closeReason: SupportCloseReason | null;
  trackingNumber: string | null;
  orderNumber: string | null;
  reporterEmail: string;
  reporterName: string | null;
  createdAt: string;
  updatedAt: string;
  editedAt: string | null;
  closedAt: string | null;
  closedBy: string | null;
}

export interface SupportComment {
  id: number;
  ticketId: number;
  body: string;
  authorEmail: string;
  authorName: string | null;
  createdAt: string;
  editedAt: string | null;
}

export interface SupportEvent {
  id: number;
  kind: "filed" | "edited" | "closed" | "reopened";
  actorEmail: string | null;
  actorName: string | null;
  detail: { reason?: SupportCloseReason; fields?: string[] } | null;
  at: string;
}

export interface SupportAttachment {
  id: number;
  ticketId: number;
  commentId: number | null;
  path: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
}

export interface SupportTicketDetail {
  ticket: SupportTicket;
  comments: SupportComment[];
  events: SupportEvent[];
  attachments: SupportAttachment[];
}

export interface SupportSummary { open: number; closed: number }

export interface ParcelSuggestion {
  trackingNumber: string;
  orderNumber: string | null;
  platform: string | null;
  packingStatus: string | null;
  updatedAt: string | null;
}
```

- [ ] **Step 2: Write the failing identity test**

Create `frontend/app/hooks/useAccessIdentity.test.ts`:

```ts
// @vitest-environment jsdom
import { act } from "react";
import { createRoot, Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useAccessIdentity } from "./useAccessIdentity";

(globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;

let container: HTMLDivElement;
let root: Root;
let seen: ReturnType<typeof useAccessIdentity>;

function Probe() {
  seen = useAccessIdentity();
  return null;
}

beforeEach(() => {
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
});
afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.restoreAllMocks();
});

describe("useAccessIdentity", () => {
  it("reads name and email when behind Access", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(
      JSON.stringify({ name: "Pat Warehouse", email: "pat@example.com" }),
      { status: 200, headers: { "content-type": "application/json" } },
    )));
    await act(async () => { root.render(<Probe />); });
    expect(seen.signedIn).toBe(true);
    expect(seen.identity?.name).toBe("Pat Warehouse");
  });

  it("falls back cleanly when the endpoint 404s with HTML (off-tunnel)", async () => {
    // fetch RESOLVES here — a bare try/catch would never fire. This is the
    // measured off-tunnel behaviour, see spec §4.4.
    vi.stubGlobal("fetch", vi.fn(async () => new Response(
      "<!doctype html><title>404</title>",
      { status: 404, headers: { "content-type": "text/html" } },
    )));
    await act(async () => { root.render(<Probe />); });
    expect(seen.signedIn).toBe(false);
    expect(seen.identity).toBeNull();
  });

  it("survives a 200 with an unparseable body", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("not json", { status: 200 })));
    await act(async () => { root.render(<Probe />); });
    expect(seen.signedIn).toBe(false);
  });
});
```

Rename the file to `.tsx` if the repo's Vitest config rejects JSX in `.ts` — check a neighbouring test such as `useDialog.test.tsx`.

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/hooks/useAccessIdentity.test`
Expected: FAIL — module not found.

- [ ] **Step 4: Implement `useAccessIdentity`**

Create `frontend/app/hooks/useAccessIdentity.ts`:

```ts
"use client";

import { useEffect, useState } from "react";
import { AccessIdentity } from "../types";

// Cloudflare Access identity, DISPLAY ONLY. Never send this to the backend as
// identity — the backend reads the Cf-Access-Jwt-Assertion header. Spec §4.4.
export function useAccessIdentity() {
  const [identity, setIdentity] = useState<AccessIdentity | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch("/cdn-cgi/access/get-identity", { credentials: "include" });
        // Off-tunnel this 404s with text/html: fetch RESOLVES, so branching on
        // res.ok is the only thing that catches it. Measured, not assumed.
        if (!res.ok) return;
        let data: AccessIdentity | null = null;
        try {
          data = (await res.json()) as AccessIdentity;
        } catch {
          return; // guarded separately: a 200 with a non-JSON body must not throw
        }
        if (!cancelled && data?.email) {
          setIdentity({ name: data.name, email: data.email });
        }
      } catch {
        // network error off-tunnel — fall through to the signed-out state
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  return { identity, loading, signedIn: identity !== null };
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/hooks/useAccessIdentity.test`
Expected: 3 passed.

- [ ] **Step 6: Implement the three data hooks**

Follow `app/hooks/useIssues.ts` for structure: `const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080";`, its `safeJson` helper, and its 300 ms debounce pattern for search (reset `page` inside the timeout callback, never in a `useEffect` — the repo's ESLint errors on set-state-in-effect).

`useSupportTickets.ts` fetches `GET /support/tickets?status&category&search&page&pageSize=50` plus `GET /support/tickets/summary`, and exposes the state listed under **Produces**.

`useSupportTicket.ts` fetches `GET /support/tickets/{id}` into `detail`, and exposes mutations that call the Task 4–7 routes and then `refresh()`:

```ts
addComment(body: string, authorName?: string): Promise<void>
editComment(commentId: number, body: string): Promise<void>
editTicket(patch: Partial<Pick<SupportTicket,
  "title" | "body" | "category" | "categoryNote" | "trackingNumber" | "orderNumber">>): Promise<void>
close(reason: SupportCloseReason): Promise<void>
reopen(): Promise<void>
uploadAttachment(file: File, commentId?: number): Promise<void>
```

`uploadAttachment` posts `FormData` with the field name `file` to `/support/tickets/{id}/attachments`, appending `?commentId=` when given.

Every mutation must surface failure — a 403 from a non-author edit has to reach the UI, so throw on `!res.ok` with the response text and let the caller render it. Do not swallow it.

`useParcelSuggest.ts` debounces `term` by 250 ms, skips terms shorter than 2 characters, and fetches `GET /packing-lists/suggest?q={term}&limit=5`.

- [ ] **Step 7: Typecheck and commit**

Run: `cd frontend && npm run lint && npx tsc --noEmit`
Expected: clean.

```bash
cd frontend
git add app/types.ts app/hooks/useAccessIdentity.ts app/hooks/useAccessIdentity.test.tsx \
        app/hooks/useSupportTickets.ts app/hooks/useSupportTicket.ts app/hooks/useParcelSuggest.ts
git commit -m "feat(support): types and data hooks"
```

---

## Task 10: Sidebar groups — drop System, add Help

**Files:**
- Modify: `frontend/app/components/Sidebar.tsx` (the `navGroups` const, currently lines 141–183)
- Modify: `frontend/app/components/Sidebar.rail.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: a `help` nav group containing `/support`; no `system` group. Task 11 mounts the user menu into the same file's footer.

- [ ] **Step 1: Update the test first**

In `frontend/app/components/Sidebar.rail.test.tsx`:

```ts
const GROUP_TITLES = ["Operation", "Analytics", "Invoices", "Help"];
```

Then, in the three places that assert on `/settings`, swap to the new surface:

```ts
    // Settings moved into the sidebar user menu; Support is the Help group's item.
    expect(links.some((a) => a.getAttribute("href") === "/settings")).toBe(false);
    expect(links.some((a) => a.getAttribute("href") === "/support")).toBe(true);
```

```ts
    expect(hrefs).toContain("/");
    expect(hrefs).toContain("/support");
```

and change the seeded localStorage key in the third test:

```ts
    localStorage.setItem("sidebar-groups", JSON.stringify({ help: true }));
    document.documentElement.setAttribute("data-sidebar-collapsed", "true");
    await renderSidebar();
    const hrefs = Array.from(container.querySelectorAll("a")).map((a) => a.getAttribute("href"));
    expect(hrefs).toContain("/support");
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/Sidebar.rail.test`
Expected: FAIL — no `/support` link, 4 group headers but one titled "System".

- [ ] **Step 3: Change the nav groups**

In `frontend/app/components/Sidebar.tsx`, add the headset icon beside the other icon components (headphones over a rounded face, matching the mockup):

```tsx
const HeadsetIcon = () => (
  <svg xmlns="http://www.w3.org/2000/svg" className="h-4 w-4 shrink-0" viewBox="0 0 20 20" fill="currentColor">
    <path d="M10 2a7 7 0 0 0-7 7v3a3 3 0 0 0 3 3h1V9H5V9a5 5 0 0 1 10 0v3h-2v6h1a3 3 0 0 0 3-3V9a7 7 0 0 0-7-7z" />
    <path d="M4 10h2v5H4a1 1 0 0 1-1-1v-3a1 1 0 0 1 1-1zm12 0h-2v5h2a1 1 0 0 0 1-1v-3a1 1 0 0 0-1-1z" />
  </svg>
);
```

Replace the whole `system` group with:

```tsx
  {
    key: "help",
    label: "Help",
    Icon: HeadsetIcon,
    items: [
      { href: "/support", label: "Support", Icon: HeadsetIcon },
    ],
  },
```

Delete the now-unused `SystemGroupIcon` and `SettingsIcon` components if nothing else references them (`grep -n "SystemGroupIcon\|SettingsIcon" app/components/Sidebar.tsx`).

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/components/Sidebar.rail.test`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/components/Sidebar.tsx app/components/Sidebar.rail.test.tsx
git commit -m "feat(sidebar): replace System group with Help › Support"
```

---

## Task 11: Sidebar footer identity and user menu

**Files:**
- Create: `frontend/app/components/SidebarUserMenu.tsx`
- Modify: `frontend/app/components/Sidebar.tsx` (footer block, currently the `{/* Collapse toggle + version */}` div)
- Test: `frontend/app/components/SidebarUserMenu.test.tsx`

**Interfaces:**
- Consumes: `useAccessIdentity` (Task 9), `useTheme` from `app/context/ThemeContext.tsx`.
- Produces: `<SidebarUserMenu collapsed={boolean} onOpenSettings={() => void} />`. Task 12 supplies `onOpenSettings`.

- [ ] **Step 1: Write the failing test**

Create `frontend/app/components/SidebarUserMenu.test.tsx`, mocking `useAccessIdentity`, and assert:

```tsx
it("shows the signed-in name and the non-committal logout copy", async () => {
  // identity mocked as { name: "Pat Warehouse", email: "pat@example.com" }
  await render();
  expect(container.textContent).toContain("Pat Warehouse");
  fireClick(byTitle("Account menu"));
  expect(container.textContent).toContain("Log out");
  // #98: Access ends its own session; Google keeps its own. The copy must
  // not imply a full sign-out on a shared workstation.
  expect(container.textContent).toContain(
    "Ends your dashboard session. You may still be signed in to Google.",
  );
});

it("renders a fallback label and hides Log out when not behind Access", async () => {
  // identity mocked as null
  await render();
  expect(container.textContent).toContain("Not signed in");
  fireClick(byTitle("Account menu"));
  expect(container.textContent).not.toContain("Log out");
});

it("keeps the name reachable in the popover when collapsed", async () => {
  await render({ collapsed: true });
  fireClick(byTitle("Account menu"));
  expect(container.textContent).toContain("pat@example.com");
});
```

Copy the render/mount scaffolding (`createRoot`, `act`, `IS_REACT_ACT_ENVIRONMENT`) from `Sidebar.rail.test.tsx`; `fireClick` is `act(() => el.dispatchEvent(new MouseEvent("click", { bubbles: true })))`, and `byTitle` is `container.querySelector('[title="Account menu"]')`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/SidebarUserMenu.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

Create `frontend/app/components/SidebarUserMenu.tsx`. Requirements, all from the mockup and #94/#98:

- Row: initials avatar (first letters of `name`, else the email's first two characters — reuse the initials helper in `app/components/leaderboard/primitives.tsx`), name on line 1, email muted on line 2, chevron on the right. `title="Account menu"`.
- When `collapsed`, render the avatar only; the name and email move into the popover so they stay reachable.
- Popover (anchored above the row, closes on outside click and Escape) contains, in order:
  1. **Settings** — calls `onOpenSettings()`.
  2. A **System / Dark / Light** quick-row using `useTheme()`; the active preference is highlighted.
  3. **Log out** — an `<a href="/cdn-cgi/access/logout">` with the muted hint line **"Ends your dashboard session. You may still be signed in to Google."** Rendered only when `signedIn`.
- When `!signedIn`, the name line reads **"Not signed in"** and the email line is omitted.

Mount it in `Sidebar.tsx` immediately above the Collapse button, inside the same bordered footer div:

```tsx
      <div className="border-t border-sidebar-border p-2">
        <SidebarUserMenu collapsed={collapsed} onOpenSettings={() => setSettingsOpen(true)} />
        {/* existing Collapse button + version paragraph unchanged */}
```

Add `const [settingsOpen, setSettingsOpen] = useState(false);` to `Sidebar`. Task 12 renders the modal from that state.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd frontend && npx vitest run app/components/SidebarUserMenu.test app/components/Sidebar.rail.test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/components/SidebarUserMenu.tsx app/components/SidebarUserMenu.test.tsx app/components/Sidebar.tsx
git commit -m "feat(sidebar): Cloudflare Access identity footer and user menu"
```

---

## Task 12: Settings modal, `/settings` retired

**Files:**
- Create: `frontend/app/components/SettingsModal.tsx`
- Modify: `frontend/app/components/Sidebar.tsx` (render the modal from `settingsOpen`)
- Modify: `frontend/app/settings/page.tsx`
- Delete: `frontend/app/components/SettingsDashboard.tsx`
- Test: `frontend/app/components/SettingsModal.test.tsx`

**Interfaces:**
- Consumes: `useTheme()`, `useDialog` from `app/hooks/useDialog.ts`.
- Produces: `<SettingsModal open={boolean} onClose={() => void} />`.

- [ ] **Step 1: Write the failing test**

Create `frontend/app/components/SettingsModal.test.tsx` asserting:

```tsx
it("renders the General nav and the Appearance segmented control", async () => {
  await render({ open: true });
  expect(container.textContent).toContain("Settings");
  expect(container.textContent).toContain("General");
  expect(container.textContent).toContain("Preferences");
  expect(container.textContent).toContain("Appearance");
  // Icon-only control: three buttons, no captions, no preview card (#95).
  const modes = ["System", "Dark", "Light"].map((m) =>
    container.querySelector(`[title="${m}"]`));
  expect(modes.every(Boolean)).toBe(true);
  expect(container.textContent).not.toContain("Preview");
});

it("closes on Escape", async () => {
  const onClose = vi.fn();
  await render({ open: true, onClose });
  await act(async () => {
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
  });
  expect(onClose).toHaveBeenCalled();
});

it("switching to Dark calls setTheme('dark')", async () => {
  await render({ open: true });
  fireClick(container.querySelector('[title="Dark"]')!);
  expect(setThemeSpy).toHaveBeenCalledWith("dark");
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/SettingsModal.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the modal**

Create `frontend/app/components/SettingsModal.tsx` following the mockup:

- Backdrop + centred panel; Escape, backdrop click, and a ✕ button all call `onClose`.
- Left column: the word "Settings" and a single **General** nav entry (active).
- Right pane: **Preferences** heading, then an **Appearance** row whose control is an icon-only segmented group of three buttons — monitor (`title="System"`), moon (`title="Dark"`), sun (`title="Light"`). Active segment gets the brand-surface treatment. **No caption text, no thumbnails, no Preview card.**
- Wiring is `const { theme, setTheme } = useTheme();` — nothing else, no backend call.

In `Sidebar.tsx`, after the `</aside>`, wrap the return in a fragment and render:

```tsx
      <SettingsModal open={settingsOpen} onClose={() => setSettingsOpen(false)} />
```

- [ ] **Step 4: Retire the page**

Replace `frontend/app/settings/page.tsx` with:

```tsx
import { redirect } from "next/navigation";

// Settings became a modal (wayfinder #95). Kept as a redirect so existing
// bookmarks do not 404.
export default function Page() {
  redirect("/");
}
```

Then: `rm frontend/app/components/SettingsDashboard.tsx` and confirm nothing else imports it — `grep -rn "SettingsDashboard" frontend/app` must return no hits.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd frontend && npx vitest run app/components/SettingsModal.test && npm run build`
Expected: tests pass, build succeeds.

- [ ] **Step 6: Commit**

```bash
cd frontend
git add app/components/SettingsModal.tsx app/components/SettingsModal.test.tsx \
        app/components/Sidebar.tsx app/settings/page.tsx
git rm app/components/SettingsDashboard.tsx
git commit -m "feat(settings): Claude.ai-style settings modal, retire the page"
```

---

## Task 13: Match highlighting and parcel linkification

**Files:**
- Create: `frontend/app/lib/matchHighlight.tsx`, `frontend/app/lib/parcelLinkify.tsx`
- Test: `frontend/app/lib/matchHighlight.test.ts`, `frontend/app/lib/parcelLinkify.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `matchRun(haystack: string, needle: string): { start: number; end: number } | null` — case-insensitive contiguous run, first occurrence, `null` when absent.
  - `highlightMatch(text: string, needle: string): React.ReactNode` — wraps the matched run in `<mark>`.
  - `linkifyParcels(text: string, onOpen: (ref: string) => void): React.ReactNode` — replaces parcel-looking runs with mono brand buttons.

- [ ] **Step 1: Write the failing tests**

`frontend/app/lib/matchHighlight.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { matchRun } from "./matchHighlight";

describe("matchRun", () => {
  it("matches a contiguous run starting anywhere", () => {
    // Export-invoice search-bar semantics, locked in #96.
    expect(matchRun("TH123456789", "6789")).toEqual({ start: 7, end: 11 });
  });
  it("is case-insensitive", () => {
    expect(matchRun("th123", "TH")).toEqual({ start: 0, end: 2 });
  });
  it("does not match a non-contiguous subsequence", () => {
    expect(matchRun("TH123456789", "159")).toBeNull();
  });
  it("returns null for an empty needle", () => {
    expect(matchRun("TH123", "")).toBeNull();
  });
});
```

`frontend/app/lib/parcelLinkify.test.tsx` renders `linkifyParcels("Parcel TH123456789 was late", onOpen)` and asserts one button exists whose text is `TH123456789`, that clicking it calls `onOpen("TH123456789")`, and that the surrounding words survive as text.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd frontend && npx vitest run app/lib/matchHighlight.test app/lib/parcelLinkify.test`
Expected: FAIL — modules not found.

- [ ] **Step 3: Implement**

```tsx
// frontend/app/lib/matchHighlight.tsx
export function matchRun(haystack: string, needle: string) {
  if (!needle) return null;
  const i = haystack.toLowerCase().indexOf(needle.toLowerCase());
  return i === -1 ? null : { start: i, end: i + needle.length };
}

export function highlightMatch(text: string, needle: string) {
  const run = matchRun(text, needle);
  if (!run) return text;
  return (
    <>
      {text.slice(0, run.start)}
      <mark className="bg-brand/20 text-brand">{text.slice(run.start, run.end)}</mark>
      {text.slice(run.end)}
    </>
  );
}
```

For `parcelLinkify.tsx`: split on `/\b([A-Z]{2,4}\d{6,}|\d{9,})\b/g` — the tracking and order shapes this warehouse sees — and render each captured run as `<button className="font-mono text-brand hover:underline" onClick={() => onOpen(ref)}>`. Plain text between matches passes through untouched. **Nothing is stored in this format**; linkification is render-side only (#96 item 7).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd frontend && npx vitest run app/lib/matchHighlight.test app/lib/parcelLinkify.test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/lib/matchHighlight.tsx app/lib/matchHighlight.test.ts \
        app/lib/parcelLinkify.tsx app/lib/parcelLinkify.test.tsx
git commit -m "feat(support): match highlighting and parcel linkification"
```

---

## Task 14: `/support` list view

**Files:**
- Create: `frontend/app/support/page.tsx`, `frontend/app/components/support/supportUi.tsx`, `frontend/app/components/support/SupportDashboard.tsx`
- Test: `frontend/app/components/support/SupportDashboard.test.tsx`

**Interfaces:**
- Consumes: `useSupportTickets` (Task 9).
- Produces: `statusGlyph(ticket)`, `categoryChip(category)`, `closeReasonLabel(reason)` from `supportUi.tsx`; `<SupportDashboard />`.

- [ ] **Step 1: Write the failing test**

Create `frontend/app/components/support/SupportDashboard.test.tsx` with `useSupportTickets` mocked to return two tickets (one open, one closed as `not_planned`). Assert:

- the heading "Help & Support Center" renders;
- filter tabs "Open", "Closed", "All" render with their counts;
- clicking "Closed" calls `setStatus("closed")`;
- each row shows the ticket title, its category chip, and `#{id}` plus the reporter;
- the "New ticket" button exists.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/support/SupportDashboard.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

`supportUi.tsx` — sibling of `app/components/issues/issueUi.tsx`. Status glyphs per #96: **open** = filled dot, **completed** = check, **not_planned** = slash, **duplicate** = copy. Category chips for all five categories.

`SupportDashboard.tsx` — copy the chrome from `app/components/issues/IssuesDashboard.tsx`:

- 60px `bg-card border-b` header bar with the heading + open/closed counts, a search pill bound to `searchInput`, and a round brand "New ticket" button that opens the Task 15 modal.
- GitHub-style filter tabs (Open / Closed / All with counts) and a category dropdown.
- Rows: status glyph, title + category chip, `#{id} · opened {relative time} by {reporterName ?? reporterEmail}`, and attachment/comment counts on the right. The whole row links to `/support/{id}`.

`app/support/page.tsx`:

```tsx
import { SupportDashboard } from "../components/support/SupportDashboard";

export default function Page() {
  return <SupportDashboard />;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/components/support/SupportDashboard.test`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/support/page.tsx app/components/support/
git commit -m "feat(support): Help & Support Center list view"
```

---

## Task 15: New-ticket modal with parcel typeahead

**Files:**
- Create: `frontend/app/components/support/ParcelTypeahead.tsx`, `frontend/app/components/support/NewTicketModal.tsx`
- Modify: `frontend/app/components/support/SupportDashboard.tsx` (wire the button)
- Test: `frontend/app/components/support/NewTicketModal.test.tsx`

**Interfaces:**
- Consumes: `useParcelSuggest`, `useAccessIdentity`, `highlightMatch` (Task 13), `PlatformGlyph` from `app/lib/platform.tsx`.
- Produces: `<NewTicketModal open onClose onCreated={(ticket) => void} />`, `<ParcelTypeahead value onChange />`.

- [ ] **Step 1: Write the failing test**

Assert in `NewTicketModal.test.tsx`:

- the "Filing as {name} · {email}" line renders from the mocked identity;
- submitting with an empty title is blocked (button disabled or no POST fired);
- choosing category `other` reveals the category-note field;
- typing in the parcel field and picking a suggestion sets both tracking and order on the submitted payload;
- the POST body contains `reporterName` but **no** `reporterEmail` — identity is the backend's job (spec §4.3).

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/support/NewTicketModal.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

`NewTicketModal.tsx` — modal chrome copied from `app/components/issues/ReportIssueModal.tsx`: top-anchored, 520px wide, footer pill buttons. Fields in order: **Title** (required, `maxLength={120}`), **Category** (select; reveals **Category note** when `other`), **Description** (textarea — the Task 17 mention popup mounts here), **Related parcel** (`<ParcelTypeahead />`), **Attachments** (Task 18). Footer line: "Filing as {name} · {email}", or "Filing as an unidentified user" when `!signedIn`.

`ParcelTypeahead.tsx` — text input driving `useParcelSuggest`; the dropdown reuses the row design from `app/components/AlertReconcileDropdown.tsx`: real `PlatformGlyph` + mono tracking (run highlighted via `highlightMatch`) + status/time badge. Picking a row calls `onChange({ trackingNumber, orderNumber })`; free text that matches nothing is submitted as `trackingNumber` alone.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/components/support/NewTicketModal.test`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/components/support/NewTicketModal.tsx app/components/support/NewTicketModal.test.tsx \
        app/components/support/ParcelTypeahead.tsx app/components/support/SupportDashboard.tsx
git commit -m "feat(support): new-ticket modal with parcel typeahead"
```

---

## Task 16: Ticket detail view

**Files:**
- Create: `frontend/app/support/[id]/page.tsx`, `frontend/app/components/support/SupportTicketDetail.tsx`
- Test: `frontend/app/components/support/SupportTicketDetail.test.tsx`

**Interfaces:**
- Consumes: `useSupportTicket` (Task 9), `linkifyParcels` (Task 13), `OrderTimelineModal` from `app/components/issues/OrderTimelineModal.tsx`.
- Produces: `<SupportTicketDetail ticketId={number} />`.

- [ ] **Step 1: Write the failing test**

Assert in `SupportTicketDetail.test.tsx`, with `useSupportTicket` mocked:

- breadcrumb reads `Support › Ticket #{id}`;
- the thread interleaves comments and events strictly by timestamp, and an event renders as "closed this as completed";
- an edited comment shows "(edited)";
- the sidebar Status card's "Close ticket" button calls `close("completed")` with the selected reason;
- a closed ticket shows "Reopen" instead, and clicking it calls `reopen()`;
- the Details card lists reporter, email, category, tracking, order, opened, attachments;
- a tracking number inside the body renders as a button that opens the timeline modal;
- **an author-only 403 from `editTicket` surfaces as visible error text** — it must not fail silently.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/support/SupportTicketDetail.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

Layout copied from `app/components/issues/ReportDetail.tsx`: breadcrumb header bar, then a two-column grid with the thread left and a 340px sidebar right.

- **Thread**: `[...comments, ...events]` sorted by `createdAt`/`at`. Comments render author + relative time + `linkifyParcels(body, openTimeline)` and an Edit affordance when `authorEmail === identity.email`. Events render GitHub-style single lines: `filed` → "opened this ticket", `edited` → "edited this ticket", `closed` → "closed this as {closeReasonLabel(detail.reason)}", `reopened` → "reopened this ticket".
- **Composer** at the bottom of the thread: textarea + "Comment" button calling `addComment`. **No close control here** — closing lives in the sidebar (#96 item 3).
- **Status card** (sidebar): close-reason `<select>` (Completed / Not planned / Duplicate) plus a "Close ticket" button, or a "Reopen" button when already closed. Anyone may act.
- **Details card** (sidebar): reporter, email, category, tracking, order, opened, attachment count.
- Parcel clicks open a cloned `OrderTimelineModal` keyed by the clicked reference.

`app/support/[id]/page.tsx` unwraps the route param and renders `<SupportTicketDetail ticketId={Number(id)} />`. In Next 16 `params` is a Promise — follow whatever the neighbouring dynamic route in `app/` already does rather than inventing a signature.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/components/support/SupportTicketDetail.test`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/support/\[id\]/page.tsx app/components/support/SupportTicketDetail.tsx \
        app/components/support/SupportTicketDetail.test.tsx
git commit -m "feat(support): ticket detail with merged comment/event thread"
```

---

## Task 17: `#` parcel mention popup

**Files:**
- Create: `frontend/app/components/support/ParcelMentionPopup.tsx`
- Modify: `frontend/app/components/support/NewTicketModal.tsx`, `frontend/app/components/support/SupportTicketDetail.tsx` (mount on both composers)
- Test: `frontend/app/components/support/ParcelMentionPopup.test.tsx`

**Interfaces:**
- Consumes: `useParcelSuggest`, `highlightMatch`, `PlatformGlyph`.
- Produces: `<ParcelMentionPopup textareaRef value onChange />` — a controlled wrapper that watches the caret and inserts the chosen tracking number as plain text.

- [ ] **Step 1: Write the failing test**

Assert:

- typing `#` opens the popup and typing after it filters;
- at most **5** rows render even when the hook returns more;
- the matched run is wrapped in `<mark>`, and rows match on order number as well as tracking;
- ↓ then Enter inserts the highlighted row's tracking number as plain text, replacing the `#…` token, and closes the popup;
- Escape closes it without inserting;
- typing `@` does nothing (reserved for people, v1 no-op);
- typing `#` does **not** search tickets — the hook is called with the parcel endpoint only.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/support/ParcelMentionPopup.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

GitHub behaviour, per #96 item 8:

- Trigger on `#` at a word boundary; the query is everything typed after it up to whitespace.
- The popup is anchored **below the caret** (measure with a mirrored hidden div, the standard textarea-caret technique) and has **no inner search box**.
- Max 5 rows, from `useParcelSuggest` (which already asks the backend for `limit=5`).
- Rows use the `AlertReconcileDropdown` design: real `PlatformGlyph`, mono tracking with the matched run highlighted, status/time badge.
- The selected row's background is brand-surface. **No left stripe, no hint row.**
- ↑/↓ move the selection, Enter inserts the plain tracking number, Escape closes.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/components/support/ParcelMentionPopup.test`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/components/support/ParcelMentionPopup.tsx app/components/support/ParcelMentionPopup.test.tsx \
        app/components/support/NewTicketModal.tsx app/components/support/SupportTicketDetail.tsx
git commit -m "feat(support): # parcel mention popup"
```

---

## Task 18: Attachment UI

**Files:**
- Create: `frontend/app/components/support/AttachmentPicker.tsx`
- Modify: `frontend/app/components/support/NewTicketModal.tsx`, `frontend/app/components/support/SupportTicketDetail.tsx`
- Test: `frontend/app/components/support/AttachmentPicker.test.tsx`

**Interfaces:**
- Consumes: `uploadAttachment` from `useSupportTicket`.
- Produces: `<AttachmentPicker files onChange />` for the pre-create case (files held in state until the ticket exists) and `<AttachmentPicker ticketId commentId />` for the post-create case (uploads immediately).

- [ ] **Step 1: Write the failing test**

Assert: an `image/png` file renders as a thumbnail; an `.xlsx` renders as a chip showing name and human-readable size; removing a pending file drops it from the list; on a ticket that already exists, adding a file calls `uploadAttachment`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run app/components/support/AttachmentPicker.test`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

Files **and** images (#96 item 5 — xlsx evidence is the primary use case). `contentType.startsWith("image/")` decides thumbnail versus chip; chips show `fileName` and a formatted size. In the new-ticket modal the files are held in state and uploaded after `POST /support/tickets` returns an id. Existing attachments render from `detail.attachments`, sourced at `GET {API}/support/attachments/{id}/media`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npx vitest run app/components/support/AttachmentPicker.test`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd frontend
git add app/components/support/AttachmentPicker.tsx app/components/support/AttachmentPicker.test.tsx \
        app/components/support/NewTicketModal.tsx app/components/support/SupportTicketDetail.tsx
git commit -m "feat(support): file and image attachment UI"
```

---

## Task 19: Deployment checklist and full verification

**Files:**
- Create: `docs/runbooks/2026-08-02-support-deployment.md` (root repo)

**Interfaces:**
- Consumes: everything above.
- Produces: the operator-facing checklist for enabling Access protection and the MinIO bucket.

- [ ] **Step 1: Write the checklist**

Create `docs/runbooks/2026-08-02-support-deployment.md` covering, in order:

1. **Enable Protect with Access on the tunnel** — `access: { required: true, teamName: <team>, audTag: [<AUD>] }` on this app's public hostname service. Where to find the AUD tag: Zero Trust → Access controls → Applications → Configure → Additional settings → Application Audience (AUD) Tag. This is the whole of the signature verification; §4.1 of the spec explains why there is none in Rust.
2. **Create the MinIO bucket `support`.**
3. **Confirm `ACCESS_AUTH` is unset in production** (default = required). Only the dev compose file sets `ACCESS_AUTH=dev_bypass`.
4. **Run the migration** — `sqlx migrate run --ignore-missing` (the dev DB's orphan `20260703220000` row).
5. **Post-deploy smoke** — the acceptance criteria list from spec §7.
6. **Recorded accepted risk** — a deliberate LAN forger posting a self-made JWT straight to `192.168.1.112:8080` is not stopped. Do not describe the origin as "verified"; it is "verified at the tunnel, trusted at the origin".

- [ ] **Step 2: Run the full backend suite**

Run: `cd backend && cargo test`
Expected: green. If a leaderboard or `product_insights` test fails, check it against the base branch first — `reference_dev12-preexisting-test-failures` records four failures that predate this work. Use `--no-fail-fast` so they do not mask real ones.

- [ ] **Step 3: Run the full frontend suite and build**

Run: `cd frontend && npx vitest run && npm run lint && npm run build`
Expected: green.

- [ ] **Step 4: Walk the acceptance criteria against the running stack**

Bring up the dev stack (`reference_local-dev-stack`), then walk spec §7 items 1–10 by hand. Criterion 3 is the one to be pedantic about:

```bash
curl -i -X POST localhost:8080/support/tickets \
  -H 'content-type: application/json' \
  -d '{"title":"forged","category":"bug"}'
```

Expected with `ACCESS_AUTH` unset: `HTTP/1.1 403 Forbidden`.

- [ ] **Step 5: Commit**

```bash
git add docs/runbooks/2026-08-02-support-deployment.md
git commit -m "docs(support): deployment runbook"
```

---

## Self-review notes

Checked against the spec on 2026-08-02:

- **Coverage** — spec §3 → Task 1; §4.1 → Tasks 7 (bucket) and 19 (tunnel); §4.2 → Task 2; §4.3 → Tasks 3, 6, 15; §4.4 → Task 9; §4.5 → Task 11; §5 → Tasks 3–8; §6.1–6.2 → Tasks 9, 14–18; §6.3 → Task 14; §6.4 → Task 16; §6.5 → Task 15; §6.6 → Tasks 13, 15, 17; §6.7 → Task 18; §6.8 → Tasks 10, 11; §6.9 → Task 12; §7 → Task 19; §8 → Global Constraints.
- **Naming consistency** — `AccessIdentity` (Rust extractor, Task 2) and `AccessIdentity` (TS interface, Task 9) intentionally share a name across languages; they are different shapes (`{sub, email}` vs `{name?, email}`) because the JWT has no name and get-identity has no `sub`. That asymmetry is the spec's §4.3/§4.4 split, not a mistake.
- **Known softness** — Tasks 14–18 give exact files, props, reuse targets and assertions but prose rather than full component source, because the mockup is the authoritative pixel reference and transcribing it into this document would fork the truth. Open `docs/mockups/2026-08-02-support-settings-rebrand.html` alongside each of those tasks. Every other task carries literal code.
