# Research: Export backend surface for invoice-export drawer filters

Wayfinder research ticket #77 (map #75). Date: 2026-07-23.

Pinned refs (all file:line references below point at these, not the working tree):

- Backend: `naff-warehouse-backend` @ `origin/dev-1.3` = `05b7505` (merge of PR #53 `feat/warehouse-invoice`)
- Frontend: `naff-warehouse-frontend` @ `origin/dev-1.1` = `687e02f`

## Summary

The invoice export pipeline is: `select_parcels` (packing_lists timestamp window +
live cancel exclusion → tracking-number set) → join into `import_rows` for preview
counts / raw row payloads → `rebuild` seam (newest-batch header layout →
`generate_xlsx` flat rebuild) → per-platform Python invoice transform → audit write
(`invoice_exports` + `invoice_export_items` snapshot + `packing_lists.invoiced_at`
COALESCE stamp, one transaction). Every proposed drawer filter has a natural column
to bind on — platform, shipping option, order/tracking numbers, and invoiced state
all live on `packing_lists` (import-time denormalized), so all new predicates can go
into `select_parcels` without touching the rebuild or transform layers. Row-selection
narrowing is safe downstream: `generate_xlsx` writes no formulas/totals, and all
three Python transforms are strictly row-wise (the only formula emitted is Tiktok's
same-row `=before-seller`). The real spec landmines are: (a) the `Condition` enum is
baked into the audit row (`invoice_exports.condition NOT NULL varchar`) and the
filename; a lifecycle multi-select needs a new audit representation; (b) axum's
`Query` extractor cannot deserialize repeated query params into `Vec` — multi-selects
on the GET preview need CSV params, `axum_extra::Query`, or a switch to POST;
(c) `returned_at`'s index has a status-scoped partial predicate that a pure
`returned_at` range scan cannot use (the exact trap already fixed once for
`shipped_at`); (d) `invoiced_at` and the `already_exported` EXISTS check can disagree
after Instant/Express placeholder promotion, because `invoice_export_items`
deliberately has no FK and keeps the old placeholder tracking number.

## 1. Invoice endpoints — `src/api/exports/invoices.rs`

Routes mounted at `src/api/mod.rs:265-266`:
`GET /exports/invoices/preview`, `POST /exports/invoices/generate`.

### Condition + select_parcels

- `Condition` enum (`invoices.rs:32-56`): `shipped | packed`, lowercase serde.
  `ts_column()` maps to `shipped_at` / `packed_at` — the **only** strings ever
  `format!`-interpolated into the SQL (compile-time constants; everything else is
  bound). Selection is pure-timestamp, NOT `packing_status` (grilled decision
  2026-07-04, header comment `invoices.rs:13-15`).
- `select_parcels` (`invoices.rs:62-90`): two queries over `packing_lists`:
  1. `SELECT tracking_number WHERE {col} >= $1 AND {col} <= $2 AND
     (order_status IS NULL OR order_status <> 'Cancelled')` → `Vec<String>` trackings.
  2. `COUNT(*)` of the same window with `order_status = 'Cancelled'` →
     `cancelled_excluded`.
  Live `packing_lists.order_status` beats the import-time snapshot in `raw_data`
  (cancellation-detection loop in `src/api/imports.rs:398-412` stamps it).

### Preview

- `PreviewQuery` (`invoices.rs:92-97`): `condition`, `from`, `to` (UTC `DateTime`).
- Per-platform aggregate (`invoices.rs:128-147`): joins the tracking set into
  `import_rows ir JOIN import_batches ib`, `GROUP BY ir.platform`, returning
  `PlatformPreview` (`invoices.rs:99-111`): `platform`, `parcels`
  (DISTINCT tracking), `rows` (row_count), `orders` (DISTINCT order_number),
  `batches`, `layoutMismatch` (`COUNT(DISTINCT ib.header_layout::text) > 1`), and
  `already_exported` = DISTINCT trackings with an `EXISTS` row in
  `invoice_export_items` — **any export ever, not window-scoped, not
  platform-scoped beyond the GROUP BY**.
- `missing` (`invoices.rs:149-156`): UNNEST of the tracking set with no
  `import_rows` match at all — a flat `Vec<String>` in the response
  (`PreviewResponse` `invoices.rs:113-119`: `platforms`, `missing`,
  `cancelledExcluded`). Unbounded list; a wide multi-filter window can make it big.

### Generate

- `GenerateRequest` (`invoices.rs:161-170`): `platform`, `condition`, `from`, `to`,
  optional `exportedBy`. Single platform per call — per-platform files are
  structural, a platform multi-select means N generate calls.
- Flow (`invoices.rs:196-253`): platform validated against
  `issue_invoice::script_for` → `select_parcels` re-runs (preview and generate can
  drift if data changed between calls) → fetch `(raw_data, batch_id,
  tracking_number)` rows `WHERE ir.tracking_number = ANY($1) AND ir.platform = $2
  ORDER BY ir.order_number NULLS LAST, ir.id` (`invoices.rs:212-221`) → empty ⇒ 400
  → `rebuild::newest_batch_layout` → `apply_tiktok_shim` → `rebuild_layout_xlsx` →
  `invoice_transform` (Python) → filename (`invoices.rs:174-194`, Bangkok fixed
  +07:00 day range, `{platform}_{condition}_{range}_invoice.xlsx`) → `record_export`
  → 200 with `X-Export-Id` header + `Content-Disposition`.

### What generate writes — `record_export` (`invoices.rs:260-316`)

One transaction; failure fails the whole request ("an invoice we cannot account for
is not issued", ADR 0002):

1. `INSERT INTO invoice_exports (platform, condition, from_ts, to_ts, filename,
   exported_by, parcel_count, row_count)` (`invoices.rs:280-295`). Note
   `condition` is a `NOT NULL varchar` storing `'shipped'|'packed'`
   (migration `migrations/20260705130000_invoice_exports.sql`).
2. One `invoice_export_items (export_id, tracking_number, lines)` row per parcel;
   `lines` snapshots `[{orderNumber, sellerSku, quantity}]` from `raw_data`
   (`invoices.rs:269-306`). Deliberately **no FK to packing_lists** — audit must
   survive parcel deletion and placeholder tracking rewrites.
3. `UPDATE packing_lists SET invoiced_at = COALESCE(invoiced_at, now()) WHERE
   tracking_number = ANY($1)` (`invoices.rs:307-313`) — first export wins; the
   UPDATE fires the existing `packing_updated` pg_notify so the dashboard pill
   flips live.

## 2. Where each new drawer filter binds

Relevant `packing_lists` columns (init: `migrations/20260421050516_init.sql:47-63`,
plus later ALTERs): `tracking_number` (UNIQUE), `order_number` (NOT NULL, indexed),
`platform`, `packing_status`, `order_status` (20260603130000), `shipping_options`
(20260421050535), `packed_at` (20260525000000), `shipped_at`/`shipped_by`/
`shipping_station_id` (20260618200000), `returned_at`/`returned_by`/
`return_station_id` (20260703220000), `invoiced_at` (20260705130000),
`ordered_at`/`paid_at` (20260421050536).

| Filter | Column | Notes |
|---|---|---|
| Platform multi-select | `packing_lists.platform` (set at import, `src/api/imports.rs:348-356`) or `import_rows.platform` | Preview already groups by `ir.platform`; a multi-select can simply gate `WHERE ir.platform = ANY($n)` in the preview aggregate, or filter `pl.platform` in `select_parcels`. Values are capitalized (`'Shopee'|'Lazada'|'Tiktok'`); `script_for` matching is case-insensitive but the columns are not. `import_rows.platform` is indexed (`idx_import_rows_platform`); `packing_lists.platform` is NOT. Legacy pre-import-pipeline rows can have NULL `pl.platform`. |
| Invoiced status | `packing_lists.invoiced_at IS [NOT] NULL` | Cheap tri-state (all / invoiced / not-yet). The alternative — `EXISTS invoice_export_items` (what `already_exported` uses) — diverges after placeholder promotion: items rows keep the OLD placeholder tracking number (no FK, no cascade; cascade migration `20260716120000` only touches the 5 real FKs), while `invoiced_at` travels with the renamed row. **Recommend `invoiced_at` as filter truth.** No index on `invoiced_at`. |
| packing_status lifecycle multi-select (Shipped/Packed/Returned) | timestamp columns, not the status string | Current semantics are pure-timestamp. Natural generalization: parcel qualifies when the milestone timestamp for a selected stage is in-window — `shipped_at`, `packed_at`, `returned_at` — OR'd/UNIONed per selected stage. If the spec instead means the literal `packing_status` column, actual live values are: `To be packed`, `Packing`, `Packed`, `QC Passed`, `Shipping`, `Shipped`, `Returned`, `QC Hold` (see `src/api/packing.rs:171-179,210-217,362-386,418-424,463-477`) — "Shipped/Packed/Returned" is a subset, and a status filter alone loses the date-window anchor for stages other than the current one. Spec must pick one semantic; timestamp-OR keeps continuity with today's `Condition`. |
| Shipping option | `packing_lists.shipping_options` (TEXT) | Populated from the import aggregate (`imports.rs:310,325,348-356`); free-text, per-platform vocabulary; blank/NULL handling precedent: `COALESCE(NULLIF(btrim(shipping_options), ''), 'Unknown')` (`src/api/leaderboard.rs:423`, `src/api/dashboard.rs:739`). No index. Distinct-values endpoint for populating the multi-select doesn't exist for packing_lists (only `GET /reports/shipping-options`, check its source table before reuse). |
| Tracking-number paste | `packing_lists.tracking_number` | UNIQUE constraint; direct `= ANY($n)` intersect with the window (or bypass window entirely — spec decision). |
| Order-number paste | `packing_lists.order_number` | NOT NULL, indexed `idx_packing_lists_order_number` (`migrations/20260421050536`). Order numbers ALSO exist as `import_rows.order_number` (GENERATED from `raw_data->>'order_number'`, indexed `idx_import_rows_order`, `migrations/20260603120000:28`). One order number can map to multiple parcels. Resolution idiom already exists: `WHERE tracking_number = $1 OR (order_number = $1 AND tracking_number != $1)` (`src/api/packing.rs:505`; the `!=` guards the Instant/Express placeholder case where `tracking_number = order_number` until promotion — `imports.rs:298-328`). The 2026-07-16 "orphan order-match" work was **frontend-only** ranking in `app/lib/orphanMatch.ts` (spec `docs/specs/2026-07-16-orphan-order-match.md`); it added no backend columns — the backend piece was the FK ON UPDATE CASCADE migration. A mixed paste box should match `tracking_number = ANY($n) OR order_number = ANY($n)` against `packing_lists` and resolve to tracking numbers. |

## 3. Rebuild + xlsx layer behavior under narrowed row selection

- `src/api/exports/rebuild.rs` — shared seam: `newest_batch_layout`
  (`rebuild.rs:26-43`, newest batch by `created_at` among the selected rows' batch
  ids wins; preview's `layoutMismatch` flags cross-batch drift),
  `rebuild_layout_xlsx` (`rebuild.rs:50-56` → `generate_xlsx(…, false)`),
  `apply_tiktok_shim` (`rebuild.rs:81-85`, prepends one blank row so Tiktok data
  starts at spreadsheet row 3), `invoice_transform` (`rebuild.rs:92-94`).
- `src/export/xlsx_writer.rs::generate_xlsx` (`xlsx_writer.rs:7-90`): pure flat
  writer — header row + one row per input, `_raw` original-cell text wins with
  `__col{i}` dedup keys, canonical-field fallback for legacy rows. **No formulas,
  no totals row, no cross-row references, nothing that assumes "all rows in
  window".** Empty selection produces a header-only file (tested,
  `xlsx_writer.rs:158-166`), though both generate handlers 400 before that.
- Python transforms (`scripts/issue_invoice_{shopee,tiktok,lazada}.py`): all
  row-wise loops. The only formula written is Tiktok's per-row
  `=SKU-before{r}-seller{r}` (`issue_invoice_tiktok.py:88-100`) referencing cells
  in the SAME row — safe under any row subset. Tiktok's positional assumption
  (FIRST_DATA_ROW = 3) is exactly what `apply_tiktok_shim` preserves. Shopee/Lazada
  transforms mutate per-row values and drop cancelled rows/columns; no per-order or
  sheet-level aggregation. **Conclusion: arbitrary filter-narrowed selections are
  safe through the whole rebuild + transform pipeline.** (The "formula-cell
  comparator" gotcha from the E2E sim was a test-oracle issue — openpyxl reads a
  formula string, not its value — not a runtime hazard.)

## 4. Orders export — `src/api/exports/orders.rs`

Routes at `src/api/mod.rs:267-268`. Windows on `import_rows.ordered_at` — a
GENERATED TEXT column — via the regex `ORDERED_AT_GUARD` + guarded
`::timestamptz` cast (`orders.rs:65-66,105-108`); no `packing_lists` join at all,
cancelled orders included, no audit trail.

- `PreviewQuery` (`orders.rs:68-72`): `from`, `to` only. Response
  (`orders.rs:74-91`): per-platform `{platform, orders, rows, batches,
  layoutMismatch}` + top-level `excluded` (standing count of guard-failing rows
  table-wide, `orders.rs:117-122`).
- `GenerateRequest` (`orders.rs:130-136`): `platform`, `from`, `to` — one platform
  per file, same rebuild seam, no Python transform.
- **Platform multi-select impact: minimal.** Preview already returns all platforms
  grouped; a multi-select needs either client-side filtering of the existing
  response or one added `AND ir.platform = ANY($3)` in the preview SQL
  (`orders.rs:97-115`). Generate is inherently per-platform (per-platform files
  stay) — the drawer just fires one POST per selected platform, exactly what the
  popover does today per-card. `excluded` is platform-agnostic by design
  (`orders.rs:26-30`); if the drawer filters platforms it should keep showing it
  unscoped or the spec must re-decide its meaning.

## 5. Indexes the new predicates touch (`migrations/`)

Existing and relevant:

- `idx_packing_lists_shipped_at` — partial `WHERE shipped_at IS NOT NULL`
  (re-stacked in `20260710230000_broaden_shipped_at_index.sql` precisely so
  shipped_at range scans can use it).
- `idx_packing_lists_packed_at` — partial `WHERE packed_at IS NOT NULL`
  (`20260525000000_add_packed_at.sql`). Range-scan friendly.
- `idx_packing_lists_returned_at` — partial `WHERE packing_status = 'Returned'`
  (`20260703220000_returns_shipping_mode.sql:10-11`). **A bare `returned_at`
  range does NOT imply that predicate, so a Returned-stage window filter falls
  back to seq scan — the same trap `20260710230000` fixed for shipped_at.** A
  Returned lifecycle filter wants a new migration re-creating it as
  `WHERE returned_at IS NOT NULL`.
- `idx_packing_lists_status` / `idx_packing_lists_status_created` — if the spec
  binds on the literal `packing_status` column instead of timestamps.
- `idx_packing_lists_order_number` (`20260421050536`) — order-number paste.
- `packing_lists_tracking_number_unique` — tracking paste.
- `import_rows`: `idx_import_rows_tracking`, `idx_import_rows_order`,
  `idx_import_rows_platform`, `idx_import_rows_batch`, `idx_import_rows_sku`
  (`20260603120000_import_tables.sql:57-61`) — the preview join and generate
  fetch stay covered.
- `idx_invoice_export_items_tracking` (`20260705130000`) — keeps the
  `already_exported` EXISTS cheap.

Missing (add only if the predicate is actually selective enough to matter at this
table's size):

- `packing_lists.invoiced_at` — no index. For a "not invoiced" filter a partial
  index `ON packing_lists (invoiced_at) WHERE invoiced_at IS NULL` (or a composite
  with the stage timestamp) is the natural shape; at current warehouse volumes the
  window index likely already narrows enough that this is optional.
- `packing_lists.shipping_options` — no index; low-cardinality text, probably fine
  unindexed as a residual filter after the timestamp window.
- `packing_lists.platform` — no index; same reasoning.
- `packing_lists.order_status` — no index; the existing cancelled-exclusion already
  runs as a residual filter after the timestamp index, unchanged.

## 6. Frontend hooks (origin/dev-1.1 @ 687e02f)

Consumers: `app/components/ExportPopover.tsx` (to be replaced by the drawer) and
`app/components/WarehouseInvoicePanel.tsx`.

- `app/hooks/useWarehouseInvoice.ts` — the idiom the drawer extends:
  - Filter state as individual `useState`s: `condition`, `mode`
    (`today|yesterday|custom`), `from`, `to` (lines 85-96).
  - `resolveWindow` (lines 45-80): mode → UTC ISO instants using the **browser's
    local day boundaries** (Bangkok assumption, mirrors `lib/leaderboardWindow`);
    backend compares raw timestamptz.
  - Preview refresh: one `useEffect` keyed on every filter (`[condition, mode,
    from, to, refreshTick]`, line 123) → `URLSearchParams` GET with
    `AbortController` cancellation; errors clear the preview (lines 98-123).
  - `generate(platform)` (lines 125-153): JSON POST, per-platform
    `gen: Record<string, GenState>` state machine
    (`idle|generating|done|error`), client-rebuilt filename **duplicating the
    server's `build_filename` logic** (line 139-140 — cross-origin
    Content-Disposition is not exposed; new filter-dependent filename parts must
    be mirrored here or `Access-Control-Expose-Headers` added), then
    `refreshTick++` so the preview re-fetches fresh `alreadyExported`, plus
    optional `onGenerated` callback for sibling data (export history).
- `app/hooks/useOrdersExport.ts` — same skeleton minus condition/audit: preview
  effect on `[mode, from, to, retryTick]` (line 72), `retry()` bumps a tick after
  errors (lines 76-78), generate posts `{platform, from, to}` only.

Drawer extension pattern: add each new filter as hook state, include it in the
effect dependency array and `URLSearchParams`/POST body, and debounce the
multi-paste textarea before letting it hit the dependency array (today every
keystroke-level state change refires the preview; AbortController makes it correct
but a 500-number paste would spam the backend).

## 7. Spec implications — param shapes and gotchas

Recommended param shapes (backwards compatible where possible):

- **Preview** (`GET /exports/invoices/preview`) grows:
  - `stages=shipped,packed,returned` (CSV) replacing `condition` — or keep
    `condition` accepted as a one-element alias during transition.
  - `platforms=Shopee,Tiktok` (CSV; omitted = all).
  - `invoiced=all|invoiced|not_invoiced` (default `all`), bound as
    `invoiced_at IS [NOT] NULL`.
  - `shipping=<CSV of shipping_options values>` (omitted = all; decide whether
    `Unknown` maps to `NULL/blank` via the `COALESCE(NULLIF(btrim…))` idiom).
  - `numbers=<CSV>` mixed tracking/order paste — **but** a 500-entry paste will
    blow query-string limits; prefer making preview a POST (or `POST
    /exports/invoices/preview` twin) once the paste filter exists. Generate is
    already POST/JSON so arrays are free there.
- **Generate** (`POST /exports/invoices/generate`): same fields in the JSON body,
  still one `platform` per call (per-platform files stay).
- **Orders**: `platforms` CSV on preview only, or pure client-side filtering of
  the existing grouped response; generate unchanged.

Gotchas the spec must address:

1. **axum `Query` cannot deserialize repeated params into `Vec<T>`**
   (serde_urlencoded limitation). Use CSV strings with a custom deserializer,
   `axum_extra::extract::Query`, or POST bodies. Pick one convention repo-wide.
2. **`select_parcels` interpolation discipline**: only the two `ts_column()`
   constants are ever `format!`-ed into SQL. A stage multi-select turns the single
   range into an OR/UNION of per-stage ranges — keep each column name a
   compile-time constant and bind everything else; do not let filter values near
   `format!`.
3. **Audit row shape**: `invoice_exports.condition` is `NOT NULL varchar`
   `'shipped'|'packed'`, and `from_ts/to_ts` assume one window. A filtered export
   needs the full filter set persisted for reproducibility — recommend a new
   nullable `filters jsonb` column (new migration; never edit applied ones) and a
   defined `condition` value for multi-stage exports (e.g. CSV `'shipped,returned'`
   or a sentinel), plus the same decision for `build_filename`
   (`invoices.rs:174-194`) and the client-side filename duplicate
   (`useWarehouseInvoice.ts:139-140`).
4. **Returned-stage index trap**: `idx_packing_lists_returned_at`'s partial
   predicate is `packing_status = 'Returned'`; a `returned_at` range filter can't
   use it. Ship a re-stacked index migration (`WHERE returned_at IS NOT NULL`)
   alongside the feature, mirroring `20260710230000`.
5. **`already_exported` vs `invoiced_at` divergence**: `invoice_export_items` has
   no FK and is not renamed by the `20260716120000` cascade, so after an
   Instant/Express placeholder promotion the EXISTS-based `already_exported`
   count misses the parcel while `invoiced_at` still reflects it. Bind the
   invoiced filter on `invoiced_at`; treat `already_exported` as advisory UI copy.
6. **Formula/totals: non-issue.** `generate_xlsx` emits no formulas or totals and
   the Python transforms are row-wise (only Tiktok writes a same-row formula), so
   narrowed selections need no changes in `src/export/` or `scripts/`.
7. **Preview/generate drift**: generate re-runs `select_parcels`; with more
   filters the preview-vs-generate mismatch window grows. Acceptable today
   (documented behavior), but the spec should state it stays acceptable.
8. **`missing` list growth**: `PreviewResponse.missing` is unbounded; rich filters
   over long windows can return thousands of tracking numbers. Consider capping
   with a count (`missingCount` + first N) in the new response shape.
9. **Placeholder rows in the paste filter**: `tracking_number = order_number` for
   un-promoted Instant/Express parcels — a mixed paste matcher should use the
   existing `packing.rs:505` idiom so one pasted value can't double-match.
10. **Platform value casing**: columns store `'Shopee'/'Lazada'/'Tiktok'`;
    `script_for` is case-insensitive but SQL `= ANY(...)` is not — normalize at
    the API boundary.
11. **`packing_lists.platform` NULLs**: legacy rows predating the import pipeline
    have NULL platform; a platform filter bound on `packing_lists` silently drops
    them, while binding on `import_rows.platform` (as the preview aggregate
    already does) does not. Prefer filtering at the `import_rows` join.
