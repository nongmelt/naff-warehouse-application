# Leaderboard Ship-Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore Leaderboard/Live Leaderboard integrity after the Ship feature (event-time counting), add the unregistered-operator sidecar, the single-day picker, live-cache revalidation, and three bundled minor fixes — per `docs/specs/2026-07-15-leaderboard-ship-integrity.md`.

**Architecture:** Backend flips 8 `packing_status = 'Packed'` gates to `packed_at IS NOT NULL` in `src/api/leaderboard.rs` (drill/breakdown inherit via `key_filter`), adds an `unregistered` sidecar to `GET /leaderboard`, and fixes `holds_by_status` labels + `daily14` timezone. Frontend adds a `day` ShellPeriod with a Day chip on both pages, revision-stamped cache revalidation, the unregistered footer line, and the coverage one-token fix.

**Tech Stack:** Rust / Axum / SQLx (integration tests vs live Postgres) · Next.js 16 / React 19 / TypeScript strict · vitest.

## Global Constraints

- Work ONLY in the worktrees: backend `backend/.worktrees/leaderboard-ship-integrity` (base `6678c5e`), frontend `frontend/.worktrees/leaderboard-ship-integrity` (base `b1a10c5`); commits land on `fix/leaderboard-ship-integrity` in each submodule (branch created 2026-07-15 off the `feat/warehouse-invoice` tips). Never checkout-switch the main trees.
- Backend tests need a live migrated `DATABASE_URL` (e.g. `postgresql://warehouse_user:warehouse_user@localhost:5432/warehouse_db_test`); shared dev DB migrations need `sqlx migrate run --ignore-missing`. Builds do NOT need the DB (no compile-time macros, no `.sqlx`).
- Run backend suites with `cargo test --no-fail-fast`. Pre-existing failure on base that stays: 1 in `product_insights`. The 3 leaderboard failures are FIXED by Task 1.
- Event-time counting is immutable packed-event counting (ADR-0003 parity): later ship/return transitions never change tallies; Returned parcels still count. No status-set widening anywhere.
- No DB migration, no schema change, no MAUI change. Sidecar is additive (BE/FE independently deployable).
- The shared test DB contains organic unregistered work (`OP-1`) — sidecar tests must use far-past windows for exact totals/zero-state and assert fixture codes inside `codes[]` only for recent windows.
- UI copy for the sidecar line is exactly: `{parcels} parcels by {codes} unregistered codes — hidden from board`.
- The range control stays page-level: one `PeriodBar` per page above scopes/disciplines.

---

## Phase 1 — Backend (worktree `backend/.worktrees/leaderboard-ship-integrity`)

### Task 1: Repair the 3 pre-existing leaderboard test failures

**Files:**
- Modify: `tests/leaderboard_api.rs:218-253` (packing agg test), `:255-285` (QC agg test), `:339-365` (daily14 test)

**Interfaces:**
- Consumes: existing helpers `insert_packing_list`, `insert_packing_list_full`, `set_packed`, `cleanup` (all already in the file).
- Produces: three green tests whose fixtures are registered operators — the pattern Tasks 2–6 reuse.

**Context you must know:** These tests predate the registered-only `EXISTS operator_lists` filter (added in `6cf8fe8`) and never register their operators, so no row is ever built. Registration alone is impossible as-written: `operator_lists.staff_code` is `VARCHAR(15)` and the fixture codes are 17–19 chars. Fix = shorten codes + register + rewrite the two staff-code-fallback assertions. No production code changes in this task.

- [ ] **Step 1: Shorten the operator codes and register them**

In `leaderboard_packing_operators_aggregates_parcels_items_days` (:227):

```rust
    let op = "lb_pk_OP1".to_string(); // ≤15 chars: operator_lists.staff_code is VARCHAR(15)
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('PackOps', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(&op).execute(&pool).await.unwrap();
```

In `leaderboard_qc_operators_counts_cleared_and_hold` (:262):

```rust
    let chk = "lb_qc_QC1".to_string();
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('QcOps', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(&chk).execute(&pool).await.unwrap();
```

In `leaderboard_daily14_has_14_slots_and_counts_recent` (:346):

```rust
    let op = "lb_d14_OP1".to_string();
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('Daily14', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(&op).execute(&pool).await.unwrap();
```

(The tracking-number prefixes `test_lb_packops_` etc. and the `cleanup` calls stay unchanged — cleanup keys on tracking numbers, not staff codes.)

- [ ] **Step 2: Rewrite the fallback assertions in test 1**

Replace (`:248-250`):

```rust
    // no operator_lists row → name falls back to the staff_code
    assert_eq!(row["name"], op);
    assert_eq!(row["sub"], op);
```

with:

```rust
    // registered operator → name comes from operator_lists.first_name
    assert_eq!(row["name"], "PackOps");
    assert_eq!(row["sub"], op);
```

- [ ] **Step 3: Run the three tests**

Run: `cargo test --test leaderboard_api leaderboard_packing_operators_aggregates_parcels_items_days leaderboard_qc_operators_counts_cleared_and_hold leaderboard_daily14_has_14_slots_and_counts_recent -- --nocapture`
Expected: 3 passed. (If `sub` mismatches, read the `sub` construction in `operator_rows` and fix the assertion to the actual registered-operator value — the numeric assertions are the point of these tests.)

- [ ] **Step 4: Commit**

```bash
git add tests/leaderboard_api.rs
git commit -m "test(leaderboard): register fixture operators, fix pre-existing failures

The registered-only EXISTS filter (6cf8fe8) postdates these tests; codes
also exceeded operator_lists.staff_code VARCHAR(15)."
```

### Task 2: Event-time gates — flip all 8 `packing_status = 'Packed'` sites

**Files:**
- Modify: `src/api/leaderboard.rs:171` (operator agg), `:318`/`:323` (key_filter), `:434` (videos_map), `:469` (videoed_parcels_map), `:711`/`:721` (daily14_map), `:808` (station agg)
- Test: `tests/leaderboard_api.rs` (append)

**Interfaces:**
- Consumes: Task 1's registered-fixture pattern.
- Produces: packing aggregates that count by packed event regardless of later status — every later task builds on this contract.

**Context you must know:** This is the core drain fix. Replace each `packing_status = 'Packed'` predicate with `packed_at IS NOT NULL`; window bounds (`($1::timestamptz IS NULL OR pl.packed_at >= $1) …`) stay untouched. Sites `:318`/`:323` automatically fix `breakdown_map` and all three `GET /leaderboard/operator` queries. Do NOT touch any QC arm (none has a status gate) and do NOT touch the five `COALESCE(packed_at, created_at)` expressions (`:155, :639, :710, :720, :790`) — they are provably inert post-fix.

- [ ] **Step 1: Write the failing test — shipped and returned parcels still count**

Append to `tests/leaderboard_api.rs`:

```rust
// ── Ship-integrity: packed-event counting is immutable (spec 2026-07-15) ──

#[tokio::test]
async fn leaderboard_counts_shipped_and_returned_parcels_by_packed_event() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_ship_";
    cleanup(&pool, prefix).await;

    let now = Utc::now();
    let op = "lb_ship_OP1";
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('ShipInt', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(op).execute(&pool).await.unwrap();

    // three parcels packed today: one stays Packed, one is shipped, one is returned
    for (suffix, items) in [("A", 2), ("B", 3), ("C", 4)] {
        insert_packing_list(&pool, &format!("{prefix}{suffix}"), &format!("{prefix}O{suffix}"), "Shopee", Some("Packed"), now).await;
        set_packed(&pool, &format!("{prefix}{suffix}"), op, now, items).await;
    }
    sqlx::query("UPDATE packing_lists SET packing_status = 'Shipped', shipped_at = $2 WHERE tracking_number = $1")
        .bind(format!("{prefix}B")).bind(now).execute(&pool).await.unwrap();
    sqlx::query("UPDATE packing_lists SET packing_status = 'Returned', shipped_at = $2, returned_at = $2 WHERE tracking_number = $1")
        .bind(format!("{prefix}C")).bind(now).execute(&pool).await.unwrap();

    let from = (now - Duration::days(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let to = (now + Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let res = reqwest::get(format!("{url}/leaderboard?discipline=packing&scope=operators&from={from}&to={to}"))
        .await.unwrap();
    let body: Value = res.json().await.unwrap();
    let row = body["rows"].as_array().unwrap().iter()
        .find(|r| r["id"] == op).expect("operator row present");
    // all three parcels count: ship/return transitions never change packing tallies
    assert_eq!(row["parcels"].as_i64().unwrap(), 3);
    assert_eq!(row["items"].as_i64().unwrap(), 9);

    cleanup(&pool, prefix).await;
}

#[tokio::test]
async fn leaderboard_station_scope_counts_shipped_parcels() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_shipst_";
    cleanup(&pool, prefix).await;

    let now = Utc::now();
    let station_id = insert_station(&pool, &format!("{prefix}S1")).await;
    let id = insert_packing_list(&pool, &format!("{prefix}A"), &format!("{prefix}OA"), "Shopee", Some("Packed"), now).await;
    sqlx::query("UPDATE packing_lists SET packed_by = 'lb_ship_OP1', packed_at = $2, packing_station_id = $3, total_items = 2 WHERE packing_id = $1")
        .bind(id).bind(now).bind(station_id).execute(&pool).await.unwrap();
    sqlx::query("UPDATE packing_lists SET packing_status = 'Shipped', shipped_at = $2 WHERE packing_id = $1")
        .bind(id).bind(now).execute(&pool).await.unwrap();

    let from = (now - Duration::days(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let to = (now + Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let res = reqwest::get(format!("{url}/leaderboard?discipline=packing&scope=stations&from={from}&to={to}"))
        .await.unwrap();
    let body: Value = res.json().await.unwrap();
    let row = body["rows"].as_array().unwrap().iter()
        .find(|r| r["id"] == station_id.to_string()).expect("station row present");
    assert_eq!(row["parcels"].as_i64().unwrap(), 1);

    cleanup(&pool, prefix).await;
}
```

(If `insert_station`'s signature differs — check `:289-296` — adapt the call, keep the assertion.)

- [ ] **Step 2: Run to verify both fail**

Run: `cargo test --test leaderboard_api leaderboard_counts_shipped leaderboard_station_scope_counts -- --nocapture`
Expected: FAIL — packing rows for shipped/returned parcels missing (`parcels` = 1 vs 3, station row absent), because the `'Packed'` gates exclude them.

- [ ] **Step 3: Flip the 8 gate sites**

In `src/api/leaderboard.rs`, replace **exactly** these lines:

`:171` (operator packing agg) and `:434` (videos_map) and `:469` (videoed_parcels_map) and `:808` (station packing agg):

```
                 AND pl.packing_status = 'Packed'
```
→
```
                 AND pl.packed_at IS NOT NULL
```

(`:434`/`:469` use the same text with different indentation — match each in place.)

`:318` (key_filter operators/packing):

```rust
            ("packed_by", "packed_by IS NOT NULL AND packing_status = 'Packed'")
```
→
```rust
            ("packed_by", "packed_by IS NOT NULL AND packed_at IS NOT NULL")
```

`:323` (key_filter stations/packing):

```rust
            "packing_station_id IS NOT NULL AND packing_status = 'Packed'",
```
→
```rust
            "packing_station_id IS NOT NULL AND packed_at IS NOT NULL",
```

`:711` and `:721` (daily14_map operators/stations packing arms):

```rust
            "packed_by IS NOT NULL AND packing_status = 'Packed'",
```
→
```rust
            "packed_by IS NOT NULL AND packed_at IS NOT NULL",
```

```rust
            "packing_station_id IS NOT NULL AND packing_status = 'Packed'",
```
→
```rust
            "packing_station_id IS NOT NULL AND packed_at IS NOT NULL",
```

After the edits: `grep -n "packing_status = 'Packed'" src/api/leaderboard.rs` must return **zero** matches.

- [ ] **Step 4: Run the new tests + the full leaderboard suite**

Run: `cargo test --test leaderboard_api --no-fail-fast`
Expected: all pass (including Task 1's three).

- [ ] **Step 5: Commit**

```bash
git add src/api/leaderboard.rs tests/leaderboard_api.rs
git commit -m "fix(leaderboard): count packing work by packed event, not current status

Replaces the 8 packing_status='Packed' gates with packed_at IS NOT NULL
(ADR-0003 parity). Ship/return scans no longer drain boards."
```

### Task 3: Drill endpoint + trend-delta window oracles

**Files:**
- Test: `tests/leaderboard_api.rs` (append; no production code — `key_filter` already fixed in Task 2)

**Interfaces:**
- Consumes: Task 2's gate contract.
- Produces: pinned drill/trend behavior — the FE delta correctness oracle (spec §7).

- [ ] **Step 1: Write the tests**

```rust
#[tokio::test]
async fn leaderboard_operator_drill_includes_shipped_parcels() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_drill_";
    cleanup(&pool, prefix).await;

    let now = Utc::now();
    let op = "lb_drill_OP1";
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('Drill', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(op).execute(&pool).await.unwrap();
    insert_packing_list(&pool, &format!("{prefix}A"), &format!("{prefix}OA"), "Shopee", Some("Packed"), now).await;
    set_packed(&pool, &format!("{prefix}A"), op, now, 5).await;
    sqlx::query("UPDATE packing_lists SET packing_status = 'Shipped', shipped_at = $2 WHERE tracking_number = $1")
        .bind(format!("{prefix}A")).bind(now).execute(&pool).await.unwrap();

    let from = (now - Duration::days(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let to = (now + Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let res = reqwest::get(format!("{url}/leaderboard/operator?discipline=packing&scope=operators&id={op}&from={from}&to={to}"))
        .await.unwrap();
    assert_eq!(res.status(), 200);
    let body: Value = res.json().await.unwrap();
    assert_eq!(body["totals"]["parcels"].as_i64().unwrap(), 1, "drill totals must include the shipped parcel");
    let trend_sum: i64 = body["trend"].as_array().unwrap().iter()
        .map(|b| b["parcels"].as_i64().unwrap_or(0)).sum();
    assert_eq!(trend_sum, 1, "trend buckets must include the shipped parcel");

    cleanup(&pool, prefix).await;
}

#[tokio::test]
async fn leaderboard_adjacent_windows_split_by_packed_event() {
    // FE trend-delta oracle: two adjacent windows attribute each parcel to
    // exactly the window its packed_at falls in, independent of status.
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_win_";
    cleanup(&pool, prefix).await;

    let now = Utc::now();
    let op = "lb_win_OP1";
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('Win', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(op).execute(&pool).await.unwrap();
    // parcel P1 packed 3 days ago then shipped today; parcel P2 packed today
    insert_packing_list(&pool, &format!("{prefix}P1"), &format!("{prefix}O1"), "Shopee", Some("Packed"), now - Duration::days(3)).await;
    set_packed(&pool, &format!("{prefix}P1"), op, now - Duration::days(3), 1).await;
    sqlx::query("UPDATE packing_lists SET packing_status = 'Shipped', shipped_at = $2 WHERE tracking_number = $1")
        .bind(format!("{prefix}P1")).bind(now).execute(&pool).await.unwrap();
    insert_packing_list(&pool, &format!("{prefix}P2"), &format!("{prefix}O2"), "Shopee", Some("Packed"), now).await;
    set_packed(&pool, &format!("{prefix}P2"), op, now, 1).await;

    let parcels_in = |from: chrono::DateTime<Utc>, to: chrono::DateTime<Utc>| {
        let url = url.clone();
        async move {
            let f = from.to_rfc3339_opts(SecondsFormat::Secs, true);
            let t = to.to_rfc3339_opts(SecondsFormat::Secs, true);
            let body: Value = reqwest::get(format!("{url}/leaderboard?discipline=packing&scope=operators&from={f}&to={t}"))
                .await.unwrap().json().await.unwrap();
            body["rows"].as_array().unwrap().iter()
                .find(|r| r["id"] == op)
                .map(|r| r["parcels"].as_i64().unwrap()).unwrap_or(0)
        }
    };
    // current window = last 2 days (contains P2 only), previous = the 2 days before (contains P1 only)
    assert_eq!(parcels_in(now - Duration::days(2), now + Duration::hours(1)).await, 1);
    assert_eq!(parcels_in(now - Duration::days(4), now - Duration::days(2)).await, 1,
        "P1 stays in its packed window even though it shipped today");

    cleanup(&pool, prefix).await;
}
```

- [ ] **Step 2: Run — expect PASS (behavior already delivered by Task 2)**

Run: `cargo test --test leaderboard_api leaderboard_operator_drill leaderboard_adjacent_windows -- --nocapture`
Expected: PASS. These pin inherited behavior; if either fails, Task 2's `key_filter` flip is wrong — fix there, not here.

- [ ] **Step 3: Commit**

```bash
git add tests/leaderboard_api.rs
git commit -m "test(leaderboard): pin drill + adjacent-window packed-event oracles"
```

### Task 4: Unregistered-operator sidecar on `GET /leaderboard`

**Files:**
- Modify: `src/api/leaderboard.rs` — `LeaderboardResponse` (`:21-25`), `leaderboard()` handler (`:100-112`), new fn + structs near the other map helpers
- Test: `tests/leaderboard_api.rs` (append)

**Interfaces:**
- Consumes: Task 2's event-time gates.
- Produces: response field `unregistered?: { parcels: number, codes: [{ code, count }] }` (camelCase; omitted when `None`) — Task 8 (FE types) consumes this exact shape.

**Context you must know:** Option C from ticket #70. Populated for `scope=operators` only, both disciplines; `None` for stations. WHERE = the operator agg's WHERE **minus the EXISTS** (keeps `packed_by IS NOT NULL` — without it, NULL packers pass `NOT EXISTS` and break the `String` decode), **plus** `NOT EXISTS`. Codes sorted by count desc.

- [ ] **Step 1: Write the failing tests**

```rust
#[tokio::test]
async fn leaderboard_reports_unregistered_sidecar_for_operators() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_unreg_";
    cleanup(&pool, prefix).await;

    // Far-past window (2010) so organic unregistered rows (OP-1 etc.) can't leak in.
    let base = chrono::DateTime::parse_from_rfc3339("2010-06-01T04:00:00Z").unwrap().with_timezone(&Utc);
    let ghost = "lb_ghost_9"; // NOT registered on purpose

    for suffix in ["A", "B"] {
        insert_packing_list(&pool, &format!("{prefix}{suffix}"), &format!("{prefix}O{suffix}"), "Shopee", Some("Packed"), base).await;
        set_packed(&pool, &format!("{prefix}{suffix}"), ghost, base, 1).await;
    }

    let res = reqwest::get(format!(
        "{url}/leaderboard?discipline=packing&scope=operators&from=2010-05-01T00:00:00Z&to=2010-07-01T00:00:00Z"
    )).await.unwrap();
    let body: Value = res.json().await.unwrap();

    // ghost is hidden from rows...
    assert!(body["rows"].as_array().unwrap().iter().all(|r| r["id"] != ghost));
    // ...but visible in the sidecar
    let unreg = &body["unregistered"];
    assert_eq!(unreg["parcels"].as_i64().unwrap(), 2);
    let codes = unreg["codes"].as_array().unwrap();
    assert_eq!(codes.len(), 1);
    assert_eq!(codes[0]["code"], ghost);
    assert_eq!(codes[0]["count"].as_i64().unwrap(), 2);

    cleanup(&pool, prefix).await;
}

#[tokio::test]
async fn leaderboard_unregistered_sidecar_zero_state_and_stations() {
    let (url, pool) = spawn_app().await;
    // Far-past empty window → zero-state: field omitted entirely.
    let res = reqwest::get(format!(
        "{url}/leaderboard?discipline=packing&scope=operators&from=2009-01-01T00:00:00Z&to=2009-02-01T00:00:00Z"
    )).await.unwrap();
    let body: Value = res.json().await.unwrap();
    assert!(body.get("unregistered").is_none(), "zero-state must omit the field");

    // stations scope: always omitted
    let res = reqwest::get(format!(
        "{url}/leaderboard?discipline=packing&scope=stations&from=2009-01-01T00:00:00Z&to=2009-02-01T00:00:00Z"
    )).await.unwrap();
    let body: Value = res.json().await.unwrap();
    assert!(body.get("unregistered").is_none(), "stations scope must omit the field");
}
```

Also append a QC-arm case inside the first test (after the packing assertions) or as a third test: seed a `checked_by = ghost, checked_at = base` row and assert the `discipline=qc` response reports it.

- [ ] **Step 2: Run to verify failure**

Run: `cargo test --test leaderboard_api leaderboard_reports_unregistered leaderboard_unregistered_sidecar -- --nocapture`
Expected: FAIL — `unregistered` is null (field doesn't exist yet).

- [ ] **Step 3: Implement**

In `src/api/leaderboard.rs`:

```rust
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LeaderboardResponse {
    pub rows: Vec<LeaderboardRow>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unregistered: Option<Unregistered>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Unregistered {
    pub parcels: i64,
    pub codes: Vec<UnregisteredCode>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UnregisteredCode {
    pub code: String,
    pub count: i64,
}
```

New helper (place near the other `*_map` helpers):

```rust
/// Work hidden by the registered-only EXISTS filter (operators scope only).
/// Same WHERE as the operator agg minus the EXISTS: the packed_by/checked_by
/// IS NOT NULL conjunct stays (NULL keys would pass NOT EXISTS and break the
/// String decode).
async fn unregistered_sidecar(
    pool: &sqlx::PgPool,
    discipline: Discipline,
    from: Option<DateTime<Utc>>,
    to: Option<DateTime<Utc>>,
) -> Option<Unregistered> {
    let (key, event) = match discipline {
        Discipline::Packing => ("packed_by", "packed_at"),
        Discipline::Qc => ("checked_by", "checked_at"),
    };
    let sql = format!(
        r#"SELECT {key} AS code, COUNT(*)::bigint AS count
           FROM packing_lists pl
           WHERE pl.{key} IS NOT NULL
             AND pl.{event} IS NOT NULL
             AND NOT EXISTS (SELECT 1 FROM operator_lists o WHERE o.staff_code = pl.{key})
             AND ($1::timestamptz IS NULL OR pl.{event} >= $1)
             AND ($2::timestamptz IS NULL OR pl.{event} <= $2)
           GROUP BY 1
           ORDER BY count DESC"#,
    );
    let codes: Vec<UnregisteredCode> = sqlx::query_as::<_, (String, i64)>(&sql)
        .bind(from)
        .bind(to)
        .fetch_all(pool)
        .await
        .unwrap_or_default()
        .into_iter()
        .map(|(code, count)| UnregisteredCode { code, count })
        .collect();
    let parcels: i64 = codes.iter().map(|c| c.count).sum();
    if parcels == 0 { None } else { Some(Unregistered { parcels, codes }) }
}
```

Wire in `leaderboard()` (replacing the current `Ok(Json(LeaderboardResponse { rows }))`):

```rust
    let unregistered = match scope {
        Scope::Operators => unregistered_sidecar(&pool, discipline, q.from, q.to).await,
        Scope::Stations => None,
    };

    Ok(Json(LeaderboardResponse { rows, unregistered }))
```

(If `query_as` tuple decode complains, use a `#[derive(sqlx::FromRow)] struct CodeCount { code: String, count: i64 }` like the file's sibling `KeyCount` pattern.)

- [ ] **Step 4: Run the tests**

Run: `cargo test --test leaderboard_api --no-fail-fast`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/api/leaderboard.rs tests/leaderboard_api.rs
git commit -m "feat(leaderboard): unregistered-operator sidecar on GET /leaderboard

Option C (wayfinder #70): registered-only filter stays; hidden work is
reported as {parcels, codes[]} for the operators scope."
```

### Task 5: `holds_by_status` — bucket force-shipped/returned as `Force-shipped`

**Files:**
- Modify: `src/api/leaderboard.rs:506` (status expression in `holds_by_status_map`)
- Test: `tests/leaderboard_api.rs` (append)

**Interfaces:** self-contained; FE renders bucket labels as-is.

**Context you must know:** QC-discipline-only map (keyed `checked_by`/`checking_station_id`, windowed `checked_at`; packing rows hardcode `holds = 0`). Bucket label = the row's CURRENT status, so never-cleared force-shipped parcels show as `Shipped`/`Returned` "holds".

- [ ] **Step 1: Write the failing test**

```rust
#[tokio::test]
async fn holds_by_status_buckets_terminal_rows_as_force_shipped() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_fship_";
    cleanup(&pool, prefix).await;

    let now = Utc::now();
    let chk = "lb_fs_QC1";
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('ForceShip', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(chk).execute(&pool).await.unwrap();
    // QC-held parcel that later got force-shipped without clearing
    insert_packing_list(&pool, &format!("{prefix}A"), &format!("{prefix}OA"), "Shopee", Some("QC Hold"), now).await;
    sqlx::query("UPDATE packing_lists SET checked_by = $2, checked_at = $3 WHERE tracking_number = $1")
        .bind(format!("{prefix}A")).bind(chk).bind(now).execute(&pool).await.unwrap();
    sqlx::query("UPDATE packing_lists SET packing_status = 'Shipped', shipped_at = $2 WHERE tracking_number = $1")
        .bind(format!("{prefix}A")).bind(now).execute(&pool).await.unwrap();

    let from = (now - Duration::days(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let to = (now + Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
    let res = reqwest::get(format!("{url}/leaderboard?discipline=qc&scope=operators&from={from}&to={to}"))
        .await.unwrap();
    let body: Value = res.json().await.unwrap();
    let row = body["rows"].as_array().unwrap().iter()
        .find(|r| r["id"] == chk).expect("qc operator row present");
    let buckets = row["holdsByStatus"].as_array().unwrap();
    assert!(buckets.iter().any(|b| b["status"] == "Force-shipped"),
        "terminal-status uncleared parcel must bucket as Force-shipped, got {buckets:?}");
    assert!(buckets.iter().all(|b| b["status"] != "Shipped" && b["status"] != "Returned"));

    cleanup(&pool, prefix).await;
}
```

(Check the actual field names of `HoldStatus` at `:72-77` — if the JSON key is not `status`, adapt.)

- [ ] **Step 2: Run to verify it fails** — bucket comes back as `Shipped`.

- [ ] **Step 3: Implement**

At `:506`, replace:

```sql
             COALESCE(NULLIF(btrim(packing_status), ''), 'Unknown') AS status,
```

with:

```sql
             CASE WHEN packing_status IN ('Shipped', 'Returned') THEN 'Force-shipped'
                  ELSE COALESCE(NULLIF(btrim(packing_status), ''), 'Unknown') END AS status,
```

- [ ] **Step 4: Run** `cargo test --test leaderboard_api holds_by_status_buckets -- --nocapture` — PASS.

- [ ] **Step 5: Commit**

```bash
git add src/api/leaderboard.rs tests/leaderboard_api.rs
git commit -m "fix(leaderboard): bucket force-shipped uncleared parcels as Force-shipped"
```

### Task 6: `daily14` Bangkok-date fix

**Files:**
- Modify: `src/api/leaderboard.rs:733` (days_ago expr) and `:737` (window predicate)
- Test: `tests/leaderboard_api.rs` (append)

**Interfaces:** self-contained.

**Context you must know:** `days_ago` mixes session-TZ `CURRENT_DATE` with a Bangkok-local bucket — between 00:00–07:00 Bangkok, today's rows compute `days_ago = -1` and the `:752` guard drops them. The regression test is only red pre-fix during 17:00–24:00 UTC; keep it anyway as permanent regression coverage.

- [ ] **Step 1: Write the test**

```rust
#[tokio::test]
async fn daily14_counts_today_bangkok_row_in_slot_zero() {
    let (url, pool) = spawn_app().await;
    let prefix = "test_lb_d14tz_";
    cleanup(&pool, prefix).await;

    let now = Utc::now();
    let op = "lb_d14tz_OP1";
    sqlx::query("INSERT INTO operator_lists (first_name, staff_code) VALUES ('TzD14', $1) ON CONFLICT (staff_code) DO NOTHING")
        .bind(op).execute(&pool).await.unwrap();
    insert_packing_list(&pool, &format!("{prefix}A"), &format!("{prefix}OA"), "Shopee", Some("Packed"), now).await;
    set_packed(&pool, &format!("{prefix}A"), op, now, 1).await;

    let res = reqwest::get(format!("{url}/leaderboard?discipline=packing&scope=operators")).await.unwrap();
    let body: Value = res.json().await.unwrap();
    let row = body["rows"].as_array().unwrap().iter()
        .find(|r| r["id"] == op).expect("operator row present");
    let daily = row["daily14"].as_array().unwrap();
    let total: i64 = daily.iter().map(|v| v.as_i64().unwrap()).sum();
    assert_eq!(total, 1, "a just-packed row must land in the 14-day sparkline regardless of UTC/Bangkok date skew");

    cleanup(&pool, prefix).await;
}
```

- [ ] **Step 2: Run it** (red only during 17:00–24:00 UTC; otherwise passes pre-fix — proceed either way).

- [ ] **Step 3: Implement**

At `:733`, replace:

```sql
             (CURRENT_DATE - ({ts} AT TIME ZONE 'Asia/Bangkok')::date)::int AS days_ago,
```

with:

```sql
             ((now() AT TIME ZONE 'Asia/Bangkok')::date - ({ts} AT TIME ZONE 'Asia/Bangkok')::date)::int AS days_ago,
```

At `:737`, replace:

```sql
             AND ({ts}) >= (now() - interval '14 days')
```

with:

```sql
             AND ({ts} AT TIME ZONE 'Asia/Bangkok')::date >= (now() AT TIME ZONE 'Asia/Bangkok')::date - 13
```

- [ ] **Step 4: Run** `cargo test --test leaderboard_api daily14 -- --nocapture` — both daily14 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/api/leaderboard.rs tests/leaderboard_api.rs
git commit -m "fix(leaderboard): daily14 uses Bangkok date on both sides of days_ago"
```

### Task 7: Backend verification sweep

**Files:** none (verification only)

- [ ] **Step 1:** `cargo build` — compiles clean.
- [ ] **Step 2:** `cargo test --no-fail-fast` — expected: leaderboard suite fully green; the only pre-existing failure remaining anywhere is 1 `product_insights` (base condition, untouched).
- [ ] **Step 3:** `grep -n "packing_status = 'Packed'" src/api/leaderboard.rs` — zero matches.
- [ ] **Step 4:** No commit (nothing changed); record results in the task notes.

---

## Phase 2 — Frontend (worktree `frontend/.worktrees/leaderboard-ship-integrity`)

### Task 8: Types + cache entry shape + `unregistered` threading

**Files:**
- Modify: `app/types.ts` (after `LeaderboardApiResponse`, `:841-843`), `app/lib/leaderboardCache.ts`, `app/hooks/useLeaderboard.ts`
- Test: `app/hooks/useLeaderboard.unregistered.test.ts` (create)

**Interfaces:**
- Consumes: backend field `unregistered?: { parcels, codes: [{code, count}] }` (Task 4).
- Produces: `useLeaderboard` returns `{ rows, loading, unregistered }` where `unregistered: LbUnregistered | undefined`; cache entries are `{ rows: BoardRow[], unregistered?: LbUnregistered }`. Tasks 10–11 rely on these exact names.

- [ ] **Step 1: Write the failing test**

`app/hooks/useLeaderboard.unregistered.test.ts`:

```ts
// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";

const RESPONSE = {
  rows: [],
  unregistered: { parcels: 13, codes: [{ code: "OP-1", count: 13 }] },
};

beforeEach(() => {
  vi.restoreAllMocks();
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(RESPONSE))));
});

describe("useLeaderboard unregistered sidecar", () => {
  it("exposes the sidecar from the API response", async () => {
    const { useLeaderboard } = await import("./useLeaderboard");
    const { result } = renderHook(() =>
      useLeaderboard("packing", "operators", { from: "2026-07-15T00:00:00Z" }, undefined, { parcels: 1, qcPenalty: 1 }),
    );
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.unregistered).toEqual(RESPONSE.unregistered);
  });
});
```

(Match the file's sibling hook tests for the exact `LbWeights` shape — check `app/lib/leaderboardWeights.ts` if `{ parcels, qcPenalty }` doesn't typecheck.)

- [ ] **Step 2: Run** `npm test -- useLeaderboard.unregistered` — FAIL (`unregistered` undefined on the hook result).

- [ ] **Step 3: Implement**

`app/types.ts` — after `LeaderboardApiResponse`:

```ts
/** Work hidden by the registered-only operator filter (operators scope only). */
export interface LbUnregistered {
  parcels: number;
  codes: { code: string; count: number }[];
}
```

and add the field to `LeaderboardApiResponse`:

```ts
export interface LeaderboardApiResponse {
  rows: LeaderboardApiRow[];
  unregistered?: LbUnregistered;
}
```

`app/lib/leaderboardCache.ts` — entry shape (single Map, `boardKey` unchanged; do NOT add a parallel map):

```ts
export interface BoardEntry {
  rows: BoardRow[];
  unregistered?: LbUnregistered;
}

const cache = new Map<string, BoardEntry>();
```

`cacheGet`/`cacheSet` move to `BoardEntry`; `fetchBoard` returns `BoardEntry`:

```ts
export function cacheGet(key: string): BoardEntry | undefined {
  return cache.get(key);
}
export function cacheSet(key: string, entry: BoardEntry): void {
  cache.set(key, entry);
}
```

```ts
export async function fetchBoard(
  d: LbDiscipline, s: LbScope, range: LbRange, prevRange: LbRange | undefined, weights: LbWeights,
): Promise<BoardEntry> {
  const cur = await fetch(url(d, s, range));
  const curJson = await safeJson<LeaderboardApiResponse>(cur, { rows: [] });
  let prevRank: Map<string, number> | undefined;
  if (prevRange?.from) {
    const prev = await fetch(url(d, s, prevRange));
    const prevJson = await safeJson<LeaderboardApiResponse>(prev, { rows: [] });
    prevRank = new Map(computeBoard(prevJson.rows, d, undefined, weights).map((r) => [r.id, r.rank]));
  }
  return { rows: computeBoard(curJson.rows, d, prevRank, weights), unregistered: curJson.unregistered };
}
```

`app/hooks/useLeaderboard.ts` — thread it (every `cacheGet(...)` site now reads `.rows`; state gains the sidecar):

```ts
  const [rows, setRows] = useState<BoardRow[]>(() => cacheGet(key)?.rows ?? []);
  const [unregistered, setUnregistered] = useState<LbUnregistered | undefined>(() => cacheGet(key)?.unregistered);
```

inside the effect: `if (cached) { setRows(cached.rows); setUnregistered(cached.unregistered); setLoading(false); }`; after fetch: `cacheSet(k, entry); setRows(entry.rows); setUnregistered(entry.unregistered);` — and return `{ rows, loading, unregistered }`.

- [ ] **Step 4: Run** `npm test` — new test passes, existing hook/component suites still green (they consume `rows` from the hook result, unchanged shape).
- [ ] **Step 5:** `npm run lint` and fix any type fallout (all `cacheGet`/`cacheSet`/`fetchBoard` call sites: `useLeaderboard.ts` only).
- [ ] **Step 6: Commit**

```bash
git add app/types.ts app/lib/leaderboardCache.ts app/hooks/useLeaderboard.ts app/hooks/useLeaderboard.unregistered.test.ts
git commit -m "feat(leaderboard): thread unregistered sidecar through cache and hook"
```

### Task 9: `day` period — window resolver + PeriodBar Day chip + steppers

**Files:**
- Modify: `app/lib/leaderboardWindow.ts:9` (union) and `resolveShellRange`, `app/components/leaderboard/PeriodBar.tsx` (LABELS, day block, steppers, `rightLabelTone`), `app/components/LiveLeaderboard.tsx:139` + label fn `:69-75`, `app/components/leaderboard/LeaderboardTab.tsx:80` + label fn `:58-71`
- Modify: `vitest.config.ts` (extend include)
- Test: `app/lib/leaderboardWindow.test.ts` (create, vitest-style), `app/components/leaderboard/PeriodBar.day.test.tsx` (create)

**Interfaces:**
- Consumes: nothing new from other tasks (backend from/to already arbitrary).
- Produces: `ShellPeriod` includes `"day"`; `resolveShellRange({period:"day", anchor})` → local `[00:00:00.000, 23:59:59.999]` of anchor; `PeriodBar` prop `rightLabelTone?: "brand" | "static"` (default `"brand"`). Task 10's live/static behavior and Task 11's pages rely on these.

**Context you must know:** V1 from ticket #71. Day boundaries are **browser-local** (matching every existing period — #71's "Asia/Bangkok" parenthetical was corrected on the ticket). `LABELS: Record<ShellPeriod, string>` forces the label entry at compile time. No stepper exists anywhere — build ‹ › in PeriodBar; › clamps at today (DatePicker already hard-blocks future dates). `PeriodBar.set()` spreads the full `PeriodValue`, so `day` reuses the existing `anchor` field — zero state-shape changes.

- [ ] **Step 1: Extend vitest include**

`vitest.config.ts` — the two new lib tests are vitest-style; the pre-existing `app/lib/*.test.ts` node:test files stay excluded:

```ts
    include: [
      "app/ship/**/*.test.ts",
      "app/hooks/**/*.test.{ts,tsx}",
      "app/components/**/*.test.{ts,tsx}",
      "app/lib/leaderboardWindow.test.ts",
      "app/lib/leaderboard.test.ts",
    ],
```

- [ ] **Step 2: Write the failing window test**

`app/lib/leaderboardWindow.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { prevShellRange, resolveShellRange } from "./leaderboardWindow";

describe("day period", () => {
  it("resolves to the local day of the anchor", () => {
    const r = resolveShellRange({ period: "day", anchor: "2026-07-03", from: "2026-07-03", to: "2026-07-03" });
    expect(new Date(r.from).getTime()).toBe(new Date("2026-07-03T00:00:00").getTime());
    expect(new Date(r.to).getTime()).toBe(new Date("2026-07-03T23:59:59.999").getTime());
  });

  it("prevShellRange of a day is the previous day", () => {
    const r = resolveShellRange({ period: "day", anchor: "2026-07-03", from: "2026-07-03", to: "2026-07-03" });
    const p = prevShellRange(r);
    expect(new Date(p.to).getTime()).toBe(new Date(r.from).getTime() - 1);
    expect(new Date(p.to).getTime() - new Date(p.from).getTime())
      .toBe(new Date(r.to).getTime() - new Date(r.from).getTime());
  });
});
```

- [ ] **Step 3: Run** `npm test -- leaderboardWindow` — FAIL (TS: `"day"` not assignable to `ShellPeriod`).

- [ ] **Step 4: Implement the resolver**

`app/lib/leaderboardWindow.ts`:

```ts
export type ShellPeriod = "all" | "today" | "day" | "week" | "month" | "year" | "custom";
```

In `resolveShellRange`, after the `today` branch:

```ts
  if (v.period === "day") {
    const a = new Date(`${v.anchor}T00:00:00`);
    return { from: startISO(a), to: endISO(a) };
  }
```

- [ ] **Step 5: Run** `npm test -- leaderboardWindow` — window tests PASS (PeriodBar will fail to typecheck until the LABELS entry lands — that's Step 6).

- [ ] **Step 6: Write the failing PeriodBar test, then implement the Day chip**

`app/components/leaderboard/PeriodBar.day.test.tsx`:

```tsx
// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { PeriodBar } from "./PeriodBar";

const value = { period: "day" as const, anchor: "2026-07-03", from: "2026-07-03", to: "2026-07-03" };

describe("PeriodBar day chip", () => {
  it("renders one date field and steppers when day is selected", () => {
    render(<PeriodBar chips={["day", "week"]} value={value} onChange={() => {}} />);
    expect(screen.getByText("Day")).toBeTruthy();
    expect(screen.getByLabelText("previous day")).toBeTruthy();
    expect(screen.getByLabelText("next day")).toBeTruthy();
  });

  it("steppers move the anchor by one day", () => {
    const onChange = vi.fn();
    render(<PeriodBar chips={["day"]} value={value} onChange={onChange} />);
    fireEvent.click(screen.getByLabelText("previous day"));
    expect(onChange).toHaveBeenCalledWith({ ...value, anchor: "2026-07-02" });
    fireEvent.click(screen.getByLabelText("next day"));
    expect(onChange).toHaveBeenCalledWith({ ...value, anchor: "2026-07-04" });
  });

  it("next-day stepper is disabled when anchor is today", () => {
    const today = new Date();
    const pad = (n: number) => String(n).padStart(2, "0");
    const t = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;
    render(<PeriodBar chips={["day"]} value={{ ...value, anchor: t }} onChange={() => {}} />);
    expect((screen.getByLabelText("next day") as HTMLButtonElement).disabled).toBe(true);
  });
});
```

`app/components/leaderboard/PeriodBar.tsx`:

LABELS gains the entry:

```ts
const LABELS: Record<ShellPeriod, string> = {
  all: "All time",
  today: "Today",
  day: "Day",
  week: "Week",
  month: "Month",
  year: "Year",
  custom: "Custom",
};
```

Props gain the tone (and the wrapper span uses it):

```tsx
  rightLabelTone,
}: {
  // ...existing props...
  /** Pill tint for rightLabel — 'static' greys it out for non-live windows. */
  rightLabelTone?: "brand" | "static";
```

In the `rightLabel` span's `style`, replace the three hardcoded `var(--brand)` values with a tone variable:

```tsx
          {rightLabel != null && (() => {
            const tone = rightLabelTone === "static" ? "var(--muted-foreground)" : "var(--brand)";
            return (
              <span
                className={ /* unchanged className expression */ }
                style={{
                  color: tone,
                  background: `color-mix(in srgb, ${tone} 12%, transparent)`,
                  border: `1px solid color-mix(in srgb, ${tone} 32%, transparent)`,
                }}
              >
                <span aria-hidden style={{ width: 6, height: 6, borderRadius: "50%", background: tone }} />
                {rightLabel}
              </span>
            );
          })()}
```

Day block (after the anchor-picker block, parallel to the custom block), plus a local date-shift helper:

```tsx
function shiftDay(anchor: string, delta: number): string {
  const d = new Date(`${anchor}T00:00:00`);
  d.setDate(d.getDate() + delta);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
```

```tsx
        {/* single-day picker: one date field + prev/next steppers (next clamps at today) */}
        {value.period === "day" && (
          <div className="flex items-center gap-2">
            <button
              aria-label="previous day"
              onClick={() => set({ anchor: shiftDay(value.anchor, -1) })}
              className="rounded-md border border-border bg-card px-2 py-1 text-xs text-muted-foreground hover:text-foreground"
            >
              ‹
            </button>
            <DatePicker label="Day" selectedDate={value.anchor} onSelectDate={(d) => set({ anchor: d })} />
            <button
              aria-label="next day"
              disabled={shiftDay(value.anchor, 1) > localToday()}
              onClick={() => set({ anchor: shiftDay(value.anchor, 1) })}
              className="rounded-md border border-border bg-card px-2 py-1 text-xs text-muted-foreground hover:text-foreground disabled:opacity-35"
            >
              ›
            </button>
          </div>
        )}
```

with the browser-local (never UTC) today helper beside `shiftDay`:

```tsx
function localToday(): string {
  const n = new Date();
  const pad = (x: number) => String(x).padStart(2, "0");
  return `${n.getFullYear()}-${pad(n.getMonth() + 1)}-${pad(n.getDate())}`;
}
```

(If the codebase already exports a `toLocalDateStr()` helper — `LiveLeaderboard.tsx` uses one — import and reuse it instead of `localToday`, in both PeriodBar and the label/tone code below.)

- [ ] **Step 7: Wire the chips + labels on both pages**

`LiveLeaderboard.tsx:139`: `chips={["today", "day", "week", "month", "year"]}`.
`periodLabel` (`:69-75`) gains, before the week fallback:

```ts
    if (pv.period === "day") {
      const a = new Date(`${pv.anchor}T00:00:00`);
      const label = a.toLocaleDateString("en", { weekday: "short", day: "numeric", month: "short" });
      return pv.anchor === toLocalDateStr() ? `Live · ${label}` : `${label} · static`;
    }
```

and pass the tone: `rightLabelTone={pv.period === "day" && pv.anchor !== toLocalDateStr() ? "static" : "brand"}`.

`LeaderboardTab.tsx:80`: `chips={["day", "week", "month", "year", "custom"]}`.
Its label fn gains:

```ts
    if (pv.period === "day") {
      const a = new Date(`${pv.anchor}T00:00:00`);
      return a.toLocaleDateString("en", { weekday: "short", day: "numeric", month: "short", year: "numeric" });
    }
```

The settled board's deltas need no work: `prevShellRange` handles the day window generically and `LeaderboardTab` already passes it (`:52,:55`). Do NOT move the PeriodBar — the range control stays page-level (one bar above scopes/disciplines).

- [ ] **Step 8: Run** `npm test` — all green — and `npm run lint`.
- [ ] **Step 9: Commit**

```bash
git add vitest.config.ts app/lib/leaderboardWindow.ts app/lib/leaderboardWindow.test.ts \
  app/components/leaderboard/PeriodBar.tsx app/components/leaderboard/PeriodBar.day.test.tsx \
  app/components/LiveLeaderboard.tsx app/components/leaderboard/LeaderboardTab.tsx
git commit -m "feat(leaderboard): single-day picker on both boards (V1, wayfinder #71)"
```

### Task 10: Cache revalidation — revision-stamped entries + WS bump

**Files:**
- Modify: `app/lib/leaderboardCache.ts`, `app/hooks/useLeaderboard.ts`, `app/components/LiveLeaderboard.tsx:78-91`, `app/components/leaderboard/LeaderboardTab.tsx`
- Test: `app/hooks/useLeaderboard.revalidate.test.ts` (create)

**Interfaces:**
- Consumes: Task 8's `BoardEntry` shape.
- Produces: `cacheBump()`, `subscribeRevision(cb)`, `getRevision()` exported from `leaderboardCache.ts`. Entries carry a `rev` stamp; stale = `entry.rev < getRevision()`.

**Context you must know:** Bump-only refetch leaves the sibling-prefetch hole (spec §5) — entries must be revision-stamped and the missing-**or-stale** test applied on every effect run, including the sibling guard. Stale rows stay visible while revalidating (no loading flash). No `Map.clear()` on bump.

- [ ] **Step 1: Write the failing test**

`app/hooks/useLeaderboard.revalidate.test.ts`:

```ts
// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook, waitFor, act } from "@testing-library/react";

let fetchCount = 0;
beforeEach(() => {
  fetchCount = 0;
  vi.restoreAllMocks();
  vi.stubGlobal("fetch", vi.fn(async () => {
    fetchCount++;
    return new Response(JSON.stringify({ rows: [] }));
  }));
});

describe("useLeaderboard revalidation", () => {
  it("refetches the mounted key after cacheBump, keeping stale rows meanwhile", async () => {
    const { useLeaderboard } = await import("./useLeaderboard");
    const { cacheBump } = await import("../lib/leaderboardCache");
    const range = { from: "2026-07-01T00:00:00Z", to: "2026-07-01T23:59:59Z" };
    const { result } = renderHook(() =>
      useLeaderboard("packing", "operators", range, undefined, { parcels: 1, qcPenalty: 1 }),
    );
    await waitFor(() => expect(result.current.loading).toBe(false));
    const before = fetchCount; // own fetch + sibling prefetch

    act(() => { cacheBump(); });
    await waitFor(() => expect(fetchCount).toBeGreaterThan(before));
    expect(result.current.loading).toBe(false); // stale-while-revalidate: no loading flash
  });
});
```

- [ ] **Step 2: Run** `npm test -- revalidate` — FAIL (`cacheBump` not exported).

- [ ] **Step 3: Implement the cache side**

`app/lib/leaderboardCache.ts`:

```ts
export interface BoardEntry {
  rows: BoardRow[];
  unregistered?: LbUnregistered;
  /** Revision stamp at write time; entry is stale when rev < getRevision(). */
  rev: number;
}

let revision = 0;
const listeners = new Set<() => void>();

/** Data-freshness signal (WS event / fallback tick). Never clears the Map. */
export function cacheBump(): void {
  revision++;
  listeners.forEach((l) => l());
}
export function getRevision(): number {
  return revision;
}
export function subscribeRevision(cb: () => void): () => void {
  listeners.add(cb);
  return () => listeners.delete(cb);
}

export function cacheFresh(key: string): BoardEntry | undefined {
  const e = cache.get(key);
  return e && e.rev >= revision ? e : undefined;
}
```

`cacheSet` stamps: `cache.set(key, { ...entry, rev: revision })` (callers pass `{rows, unregistered}` without `rev` — make the param `Omit<BoardEntry, "rev">`).

- [ ] **Step 4: Implement the hook side**

`app/hooks/useLeaderboard.ts`:

```ts
  const revision = useSyncExternalStore(subscribeRevision, getRevision, getRevision);
```

- `revision` joins the effect dep array.
- Display still seeds from any cached entry (fresh or stale): `cacheGet(k)` for `setRows`/`setUnregistered`; only the **fetch decision** uses freshness: `if (!cacheFresh(k)) { const entry = await fetchBoard(...); cacheSet(k, entry); ... }`. When a stale entry exists, do NOT `setLoading(true)` — stale-while-revalidate.
- Sibling prefetch guard becomes `if (!cacheFresh(sk)) { ... }`.

- [ ] **Step 5: Wire the producers**

`LiveLeaderboard.tsx` — in the existing WS debounce handler and the 10 s fallback interval (`:78-91`), alongside `setTick`, call `cacheBump()`.

`LeaderboardTab.tsx` — the settled page has no WS today; add the same pattern:

```ts
  const bumpTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  usePackingSocket((ev) => {
    if (!["packing_inserted", "packing_updated", "packing_video_inserted", "packing_video_updated"].includes(ev.type)) return;
    if (bumpTimer.current) return;
    bumpTimer.current = setTimeout(() => { bumpTimer.current = null; cacheBump(); }, 500);
  });
```

(Match `usePackingSocket`'s actual callback signature — copy the invocation shape from `LiveLeaderboard.tsx:84-91` verbatim, adding `cacheBump()` where it bumps the tick.)

- [ ] **Step 6: Run** `npm test` — all green (Task 8's test still passes: seeding reads stay `cacheGet`).
- [ ] **Step 7: Commit**

```bash
git add app/lib/leaderboardCache.ts app/hooks/useLeaderboard.ts app/hooks/useLeaderboard.revalidate.test.ts \
  app/components/LiveLeaderboard.tsx app/components/leaderboard/LeaderboardTab.tsx
git commit -m "fix(leaderboard): WS-keyed revision revalidates mounted boards

Revision-stamped entries close the sibling-prefetch stale-serve hole;
stale rows stay visible while revalidating."
```

### Task 11: Unregistered footer line on both pages

**Files:**
- Create: `app/components/leaderboard/UnregisteredLine.tsx`
- Modify: `app/components/leaderboard/LiveBoard.tsx` (after the rows `</div>` `:122`, before `</section>`), `app/components/leaderboard/LeaderboardTab.tsx` (directly after `<RankCards …/>` `:126`), plus threading `unregistered` from the hook to both
- Test: `app/components/leaderboard/UnregisteredLine.test.tsx` (create)

**Interfaces:**
- Consumes: `unregistered` from `useLeaderboard` (Task 8), shape `LbUnregistered`.
- Produces: `<UnregisteredLine data={unregistered} />` — renders `null` when `data` is undefined or `parcels === 0`.

**Context you must know:** Copy is exactly `{parcels} parcels by {codes.length} unregistered codes — hidden from board`. Expand-on-click reveals per-code counts (raw codes appear only there). Gate on `scope === "operators"`. In `LiveBoard`, place OUTSIDE the FLIP container (`boardRef` animates `[data-key]` children). In the settled page, do NOT put it inside `RankCards` (returns `null` on zero rows). Live page renders two boards (packing + QC) — each gets its own line from its own hook data.

- [ ] **Step 1: Write the failing test**

`app/components/leaderboard/UnregisteredLine.test.tsx`:

```tsx
// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { UnregisteredLine } from "./UnregisteredLine";

describe("UnregisteredLine", () => {
  it("renders the decided copy and expands to per-code counts", () => {
    render(<UnregisteredLine data={{ parcels: 13, codes: [{ code: "OP-1", count: 13 }] }} />);
    expect(screen.getByText("13 parcels by 1 unregistered codes — hidden from board")).toBeTruthy();
    expect(screen.queryByText("OP-1")).toBeNull();
    fireEvent.click(screen.getByRole("button"));
    expect(screen.getByText(/OP-1/)).toBeTruthy();
    expect(screen.getByText(/13/)).toBeTruthy();
  });

  it("renders nothing at zero-state", () => {
    const { container: c1 } = render(<UnregisteredLine data={undefined} />);
    expect(c1.firstChild).toBeNull();
    const { container: c2 } = render(<UnregisteredLine data={{ parcels: 0, codes: [] }} />);
    expect(c2.firstChild).toBeNull();
  });
});
```

- [ ] **Step 2: Run** `npm test -- UnregisteredLine` — FAIL (module missing).

- [ ] **Step 3: Implement**

`app/components/leaderboard/UnregisteredLine.tsx`:

```tsx
"use client";
import { useState } from "react";
import { LbUnregistered } from "../../types";

/** Muted footer under operator rankings: work hidden by the registered-only
 *  filter (wayfinder #70, Option C). Absent entirely at zero-state. */
export function UnregisteredLine({ data }: { data?: LbUnregistered }) {
  const [open, setOpen] = useState(false);
  if (!data || data.parcels === 0) return null;
  return (
    <div className="border-t border-dashed border-border px-4 py-2 text-xs text-muted-foreground">
      <button
        onClick={() => setOpen((o) => !o)}
        className="underline decoration-dotted underline-offset-2 hover:text-foreground"
      >
        {data.parcels} parcels by {data.codes.length} unregistered codes — hidden from board
      </button>
      {open && (
        <ul className="mt-1 space-y-0.5 font-mono">
          {data.codes.map((c) => (
            <li key={c.code}>
              {c.code} · {c.count}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
```

- [ ] **Step 4: Mount on both pages**

`LiveBoard.tsx`: accept `unregistered` as a prop (threaded from its `useLeaderboard` call), render `{scope === "operators" && <UnregisteredLine data={unregistered} />}` after the rows container `</div>`, before `</section>`.

`LeaderboardTab.tsx`: destructure `unregistered` from its existing `useLeaderboard` call and render `{scope === "operators" && <UnregisteredLine data={unregistered} />}` directly after `<RankCards …/>`.

- [ ] **Step 5: Run** `npm test` and `npm run lint` — green.
- [ ] **Step 6: Commit**

```bash
git add app/components/leaderboard/UnregisteredLine.tsx app/components/leaderboard/UnregisteredLine.test.tsx \
  app/components/leaderboard/LiveBoard.tsx app/components/leaderboard/LeaderboardTab.tsx
git commit -m "feat(leaderboard): unregistered-codes footer line on both boards (wayfinder #70)"
```

### Task 12: Coverage one-token fix

**Files:**
- Modify: `app/lib/leaderboard.ts:70`
- Test: `app/lib/leaderboard.test.ts` (create — already added to the vitest include in Task 9)

**Context you must know:** `computeBoard` passes `row.videos` (video FILE count — several files per parcel possible) into `coverageOf`, so coverage can exceed 100%. `DrillPanel.tsx:348` already uses `videoedParcels`; this makes the board row agree with its own drilldown.

- [ ] **Step 1: Write the failing test**

`app/lib/leaderboard.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { computeBoard } from "./leaderboard";
import type { LeaderboardApiRow } from "../types";

const row = (over: Partial<LeaderboardApiRow>): LeaderboardApiRow => ({
  id: "op1", name: "Op", nick: "Op", sub: "op1", station: null, isStation: false,
  parcels: 10, items: 10, holds: 0, avgMinutesPerParcel: 1, workingDays: 1, workingHours: 1,
  daily14: Array(14).fill(0), image: null, videos: 0, videoedParcels: 0,
  platforms: [], holdsByStatus: [],
  ...over,
});

describe("coverage", () => {
  it("uses videoedParcels, never exceeding 100% on multi-file parcels", () => {
    const [r] = computeBoard(
      [row({ parcels: 10, videos: 23, videoedParcels: 8 })],
      "packing", undefined, { parcels: 1, qcPenalty: 1 },
    );
    expect(r.coverage).toBe(80); // 8/10, not 23/10 = 230
  });
});
```

(Adjust the `row()` literal to the real `LeaderboardApiRow` fields if any are missing/renamed — `types.ts:813-839`. Adjust `LbWeights` literal as in Task 8.)

- [ ] **Step 2: Run** `npm test -- app/lib/leaderboard` — FAIL (coverage 230).

- [ ] **Step 3: Implement** — `app/lib/leaderboard.ts:70`:

```ts
    coverage: discipline === "qc" ? 0 : coverageOf(row.parcels, row.videoedParcels),
```

Also update `coverageOf`'s stale "can exceed 100%" comment (`:23`) to reflect the parcel-based denominator.

- [ ] **Step 4: Run** `npm test` — green.
- [ ] **Step 5: Commit**

```bash
git add app/lib/leaderboard.ts app/lib/leaderboard.test.ts
git commit -m "fix(leaderboard): coverage uses videoedParcels, capping at 100%"
```

### Task 13: Frontend verification sweep

**Files:** none (verification only)

- [ ] **Step 1:** `npm test` — full suite green.
- [ ] **Step 2:** `npm run lint` — clean.
- [ ] **Step 3:** `npm run build` — compiles.
- [ ] **Step 4:** Manual smoke against the local stack (backend `cargo run` on :8080, `npm run dev` on :3000, per `reference_local-dev-stack`): `/live-leaderboard` shows Day chip; picking a past day shows the static grey pill; `/leaderboard` Day chip shows prev-day deltas; the unregistered line appears on a window containing `OP-1` rows and expands to the code list.
- [ ] **Step 5:** No commit (nothing changed); record results.
