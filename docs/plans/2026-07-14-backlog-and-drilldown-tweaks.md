# Open Backlog + Drill-down Tweaks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an as-of-now Open Backlog view (red-accent section on `/` with stage chips, age column, and bulk manual order-cancel) plus five small carryover drill-down tweaks (amber Submitted At, bare `×` close, shipping+status table filters, distinct-parcel video count).

**Architecture:** Backend adds five `backlog*` itemType arms and a `BACKLOG_WATERMARK` constant to `packing_list.rs`, a `GET /dashboard/backlog` grouped-count endpoint, a `("backlog","cancel")` resolve arm that stamps `order_status='Cancelled'` across the whole order, one partial-index migration, and switches the carryover `completed_videos` to distinct parcels (raw count moves to a new `videoEvents` field). Frontend adds `useBacklogParcels`/`useBacklogSummary` hooks, a `BacklogSection` that reuses `PackingTable` (new opt-in props: `highlightSubmittedAt`, `showAge`, row selection), a shared `CohortFilters` (shipping + status selects) used by both the carryover drill-down and the backlog table, and a bulk-cancel confirm dialog.

**Tech Stack:** Rust / Axum / SQLx (integration tests vs real Postgres) · Next.js 16 / React 19 / Tailwind v4 / TypeScript strict / vitest 4 + jsdom (NO @testing-library).

## Global Constraints

- Spec: `docs/specs/2026-07-14-backlog-and-drilldown-tweaks.md`. Copy strings in it are verbatim deliverables.
- Backend worktree: `backend/.worktrees/analytics-debug`, branch `fix/analytics-post-shipped` (continue on tip f809089). Frontend worktree: `frontend/.worktrees/analytics-debug`, same branch (continue on tip 6b635ee). Commit in each submodule worktree, never the monorepo root. Verify each commit's parent is the expected branch tip before review.
- Backend tests: `cargo test --no-fail-fast` from the BE worktree (needs the dev stack: `DATABASE_URL=postgresql://warehouse_user:warehouse_user@localhost:5432/warehouse_db_test`, MinIO env or minioadmin defaults). PRE-EXISTING failures at base — do NOT chase: dashboard_api `resolve_stale_cancel`, `resolve_packing_video_accept`, `resolve_packing_video_accept_backfills_packed_by_and_station`, `resolve_audit_trail_fields`; 3 leaderboard; 1 product_insights; 1 import_trace; 5 product_images; 1 warehouse_invoice. Only new/changed tests must be green — run them by name.
- Migration discipline: stack NEW files only, never edit applied ones; `sqlx migrate run --ignore-missing` (shared dev DB has an orphan `20260703220000` row).
- Frontend tests: `npm test` (vitest run) + `npm run lint`; `// @vitest-environment jsdom` per DOM test file; `react-dom/client` `createRoot` + `act` from `react`; `(globalThis as any).IS_REACT_ACT_ENVIRONMENT = true`; NO @testing-library. Tailwind v4: every new colour style needs a `dark:` variant. Numbers via `toLocaleString()`.
- The FE worktree carries the uncommitted DIAGNOSTIC(bkk) patch in `app/lib/dateWindow.ts` (Bangkok-pinned `todayStr()`/`dateRange()` bodies, marked "never commit"). IMPORT and CALL these functions freely — but never commit that file: `git add` named files only, never `-A`; check `git diff --cached` before each commit.
- Backlog wire vocabulary (Tasks 3/4/10 must match exactly): itemTypes `backlog`, `backlog-submitted`, `backlog-qc-hold`, `backlog-qc-passed`, `backlog-packed`; summary route `GET /dashboard/backlog?cutoff=<ISO>`; watermark constant `2026-07-02T17:00:00Z`.
- Verbatim copy (FE):
  - Video def: `of the {total}, these had a packing video completed in this period · {videoEvents} videos`
  - Close control: `×` with `aria-label="Close"`
  - Filter selects: first options `All shipping` / `All statuses`
  - Backlog eyebrow: `Open backlog`; summary line tail: `parcels not yet shipped · submitted before today · oldest {date}`
  - Backlog chips: `All · {n}` / `Submitted · {n}` / `QC Hold · {n}` / `QC Passed · {n}` / `Packed · {n}`
  - Backlog footer: `backlog since 3 Jul 2026 (Shipping go-live) · cancelled orders excluded · independent of the date filter above`
  - Backlog table heading: `Backlog parcels`
  - Error row: `Couldn't load parcels — retry`
  - Cancel bar: `Cancel {n} parcels` + `Clear selection`
  - Dialog warning: `Cancelling marks the whole order as cancelled on every parcel, including any already shipped. This cannot be undone here.`
- Deploy together: old BE silently drops unknown `backlog*` itemTypes (full-window list under backlog headers) and lacks `videoEvents`. Both submodules ship on this branch as one release.
- Pushes and monorepo submodule-pointer bumps are USER-GATED — do not push.
- `graphify update .` is broken (`NameError: name '_os' is not defined`) — skip it.

---

## Phase 1 — Backend (worktree `backend/.worktrees/analytics-debug`)

Run every command from `backend/.worktrees/analytics-debug`.

### Task 1: Partial index migration

**Files:**
- Create: `migrations/20260714120000_open_backlog_index.sql`

**Interfaces:**
- Produces: index `idx_packing_lists_open_backlog` serving Task 3's arms and Task 4's summary query. No code depends on its name; perf only.

- [ ] **Step 1: Write the migration**

```sql
-- Open Backlog (spec 2026-07-14 §B.14): partial index for the as-of-now
-- open-parcel membership used by GET /dashboard/backlog and the backlog
-- itemType arms. Predicate matches the membership's immutable core;
-- created_at is the range column both consumers filter and MIN() over.
CREATE INDEX idx_packing_lists_open_backlog
    ON packing_lists (created_at)
    WHERE shipped_at IS NULL AND returned_at IS NULL;
```

- [ ] **Step 2: Apply and verify**

Run: `sqlx migrate run --ignore-missing`
Expected: `Applied 20260714120000/migrate open backlog index`

Run: `psql "$DATABASE_URL" -c "\di idx_packing_lists_open_backlog"`
Expected: one row, table `packing_lists`.

- [ ] **Step 3: Commit**

```bash
git add migrations/20260714120000_open_backlog_index.sql
git commit -m "feat(db): partial index for open-backlog membership"
```

### Task 2: Carryover video count → distinct parcels + `videoEvents`

**Files:**
- Modify: `src/api/dashboard.rs` (struct `CarryoverBreakdown` ~line 82; carryover `co_videos_sql` block lines ~376-388; carryover per-platform `co_video_rows` block lines ~412-425)
- Test: `tests/dashboard_api.rs` (append)

**Interfaces:**
- Consumes: existing `GET /dashboard/summary?from&to`; carryover block gated on `q.from`.
- Produces: `CarryoverBreakdown.completed_videos` (wire `completedVideos`) = DISTINCT parcels with ≥1 completed video in window and `created_at < from`; NEW `CarryoverBreakdown.video_events` (wire `videoEvents`) = raw completed-video row count (old semantics). The carryover per-platform `completed_videos` (inside `by_platform`) ALSO switches to distinct parcels so the video pill's platform breakdown sums to the pill headline. Window-level `Summary.completed_videos` UNCHANGED. Task 7 (FE) reads `videoEvents`.

**Context:** the summary response is cached by `CACHE.summary` keyed on `from|to|carryover` — use a window unique to this test so no cached entry exists. Isolation trick: put the *window* in year 2100; only this test's fixtures have stage/video events there, and `created_at < from` is satisfied by any past `created_at`.

- [ ] **Step 1: Write the failing test**

Append to `tests/dashboard_api.rs`:

```rust
/// Spec 2026-07-14 §A.5: carryover completedVideos counts DISTINCT parcels
/// (pill↔table parity); the raw video-row count moves to videoEvents.
#[tokio::test]
async fn carryover_video_counts_distinct_parcels() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_covdp_";
    cleanup(&pool, prefix).await;

    // One carryover parcel (created long before the 2100 window, QC'd inside
    // it) with TWO completed videos inside the window.
    let tn = format!("{prefix}A");
    insert_packing_list(&pool, &tn, &format!("{prefix}ORD_A"), "Shopee", Some("Packed"), Utc::now() - Duration::days(5)).await;
    sqlx::query("UPDATE packing_lists SET checked_at = '2100-01-01T05:00:00Z' WHERE tracking_number = $1")
        .bind(&tn).execute(&pool).await.unwrap();
    for n in 1..=2 {
        sqlx::query(
            "INSERT INTO packing_videos (tracking_number, status, station_id, file_path, file_name, created_at, updated_at)
             VALUES ($1, 'Completed', NULL, $2, $2, '2100-01-01T06:00:00Z', '2100-01-01T06:00:00Z')")
            .bind(&tn).bind(format!("{prefix}{n}.mp4"))
            .execute(&pool).await.unwrap();
    }

    let body: Value = reqwest::get(format!(
        "{url}/dashboard/summary?from=2100-01-01T00:00:00Z&to=2100-01-02T00:00:00Z"
    )).await.unwrap().json().await.unwrap();

    let co = &body["carryover"];
    assert_eq!(co["completedVideos"].as_i64().unwrap(), 1, "distinct parcels, not videos");
    assert_eq!(co["videoEvents"].as_i64().unwrap(), 2, "raw video rows keep living in videoEvents");
    // Platform breakdown must sum to the headline (same distinct semantics).
    let shopee = co["byPlatform"].as_array().unwrap().iter()
        .find(|p| p["platform"] == "Shopee").expect("Shopee platform row");
    assert_eq!(shopee["completedVideos"].as_i64().unwrap(), 1, "per-platform video count is distinct parcels too");

    cleanup(&pool, prefix).await;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test carryover_video_counts_distinct_parcels -- --nocapture`
Expected: FAIL — `videoEvents` is null (field doesn't exist yet) or completedVideos == 2.

- [ ] **Step 3: Implement**

In `src/api/dashboard.rs`, add the field to the struct:

```rust
pub struct CarryoverBreakdown {
    pub total_orders: i64,
    pub total_parcels: i64,
    pub by_status: StatusBreakdown,
    pub by_platform: Vec<PlatformCount>,
    pub completed_videos: i64,
    /// Raw completed-video row count (a parcel re-recorded twice counts twice).
    /// completed_videos above counts DISTINCT parcels (spec 2026-07-14 §A.5).
    pub video_events: i64,
    pub failed_videos: i64,
    pub dates: Vec<CarryoverDateGroup>,
}
```

Replace the carryover videos query (lines ~376-388) with a single two-column query:

```rust
            let co_videos_sql = format!(
                "SELECT COUNT(*)::bigint, COUNT(DISTINCT pv.tracking_number)::bigint
                 FROM packing_videos pv
                 JOIN packing_lists pl ON pl.tracking_number = pv.tracking_number
                 WHERE pv.status = 'Completed'
                   AND pl.created_at < $1
                   AND {w_video}"
            );
            let (co_video_events, co_videos): (i64, i64) = sqlx::query_as(&co_videos_sql)
                .bind(from_ts)
                .bind(q.to)
                .fetch_one(&pool)
                .await?;
```

and in the `Some(CarryoverBreakdown { ... })` assembly add `video_events: co_video_events,` (keep `completed_videos: co_videos,`).

Also switch the carryover PER-PLATFORM video counts (the `co_video_rows` query, lines ~412-425, raw `COUNT(*)` per platform feeding `by_platform[].completed_videos`) to the same distinct semantics — change its `COUNT(*)` to `COUNT(DISTINCT pv.tracking_number)` with the query otherwise untouched. Without this the video pill headline would show 98 while its own expandable platform rows still sum to 107.

- [ ] **Step 4: Run tests**

Run: `cargo test carryover_video_counts_distinct_parcels`
Expected: PASS.
Run: `cargo test --test dashboard_api --no-fail-fast 2>&1 | tail -20`
Expected: only the four pre-existing `resolve_*` failures.

- [ ] **Step 5: Commit**

```bash
git add src/api/dashboard.rs tests/dashboard_api.rs
git commit -m "feat(dashboard): carryover video count = distinct parcels, raw count as videoEvents"
```

### Task 3: Backlog itemType arms + suppression + carried-in test assert

**Files:**
- Modify: `src/api/packing_list.rs` (both `match *t` blocks — anchor on `"carryover-video" =>`; the carryover-family const array ~line 202; the itemType split)
- Test: `tests/packing_list_types.rs` (append + 1-line edit)

**Interfaces:**
- Consumes: `GET /packing-lists/list` with `itemType`, `from` bind `$3` (used as the start-of-today cutoff), `to` bind `$4` (unused by backlog arms).
- Produces: `pub const BACKLOG_WATERMARK: &str = "2026-07-02T17:00:00Z";` (Task 4 imports it) and five wire itemTypes `backlog`, `backlog-submitted`, `backlog-qc-hold`, `backlog-qc-passed`, `backlog-packed` (Task 10's `BACKLOG_ITEM_TYPE` map must match verbatim). Membership per spec §B.6, stages per §B.7.

**Context:** both match blocks must gain all five arms — aliased (`pl.` prefix, rows query) and unaliased twin (COUNT + availability). The existing all-carryover window suppression array must also list the backlog family, or the base `updated_at` window would silently drop backlog rows whose `updated_at` drifted.

- [ ] **Step 1: Write the failing test**

Append to `tests/packing_list_types.rs`:

```rust
/// Open Backlog arms (spec 2026-07-14 §B.6-B.8): event-time membership with
/// the Jul-3 watermark and start-of-today cutoff riding the `from` param;
/// stage partition from event timestamps.
#[tokio::test]
async fn backlog_stage_cohorts_and_exclusions() {
    let (base, pool) = spawn_app().await;
    let prefix = "PLBKLG";

    let wipe = |pool: sqlx::PgPool| async move {
        sqlx::query("DELETE FROM packing_lists WHERE tracking_number LIKE 'PLBKLG-%'")
            .execute(&pool).await.unwrap();
    };
    wipe(pool.clone()).await;

    // (suffix, created, checked, packed, shipped, returned, order_status, packing_status, upl)
    // upl drives trigger-computed all_items_cleared (fn_compute_all_items_cleared,
    // migration 20260421050535): the trigger requires an OBJECT with an 'items'
    // array — {"items":[...]}; items quantity-sum 0 => true, non-zero => false,
    // anything else (incl. a bare top-level array) => false.
    let rows: [(&str, &str, Option<&str>, Option<&str>, Option<&str>, Option<&str>, Option<&str>, &str, Option<&str>); 9] = [
        ("SUB",  "2026-07-04T04:00:00Z", None, None, None, None, None, "To be packed", None),
        ("HOLD", "2026-07-04T05:00:00Z", Some("2026-07-04T06:00:00Z"), None, None, None, None, "QC Hold", Some(r#"{"items":[{"name":"x","quantity":1}]}"#)),
        ("PASS", "2026-07-04T05:00:00Z", Some("2026-07-04T06:00:00Z"), None, None, None, None, "QC Passed", Some(r#"{"items":[{"name":"x","quantity":0}]}"#)),
        ("PACK", "2026-07-04T05:00:00Z", Some("2026-07-04T06:00:00Z"), Some("2026-07-04T07:00:00Z"), None, None, None, "Packed", Some(r#"{"items":[{"name":"x","quantity":0}]}"#)),
        ("SHIP", "2026-07-04T05:00:00Z", None, None, Some("2026-07-04T08:00:00Z"), None, None, "Shipped", None),
        ("RET",  "2026-07-04T05:00:00Z", None, None, None, Some("2026-07-04T08:00:00Z"), None, "Returned", None),
        ("CANO", "2026-07-04T05:00:00Z", None, None, None, None, Some("Cancelled"), "To be packed", None),
        ("OLD",  "2026-06-20T05:00:00Z", None, None, None, None, None, "To be packed", None),
        ("NEW",  "2026-07-06T05:00:00Z", None, None, None, None, None, "To be packed", None),
    ];
    for (suffix, created, checked, packed, shipped, returned, order_status, packing_status, upl) in rows {
        sqlx::query(
            "INSERT INTO packing_lists
               (tracking_number, order_number, platform, packing_status, created_at, updated_at,
                total_items, checked_at, packed_at, shipped_at, returned_at, order_status, updated_product_lists)
             VALUES ($1, $2, 'Shopee', $3, $4::timestamptz, $4::timestamptz, 1,
                     $5::timestamptz, $6::timestamptz, $7::timestamptz, $8::timestamptz, $9, $10::jsonb)")
            .bind(format!("{prefix}-{suffix}"))
            .bind(format!("{prefix}-O{suffix}"))
            .bind(packing_status)
            .bind(created)
            .bind(checked)
            .bind(packed)
            .bind(shipped)
            .bind(returned)
            .bind(order_status)
            .bind(upl)
            .execute(&pool).await.unwrap();
    }
    // Legacy manual stale-cancel: excluded via packing_status.
    sqlx::query(
        "INSERT INTO packing_lists (tracking_number, order_number, platform, packing_status, created_at, updated_at, total_items)
         VALUES ($1, $2, 'Shopee', 'Cancelled', '2026-07-04T05:00:00Z', '2026-07-04T05:00:00Z', 1)")
        .bind(format!("{prefix}-CANP")).bind(format!("{prefix}-OCANP"))
        .execute(&pool).await.unwrap();

    // cutoff = "start of today" for this test's fixture world.
    let window = "from=2026-07-06T00:00:00Z&to=2026-07-06T23:59:59Z";
    for (item_type, expected) in [
        ("backlog", vec!["HOLD", "PACK", "PASS", "SUB"]),
        ("backlog-submitted", vec!["SUB"]),
        ("backlog-qc-hold", vec!["HOLD"]),
        ("backlog-qc-passed", vec!["PASS"]),
        ("backlog-packed", vec!["PACK"]),
    ] {
        let body: Value = reqwest::get(format!(
            "{base}/packing-lists/list?search={prefix}-&itemType={item_type}&{window}&limit=50"
        )).await.unwrap().json().await.unwrap();
        let mut tns: Vec<String> = body["items"].as_array().unwrap().iter()
            .map(|i| i["trackingNumber"].as_str().unwrap().to_string()).collect();
        tns.sort();
        let want: Vec<String> = expected.iter().map(|s| format!("{prefix}-{s}")).collect();
        assert_eq!(tns, want, "itemType={item_type}");
        assert_eq!(body["total"].as_i64().unwrap(), want.len() as i64, "count arm for {item_type}");
    }

    wipe(pool.clone()).await;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test backlog_stage_cohorts_and_exclusions -- --nocapture`
Expected: FAIL — unknown itemType arms are silently ignored AND the selection is not in the suppression family yet, so the base `updated_at` window (the 2026-07-06 day) applies: every selection returns only `PLBKLG-NEW` (its `updated_at` is the sole in-window one) instead of its cohort.

- [ ] **Step 3: Implement the arms**

In `src/api/packing_list.rs`, above the handler add:

```rust
/// Open Backlog can only trust event-time data from the Shipping-mode
/// go-live onward (2026-07-03 00:00 Asia/Bangkok). Earlier rows may be
/// physically shipped with no shipped_at (spec 2026-07-14 §B.6).
pub const BACKLOG_WATERMARK: &str = "2026-07-02T17:00:00Z";
```

In the ALIASED `match *t` block (after the `"carryover-video"` arm) add:

```rust
            "backlog" => Some(format!(
                "(pl.created_at >= '{BACKLOG_WATERMARK}'::timestamptz AND pl.created_at < $3 \
                  AND pl.shipped_at IS NULL AND pl.returned_at IS NULL \
                  AND pl.order_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packing_status IS DISTINCT FROM 'Cancelled')"
            )),
            "backlog-submitted" => Some(format!(
                "(pl.created_at >= '{BACKLOG_WATERMARK}'::timestamptz AND pl.created_at < $3 \
                  AND pl.shipped_at IS NULL AND pl.returned_at IS NULL \
                  AND pl.order_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packing_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.checked_at IS NULL AND pl.packed_at IS NULL)"
            )),
            "backlog-qc-hold" => Some(format!(
                "(pl.created_at >= '{BACKLOG_WATERMARK}'::timestamptz AND pl.created_at < $3 \
                  AND pl.shipped_at IS NULL AND pl.returned_at IS NULL \
                  AND pl.order_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packing_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packed_at IS NULL AND pl.checked_at IS NOT NULL AND pl.all_items_cleared IS NOT TRUE)"
            )),
            "backlog-qc-passed" => Some(format!(
                "(pl.created_at >= '{BACKLOG_WATERMARK}'::timestamptz AND pl.created_at < $3 \
                  AND pl.shipped_at IS NULL AND pl.returned_at IS NULL \
                  AND pl.order_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packing_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packed_at IS NULL AND pl.checked_at IS NOT NULL AND pl.all_items_cleared IS TRUE)"
            )),
            "backlog-packed" => Some(format!(
                "(pl.created_at >= '{BACKLOG_WATERMARK}'::timestamptz AND pl.created_at < $3 \
                  AND pl.shipped_at IS NULL AND pl.returned_at IS NULL \
                  AND pl.order_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packing_status IS DISTINCT FROM 'Cancelled' \
                  AND pl.packed_at IS NOT NULL)"
            )),
```

Add the same five arms to the UNALIASED `type_clause_count` block with every `pl.` prefix removed (the `carryover-video` arm there shows the unaliased style — `packing_lists.tracking_number` for the EXISTS correlation; backlog arms have no subquery so it is a plain prefix strip).

Replace the window-suppression const (~line 201-203). It is explicitly length-typed — `const CARRYOVER_FAMILY: [&str; 5] = […];` — so appending entries without retyping is a compile error, and the name no longer describes the contents. Full replacement:

```rust
    /// itemType families whose clauses ARE the window (event-time / as-of-now):
    /// when every selected type is in here, the base updated_at window predicate
    /// is suppressed (spec 2026-07-13 §2.7, extended by 2026-07-14 §B.8).
    const EVENT_TIME_FAMILY: [&str; 10] = [
        "carryover", "carryover-qc", "carryover-packed", "carryover-shipped", "carryover-video",
        "backlog", "backlog-submitted", "backlog-qc-hold", "backlog-qc-passed", "backlog-packed",
    ];
```

Update the two usage sites (`.all(|t| CARRYOVER_FAMILY.contains(t))` at ~line 204-205 and any comment naming it) to `EVENT_TIME_FAMILY`.

Carried-in optional: where `itemType` is split on `','`, add `.map(str::trim)` so `"backlog, backlog-packed"` parses.

- [ ] **Step 4: Carried-in review fix — items-length assert**

In `all_carryover_selection_ignores_updated_at_window`, inside the pure-carryover `for` loop after the `total` assert, add:

```rust
        assert_eq!(
            body["items"].as_array().unwrap().len(), 1,
            "itemType={item_type}: rows sql must apply the same window suppression"
        );
```

- [ ] **Step 5: Run tests**

Run: `cargo test --test packing_list_types --no-fail-fast`
Expected: ALL tests in this file PASS (including the amended carryover test).

- [ ] **Step 6: Commit**

```bash
git add src/api/packing_list.rs tests/packing_list_types.rs
git commit -m "feat(packing-list): open-backlog itemType arms with Jul-3 watermark"
```

### Task 4: GET /dashboard/backlog summary endpoint

**Files:**
- Modify: `src/api/dashboard.rs` (new structs + handler), `src/api/mod.rs` (route)
- Test: `tests/dashboard_api.rs` (append)

**Interfaces:**
- Consumes: `BACKLOG_WATERMARK` from Task 3 (`use super::packing_list::BACKLOG_WATERMARK;` — packing_list is a sibling module under `api`).
- Produces: `GET /dashboard/backlog?cutoff=<ISO>` → `{ total, byStage: { submitted, qcHold, qcPassed, packed }, oldestCreatedAt }` (camelCase; `oldestCreatedAt` null when empty). Task 10's `backlogSummaryUrl` targets this path/param verbatim.

- [ ] **Step 1: Write the failing test**

Append to `tests/dashboard_api.rs` (delta assertions — the endpoint is global, so compare before/after our fixtures inside a cutoff window no other test writes into):

```rust
/// Spec 2026-07-14 §B.8: as-of-now backlog summary, event-time stages,
/// watermark + cutoff bounds, cancelled exclusions.
#[tokio::test]
async fn backlog_summary_counts_by_stage() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_bklg_";
    cleanup(&pool, prefix).await;

    let get = |url: String| async move {
        reqwest::get(url).await.unwrap().json::<Value>().await.unwrap()
    };
    let ep = format!("{url}/dashboard/backlog?cutoff=2026-07-06T00:00:00Z");
    let before = get(ep.clone()).await;

    // submitted / qc-hold / qc-passed / packed + one excluded (shipped).
    // upl must be the {"items":[...]} OBJECT shape the all_items_cleared
    // trigger expects (see Task 3's fixture comment).
    for (suffix, checked, packed, shipped, upl) in [
        ("SUB",  None, None, None, None),
        ("HOLD", Some("2026-07-04T06:00:00Z"), None, None, Some(r#"{"items":[{"name":"x","quantity":1}]}"#)),
        ("PASS", Some("2026-07-04T06:00:00Z"), None, None, Some(r#"{"items":[{"name":"x","quantity":0}]}"#)),
        ("PACK", Some("2026-07-04T06:00:00Z"), Some("2026-07-04T07:00:00Z"), None, Some(r#"{"items":[{"name":"x","quantity":0}]}"#)),
        ("SHIP", None, None, Some("2026-07-04T08:00:00Z"), None),
    ] {
        sqlx::query(
            "INSERT INTO packing_lists
               (tracking_number, order_number, platform, packing_status, created_at, updated_at,
                total_items, checked_at, packed_at, shipped_at, updated_product_lists)
             VALUES ($1, $2, 'Shopee', 'Packed', '2026-07-04T05:00:00Z', '2026-07-04T05:00:00Z', 1,
                     $3::timestamptz, $4::timestamptz, $5::timestamptz, $6::jsonb)")
            .bind(format!("{prefix}{suffix}"))
            .bind(format!("{prefix}O{suffix}"))
            .bind(checked).bind(packed).bind(shipped).bind(upl)
            .execute(&pool).await.unwrap();
    }

    let after = get(ep).await;
    let d = |k: &str| {
        after["byStage"][k].as_i64().unwrap() - before["byStage"][k].as_i64().unwrap()
    };
    assert_eq!(after["total"].as_i64().unwrap() - before["total"].as_i64().unwrap(), 4);
    assert_eq!(d("submitted"), 1);
    assert_eq!(d("qcHold"), 1);
    assert_eq!(d("qcPassed"), 1);
    assert_eq!(d("packed"), 1);
    assert!(after["oldestCreatedAt"].as_str().is_some());

    cleanup(&pool, prefix).await;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test backlog_summary_counts_by_stage -- --nocapture`
Expected: FAIL with 404-shaped JSON error (route doesn't exist).

- [ ] **Step 3: Implement**

In `src/api/dashboard.rs` (near `SummaryQuery`):

```rust
use super::packing_list::BACKLOG_WATERMARK;

#[derive(Debug, Deserialize)]
pub struct BacklogQuery {
    /// Start of today (Asia/Bangkok), supplied by the FE via dateWindow.ts.
    pub cutoff: DateTime<Utc>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BacklogByStage {
    pub submitted: i64,
    pub qc_hold: i64,
    pub qc_passed: i64,
    pub packed: i64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BacklogSummary {
    pub total: i64,
    pub by_stage: BacklogByStage,
    pub oldest_created_at: Option<DateTime<Utc>>,
}

/// Open Backlog headline (spec 2026-07-14 §B.8): as-of-now, event-time,
/// same membership predicate as the `backlog` itemType arm.
pub async fn backlog(
    State(pool): State<PgPool>,
    Query(q): Query<BacklogQuery>,
) -> Result<Json<BacklogSummary>, AppError> {
    let row: (i64, i64, i64, i64, i64, Option<DateTime<Utc>>) = sqlx::query_as(
        "SELECT COUNT(*)::bigint,
                COUNT(*) FILTER (WHERE checked_at IS NULL AND packed_at IS NULL)::bigint,
                COUNT(*) FILTER (WHERE packed_at IS NULL AND checked_at IS NOT NULL AND all_items_cleared IS NOT TRUE)::bigint,
                COUNT(*) FILTER (WHERE packed_at IS NULL AND checked_at IS NOT NULL AND all_items_cleared IS TRUE)::bigint,
                COUNT(*) FILTER (WHERE packed_at IS NOT NULL)::bigint,
                MIN(created_at)
         FROM packing_lists
         WHERE created_at >= $1::timestamptz AND created_at < $2
           AND shipped_at IS NULL AND returned_at IS NULL
           AND order_status IS DISTINCT FROM 'Cancelled'
           AND packing_status IS DISTINCT FROM 'Cancelled'")
        .bind(BACKLOG_WATERMARK)
        .bind(q.cutoff)
        .fetch_one(&pool)
        .await?;

    Ok(Json(BacklogSummary {
        total: row.0,
        by_stage: BacklogByStage { submitted: row.1, qc_hold: row.2, qc_passed: row.3, packed: row.4 },
        oldest_created_at: row.5,
    }))
}
```

In `src/api/mod.rs` next to the other dashboard routes (~line 67):

```rust
        .route("/dashboard/backlog", get(dashboard::backlog))
```

- [ ] **Step 4: Run tests**

Run: `cargo test backlog_summary_counts_by_stage`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/api/dashboard.rs src/api/mod.rs tests/dashboard_api.rs
git commit -m "feat(dashboard): GET /dashboard/backlog stage summary"
```

### Task 5: ("backlog","cancel") resolve arm

**Files:**
- Modify: `src/api/dashboard.rs` (`resolve_alert` match, after the `("stale","cancel")` arm ~line 888)
- Test: `tests/dashboard_api.rs` (append)

**Interfaces:**
- Consumes: existing `POST /dashboard/alerts/{type}/resolve` with `ResolveRequest { tracking_numbers, action, operator, note }`; the loop-tail `workflow_events` INSERT and `resolved` counter run unchanged for this arm.
- Produces: action `("backlog","cancel")` — Task 12's dialog POSTs `/dashboard/alerts/backlog/resolve` `{ trackingNumbers, action: "cancel", operator, note }`.

**Context:** four `resolve_*` tests in this file FAIL AT BASE — the new test must pass by name; do not touch the failing ones.

- [ ] **Step 1: Write the failing test**

Append to `tests/dashboard_api.rs`:

```rust
/// Spec 2026-07-14 §B.12: backlog cancel stamps order_status='Cancelled'
/// across the WHOLE order (import-sweep parity), leaves packing_status
/// alone, and skips parcels that shipped between render and click.
#[tokio::test]
async fn resolve_backlog_cancel_cascades_order_status_only() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_rslv_bklg_";
    cleanup(&pool, prefix).await;

    // Two parcels of ONE order: A open (Packed), B already Shipped.
    let tn_a = format!("{prefix}A");
    let tn_b = format!("{prefix}B");
    let order = format!("{prefix}ORD");
    insert_packing_list(&pool, &tn_a, &order, "Shopee", Some("Packed"), Utc::now() - Duration::days(3)).await;
    insert_packing_list(&pool, &tn_b, &order, "Shopee", Some("Shipped"), Utc::now() - Duration::days(3)).await;
    sqlx::query("UPDATE packing_lists SET shipped_at = NOW() WHERE tracking_number = $1")
        .bind(&tn_b).execute(&pool).await.unwrap();

    let client = reqwest::Client::new();
    let res = client.post(format!("{url}/dashboard/alerts/backlog/resolve"))
        .json(&serde_json::json!({
            "trackingNumbers": [tn_a],
            "action": "cancel",
            "operator": "Keng",
            "note": "cancelled on Shopee"
        }))
        .send().await.unwrap();
    assert_eq!(res.status(), 200);
    assert_eq!(res.json::<Value>().await.unwrap()["resolved"].as_i64().unwrap(), 1);

    // Whole order stamped, packing_status untouched on both parcels.
    let rows: Vec<(String, Option<String>, Option<String>)> = sqlx::query_as(
        "SELECT tracking_number, order_status, packing_status FROM packing_lists WHERE order_number = $1 ORDER BY tracking_number")
        .bind(&order).fetch_all(&pool).await.unwrap();
    assert_eq!(rows.len(), 2);
    for (tn, order_status, packing_status) in &rows {
        assert_eq!(order_status.as_deref(), Some("Cancelled"), "{tn}");
        assert_ne!(packing_status.as_deref(), Some("Cancelled"), "{tn}: packing_status must be untouched");
    }

    // Audit row for the selected parcel.
    let wf: i64 = sqlx::query_scalar(
        "SELECT COUNT(*) FROM workflow_events WHERE tracking_number = $1 AND workflow_name = 'AlertResolve' AND from_state = 'backlog' AND to_state = 'cancel'")
        .bind(&tn_a).fetch_one(&pool).await.unwrap();
    assert_eq!(wf, 1);

    // Guard: selecting the SHIPPED parcel resolves nothing and logs nothing.
    let res = client.post(format!("{url}/dashboard/alerts/backlog/resolve"))
        .json(&serde_json::json!({ "trackingNumbers": [tn_b], "action": "cancel" }))
        .send().await.unwrap();
    assert_eq!(res.json::<Value>().await.unwrap()["resolved"].as_i64().unwrap(), 0);

    cleanup(&pool, prefix).await;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test resolve_backlog_cancel_cascades_order_status_only -- --nocapture`
Expected: FAIL — `invalid action 'cancel' for alert type 'backlog'` (400).

- [ ] **Step 3: Implement**

In `resolve_alert`, after the `("stale", "cancel")` arm:

```rust
            ("backlog", "cancel") => {
                // Spec 2026-07-14 §B.12: record a platform cancellation found
                // manually. order_status only — packing_status keeps whatever
                // stage the parcel reached — stamped across the WHOLE order to
                // mirror the import cancel sweep. The subquery's guard makes it
                // return NULL (=> 0 rows) when the selected parcel shipped or
                // returned between render and click: skip without an event.
                let result = sqlx::query(
                    "UPDATE packing_lists SET order_status = 'Cancelled', updated_at = NOW()
                     WHERE order_number = (SELECT order_number FROM packing_lists
                                            WHERE tracking_number = $1
                                              AND packing_status NOT IN ('Shipped', 'Returned'))")
                    .bind(tn).execute(&mut *tx).await?;
                if result.rows_affected() == 0 {
                    continue;
                }
            }
```

- [ ] **Step 4: Run tests**

Run: `cargo test resolve_backlog_cancel_cascades_order_status_only`
Expected: PASS.
Run: `cargo test --test dashboard_api --no-fail-fast 2>&1 | tail -20`
Expected: only the four pre-existing `resolve_*` failures (unchanged set).

- [ ] **Step 5: Commit**

```bash
git add src/api/dashboard.rs tests/dashboard_api.rs
git commit -m "feat(dashboard): backlog cancel resolve arm — order_status cascade"
```

---

## Phase 2 — Frontend (worktree `frontend/.worktrees/analytics-debug`)

Run every command from `frontend/.worktrees/analytics-debug`. Reminder: `app/lib/dateWindow.ts` carries the uncommitted DIAGNOSTIC(bkk) patch — import from it, never `git add` it.

### Task 6: Carried-in hook/section fixes (res.ok, abort, stale rows, gate)

**Files:**
- Modify: `app/hooks/useCarryoverParcels.ts`, `app/components/pipeline/PipelineSection.tsx`
- Test: `app/components/pipeline/PipelineSection.drilldown.test.tsx` (append + amend mock)

**Interfaces:**
- Consumes: existing hook shape.
- Produces: hook additionally returns `{ error: string | null, retry: () => void }`. PipelineSection renders the error row (copy `Couldn't load parcels — retry`) between the drill-down header and the table. Tasks 10/11 clone this exact error contract for the backlog.

- [ ] **Step 1: Write the failing test**

In `PipelineSection.drilldown.test.tsx`, FIRST amend `mockFetch` — the current mock lacks `ok`, which the new guard would read as falsy and break the existing tests:

```ts
function mockFetch() {
  const fn = vi.fn().mockResolvedValue({
    ok: true,
    json: () => Promise.resolve({ items: [], total: 218 }),
  });
  vi.stubGlobal("fetch", fn);
  return fn;
}
```

Then append:

```tsx
  it("surfaces a non-OK list response as an inline error row", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({
      ok: false,
      status: 500,
      json: () => Promise.resolve({}),
    }));
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);
    act(() =>
      root.render(
        <PipelineSection summary={summary} fromDate="2026-07-10" toDate="2026-07-10" />,
      ),
    );
    const parcelsPill = container.querySelector(
      '[data-stage="parcels"] [data-carryover-pill]',
    ) as HTMLElement;
    act(() => {
      parcelsPill.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    await flush();
    expect(container.textContent).toContain("Couldn't load parcels — retry");
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- PipelineSection.drilldown`
Expected: new test FAILS (no error copy rendered); the two existing tests still pass (mock now has `ok: true`).

- [ ] **Step 3: Implement the hook fixes**

In `useCarryoverParcels.ts`:

```ts
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const retry = useCallback(() => setAttempt((a) => a + 1), []);

  // New cohort = new list; keep limit/sort, restart paging, drop stale rows
  // (kills the brief flash of the previous cohort under the new header).
  useEffect(() => {
    // set-state-in-effect: deliberate cohort-scoped reset — one synchronous
    // clear per cohort change, before the fetch effect below runs.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setPage(0);
    setItems([]);
    setTotal(0);
  }, [cohort]);
```

and in the fetch effect (deps gain `attempt`):

```ts
    // set-state-in-effect: fetch lifecycle flags belong with the request they
    // describe; the abort guard below keeps them race-free.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);
    setError(null);
    fetch(cohortListUrl(API, cohort, from, to, sortBy, sortDir, limit, page * limit), {
      signal: controller.signal,
    })
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
      })
      .then((body) => {
        setItems(body.items ?? []);
        setTotal(body.total ?? 0);
      })
      .catch((err) => {
        if (err?.name !== "AbortError") {
          setItems([]);
          setTotal(0);
          setError("Couldn't load parcels — retry");
        }
      })
      .finally(() => {
        // A replaced request must not clear the replacement's loading flag.
        if (!controller.signal.aborted) setLoading(false);
      });
```

Return `{ items, total, page, setPage, limit, setLimit, sortBy, sortDir, onSort, loading, error, retry }`.

- [ ] **Step 4: Implement the section fixes**

In `PipelineSection.tsx`:
- Pill gate: `onCarryoverClick={co && fromDate && toDate ? () => toggleCohort(stage.id) : undefined}`
- Error row inside `<CarryoverDrilldown>` immediately before `<PackingTable …>`:

```tsx
          {cohortList.error && (
            <button
              type="button"
              data-drilldown-error
              onClick={cohortList.retry}
              className="mx-5 mb-2 rounded px-2 py-1 text-left text-[12px] font-medium text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/20"
            >
              {cohortList.error}
            </button>
          )}
```

- [ ] **Step 5: Run tests**

Run: `npm test -- PipelineSection.drilldown useCarryoverParcels && npm run lint`
Expected: PASS, no new lint errors.

- [ ] **Step 6: Commit**

```bash
git add app/hooks/useCarryoverParcels.ts app/components/pipeline/PipelineSection.tsx app/components/pipeline/PipelineSection.drilldown.test.tsx
git commit -m "fix(drilldown): res.ok guard with retry row, abort-aware loading, stale-row clear, pill date gate"
```

### Task 7: Drill-down cosmetics + videoEvents

**Files:**
- Modify: `app/types.ts` (CarryoverBreakdown), `app/components/pipeline/CarryoverDrilldown.tsx`, `app/components/PackingTable.tsx`, `app/components/pipeline/PipelineSection.tsx` (pass the new prop)
- Test: `app/components/pipeline/CarryoverDrilldown.test.tsx`, `app/components/PackingTable.columns.test.tsx`, plus the only other file that builds a `CarryoverBreakdown` literal: `PipelineSection.drilldown.test.tsx` (the `summary.carryover` fixture). Verified: `PipelineSection.inclusive.test.tsx` has no carryover block, `PipelineStage.carryover.test.tsx` uses plain pill numbers, and `pipelineMath.test.ts` builds no breakdown — none of those change. (Sanity: `npm test` under TS strict surfaces any literal the grep missed.)

**Interfaces:**
- Consumes: BE Task 2's `videoEvents` summary field.
- Produces: `CarryoverBreakdown.videoEvents: number` (required); `PackingTable` prop `highlightSubmittedAt?: boolean`; close button is `×` with `aria-label="Close"`. Task 11 reuses both PackingTable props.

- [ ] **Step 1: Write the failing tests**

In `CarryoverDrilldown.test.tsx`: update the fixture and expectations —

```ts
const co: CarryoverBreakdown = {
  totalOrders: 218,
  totalParcels: 218,
  byStatus: { packed: 66, shipped: 216, returned: 0, qcHold: 2, qcPassed: 55 },
  byPlatform: [],
  completedVideos: 98,
  videoEvents: 107,
  failedVideos: 0,
  dates: [],
};
```

- `cohortCount("video", co)` expectation becomes `98`.
- video def expectation becomes:

```ts
    expect(cohortDef("video", co)).toBe(
      "of the 218, these had a packing video completed in this period · 107 videos",
    );
```

- append a close-control test:

```ts
  it("close control is a bare × with an accessible name", () => {
    const { container } = render("all");
    const close = container.querySelector("[data-drilldown-close]") as HTMLElement;
    expect(close.textContent?.trim()).toBe("×");
    expect(close.getAttribute("aria-label")).toBe("Close");
  });
```

In `PackingTable.columns.test.tsx` append:

```tsx
  it("highlightSubmittedAt colours the Submitted At cell amber", () => {
    const markup = renderToStaticMarkup(
      <PackingTable
        items={[item]} total={1} page={0} limit={20} fromDate="2026-01-01"
        onPage={() => {}} onLimitChange={() => {}} onOpenModal={() => {}} onCloseModal={() => {}}
        selectedDetail={null} selectedVideos={[]} modalLoading={false}
        sortBy="createdAt" sortDir="desc" onSort={() => {}}
        highlightSubmittedAt
      />,
    );
    expect(markup).toContain("text-amber-700");
  });
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `npm test -- CarryoverDrilldown PackingTable.columns`
Expected: FAIL — TS error on `videoEvents` (unknown field) / def string mismatch / no amber class.

- [ ] **Step 3: Implement**

`app/types.ts` — inside `CarryoverBreakdown` after `completedVideos`:

```ts
  /** Raw completed-video rows (re-records count twice); completedVideos counts distinct parcels. */
  videoEvents: number;
```

`CarryoverDrilldown.tsx`:
- video def: `` return `of the ${total}, these had a packing video completed in this period · ${co.videoEvents.toLocaleString()} videos`; ``
- close button:

```tsx
          <button
            type="button"
            data-drilldown-close
            aria-label="Close"
            onClick={onClose}
            className="ml-auto rounded px-3 py-1 text-2xl font-semibold leading-none text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            ×
          </button>
```

`PackingTable.tsx`:
- `Props` gains `highlightSubmittedAt?: boolean;` (destructure it).
- Submitted At `<td>` (line ~327):

```tsx
                  <td className={`px-3 py-2 ${highlightSubmittedAt ? "font-semibold text-amber-700 dark:text-amber-400" : "text-muted-foreground"} ${COL_VIS.createdAt}`}>{fmt(item.createdAt)}</td>
```

`PipelineSection.tsx` — the drill-down `<PackingTable>` instance gains `highlightSubmittedAt`.

Fixture sweep: add `videoEvents: 107` (and set `completedVideos: 98`) in the two `CarryoverBreakdown` literals — `CarryoverDrilldown.test.tsx` and `PipelineSection.drilldown.test.tsx`'s `summary.carryover`. The top-level `Summary` type does NOT gain the field.

- [ ] **Step 4: Run tests**

Run: `npm test && npm run lint`
Expected: full suite PASS (TS strict forces every missed fixture to surface here).

- [ ] **Step 5: Commit**

```bash
git add app/types.ts app/components/pipeline/CarryoverDrilldown.tsx app/components/pipeline/CarryoverDrilldown.test.tsx app/components/PackingTable.tsx app/components/PackingTable.columns.test.tsx app/components/pipeline/PipelineSection.tsx app/components/pipeline/PipelineSection.drilldown.test.tsx
git commit -m "feat(drilldown): bare × close, amber Submitted At, video cohort = distinct parcels + videoEvents def"
```

### Task 8: CohortFilters — shipping + status selects on the drill-down table

**Files:**
- Create: `app/components/pipeline/CohortFilters.tsx`
- Modify: `app/hooks/useCarryoverParcels.ts` (params + availableShippingOptions), `app/components/pipeline/CarryoverDrilldown.tsx` (render slot), `app/components/pipeline/PipelineSection.tsx` (wire)
- Test: `app/hooks/useCarryoverParcels.test.ts`, `app/components/pipeline/CohortFilters.test.tsx` (new)

**Interfaces:**
- Consumes: list endpoint params `shippingOption` and `status` (verbatim `StatusFilter` values — same convention as `useDashboard.fetchList`), `availableShippingOptions` from the list response.
- Produces:
  - `cohortListUrl(api, cohort, fromIso, toIso, sortBy, sortDir, limit, offset, shippingOption = "all", status = "all")` — the two NEW params take `"all"` defaults so the pre-existing 8-arg test call (and any other caller) stays type-correct under TS strict (vitest doesn't typecheck; `npm run build` does). Appends each param only when not `"all"`.
  - Hook returns additionally `{ shippingOption, setShippingOption, status, setStatus, availableShippingOptions }`; both reset to `"all"` on cohort switch.
  - `<CohortFilters shippingOptions={string[]} shippingOption={string} onShippingOption={(v)=>void} status={string} onStatus={(v)=>void} />` with `data-cohort-shipping` / `data-cohort-status` hooks. Task 11 reuses it.
  - `CarryoverDrilldown` gains optional `filters?: React.ReactNode` rendered at the right end of the chips row.

- [ ] **Step 1: Write the failing tests**

Append to `useCarryoverParcels.test.ts`:

```ts
  it("appends shipping and status filters only when set", () => {
    const url = cohortListUrl(
      "http://api", "all",
      "2026-07-09T17:00:00.000Z", "2026-07-10T16:59:59.999Z",
      "createdAt", "asc", 10, 0, "Standard Delivery", "Shipped",
    );
    const params = new URL(url).searchParams;
    expect(params.get("shippingOption")).toBe("Standard Delivery");
    expect(params.get("status")).toBe("Shipped");

    const bare = cohortListUrl(
      "http://api", "all",
      "2026-07-09T17:00:00.000Z", "2026-07-10T16:59:59.999Z",
      "createdAt", "asc", 10, 0, "all", "all",
    );
    const bareParams = new URL(bare).searchParams;
    expect(bareParams.get("shippingOption")).toBeNull();
    expect(bareParams.get("status")).toBeNull();
  });
```

Create `app/components/pipeline/CohortFilters.test.tsx`:

```tsx
// @vitest-environment jsdom
import { act } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, it, vi } from "vitest";
import { CohortFilters } from "./CohortFilters";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

describe("CohortFilters", () => {
  it("renders both selects with All defaults and fires callbacks", () => {
    const onShippingOption = vi.fn();
    const onStatus = vi.fn();
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);
    act(() =>
      root.render(
        <CohortFilters
          shippingOptions={["Standard Delivery", "Express"]}
          shippingOption="all"
          onShippingOption={onShippingOption}
          status="all"
          onStatus={onStatus}
        />,
      ),
    );
    const shipping = container.querySelector("[data-cohort-shipping]") as HTMLSelectElement;
    const status = container.querySelector("[data-cohort-status]") as HTMLSelectElement;
    expect(shipping.options[0].textContent).toBe("All shipping");
    expect(status.options[0].textContent).toBe("All statuses");
    expect([...status.options].map((o) => o.value)).toContain("QC Hold");

    act(() => {
      shipping.value = "Express";
      shipping.dispatchEvent(new Event("change", { bubbles: true }));
    });
    expect(onShippingOption).toHaveBeenCalledWith("Express");
    act(() => {
      status.value = "Shipped";
      status.dispatchEvent(new Event("change", { bubbles: true }));
    });
    expect(onStatus).toHaveBeenCalledWith("Shipped");
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `npm test -- useCarryoverParcels CohortFilters`
Expected: FAIL — `cohortListUrl` arity, module `./CohortFilters` not found.

- [ ] **Step 3: Implement**

`CohortFilters.tsx`:

```tsx
"use client";

const STATUSES = [
  "To be packed", "Packing", "Packed", "QC Hold", "QC Passed", "Shipped", "Returned",
] as const;

interface CohortFiltersProps {
  shippingOptions: string[];
  shippingOption: string;
  onShippingOption: (v: string) => void;
  status: string;
  onStatus: (v: string) => void;
}

/** Table-scoped filters for cohort tables (carryover drill-down + backlog).
 *  Values ride the list endpoint's existing shippingOption/status params;
 *  they never re-query pills or chips (spec 2026-07-14 §A.3-A.4). */
export function CohortFilters({
  shippingOptions, shippingOption, onShippingOption, status, onStatus,
}: CohortFiltersProps) {
  const cls =
    "rounded border border-border bg-card px-2 py-1 text-xs text-foreground hover:border-foreground/30";
  return (
    <span className="ml-auto flex items-center gap-1.5">
      <select
        data-cohort-shipping
        value={shippingOption}
        onChange={(e) => onShippingOption(e.target.value)}
        className={cls}
      >
        <option value="all">All shipping</option>
        {shippingOptions.map((o) => (
          <option key={o} value={o}>{o}</option>
        ))}
      </select>
      <select
        data-cohort-status
        value={status}
        onChange={(e) => onStatus(e.target.value)}
        className={cls}
      >
        <option value="all">All statuses</option>
        {STATUSES.map((s) => (
          <option key={s} value={s}>{s}</option>
        ))}
      </select>
    </span>
  );
}
```

`useCarryoverParcels.ts`:
- `cohortListUrl` gains trailing params `shippingOption: string = "all", status: string = "all"` (defaults keep the existing 8-arg test call compiling); append `if (shippingOption !== "all") params.set("shippingOption", shippingOption);` and same for `status`.
- Hook state: `const [shippingOption, setShippingOption] = useState("all");`, `const [status, setStatus] = useState("all");`, `const [availableShippingOptions, setAvailableShippingOptions] = useState<string[]>([]);`
- Cohort-reset effect additionally does `setShippingOption("all"); setStatus("all");`
- Fetch effect: deps gain `shippingOption, status`; pass both to `cohortListUrl`; on success `setAvailableShippingOptions(body.availableShippingOptions ?? []);` and setting either filter also `setPage(0)` (wrap the setters: `const pickShipping = useCallback((v: string) => { setShippingOption(v); setPage(0); }, []);` — return the wrapped versions).
- Return the new fields.

`CarryoverDrilldown.tsx`: props gain `filters?: React.ReactNode;` rendered as the last child of the chips-row `<div>` (after the `COHORTS.map`).

`PipelineSection.tsx`:

```tsx
          filters={
            <CohortFilters
              shippingOptions={cohortList.availableShippingOptions}
              shippingOption={cohortList.shippingOption}
              onShippingOption={cohortList.setShippingOption}
              status={cohortList.status}
              onStatus={cohortList.setStatus}
            />
          }
```

- [ ] **Step 4: Run tests**

Run: `npm test && npm run lint`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/CohortFilters.tsx app/components/pipeline/CohortFilters.test.tsx app/hooks/useCarryoverParcels.ts app/hooks/useCarryoverParcels.test.ts app/components/pipeline/CarryoverDrilldown.tsx app/components/pipeline/PipelineSection.tsx
git commit -m "feat(drilldown): shipping + status table filters via shared CohortFilters"
```

### Task 9: PackingTable Age column + row selection props

**Files:**
- Modify: `app/components/PackingTable.tsx`
- Test: `app/components/PackingTable.columns.test.tsx`

**Interfaces:**
- Consumes: nothing new.
- Produces (Tasks 11/12 depend on these exact names):
  - `showAge?: boolean` — appends one trailing `Age` column, cell `"{age}d · idle {idle}d"` (`<1d` under one day), not sortable. Age basis `createdAt`; idle basis `max(createdAt, checkedAt, packedAt)`.
  - `selectable?: boolean; selected?: Set<number>; onToggleRow?: (packingId: number) => void; onToggleAll?: (pageIds: number[], checked: boolean) => void;` — leading checkbox column keyed by `packingId`, header checkbox = select-all-on-page, checkbox clicks do not open the row modal.

- [ ] **Step 1: Write the failing tests**

Append to `PackingTable.columns.test.tsx`:

```tsx
  it("showAge appends one Age column; selectable prepends one checkbox column", () => {
    const base = renderToStaticMarkup(
      <PackingTable
        items={[item]} total={1} page={0} limit={20} fromDate="2026-01-01"
        onPage={() => {}} onLimitChange={() => {}} onOpenModal={() => {}} onCloseModal={() => {}}
        selectedDetail={null} selectedVideos={[]} modalLoading={false}
        sortBy="createdAt" sortDir="desc" onSort={() => {}}
        showAge selectable selected={new Set()} onToggleRow={() => {}} onToggleAll={() => {}}
      />,
    );
    const thCount = (base.match(/<th[\s>]/g) ?? []).length;
    const tdCount = (base.match(/<td[\s>]/g) ?? []).length;
    expect(thCount).toBe(COLUMNS.length + 2);
    expect(tdCount).toBe(COLUMNS.length + 2);
    expect(base).toContain("Age");
    expect(base).toContain("idle");
    expect(base).toContain('type="checkbox"');
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- PackingTable.columns`
Expected: FAIL — unknown props / counts off by 2.

- [ ] **Step 3: Implement**

In `PackingTable.tsx`:

```ts
/** Whole days since `iso`; "<1d" under a day; "—" when null. */
function daysSince(iso: string | null | undefined): string {
  if (!iso) return "—";
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000);
  return days < 1 ? "<1d" : `${days}d`;
}

/** Latest stage event we hold on the row — the "idle since" basis. */
function lastEventIso(item: PackingItem): string | null {
  const times = [item.createdAt, item.checkedAt, item.packedAt]
    .filter((t): t is string => Boolean(t))
    .map((t) => new Date(t).getTime());
  return times.length ? new Date(Math.max(...times)).toISOString() : null;
}
```

Props gain `showAge?: boolean; selectable?: boolean; selected?: Set<number>; onToggleRow?: (packingId: number) => void; onToggleAll?: (pageIds: number[], checked: boolean) => void;`.

Header row: before the `COLUMNS.map` add

```tsx
                {selectable && (
                  <th className="w-8 px-3 py-2.5">
                    <input
                      type="checkbox"
                      aria-label="Select all on page"
                      checked={items.length > 0 && items.every((i) => selected?.has(i.packingId))}
                      onChange={(e) => onToggleAll?.(items.map((i) => i.packingId), e.target.checked)}
                    />
                  </th>
                )}
```

and after it

```tsx
                {showAge && <th className="px-3 py-2.5 text-left 2xl:w-[8%]">Age</th>}
```

Body row: first cell when `selectable`

```tsx
                  {selectable && (
                    <td className="px-3 py-2" onClick={(e) => e.stopPropagation()}>
                      <input
                        type="checkbox"
                        aria-label={`Select ${item.trackingNumber}`}
                        checked={selected?.has(item.packingId) ?? false}
                        onChange={() => onToggleRow?.(item.packingId)}
                      />
                    </td>
                  )}
```

last cell when `showAge` (after the `updatedAt` td)

```tsx
                  {showAge && (
                    <td className="whitespace-nowrap px-3 py-2 text-xs font-semibold text-rose-700 dark:text-rose-400">
                      {daysSince(item.createdAt)} · idle {daysSince(lastEventIso(item))}
                    </td>
                  )}
```

Also widen the empty-state `colSpan`: `colSpan={COLUMNS.length + (selectable ? 1 : 0) + (showAge ? 1 : 0)}`.

- [ ] **Step 4: Run tests**

Run: `npm test -- PackingTable.columns && npm run lint`
Expected: PASS (both the old count test and the new one).

- [ ] **Step 5: Commit**

```bash
git add app/components/PackingTable.tsx app/components/PackingTable.columns.test.tsx
git commit -m "feat(table): opt-in Age/idle column and row-selection checkboxes"
```

### Task 10: Backlog hooks + types

**Files:**
- Modify: `app/types.ts`
- Create: `app/hooks/useBacklog.ts`
- Test: `app/hooks/useBacklog.test.ts`

**Interfaces:**
- Consumes: BE Tasks 3/4 wire vocabulary; `dateRange`, `todayStr` from `../lib/dateWindow` (import only — file stays uncommitted); list response `availableShippingOptions`.
- Produces (Task 11 depends on these exact names):

```ts
export type BacklogStage = "all" | "submitted" | "qc-hold" | "qc-passed" | "packed";
export interface BacklogByStage { submitted: number; qcHold: number; qcPassed: number; packed: number; }
export interface BacklogSummary { total: number; byStage: BacklogByStage; oldestCreatedAt: string | null; }
```

  - `BACKLOG_ITEM_TYPE: Record<BacklogStage, string>` → `backlog` / `backlog-submitted` / `backlog-qc-hold` / `backlog-qc-passed` / `backlog-packed`
  - `backlogSummaryUrl(api: string, cutoffIso: string): string` → `{api}/dashboard/backlog?cutoff={enc}`
  - `backlogListUrl(api, stage, cutoffIso, sortBy, sortDir, limit, offset, shippingOption, status)` — sends `from=cutoffIso`, `to=cutoffIso`, `itemType`, sort/paging, optional filters (the backlog arms only read `from`; `to` rides along harmlessly).
  - `useBacklogSummary()` → `{ summary: BacklogSummary | null, refetch: () => void }` (fetches on mount with `cutoff = dateRange(todayStr(), todayStr()).from`).
  - `useBacklogParcels(stage: BacklogStage | null)` → `{ items, total, page, setPage, limit, setLimit, sortBy, sortDir, onSort, loading, error, retry, shippingOption, setShippingOption, status, setStatus, availableShippingOptions }`, default sort `createdAt asc`, inert while `stage` is null, resets paging/filters and clears rows on stage switch, res.ok → error `Couldn't load parcels — retry`, abort-aware `setLoading`. `retry()` also serves as the post-cancel list refresh (Task 12 calls `list.retry()`).

- [ ] **Step 1: Write the failing test**

`app/hooks/useBacklog.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { BACKLOG_ITEM_TYPE, backlogListUrl, backlogSummaryUrl } from "./useBacklog";

describe("BACKLOG_ITEM_TYPE", () => {
  it("maps every stage to its wire itemType", () => {
    expect(BACKLOG_ITEM_TYPE).toEqual({
      all: "backlog",
      submitted: "backlog-submitted",
      "qc-hold": "backlog-qc-hold",
      "qc-passed": "backlog-qc-passed",
      packed: "backlog-packed",
    });
  });
});

describe("backlogSummaryUrl", () => {
  it("targets /dashboard/backlog with the cutoff", () => {
    expect(backlogSummaryUrl("http://api", "2026-07-13T17:00:00.000Z")).toBe(
      "http://api/dashboard/backlog?cutoff=2026-07-13T17%3A00%3A00.000Z",
    );
  });
});

describe("backlogListUrl", () => {
  it("builds the stage list URL with cutoff-as-from, sort, paging and filters", () => {
    const url = backlogListUrl(
      "http://api", "qc-hold", "2026-07-13T17:00:00.000Z",
      "createdAt", "asc", 10, 0, "Express", "Packed",
    );
    const params = new URL(url).searchParams;
    expect(params.get("itemType")).toBe("backlog-qc-hold");
    expect(params.get("from")).toBe("2026-07-13T17:00:00.000Z");
    expect(params.get("shippingOption")).toBe("Express");
    expect(params.get("status")).toBe("Packed");
    expect(params.get("sortBy")).toBe("createdAt");
    expect(params.get("limit")).toBe("10");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- useBacklog`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

`app/types.ts` (near `CarryoverCohort`):

```ts
/** Open Backlog stage chips (spec 2026-07-14 §B.7). */
export type BacklogStage = "all" | "submitted" | "qc-hold" | "qc-passed" | "packed";

export interface BacklogByStage {
  submitted: number;
  qcHold: number;
  qcPassed: number;
  packed: number;
}

export interface BacklogSummary {
  total: number;
  byStage: BacklogByStage;
  oldestCreatedAt: string | null;
}
```

`app/hooks/useBacklog.ts` — complete file:

```ts
"use client";

import { useCallback, useEffect, useState } from "react";
import { dateRange, todayStr } from "../lib/dateWindow";
import { BacklogStage, BacklogSummary, PackingItem } from "../types";

const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080";

/** Wire itemType per backlog stage — must match the backend match arms verbatim. */
export const BACKLOG_ITEM_TYPE: Record<BacklogStage, string> = {
  all: "backlog",
  submitted: "backlog-submitted",
  "qc-hold": "backlog-qc-hold",
  "qc-passed": "backlog-qc-passed",
  packed: "backlog-packed",
};

export function backlogSummaryUrl(api: string, cutoffIso: string): string {
  return `${api}/dashboard/backlog?cutoff=${encodeURIComponent(cutoffIso)}`;
}

export function backlogListUrl(
  api: string,
  stage: BacklogStage,
  cutoffIso: string,
  sortBy: string,
  sortDir: "asc" | "desc",
  limit: number,
  offset: number,
  shippingOption: string,
  status: string,
): string {
  const params = new URLSearchParams();
  // The backlog arms read `from` as the start-of-today cutoff; `to` rides
  // along unused (the endpoint requires a well-formed window).
  params.set("from", cutoffIso);
  params.set("to", cutoffIso);
  params.set("itemType", BACKLOG_ITEM_TYPE[stage]);
  if (shippingOption !== "all") params.set("shippingOption", shippingOption);
  if (status !== "all") params.set("status", status);
  params.set("sortBy", sortBy);
  params.set("sortDir", sortDir);
  params.set("limit", String(limit));
  params.set("offset", String(offset));
  return `${api}/packing-lists/list?${params.toString()}`;
}

/** Headline + stage chip counts. As-of-now: cutoff = start of today (Bangkok). */
export function useBacklogSummary() {
  const [summary, setSummary] = useState<BacklogSummary | null>(null);
  const [attempt, setAttempt] = useState(0);
  const refetch = useCallback(() => setAttempt((a) => a + 1), []);

  useEffect(() => {
    const cutoff = dateRange(todayStr(), todayStr()).from;
    const controller = new AbortController();
    fetch(backlogSummaryUrl(API, cutoff), { signal: controller.signal })
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
      })
      .then((body) => setSummary(body))
      .catch((err) => {
        if (err?.name !== "AbortError") setSummary(null);
      });
    return () => controller.abort();
  }, [attempt]);

  return { summary, refetch };
}

/**
 * One backlog stage's parcels for the expanded panel table.
 * Inert while `stage` is null (panel collapsed).
 * Default sort: Submitted At ascending — oldest backlog first.
 */
export function useBacklogParcels(stage: BacklogStage | null) {
  const [items, setItems] = useState<PackingItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(0);
  const [limit, setLimit] = useState(10);
  const [sortBy, setSortBy] = useState("createdAt");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [shippingOption, setShippingOptionState] = useState("all");
  const [status, setStatusState] = useState("all");
  const [availableShippingOptions, setAvailableShippingOptions] = useState<string[]>([]);

  /** Doubles as the post-cancel refresh (Task 12 calls list.retry()). */
  const retry = useCallback(() => setAttempt((a) => a + 1), []);

  const setShippingOption = useCallback((v: string) => {
    setShippingOptionState(v);
    setPage(0);
  }, []);
  const setStatus = useCallback((v: string) => {
    setStatusState(v);
    setPage(0);
  }, []);

  // New stage = new list; keep limit/sort, restart paging + filters, drop
  // stale rows so the previous stage never flashes under the new chip.
  useEffect(() => {
    // set-state-in-effect: deliberate stage-scoped reset — one synchronous
    // clear per stage change, before the fetch effect below runs.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setPage(0);
    setItems([]);
    setTotal(0);
    setShippingOptionState("all");
    setStatusState("all");
  }, [stage]);

  const onSort = useCallback((col: string) => {
    setSortDir((d) => (col === sortBy ? (d === "asc" ? "desc" : "asc") : "asc"));
    setSortBy(col);
    setPage(0);
  }, [sortBy]);

  useEffect(() => {
    if (!stage) return;
    const cutoff = dateRange(todayStr(), todayStr()).from;
    const controller = new AbortController();
    // set-state-in-effect: fetch lifecycle flags belong with the request they
    // describe; the abort guard below keeps them race-free.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);
    setError(null);
    fetch(backlogListUrl(API, stage, cutoff, sortBy, sortDir, limit, page * limit, shippingOption, status), {
      signal: controller.signal,
    })
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
      })
      .then((body) => {
        setItems(body.items ?? []);
        setTotal(body.total ?? 0);
        setAvailableShippingOptions(body.availableShippingOptions ?? []);
      })
      .catch((err) => {
        if (err?.name !== "AbortError") {
          setItems([]);
          setTotal(0);
          setError("Couldn't load parcels — retry");
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [stage, sortBy, sortDir, limit, page, shippingOption, status, attempt]);

  return {
    items, total, page, setPage, limit, setLimit, sortBy, sortDir, onSort,
    loading, error, retry, shippingOption, setShippingOption, status, setStatus,
    availableShippingOptions,
  };
}
```

- [ ] **Step 4: Run tests**

Run: `npm test -- useBacklog && npm run lint`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/types.ts app/hooks/useBacklog.ts app/hooks/useBacklog.test.ts
git commit -m "feat(backlog): summary + parcels hooks with cutoff window"
```

### Task 11: BacklogSection — collapsed line, chips, table

**Files:**
- Create: `app/components/pipeline/BacklogSection.tsx`
- Modify: `app/components/Dashboard.tsx` (mount between `PipelineSection` and the FilterBar card)
- Test: `app/components/pipeline/BacklogSection.test.tsx` (new)

**Interfaces:**
- Consumes: Task 10 hooks, Task 9 table props, Task 8 `CohortFilters`, Task 7 `highlightSubmittedAt`.
- Produces: `<BacklogSection onOpenParcel={(tn) => void} operators={Operator[]} />`. Cancel wiring lands in Task 12 — this task renders selection checkboxes but the bulk bar/dialog come next.

**Copy (verbatim, from Global Constraints):** eyebrow `Open backlog`; summary tail `parcels not yet shipped · submitted before today · oldest {d Mmm yyyy}`; chips `All · {n}` / `Submitted · {n}` / `QC Hold · {n}` / `QC Passed · {n}` / `Packed · {n}`; footer `backlog since 3 Jul 2026 (Shipping go-live) · cancelled orders excluded · independent of the date filter above`; heading `Backlog parcels`.

- [ ] **Step 1: Write the failing test**

`app/components/pipeline/BacklogSection.test.tsx`:

```tsx
// @vitest-environment jsdom
import { act } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { BacklogSection } from "./BacklogSection";

vi.mock("next/image", () => ({
  default: (props: { alt?: string }) => <span data-mock-image>{props.alt}</span>,
}));

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const summaryBody = {
  total: 1660,
  byStage: { submitted: 95, qcHold: 4, qcPassed: 33, packed: 1525 },
  oldestCreatedAt: "2026-07-02T18:00:00Z",
};

function mockFetch() {
  const fn = vi.fn((input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes("/dashboard/backlog")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(summaryBody) });
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve({ items: [], total: 0, availableShippingOptions: [] }) });
  });
  vi.stubGlobal("fetch", fn);
  return fn;
}

afterEach(() => vi.unstubAllGlobals());

async function flush() {
  await act(async () => {
    await Promise.resolve();
  });
}

describe("BacklogSection", () => {
  it("collapsed line shows the headline; expanding shows chips and fetches the stage list", async () => {
    const fetchFn = mockFetch();
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);
    act(() => root.render(<BacklogSection operators={[]} />));
    await flush();

    const text = container.textContent ?? "";
    expect(text).toContain("Open backlog");
    expect(text).toContain("1,660");
    expect(text).toContain("parcels not yet shipped · submitted before today");
    // Collapsed: no chips yet.
    expect(container.querySelector("[data-backlog-chip]")).toBeNull();

    const toggle = container.querySelector("[data-backlog-toggle]") as HTMLElement;
    act(() => {
      toggle.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    expect(container.textContent).toContain("QC Hold · 4");
    expect(container.textContent).toContain("backlog since 3 Jul 2026");

    const holdChip = container.querySelector('[data-backlog-chip="qc-hold"]') as HTMLElement;
    act(() => {
      holdChip.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    const listCalls = fetchFn.mock.calls.map((c) => String(c[0])).filter((u) => u.includes("itemType="));
    expect(listCalls.some((u) => u.includes("itemType=backlog-qc-hold"))).toBe(true);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- BacklogSection`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

`BacklogSection.tsx` structure (rose family everywhere amber would be; every colour has its `dark:` twin):

```tsx
"use client";

import { useState } from "react";
import { BacklogStage, Operator } from "../../types";
import { useBacklogParcels, useBacklogSummary } from "../../hooks/useBacklog";
import { todayStr } from "../../lib/dateWindow";
import { PackingTable } from "../PackingTable";
import { CohortFilters } from "./CohortFilters";

const CHIP_LABEL: Record<BacklogStage, string> = {
  all: "All", submitted: "Submitted", "qc-hold": "QC Hold", "qc-passed": "QC Passed", packed: "Packed",
};
const STAGES: BacklogStage[] = ["all", "submitted", "qc-hold", "qc-passed", "packed"];

interface BacklogSectionProps {
  operators: Operator[];
  onOpenParcel?: (trackingNumber: string) => void;
}

export function BacklogSection({ operators, onOpenParcel }: BacklogSectionProps) {
  const { summary, refetch } = useBacklogSummary();
  const [expanded, setExpanded] = useState(false);
  const [stage, setStage] = useState<BacklogStage>("all");
  const list = useBacklogParcels(expanded ? stage : null);
  // Task 12 adds: selection state + bulk bar + dialog (operators/refetch used there).

  function chipCount(s: BacklogStage): number {
    if (!summary) return 0;
    if (s === "all") return summary.total;
    const key = { submitted: "submitted", "qc-hold": "qcHold", "qc-passed": "qcPassed", packed: "packed" }[s] as keyof typeof summary.byStage;
    return summary.byStage[key];
  }
  // …
}
```

Collapsed header (always rendered; whole row is the toggle):

```tsx
    <section
      aria-label="Open backlog"
      className="mt-2 mb-3 overflow-hidden rounded-md border border-border border-l-[3px] border-l-[#E11D48] bg-card shadow-sm dark:border-l-rose-500 2xl:mb-4"
    >
      <button
        type="button"
        data-backlog-toggle
        aria-expanded={expanded}
        onClick={() => setExpanded((e) => !e)}
        className="flex w-full flex-wrap items-baseline gap-3 px-5 py-3 text-left hover:bg-lifted"
      >
        <span className="text-[11px] font-extrabold uppercase tracking-[0.08em] text-[#BE123C] dark:text-rose-300">
          Open backlog
        </span>
        <span className="text-2xl font-bold leading-none tabular-nums text-[#E11D48] dark:text-rose-400">
          {summary ? summary.total.toLocaleString() : "—"}
        </span>
        <span className="text-[13px] text-muted-foreground">
          parcels not yet shipped · submitted before today
          {summary?.oldestCreatedAt ? ` · oldest ${new Date(summary.oldestCreatedAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })}` : ""}
        </span>
        <span aria-hidden className="ml-auto text-sm text-muted-foreground">{expanded ? "▴" : "▾"}</span>
      </button>
```

Expanded body — chip row + filters (error row `data-backlog-error` follows, same pattern as Task 6's):

```tsx
      {expanded && (
        <>
          <div className="flex flex-wrap items-center gap-1.5 px-5 pb-3 pt-1">
            {STAGES.map((s) => {
              const active = s === stage;
              return (
                <button
                  key={s}
                  type="button"
                  data-backlog-chip={s}
                  data-active={active}
                  onClick={() => setStage(s)}
                  className={`rounded-full border px-2.5 py-0.5 text-xs font-semibold tabular-nums transition-colors ${
                    active
                      ? "border-transparent bg-rose-100 text-rose-800 ring-2 ring-[#E11D48] dark:bg-rose-900/40 dark:text-rose-300 dark:ring-rose-400"
                      : "border-border bg-lifted text-muted-foreground hover:border-[#E11D48]/40 hover:text-foreground dark:hover:border-rose-400/40"
                  }`}
                >
                  {CHIP_LABEL[s]} · {chipCount(s).toLocaleString()}
                </button>
              );
            })}
            <CohortFilters
              shippingOptions={list.availableShippingOptions}
              shippingOption={list.shippingOption}
              onShippingOption={list.setShippingOption}
              status={list.status}
              onStatus={list.setStatus}
            />
          </div>
          {list.error && (
            <button
              type="button"
              data-backlog-error
              onClick={list.retry}
              className="mx-5 mb-2 rounded px-2 py-1 text-left text-[12px] font-medium text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/20"
            >
              {list.error}
            </button>
          )}
```

then

```tsx
          <div className="px-5 pb-3">
            <PackingTable
              heading="Backlog parcels"
              items={list.items}
              total={list.total}
              page={list.page}
              limit={list.limit}
              fromDate={todayStr()}
              onPage={list.setPage}
              onLimitChange={list.setLimit}
              onOpenModal={onOpenParcel ?? (() => {})}
              onCloseModal={() => {}}
              selectedDetail={null}
              selectedVideos={[]}
              modalLoading={false}
              sortBy={list.sortBy}
              sortDir={list.sortDir}
              onSort={list.onSort}
              highlightSubmittedAt
              showAge
            />
          </div>
          <div className="border-t border-border px-5 py-2 text-[11.5px] text-muted-foreground">
            backlog since 3 Jul 2026 (Shipping go-live) · cancelled orders excluded · independent of the date filter above
          </div>
```

`fromDate={todayStr()}` is REQUIRED, not cosmetic: `PackingTable` runs `dateRange(fromDate, fromDate)` unconditionally (line ~170) and `dateRange("")` throws `RangeError: Invalid time value` in BOTH the committed and the diagnostic body — an empty string would crash the section on expand. Import `todayStr` from `"../../lib/dateWindow"`. With today's boundary every backlog row (all pre-today) legitimately wears the amber Carryover row badge — correct, they are carryover by definition.

`Dashboard.tsx`: import and mount after `<PipelineSection … />`:

```tsx
          <BacklogSection operators={operators} onOpenParcel={openModal} />
```

- [ ] **Step 4: Run tests**

Run: `npm test -- BacklogSection && npm run lint`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/BacklogSection.tsx app/components/pipeline/BacklogSection.test.tsx app/components/Dashboard.tsx
git commit -m "feat(backlog): red-accent BacklogSection with stage chips and inline table"
```

### Task 12: Bulk cancel — selection, action bar, confirm dialog

**Files:**
- Modify: `app/components/pipeline/BacklogSection.tsx`
- Test: `app/components/pipeline/BacklogSection.cancel.test.tsx` (new)

**Interfaces:**
- Consumes: Task 9 selection props, Task 5's `POST /dashboard/alerts/backlog/resolve`, Task 10's `refetch` + `refetchList`, `operators: Operator[]` prop (dropdown label = `nickname ?? firstName`, value sent as the resolve `operator` string).
- Produces: complete cancel flow; selection survives paging (accumulated `Map<packingId, {trackingNumber, orderNumber}>`).

- [ ] **Step 1: Write the failing test**

`BacklogSection.cancel.test.tsx` (reuse Task 11's mockFetch/flush helpers inline; list mock now returns one row):

```tsx
// @vitest-environment jsdom
import { act } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Operator, PackingItem } from "../../types";
import { BacklogSection } from "./BacklogSection";

vi.mock("next/image", () => ({
  default: (props: { alt?: string }) => <span data-mock-image>{props.alt}</span>,
}));
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const row: PackingItem = {
  packingId: 7, trackingNumber: "TH777", orderNumber: "ORD-7", platform: "Shopee",
  packingStatus: "To be packed", totalItems: 1,
  createdAt: "2026-07-04T05:00:00Z", updatedAt: "2026-07-04T05:00:00Z",
  packedBy: null, packedByName: null, packedAt: null,
  checkedBy: null, checkedByName: null, checkedAt: null,
  latestVideoStatus: null, packingStationId: null, checkingStationId: null,
  packingStationName: null, checkingStationName: null, allItemsCleared: null,
  shippedBy: null, shippedByName: null, shippingStationId: null, shippingStationName: null,
  shippingOptions: null, invoicedAt: null,
};
const op: Operator = {
  id: 1, firstName: "Keng", middleName: null, lastName: null, nickname: null,
  staffCode: "K1", imageFilename: null, createdAt: null, active: true, startDate: null,
};

function mockFetch() {
  const fn = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes("/resolve")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ resolved: 1 }) });
    }
    if (url.includes("/dashboard/backlog")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve({
        total: 1, byStage: { submitted: 1, qcHold: 0, qcPassed: 0, packed: 0 }, oldestCreatedAt: row.createdAt,
      }) });
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve({ items: [row], total: 1, availableShippingOptions: [] }) });
  });
  vi.stubGlobal("fetch", fn);
  return fn;
}
afterEach(() => vi.unstubAllGlobals());
async function flush() {
  await act(async () => {
    await Promise.resolve();
  });
}

describe("BacklogSection cancel flow", () => {
  it("select → bulk bar → dialog → POST payload → selection cleared", async () => {
    const fetchFn = mockFetch();
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);
    act(() => root.render(<BacklogSection operators={[op]} />));
    await flush();
    act(() => {
      (container.querySelector("[data-backlog-toggle]") as HTMLElement)
        .dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    await flush();

    const checkbox = container.querySelector('tbody input[type="checkbox"]') as HTMLInputElement;
    expect(checkbox).not.toBeNull();
    act(() => {
      checkbox.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    expect(container.textContent).toContain("Cancel 1 parcels");

    act(() => {
      (container.querySelector("[data-backlog-cancel]") as HTMLElement)
        .dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    expect(container.textContent).toContain(
      "Cancelling marks the whole order as cancelled on every parcel, including any already shipped. This cannot be undone here.",
    );

    act(() => {
      (container.querySelector("[data-backlog-confirm]") as HTMLElement)
        .dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    await flush();
    await flush();

    const resolveCall = fetchFn.mock.calls.find((c) => String(c[0]).includes("/resolve"));
    expect(resolveCall).toBeTruthy();
    expect(String(resolveCall![0])).toContain("/dashboard/alerts/backlog/resolve");
    const body = JSON.parse(String((resolveCall![1] as RequestInit).body));
    expect(body.trackingNumbers).toEqual(["TH777"]);
    expect(body.action).toBe("cancel");
    expect(container.textContent).not.toContain("Cancel 1 parcels");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- BacklogSection.cancel`
Expected: FAIL — no checkbox / no bulk bar.

- [ ] **Step 3: Implement**

In `BacklogSection.tsx`:
- New state:

```ts
  const [selectedRows, setSelectedRows] = useState<Map<number, { trackingNumber: string; orderNumber: string }>>(new Map());
  const [dialogOpen, setDialogOpen] = useState(false);
  const [operator, setOperator] = useState("");
  const [note, setNote] = useState("");
```

  `onToggleRow` looks the row up in `list.items`; `onToggleAll(ids, checked)` adds/removes the current page's rows; `Clear selection` empties it; a stage/filter change also clears it (effect on `[stage, list.shippingOption, list.status]`).
- Pass to `PackingTable`: `selectable selected={new Set(selectedRows.keys())} onToggleRow={…} onToggleAll={…}`.
- Bulk bar between chips and table when `selectedRows.size > 0`:

```tsx
          <div className="mx-5 mb-2 flex items-center gap-3 rounded-md bg-rose-50 px-3 py-2 dark:bg-rose-900/20">
            <button
              type="button"
              data-backlog-cancel
              onClick={() => setDialogOpen(true)}
              className="rounded bg-[#E11D48] px-3 py-1 text-xs font-bold text-white hover:bg-rose-700 dark:bg-rose-600 dark:hover:bg-rose-500"
            >
              Cancel {selectedRows.size.toLocaleString()} parcels
            </button>
            <button
              type="button"
              onClick={() => setSelectedRows(new Map())}
              className="text-xs font-medium text-muted-foreground hover:text-foreground"
            >
              Clear selection
            </button>
          </div>
```

- Dialog (`dialogOpen` state; fixed overlay, no portal needed):

```tsx
      {dialogOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 dark:bg-black/60">
          <div role="dialog" aria-modal="true" aria-label="Cancel orders" className="w-[28rem] max-w-[90vw] rounded-xl bg-card p-5 shadow-xl">
            <h3 className="text-base font-semibold text-foreground">
              Cancel {selectedRows.size.toLocaleString()} parcels · {distinctOrders.toLocaleString()} orders
            </h3>
            <p className="mt-2 text-[13px] text-red-600 dark:text-red-400">
              Cancelling marks the whole order as cancelled on every parcel, including any already shipped. This cannot be undone here.
            </p>
            <label className="mt-3 block text-xs font-semibold text-muted-foreground">
              Operator
              <select data-cancel-operator value={operator} onChange={(e) => setOperator(e.target.value)}
                className="mt-1 w-full rounded border border-border bg-card px-2 py-1.5 text-sm text-foreground">
                <option value="">—</option>
                {operators.map((o) => (
                  <option key={o.id} value={o.nickname ?? o.firstName}>{o.nickname ?? o.firstName}</option>
                ))}
              </select>
            </label>
            <label className="mt-3 block text-xs font-semibold text-muted-foreground">
              Note (optional)
              <textarea data-cancel-note value={note} onChange={(e) => setNote(e.target.value)} rows={2}
                className="mt-1 w-full rounded border border-border bg-card px-2 py-1.5 text-sm text-foreground" />
            </label>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setDialogOpen(false)}
                className="rounded px-3 py-1.5 text-sm font-medium text-muted-foreground hover:bg-muted">
                Keep them
              </button>
              <button type="button" data-backlog-confirm onClick={confirmCancel}
                className="rounded bg-[#E11D48] px-3 py-1.5 text-sm font-bold text-white hover:bg-rose-700 dark:bg-rose-600 dark:hover:bg-rose-500">
                Cancel orders
              </button>
            </div>
          </div>
        </div>
      )}
```

- `distinctOrders = new Set([...selectedRows.values()].map((r) => r.orderNumber)).size`
- `confirmCancel`:

```ts
  async function confirmCancel() {
    const trackingNumbers = [...selectedRows.values()].map((r) => r.trackingNumber);
    try {
      const res = await fetch(`${API}/dashboard/alerts/backlog/resolve`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ trackingNumbers, action: "cancel", operator: operator || null, note: note || null }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setSelectedRows(new Map());
      setDialogOpen(false);
      setNote("");
      refetch();     // summary
      list.retry();  // table
    } catch {
      setDialogOpen(false);
      // surface through the existing error row
      list.retry();
    }
  }
```

(`API` constant mirrors the hooks: `const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080";`)

- [ ] **Step 4: Run tests**

Run: `npm test && npm run lint`
Expected: full suite PASS.

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/BacklogSection.tsx app/components/pipeline/BacklogSection.cancel.test.tsx
git commit -m "feat(backlog): checkbox multi-select with bulk cancel dialog"
```

---

## Phase 3 — Verification

### Task 13: Full suites + live smoke vs warehouse_snapshot

**Files:**
- Create: `.superpowers/sdd/backlog-t13-smoke-report.md` (FE worktree)

**Context:** dev stack should already be running (BE :8080 on `warehouse_snapshot`, FE :3000 — see the handoff doc; restart per memory `reference_local-dev-stack` if dead). The BE server must be REBUILT/restarted after Phase 1 (`cargo run` picks up the new arms). Browser automation via the playwright-cli skill (screenshots to `.playwright-cli/`).

- [ ] **Step 1: Full test suites**

BE: `cargo test --no-fail-fast 2>&1 | tail -30` — only the pre-existing failure set (Global Constraints) may fail.
FE: `npm test && npm run lint && npm run build` — all green.

- [ ] **Step 2: Restart dev backend on the new code, keep `warehouse_snapshot`**

Kill the old `cargo run` (pid in the previous session's scratchpad log), then from the BE worktree:
`DATABASE_URL=postgresql://warehouse_user:warehouse_user@localhost:5432/warehouse_snapshot cargo run` (nohup, log to scratchpad).

Index on the snapshot: do NOT `sqlx migrate run` against warehouse_snapshot — its `_sqlx_migrations` checksums mismatch the current files for ALL applied migrations (restored dump; sqlx would fail validation, and `--ignore-missing` does not bypass checksum checks). Create the index directly instead (perf-only, nothing reads the migration row):

```bash
PGPASSWORD=warehouse_user psql -h localhost -U warehouse_user -d warehouse_snapshot \
  -c "CREATE INDEX IF NOT EXISTS idx_packing_lists_open_backlog ON packing_lists (created_at) WHERE shipped_at IS NULL AND returned_at IS NULL;"
```

(warehouse_db_test checksums match the files, so Task 1's normal `sqlx migrate run --ignore-missing` there is fine.)

- [ ] **Step 3: Smoke checklist (record every number in the report)**

1. Dashboard loads, console clean.
2. Friday window (10 Jul): carryover video pill = video chip = video table total (expected 98) and video def line ends `· 107 videos`.
3. Drill-down: `×` control is bare and bigger; Submitted At column amber in the panel, normal in the main table.
4. Drill-down shipping select lists real options; picking one narrows table + total, pills/chips unchanged; status select narrows to e.g. `Shipped`; both reset on cohort switch.
5. Backlog collapsed line: headline ≈ 1.6k (record exact), oldest date shown, red accent.
6. Expand: chip counts sum to total (submitted + qcHold + qcPassed + packed == total — record all five).
7. Chip → table: `Packed` chip total == chip count; rows all pre-today; Age column renders `Nd · idle Nd`.
8. Parity probe (SQL vs API): `SELECT COUNT(*) FROM packing_lists WHERE created_at >= '2026-07-02T17:00:00Z' AND created_at < '<cutoff>' AND shipped_at IS NULL AND returned_at IS NULL AND order_status IS DISTINCT FROM 'Cancelled' AND packing_status IS DISTINCT FROM 'Cancelled'` == `/dashboard/backlog` total == `itemType=backlog` list total.
9. Cancel flow on a THROWAWAY row: insert a synthetic parcel (`SMOKE-BKLG-1`, created 2026-07-05, no events) via psql, refresh, select it, cancel with note; verify it vanishes from backlog, `order_status='Cancelled'` in DB, `workflow_events` row exists; then DELETE the synthetic rows (packing_lists + workflow_events).
10. Screenshots: collapsed backlog, expanded chips, filtered table, dialog.

- [ ] **Step 4: Write the report + commit**

Write `.superpowers/sdd/backlog-t13-smoke-report.md` with the checklist results and exact numbers.

```bash
git add .superpowers/sdd/backlog-t13-smoke-report.md
git commit -m "test: backlog + drilldown tweaks live smoke report"
```

### Task 14: Final whole-branch review

- [ ] Dispatch a fresh reviewer subagent over the full diff of BOTH worktrees since BE f809089 / FE 6b635ee (this plan's commits only), spec at hand, verdict format: Ready-to-merge YES/NO + Critical/Major/Minor findings. Verify each commit's parentage chains from the correct tips. Fix Criticals; record Minors in the ledger; do NOT push (user-gated).
