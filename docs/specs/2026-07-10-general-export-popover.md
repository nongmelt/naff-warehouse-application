# Spec: General Export popover (Orders + Invoices)

**Status:** build-ready
**Wayfinder map:** [#52](https://github.com/nongmelt/naff-warehouse-application/issues/52) — decisions resolved in [#53](https://github.com/nongmelt/naff-warehouse-application/issues/53) (order-date research), [#54](https://github.com/nongmelt/naff-warehouse-application/issues/54) (UI prototype), [#55](https://github.com/nongmelt/naff-warehouse-application/issues/55) (API contract)
**Base:** builds atop `feat/warehouse-invoice` (backend PR [#53](https://github.com/nongmelt/naff-warehouse-backend/pull/53), frontend PR [#50](https://github.com/nongmelt/naff-warehouse-frontend/pull/50)) — the popover and `_raw` export machinery land there.
**Research:** [docs/research/2026-07-10-orders-export-order-dates.md](../research/2026-07-10-orders-export-order-dates.md)
**UI reference:** frontend branch [`prototype/export-popover`](https://github.com/nongmelt/naff-warehouse-frontend/tree/prototype/export-popover), Variant C (`prototype-shots/variant-c-*.png`) — throwaway; delete after implementation starts.

## 1. Summary

The dashboard-header "Export invoices" popover becomes a general **Export** popover with a type selector — v1 types: **Invoices** (existing behavior, restyled) and **Orders** (re-download of imported platform order files with latest stored changes). Popover-only change: `/invoices` and `/imports` pages untouched.

## 2. Orders export semantics

- **Selection anchor:** the platform order-date column, normalized into `import_rows.ordered_at` (generated TEXT column, `YYYY-MM-DD HH:MM:SS+07:00` Bangkok-explicit ISO). Source headers: Shopee `วันที่ทำการสั่งซื้อ`, Lazada `createTime`, Tiktok `Created Time`.
- **Content:** stored DB rows as-is (last-import-wins upsert + any manual row edits), rebuilt from `raw_data->'_raw'` into the original import layout. **Cancelled orders included** — no status filter anywhere.
- **Output:** one xlsx per platform. Cross-batch windows rebuild with the **newest batch's** `header_layout`/`header_mapping`; the Tiktok description-row shim (blank row 2) is re-emitted as in the invoice export.
- **No audit trail.** `invoice_exports` stays invoice-only; orders exports write nothing.
- **Cast guard (required):** `ordered_at` is TEXT; the selection query must regex-guard rows before `::timestamptz` — one unparseable value must not fail the whole query. Guarded-out rows are counted as `excluded` and surfaced in the preview, never silently dropped.
- Never window on `paid_at` (sparse).

## 3. API contract (backend)

### 3.1 Namespace migration (pre-merge, no alias)

`/issue-invoice/preview|generate` → **`/exports/invoices/preview|generate`** on the `feat/warehouse-invoice` stack before merge. `useWarehouseInvoice` fetch paths update in the same stack. No legacy routes ship.

### 3.2 `GET /exports/orders/preview?from=<RFC3339>&to=<RFC3339>`

UTC instants, identical param shape to the invoice preview (frontend reuses `resolveWindow()`; inclusive `to`). Response (camelCase):

```json
{
  "platforms": [
    { "platform": "Shopee", "orders": 812, "rows": 1492, "batches": 2, "layoutMismatch": false }
  ],
  "excluded": 3
}
```

- `orders` = `COUNT(DISTINCT order_number)` within window per platform (same semantics as the invoice preview).
- `rows` = file rows in window; `batches` / `layoutMismatch` mirror the invoice preview and power a multi-batch warning.
- `excluded` (top-level) = rows failing the cast guard across the window.

### 3.3 `POST /exports/orders/generate`

Body `{ "platform": "Shopee", "from": "<RFC3339>", "to": "<RFC3339>" }` — no `exportedBy`.
Returns the xlsx byte stream: `Content-Type` spreadsheetml, `Content-Disposition: attachment; filename="{platform}_orders_{range}.xlsx"` with Bangkok-local days (`shopee_orders_2026-07-10.xlsx`; ranges `..._2026-07-01_to_2026-07-10.xlsx`). No `X-Export-Id`. `400` when the window holds zero rows for that platform.

### 3.4 Module layout

```
src/api/exports/
  mod.rs       // mounts /exports/* routes
  invoices.rs  // moved from warehouse_invoice.rs
  orders.rs    // new: ordered_at-window selection
  rebuild.rs   // shared: newest-batch layout fetch → flat rows → generate_xlsx → transform_for (incl. Tiktok shim)
```

Selection queries stay per-handler (invoices: parcel-status join; orders: `ordered_at` window). The rebuild path exists once.

## 4. UI spec (frontend) — prototype Variant C

- **Trigger:** header button renamed **"Export"** (same brand styling).
- **Type selector:** inline dropdown beside the bold panel title — reads `Export [Orders ▾]`. Extensible to future types without layout change.
- **Both types render as a dense table** (text-xs, tabular-nums, right-aligned numeric columns):
  - Orders columns: `Platform / Orders / Rows / ⬇`
  - Invoices columns: `Platform / Parcels / Orders / ⬇`
  - Shipped/Packed condition chips stay above the table — **invoices-only**.
  - This consciously reworks the invoice panel's visuals; its feature set is unchanged (missing-order-file warning, cancelled-excluded note, download-all, footer link).
- **Date control (both types):** `Today | Yesterday | Custom` chips; Custom reveals From/To pickers. Orders defaults to **Today**; Invoices defaults to Today + Shipped.
- **Carry semantics:** per-type memory while the popover is mounted; nothing persists across popover close.
- **States (Orders):**
  - Zero-row platform: stays in table, grayed (`opacity-40`), em-dash counts, disabled button.
  - Whole window empty: single muted table row "No imported orders in this window".
  - Preview error: table body replaced by destructive message + Retry.
  - Per-row download error: button tooltip (mirrors invoices).
- **Footer (Orders):** excluded note bottom-left ("N rows excluded — no order date"), total row count bottom-right, full-width "Download all N workbooks" button, "Import history →" link to `/imports`. Invoices footer keeps "Full invoice tools →".
- Download-all loops per-platform generates sequentially, as invoices does today.

## 5. Constraints

- Work stacks on `feat/warehouse-invoice` in both submodules; changes commit in the submodule repos.
- Backend: SQLx compile-time checking (`DATABASE_URL` or `SQLX_OFFLINE=true`).
- Frontend: Next.js 16 / React 19 / Tailwind v4, strict TS, no test suite — mirror `useWarehouseInvoice` hook idiom for the orders hook.
- Delete the `prototype/export-popover` frontend branch once implementation lands.

## 6. Out of scope

- Leaderboard / product checked-shipped export types (dropdown design must not preclude them).
- A general exports page or unified export history.
- Any audit trail for orders exports.
