# Spec: Export Drawer — TikTok-Seller-style right panel with rich invoice filters

Date: 2026-07-23. Wayfinder map [#75](https://github.com/nongmelt/naff-warehouse-application/issues/75);
decisions locked in tickets #76 (semantics grilling), #77 (research —
`docs/research/2026-07-23-export-backend-surface.md`), #80 (lifecycle/audit grilling), and the
#78 HITL prototype (7 rounds; final mockup `docs/mockups/2026-07-23-export-drawer-v1-v2-v3.html`,
V1 selected, commit `4c1e01e`). Touches BOTH submodules: backend worktree
`backend/.worktrees/export-drawer` branch `feat/export-drawer` off `origin/dev-1.3` (= `05b7505`),
frontend worktree `frontend/.worktrees/export-drawer` branch `feat/export-drawer` off
`origin/dev-1.1` (= `687e02f`).

## 1. Problem

The dashboard's `ExportPopover` is a 380px anchored popover with only a condition toggle
(Shipped/Packed) and a date range. The warehouse manager needs: Returned-stage exports, an
invoiced/not-invoiced cut, shipping-option and platform cuts, and a way to export a hand-picked
set of parcels by tracking/order number. A popover cannot hold that filter stack; the export UI
becomes a right side drawer (Seller-center pattern: scrim + drawer + sticky footer).

## 2. Decisions (locked)

| # | Decision | Source |
|---|----------|--------|
| D1 | Drawer replaces the popover for BOTH export types (Invoices/Orders). Rich filters are Invoices-only; Orders keeps platform + date range. Per-platform files stay. `/invoices` page untouched. | kickoff |
| D2 | Cancelled parcels are ALWAYS excluded (both modes). Footer note verbatim: `Cancelled orders are always excluded.` | #76 |
| D3 | Invoiced filter is 3-state — `All / Not yet invoiced / Already invoiced` — bound on `packing_lists.invoiced_at IS [NOT] NULL`, never on the `invoice_export_items` EXISTS (placeholder-promotion divergence, research §7.5). | #76 + #77 |
| D4 | Lifecycle stage is a SINGLE-select radio `Shipped \| Packed \| Returned`. Pure-timestamp semantics: the stage picks the timestamp column (`shipped_at` / `packed_at` / `returned_at`) the date range binds on. `Condition` enum gains `returned`. | #80 |
| D5 | Audit: new nullable `invoice_exports.filters jsonb` records the full filter state; `condition` varchar stays (no CHECK constraint exists). Filename gains a `_filtered` marker when extra filters are active. | #80 |
| D6 | Client-side `build_filename` duplicate dies: server exposes `Content-Disposition` + `X-Export-Id` via `Access-Control-Expose-Headers`; FE reads the header. | #80 |
| D7 | `returned_at` index re-stack migration (`WHERE returned_at IS NOT NULL`) ships with the feature (current partial predicate `packing_status = 'Returned'` can't serve a bare range scan — same trap as `20260710230000`). | #80 |
| D8 | **Number search replaces bulk paste** (revises #76's auto-detect textarea): one search box labeled `Tracking number or Order number`, first row of the drawer. Typeahead suggestions after each keystroke; click (or Enter = first row) confirms a number into a removable chip. The "N of M not matched" list is obsolete — only existing numbers can be confirmed. | #78 HITL |
| D9 | Match rule: **contiguous, case-insensitive substring only** — the typed characters must appear as one unbroken run in the tracking OR order number. In-order-with-gaps matching explicitly rejected. Matched characters highlight in real time in both fields. | #78 HITL r6–r7 |
| D10 | Suggestion row = one parcel: tracking number, order number beneath it, platform glyph, and the parcel's live `packing_status` as a colored pill. | #78 HITL r4 |
| D11 | **Confirmed numbers override ALL other filters** (revises #76's "AND with other filters"): with ≥1 chip, every other filter group disables/dims and the export is exactly the selected parcels. Note verbatim: `Filters below ignored — exporting the N selected numbers instead.` Cancelled exclusion still applies (D2). | #78 HITL r5 |
| D12 | Chips are keyed by tracking number (parcel identity) regardless of which field matched; the FE sends tracking numbers to the backend. | #78 HITL |
| D13 | Drawer field order (Invoices): number search → date range → Stage radio → Platforms → Invoiced status → Shipping option → Preview. Sticky footer: cancelled note, filename hint, `Download all (N files)`. | #78 HITL r3/r5 |
| D14 | Platform multi-select is CLIENT-side: it gates which per-platform preview rows render and which files `Download all` fetches. No backend platform param (preview already returns all platforms grouped; generate is per-platform by construction). | this spec |
| D15 | Stage/date/invoiced/shipping/numbers bind SERVER-side in `select_parcels`. | #77 |
| D16 | Orders export backend is untouched; the Orders side of the drawer is platform chips (client-side row gating) + date range, `Rows` column, no invoice-only notes. | kickoff + #77 §4 |

## 3. Current state (verified 2026-07-23 against pinned bases)

Backend `origin/dev-1.3` = `05b7505`:

- Full export surface mapped in `docs/research/2026-07-23-export-backend-surface.md` — the
  authoritative file:line reference for `invoices.rs`, `orders.rs`, `rebuild.rs`,
  `xlsx_writer.rs`, transforms, and indexes. Key facts used below:
  - `Condition` enum `shipped|packed` (`invoices.rs:32-56`); `ts_column()` values are the only
    strings `format!`-ed into SQL.
  - `select_parcels` (`invoices.rs:62-90`): timestamp window + live cancelled exclusion →
    tracking set + `cancelled_excluded` count.
  - `PreviewQuery` = `condition, from, to`; `GenerateRequest` = `platform, condition, from, to,
    exportedBy?`. Generate re-runs `select_parcels`, 400 on empty, writes audit in one
    transaction (`record_export`, `invoices.rs:260-316`), stamps
    `invoiced_at = COALESCE(invoiced_at, now())`, returns file + `X-Export-Id` +
    `Content-Disposition`.
  - `invoice_exports` schema (`20260705130000`): `condition varchar NOT NULL` (no CHECK),
    `from_ts/to_ts timestamptz NOT NULL`, `filename NOT NULL`. Number-mode exports need
    `from_ts/to_ts` nullable → migration (§4.1).
  - xlsx rebuild + Python transforms are row-wise; arbitrary narrowed selections are safe
    (research §3). Empty selection 400s before reaching the writer.
  - CORS (`mod.rs:50-58`): `CorsLayer` with `allow_headers(Any)`, **no `expose_headers`** —
    browser JS cannot read `Content-Disposition`/`X-Export-Id` today (hence the FE filename
    duplicate).
  - `GET /packing-lists/suggest` (`packing.rs:126-148`, mounted `mod.rs:79`): ILIKE `%term%`
    over `tracking_number` + `order_number` (contiguous substring — exactly D9), min 2 chars,
    `LIMIT 8`, `ORDER BY updated_at DESC NULLS LAST, created_at DESC`, returns
    `tracking_number, order_number, platform, packing_status, updated_at`. **The typeahead
    endpoint already exists; zero new search surface.**
  - `GET /reports/shipping-options` (`reports.rs:360-378`) reads legacy `order_lists_raw*`
    tables — NOT reusable for a packing_lists-backed dropdown. Blank/NULL idiom elsewhere:
    `COALESCE(NULLIF(btrim(shipping_options), ''), 'Unknown')` (`leaderboard.rs:423`).
  - axum `Query` cannot deserialize repeated params into `Vec` — CSV params on GET (research
    §7.1).

Frontend `origin/dev-1.1` = `687e02f`:

- `ExportPopover.tsx` — button + anchored panel; both hooks mounted while open (per-type state
  memory); dense per-platform table; 7×7 `DownloadButton` with generating/done/error states.
  Tests: `ExportPopover.shell.test.tsx`, `ExportPopover.orders.test.tsx`.
- `useWarehouseInvoice.ts` — filter state (`condition`, `mode`, `from`, `to`), preview effect
  with `AbortController`, `generate(platform)` per-platform `GenState` machine, client
  `build_filename` duplicate (lines 139-140) — dies per D6.
- `useOrdersExport.ts` — same skeleton minus condition/audit; `retry()` tick.
- Test conventions: vitest 4 + jsdom, `createRoot` + `act`, NO @testing-library. Tailwind v4,
  every new style needs a `dark:` variant.
- `DashboardSearch` from the order-lookup spec is NOT on `dev-1.1` (that FE branch went to
  `feat/warehouse-invoice`); the drawer builds its own typeahead UI against `suggest`.

## 4. Design

### 4.1 Backend — `invoices.rs` + migrations

**Migration A — `*_invoice_export_filters.sql`** (new file, never edit applied migrations):

```sql
ALTER TABLE public.invoice_exports
    ALTER COLUMN from_ts DROP NOT NULL,
    ALTER COLUMN to_ts DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS filters jsonb NULL;
```

**Migration B — `*_broaden_returned_at_index.sql`** (mirror of `20260710230000`):

```sql
DROP INDEX IF EXISTS idx_packing_lists_returned_at;
CREATE INDEX idx_packing_lists_returned_at
    ON public.packing_lists (returned_at)
    WHERE returned_at IS NOT NULL;
```

**`Condition` enum** gains `Returned` (`serde rename "returned"`), `ts_column()` → `"returned_at"`.
The three column names stay the only `format!`-interpolated strings.

**`PreviewQuery` grows (all optional, backwards compatible):**

- `invoiced=all|not_invoiced|invoiced` (default `all`) → predicate `invoiced_at IS NULL` /
  `IS NOT NULL` / none.
- `shipping=<exact shipping_options value>` (optional; absent = all). Bound as
  `COALESCE(NULLIF(btrim(shipping_options), ''), 'Unknown') = $n` so the dropdown's `Unknown`
  entry works.
- `numbers=<CSV of tracking numbers>` (optional). Non-empty ⇒ **number mode**: `condition`,
  `from`, `to`, `invoiced`, `shipping` are all ignored (D11).

**`select_parcels` two modes** (signature grows to take a params struct; executor's shape):

- *Filter mode* (numbers absent): today's timestamp window + cancelled exclusion, plus the
  invoiced and shipping predicates appended as bound residual filters.
- *Number mode* (numbers present):

```sql
SELECT tracking_number FROM packing_lists
WHERE tracking_number = ANY($1)
  AND (order_status IS NULL OR order_status <> 'Cancelled')
```

  and `cancelled_excluded` = `COUNT(*)` of `tracking_number = ANY($1) AND order_status =
  'Cancelled'`. (FE sends tracking numbers only — D12 — so no order-number OR-arm and no
  placeholder double-match risk.)

**`GenerateRequest` grows** the same fields (`invoiced`, `shipping`, `numbers: Vec<String>` —
JSON body, arrays free). Platform stays required + per-file.

**`record_export` / audit:**

- Filter mode: `condition` = stage as today; `from_ts/to_ts` as today; `filters` jsonb written
  ONLY when a non-default extra filter is active:
  `{"invoiced": "not_invoiced", "shipping": "Standard delivery"}` (omit default keys). NULL
  `filters` ⇒ row is semantically identical to a legacy export.
- Number mode: `condition = 'selected'` (varchar, no CHECK — free), `from_ts/to_ts = NULL`,
  `filters = {"numbers": ["<tracking>", …]}`.

**`build_filename`:**

- Filter mode, no extra filters: unchanged `{platform}_{condition}_{range}_invoice.xlsx`.
- Filter mode with extra filters: `{platform}_{condition}_{range}_filtered_invoice.xlsx`.
- Number mode: `{platform}_selected_{yyyymmdd}_invoice.xlsx` (Bangkok date of export, same
  +07:00 convention as the existing range).

**CORS** (`mod.rs:50-58`): add

```rust
.expose_headers([header::CONTENT_DISPOSITION, HeaderName::from_static("x-export-id")])
```

**New endpoint** `GET /exports/invoices/shipping-options` (the legacy `/reports/shipping-options`
reads the wrong tables):

```sql
SELECT DISTINCT COALESCE(NULLIF(btrim(shipping_options), ''), 'Unknown') AS v
FROM packing_lists ORDER BY v
```

returns `Vec<String>` for the dropdown (prepend `All shipping options` client-side).

**Preview/generate drift** stays acceptable and documented (research §7.7). `missing` list
response field unchanged (out of scope).

### 4.2 Frontend — drawer replaces popover

**New `app/components/ExportDrawer.tsx`** (+ the Export button stays in the Dashboard header
where `ExportPopover` sits today; the button now opens the drawer). `ExportPopover.tsx` and its
two test files are DELETED; their coverage moves to the drawer tests. Drawer shell:

- Fixed right panel, 420px (`max-w-[96vw]`), full height, scrim behind
  (`bg-black/40`-equivalent token + blur), slide-in animation respecting
  `prefers-reduced-motion`. `role="dialog"` `aria-modal="true"`, Esc and scrim-click close,
  focus moves to the number-search input on open. Mounted only while open (same fetch-laziness
  rationale as the popover; both hooks stay mounted while open for per-type memory).
- Header: title `Export`, segmented `Invoices | Orders` toggle, close ✕.
- Body (Invoices, top→bottom, D13): number search → date range (label follows stage:
  `Shipped between` / `Packed between` / `Returned between`; `Ordered between` on Orders) →
  `Stage` radio (Shipped/Packed/Returned) → `Platforms` chips (Shopee/Lazada/TikTok, all on by
  default) → `Invoiced status` 3-state segmented (`All / Not yet invoiced / Already invoiced`)
  → `Shipping option` select (from the new endpoint, first option `All shipping options`) →
  `Preview` (matchline + per-platform table: Platform / Parcels / Items + per-row
  `DownloadButton`, empty rows dimmed with `—`).
- Footer (sticky): `Cancelled orders are always excluded.` (Invoices only), filename hint line
  (Invoices only, from the last generate's `Content-Disposition`), primary
  `Download all (N files)` (N = active platforms after the client-side platform gate; disabled
  + `Nothing to download` when zero).
- Empty state (zero parcels across active platforms): search glyph, `No parcels match`,
  recovery hint copy per mockup.
- Orders side: platform chips + date range + preview (`Rows` column) only (D16).

**Number search (Invoices only, first row):**

- Label `Tracking number or Order number`; input placeholder `Type a tracking or order number…`.
- ≥2 chars (endpoint floor), debounce ~250 ms, `GET /packing-lists/suggest?q=`,
  `AbortController` on stale queries; rows already confirmed as chips are filtered out.
- Suggestion row (D10): platform glyph, tracking number (mono), `Order · <order_number>`
  beneath (muted), `packing_status` pill using the dashboard's existing status pill palette.
- Highlight (D9): contiguous case-insensitive `indexOf` on each field; wrap the matched run in
  an accent-highlighted span (mockup's `.hl` treatment). Both fields highlight independently.
- Click row (or Enter = first row, Esc closes list) → chip above the input showing the
  TRACKING number with a ✕ remover (D12).
- Chips ≥1 ⇒ every other filter group gets `opacity-45 pointer-events-none` (+ `dark:`
  variants), amber note under the input: `Filters below ignored — exporting the N selected
  numbers instead.` (singular form `1 selected number`); preview refetches with `numbers=` CSV
  and the matchline reads `N parcels match the selected numbers.` / `1 parcel matches the
  selected number.`

**`useWarehouseInvoice` changes:**

- `condition` accepts `"returned"`; new state `invoiced` (`"all" | "not_invoiced" |
  "invoiced"`), `shipping` (`string | null`), `numbers` (`string[]`).
- Preview effect deps + `URLSearchParams` grow accordingly; when `numbers.length > 0` ONLY
  `numbers` is sent (server ignores the rest anyway; not sending keeps URLs honest).
- `generate(platform)` posts the new fields; **filename now read from
  `Content-Disposition`** (`filename="…"` token) with the old client-rebuild kept ONLY as a
  fallback when the header is absent (dev-server proxies); delete the primary duplication (D6).
- New tiny hook or fetch for `GET /exports/invoices/shipping-options` (once per drawer open).

**`useOrdersExport`:** untouched.

**Types:** extend `WarehousePlatformPreview` consumers as needed; suggestion type
`{ trackingNumber, orderNumber, platform, packingStatus, updatedAt }` (camelCase mapping of the
suggest response).

### 4.3 Out of scope

- Backend platform param (D14 — client-side gate), orders-export backend changes (D16).
- `missing` list capping; suggest-endpoint changes (limit/floor stay 8/2).
- Bulk paste of many numbers (explicitly replaced by D8; if it ever returns it is a new ticket).
- `/invoices` deep-dive page; MAUI app; export history UI.
- Suggest-list stage awareness: suggestions show parcels of ANY `packing_status`; selection
  overrides all filters (D11) so no stage/suggestion interlock. Flagged during HITL; revisit
  only if operators select un-exportable parcels in practice.
- SDD execution (separate local session; plan + handoff).

## 5. Test plan sketch

Backend (integration, real Postgres; append to the existing export test file(s) — locate with
`ls backend/tests | grep -i export` at execution time):

- Condition `returned`: preview/generate bind on `returned_at` window.
- `invoiced=not_invoiced` excludes a parcel with `invoiced_at` set; `invoiced=invoiced` is the
  complement; default = both.
- `shipping=` exact value filters; `Unknown` matches blank/NULL via the COALESCE idiom.
- Number mode: `numbers=` returns exactly those parcels grouped by platform; cancelled parcel
  in the list is excluded and counted in `cancelledExcluded`; other params ignored when
  `numbers` present.
- Generate number mode: audit row has `condition='selected'`, NULL `from_ts/to_ts`, `filters`
  jsonb with the numbers array; filename matches `{platform}_selected_{date}_invoice.xlsx`.
- Generate filter mode with `invoiced` filter: `filters` jsonb written; filename gains
  `_filtered`; without extra filters: `filters` NULL, legacy filename.
- `GET /exports/invoices/shipping-options` returns distinct trimmed values incl. `Unknown`.
- CORS: response exposes `Content-Disposition`/`X-Export-Id` (header assert on any route).

Frontend (vitest, jsdom, no @testing-library):

- Drawer shell: opens from Export button, Esc/scrim close, Invoices↔Orders toggle preserves
  each side's state, Orders side hides invoice-only groups/notes.
- Typeahead: <2 chars no fetch; suggestions render tracking/order/status pill; contiguous
  highlight spans on both fields; gapped query renders empty state (D9 regression);
  confirmed chip removed from subsequent suggestions; Enter confirms first row.
- Chips: add/remove; ≥1 chip disables other groups + note text (verbatim, singular/plural);
  preview called with `numbers=` CSV only.
- Stage radio: relabels the date range (`Returned between` etc.) and sends
  `condition=returned`.
- `useWarehouseInvoice`: filename parsed from mocked `Content-Disposition`; fallback path when
  header missing.
- Download all: N follows active platform chips; disabled on empty preview.
