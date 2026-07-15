# Dashboard De-clutter Implementation Plan — slim carryover drill-down + Backlog tab

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the carryover drill-down into a slim single-cohort band (no chip switcher, compact `Tracking · Overnight stage → Today's events · Age` table sized to its stage card) and move the backlog band behind a `Parcels | ⚠ Backlog <count>` view tab on the main packing-lists table card — no new route, dashboard header untouched.

**Architecture:** Frontend-only, one submodule. A new pure module `carryoverRows.ts` derives each parcel's overnight stage and in-window event chain from `PackingItem` timestamps (client-side; `~ updatedAt` proxy for shipped/video times, `time n/a` for overnight-Shipped). A new `CarryoverSlimTable` renders it and replaces `PackingTable` inside `CarryoverDrilldown`, which loses chips/def-copy/filters and gains anchor-based sizing (width fits content, min-width = clicked stage card, centered + clamped, notch at the pill). A new `TableViewTabs` row tops the main-table card (the dashboard h1 + subtitle stay); `useBacklogSummary` lifts from `BacklogSection` into `Dashboard` so the card's tab badge and the band share one fetch; `BacklogSection` renders always-expanded (outer chrome flattened) in the main `PackingTable`'s place inside the card — `PackingTable` gains a `frameless` opt-in for the same embedding — with its content verbatim, plus a green zero-state. No backend, migration, or wire changes.

**Tech Stack:** Next.js 16 / React 19 / Tailwind v4 / TypeScript strict / vitest 4 + jsdom (NO @testing-library).

## Global Constraints

- Spec: `docs/specs/2026-07-15-declutter-carryover-backlog-tab.md`. Copy strings in it are verbatim deliverables.
- Worktree: `frontend/.worktrees/analytics-debug`, branch `fix/analytics-post-shipped` (continue on tip **40814c6**). Run every command from that directory. Commit in the submodule worktree, never the monorepo root. Verify each commit's parent is the expected branch tip before review.
- Tests: `npx vitest run` (or `npm test`); `// @vitest-environment jsdom` per DOM test file; `react-dom/client` `createRoot` + `act` from `react`; `(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true`; `vi.mock("next/image")` wherever `PackingTable` renders; NO @testing-library. Suite is GREEN at base (99/99) — it must stay green; there are no pre-existing test failures to step around.
- Lint baseline: `npm run lint` reports **36 pre-existing issues** at base — new code must add ZERO new issues; do not fix the baseline. `npm run build` must pass (TS strict catches fixture drift vitest won't).
- Tailwind v4: every new colour style needs a `dark:` variant. Numbers via `toLocaleString()`.
- **`app/lib/dateWindow.ts` carries the uncommitted DIAGNOSTIC(bkk) patch** (Bangkok-pinned `todayStr()`/`dateRange()`, marked "never commit"). IMPORT and CALL these functions freely — but NEVER touch or commit that file: `git add` named files only, never `-A` / `-u` / `.`; check `git diff --cached` before every commit.
- Verbatim copy (FE):
  - Drill-down header meta: `{label} this period · submitted before today`; labels: all → `updated`, qc → `QC-checked`, packed → `Packed`, shipped → `Shipped`, video → `Video completed`
  - Slim table column heads: `Tracking` · `Overnight stage` · (blank th for the `→` column) · `Today's events` · `Age`
  - Missing overnight-Shipped time: `time n/a`; proxy prefix: `~`
  - Drill-down footer: existing `stageEventsNote()` output — UNCHANGED
  - Error row (both surfaces, unchanged): `Couldn't load parcels — retry`
  - Card tabs: `Parcels`; `⚠ Backlog` + `{count}`; loading `Backlog` + `—`; zero `✓ Backlog clear`
  - Backlog zero-state row: pill `✓ All clear` + `every parcel submitted before today has shipped · checked at page load`
  - Backlog band copy: UNCHANGED verbatim from `40814c6` (eyebrow `Open backlog`; meta `parcels not yet shipped · submitted before today · oldest {d MMM yyyy}`; chips; bulk bar; partial-cancel notice; dialog warning; footer `backlog since 3 Jul 2026 (Shipping go-live) · cancelled orders excluded · independent of the date filter above`)
- Deletions are deliverables too: `cohortDef`, chip switcher, `CohortFilters` usage in the drill-down (component itself stays — backlog uses it), the drill-down `PackingTable` instance, the backlog toggle button. The dashboard h1 + subtitle (`Dashboard.tsx:61-62`) are NOT touched — the earlier header-tab direction is superseded by the card tabs.
- Pushes and monorepo submodule-pointer bumps are USER-GATED — do not push.
- `graphify update .` is broken (`NameError: name '_os' is not defined`) — skip it.

---

## Phase 1 — Slim single-cohort carryover drill-down

### Task 1: `carryoverRows.ts` — pure overnight/chain derivation

**Files:**
- Create: `app/components/pipeline/carryoverRows.ts`
- Test: `app/components/pipeline/carryoverRows.test.ts` (new; pure module — no jsdom pragma, mirror `pipelineMath.test.ts`)

**Interfaces:**
- Consumes: `PackingItem` (`app/types.ts:72-99` — `createdAt`, `checkedAt`, `packedAt`, `updatedAt`, `packingStatus`, `allItemsCleared`, `latestVideoStatus`), `CarryoverCohort` (`types.ts:252`).
- Produces (Task 2 renders these; names are load-bearing):

```ts
export type StageBadge =
  | "submitted" | "qc-passed" | "qc-hold" | "packed" | "shipped" | "returned" | "video";

export interface StageEvent {
  badge: StageBadge;
  /** RFC3339, or null => render "time n/a" (overnight-Shipped only). */
  time: string | null;
  /** true => updatedAt proxy, render with a leading "~". */
  approx: boolean;
}

export function overnightStage(item: PackingItem, fromIso: string): StageEvent;
export function chainEvents(item: PackingItem, fromIso: string, toIso: string, cohort: CarryoverCohort): StageEvent[];
/** The chain badge the active cohort rings; null for "all" (no ring). */
export function ringTarget(cohort: CarryoverCohort): StageBadge[] | null;
```

**Context (spec §A.5-A.6):** overnight = furthest stage event strictly before `fromIso` (pipeline order guarantees `createdAt ≤ checkedAt ≤ packedAt`); creation-only rows show Submitted. Overnight-Shipped/Returned detection: `packingStatus ∈ {Shipped, Returned}` AND `updatedAt < fromIso` → `time: null`. Chain (fixed pipeline order qc → packed → shipped/returned → video, no sorting): `checkedAt`/`packedAt` window tests exact; Shipped/Returned via `packingStatus` + `updatedAt ≥ fromIso`, `approx: true`; Video via `latestVideoStatus === "Completed"` AND (`updatedAt ≥ fromIso` OR `cohort === "video"`), `approx: true`, `time: updatedAt`. QC badge splits on `allItemsCleared === true` (false/null → `qc-hold`). `ringTarget`: `qc → ["qc-passed","qc-hold"]`, `packed → ["packed"]`, `shipped → ["shipped","returned"]`, `video → ["video"]`, `all → null`.

- [ ] **Step 1: Write the failing tests**

`carryoverRows.test.ts` — build a `mk(overrides): PackingItem` helper from a base row (copy the null-heavy literal in `BacklogSection.cancel.test.tsx:13-23`). Window: `FROM = "2026-07-09T17:00:00.000Z"`, `TO = "2026-07-10T16:59:59.999Z"` (the Friday 10 Jul Bangkok window). Cases:

```ts
describe("overnightStage", () => {
  it("creation-only pre-window row is Submitted at createdAt", ...);        // createdAt 07-08, rest null → { badge: "submitted", time: createdAt, approx: false }
  it("pre-window checkedAt wins over createdAt and splits on allItemsCleared", ...); // checkedAt 07-09 07:00Z, cleared true → qc-passed; null → qc-hold
  it("pre-window packedAt wins over checkedAt", ...);                        // packedAt 07-09 → packed @ packedAt
  it("in-window events do not count: packedAt inside window falls back to the QC stage", ...);
  it("Shipped status with pre-window updatedAt is overnight-Shipped with null time", ...); // packingStatus "Shipped", updatedAt 07-09 02:00Z → { badge: "shipped", time: null }
});
describe("chainEvents", () => {
  it("full hop chain: QC + Packed in window, Shipped via status proxy", ...); // expect 3 events in order, shipped approx true @ updatedAt
  it("Shipped with pre-window updatedAt yields NO shipped chain event", ...);
  it("video event needs Completed status and in-window updatedAt", ...);     // approx true
  it('video cohort forces the video event even when updatedAt predates the window', ...); // cohort "video", updatedAt 07-09 → event present, approx true
  it("qc-hold when allItemsCleared is false or null", ...);
});
describe("ringTarget", () => {
  it("maps each cohort; all → null", ...);
});
```

Write every case as a real assertion (no todos).

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx vitest run carryoverRows`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

`carryoverRows.ts` (sketch — keep it dependency-free and side-effect-free):

```ts
import { CarryoverCohort, PackingItem } from "../../types";

const lt = (iso: string | null | undefined, bound: string) =>
  !!iso && new Date(iso).getTime() < new Date(bound).getTime();
const within = (iso: string | null | undefined, from: string, to: string) =>
  !!iso && new Date(iso).getTime() >= new Date(from).getTime()
        && new Date(iso).getTime() <= new Date(to).getTime();

export function overnightStage(item: PackingItem, fromIso: string): StageEvent {
  // Shipped/Returned before the window: only detectable via status + stale
  // updatedAt (no shippedAt on list rows — spec §A.5); real time unavailable.
  if ((item.packingStatus === "Shipped" || item.packingStatus === "Returned") && lt(item.updatedAt, fromIso)) {
    return { badge: item.packingStatus === "Returned" ? "returned" : "shipped", time: null, approx: false };
  }
  if (lt(item.packedAt, fromIso)) return { badge: "packed", time: item.packedAt!, approx: false };
  if (lt(item.checkedAt, fromIso)) {
    return { badge: item.allItemsCleared === true ? "qc-passed" : "qc-hold", time: item.checkedAt!, approx: false };
  }
  return { badge: "submitted", time: item.createdAt, approx: false };
}

export function chainEvents(item, fromIso, toIso, cohort): StageEvent[] {
  const events: StageEvent[] = [];
  if (within(item.checkedAt, fromIso, toIso))
    events.push({ badge: item.allItemsCleared === true ? "qc-passed" : "qc-hold", time: item.checkedAt!, approx: false });
  if (within(item.packedAt, fromIso, toIso))
    events.push({ badge: "packed", time: item.packedAt!, approx: false });
  if ((item.packingStatus === "Shipped" || item.packingStatus === "Returned") && !lt(item.updatedAt, fromIso))
    events.push({ badge: item.packingStatus === "Returned" ? "returned" : "shipped", time: item.updatedAt, approx: true });
  if (item.latestVideoStatus === "Completed" && (!lt(item.updatedAt, fromIso) || cohort === "video"))
    events.push({ badge: "video", time: item.updatedAt, approx: true });
  return events;
}
```

`ringTarget` per the interface table. Doc-comment the module with the spec pointer (`spec 2026-07-15 §A.5-A.7`) and the proxy caveats.

- [ ] **Step 4: Run tests**

Run: `npx vitest run carryoverRows && npm run lint`
Expected: PASS; no new lint issues.

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/carryoverRows.ts app/components/pipeline/carryoverRows.test.ts
git commit -m "feat(drilldown): pure overnight-stage + event-chain derivation"
```

### Task 2: `CarryoverSlimTable` component

**Files:**
- Create: `app/components/pipeline/CarryoverSlimTable.tsx`
- Modify: `app/components/PackingTable.tsx` (one word: `export` on `daysSince`, line ~148)
- Test: `app/components/pipeline/CarryoverSlimTable.test.tsx` (new)

**Interfaces:**
- Consumes: Task 1's derivation; `daysSince` from `../PackingTable` (add `export` to the existing fn — do NOT duplicate it); `dateRange` from `../../lib/dateWindow` (import only, file stays uncommitted).
- Produces (Task 4 mounts this):

```tsx
interface CarryoverSlimTableProps {
  cohort: CarryoverCohort;
  items: PackingItem[];
  total: number;
  page: number;
  limit: number;
  fromDate: string;  // yyyy-mm-dd dashboard window
  toDate: string;
  loading?: boolean;
  onPage: (p: number) => void;
  onOpenParcel?: (trackingNumber: string) => void;
}
```

**Context (spec §A.4-A.7):** columns `Tracking · Overnight stage · → · Today's events · Age`; compact (`text-xs`, th `px-2 py-1`, td `px-2 py-1.5`, `table-auto w-auto`, side margins `mx-4 mb-3`); no sortable headers; rows clickable → `onOpenParcel`. Highlight B: badge + amber semibold time (`text-amber-700 dark:text-amber-400`), no cell wash. Active cohort's chain event: `ring-2 ring-[#D97706] dark:ring-amber-400` on the badge + amber time. Time formats: overnight `d MMM HH:mm` (en-GB); chain `HH:mm` when `fromDate === toDate`, else `d MMM HH:mm`; `approx` → `~` prefix; `time: null` → `time n/a`. Age column: `daysSince(item.createdAt)` in rose (`text-rose-700 dark:text-rose-400`, semibold). Empty state row `No parcels`. Pager only when `total > limit`: `‹ Prev · Page {n} of {m} · Next ›` (buttons disabled at ends). Badge palette (all with `dark:` twins, matching `PipelineStage`/mockup): submitted `bg-[#EFF6FF] text-[#2563EB]`, qc-passed `bg-[#D1FAE5] text-[#065F46]`, qc-hold `bg-[#FEE2E2] text-[#991B1B]`, packed `bg-[#E0E7FF] text-[#4F46E5]`, shipped `bg-[#D1FAE5] text-[#10B981]`, returned `bg-[#FEE2E2] text-[#991B1B]`, video `bg-[#EDE9FE] text-[#7C3AED]`. Chain events render as inline-flex columns (badge on top, time stacked underneath) separated by a muted `·`; the overnight→chain arrow is its own narrow `→` cell. Compute `const { from, to } = dateRange(fromDate, toDate)` once per render.

- [ ] **Step 1: Write the failing test**

`CarryoverSlimTable.test.tsx` (jsdom pragma + act-environment + createRoot boilerplate; no `next/image` in this component — no mock needed). Fixture rows built from the same `mk()` base as Task 1. Window `fromDate="2026-07-10" toDate="2026-07-10"`. Cases:

```tsx
it("renders the four labelled columns and one row per parcel", ...);
  // th texts: Tracking / Overnight stage / Today's events / Age (5 th incl. blank arrow col)
it("overnight cell shows the stage badge with an amber time and no cell wash", ...);
  // querySelector('[data-overnight]') textContent contains "Submitted"; time span has text-amber-700
it("rings only the active cohort's chain event", async () => {
  // cohort "shipped", row with qc+packed+shipped chain:
  // container.querySelectorAll('[data-chain-event][data-ringed="true"]').length === 1
  // and its badge text is "Shipped"; time starts with "~"
});
it('overnight-Shipped renders "time n/a"; video cohort still shows its ringed Video event', ...);
it("row click calls onOpenParcel with the tracking number", ...);
it("pager appears only when total exceeds limit and pages via onPage", ...);
  // total 25, limit 10, page 0 → "Page 1 of 3"; Next click → onPage(1); total 5 → no pager
it("empty items show the No parcels row", ...);
```

Use `data-overnight`, `data-chain-event`, `data-ringed`, `data-slim-pager` hooks in the markup for these selectors.

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run CarryoverSlimTable`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

Add `export` to `daysSince` in `PackingTable.tsx` (`function daysSince` → `export function daysSince`; no other change to that file in this task). Build the component per Context; time formatter:

```ts
function fmtEvent(e: StageEvent, singleDay: boolean): string {
  if (!e.time) return "time n/a";
  const d = new Date(e.time);
  const t = d.toLocaleString("en-GB", singleDay
    ? { hour: "2-digit", minute: "2-digit" }
    : { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  return e.approx ? `~${t}` : t;
}
```

Ring check: `ringTarget(cohort)?.includes(event.badge)` — ring at most the FIRST matching event per row.

- [ ] **Step 4: Run tests**

Run: `npx vitest run CarryoverSlimTable PackingTable && npm run lint`
Expected: PASS (PackingTable's own tests unaffected by the export); no new lint issues.

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/CarryoverSlimTable.tsx app/components/pipeline/CarryoverSlimTable.test.tsx app/components/PackingTable.tsx
git commit -m "feat(drilldown): compact overnight→chain cohort table"
```

### Task 3: `CarryoverDrilldown` — single-cohort slim band

**Files:**
- Modify: `app/components/pipeline/CarryoverDrilldown.tsx`
- Test: `app/components/pipeline/CarryoverDrilldown.test.tsx` (amend)

**Interfaces:**
- Consumes: `cohortCount` (unchanged, `:7-15`), `stageEventsNote` (unchanged, `:35-40`).
- Produces (Task 4 depends on these exact names):

```ts
export interface DrilldownAnchor {
  cardLeft: number;    // px from the track's left edge
  cardWidth: number;
  pillCenter: number;  // px from the track's left edge
  trackWidth: number;
}
export const HEADLINE_LABEL: Record<CarryoverCohort, string> = {
  all: "updated", qc: "QC-checked", packed: "Packed", shipped: "Shipped", video: "Video completed",
};
```

  Props become `{ cohort, carryover, anchor: DrilldownAnchor | null, onClose, children }` — `onCohortChange`, `filters`, and `notchLeft` are REMOVED. `cohortDef` (`:17-33`), `CHIP_LABEL`/`COHORTS` (`:42-49`), and the chips block (`:105-129`) are DELETED. Header meta: `<b>{HEADLINE_LABEL[cohort]}</b> this period · submitted before today` (bold segment `font-semibold text-foreground`, rest muted). Footer + Esc handler + `×` close (`data-drilldown-close`, `aria-label="Close"`) unchanged.

**Context (spec §A.3):** the `<section>` gains `w-fit max-w-full` and inline `style={{ minWidth: anchor?.cardWidth, marginLeft: bandLeft ?? undefined }}`; a `useLayoutEffect` (deps `[anchor, cohort]`) plus a `ResizeObserver` on the section (guard `typeof ResizeObserver !== "undefined"` — jsdom lacks it) recompute:

```ts
const bw = Math.min(el.offsetWidth, anchor.trackWidth);
const left = Math.max(0, Math.min(anchor.cardLeft + anchor.cardWidth / 2 - bw / 2, anchor.trackWidth - bw));
setBandLeft(left);
setNotchLeft(Math.max(left + 14, Math.min(anchor.pillCenter, left + bw - 14)));
```

The notch `<span>` keeps its current classes, positioned at the computed `notchLeft`; `anchor === null` hides it and skips positioning (band renders full-flow, as today's `notchLeft === null` case). In jsdom `offsetWidth` is 0 — the math degrades gracefully; tests assert copy/structure, not geometry.

- [ ] **Step 1: Update the tests to the new contract (failing first)**

In `CarryoverDrilldown.test.tsx`:
- DELETE the `cohortDef` import + the `def strings are verbatim spec copy` test (`:30-42`).
- Keep `cohortCount` (`:22-28`) and `stageEventsNote` (`:44-48`) tests unchanged.
- `render()` helper: drop `onCohortChange` and `notchLeft`, pass `anchor={{ cardLeft: 100, cardWidth: 200, pillCenter: 180, trackWidth: 1000 }}`.
- Rewrite the header test:

```tsx
it("renders header count, single-cohort meta line, children slot and footer — no chips", () => {
  const { container } = render("qc");
  const text = container.textContent ?? "";
  expect(text).toContain("Carryover");
  expect(text).toContain("57");
  expect(text).toContain("QC-checked this period · submitted before today");
  expect(text).not.toContain("Updated this period:");
  expect(container.querySelector("[data-cohort-chip]")).toBeNull();
  expect(container.querySelector('[data-testid="table-slot"]')).not.toBeNull();
  expect(text).toContain("339 stage events across 218 distinct parcels");
});
```

- Rewrite the interaction test to close-only (chip-click half goes; `×` + Esc assertions stay). Keep the bare-`×` test (`:104-109`) as-is.

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx vitest run CarryoverDrilldown`
Expected: FAIL — unknown `anchor` prop / removed exports still exported / chips still render.

- [ ] **Step 3: Implement**

Per Interfaces + Context. Delete dead imports (`CHIP_LABEL` users). Keep `cohortCount` and `stageEventsNote` exported (PipelineSection tests and the footer rely on them).

- [ ] **Step 4: Run tests**

Run: `npx vitest run CarryoverDrilldown && npm run lint`
Expected: CarryoverDrilldown tests PASS. (`PipelineSection.drilldown` now FAILS on the old props — fixed in Task 4; do not run the full suite gate until then.)

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/CarryoverDrilldown.tsx app/components/pipeline/CarryoverDrilldown.test.tsx
git commit -m "feat(drilldown): single-cohort slim band — chips and def copy removed, anchor sizing"
```

### Task 4: `PipelineSection` wiring — anchor + slim table

**Files:**
- Modify: `app/components/pipeline/PipelineSection.tsx`
- Test: `app/components/pipeline/PipelineSection.drilldown.test.tsx` (amend)

**Interfaces:**
- Consumes: Task 2's `CarryoverSlimTable`, Task 3's `anchor` prop shape.
- Produces: no new exports. `PipelineStage`, `STAGE_COHORT`, `toggleCohort` re-scoping, the pill date gate (`:267`), and `useCarryoverParcels` stay as-is (its `shippingOption`/`status` remain `"all"` — leave the hook untouched).

**Context:** replace `notchLeft` state (`:33`) with `anchor: DrilldownAnchor | null`; the effect at `:36-46` and the branch in `toggleCohort` (`:193-199`) both become one helper that measures `stageRect`/`trackRect` and the pill (`stageEl.querySelector('[data-carryover-pill]')`, fallback card center):

```ts
setAnchor({
  cardLeft: stageRect.left - trackRect.left,
  cardWidth: stageRect.width,
  pillCenter: (pillRect ?? stageRect).left - trackRect.left + (pillRect ?? stageRect).width / 2,
  trackWidth: trackRect.width,
});
```

In the drill-down JSX (`:283-330`): drop the `filters={<CohortFilters …/>}` block and the `CohortFilters` import (component stays in the codebase — `BacklogSection` uses it); drop `onCohortChange`; keep the error row (`data-drilldown-error`, `:300-309`) verbatim; replace the `<PackingTable …>` instance (`:310-328`) with:

```tsx
<CarryoverSlimTable
  cohort={activeCohort}
  items={cohortList.items}
  total={cohortList.total}
  page={cohortList.page}
  limit={cohortList.limit}
  fromDate={fromDate ?? ""}
  toDate={toDate ?? ""}
  loading={cohortList.loading}
  onPage={cohortList.setPage}
  onOpenParcel={onOpenParcel}
/>
```

(The pill gate at `:267` guarantees `fromDate`/`toDate` are set whenever the band can open.)

- [ ] **Step 1: Update the tests (failing first)**

In `PipelineSection.drilldown.test.tsx`:
- Test 1 (`:53-95`): replace the def-copy expectations — after the parcels-pill click expect
  `"updated this period · submitted before today"`; after the QC-pill click expect
  `"QC-checked this period · submitted before today"` and `"57"`; the collapse assertion becomes
  `not.toContain("QC-checked this period")`. Keep the `itemType=carryover` fetch assert and
  `aria-expanded` asserts.
- Test 2 (`:97-123`): `"Updated this period:"` no longer exists — assert on
  `"submitted before today"` presence/absence instead.
- Test 3 (error row, `:125-148`): unchanged.
- Add one slim-table smoke assert to test 1: mock list body returns one item (reuse the
  `PackingItem` literal from `BacklogSection.cancel.test.tsx:13-23` with
  `createdAt: "2026-07-09T05:00:00Z"`, `checkedAt: "2026-07-10T02:00:00Z"`,
  `allItemsCleared: true`) and expect `container.querySelector("[data-overnight]")` non-null
  after the QC-pill click + flushes.

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx vitest run PipelineSection.drilldown`
Expected: FAIL — old props passed to the new `CarryoverDrilldown` contract.

- [ ] **Step 3: Implement** per Context.

- [ ] **Step 4: Run the FULL suite**

Run: `npx vitest run && npm run lint && npm run build`
Expected: all green (this is the Phase-1 gate — `PipelineSection.inclusive`, `PipelineStage.carryover`, `pipelineMath`, `CohortFilters` tests must be untouched and passing); lint still 36; build clean.

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/PipelineSection.tsx app/components/pipeline/PipelineSection.drilldown.test.tsx
git commit -m "feat(drilldown): wire slim table + card-anchored band, drop drill-down filters"
```

---

## Phase 2 — Backlog behind a table-card view tab

### Task 5: `TableViewTabs` component

**Files:**
- Create: `app/components/TableViewTabs.tsx`
- Test: `app/components/TableViewTabs.test.tsx` (new)

**Interfaces:**
- Consumes: `BacklogSummary` (`app/types.ts:264`).
- Produces (Task 7 depends on these exact names):

```tsx
export type TableView = "parcels" | "backlog";
interface TableViewTabsProps {
  view: TableView;
  onViewChange: (v: TableView) => void;
  backlog: BacklogSummary | null;
}
export function TableViewTabs({ view, onViewChange, backlog }: TableViewTabsProps): JSX.Element;
```

**Context (spec §B.9, B.11, B.14; mockup `2026-07-15-backlog-tab.html` `.dashhead`/`.dtab` lines 76-89, mounted on the card at lines 181-184):** two buttons in a `flex items-end gap-1 border-b border-border px-3` row rendered as the top strip of the table card (Task 7 mounts it); each `data-table-view-tab="parcels"|"backlog"`, `data-active`, `aria-pressed`, `border-b-2` (transparent inactive; active: `border-accent` for Parcels, rose `border-[#E11D48] dark:border-rose-500` for Backlog, green `border-green-600 dark:border-green-500` when the Backlog tab is active at zero); text `text-sm font-bold`, muted when inactive. Backlog tab content by state:
- `backlog === null` → `Backlog` + `—` (muted, no glyph)
- `total > 0` → `⚠ Backlog` (uppercase eyebrow style `text-[11px] font-extrabold tracking-[0.06em] text-[#BE123C] dark:text-rose-300`) + `{total.toLocaleString()}` (`text-[15px] font-bold text-[#E11D48] dark:text-rose-400`)
- `total === 0` → `✓ Backlog clear` (green: `text-green-700 dark:text-green-400`), no count.
Full WAI-ARIA tabs pattern is out of scope (spec §6).

- [ ] **Step 1: Write the failing test**

`TableViewTabs.test.tsx` (jsdom + createRoot boilerplate):

```tsx
it("renders both tabs, marks the active one, and fires onViewChange", ...);
  // view "parcels" → [data-table-view-tab="parcels"][data-active="true"], textContent contains "Parcels";
  // click backlog tab → onViewChange("backlog")
it("badge states: — while loading, ⚠ + count when open, ✓ Backlog clear at zero", () => {
  // null → textContent contains "Backlog" and "—", not "⚠"
  // { total: 1660, byStage: {...}, oldestCreatedAt: "..." } → contains "⚠ Backlog" and "1,660"
  // { total: 0, ... } → contains "✓ Backlog clear", not "0"
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run TableViewTabs`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement** per Context.

- [ ] **Step 4: Run tests**

Run: `npx vitest run TableViewTabs && npm run lint`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/components/TableViewTabs.tsx app/components/TableViewTabs.test.tsx
git commit -m "feat(dashboard): Parcels/Backlog view tabs for the main table card"
```

### Task 6: `BacklogSection` — always-expanded, lifted summary, zero state

**Files:**
- Modify: `app/components/pipeline/BacklogSection.tsx`
- Test: `app/components/pipeline/BacklogSection.test.tsx` (rewrite the toggle test), `app/components/pipeline/BacklogSection.cancel.test.tsx` (amend mounts)

**Interfaces:**
- Consumes: `BacklogSummary` via props (hook call removed).
- Produces (Task 7 depends on these exact names):

```tsx
interface BacklogSectionProps {
  summary: BacklogSummary | null;
  onRefetchSummary: () => void;
  operators: Operator[];
  onOpenParcel?: (trackingNumber: string) => void;
}
```

**Context (spec §B.12-B.14):**
- DELETE: `useBacklogSummary` import/call (`:23`), `expanded` state (`:24`), the toggle `<button data-backlog-toggle>` (`:121-139` — becomes a static `<div>` header with the SAME eyebrow/headline/meta copy, no chevron, no `aria-expanded`, no hover), the `{expanded && …}` gate (`:141`).
- `useBacklogParcels(stage)` runs unconditionally (`:26` loses the ternary) — the section only mounts when the tab is open, preserving lazy fetch.
- `confirmCancel`: `refetch()` (`:100`) → `onRefetchSummary()`. Everything else in the cancel flow, chips, `CohortFilters`, bulk bar, partial-cancel notice, error row, `PackingTable` instance, dialog, and footer is byte-for-byte UNCHANGED (the `40814c6` verbatim lock).
- Outer chrome flattens — the section now renders inside the tabbed table card (Task 7): the `<section>` classes drop `mt-2 mb-3 2xl:mb-4 rounded-md border border-border shadow-sm`, keeping `overflow-hidden` + the 3px left accent (`border-l-[3px] border-l-[#E11D48] dark:border-l-rose-500` — mockup `2026-07-15-backlog-tab.html:185`).
- Zero state (mockup `2026-07-15-backlog-tab.html` §2): when `summary !== null && summary.total === 0`, the all-clear row is the ENTIRE band — header line, chips, bulk bar, notice, error row, table AND footer all hidden:

```tsx
<div data-backlog-clear className="flex items-center gap-2.5 px-5 py-4">
  <span className="rounded-full bg-green-100 px-3 py-0.5 text-[13px] font-extrabold text-green-800 dark:bg-green-900/30 dark:text-green-400">
    ✓ All clear
  </span>
  <span className="text-[13px] text-muted-foreground">
    every parcel submitted before today has shipped · checked at page load
  </span>
</div>
```

  and the section's left accent bar swaps rose → green (`border-l-green-600 dark:border-l-green-500`) via a conditional class.

- [ ] **Step 1: Update the tests (failing first)**

`BacklogSection.test.tsx` — the collapsed→expand toggle test (`:40-70`) is rewritten for always-expanded-in-tab rendering:
- `mockFetch` drops the `/dashboard/backlog` arm (the section no longer fetches the summary); keep the list arm.
- Mount becomes `<BacklogSection summary={summaryBody} onRefetchSummary={vi.fn()} operators={[]} />` (reuse the existing `summaryBody` literal as the prop).
- New assertions, replacing the toggle choreography:

```tsx
it("renders expanded: header line, chips, footer, and an eager stage-list fetch", async () => {
  // after mount + flush:
  //   textContent contains "Open backlog", "1,660", "parcels not yet shipped · submitted before today"
  //   [data-backlog-toggle] is NULL (toggle gone)
  //   chips render immediately: "QC Hold · 4", footer "backlog since 3 Jul 2026"
  //   fetch was called with itemType=backlog (the all-stage list, no click needed)
  //   chip click still swaps the itemType (keep the qc-hold assertion from the old test)
});
it("zero backlog renders the green all-clear row and hides everything else", async () => {
  // summary={{ total: 0, byStage: { submitted: 0, qcHold: 0, qcPassed: 0, packed: 0 }, oldestCreatedAt: null }}
  // [data-backlog-clear] text contains "✓ All clear" and
  //   "every parcel submitted before today has shipped · checked at page load"
  // [data-backlog-chip] null; table absent; no "Open backlog" header line;
  //   footer text "backlog since 3 Jul 2026" absent
});
```

`BacklogSection.cancel.test.tsx` — mechanical amendments, cancel assertions unchanged:
- `mockFetch` drops the `/dashboard/backlog` arm.
- `mount()` (`:132-138`) and the first test's mount (`:60`) pass
  `summary={…}` (derive from the old summary literals) and `onRefetchSummary={refetchSpy}`.
- `openAndSelect` (`:105-130`): DELETE the toggle click (`:106-109`) — rows are already there after mount flushes.
- Add to the happy-path test: `expect(refetchSpy).toHaveBeenCalledTimes(1)` after confirm (the badge-refresh contract); the failure tests assert it was NOT called.

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx vitest run BacklogSection`
Expected: FAIL — unknown props / toggle still required.

- [ ] **Step 3: Implement** per Context.

- [ ] **Step 4: Run tests**

Run: `npx vitest run BacklogSection && npm run lint`
Expected: all five cancel cases + both section tests PASS. (`Dashboard.tsx` now has a TS error on the old `<BacklogSection operators… />` call — fixed in Task 7; do not run `npm run build` yet.)

- [ ] **Step 5: Commit**

```bash
git add app/components/pipeline/BacklogSection.tsx app/components/pipeline/BacklogSection.test.tsx app/components/pipeline/BacklogSection.cancel.test.tsx
git commit -m "feat(backlog): always-expanded band with lifted summary and green zero state"
```

### Task 7: `Dashboard` — card-level view swap (+ `PackingTable.frameless`)

**Files:**
- Modify: `app/components/Dashboard.tsx`, `app/components/PackingTable.tsx`
- Test: `app/components/PackingTable.columns.test.tsx` (append)

**Interfaces:**
- Consumes: Task 5's `TableViewTabs`/`TableView`, Task 6's props, `useBacklogSummary` from `app/hooks/useBacklog.ts` (unchanged hook).
- Produces: `PackingTable` prop `frameless?: boolean` (default false) — drops the table's own outer card chrome so it can live inside the tabbed card; only Dashboard's main instance passes it. Plus the shipped page. No test file exists for `Dashboard.tsx` (it is a `useDashboard` orchestrator); coverage = the component tests above + `npm run build` + Task 8's live smoke.

**Context (spec §B.9-B.10):** the header — h1 + subtitle (`:61-62`) and the right-side cluster (`:64-70`) — is UNTOUCHED. CalendarStrip, PipelineSection, and the FilterBar card render on BOTH views, unchanged (the date filter + FilterBar affect only the Parcels view; the backlog stays date-independent — spec §B.10's flagged UX nit, do not redesign).

```tsx
const [view, setView] = useState<TableView>("parcels");
const { summary: backlogSummary, refetch: refetchBacklog } = useBacklogSummary();
```

Remove the old `BacklogSection` mount at `:104`. Replace the bare `<PackingTable …/>` (`:138-154`) with the tabbed card:

```tsx
<div className="overflow-hidden rounded-2xl border border-border bg-card shadow-sm">
  <TableViewTabs view={view} onViewChange={setView} backlog={backlogSummary} />
  {view === "parcels" ? (
    <PackingTable
      /* …all existing props unchanged… */
      frameless
    />
  ) : (
    <BacklogSection
      summary={backlogSummary}
      onRefetchSummary={refetchBacklog}
      operators={operators}
      onOpenParcel={openModal}
    />
  )}
</div>
```

`PackingTable.tsx:220` — the outer div's class becomes conditional (destructure the new prop):

```tsx
<div className={frameless ? "overflow-hidden bg-card" : "overflow-hidden rounded-2xl border border-border bg-card shadow-sm"}>
```

- [ ] **Step 1: Write the failing test**

Append to `PackingTable.columns.test.tsx` (reuse the file's existing base-prop/item literals and `renderToStaticMarkup` style):

```tsx
  it("frameless drops the outer card chrome", () => {
    // default render → markup contains "rounded-2xl"
    // same props + frameless → markup does NOT contain "rounded-2xl" (nor "shadow-sm")
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run PackingTable.columns`
Expected: FAIL — the frameless render still contains `rounded-2xl`.

- [ ] **Step 3: Implement** per Context (PackingTable prop first, then the Dashboard wiring; `npm run build` also fails at this task's base because `BacklogSection`'s new required props are unsatisfied at `Dashboard.tsx:104` — this task clears it).

- [ ] **Step 4: Full gate**

Run: `npx vitest run && npm run lint && npm run build`
Expected: full suite green; lint 36; build clean.

- [ ] **Step 5: Commit**

```bash
git add app/components/Dashboard.tsx app/components/PackingTable.tsx app/components/PackingTable.columns.test.tsx
git commit -m "feat(dashboard): Parcels/Backlog tabs on the main table card — in-place view swap"
```

---

## Phase 3 — Verification

### Task 8: Full suites + live smoke vs warehouse_snapshot

**Files:**
- Create: `.superpowers/sdd/declutter-t8-smoke-report.md` (FE worktree)

**Context:** dev stack should already be running (BE :8080 on `warehouse_snapshot`, FE :3000); restart per memory `reference_local-dev-stack` if dead. The backend is UNCHANGED by this plan — no rebuild needed; only restart the FE dev server so it picks up the new components. Browser automation via the playwright-cli skill (screenshots to `.playwright-cli/`).

- [ ] **Step 1: Full test suites**

Run: `npx vitest run && npm run lint && npm run build` — all green, lint at the 36 baseline.

- [ ] **Step 2: Smoke checklist (record every number in the report)**

1. Dashboard loads; header h1 `Order Pipeline` + subtitle UNCHANGED; the main-table card carries the `Parcels | ⚠ Backlog <n>` tab row (`Parcels` active, n ≈ 1.6k — record exact); console clean.
2. Friday window (10 Jul): QC card's amber pill click → band opens with `CARRYOVER 57 · QC-checked this period · submitted before today`; NO cohort chips, NO shipping/status selects, NO def sentence; footer reads `… 339 stage events across 218 distinct parcels`.
3. Band geometry: visibly narrower than the track, min-width ≥ the QC card, centered under it and clamped inside the track; notch points at the pill. Re-click Shipped's pill → band re-scopes and re-anchors under the Shipped card.
4. Rows: overnight badge + amber time stacked (no cell wash); `→`; chain badges with stacked times; exactly one ringed event per row matching the active cohort; Shipped times wear `~`; Age column in rose. Video cohort: hunt for an overnight-Shipped row showing `time n/a` (may not exist in the window — note either way).
5. Parcels card's pill → `CARRYOVER 218 · updated this period …`, no ring on any chain event; pager renders (`218 > 10`) and pages.
6. Row click opens the shared parcel detail panel; `×` and Esc close the band.
7. Backlog tab click: CalendarStrip, pipeline track, and FilterBar all STAY; only the table card swaps — the band renders expanded inside the card (no doubled border/rounding), header line with `oldest {date}`, chips summing to the total (record all five), shipping/status selects, footer `…independent of the date filter above` (now literally true — the date strip is above it). Card tab badge == band headline == `GET /dashboard/backlog` total.
8. Cancel flow on a THROWAWAY row: insert a synthetic parcel (`SMOKE-DCLT-1`, created 2026-07-05, no events) via psql, reload, select it, cancel with a note; verify the partial/OK behaviour unchanged AND the tab badge decrements without a reload (the lifted refetch); then DELETE the synthetic rows (packing_lists + workflow_events).
9. Parcels tab restores the table (state preserved — component state, not URL). Date-independence probe: change the date filter / a FilterBar filter while on the Backlog view → backlog list unchanged; switch back → the Parcels table reflects the new filters.
10. Dark mode pass over: band header/badges/ring, card tabs (zero state can't be forced live on the snapshot — test-only), backlog band inside the card.
11. Screenshots: slim band under QC card (light+dark), re-anchored under Shipped, backlog tab open on the card, card tab row close-up.

- [ ] **Step 3: Write the report + commit**

```bash
git add .superpowers/sdd/declutter-t8-smoke-report.md
git commit -m "test: declutter drill-down + backlog tab live smoke report"
```

### Task 9: Final whole-branch review

- [ ] Dispatch a fresh reviewer subagent over the full FE worktree diff since **40814c6** (this plan's commits only), spec at hand, verdict format: Ready-to-merge YES/NO + Critical/Major/Minor findings. Verify each commit's parentage chains from 40814c6 (no orphaned bases). Confirm `app/lib/dateWindow.ts` appears in NO commit (`git log --oneline 40814c6..HEAD -- app/lib/dateWindow.ts` must be empty). Fix Criticals; record Minors in the ledger (`.superpowers/sdd/progress.md`); do NOT push (user-gated).
