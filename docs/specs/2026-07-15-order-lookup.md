# Spec: Order Lookup — order_number scanning on /ship + dashboard search bar

Date: 2026-07-15. Decisions grilled with the user 2026-07-15 (wayfinder session; user collapsed
the map into direct spec+plan). Touches BOTH submodules: backend worktree
`backend/.worktrees/order-lookup` and frontend worktree `frontend/.worktrees/order-lookup`,
branch `feat/order-lookup` in each, created off the `feat/warehouse-invoice` tips
(BE base `6678c5e`, FE base `b1a10c5`). One combined branch carries both features (user decision).

## 1. Problem

Two related gaps, one theme: **order_number as a first-class lookup key**.

1. **/ship order scanning.** Operators sometimes hold a parcel whose scannable barcode is the
   platform *order number*, not the courier tracking number (notably Shopee **Instant Shipping**
   parcels, whose rows are imported with `tracking_number = order_number` as a placeholder until a
   later upload backfills the real tracking — `imports.rs:298`, `imports.rs:316-317`). Today:
   - **Ship mode** sends the raw barcode to `PATCH /packing-lists/scan/{barcode}/ship`, and the
     backend's `resolve_tracking` (`packing.rs:442-460`) *already* falls back to order_number —
     but with `LIMIT 1`, **no ORDER BY and no eligibility filter**: a multi-parcel order ships an
     arbitrary parcel. Worse, the frontend then records the *scanned barcode* (the order number)
     in the recent-scans list and in `postShippedEvent` → `workflow_events` gets an order number
     stored as a tracking number (`app/ship/page.tsx:109-111`).
   - **Return mode** pre-checks with `searchParcel` which keeps only exact *tracking* matches
     (`app/ship/lib/shipApi.ts:99`) — order scans die client-side with NOT FOUND even though the
     backend could resolve them.
2. **Dashboard search.** The main dashboard (`/`) has no way to look up a parcel at all. The
   warehouse manager must know the exact tracking number and hand-type it on `/tracking`.

## 2. Decisions (locked with user)

| # | Decision |
|---|----------|
| D1 | /ship accepts order_number scans in **ship, return, and undo-return** flows. |
| D2 | Resolution rule: if exactly **one eligible** parcel matches → proceed automatically; otherwise error banner telling the operator to scan the tracking number. No picker, no ship-all. |
| D3 | Dashboard `/` gets a **typeahead search bar** (tracking or order number) backed by the existing `GET /packing-lists/suggest` endpoint; picking a result navigates to the parcel timeline at `/tracking?t=<tracking>`. |
| D4 | One combined branch `feat/order-lookup` per submodule, based on `feat/warehouse-invoice` tips. |
| D5 | Destination: build-ready spec + plan; SDD execution is a separate effort. No mockup round — search bar follows existing header component conventions. |

## 3. Current state (verified 2026-07-15)

### Backend (`feat/warehouse-invoice` @ 6678c5e)

- `GET /packing-lists?q=` (`packing::search`, `packing.rs:92-121`) — **exact**-match UNION:
  rows where `tracking_number = $1`, plus rows where `order_number = $1 AND tracking_number != $1`
  (the `!=` guard deduplicates placeholder rows). Response rows include `packing_status`,
  `order_status`, `platform`, `shipping_options`, `total_items` — but NOT `shipped_at`/`returned_at`.
  `packing_status` alone is sufficient for eligibility (`'Shipped'`, `'Returned'` are statuses).
- `GET /packing-lists/suggest?q=` (`packing.rs:127-147`) — ILIKE substring typeahead over
  tracking + order, `LIMIT 8`, ordered by recency, empty under 2 chars. Returns
  `tracking_number, order_number, platform, packing_status, updated_at`. **Fits D3 unchanged.**
- `resolve_tracking` (`packing.rs:442-460`, `pub(crate)`) — exact tracking fast path, then
  `order_number = $1 LIMIT 1` fallback (unordered, unfiltered). Used by `packing::ship`
  (`packing.rs:327`), `returns::return_scan` (`returns.rs:181`), `returns::undo_return`
  (`returns.rs:264`) + two more `returns.rs` call sites (`:358`, `:404`).
- `videos.rs:96` has a **private duplicate** of the same function (used at `videos.rs:76`).
- `packing::ship` eligibility (`packing.rs:322-360`): Cancelled → 410; `'Shipped'` →
  `alreadyShipped` 200; `'QC Hold'` → 409 "hold"; shippable = `Packed | QC Passed | Packing`
  gated on `all_items_cleared`; force path for supervisors on the not-ready gate.
- Placeholder lifecycle (Shopee Instant Shipping): first import inserts
  `tracking_number = order_number` (`imports.rs:298`, `:348`); a later upload UPDATEs the row to
  the real tracking (`imports.rs:316-317` — `WHERE pl.order_number = $7 AND
  (pl.tracking_number = pl.order_number …)`).
- Integration tests: `tests/ship_api.rs`, `tests/ship_force.rs`, `tests/return_api.rs` run
  against real Postgres.

### Frontend (`feat/warehouse-invoice` @ b1a10c5)

- `/ship` page (`app/ship/page.tsx`):
  - `handleParcel` (ship mode, `:95-117`): `normalizeTracking` (KEX first-token trim), rejects
    empty or **dash-containing** barcodes, then `shipScan(barcode)` straight to the backend.
    On success records `barcode` (the *scanned* string) in recent list + `postShippedEvent`.
  - `handleReturnParcel` (`:119-143`): `searchParcel(barcode)` → exact tracking match only →
    client-side status checks (`'Returned'` → ALREADY RETURNED + supervisor undo offer;
    not `'Shipped'` → NOT SHIPPED) → confirm sheet → `returnScan(parcel.trackingNumber)`.
  - `searchParcel` doc-comment (`shipApi.ts:87-93`) claims the endpoint is fuzzy — **stale**;
    the endpoint is exact (see above). The comment's caution no longer holds.
- `classifyShip` (`app/ship/lib/verdict.ts`): maps 409+"hold" → QC HOLD, other 409 → NOT READY
  (force-eligible). **A new ambiguous-order 409 must be distinguished before the generic 409 arm.**
- Dashboard header (`app/components/Dashboard.tsx`): title left; right controls row =
  `AlertPills · divider · LiveStations · divider · ExportPopover`.
- `/tracking` (`app/components/TrackingTimeline.tsx:197-215`) **already** reads `?t=` via
  `useSearchParams` (Suspense wrapper in place, back/forward sync works). Deep link exists; D3
  needs zero changes here.
- Test conventions: vitest 4 + jsdom, `createRoot` + `act`, NO @testing-library. Ship-lib tests
  exist at `app/ship/lib/{verdict,returnVerdict,shipApi,normalizeTracking,beep}.test.ts`.

### Order number formats (dev-DB + import lineage)

Real platform order numbers contain **no dashes**: TikTok ~18-digit numeric (122 real rows in
dev DB, all `^[0-9]+$`), Shopee 14-char uppercase alphanumeric (date-prefixed), Lazada numeric.
Dash-containing rows in dev DB are synthetic test data (`WHI-*`, `ORD-*`). The /ship dash-guard
(`barcode.includes("-")` → reject) therefore stays. **Verification task for the executor:** spot-check
a production sample before relying on this (query in plan Task 1 notes).

## 4. Design

### 4.1 Backend — unified candidate resolution (`packing.rs`)

Replace `resolve_tracking`'s two-step fast-path/fallback with a **unified candidate set** so
placeholder rows (tracking == order) cannot shadow same-order siblings:

```sql
SELECT tracking_number, packing_status
FROM packing_lists
WHERE tracking_number = $1
   OR (order_number = $1 AND tracking_number != $1)
ORDER BY created_at DESC
```

(the same shape as `packing::search`'s UNION, deduplicated by the `!=` guard).

New signature (name indicative, executor may adjust):

```rust
pub(crate) enum ScanOp { Ship, Return, UndoReturn }
pub(crate) async fn resolve_scan_target(pool: &PgPool, input: &str, op: ScanOp)
    -> Result<String, AppError>
```

Resolution rules, in order:

1. **No candidates** → `AppError::NotFound` (404, unchanged).
2. **Exactly one candidate** → return it regardless of status (the handler's own status checks
   produce the natural verdict — ALREADY SHIPPED, NOT SHIPPED, CANCELLED, etc.). This preserves
   today's behavior for every plain tracking scan and single-parcel order.
3. **Multiple candidates** → filter by op eligibility on `packing_status`:
   - `Ship`: eligible = status NOT IN (`'Shipped'`, `'Returned'`)
   - `Return`: eligible = status = `'Shipped'`
   - `UndoReturn`: eligible = status = `'Returned'`
   - exactly **1 eligible** → return it (D2 auto-proceed).
   - **≥2 eligible** → `AppError::Conflict` with verbatim message
     `ambiguous order: {n} parcels — scan tracking number` (409).
   - **0 eligible** → return the most recent candidate (`ORDER BY created_at DESC` first row);
     the handler's status checks then yield the correct terminal verdict (e.g. every parcel
     already shipped → ALREADY SHIPPED).

Call-site changes: `packing::ship` passes `ScanOp::Ship`; `returns.rs:181` → `ScanOp::Return`;
`returns.rs:264` → `ScanOp::UndoReturn`. The two other `returns.rs` call sites (`:358`, `:404` —
inspect what they serve; they are item-restock/report paths) pass the op matching their flow
(executor judgment; if they operate on already-returned parcels, `UndoReturn` semantics fit —
i.e. eligible = `'Returned'`).

**Response contract change (additive):** `ship`, `return_scan`, and `undo_return` responses gain
`"tracking": "<resolved tracking_number>"` in their success JSON bodies so the client can display
and audit the real parcel identity. Existing consumers ignore unknown fields — safe.

`videos.rs:96` private copy: **out of scope** (video attach flow, different ambiguity semantics).
Add `ORDER BY created_at DESC` to its fallback for determinism if touched, but no eligibility work.

### 4.2 Backend — ambiguity error contract

`AppError::Conflict("ambiguous order: {n} parcels — scan tracking number")` — the FE
distinguishes it from the not-ready 409 by substring `ambiguous order` in `body.message` (mirrors
the existing `"hold"` sniff in `classifyShip`). It must NOT be force-eligible and must NOT match
the `msg.includes("hold")` arm (it does not contain "hold").

### 4.3 Frontend — /ship changes

- **`shipApi.ts`**
  - Ship/return/undo response types gain optional `tracking?: string`.
  - Fix the stale fuzzy-search doc-comment on `searchParcel`.
  - New `resolveParcelForReturn(barcode): Promise<{ parcel: ParcelLite | null; ambiguous: number; returned: ParcelLite | null }>`
    (shape indicative): fetch `/packing-lists?q=`; exact tracking match wins; else among
    order-matched rows compute eligible = `packingStatus === 'Shipped'`;
    1 eligible → `parcel`; ≥2 → `ambiguous: n`; 0 eligible → surface a `'Returned'` row (if any)
    so the caller can keep the ALREADY-RETURNED + supervisor-undo affordance, else null.
- **`verdict.ts`** — `classifyShip` gains `ambiguousOrder` outcome: status 409 AND message
  includes `ambiguous order` → `{ outcome: "ambiguousOrder", tone: "error", label: "MULTIPLE
  PARCELS — SCAN TRACKING", forceEligible: false }`, checked **before** the hold arm and the
  generic 409 arm.
- **`returnVerdict.ts`** — no BE-code change needed for returns ambiguity (it is resolved
  client-side pre-write), but `classifyReturn` gets the same guard for defense if the executor
  routes raw barcodes to the endpoint.
- **`page.tsx`**
  - `handleParcel`: on `shipped`, use `res.body.tracking ?? barcode` for the recent-list row and
    `postShippedEvent` — the order-as-tracking audit bug dies here.
  - `handleReturnParcel`: replace the `searchParcel` block with `resolveParcelForReturn`:
    `ambiguous ≥ 2` → flash `MULTIPLE PARCELS — SCAN TRACKING`; `returned` present → existing
    ALREADY RETURNED flash + `setPendingUndo(returned.trackingNumber)` for supervisors;
    `parcel` → existing confirm-sheet path (unchanged from there on); none → NOT FOUND /
    NOT SHIPPED as today.
  - Dash-guard and `normalizeTracking` stay untouched (§3 formats).
- Banner copy is part of the deliverable: `MULTIPLE PARCELS — SCAN TRACKING` (both modes).

### 4.4 Frontend — dashboard search bar

New component `app/components/DashboardSearch.tsx`, mounted in the Dashboard header controls row
**leftmost** (before `AlertPills`, followed by the existing divider pattern).

- Input, placeholder `Search tracking or order…` (mirrors `AlertReconcileDropdown`'s copy at
  `AlertReconcileDropdown.tsx:237`).
- ≥2 chars, debounced 300 ms → `GET /packing-lists/suggest?q=`; abort/ignore stale responses.
- Dropdown (max 8 rows, endpoint-limited): tracking number (mono), order number (muted),
  platform via existing `PlatformGlyph`, packing-status pill. States: loading, `No matches`,
  error silently treated as empty.
- Keyboard: ↑/↓ move, Enter selects highlighted (or first), Esc closes; click-outside closes.
- Select → `router.push('/tracking?t=' + encodeURIComponent(tracking_number))` — always the
  tracking number, even when matched on order.
- Tailwind v4: every new style needs a `dark:` variant. No @testing-library in tests.
- No backend change; no /tracking change (deep link already live).

### 4.5 Out of scope

- MAUI desktop app changes (its scan flows already call the shared endpoints).
- `videos.rs` resolve copy beyond the determinism note (§4.1).
- Order-number search on the /tracking page's own input (timeline is tracking-keyed; fog for a
  later effort).
- Suggest-endpoint enrichment (e.g. shipped_at in rows) — current fields suffice.
- Renaming historical `workflow_events` rows that already carry order numbers as tracking.
- SDD execution (separate effort; see plan + handoff).

## 5. Test plan sketch

Backend (integration, real Postgres; append to existing files):
- `ship_api.rs`: order scan single eligible parcel ships that parcel + response `tracking` equals
  real tracking; multi-parcel order with 2 eligible → 409 `ambiguous order`; all parcels shipped →
  `alreadyShipped` on most recent; placeholder row (tracking == order) + one real sibling →
  ambiguity respected (the Instant Shipping shadow case); unknown input → 404.
- `return_api.rs`: order scan resolves the single `'Shipped'` parcel among mixed statuses;
  2 shipped siblings → 409; undo path resolves the single `'Returned'` parcel.

Frontend (vitest):
- `verdict.test.ts`: ambiguous-order 409 → `ambiguousOrder`, not `blocked`, not force-eligible;
  hold arm unaffected.
- `shipApi.test.ts`: `resolveParcelForReturn` matrix (exact tracking hit; order hit 1 eligible;
  ≥2 eligible; 0 eligible w/ returned row; no rows).
- `DashboardSearch.test.tsx` (jsdom): renders rows from mocked fetch, keyboard nav, Enter
  navigates with tracking (mock `next/navigation` router), Esc/blur closes, <2 chars no fetch.
