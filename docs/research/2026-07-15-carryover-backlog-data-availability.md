# Research: carryover date/stage data availability + /backlog page reuse

**Date:** 2026-07-15 · **Wayfinder:** map #62, ticket #63
**Branches inspected:** `fix/analytics-post-shipped` in `backend/.worktrees/analytics-debug` and `frontend/.worktrees/analytics-debug`
**Method:** 3 parallel read-only investigators + 1 adversarial verifier (spot-checked ~12 citations; the verifier overturned the investigators' headline conclusion — see §1.3).

## 1. Carryover: what the payload has, and what "oldest date" / "previous stage" would take

### 1.1 What exists today

- `CarryoverBreakdown` (`backend src/api/dashboard.rs:135-148`, serde camelCase) exposes `totalOrders`, `totalParcels`, `byStatus {packed, shipped, returned, qcHold, qcPassed}`, `byPlatform`, `completedVideos`, `videoEvents`, `failedVideos`, `dates[]`. It rides the 15s-cached `/dashboard/summary` response (`Summary.carryover`, optional field — `types.ts:69`).
- **No machine-readable oldest/min submitted date exists** — for the whole cohort or per stage. The only date-ish data is `dates[]`: whole-cohort groups keyed by Bangkok-local created-date, label `'DD Mon'` (display string, no year), count = DISTINCT **orders** (not parcels), ordered DESC (`dashboard.rs:531-546`).
- **No predecessor-stage data exists anywhere in the payload.** `byStatus` records what carryover parcels *did this period* (stage events), not what stage they were in before the window. Any "was <stage>" copy at band level is static per-card copy from pipeline order.
- Pill counts are pure client-side derivations of the breakdown (`cohortCount()`, `CarryoverDrilldown.tsx:7-15`); stage-card → cohort mapping is the static `STAGE_COHORT` table (`PipelineSection.tsx:13-15`).
- The drill-down table is a separate lazy fetch: `useCarryoverParcels` → `GET /packing-lists/list?itemType=carryover[-qc|-packed|-shipped|-video]`, default sort `createdAt asc` (oldest first).

### 1.2 Per-row fields (matters for the slim-table direction)

`PackingItem` list rows (`types.ts:72-99`) carry `createdAt` (submitted), `checkedAt` (QC), `packedAt`, `updatedAt`, `latestVideoStatus` — **but no `shippedAt` and no per-row video-completed timestamp**. Per-row "previous stage + its time" is derivable client-side for:

| Cohort | Updated status (event) | Previous stage | Previous-stage time |
|---|---|---|---|
| qc | QC Passed/Hold (`checkedAt`) | Submitted | `createdAt` ✅ |
| packed | Packed (`packedAt`) | QC Passed (or Submitted if never checked) | `checkedAt` / `createdAt` ✅ |
| shipped | Shipped (**no `shippedAt`** — `updatedAt` proxy) | Packed | `packedAt` ✅ |
| video | Video completed (**no per-row timestamp**) | Packed | `packedAt` ✅ |

**Gap:** shipped-event time and video-completed time are not in the list payload. Options: `updatedAt` proxy (approximate) or add fields to the list response (backend `packing_list.rs` row struct).

### 1.3 Band-level "oldest submitted date" — verifier's corrected verdict

Investigators concluded "needs backend work" (echoing spec 2026-07-11 §2.8's declined per-stage dates). The adversarial verifier **refuted** that as stated:

- **A sound client-side path exists today**: one dedicated eager request per cohort — `GET /packing-lists/list?from&to&itemType=carryover-<stage>&sortBy=createdAt&sortDir=asc&limit=1&offset=0` → `items[0].createdAt` (full RFC3339). Verified: per-cohort itemType arms (`packing_list.rs:109-125`), `createdAt` sort whitelist (`:292`), `asc` (`:296`), limit/offset (`:64-67`). The old unsoundness argument (user re-sort/filter breaking first-row-is-oldest) only applies to reusing `useCarryoverParcels`' shared mutable state, not a pinned-params `limit=1` fetch.
- **But the backend field remains materially better**: (1) client path costs 4–5 extra HTTP round trips per window change, each running three uncached queries server-side (`count_sql`, `avail_sql`, page query — `packing_list.rs:300-378`) vs zero extra queries riding the cached summary; (2) parity caveat — list `carryover-qc` is `checked_at`-in-window (`packing_list.rs:110`), a **superset** of the pill's qc cohort (`dashboard.rs:417-418` additionally requires `updated_product_lists IS NOT NULL` / `all_items_cleared`), so the derived oldest can belong to a parcel the pill doesn't count.
- **Smallest backend change** (if wanted): append `MIN(created_at) FILTER (WHERE <stage predicate>)` columns to the existing `co_main_sql` single-scan aggregate (`dashboard.rs:410-422`) + `Option<DateTime<Utc>>` fields on `CarryoverBreakdown`. Zero extra queries. Precedent in the same file: `BacklogSummary.oldest_created_at` (`:47-53`, `:67`, `:81`). Backward-compatible both ways (deployed FE ignores unknown keys; newer FE uses the `videoEvents ?? 0` stale-backend pattern). For pill-exact parity, mirror the `qc_hold`/`qc_passed` predicates rather than bare `w_checked`.

> **Post-research note:** the user's mockup reaction (same day) pivoted the design from a band-level "oldest" to **per-row previous-stage/time in a slimmer drill-down table** (§1.2 covers that path). Band-level oldest may no longer be needed; this section stands as the answer if it returns.

## 2. /backlog page: reuse verdict

**Yes — hooks and band move unchanged.**

- `useBacklogSummary` / `useBacklogParcels` are fully self-contained: own start-of-today Bangkok cutoff via `todayStr()`/`dateRange()`, direct `GET /dashboard/backlog` + `GET /packing-lists/list`, no dashboard-state, context, date-filter, or WebSocket coupling (`useBacklog.ts:3-7`, `:18-20`, `:49-69`, `:77-160`).
- `BacklogSection` props: `{ operators, onOpenParcel? }` only. Existing tests render it standalone with `operators={[]}` and mocked fetch — proof no provider needed (`BacklogSection.test.tsx:45`).
- A standalone page must supply:
  - **operators** — `useOperators()` already exists (`useOperators.ts:23-36`, same `/operator-lists` endpoint the dashboard uses).
  - **a modal host** — the parcel modal never renders inside `BacklogSection` (its embedded `PackingTable` hardcodes `selectedDetail={null}`); on the dashboard the modal renders from the *main* table via `useDashboard.openModal`. Lightest option: `trackingNumber` state + `AlertDetailPanel`, which self-fetches (`AlertDetailPanel.tsx:228-229`).
- `PackingTable`'s `fromDate` prop is only the row-level Carryover-badge boundary, not a data filter (`PackingTable.tsx:163`, `:195`).
- **No live updates exist for backlog data** — fetch-on-mount + manual refetch after cancel POST; zero `backlog` matches in `useDashboard.ts`/`usePackingSocket.ts`. A long-lived `/backlog` tab shows stale counts unless polling/WS refetch is added.
- **Page shell convention:** `<Sidebar /> + flex shell` per `app/issues/page.tsx` / `app/insights/page.tsx`; `insights/page.tsx:8` notes extracting a `DashboardShell` once a third page needs it — `/backlog` would be that third page. (`app/packing/page.tsx` is now just a redirect — don't copy it.)
- **Caveat:** the backlog cutoff depends on `dateWindow.ts`'s `todayStr()`, currently carrying the uncommitted `DIAGNOSTIC(bkk)` timezone pin ("revert before merging; never commit"). The page inherits whatever that resolves to.

## 3. Open questions surfaced (feed grilling tickets #65/#66)

1. Slim table: handle the missing per-row `shippedAt` / video-completed time — `updatedAt` proxy or backend list-payload addition?
2. Modal strategy on `/backlog`: lightweight `AlertDetailPanel` host vs the dashboard's detail+videos prefetch pattern; is the panel's alert-resolve UI appropriate there?
3. Extract `DashboardShell` (third consumer) or copy the inline shell again?
4. Staleness: is fetch-on-mount acceptable for `/backlog`, or add polling/WS refetch?
5. Final timezone behaviour for `todayStr()` once the DIAGNOSTIC pin is reverted — backlog cutoff depends on it.
6. If band-level oldest returns: flat vs nested new fields; combined `w_checked` vs pill-exact QC predicates; FE worktree pairs with the ADR-0003 `{co_any_stage}` backend, not dev-1.2's `updated_at` window — confirm ship pairing.
