# Export Drawer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the dashboard `ExportPopover` with a right-side Export drawer: Invoices gain a Returned stage, invoiced/shipping filters, and a typeahead number-selection mode that overrides all other filters; Orders keeps platform + dates.

**Architecture:** Backend extends the invoice export pipeline in place — `Condition` gains `returned`, `select_parcels` gains invoiced/shipping residual predicates plus a tracking-number override mode, `record_export` persists a new nullable `filters` jsonb (with `condition='selected'`, NULL window for number-mode), filenames gain `_filtered`/`selected` markers, CORS exposes `Content-Disposition`/`X-Export-Id`, and a tiny distinct-shipping-options endpoint is added. The existing `GET /packing-lists/suggest` endpoint already implements the contiguous-substring typeahead. Frontend builds `ExportDrawer` (V1 flat-stack layout from the mockup) + a chip-confirm typeahead, extends `useWarehouseInvoice`, deletes the popover and its client-side filename duplication.

**Tech Stack:** Rust / Axum / SQLx (integration tests vs real Postgres) · Next.js 16 / React 19 / Tailwind v4 / TypeScript strict / vitest 4 + jsdom (NO @testing-library).

## Global Constraints

- Spec: `docs/specs/2026-07-23-export-drawer.md` — decisions D1–D16 and all verbatim strings are deliverables. Research reference: `docs/research/2026-07-23-export-backend-surface.md`. Visual reference: `docs/mockups/2026-07-23-export-drawer-v1-v2-v3.html` (`?variant=v1` ONLY — V2/V3 were rejected).
- Backend worktree: `backend/.worktrees/export-drawer`, branch `feat/export-drawer`, base `origin/dev-1.3` (= `05b7505`). Frontend worktree: `frontend/.worktrees/export-drawer`, branch `feat/export-drawer`, base `origin/dev-1.1` (= `687e02f`). Create via `git worktree add .worktrees/export-drawer -b feat/export-drawer origin/dev-1.3` (resp. `dev-1.1`) inside each submodule. Commit in the submodule worktrees, never the monorepo root.
- Backend tests: `cargo test --no-fail-fast` from the BE worktree. Integration tests need the dev stack: Postgres container `warehouse-postgres`, `DATABASE_URL=postgresql://warehouse_user:warehouse_user@localhost:5432/warehouse_db_test`, `sqlx migrate run --source migrations --ignore-missing` (shared dev DB has an orphan migration row — never delete it). Some suites have PRE-EXISTING failures on the base lineage — before starting, record the baseline (`cargo test --no-fail-fast 2>&1 | tail -30`) and only require new/changed tests green plus no regressions vs that baseline.
- NEVER edit an applied migration — new timestamped files only, stacked after the newest existing migration.
- Frontend tests: `npm test` (vitest run) + `npm run lint`; `// @vitest-environment jsdom` per DOM test file; `react-dom/client` `createRoot` + `act` from `react`; `(globalThis as any).IS_REACT_ACT_ENVIRONMENT = true`; NO @testing-library. Tailwind v4: every new style needs a `dark:` variant.
- Verbatim UI strings (from the mockup / spec):
  - Search label `Tracking number or Order number` · placeholder `Type a tracking or order number…`
  - Override note: `Filters below ignored — exporting the N selected numbers instead.` (`1 selected number` singular)
  - Matchline: `N parcels match your filters.` / `N parcels match the selected numbers.` / `1 parcel matches the selected number.`
  - Footer note: `Cancelled orders are always excluded.` · button `Download all (N files)` / disabled empty-state `Nothing to download`
  - Empty state heading `No parcels match`
  - Invoiced segmented: `All` / `Not yet invoiced` / `Already invoiced` · shipping select first option `All shipping options`
  - Date labels: `Shipped between` / `Packed between` / `Returned between` / `Ordered between` (Orders)
- Backend literals: audit condition `selected`; filename shapes `{platform}_{condition}_{range}_filtered_invoice.xlsx` and `{platform}_selected_{yyyymmdd}_invoice.xlsx` (Bangkok +07:00 dates, same helper as today).
- TDD every task: failing test first, watch it fail, implement, watch it pass.
- Pushes and monorepo submodule-pointer bumps are USER-GATED — do not push.

---

## Phase 1 — Backend (worktree `backend/.worktrees/export-drawer`)

### Task 1: Migrations + `Condition::Returned`

**Files:**
- Create: `migrations/<now>_invoice_export_filters.sql`
- Create: `migrations/<now+1>_broaden_returned_at_index.sql`
- Modify: `src/api/exports/invoices.rs` (`Condition` enum + `ts_column`, ~lines 32-56)
- Test: the existing invoice-export integration test file (`ls tests | grep -i -E "invoice|export"`; append there — if none exists, create `tests/invoice_export_filters.rs` following the setup idiom of `tests/ship_api.rs`)

**Interfaces:**
- Produces: `Condition::Returned` (serde `"returned"`, `ts_column() == "returned_at"`); schema columns `invoice_exports.filters jsonb NULL`, `from_ts/to_ts` nullable.

Migration A (`_invoice_export_filters.sql`):

```sql
ALTER TABLE public.invoice_exports
    ALTER COLUMN from_ts DROP NOT NULL,
    ALTER COLUMN to_ts DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS filters jsonb NULL;
```

Migration B (`_broaden_returned_at_index.sql` — mirrors `20260710230000_broaden_shipped_at_index.sql`):

```sql
DROP INDEX IF EXISTS idx_packing_lists_returned_at;
CREATE INDEX idx_packing_lists_returned_at
    ON public.packing_lists (returned_at)
    WHERE returned_at IS NOT NULL;
```

- [ ] **Step 1: Failing test** — append an integration test: seed a parcel with `returned_at` inside a window (and `shipped_at` outside it), `GET /exports/invoices/preview?condition=returned&from=…&to=…` → 200 and the parcel counted; `condition=shipped` over the same window → zero.
- [ ] **Step 2: Red** — `cargo test --no-fail-fast --test <file>` fails (unknown variant `returned`).
- [ ] **Step 3: Implement** — run `sqlx migrate run --source migrations --ignore-missing`; add `Returned` to `Condition` + `ts_column`.
- [ ] **Step 4: Green** — targeted test file passes; full sweep no regressions vs baseline.
- [ ] **Step 5: Commit** `feat(exports): returned condition + filters/audit migrations`.

### Task 2: `select_parcels` filters — invoiced, shipping, number mode

**Files:**
- Modify: `src/api/exports/invoices.rs` (`select_parcels` `:62-90`, `PreviewQuery` `:92-97`, preview handler `:121-158`)
- Test: same integration file as Task 1 (append)

**Interfaces:**
- Consumes: Task 1's `Condition::Returned`.
- Produces: `struct ParcelFilter { condition: Condition, from: DateTime<Utc>, to: DateTime<Utc>, invoiced: InvoicedFilter, shipping: Option<String>, numbers: Vec<String> }` (name indicative) consumed by Task 3's generate path; `InvoicedFilter` enum `All | NotInvoiced | Invoiced` (serde lowercase `all|not_invoiced|invoiced`, default `All`).

**Context you must know:**
- `PreviewQuery` gains `invoiced` (default all), `shipping: Option<String>`, `numbers: Option<String>` (CSV — axum `Query` can't do `Vec`, research §7.1; split on `,`, trim, drop empties).
- Number mode (spec D11/D12): `numbers` non-empty ⇒ ignore condition/from/to/invoiced/shipping. SQL:

```sql
-- trackings
SELECT tracking_number FROM packing_lists
WHERE tracking_number = ANY($1)
  AND (order_status IS NULL OR order_status <> 'Cancelled');
-- cancelled_excluded
SELECT COUNT(*) FROM packing_lists
WHERE tracking_number = ANY($1) AND order_status = 'Cancelled';
```

- Filter mode: existing window SQL + appended bound residuals:
  - invoiced: `AND invoiced_at IS NULL` / `AND invoiced_at IS NOT NULL` (compile-time constant fragments chosen by match — do NOT interpolate user input; only `ts_column()` constants ever reach `format!`).
  - shipping: `AND COALESCE(NULLIF(btrim(shipping_options), ''), 'Unknown') = $n` (bound).
  - Apply the same residuals to the `cancelled_excluded` count query so the note stays consistent.

- [ ] **Step 1: Failing tests** — append: (a) parcel with `invoiced_at` set is excluded under `invoiced=not_invoiced` and included under `invoiced=invoiced`; (b) `shipping=Standard delivery` keeps only matching parcels and `shipping=Unknown` matches a blank-`shipping_options` parcel; (c) `numbers=T1,T2` returns exactly those two grouped by platform while a third in-window parcel is absent; (d) cancelled parcel inside `numbers` list is excluded and `cancelledExcluded == 1`; (e) `numbers` present + absurd window (`from > to`) still returns the parcels (other params ignored).
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement** `InvoicedFilter`, CSV parse, two-mode `select_parcels`.
- [ ] **Step 4: Green** + full sweep vs baseline.
- [ ] **Step 5: Commit** `feat(exports): invoiced/shipping filters + tracking-number selection mode`.

### Task 3: Generate — audit filters, filenames, CORS expose

**Files:**
- Modify: `src/api/exports/invoices.rs` (`GenerateRequest` `:161-170`, `build_filename` `:174-194`, generate `:196-253`, `record_export` `:260-316`)
- Modify: `src/api/mod.rs` (CORS `:50-58`)
- Test: same integration file (append)

**Interfaces:**
- Consumes: Task 2's `ParcelFilter`/`InvoicedFilter`.
- Produces: `invoice_exports` rows with `filters` jsonb per spec §4.1; exposed headers for Task 5.

**Context you must know:**
- `GenerateRequest` gains `invoiced` (default all), `shipping: Option<String>`, `numbers: Option<Vec<String>>` (JSON body — real array, no CSV).
- Audit rules (spec §4.1): number mode ⇒ `condition='selected'`, `from_ts/to_ts` NULL, `filters = {"numbers": [...]}`. Filter mode ⇒ condition/window as today; `filters` jsonb ONLY when a non-default extra filter is active, e.g. `{"invoiced":"not_invoiced","shipping":"Standard delivery"}` (omit default keys); else NULL.
- Filenames: filter mode + any extra filter ⇒ insert `_filtered` before `_invoice`; number mode ⇒ `{platform}_selected_{yyyymmdd}_invoice.xlsx` (single Bangkok date, reuse the existing +07:00 formatting helper).
- CORS: 

```rust
use axum::http::{header, HeaderName};
// on the CorsLayer chain:
.expose_headers([header::CONTENT_DISPOSITION, HeaderName::from_static("x-export-id")])
```

- [ ] **Step 1: Failing tests** — (a) generate with `numbers` ⇒ 200, audit row `condition='selected'`, `from_ts IS NULL`, `filters->'numbers'` array matches, filename matches `^{platform}_selected_\d{8}_invoice\.xlsx$`; (b) generate with `invoiced=not_invoiced` ⇒ `filters` jsonb `{"invoiced":"not_invoiced"}` + filename contains `_filtered_invoice`; (c) plain generate ⇒ `filters IS NULL`, legacy filename (regression); (d) any response carries `access-control-expose-headers` containing `content-disposition` (fire a preflight/simple GET and assert the header).
- [ ] **Step 2: Red.** → **Step 3: Implement.** → **Step 4: Green** + sweep.
- [ ] **Step 5: Commit** `feat(exports): filtered-export audit trail, filenames, exposed headers`.

### Task 4: `GET /exports/invoices/shipping-options`

**Files:**
- Modify: `src/api/exports/invoices.rs` (new handler at the end), `src/api/mod.rs` (route next to `:265-266`)
- Test: same integration file (append)

**Interfaces:**
- Produces: `GET /exports/invoices/shipping-options` → `Json<Vec<String>>` for Task 7's dropdown.

Handler:

```rust
pub async fn shipping_options(State(state): State<AppState>) -> Result<Json<Vec<String>>, AppError> {
    let rows: Vec<(String,)> = sqlx::query_as(
        "SELECT DISTINCT COALESCE(NULLIF(btrim(shipping_options), ''), 'Unknown') AS v
         FROM packing_lists ORDER BY v",
    )
    .fetch_all(&state.pool)
    .await?;
    Ok(Json(rows.into_iter().map(|(s,)| s).collect()))
}
```

(match the State extractor shape the sibling handlers in `invoices.rs` actually use).

- [ ] **Step 1: Failing test** — seed parcels with `'Standard delivery'`, `'  '`, NULL ⇒ endpoint returns `["Standard delivery", "Unknown"]` sorted.
- [ ] **Step 2: Red.** → **Step 3: Implement + route.** → **Step 4: Green** + sweep.
- [ ] **Step 5: Commit** `feat(exports): distinct shipping-options endpoint for the drawer`.

---

## Phase 2 — Frontend (worktree `frontend/.worktrees/export-drawer`)

### Task 5: `useWarehouseInvoice` — new filters, numbers mode, header filename

**Files:**
- Modify: `app/hooks/useWarehouseInvoice.ts`
- Test: `app/hooks/useWarehouseInvoice.test.ts` (create if absent; mock `fetch`)

**Interfaces:**
- Consumes: Task 2/3 params (`invoiced`, `shipping`, `numbers`, CSV on GET / arrays on POST).
- Produces (Task 7 consumes): hook returns gain `invoiced, setInvoiced` (`"all" | "not_invoiced" | "invoiced"`), `shipping, setShipping` (`string | null`), `numbers, setNumbers` (`string[]`); `Condition` type union gains `"returned"`; `gen` state's `done` filename comes from the response header.

**Context you must know:**
- Preview effect (`useWarehouseInvoice.ts:98-123`): add the new state to the dep array; when `numbers.length > 0` send ONLY `numbers=<csv>` (drop the other params from `URLSearchParams`); else append `invoiced` (when not `all`) and `shipping` (when set).
- `generate` (`:125-153`): include `invoiced`/`shipping`/`numbers` in the POST body under the same conditions; filename resolution becomes:

```ts
const cd = res.headers.get("content-disposition");
const m = cd && /filename="?([^";]+)"?/i.exec(cd);
const filename = m ? m[1] : buildFallbackFilename(platform); // old logic, demoted to fallback
```

- [ ] **Step 1: Failing tests** — (a) with `numbers: ["T1","T2"]` the preview URL contains `numbers=T1%2CT2` and NOT `condition=`; (b) with `invoiced="not_invoiced"` + `shipping="Unknown"` both params present; (c) `generate` resolves filename from a mocked `Content-Disposition: attachment; filename="x_selected_20260723_invoice.xlsx"`; (d) header absent ⇒ fallback filename used; (e) `condition` accepts `"returned"` and lands in the URL.
- [ ] **Step 2: Red** (`npm test -- useWarehouseInvoice`). → **Step 3: Implement.** → **Step 4: Green + lint.**
- [ ] **Step 5: Commit** `feat(export-hooks): returned stage, invoiced/shipping/numbers filters, header filename`.

### Task 6: `ExportNumberSearch` — typeahead + confirm chips

**Files:**
- Create: `app/components/ExportNumberSearch.tsx`
- Test: `app/components/ExportNumberSearch.test.tsx` (jsdom)

**Interfaces:**
- Produces (Task 7 consumes):

```tsx
export interface ParcelSuggestion {
  trackingNumber: string; orderNumber: string;
  platform: string | null; packingStatus: string; updatedAt: string | null;
}
export function ExportNumberSearch({ chips, onChange }: {
  chips: string[];                       // confirmed tracking numbers
  onChange: (chips: string[]) => void;
}) { … }
```

**Context you must know:**
- Fetch: ≥2 chars, ~250 ms debounce, `GET ${API}/packing-lists/suggest?q=` (response fields are snake_case: `tracking_number`, `order_number`, `platform`, `packing_status`, `updated_at` — map to `ParcelSuggestion`), `AbortController` per keystroke, rows whose tracking is already in `chips` filtered out.
- Highlight helper (contiguous only — spec D9):

```ts
function highlight(text: string, q: string): ReactNode {
  const i = text.toLowerCase().indexOf(q.toLowerCase());
  if (i < 0 || !q) return text;
  return (<>{text.slice(0, i)}<span className="rounded-[3px] bg-brand/15 font-extrabold text-brand dark:bg-brand/25">{text.slice(i, i + q.length)}</span>{text.slice(i + q.length)}</>);
}
```

- Row layout per mockup: platform glyph (reuse `PLATFORM_ICONS`/`PlatformGlyph` from `./shared`), mono tracking line, muted `Order · {orderNumber}` line (both through `highlight`), status pill on the right — reuse the dashboard's existing packing-status pill classes (grep for the status pill in `PackingTable.tsx`/dashboard components and reuse; do NOT invent a new palette).
- Interactions: click row / Enter (first row) → `onChange([...chips, trackingNumber])`, clear input, close list, refocus input; Esc closes list; click-outside closes; chip ✕ → `onChange(chips.filter(...))`. Chips render above the input, mono, wrap.
- Label + placeholder are the Global Constraints verbatim strings. All styles need `dark:` variants.

- [ ] **Step 1: Failing tests** — (a) 1 char ⇒ no fetch; 2 chars ⇒ fetch with `q=`; (b) rows render tracking + `Order · …` + status pill text; (c) highlight span wraps the matched run in BOTH fields (query matching order number); (d) click row calls `onChange` with the tracking number and clears the input; (e) Enter confirms first row; (f) a chip's tracking never reappears in suggestions; (g) ✕ removes.
- [ ] **Step 2: Red.** → **Step 3: Implement.** → **Step 4: Green + lint.**
- [ ] **Step 5: Commit** `feat(export-drawer): tracking/order typeahead with confirm chips`.

### Task 7: `ExportDrawer` — shell + filters + preview; popover retired

**Files:**
- Create: `app/components/ExportDrawer.tsx`
- Modify: `app/components/Dashboard.tsx` (swap `ExportPopover` → export button + `ExportDrawer`)
- Delete: `app/components/ExportPopover.tsx`, `ExportPopover.shell.test.tsx`, `ExportPopover.orders.test.tsx`
- Test: `app/components/ExportDrawer.shell.test.tsx`, `app/components/ExportDrawer.invoices.test.tsx`, `app/components/ExportDrawer.orders.test.tsx` (jsdom)

**Interfaces:**
- Consumes: Task 5 hook surface, Task 6 `ExportNumberSearch`, existing `useOrdersExport`, existing `DatePicker`, `PLATFORM_ICONS`, the `DownloadButton` pattern (lift it out of the popover before deleting — move to a shared location or inline copy in the drawer, executor's call, but keep its spinner/check/error-title behavior).

**Context you must know:**
- Shell: `role="dialog"` `aria-modal="true"`, fixed right 420px `max-w-[96vw]`, scrim button behind (click closes), Esc closes, focus the search input on open, slide-in with `motion-reduce:` variant, mounted only while open. Header: `Export` title + `Invoices | Orders` segmented + ✕.
- Invoices body order (spec D13): `ExportNumberSearch` → date range (label switches by stage/type) → Stage radio → Platforms chips → Invoiced segmented → Shipping select (options from `GET /exports/invoices/shipping-options`, prepend `All shipping options` ⇒ `shipping=null`) → Preview table.
- `chips.length > 0` ⇒ wrap every other filter group in `opacity-45 pointer-events-none dark:opacity-40` and show the amber override note (verbatim, singular/plural). Preview matchline strings verbatim.
- Platform chips are CLIENT-side (spec D14): they gate which preview rows render and which platforms `Download all` iterates; `Download all (N files)` counts active non-empty platforms; zero ⇒ disabled `Nothing to download`. Empty preview ⇒ `No parcels match` block per mockup.
- Orders side: platform chips + date range (`Ordered between`) + preview with `Rows` column; no invoice-only groups, no cancelled/filename footer notes (spec D16); `useOrdersExport` untouched.
- Footer (Invoices): `Cancelled orders are always excluded.` + filename hint line showing the last generate's header-derived filename + `Download all`.
- Both hooks mounted while the drawer is open (per-type state memory — same rationale documented in the popover's module comment; keep that comment's essence).
- Port the meaningful assertions from the two deleted popover test files into the new drawer tests (type toggle memory, per-row download states, orders rows) so coverage does not shrink.

- [ ] **Step 1: Failing tests** — shell: Export button opens, Esc closes, scrim click closes, toggle to Orders hides invoice-only groups; invoices: stage radio switches date label to `Returned between`, chips ≥1 dims groups + note text, platform chip off hides its row and decrements `Download all (N files)`, empty preview shows `No parcels match` + disabled `Nothing to download`; orders: rows render with `Rows` header, no cancelled note.
- [ ] **Step 2: Red.** → **Step 3: Implement drawer + Dashboard swap + delete popover files.** → **Step 4: Green + `npm run lint` + full `npm test`.**
- [ ] **Step 5: Commit** `feat(export-drawer): right-panel export drawer replaces popover`.

---

## Final verification (both worktrees)

- [ ] BE: `cargo test --no-fail-fast` — no regressions vs the recorded baseline; migrations applied cleanly on the dev DB.
- [ ] FE: `npm test` + `npm run lint` clean (modulo documented pre-existing warnings).
- [ ] Manual smoke against the local stack (backend :8080, frontend :3000): open drawer → Returned stage preview → confirm two numbers via typeahead → preview shows exactly those parcels → generate one file → filename from `Content-Disposition` matches `selected` shape → audit row has `filters` jsonb.
- [ ] Cross-check every verbatim string in Global Constraints against the rendered drawer.
