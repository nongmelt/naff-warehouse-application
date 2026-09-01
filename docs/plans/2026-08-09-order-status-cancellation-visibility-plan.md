# Order-status Cancellation Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make platform-side cancellations (`order_status='Cancelled'`) visible where they currently hide — the alert-detail timeline and the invoice-export footer.

**Architecture:** Backend already returns `orderStatus` on the packing detail and already excludes cancelled parcels from export. Two of three tasks are pure frontend surfacing; one small backend task turns the export preview's cancelled **count** into a cancelled **parcel list** so the export footer can name them.

**Tech Stack:** Rust / Axum / SQLx (backend), Next.js 16 / React 19 / TypeScript / Vitest (frontend).

## Global Constraints

- **Branch:** all code lands on `feat/invoice-unbillable-alert` in each submodule.
  Backend worktree: `backend/.worktrees/invoice-unbillable-alert`. Frontend
  worktree: `frontend/.worktrees/invoice-unbillable-alert`. Commit in the
  submodule, not the root repo.
- **Only surface `order_status === 'Cancelled'`.** Raw platform statuses (e.g.
  Thai `ที่ต้องจัดส่ง`) are never rendered. No timeline node, no export row for them.
- **No billing-model change.** Exclusion guards, `PLACEHOLDER_MATCH_PREDICATE`,
  and `select_parcels`' `EXCLUSIONS` constant stay exactly as they are. This work
  is display-only + one query projection change.
- **No DB migration**, no new column, no `cancelled_at`. `updated_at` is the
  approximate cancellation time and must be labelled as approximate.
- **API JSON is camelCase** (e.g. `cancelledExcluded`, `layoutMismatch`) — new
  structs/fields follow suit via `#[serde(rename_all = "camelCase")]`.
- Backend tests need a live Postgres (the dev Docker `warehouse-postgres`), run
  from the backend worktree. Frontend tests run offline via `npm test`.
- Commit message trailer on every commit:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

## File Structure

| Repo | File | Responsibility |
|------|------|----------------|
| backend | `src/api/exports/invoices.rs` | `CancelledParcel` struct; `Selection` + `PreviewResponse` carry `cancelled_parcels`; two cancelled `COUNT(*)` queries become row-selects; count derived from list length |
| backend | `tests/warehouse_invoice.rs` | assert `cancelledParcels` in preview, window + numbers modes |
| frontend | `app/types.ts` | `orderStatus` on `PackingItem` |
| frontend | `app/components/AlertDetailPanel.tsx` | cancellation timeline node |
| frontend | `app/components/AlertDetailPanel.cancellation.test.tsx` (new) | node renders iff `orderStatus==='Cancelled'` |
| frontend | `app/hooks/useWarehouseInvoice.ts` | `cancelledParcels` on preview type |
| frontend | `app/components/ExportDrawer.tsx` | cancelled count line + disclosure list |
| frontend | `app/components/ExportDrawer.invoices.test.tsx` | cancelled list renders + expands |

---

## Task 1: Backend — export preview returns the cancelled parcels

**Files:**
- Modify: `backend/.worktrees/invoice-unbillable-alert/src/api/exports/invoices.rs`
- Test: `backend/.worktrees/invoice-unbillable-alert/tests/warehouse_invoice.rs`

**Interfaces:**
- Produces (JSON, consumed by Task 3): `GET /exports/invoices/preview` response gains
  `cancelledParcels: Array<{ orderNumber: string; trackingNumber: string; orderStatus: string }>`,
  and `cancelledExcluded` equals `cancelledParcels.length`.

- [ ] **Step 1: Extend the existing window-mode test to assert the list**

In `tests/warehouse_invoice.rs`, inside `preview_counts_missing_and_cancelled`
(after the existing `assert_eq!(body["cancelledExcluded"], 1);` line), add:

```rust
    let cancelled: Vec<&str> = body["cancelledParcels"]
        .as_array()
        .unwrap()
        .iter()
        .map(|p| p["trackingNumber"].as_str().unwrap())
        .collect();
    assert_eq!(cancelled, vec![t_cancelled.as_str()]);
    assert_eq!(
        body["cancelledParcels"][0]["orderStatus"], "Cancelled",
        "list carries the order_status: {body}"
    );
```

- [ ] **Step 2: Run it — expect failure (field absent)**

Run (from the backend worktree):
`cargo test --test warehouse_invoice preview_counts_missing_and_cancelled -- --nocapture`
Expected: FAIL — `body["cancelledParcels"]` is `Null`, `.as_array().unwrap()` panics.

- [ ] **Step 3: Add the `CancelledParcel` struct**

In `src/api/exports/invoices.rs`, near the `Selection` struct, add:

```rust
#[derive(serde::Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
pub struct CancelledParcel {
    pub order_number: String,
    pub tracking_number: String,
    /// Always 'Cancelled' today (the query filters on it), carried so the
    /// UI row is self-describing and the shape survives if the excluded set
    /// ever widens.
    pub order_status: String,
}
```

- [ ] **Step 4: Carry the list on `Selection` and `PreviewResponse`**

Add `cancelled_parcels: Vec<CancelledParcel>` to `struct Selection` and
`pub cancelled_parcels: Vec<CancelledParcel>` to `pub struct PreviewResponse`
(the latter directly under `pub cancelled_excluded: i64,`).

- [ ] **Step 5: Turn the two cancelled COUNT queries into row-selects**

In `select_parcels`, **numbers mode**, replace the `let cancelled: i64 = …COUNT(*)…` block with:

```rust
        let cancelled_parcels: Vec<CancelledParcel> = sqlx::query_as(
            "SELECT order_number, tracking_number, order_status FROM packing_lists
             WHERE tracking_number = ANY($1) AND order_status = 'Cancelled'
             ORDER BY tracking_number",
        )
        .bind(&filter.numbers)
        .fetch_all(pool)
        .await?;
```

and set the returned struct field to
`cancelled_excluded: cancelled_parcels.len() as i64,` plus `cancelled_parcels,`.

In **window mode**, replace the `let cancelled: i64 = …` block (keep the same
`{col} >= $1 AND {col} <= $2 … {invoiced_frag}{shipping_frag}` predicate and the
same conditional `.bind(s)` for shipping):

```rust
    let cancelled_sql = format!(
        "SELECT order_number, tracking_number, order_status FROM packing_lists
         WHERE {col} >= $1 AND {col} <= $2 AND order_status = 'Cancelled'{invoiced_frag}{shipping_frag}
         ORDER BY tracking_number"
    );
    let mut qc = sqlx::query_as::<_, CancelledParcel>(&cancelled_sql)
        .bind(window.from)
        .bind(window.to);
    if let Some(ref s) = filter.shipping {
        qc = qc.bind(s);
    }
    let cancelled_parcels: Vec<CancelledParcel> = qc.fetch_all(pool).await?;
```

Update the final `Ok(Selection { … })` to
`cancelled_excluded: cancelled_parcels.len() as i64, cancelled_parcels, returned_excluded: returned, …`.

- [ ] **Step 6: Pass the list through `preview`**

In `preview`, the final `Ok(Json(PreviewResponse { … }))` gains
`cancelled_parcels: selection.cancelled_parcels,`. (`generate` does not use it —
leave `generate` untouched; if `generate` also constructs a `Selection`, it simply
ignores the new field.)

- [ ] **Step 7: Add a numbers-mode test**

Append to `tests/warehouse_invoice.rs`:

```rust
#[tokio::test]
async fn preview_lists_cancelled_parcels_numbers_mode() {
    let (base, pool) = spawn_app().await;
    let ts = nanos();
    let t_ok = format!("WHI-NOK-{ts}");
    let t_canc = format!("WHI-NCANC-{ts}");
    seed_parcel(&pool, &t_ok, None, None, None).await;
    seed_parcel(&pool, &t_canc, None, None, Some("Cancelled")).await;

    let resp = reqwest::Client::new()
        .get(format!("{base}/exports/invoices/preview"))
        .query(&[("numbers", format!("{t_ok},{t_canc}"))])
        .send().await.unwrap();
    let body: serde_json::Value = resp.json().await.unwrap();

    assert_eq!(body["cancelledExcluded"], 1);
    let list = body["cancelledParcels"].as_array().unwrap();
    assert_eq!(list.len(), 1, "only the cancelled parcel: {body}");
    assert_eq!(list[0]["trackingNumber"], t_canc);
    assert_eq!(list[0]["orderStatus"], "Cancelled");
}
```

- [ ] **Step 8: Run both tests — expect pass**

Run: `cargo test --test warehouse_invoice preview_ -- --nocapture`
Expected: PASS for `preview_counts_missing_and_cancelled` and
`preview_lists_cancelled_parcels_numbers_mode`.

- [ ] **Step 9: Full build + suite**

Run: `cargo build && cargo test --test warehouse_invoice`
Expected: compiles clean, suite green.

- [ ] **Step 10: Commit**

```bash
cd backend/.worktrees/invoice-unbillable-alert
git add src/api/exports/invoices.rs tests/warehouse_invoice.rs
git commit -m "feat(invoices): export preview returns cancelled-parcel list

Turn the cancelled-excluded count into a list of {orderNumber, trackingNumber,
orderStatus} in both selection and window modes so the export footer can name
the parcels a platform cancelled after shipment. Count is derived from the
list length. No billing-model change.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Frontend — cancellation event in the alert timeline

**Files:**
- Modify: `frontend/.worktrees/invoice-unbillable-alert/app/types.ts`
- Modify: `frontend/.worktrees/invoice-unbillable-alert/app/components/AlertDetailPanel.tsx`
- Test: `frontend/.worktrees/invoice-unbillable-alert/app/components/AlertDetailPanel.cancellation.test.tsx` (new)

**Interfaces:**
- Consumes: backend `GET /packing-lists/{tracking}` already returns
  `orderStatus: string | null` (verified live).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Add `orderStatus` to `PackingItem`**

In `app/types.ts`, inside `interface PackingItem`, add after `packingStatus`:

```ts
  orderStatus: string | null;
```

- [ ] **Step 2: Write the failing test (new file)**

Create `app/components/AlertDetailPanel.cancellation.test.tsx`:

```tsx
// @vitest-environment jsdom
//
// The alert-detail timeline shows a "Cancelled on platform" node ONLY when
// the parcel's orderStatus is 'Cancelled' (platform cancel after ship). Raw
// platform statuses and normal shipped parcels render no such node.

import { act } from "react";
import { createRoot, Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AlertDetailPanel } from "./AlertDetailPanel";

(globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;

let container: HTMLDivElement;
let root: Root;

function stubFetch(orderStatus: string | null) {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.endsWith("/videos")) return { ok: true, json: async () => [] };
    if (url.endsWith("/issues")) return { ok: true, json: async () => [] };
    // the detail call: /packing-lists/{tracking}
    return {
      ok: true,
      json: async () => ({
        packingId: 1, trackingNumber: "TRK-C", orderNumber: "ORD-C",
        platform: "Shopee", packingStatus: "Shipped", orderStatus,
        createdAt: "2026-07-24T07:45:00Z", updatedAt: "2026-07-25T07:11:00Z",
        shippedAt: "2026-07-25T03:30:00Z", returnedAt: null, invoicedAt: null,
        productLists: null, updatedProductLists: null,
      }),
    };
  }));
}

beforeEach(() => {
  vi.stubGlobal("matchMedia", vi.fn(() => ({
    matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn(),
  })));
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.unstubAllGlobals();
});

async function render() {
  await act(async () => {
    root.render(
      <AlertDetailPanel trackingNumber="TRK-C" alertType="noVideo" onClose={() => {}} />,
    );
  });
  // let the mount fetches settle
  await act(async () => { await Promise.resolve(); });
}

describe("AlertDetailPanel cancellation node", () => {
  it("renders the node when orderStatus is Cancelled", async () => {
    stubFetch("Cancelled");
    await render();
    expect(container.textContent).toContain("Cancelled on platform");
    expect(container.textContent).toContain("Excluded from billing");
  });

  it("renders no node for a normal shipped parcel", async () => {
    stubFetch(null);
    await render();
    expect(container.textContent).not.toContain("Cancelled on platform");
  });
});
```

- [ ] **Step 3: Run it — expect failure**

Run (from the frontend worktree):
`npx vitest run app/components/AlertDetailPanel.cancellation.test.tsx`
Expected: FAIL — first test can't find "Cancelled on platform".

- [ ] **Step 4: Add the timeline node**

In `app/components/AlertDetailPanel.tsx`, in `renderTimeline()`, immediately
after the **Shipped milestone `<li>` closes** and **before** the
`{isReturned && (` Returned block, insert:

```tsx
          {/* Platform cancellation — order_status flipped after shipment.
              Only the 'Cancelled' override is shown; raw platform statuses
              (sometimes Thai) are intentionally not surfaced. */}
          {detail?.orderStatus === "Cancelled" && (
            <li>
              <div className="relative flex gap-3 pb-5 pl-8">
                <div className="absolute -left-[7px] top-0.5 z-10 flex h-3 w-3 shrink-0 items-center justify-center rounded-full ring-2 ring-card bg-red-500" />
                <div className="mt-0.5 shrink-0 text-red-500">
                  <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
                    <path d="M18 6 6 18M6 6l12 12" />
                  </svg>
                </div>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium tracking-[-0.02em] text-foreground">Cancelled on platform</p>
                  <p className="mt-0.5 text-xs font-medium text-red-600 dark:text-red-400">Excluded from billing</p>
                  {detail?.updatedAt && (
                    <p className="mt-0.5 text-xs text-muted-foreground">~ {fmtTime(detail.updatedAt)}</p>
                  )}
                </div>
              </div>
            </li>
          )}
```

- [ ] **Step 5: Run the test — expect pass**

Run: `npx vitest run app/components/AlertDetailPanel.cancellation.test.tsx`
Expected: PASS (both cases).

- [ ] **Step 6: Typecheck + lint the touched files**

Run: `npx tsc --noEmit 2>&1 | grep -E "AlertDetailPanel|types.ts" || echo clean`
then `npx eslint app/components/AlertDetailPanel.tsx app/types.ts`
Expected: no new errors in touched files; ESLint clean.

- [ ] **Step 7: Commit**

```bash
cd frontend/.worktrees/invoice-unbillable-alert
git add app/types.ts app/components/AlertDetailPanel.tsx app/components/AlertDetailPanel.cancellation.test.tsx
git commit -m "feat(dashboard): show platform-cancellation node in alert timeline

A parcel cancelled on the platform after shipment (order_status='Cancelled')
now renders a red 'Cancelled on platform / Excluded from billing' node after
Shipped, explaining why it never bills and never appears on Unbillable. Only
the 'Cancelled' override is surfaced; raw platform statuses are not.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Frontend — expandable cancelled list in the export footer

**Files:**
- Modify: `frontend/.worktrees/invoice-unbillable-alert/app/hooks/useWarehouseInvoice.ts`
- Modify: `frontend/.worktrees/invoice-unbillable-alert/app/components/ExportDrawer.tsx`
- Test: `frontend/.worktrees/invoice-unbillable-alert/app/components/ExportDrawer.invoices.test.tsx`

**Interfaces:**
- Consumes: Task 1's `cancelledParcels` array on the preview JSON.

- [ ] **Step 1: Extend the preview type in the hook**

In `app/hooks/useWarehouseInvoice.ts`, find the preview interface carrying
`cancelledExcluded: number;` and add:

```ts
  cancelledParcels: { orderNumber: string; trackingNumber: string; orderStatus: string }[];
```

If the hook maps/normalizes the raw response, default a missing field to `[]`
(e.g. `cancelledParcels: data.cancelledParcels ?? []`).

- [ ] **Step 2: Write the failing test**

In `app/components/ExportDrawer.invoices.test.tsx`, add a test that renders the
drawer with a preview containing `cancelledExcluded: 2` and a two-element
`cancelledParcels`, mirroring the file's existing preview-mock setup (reuse its
`renderDrawer`/fetch-stub helpers). Assertions:

```tsx
  it("lists cancelled parcels under an expandable count", async () => {
    // preview mock returns cancelledExcluded: 2 and two cancelledParcels
    await renderInvoicesDrawerWithPreview({
      platforms: [], missing: [], fingerprint: "", returnedExcluded: 0,
      cancelledExcluded: 2,
      cancelledParcels: [
        { orderNumber: "260724GSJF4N96", trackingNumber: "TH265908264825Z", orderStatus: "Cancelled" },
        { orderNumber: "260727QURACHBE", trackingNumber: "3191...", orderStatus: "Cancelled" },
      ],
    });
    // count line visible
    expect(container.textContent).toContain("2 cancelled parcels excluded");
    // expand
    const toggle = [...container.querySelectorAll("button")]
      .find((b) => b.textContent?.includes("cancelled parcels excluded"));
    await act(async () => toggle!.click());
    expect(container.textContent).toContain("260724GSJF4N96");
    expect(container.textContent).toContain("260727QURACHBE");
  });
```

> Note to implementer: match the exact preview-mock helper name and signature
> used by the existing tests in this file (it already stubs `fetch` for
> `/exports/invoices/preview`). If no reusable helper exists, follow the
> `ExportDrawer.shell.test.tsx` createRoot/act pattern and stub `fetch` to
> return the object above for the preview URL.

- [ ] **Step 3: Run it — expect failure**

Run: `npx vitest run app/components/ExportDrawer.invoices.test.tsx -t "lists cancelled parcels"`
Expected: FAIL — count line / list not rendered.

- [ ] **Step 4: Render the count line + disclosure**

In `app/components/ExportDrawer.tsx`, add near the other footer state a toggle:

```tsx
  const [showCancelled, setShowCancelled] = useState(false);
```

In the `type === "invoices"` footer block, directly after the existing
`returned-note` paragraph, add:

```tsx
              {preview && preview.cancelledExcluded > 0 && (
                <div className="mb-2">
                  <button
                    type="button"
                    data-testid="cancelled-note"
                    onClick={() => setShowCancelled((v) => !v)}
                    className="flex items-center gap-1 text-[11px] font-medium text-muted-foreground hover:text-foreground"
                  >
                    <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3"
                      className={`transition-transform ${showCancelled ? "rotate-90" : ""}`}>
                      <path d="m9 6 6 6-6 6" />
                    </svg>
                    {preview.cancelledExcluded} cancelled parcel
                    {preview.cancelledExcluded === 1 ? "" : "s"} excluded — platform cancelled after ship.
                  </button>
                  {showCancelled && (
                    <ul className="mt-1 space-y-0.5 pl-4">
                      {preview.cancelledParcels.map((p) => (
                        <li key={p.trackingNumber} className="flex items-center gap-2 text-[10.5px] text-muted-foreground">
                          <span className="font-mono">{p.orderNumber}</span>
                          <span className="rounded bg-red-100 px-1 py-0.5 text-[9px] font-bold text-red-700 dark:bg-red-900/40 dark:text-red-300">
                            {p.orderStatus}
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}
```

Confirm `useState` is imported in `ExportDrawer.tsx` (it is, if the component
already uses hooks; add it to the React import otherwise).

- [ ] **Step 5: Run the test — expect pass**

Run: `npx vitest run app/components/ExportDrawer.invoices.test.tsx -t "lists cancelled parcels"`
Expected: PASS.

- [ ] **Step 6: Full frontend suite + typecheck + lint**

Run: `npm test` then
`npx tsc --noEmit 2>&1 | grep -E "ExportDrawer|useWarehouseInvoice" || echo clean`
then `npx eslint app/components/ExportDrawer.tsx app/hooks/useWarehouseInvoice.ts`
Expected: suite green; no new type errors in touched files; ESLint clean.

- [ ] **Step 7: Commit**

```bash
cd frontend/.worktrees/invoice-unbillable-alert
git add app/hooks/useWarehouseInvoice.ts app/components/ExportDrawer.tsx app/components/ExportDrawer.invoices.test.tsx
git commit -m "feat(export): expandable cancelled-parcel list in invoice footer

The invoice-export footer now shows how many parcels were dropped because the
platform cancelled them after shipment, expandable to the specific order
numbers. Consumes the preview endpoint's new cancelledParcels list.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Manual verification (after all tasks, against the dev clone)

1. Backend running on :8080 against `warehouse_snapshot_test`, FE dev on :3000.
2. `curl "http://localhost:8080/exports/invoices/preview?numbers=260724GSJF4N96"`
   → JSON has `cancelledExcluded: 1` and `cancelledParcels[0].orderNumber == "260724GSJF4N96"`.
3. Dashboard → open the alert-detail for tracking `TH265908264825Z` → red
   "Cancelled on platform / Excluded from billing" node after Shipped.
4. Export drawer (invoices) over a window covering 24–25 Jul → footer shows the
   cancelled count; clicking it expands to name `260724GSJF4N96`.

---

## Self-review notes

- **Spec coverage:** Surface 1 → Task 1; Surface 2 → Task 2; Surface 3 → Task 3;
  testing → each task's TDD steps + manual verification. Acceptance criteria
  1→Task 1, 2→Task 2, 3→Task 3, 4→each task's build/lint steps.
- **Type consistency:** `CancelledParcel { order_number, tracking_number,
  order_status }` (backend) ⇄ `cancelledParcels: { orderNumber, trackingNumber,
  orderStatus }[]` (frontend) via camelCase serde. `orderStatus` added to
  `PackingItem` (Task 2) and consumed only there.
- **No billing change:** every task touches display/projection only; the
  `EXCLUSIONS` predicate and guards are untouched.
