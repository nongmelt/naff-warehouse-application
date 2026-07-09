# Orders export by date window — order-date feasibility research

**Ticket:** Wayfinder #53 (Orders export date window)
**Date:** 2026-07-10
**Code read at:** backend worktree `backend/.worktrees/warehouse-invoice`, branch `feat/warehouse-invoice`, commit `0cd9706` ("feat(invoice): preview returns distinct order count per platform")
**Data verified against:** Postgres `warehouse_db_test` (Docker container `warehouse-postgres`, postgres:18.3), read-only SELECTs on 2026-07-10.

All file paths below are relative to the worktree root
`/Users/nongmelt/Workspace/naff-warehouse-application/backend/.worktrees/warehouse-invoice/`.

---

## How order dates flow through the system (shared pipeline)

1. The xlsx is parsed cell-as-text by an embedded Python/openpyxl script (`src/import/parser.rs:10,19-27`), producing an ordered `header_layout` plus one JSON object per row.
2. Platform headers are mapped to canonical field names via `Platform::column_mapper()` (`src/import/platform.rs:32-75`). Unmapped columns are preserved verbatim under `_raw` (`src/import/parser.rs:118-137`); duplicated header names get a `__col{i}` suffix (`src/import/parser.rs:80-94`).
3. `apply_normalizations` normalizes `ordered_at`/`paid_at` for **all** platforms via `normalize_datetime` (`src/import/parser.rs:232-240`), which emits `YYYY-MM-DD HH:MM:SS+07:00` (`src/import/platform.rs:104-131`). The original cell text stays untouched inside `_raw`.
4. Rows are upserted into `import_rows`; `ordered_at` / `paid_at` are **generated TEXT columns** off `raw_data->>'ordered_at'` with `NULLIF(..., '')` (`migrations/20260603120000_import_tables.sql:42-47`). They are TEXT, not timestamptz, because a text→timestamptz cast is not IMMUTABLE and thus illegal in a GENERATED expression (comment at `migrations/20260603120000_import_tables.sql:42-45`). Read-side code casts explicitly, e.g. `$5::text::timestamptz` at `src/api/imports.rs:311`.
5. Re-export rebuilds the original layout from `import_batches.header_layout` + `raw_data->'_raw'` — `_raw` wins for every column so normalized ISO values never leak into rebuilt files (`src/export/xlsx_writer.rs:57-70`, test `mapped_columns_read_original_from_raw_not_canonical` at `src/export/xlsx_writer.rs:198-224`).

The known "parser alphabetizes headers" bug is fixed on this branch: the Python parser emits line 1 as an **ordered header array** ("duplicates + empty names preserved", `src/import/parser.rs:71-76`) and export iterates `header_layout` in order (`src/export/xlsx_writer.rs:26-31,46`). Batches store the ordered layout in `import_batches.header_layout` jsonb (`migrations/20260603120000_import_tables.sql:8`).

---

## Shopee

### 1. Order-date column
- Platform file header: **`วันที่ทำการสั่งซื้อ`** ("order placed date") → canonical `ordered_at` (`src/import/platform.rs:43`). Secondary: `เวลาการชำระสินค้า` → `paid_at` (`src/import/platform.rs:44`).
- Normalized into the **queryable generated column `import_rows.ordered_at`** (`migrations/20260603120000_import_tables.sql:46`), not only `_raw`. The original text also survives in `raw_data->'_raw'->>'วันที่ทำการสั่งซื้อ'`.

### 2. Format / timezone
- Source cell format: `YYYY-MM-DD HH:MM` (minute precision, no timezone). Parser appends `:00+07:00` **without validating** the input shape (`src/import/platform.rs:114-117`).
- Stored format: `YYYY-MM-DD HH:MM:SS+07:00` — explicit Bangkok offset; code asserts "All platform exports use Bangkok local time" (`src/import/platform.rs:104-106`). Thailand has no DST, so fixed +07:00 is exact (precedent comment `src/api/warehouse_invoice.rs:174-175`).
- Real rows (query below): raw `2026-06-01 00:01` → stored `2026-06-01 00:01:00+07:00`. All 1,492 real-file rows cast cleanly to timestamptz.

### 3. Row identity across batches
- Composite natural key: `Shopee:{order_number}|{tracking_number}|{product_name}|{product_variation}|{seller_sku}|{ordered_at}|{paid_at}` (`src/import/dedup.rs:13-25`). **Quantity is not part of the key.**
- Upsert: `ON CONFLICT (natural_key) DO UPDATE SET raw_data = EXCLUDED.raw_data, batch_id = EXCLUDED.batch_id, updated_at = now()` (`src/api/imports.rs:199-205`), backed by `CONSTRAINT import_rows_natural_key_unique UNIQUE (natural_key)` (`migrations/20260603120000_import_tables.sql:53`).
- Verified collapse: `S2.6ori.xlsx` was imported 3 times (batches 171, 218, 281, each `row_count=1492`); live rows = 1,492, all attached to the newest batch, and all have `updated_at > created_at`. Last-import-wins works, and re-keying `batch_id` to the newest batch means "which layout to use" is answerable per row.

### 4. Date-window dropouts
- Real file (`S2.6ori.xlsx`, 1,492 rows): **0 NULL/empty `ordered_at`**; 47 rows have `paid_at` NULL (raw cell `-`, i.e. unpaid/COD — `normalize_datetime` maps `-` and empty to `''` at `src/import/platform.rs:109-112`, and `NULLIF` turns that into SQL NULL). This is why `ordered_at`, not `paid_at`, must drive the window.
- Whole-DB NULL `ordered_at` for Shopee = 412 rows, but **all 412 belong to synthetic `whi-test.xlsx` test batches** (uploader `whi-test`); 256 of them don't even carry the Shopee date header (fabricated header sets like `SKU, Order ID, Tracking ID`). No real-import dropouts.

---

## Lazada

### 1. Order-date column
- Platform file header: **`createTime`** → canonical `ordered_at` (`src/import/platform.rs:59`). Lazada maps **no `paid_at`** at all (absent from the mapper, `src/import/platform.rs:47-60`), so `paid_at` is always NULL for Lazada.
- Queryable via generated column `import_rows.ordered_at`; original text in `raw_data->'_raw'->>'createTime'`.

### 2. Format / timezone
- Source cell format: `DD Mon YYYY HH:MM` (e.g. `01 Jun 2026 23:27`), parsed with chrono `%d %b %Y %H:%M` (`src/import/platform.rs:118-123`). English month abbreviations; no timezone in source; +07:00 appended.
- **Unparseable values pass through raw** (`src/import/platform.rs:121` `Err(_) => raw.to_string()`) — they would land in the generated column as non-ISO text and break a bare `::timestamptz` cast (see verdict).
- Real rows: raw `01 Jun 2026 23:27` → stored `2026-06-01 23:27:00+07:00`. All 267 real-file rows cast cleanly.

### 3. Row identity across batches
- Natural key: **`Lazada:{order_item_id}`** (`src/import/dedup.rs:9-12`) — Lazada files carry one row per unit with a platform-unique `orderItemId` (`src/import/platform.rs:49`), and the parser injects `quantity = 1` per row (`src/import/parser.rs:196-197`, mapper note `src/import/platform.rs:57-58`).
- Verified: 267 real rows = 267 distinct `order_item_id`; `L2.6ori.xlsx` imported 3× (batches 170, 217, 280) collapsed to 267 live rows on the newest batch. This is the strongest key of the three platforms.

### 4. Date-window dropouts
- Real file (`L2.6ori.xlsx`, 267 rows): **0 NULL/empty `ordered_at`**.
- Whole-DB Lazada NULLs = 56 rows, all `whi-test.xlsx` synthetic batches (these carry `createTime` in `_raw` but were written by a test path that never promoted the canonical field).

---

## Tiktok

### 1. Order-date column
- Platform file header: **`Created Time`** → canonical `ordered_at` (`src/import/platform.rs:70`). Secondary: `Paid Time` → `paid_at` (`src/import/platform.rs:71`).
- Queryable via generated column `import_rows.ordered_at`; original text in `raw_data->'_raw'->>'Created Time'`.

### 2. Format / timezone
- Source cell format: `DD/MM/YYYY HH:MM:SS` (e.g. `01/06/2026 23:59:22`), parsed with chrono `%d/%m/%Y %H:%M:%S` (`src/import/platform.rs:124-129`). Day-first; seconds precision; no timezone in source; +07:00 appended. Unparseable values pass through raw (`src/import/platform.rs:127`).
- Real rows: raw `01/06/2026 23:59:22` → stored `2026-06-01 23:59:22+07:00`. All 528 real-file rows cast cleanly.

### 3. Row identity across batches
- Same composite key shape as Shopee: `Tiktok:{order_number}|{tracking_number}|{product_name}|{product_variation}|{seller_sku}|{ordered_at}|{paid_at}` (`src/import/dedup.rs:13-25`). Quantity excluded.
- Verified: `T2.6ori.xlsx` imported 3× (batches 172, 219, 282, `row_count=528` each) → 528 live rows on the newest batch, all `updated_at > created_at`.
- Tiktok re-export quirk (relevant to the export side of this feature): the parser strips Tiktok's row-2 description row at ingest (`--skip-row-2`, `src/import/parser.rs:49-51`), and the existing invoice export re-inserts a blank placeholder row so downstream transforms that hard-code `FIRST_DATA_ROW = 3` still work (`src/api/warehouse_invoice.rs:250-269`). A plain "rebuild original file" export must decide whether to reproduce that description row (it is **not** stored anywhere).

### 4. Date-window dropouts
- Real file (`T2.6ori.xlsx`, 528 rows): **0 NULL/empty `ordered_at`**; `paid_at` populated on only 212/528 rows (real Tiktok exports leave Paid Time blank for unpaid orders) — again, do not window on `paid_at`.
- Whole-DB Tiktok NULLs = 56 rows, all `whi-test.xlsx` synthetic batches.

---

## Verification queries (warehouse_db_test, 2026-07-10)

Format census — every non-NULL `ordered_at` in the DB is strict ISO+07:00; no mixed formats survived the pre-fix era (the dev DB rows were all refreshed post-fix; `MIN(created_at)` 2026-07-04, all 2,287 real rows re-upserted since):

```sql
SELECT platform,
  CASE WHEN ordered_at IS NULL THEN 'NULL/empty'
       WHEN ordered_at ~ '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\+07:00$' THEN 'ISO+07:00'
       ELSE 'OTHER: ' || left(ordered_at, 30) END AS fmt, COUNT(*)
FROM import_rows GROUP BY 1,2 ORDER BY 1;
-- Amazon NULL/empty 33 | Lazada ISO+07:00 267, NULL 56
-- Shopee ISO+07:00 1492, NULL 412 | Tiktok ISO+07:00 528, NULL 56
```

NULL rows are all synthetic test batches (`whi-test.xlsx` / uploader `whi-test`); real files have zero:

```sql
SELECT b.platform, b.original_filename, b.uploaded_by, COUNT(r.id) AS rows,
       COUNT(r.id) FILTER (WHERE r.ordered_at IS NULL) AS null_ordered
FROM import_batches b LEFT JOIN import_rows r ON r.batch_id = b.id
GROUP BY 1,2,3;
-- S2.6ori.xlsx/fidelity-fix-verify-2: 1492 rows, 0 null
-- L2.6ori.xlsx/fidelity-fix-verify-2:  267 rows, 0 null
-- T2.6ori.xlsx/fidelity-fix-verify-2:  528 rows, 0 null
-- whi-test.xlsx/whi-test: Shopee 412/412, Lazada 56/56, Tiktok 56/56, Amazon 33/33 null
```

Timestamptz castability + range (query would error if any value were uncastable):

```sql
SELECT platform, MIN(ordered_at::timestamptz), MAX(ordered_at::timestamptz), COUNT(*)
FROM import_rows WHERE ordered_at IS NOT NULL AND platform <> 'Amazon' GROUP BY 1;
-- Shopee 1492, Lazada 267, Tiktok 528 rows; all 2026-05-31T17:01Z..2026-06-01T17:00Z (= Bangkok Jun 1)
```

Bangkok-day window proof — selects exactly the full real files (all three files cover 2026-06-01 Bangkok):

```sql
SELECT platform, COUNT(*) FROM import_rows
WHERE ordered_at IS NOT NULL AND platform <> 'Amazon'
  AND ordered_at::timestamptz >= '2026-06-01 00:00:00+07:00'::timestamptz
  AND ordered_at::timestamptz <  '2026-06-02 00:00:00+07:00'::timestamptz
GROUP BY platform;
-- Lazada 267 | Shopee 1492 | Tiktok 528  (100% of real rows per platform)
```

Upsert collapse evidence:

```sql
SELECT COUNT(*) FROM import_rows
WHERE platform <> 'Amazon' AND updated_at > created_at + interval '1 second';  -- 2287 (all)
```

Sample promoted vs raw values:

| platform | stored `ordered_at` | raw `_raw` source cell |
|---|---|---|
| Shopee | `2026-06-01 00:01:00+07:00` | `2026-06-01 00:01` |
| Lazada | `2026-06-01 23:27:00+07:00` | `01 Jun 2026 23:27` |
| Tiktok | `2026-06-01 23:59:22+07:00` | `01/06/2026 23:59:22` |

---

## Feasibility verdict

**Yes — a from/to date window can reliably drive per-platform export.** All three platforms map their order-date header to the same generated, indexed-adjacent TEXT column `import_rows.ordered_at`, uniformly normalized to `YYYY-MM-DD HH:MM:SS+07:00`, and 100% of real imported rows (2,287) cast cleanly to timestamptz and fall inside a Bangkok-local window query. The rebuild path (`import_batches.header_layout` + `_raw`-wins export writer) already exists and preserves original cell text, including original platform-native date strings.

### Edge cases the spec must handle

1. **Guard the cast.** `ordered_at` is TEXT; unparseable platform dates pass through raw (`src/import/platform.rs:121,127`) and the Shopee branch appends `:00+07:00` blindly (`:114-117`). One bad value makes a bare `WHERE ordered_at::timestamptz >= $1` throw for the whole query. Filter first with the strict shape regex `ordered_at ~ '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\+07:00$'` before casting, and surface non-matching/NULL rows as an "excluded rows" count in the preview (same UX pattern as `missing` in `src/api/warehouse_invoice.rs:151-158`).
2. **NULL `ordered_at` rows silently fall out of any window.** Zero on current real data, but blank date cells are legal (`normalize_datetime` maps `''`/`-` → NULL). Report the per-platform NULL count for the window's batches instead of silently dropping.
3. **Window semantics must be Bangkok-local.** All stored offsets are +07:00 (no DST); interpret the user's from/to dates at +07:00, half-open (`>= from 00:00+07`, `< to+1d 00:00+07`). Since all three platforms normalize to the same offset there is **no cross-platform drift** — but only because `normalize_datetime` asserts all platform exports are Bangkok-local (`src/import/platform.rs:104-106`); if a seller-center account tz ever changes, that assumption breaks upstream of this feature.
4. **Layout selection across batches.** A date window can span batches with different `header_layout`s. Precedent: newest batch's layout wins + preview flags `layout_mismatch` (`src/api/warehouse_invoice.rs:233-241,136`). Reuse both. Note the upsert re-points rows to the newest batch (`batch_id = EXCLUDED.batch_id`, `src/api/imports.rs:203`), which keeps rows aligned with the layout they were last seen under.
5. **Identical-line collapse (Shopee/Tiktok only).** The composite natural key excludes `quantity` (`src/import/dedup.rs:13-25`); two byte-identical order lines in one file collapse to one `import_rows` row, so a re-export could emit fewer rows than the original file. Not observed in the real files (batch `row_count` == live rows: 1492/267/528), but it is structurally possible. Lazada is immune (`order_item_id` key).
6. **Tiktok description row.** The parser drops Tiktok's row 2 at ingest and it is not persisted; a faithful re-export must prepend a placeholder (or accept its loss) — see `src/api/warehouse_invoice.rs:250-269`.
7. **Junk platforms in the table.** `import_rows.platform` contains an `Amazon` value (33 test rows) that `Platform::from_str` rejects (`src/import/platform.rs:13-20`). Key the feature on the three-platform enum; don't enumerate platforms from the table.
8. **`paid_at` is not a viable window column.** Sparse on real data (Shopee 47 `-` rows → NULL; Tiktok only 212/528 populated; Lazada never mapped). Use `ordered_at` only.
