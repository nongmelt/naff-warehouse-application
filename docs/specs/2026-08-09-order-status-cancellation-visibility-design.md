# Order-status cancellation visibility — design

- **Date:** 2026-08-09
- **Status:** Approved (brainstorming complete, ready for plan)
- **Code branches:** `feat/invoice-unbillable-alert` (both submodules) — frontend worktree
  `frontend/.worktrees/invoice-unbillable-alert`, backend worktree
  `backend/.worktrees/invoice-unbillable-alert`
- **Related:** map #99 invoice blank-tracking; memories `[[project_invoice-blank-tracking-wayfinder]]`,
  `[[reference_invoice-bills-on-shipment-not-payment]]`,
  `[[reference_invoice-export-join-is-tracking-only]]`,
  `[[project_invoice-unbillable-alert-shipped]]`.

## Problem

A parcel the warehouse physically packed and shipped can be cancelled on the
platform *after* shipment. When that happens, `imports.rs` overwrites
`packing_lists.order_status` to `'Cancelled'` (English), while
`packing_status` stays `'Shipped'`. Two consequences, both invisible to the
operator today:

1. **It silently drops from every invoice export.** `select_parcels`
   (`backend/src/api/exports/invoices.rs`) applies
   `AND (order_status IS NULL OR order_status <> 'Cancelled')` in both selection
   and window modes. The parcel is excluded with only an aggregate count
   surfaced, and the cancelled count is not rendered at all.
2. **It never appears on the Unbillable alert.** The `UNBILLABLE_EXCLUSIONS`
   guard (`backend/src/api/dashboard.rs`) excludes `order_status = 'Cancelled'`
   by design, so no false, undismissable alert is raised — but the operator
   gets no explanation anywhere.

Worked example (verified on `warehouse_snapshot_test`): order
`260724GSJF4N96` / tracking `TH265908264825Z` — packed 25 Jul 02:02, shipped
25 Jul 03:30, `order_status` flipped to `Cancelled` at 25 Jul 07:11 (~3.5h
post-ship; import row batch 612 carries raw Shopee status `คำขอยกเลิก`). It
matches an import row, so it is **not** unbillable; it is a deliberately
excluded cancellation the operator currently cannot see.

## Background — the billing model (do not change)

- **Billable** = shipped, `invoiced_at IS NULL`, not
  Cancelled/Returned/Duplicate, and joins an import row via
  `PLACEHOLDER_MATCH_PREDICATE` (real tracking equality, or — for a
  placeholder parcel where `tracking_number = order_number` — a blank-tracking
  import row on the same order).
- **Unbillable alert** = shipped + all the above guards + joins **no** import
  row even after placeholder widening.
- `order_status` on `packing_lists` is a **mixed column**: mostly the raw
  platform status (e.g. Thai `ที่ต้องจัดส่ง` = "to ship" on placeholder
  parcels), with `'Cancelled'` written as a normalized override by
  `imports.rs`. Only `'Cancelled'` is actionable and reliably readable; raw
  values are platform noise, sometimes Thai. **This design surfaces only the
  `'Cancelled'` state.**

## Decisions (from grilling)

- **Timeline: cancellation event only.** The alert-detail timeline gains one
  red node, rendered *only* when `order_status === 'Cancelled'`. Raw platform
  statuses are never shown — keeps the noise (and Thai) out of every other
  parcel's detail view.
- **Export: count + expandable parcel list.** The invoice-export footer shows a
  live cancelled-excluded count with a disclosure that expands to the specific
  cancelled parcels. Chosen over count-only; requires the preview endpoint to
  return the parcel list, not just the number.

## Design

### Surface 1 — Backend: preview returns the cancelled parcels

File: `backend/src/api/exports/invoices.rs`.

- New struct `CancelledParcel { order_number: String, tracking_number: String,
  order_status: String }` (serde `camelCase` to match the API convention).
- `Selection` gains `cancelled_parcels: Vec<CancelledParcel>`.
- The two `SELECT COUNT(*) … AND order_status = 'Cancelled'` queries — one in
  numbers mode, one in window mode — become row-selects of
  `order_number, tracking_number, order_status`, `ORDER BY tracking_number`.
  `cancelled_excluded` is derived from `.len()` (single source of truth; the
  count and the list can never disagree).
- `PreviewResponse` gains `cancelled_parcels: Vec<CancelledParcel>`. `generate`
  continues to ignore it (only the count of nothing-billable matters there);
  returned/duplicate residuals stay count-only, unchanged.
- The window-mode select reuses the existing `invoiced_frag` / `shipping_frag`
  bind ordering exactly as the count query did — no new bind parameters beyond
  swapping the projected columns.

### Surface 2 — Frontend: cancellation event in the alert timeline

Files: `frontend/app/types.ts`, `frontend/app/components/AlertDetailPanel.tsx`.

- `types.ts`: add `orderStatus: string | null` to `PackingItem` (backend
  `PackingDetailResponse` already returns it; verified live —
  `GET /packing-lists/TH265908264825Z` → `"orderStatus":"Cancelled"`).
- `AlertDetailPanel.renderTimeline()`: after the Shipped milestone `<li>`, when
  `detail?.orderStatus === 'Cancelled'`, render a red ✕ node —
  label **"Cancelled on platform"**, subtext **"Excluded from billing"**, and
  `detail.updatedAt` as the approximate detection time (muted, prefixed to read
  as approximate, e.g. "~ 25 Jul 07:11"). No hard "cancelled_at" column exists;
  `updated_at` is the best available signal and must be labelled as approximate.
- Rendered independently of the existing Returned milestone (a parcel can be
  cancelled without being returned). The node is the only new timeline element.

### Surface 3 — Frontend: expandable cancelled list in export footer

Files: `frontend/app/hooks/useWarehouseInvoice.ts`,
`frontend/app/components/ExportDrawer.tsx`.

- `useWarehouseInvoice.ts`: extend the preview type with
  `cancelledParcels: { orderNumber: string; trackingNumber: string;
  orderStatus: string }[]` (default `[]` when absent).
- `ExportDrawer` footer (the `type === "invoices"` block): when
  `cancelledExcluded > 0`, render a count line — "N cancelled parcel(s)
  excluded — platform cancelled after ship" — with a disclosure ▾ toggle
  (local `useState`) that expands to one row per parcel showing
  `orderNumber · orderStatus`. Mirror the existing `returned-note` line's
  styling. The static "Cancelled and returned orders are always excluded" note
  stays.

### Testing

- **Backend** (`invoices` tests): `select_parcels` returns `cancelled_parcels`
  containing a shipped-then-cancelled parcel in **both** modes (explicit
  numbers, and date window); a billable parcel never appears in the list; the
  count equals the list length.
- **Frontend**:
  - `AlertDetailPanel` (or a colocated test): timeline renders the cancellation
    node **iff** `orderStatus === 'Cancelled'`; a `Shipped`-only parcel and a
    placeholder (`orderStatus` raw/Thai) render no cancellation node.
  - `ExportDrawer`: with `cancelledExcluded > 0` and `cancelledParcels`
    populated, the count line renders and the disclosure expands to the parcel
    rows; with `0`, neither renders.

## Non-goals (YAGNI)

- No change to the billing model, the exclusion guards, or the placeholder
  join. Cancelled-after-ship parcels remain excluded from billing.
- No policy decision on whether shipped-then-cancelled parcels *should*
  eventually bill — that is a separate business question (does Shopee reverse
  the parcel, or does the warehouse eat the cost?). This work only makes the
  current exclusion visible.
- Returned and Duplicate residuals stay count-only; only cancelled gets the
  list (per the operator's need to reconcile "why isn't this order billed?").
- No new database column, migration, or `cancelled_at` timestamp.

## Files touched

| Repo | File | Change |
|------|------|--------|
| backend | `src/api/exports/invoices.rs` | `CancelledParcel` struct; `Selection` + `PreviewResponse` gain `cancelled_parcels`; 2 count queries → row-selects; count derived from len |
| backend | `src/api/exports/invoices.rs` (tests) | cancelled-parcels list asserts, both modes |
| frontend | `app/types.ts` | `orderStatus` on `PackingItem` |
| frontend | `app/components/AlertDetailPanel.tsx` | cancellation timeline node |
| frontend | `app/hooks/useWarehouseInvoice.ts` | `cancelledParcels` on preview type |
| frontend | `app/components/ExportDrawer.tsx` | cancelled count line + disclosure list |
| frontend | test file(s) | timeline node + export list assertions |

## Acceptance criteria

1. Export preview JSON includes `cancelledParcels` (array of
   `{orderNumber, trackingNumber, orderStatus}`) in both selection and window
   modes; `cancelledExcluded` equals its length.
2. Viewing `260724GSJF4N96` in the alert detail shows a red "Cancelled on
   platform / Excluded from billing" node after Shipped; a billable parcel does
   not.
3. Running an invoice-export preview whose window covers a cancelled-after-ship
   parcel shows the cancelled count and an expandable list naming that parcel.
4. Backend `cargo test` and frontend `npm test` pass; changed files lint-clean;
   `tsc --noEmit` introduces no new errors in touched files.
