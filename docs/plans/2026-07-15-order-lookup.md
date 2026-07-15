# Order Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** /ship accepts order_number scans (ship, return, undo) with a safe one-eligible-parcel auto-resolve rule; the main dashboard gains a typeahead search bar (tracking or order) that deep-links to `/tracking?t=`.

**Architecture:** Backend replaces `resolve_tracking`'s LIMIT-1 order fallback with a unified candidate query (same shape as `packing::search`'s UNION) plus per-operation eligibility (`ScanOp::{Ship,Return,UndoReturn}`), returning 409 `ambiguous order: {n} parcels — scan tracking number` when several parcels qualify, and adds the resolved `tracking` to ship/return/undo success bodies. Frontend /ship consumes the resolved tracking (killing the order-number-as-tracking audit bug in `postShippedEvent`), adds an `ambiguousOrder` verdict, and swaps return-mode's exact-tracking pre-check for an order-aware resolver. A new `DashboardSearch` header component rides the existing `/packing-lists/suggest` endpoint; `/tracking` already accepts `?t=` — zero change there.

**Tech Stack:** Rust / Axum / SQLx (integration tests vs real Postgres) · Next.js 16 / React 19 / Tailwind v4 / TypeScript strict / vitest 4 + jsdom (NO @testing-library).

## Global Constraints

- Spec: `docs/specs/2026-07-15-order-lookup.md` — copy strings and the resolution rules in §4.1 are verbatim deliverables.
- Backend worktree: `backend/.worktrees/order-lookup`, branch `feat/order-lookup` (base `6678c5e`). Frontend worktree: `frontend/.worktrees/order-lookup`, branch `feat/order-lookup` (base `b1a10c5`). Commit in each submodule worktree, never the monorepo root.
- Backend tests: `cargo test --no-fail-fast` from the BE worktree; 3 leaderboard + 1 product_insights failures are PRE-EXISTING on the base lineage — only new/changed tests must be green. Integration tests need the dev stack: Postgres `warehouse-postgres` container, `DATABASE_URL=postgresql://warehouse_user:warehouse_user@localhost:5432/warehouse_db_test`, `sqlx migrate run --source migrations --ignore-missing` (shared dev DB has an orphan migration row — never delete it). No `.sqlx` offline cache on this lineage → builds need the live DB.
- Frontend tests: `npm test` (vitest run) + `npm run lint`; `// @vitest-environment jsdom` per DOM test file; `react-dom/client` `createRoot` + `act` from `react`, `(globalThis as any).IS_REACT_ACT_ENVIRONMENT = true`; NO @testing-library. Pre-existing `no-unused-vars` warning on `PackingTable.tsx` (`selectedVideos`) is not yours.
- Tailwind v4: every new style needs a `dark:` variant.
- Verbatim strings:
  - BE 409 message: `ambiguous order: {n} parcels — scan tracking number`
  - FE banner label (ship + return): `MULTIPLE PARCELS — SCAN TRACKING`
  - Search placeholder: `Search tracking or order…` · empty state: `No matches`
- TDD every task: write the failing test first, watch it fail, implement, watch it pass.
- Pushes and monorepo submodule-pointer bumps are USER-GATED — do not push.

---

## Phase 1 — Backend (worktree `backend/.worktrees/order-lookup`)

`cd backend/.worktrees/order-lookup` first in each shell; commits land in that submodule worktree.

### Task 1: `resolve_scan_target` — unified candidates + eligibility + ambiguity

**Files:**
- Modify: `src/api/packing.rs` (replace `resolve_tracking` at `:442-460`; update `ship`'s call at `:327`; add `tracking` to ship success bodies at `:343-345` and `:437-439`)
- Test: `tests/ship_api.rs` (append)

**Context you must know:**
- Candidate query (spec §4.1): `WHERE tracking_number = $1 OR (order_number = $1 AND tracking_number != $1) ORDER BY created_at DESC`, selecting `(tracking_number, packing_status)`. The `!=` guard deduplicates Shopee Instant-Shipping placeholder rows (`tracking_number = order_number`, see `imports.rs:298`, `:316-317`).
- Rules: 0 candidates → 404 · 1 candidate → return it unfiltered (handlers produce natural verdicts) · many → apply op eligibility (Ship: NOT IN ('Shipped','Returned') · Return: = 'Shipped' · UndoReturn: = 'Returned'); 1 eligible → return it; ≥2 → `AppError::Conflict("ambiguous order: {n} parcels — scan tracking number")`; 0 eligible → most recent candidate.
- Keep the function `pub(crate)` — `returns.rs` imports it (`returns.rs:8`). Task 2 updates those call sites; to keep this task compiling solo, either keep a thin `resolve_tracking` wrapper delegating with a default op, or do the mechanical call-site swap here and the behavioral return-tests in Task 2 (executor's choice; state it in the task report).
- Prod-format spot check (spec §3 caveat): before trusting the dash-guard assumption, eyeball `SELECT platform, order_number FROM packing_lists WHERE order_number LIKE '%-%' AND order_number NOT LIKE 'WHI-%' AND order_number NOT LIKE 'ORD-%' LIMIT 10` against a production snapshot if one is reachable; otherwise note it in the task report as unverified.

- [ ] **Step 1: Failing tests** — append to `tests/ship_api.rs`: (a) seed one order `O1` with a single unshipped parcel `T1`; ship by scanning `O1` → 200, `shipped: true`, body `tracking == "T1"`, DB row `T1` is `'Shipped'`; (b) order `O2` with two unshipped parcels → ship by `O2` → 409, message contains `ambiguous order: 2 parcels`; (c) order `O3` with two parcels, one `'Shipped'` + one `'Packed'` → scan `O3` ships the packed one; (d) order `O4` all parcels `'Shipped'` → scan `O4` → 200 `alreadyShipped: true`; (e) placeholder shadow: parcel `P1` with `tracking = order = 'O5'` (status `'Packed'`) plus sibling real parcel `T5` (`order = 'O5'`, status `'Packed'`) → scan `O5` → 409 ambiguous; (f) unknown input → 404.
- [ ] **Step 2: Run, watch them fail** (`cargo test --no-fail-fast --test ship_api`).
- [ ] **Step 3: Implement** `ScanOp` + `resolve_scan_target`, wire `ship`, add `tracking` to both ship success JSON bodies.
- [ ] **Step 4: Green** — new tests pass; whole `ship_api` + `ship_force` suites stay green (force path must be unaffected: ambiguity 409 must not be forceable).
- [ ] **Step 5: Commit** (normal message style, e.g. `feat(scan): order-aware scan resolution with ambiguity guard`).

### Task 2: Return/undo/restock call sites + response tracking

**Files:**
- Modify: `src/api/returns.rs` (`:181` → `ScanOp::Return`; `:264` → `ScanOp::UndoReturn`; `:358` `get_return_items` and `:404` `upsert_return_item` → `ScanOp::UndoReturn` — both restock flows operate on returned parcels; add `tracking` to return/undo success bodies)
- Test: `tests/return_api.rs` (append)

- [ ] **Step 1: Failing tests** — (a) order with one `'Shipped'` + one `'Packed'` parcel: return-scan by order resolves the shipped one, body `tracking` = its tracking; (b) two `'Shipped'` siblings → 409 ambiguous; (c) undo by order resolves the single `'Returned'` parcel; (d) `GET /returns/{order}/items` resolves the returned parcel's items.
- [ ] **Step 2: Red** → **Step 3: Implement** → **Step 4: Green** (`--test return_api`, plus full `--no-fail-fast` sweep for the pre-existing-failure baseline).
- [ ] **Step 5: Commit.**

---

## Phase 2 — Frontend /ship (worktree `frontend/.worktrees/order-lookup`)

### Task 3: shipApi — response types + `resolveParcelForReturn`

**Files:**
- Modify: `app/ship/lib/shipApi.ts` (response `tracking?: string` on ship/return/undo helpers; fix the stale "FUZZY" doc-comment on `searchParcel` — the endpoint is exact-match; add `resolveParcelForReturn`)
- Test: `app/ship/lib/shipApi.test.ts` (append; mock `fetch`)

`resolveParcelForReturn(barcode)` → `{ parcel: ParcelLite | null; ambiguous: number; returned: ParcelLite | null }`: exact tracking match wins; else among order rows eligible = `packingStatus === 'Shipped'` → 1 = `parcel`; ≥2 = `ambiguous: n`; 0 eligible → `returned` = a `'Returned'` row if present (spec §4.3).

- [ ] Failing tests (five-case matrix from spec §5) → red → implement → green → commit.

### Task 4: Verdicts — `ambiguousOrder`

**Files:**
- Modify: `app/ship/lib/verdict.ts`, `app/ship/lib/returnVerdict.ts`
- Test: `app/ship/lib/verdict.test.ts`, `app/ship/lib/returnVerdict.test.ts` (append)

409 + `message` contains `ambiguous order` → `{ outcome: "ambiguousOrder", tone: "error", label: "MULTIPLE PARCELS — SCAN TRACKING", forceEligible: false }`, checked BEFORE the `"hold"` sniff and the generic 409 arm in `classifyShip`; same guard in `classifyReturn`. Existing hold/blocked assertions must stay green.

- [ ] Failing tests → red → implement → green → commit.

### Task 5: page.tsx wiring

**Files:**
- Modify: `app/ship/page.tsx` (`handleParcel` `:95-117`: recent-list row + `postShippedEvent` use `res.body?.tracking ?? barcode`; `handleReturnParcel` `:119-143`: swap `searchParcel` for `resolveParcelForReturn` — ambiguous → flash `MULTIPLE PARCELS — SCAN TRACKING`; returned-row → existing ALREADY RETURNED flash + supervisor `setPendingUndo(returned.trackingNumber)`; parcel → unchanged confirm-sheet path; none → NOT FOUND)

No component test exists for page.tsx (hook-heavy); logic lives in the Task 3/4 units. Dash-guard and `normalizeTracking` untouched.

- [ ] Implement → full `npm test` + `npm run lint` green → manual smoke note in task report (dev stack: scan an order number in ship + return modes) → commit.

---

## Phase 3 — Frontend dashboard search

### Task 6: `DashboardSearch` component

**Files:**
- Create: `app/components/DashboardSearch.tsx`
- Test: `app/components/DashboardSearch.test.tsx` (jsdom)

Spec §4.4: ≥2 chars, 300 ms debounce, `GET /packing-lists/suggest?q=`, stale-response guard (abort or sequence counter); rows = tracking (mono) + order (muted) + `PlatformGlyph` + status pill; ↑/↓/Enter/Esc + click-outside; select → `router.push('/tracking?t=' + encodeURIComponent(tracking))`; placeholder `Search tracking or order…`; empty state `No matches`; errors render as empty; `dark:` variants throughout. Mock `next/navigation`'s `useRouter` in tests.

- [ ] Failing tests (render rows from mocked fetch · keyboard nav + Enter navigates with tracking even when matched on order · Esc closes · <2 chars fires no fetch) → red → implement → green → commit.

### Task 7: Header mount

**Files:**
- Modify: `app/components/Dashboard.tsx` (controls row: `DashboardSearch` leftmost, then existing `AlertPills · divider · LiveStations · divider · ExportPopover`; add divider after search per existing pattern)

- [ ] Mount + visual sanity (flex-wrap must not break at 2xl and narrow widths) → full `npm test` + lint → commit.

---

## Phase 4 — Verification

### Task 8: Whole-branch review + smoke

- [ ] BE: full `cargo test --no-fail-fast` — only the 4 known pre-existing failures allowed.
- [ ] FE: `npm test`, `npm run lint`, `npm run build`.
- [ ] Smoke against dev stack (backend `cargo run` on :8080, frontend `npm run dev` on :3000): ship-mode order scan (single-parcel order) ships and the recent row shows the REAL tracking; multi-parcel order scan shows `MULTIPLE PARCELS — SCAN TRACKING`; return-mode order scan reaches the confirm sheet; dashboard search finds a parcel by order number and lands on its `/tracking?t=` timeline.
- [ ] Final code review (superpowers:requesting-code-review) across both worktree diffs vs bases `6678c5e` / `b1a10c5`.
- [ ] Update `docs/plans/2026-07-15-order-lookup.md` checkboxes; write completion notes to the handoff file.
