# Invoice Orphan Parcel Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Shopee Express/Instant parcels whose `packing_lists.tracking_number` is a generated placeholder appear in the invoice workbook, without ever emitting that generated value into the file.

**Architecture:** The invoice export currently joins `packing_lists` → `import_rows` on `tracking_number` in three places that can drift. Replace all three with one shared SQL CTE that maps each selected parcel to its billing rows: direct match on `tracking_number` first, and only for parcels with no direct match, fall back to the newest `import_batches` batch carrying that parcel's `order_number`. Read-path only — no schema migration, no data mutation.

**Tech Stack:** Rust, Axum, SQLx (runtime-checked `sqlx::query*` — these are string-built queries, not macros), PostgreSQL, `tokio::test` integration tests against a live DB.

## Global Constraints

- Spec: `docs/specs/2026-08-01-invoice-orphan-parcel-resolution.md`. Read it before starting.
- Worktree: `backend/.worktrees/line-fingerprint`, branch `fix/product-line-fingerprint`, base `feat/export-drawer`. All paths below are relative to that worktree.
- Domain rule, authoritative: **if a line has a valid tracking number from import, use it; otherwise leave it blank; never emit our generated placeholder.**
- Never write to `import_rows` or `raw_data`. `import_rows.tracking_number` is a GENERATED column off `raw_data`, and `raw_data` is rendered directly into the customer-facing workbook.
- No new migration, no new column, no backfill.
- `select_parcels` is not modified by any task in this plan.
- Only `src/api/exports/invoices.rs` and `tests/warehouse_invoice.rs` change.
- Tests require a live PostgreSQL and MinIO. Bring the stack up first: `docker compose -f docker/compose.yml -f docker/compose.db.yml up -d` from the monorepo root, and export `DATABASE_URL`.
- The base branch has **pre-existing** failures in `product_insights` and `dashboard_api`. Always run with `--no-fail-fast` and never attribute those two suites to this change.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/api/exports/invoices.rs` | Invoice preview + generate handlers | Add `RESOLVED_ROWS` CTE constant; rewrite three queries to use it | 
| `tests/warehouse_invoice.rs` | Integration tests for preview + generate | Add two seed helpers and seven tests |

No new files. `invoices.rs` is 599 lines and cohesive; the CTE constant belongs beside the handlers that consume it.

---

### Task 1: Resolution CTE and `generate` fallback

**Files:**
- Modify: `src/api/exports/invoices.rs` (add constant near the top, beside `EXCLUSIONS`; rewrite the query at `:449-458`)
- Test: `tests/warehouse_invoice.rs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `const RESOLVED_ROWS: &str` — a CTE body (no leading `WITH`) exposing a relation named `resolved` with columns `parcel TEXT`, `raw_data JSONB`, `batch_id INT`, `platform VARCHAR`, `order_number VARCHAR`, `id BIGINT`. Binds `$1` = selected tracking numbers (`&[String]`). Tasks 3 and 4 consume it. Test helpers `seed_parcel_with_order` and `orphan_raw` are produced here and consumed by Tasks 2, 3, 4.

- [ ] **Step 1: Add the two seed helpers**

The existing `seed_parcel` writes `VALUES ($1, $1, …)`, i.e. `tracking_number = order_number`. Orphan tests need a parcel whose order number differs, and rows whose tracking is empty. Add both helpers next to `seed_parcel` in `tests/warehouse_invoice.rs`:

```rust
/// Like `seed_parcel` but with an explicit `order_number` distinct from the
/// tracking number — needed for promoted-parcel and ownership-guard cases.
async fn seed_parcel_with_order(
    pool: &sqlx::PgPool,
    tn: &str,
    order: &str,
    shipped_at: Option<DateTime<Utc>>,
) {
    sqlx::query(
        "INSERT INTO packing_lists (tracking_number, order_number, platform, packing_status,
                                    created_at, updated_at, total_items, shipped_at)
         VALUES ($1, $2, 'Shopee', 'Shipped', now(), now(), 1, $3)
         ON CONFLICT (tracking_number) DO UPDATE
           SET order_number = EXCLUDED.order_number,
               shipped_at = EXCLUDED.shipped_at",
    )
    .bind(tn)
    .bind(order)
    .bind(shipped_at)
    .execute(pool)
    .await
    .unwrap();
}

/// An import row with an EMPTY tracking number — what Shopee actually sends for
/// Express/Instant orders before a courier number is assigned.
fn orphan_raw(order: &str, sku: &str) -> serde_json::Value {
    json!({
        "tracking_number": "",
        "order_number": order,
        "seller_sku": sku,
        "_raw": { "Order ID": order, "Tracking ID": "", "SKU": sku }
    })
}
```

- [ ] **Step 2: Write the failing test — placeholder parcel is recovered with a blank tracking cell**

Add to `tests/warehouse_invoice.rs`:

```rust
#[tokio::test]
async fn generate_recovers_placeholder_parcel_with_blank_tracking() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    // Placeholder shape: tracking_number == order_number (what aggregator.rs writes).
    let t = format!("WHI-PH-{ts}");

    seed_parcel(&pool, &t, Some(at), None, None).await;
    let b = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    // Import rows carry EMPTY tracking, keyed only by order_number.
    seed_row(&pool, b, "Shopee", &format!("nk-{ts}-1"), orphan_raw(&t, "SKU-A")).await;
    seed_row(&pool, b, "Shopee", &format!("nk-{ts}-2"), orphan_raw(&t, "SKU-B")).await;

    let resp = reqwest::Client::new()
        .post(format!("{base}/exports/invoices/generate"))
        .json(&json!({
            "platform": "Shopee", "condition": "shipped",
            "from": from.to_rfc3339(), "to": to.to_rfc3339(),
            "exportedBy": "whi-test"
        }))
        .send()
        .await
        .unwrap();
    assert_eq!(resp.status(), 200, "generate should not 400 on a recovered parcel");

    // The audit snapshot is the observable proof of which lines were billed.
    let export_id: i32 = resp
        .headers()
        .get("X-Export-Id")
        .unwrap()
        .to_str()
        .unwrap()
        .parse()
        .unwrap();
    let (parcel, lines): (String, serde_json::Value) = sqlx::query_as(
        "SELECT tracking_number, lines FROM invoice_export_items WHERE export_id = $1",
    )
    .bind(export_id)
    .fetch_one(&pool)
    .await
    .unwrap();

    assert_eq!(parcel, t, "audit row must key on the packing_lists parcel key");
    let lines = lines.as_array().unwrap();
    assert_eq!(lines.len(), 2, "both order lines billed: {lines:?}");
    // The generated placeholder must never reach the workbook's tracking column.
    // raw_data is what gets rendered, so assert it stayed empty.
    let raws: Vec<String> = sqlx::query_scalar(
        "SELECT raw_data->>'tracking_number' FROM import_rows WHERE order_number = $1",
    )
    .bind(&t)
    .fetch_all(&pool)
    .await
    .unwrap();
    assert!(raws.iter().all(|s| s.is_empty()), "tracking must stay blank: {raws:?}");
}
```

- [ ] **Step 3: Run it and confirm it fails**

```bash
cargo test --test warehouse_invoice generate_recovers_placeholder_parcel -- --nocapture
```

Expected: FAIL. `generate` returns 400 `"no shipped parcels with imported Shopee order rows in this window"`, because the `tracking_number` join matches nothing.

- [ ] **Step 4: Add the `RESOLVED_ROWS` constant**

In `src/api/exports/invoices.rs`, directly below the `EXCLUSIONS` constant inside `select_parcels`' module scope (top level of the file, beside `XLSX_CONTENT_TYPE`):

```rust
/// Maps each selected parcel to the import rows that bill it. Defined once and
/// shared by `preview`'s two queries and `generate` so the three can never
/// disagree about what is billable (spec §4).
///
/// `$1` is the selected `packing_lists.tracking_number` array. Exposes a
/// relation `resolved(parcel, raw_data, batch_id, platform, order_number, id)`.
///
/// `direct` is the historical behaviour and covers every healthy parcel.
/// `orphan` is defined structurally — "has no directly-matched rows" — never by
/// pattern-matching the tracking value, so a real tracking number can never be
/// misclassified and the fallback stays inert whenever the direct join works.
/// `fallback` takes the newest batch's rows and only those, which is what keeps
/// a promoted parcel's duplicate lines (one empty-tracking batch, one
/// real-tracking batch) from both being billed.
const RESOLVED_ROWS: &str = r#"
    sel AS (
        SELECT tracking_number, order_number FROM packing_lists
        WHERE tracking_number = ANY($1)
    ),
    direct AS (
        SELECT s.tracking_number AS parcel, ir.*
        FROM sel s JOIN import_rows ir ON ir.tracking_number = s.tracking_number
    ),
    orphan AS (
        SELECT * FROM sel s
        WHERE NOT EXISTS (SELECT 1 FROM direct d WHERE d.parcel = s.tracking_number)
    ),
    fallback AS (
        SELECT o.tracking_number AS parcel, ir.*,
               rank() OVER (PARTITION BY o.tracking_number ORDER BY ir.batch_id DESC) AS rk
        FROM orphan o JOIN import_rows ir ON ir.order_number = o.order_number
    ),
    resolved AS (
        SELECT parcel, raw_data, batch_id, platform, order_number, id FROM direct
        UNION ALL
        SELECT parcel, raw_data, batch_id, platform, order_number, id
        FROM fallback WHERE rk = 1
    )
"#;
```

- [ ] **Step 5: Rewrite `generate`'s row query**

Replace the `sqlx::query_as` block at `src/api/exports/invoices.rs:449-458` with:

```rust
    let rows: Vec<(serde_json::Value, i32, String)> = sqlx::query_as(&format!(
        r#"WITH {RESOLVED_ROWS}
           SELECT r.raw_data, r.batch_id, r.parcel
           FROM resolved r
           WHERE r.platform = $2
           ORDER BY r.order_number NULLS LAST, r.id"#
    ))
    .bind(&trackings)
    .bind(&req.platform)
    .fetch_all(&pool)
    .await?;
```

`order_number` and `id` are carried through the CTE rather than projected away because that sort is both the workbook's row order and the order `record_export` snapshots billed lines in. `record_export` itself needs no change — its third tuple element is now `parcel`, which equals `ir.tracking_number` for every parcel that already works.

- [ ] **Step 6: Run the test and confirm it passes**

```bash
cargo test --test warehouse_invoice generate_recovers_placeholder_parcel -- --nocapture
```

Expected: PASS.

- [ ] **Step 7: Write the failing test — promoted parcel is not double-billed**

```rust
#[tokio::test]
async fn generate_does_not_double_bill_promoted_parcel() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let order = format!("WHI-ORD-{ts}");
    let real_tn = format!("WHI-REAL-{ts}");

    // Promoted: packing_lists now carries the real tracking Shopee issued.
    seed_parcel_with_order(&pool, &real_tn, &order, Some(at)).await;

    // Older batch: the pre-promotion row, empty tracking.
    let b_old = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, b_old, "Shopee", &format!("nk-{ts}-old"), orphan_raw(&order, "SKU-A")).await;
    // Newer batch: the same line again, now carrying real tracking.
    let b_new = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, b_new, "Shopee", &format!("nk-{ts}-new"), order_raw(&real_tn, &order)).await;

    let resp = reqwest::Client::new()
        .post(format!("{base}/exports/invoices/generate"))
        .json(&json!({
            "platform": "Shopee", "condition": "shipped",
            "from": from.to_rfc3339(), "to": to.to_rfc3339(),
            "exportedBy": "whi-test"
        }))
        .send()
        .await
        .unwrap();
    assert_eq!(resp.status(), 200);
    let export_id: i32 = resp
        .headers()
        .get("X-Export-Id")
        .unwrap()
        .to_str()
        .unwrap()
        .parse()
        .unwrap();

    let row_count: i32 = sqlx::query_scalar("SELECT row_count FROM invoice_exports WHERE id = $1")
        .bind(export_id)
        .fetch_one(&pool)
        .await
        .unwrap();
    assert_eq!(row_count, 1, "the same order line must be billed exactly once");
}
```

- [ ] **Step 8: Run it and confirm it passes**

```bash
cargo test --test warehouse_invoice generate_does_not_double_bill -- --nocapture
```

Expected: PASS without further code change. This parcel has a direct match on `real_tn`, so `orphan` excludes it and the older empty-tracking row is never considered. The test exists to lock that property in — it is the regression that a naive `OR order_number` fix would break.

- [ ] **Step 9: Commit**

```bash
git add src/api/exports/invoices.rs tests/warehouse_invoice.rs
git commit -m "fix(exports): resolve orphaned parcels to their newest import batch

Shopee Express/Instant orders arrive with empty tracking, so aggregator.rs
substitutes the order number as a placeholder key in packing_lists only.
import_rows.tracking_number is a generated column off raw_data and keeps the
empty string, so the invoice export's tracking_number join dropped these
parcels silently.

Add a shared RESOLVED_ROWS CTE: direct tracking match first, and for parcels
with no direct match, fall back to the newest batch carrying their
order_number. Wire generate to it. raw_data is never written, so the generated
placeholder cannot reach the workbook's tracking column."
```

---

### Task 2: Ownership guard

**Files:**
- Modify: `src/api/exports/invoices.rs` (the `fallback` CTE inside `RESOLVED_ROWS`)
- Test: `tests/warehouse_invoice.rs`

**Interfaces:**
- Consumes: `RESOLVED_ROWS`, `seed_parcel_with_order`, `orphan_raw` from Task 1.
- Produces: no new symbols.

`order_number` is not unique in `packing_lists` — 4,958 order numbers are shared across parcels. Without a guard, an orphaned parcel can bill a sibling parcel's lines.

- [ ] **Step 1: Write the failing test**

```rust
#[tokio::test]
async fn fallback_does_not_steal_a_sibling_parcels_rows() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let order = format!("WHI-SHARED-{ts}");
    let orphan_tn = format!("WHI-ORPH-{ts}");
    let sibling_tn = format!("WHI-SIB-{ts}");

    // Two parcels sharing one order number: one orphaned, one healthy.
    seed_parcel_with_order(&pool, &orphan_tn, &order, Some(at)).await;
    seed_parcel_with_order(&pool, &sibling_tn, &order, Some(at)).await;

    // Only the sibling has an import row, and it is owned by the sibling.
    let b = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, b, "Shopee", &format!("nk-{ts}-sib"), order_raw(&sibling_tn, &order)).await;

    let resp = reqwest::Client::new()
        .post(format!("{base}/exports/invoices/generate"))
        .json(&json!({
            "platform": "Shopee", "condition": "shipped",
            "from": from.to_rfc3339(), "to": to.to_rfc3339(),
            "exportedBy": "whi-test"
        }))
        .send()
        .await
        .unwrap();
    assert_eq!(resp.status(), 200);
    let export_id: i32 = resp
        .headers()
        .get("X-Export-Id")
        .unwrap()
        .to_str()
        .unwrap()
        .parse()
        .unwrap();

    let billed: Vec<String> = sqlx::query_scalar(
        "SELECT tracking_number FROM invoice_export_items WHERE export_id = $1 ORDER BY 1",
    )
    .bind(export_id)
    .fetch_all(&pool)
    .await
    .unwrap();
    assert_eq!(
        billed,
        vec![sibling_tn.clone()],
        "the orphan must not bill the sibling's line: {billed:?}"
    );
}
```

- [ ] **Step 2: Run it and confirm it fails**

```bash
cargo test --test warehouse_invoice fallback_does_not_steal -- --nocapture
```

Expected: FAIL — `billed` contains both `WHI-ORPH-…` and `WHI-SIB-…`, and `row_count` is 2 for one real order line. The orphan grabbed the sibling's row via `order_number`.

- [ ] **Step 3: Add the guard to the `fallback` CTE**

In `RESOLVED_ROWS`, add the `WHERE NOT EXISTS` clause to `fallback`:

```rust
    fallback AS (
        SELECT o.tracking_number AS parcel, ir.*,
               rank() OVER (PARTITION BY o.tracking_number ORDER BY ir.batch_id DESC) AS rk
        FROM orphan o JOIN import_rows ir ON ir.order_number = o.order_number
        WHERE NOT EXISTS (
            SELECT 1 FROM packing_lists p WHERE p.tracking_number = ir.tracking_number
        )
    ),
```

The exclusion is a `WHERE`, so Postgres applies it before the window function — an excluded newest batch correctly falls through to the next batch instead of yielding nothing. Safe against the degenerate case: `packing_lists` has zero rows with an empty-string `tracking_number`, so empty-tracking rows are never excluded wholesale.

- [ ] **Step 4: Run the test and confirm it passes**

```bash
cargo test --test warehouse_invoice fallback_does_not_steal -- --nocapture
```

Expected: PASS.

- [ ] **Step 5: Re-run Task 1's tests to confirm the guard did not break recovery**

```bash
cargo test --test warehouse_invoice generate_recovers_placeholder_parcel generate_does_not_double_bill -- --nocapture
```

Expected: both PASS.

- [ ] **Step 6: Commit**

```bash
git add src/api/exports/invoices.rs tests/warehouse_invoice.rs
git commit -m "fix(exports): never let the orphan fallback bill a sibling parcel's rows

order_number is not unique in packing_lists. Exclude import rows whose
tracking_number is already owned by some parcel, so a split order cannot have
one parcel silently billed twice."
```

---

### Task 3: Preview missing-check

**Files:**
- Modify: `src/api/exports/invoices.rs:315-322`
- Test: `tests/warehouse_invoice.rs`

**Interfaces:**
- Consumes: `RESOLVED_ROWS`, `seed_parcel_with_order`, `orphan_raw`.
- Produces: no new symbols.

- [ ] **Step 1: Write the failing test**

```rust
#[tokio::test]
async fn preview_missing_excludes_recoverable_orphans_only() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let recoverable = format!("WHI-REC-{ts}");   // orphan WITH import rows
    let unrecoverable = format!("WHI-DEL-{ts}"); // deleted-batch parcel: no rows anywhere

    seed_parcel(&pool, &recoverable, Some(at), None, None).await;
    seed_parcel(&pool, &unrecoverable, Some(at), None, None).await;

    let b = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, b, "Shopee", &format!("nk-{ts}-r"), orphan_raw(&recoverable, "SKU-A")).await;

    let body = get_preview(&base, "shipped", from, to).await;
    let missing: Vec<&str> = body["missing"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| v.as_str().unwrap())
        .collect();

    assert_eq!(
        missing,
        vec![unrecoverable.as_str()],
        "recoverable orphan must leave `missing`; deleted-batch parcel must stay: {body}"
    );
}
```

- [ ] **Step 2: Run it and confirm it fails**

```bash
cargo test --test warehouse_invoice preview_missing_excludes_recoverable -- --nocapture
```

Expected: FAIL — `missing` contains both parcels, because the check still tests `ir.tracking_number = t`.

- [ ] **Step 3: Rewrite the missing-check query**

Replace the `missing` query at `src/api/exports/invoices.rs:315-322` with:

```rust
    let missing: Vec<String> = sqlx::query_scalar(&format!(
        r#"WITH {RESOLVED_ROWS}
           SELECT t FROM UNNEST($1::text[]) AS t
           WHERE NOT EXISTS (SELECT 1 FROM resolved r WHERE r.parcel = t)
           ORDER BY t"#
    ))
    .bind(&trackings)
    .fetch_all(&pool)
    .await?;
```

`missing` now means genuinely unrecoverable, instead of lumping recoverable orphans in with deleted-batch parcels.

- [ ] **Step 4: Run the test and confirm it passes**

```bash
cargo test --test warehouse_invoice preview_missing_excludes_recoverable -- --nocapture
```

Expected: PASS.

- [ ] **Step 5: Confirm the pre-existing missing-check test still passes**

```bash
cargo test --test warehouse_invoice preview_counts_missing_and_cancelled -- --nocapture
```

Expected: PASS. Its `WHI-MISS-…` parcel has no import rows under its tracking number *or* its order number, so it stays in `missing`.

- [ ] **Step 6: Commit**

```bash
git add src/api/exports/invoices.rs tests/warehouse_invoice.rs
git commit -m "fix(exports): preview missing-check reports only unrecoverable parcels

Route the missing-check through RESOLVED_ROWS so recoverable orphans stop
appearing alongside genuinely un-invoiceable deleted-batch parcels."
```

---

### Task 4: Preview platform aggregation and counter corrections

**Files:**
- Modify: `src/api/exports/invoices.rs:294-313`
- Test: `tests/warehouse_invoice.rs`

**Interfaces:**
- Consumes: `RESOLVED_ROWS`, `orphan_raw`.
- Produces: no new symbols.

Two counters are keyed on `import_rows.tracking_number` and only *work* today because that value coincides with the parcel key for healthy parcels. Once the fallback returns rows, both break.

- [ ] **Step 1: Write the failing test**

```rust
#[tokio::test]
async fn preview_counts_recovered_orphans_per_parcel() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let a = format!("WHI-PA-{ts}");
    let b_tn = format!("WHI-PB-{ts}");

    // Two distinct orphaned parcels, each with one empty-tracking import row.
    seed_parcel(&pool, &a, Some(at), None, None).await;
    seed_parcel(&pool, &b_tn, Some(at), None, None).await;
    let batch = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, batch, "Shopee", &format!("nk-{ts}-a"), orphan_raw(&a, "SKU-A")).await;
    seed_row(&pool, batch, "Shopee", &format!("nk-{ts}-b"), orphan_raw(&b_tn, "SKU-B")).await;

    let body = get_preview(&base, "shipped", from, to).await;
    let platforms = body["platforms"].as_array().unwrap();
    assert_eq!(platforms.len(), 1, "one platform expected: {body}");
    assert_eq!(
        platforms[0]["parcels"], 2,
        "two distinct parcels, not one '' bucket: {body}"
    );
    assert_eq!(platforms[0]["rows"], 2, "{body}");
    assert_eq!(platforms[0]["alreadyExported"], 0, "{body}");
    assert!(
        body["missing"].as_array().unwrap().is_empty(),
        "both parcels resolved: {body}"
    );
}
```

- [ ] **Step 2: Run it and confirm it fails**

```bash
cargo test --test warehouse_invoice preview_counts_recovered_orphans -- --nocapture
```

Expected: FAIL — `platforms` is empty, because the aggregation still joins on `ir.tracking_number = ANY($1)` and matches nothing.

- [ ] **Step 3: Rewrite the platform aggregation query**

Replace the `platforms` query at `src/api/exports/invoices.rs:294-313` with:

```rust
    let platforms: Vec<PlatformPreview> = sqlx::query_as(&format!(
        r#"WITH {RESOLVED_ROWS}
           SELECT r.platform,
                  COUNT(DISTINCT r.parcel) AS parcels,
                  COUNT(*) AS row_count,
                  COUNT(DISTINCT r.order_number) AS orders,
                  COUNT(DISTINCT r.batch_id) AS batches,
                  (COUNT(DISTINCT ib.header_layout::text) > 1) AS layout_mismatch,
                  COUNT(DISTINCT r.parcel) FILTER (
                      WHERE EXISTS (SELECT 1 FROM invoice_export_items iei
                                    WHERE iei.tracking_number = r.parcel)
                  ) AS already_exported
           FROM resolved r
           JOIN import_batches ib ON ib.id = r.batch_id
           GROUP BY r.platform
           ORDER BY r.platform"#
    ))
    .bind(&trackings)
    .fetch_all(&pool)
    .await?;
```

Two corrections ride along, both latent bugs that only become reachable now:
- `COUNT(DISTINCT ir.tracking_number) AS parcels` → `COUNT(DISTINCT r.parcel)`. On the old key, fallback rows would collapse into a single `''` bucket and report one parcel however many resolved.
- `already_exported`'s `iei.tracking_number = ir.tracking_number` → `= r.parcel`. `invoice_export_items` stores the `packing_lists` key.

`orders`, `batches` and `layout_mismatch` keep their definitions; the `import_batches` join is retained, now against the CTE's `batch_id`.

- [ ] **Step 4: Run the test and confirm it passes**

```bash
cargo test --test warehouse_invoice preview_counts_recovered_orphans -- --nocapture
```

Expected: PASS.

- [ ] **Step 5: Write and run the preview/generate agreement test**

```rust
#[tokio::test]
async fn preview_parcel_count_matches_generated_file() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let orphan_tn = format!("WHI-AGR-O-{ts}");
    let healthy_tn = format!("WHI-AGR-H-{ts}");

    seed_parcel(&pool, &orphan_tn, Some(at), None, None).await;
    seed_parcel(&pool, &healthy_tn, Some(at), None, None).await;
    let batch = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, batch, "Shopee", &format!("nk-{ts}-o"), orphan_raw(&orphan_tn, "SKU-A")).await;
    seed_row(
        &pool, batch, "Shopee", &format!("nk-{ts}-h"),
        order_raw(&healthy_tn, &format!("ORD-{ts}")),
    )
    .await;

    let body = get_preview(&base, "shipped", from, to).await;
    let previewed = body["platforms"][0]["parcels"].as_i64().unwrap();

    let resp = reqwest::Client::new()
        .post(format!("{base}/exports/invoices/generate"))
        .json(&json!({
            "platform": "Shopee", "condition": "shipped",
            "from": from.to_rfc3339(), "to": to.to_rfc3339(),
            "exportedBy": "whi-test"
        }))
        .send()
        .await
        .unwrap();
    assert_eq!(resp.status(), 200);
    let export_id: i32 = resp
        .headers()
        .get("X-Export-Id")
        .unwrap()
        .to_str()
        .unwrap()
        .parse()
        .unwrap();
    let generated: i32 = sqlx::query_scalar(
        "SELECT parcel_count FROM invoice_exports WHERE id = $1",
    )
    .bind(export_id)
    .fetch_one(&pool)
    .await
    .unwrap();

    assert_eq!(previewed, generated as i64, "preview and generate must agree");
    assert_eq!(previewed, 2);
}
```

```bash
cargo test --test warehouse_invoice preview_parcel_count_matches -- --nocapture
```

Expected: PASS.

- [ ] **Step 6: Write and run the `already_exported` / `invoiced_at` audit test**

```rust
#[tokio::test]
async fn recovered_orphan_is_stamped_and_flagged_already_exported() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let t = format!("WHI-AUD-{ts}");

    seed_parcel(&pool, &t, Some(at), None, None).await;
    let batch = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, batch, "Shopee", &format!("nk-{ts}-1"), orphan_raw(&t, "SKU-A")).await;

    let gen_body = json!({
        "platform": "Shopee", "condition": "shipped",
        "from": from.to_rfc3339(), "to": to.to_rfc3339(),
        "exportedBy": "whi-test"
    });
    let first = reqwest::Client::new()
        .post(format!("{base}/exports/invoices/generate"))
        .json(&gen_body)
        .send()
        .await
        .unwrap();
    assert_eq!(first.status(), 200);

    let stamped: Option<DateTime<Utc>> =
        sqlx::query_scalar("SELECT invoiced_at FROM packing_lists WHERE tracking_number = $1")
            .bind(&t)
            .fetch_one(&pool)
            .await
            .unwrap();
    assert!(stamped.is_some(), "invoiced_at must be stamped on the parcel key");

    let body = get_preview(&base, "shipped", from, to).await;
    assert_eq!(
        body["platforms"][0]["alreadyExported"], 1,
        "re-preview must see the parcel as already exported: {body}"
    );
}
```

```bash
cargo test --test warehouse_invoice recovered_orphan_is_stamped -- --nocapture
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/api/exports/invoices.rs tests/warehouse_invoice.rs
git commit -m "fix(exports): count preview parcels and already-exported on the parcel key

Route the platform aggregation through RESOLVED_ROWS and key both
COUNT(DISTINCT parcels) and the already_exported filter on the packing_lists
parcel instead of import_rows.tracking_number, which only coincided for
healthy parcels."
```

---

### Task 5: Regression proof and live verification

**Files:**
- Test: `tests/warehouse_invoice.rs`
- No source changes.

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: nothing.

- [ ] **Step 1: Write the inertness regression test**

```rust
#[tokio::test]
async fn healthy_parcel_resolution_is_unchanged() {
    let (base, pool) = spawn_app().await;
    let (at, from, to) = unique_past_window();
    let ts = nanos();
    let t = format!("WHI-HEALTHY-{ts}");
    let order = format!("ORD-{ts}");

    // A perfectly normal Standard Delivery parcel: real tracking on both sides.
    seed_parcel_with_order(&pool, &t, &order, Some(at)).await;
    let batch = seed_batch(&pool, "Shopee", simple_layout(), simple_mapping()).await;
    seed_row(&pool, batch, "Shopee", &format!("nk-{ts}-1"), order_raw(&t, &order)).await;
    // A decoy row under the SAME order number but a different, unowned tracking.
    // If the fallback ever fired for a healthy parcel it would pull this in.
    seed_row(
        &pool, batch, "Shopee", &format!("nk-{ts}-decoy"),
        order_raw(&format!("WHI-DECOY-{ts}"), &order),
    )
    .await;

    let body = get_preview(&base, "shipped", from, to).await;
    assert_eq!(
        body["platforms"][0]["rows"], 1,
        "fallback must stay inert for a parcel with a direct match: {body}"
    );
    assert_eq!(body["platforms"][0]["parcels"], 1, "{body}");
}
```

- [ ] **Step 2: Run it**

```bash
cargo test --test warehouse_invoice healthy_parcel_resolution_is_unchanged -- --nocapture
```

Expected: PASS. This is the highest-weight test in the plan — it proves the change is inert for the ~60,523 parcels that already work.

- [ ] **Step 3: Run the whole invoice suite**

```bash
cargo test --test warehouse_invoice -- --no-fail-fast
```

Expected: all PASS, including the pre-existing tests.

- [ ] **Step 4: Run the full test suite and compare against the baseline**

```bash
cargo test --no-fail-fast 2>&1 | tail -40
```

Expected: only `product_insights` and `dashboard_api` fail. Those are pre-existing on `feat/export-drawer`. If anything else fails, stop and investigate — do not proceed.

- [ ] **Step 5: Live verification against the snapshot**

Confirm the spec's §6 numbers hold against real data. From the monorepo root:

```bash
docker exec warehouse-postgres psql -U warehouse_user -d warehouse_snapshot -c "
WITH sel AS (SELECT tracking_number, order_number FROM packing_lists
             WHERE created_at>='2026-06-13' AND tracking_number NOT LIKE 'WHI-%'
               AND (order_status IS NULL OR order_status<>'Cancelled') AND returned_at IS NULL),
direct AS (SELECT DISTINCT s.tracking_number AS parcel FROM sel s
           JOIN import_rows ir ON ir.tracking_number=s.tracking_number),
orphan AS (SELECT * FROM sel s WHERE NOT EXISTS (SELECT 1 FROM direct d WHERE d.parcel=s.tracking_number)),
fb AS (SELECT o.tracking_number AS parcel, ir.tracking_number AS raw_tn,
         rank() OVER (PARTITION BY o.tracking_number ORDER BY ir.batch_id DESC) rk
       FROM orphan o JOIN import_rows ir ON ir.order_number=o.order_number
       WHERE NOT EXISTS (SELECT 1 FROM packing_lists p WHERE p.tracking_number=ir.tracking_number))
SELECT (SELECT count(*) FROM orphan) AS orphans_today,
       count(DISTINCT parcel) AS recovered, count(*) AS rows_emitted,
       count(*) FILTER (WHERE coalesce(raw_tn,'')<>'') AS rows_real_tracking
FROM fb WHERE rk=1;"
```

Expected: `orphans_today` 731, `recovered` 111, `rows_emitted` 169, `rows_real_tracking` 2. Small drift is fine if new imports have landed; a large drop means the guard is over-excluding.

- [ ] **Step 6: Verify the reported parcel end-to-end**

Run the backend against `warehouse_snapshot`, then generate in number mode for the parcel from the bug report:

```bash
curl -s -X POST localhost:8080/exports/invoices/generate \
  -H 'content-type: application/json' \
  -d '{"platform":"Shopee","condition":"shipped","from":"2026-07-01T00:00:00Z","to":"2026-08-01T00:00:00Z","numbers":"2607290MY5Y1U8","exportedBy":"verify"}' \
  -o /tmp/verify.xlsx -D -
```

Expected: HTTP 200 with an `X-Export-Id` header. Then confirm the two lines are present and the tracking cell is blank:

```bash
python3 -c "
import openpyxl
ws = openpyxl.load_workbook('/tmp/verify.xlsx').active
hdr = {ws.cell(1,c).value: c for c in range(1, ws.max_column+1)}
col = hdr['*หมายเลขติดตามพัสดุ']
vals = [ws.cell(r,col).value for r in range(2, ws.max_row+1)]
print('rows:', ws.max_row-1, 'tracking cells:', vals)
assert all(v in (None,'') for v in vals), 'PLACEHOLDER LEAKED: %r' % vals
print('OK: no generated tracking number in the workbook')
"
```

Expected: `rows: 2`, all tracking cells empty, `OK:` printed. **This is the acceptance criterion for the whole plan.**

- [ ] **Step 7: Commit**

```bash
git add tests/warehouse_invoice.rs
git commit -m "test(exports): prove orphan fallback is inert for healthy parcels

A decoy row shares the healthy parcel's order_number under a different
tracking number; the parcel must still bill exactly one row."
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §4 resolution seam / CTE | Task 1 Step 4 |
| §4.1 structural orphan detection | Task 1 Step 4 (`orphan` definition) |
| §4.2 newest batch wins | Task 1 Step 4 (`rank()`), tested Task 1 Step 7 |
| §4.3 ownership guard | Task 2 |
| §5.1 `generate` + `record_export` | Task 1 Step 5 |
| §5.2 platform aggregation + counters | Task 4 Steps 3 |
| §5.3 missing-check | Task 3 |
| §6 expected effect | Task 5 Step 5 |
| §8 test 1 placeholder recovered | Task 1 Step 2 |
| §8 test 2 no double-bill | Task 1 Step 7 |
| §8 test 3 real tracking preferred | Task 1 Step 7 (the newer batch carries real tracking and is the one billed) |
| §8 test 4 ownership guard | Task 2 Step 1 |
| §8 test 5 deleted-batch stays missing | Task 3 Step 1 |
| §8 test 6 preview/generate agreement | Task 4 Step 5 |
| §8 test 7 audit contract | Task 4 Step 6 |
| §8 test 8 healthy parcel regression | Task 5 Step 1 |
| §9 verification before merge | Task 5 Steps 3-6 |

No gaps.

**Placeholder scan:** No TBD/TODO. Every code step carries runnable code; every run step carries the exact command and expected outcome.

**Type consistency:** `RESOLVED_ROWS` exposes `resolved(parcel, raw_data, batch_id, platform, order_number, id)` and every consuming query in Tasks 1, 3 and 4 selects only from that column set. `generate` continues to bind `Vec<(serde_json::Value, i32, String)>`, matching `record_export`'s `rows: &[(serde_json::Value, i32, String)]` signature unchanged. Helper names `seed_parcel_with_order` and `orphan_raw` are used consistently across all five tasks.
