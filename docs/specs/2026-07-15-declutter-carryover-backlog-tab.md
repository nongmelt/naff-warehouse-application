# Spec: Dashboard de-clutter — slim single-cohort carryover drill-down + Backlog table tab

Date: 2026-07-15. Resolves wayfinder **#67** (map **#62**); decision sets locked on **#65** (carryover,
mockup v4.2 sign-off "carryover looks good") and **#66** (backlog, grilling rounds 3–5) — **revised
same-day**: the backlog home moved from a dashboard header tab to view tabs on the main
packing-lists table card; the dashboard h1 + subtitle stay. Visual references:
`docs/mockups/2026-07-15-declutter-two-step-carryover-backlog-page.html` (v5 — carryover band;
its header-tab/backlog panes are superseded) and `docs/mockups/2026-07-15-backlog-tab.html`
(v2 — table-card tabs, zero state, dialog).
Research grounding: `docs/research/2026-07-15-carryover-backlog-data-availability.md`.
**Frontend-only** — touches ONE submodule: frontend worktree `frontend/.worktrees/analytics-debug`,
branch `fix/analytics-post-shipped` (tip at spec time: **40814c6**, UNPUSHED; suite green 99/99).
New commits continue on the same branch — one feature line, one review unit. No backend changes,
no migrations, no wire-format changes: the FE keeps consuming today's endpoints exactly as-is.

## 1. Problem

Two de-clutter follow-ups from the Open Backlog live review (both grilled to resolution this session):

1. **The drill-down is a second dashboard, not a lens.** The carryover drill-down spans the full
   track, repeats the five-cohort chip switcher, a def-copy sentence, shipping/status filters, and
   the full 10-column `PackingTable` — for a question that is per-stage ("what are these 12 QC
   carryover parcels?"). Worse, the wide table answers the *wrong* question: it shows current
   status, not **what stage the parcel slept in and what happened to it today** — the two facts
   that explain why a pre-window parcel appears under a stage pill at all.
2. **The backlog band crowds the pipeline.** `BacklogSection` sits permanently between the
   pipeline track and the filter bar (`Dashboard.tsx:104`) — a red management surface wedged into
   an operational monitoring page. Its headline competes with the pipeline stats, and its
   collapse toggle duplicates an affordance a view tab on the main table card can carry: the
   backlog IS a packing-lists listing, so it belongs where the packing-lists table lives.

## 2. Decisions (locked)

### A. Slim single-cohort carryover drill-down

1. **Single-cohort drill-down — chip switcher removed.** Clicking a stage's amber carryover pill
   opens the drill-down for ONLY that stage's cohort (`STAGE_COHORT`, `PipelineSection.tsx:13-15`,
   unchanged — including `parcels → all`). The "Updated this period" cohort chips row
   (`CarryoverDrilldown.tsx:105-129`) is DELETED, along with `CHIP_LABEL`/`COHORTS` and the
   `onCohortChange` prop. Clicking a *different* stage's pill re-scopes the open band (existing
   `toggleCohort`, `PipelineSection.tsx:182-200`); clicking the active pill, the `×`, or Esc
   closes it. Pill markup/behaviour in `PipelineStage.tsx:153-176` is unchanged
   (`PipelineStage.carryover.test.tsx` stays green as-is).
2. **Band header.** `CARRYOVER <n> · <label> this period · submitted before today` — eyebrow +
   count as today (`CarryoverDrilldown.tsx:86-93`), then the meta string with the label bold
   foreground inside muted text (mockup `.chead-meta`). `n` stays `cohortCount()`
   (`CarryoverDrilldown.tsx:7-15`, untouched — **pill parity kept**: each pill counts that
   stage's events in window, a subset of its own card's number; one parcel can appear under
   several pills). Labels per cohort: `all → updated`, `qc → QC-checked`, `packed → Packed`,
   `shipped → Shipped`, `video → Video completed`. The def-copy sentence (`cohortDef`,
   `CarryoverDrilldown.tsx:17-33`) is DELETED. The stage-events footer (`stageEventsNote`,
   `:35-40` — "one parcel can appear under several stages — N stage events across M distinct
   parcels") is KEPT verbatim. (The mockup demos no pill on the Parcels card; the real dashboard
   has one (`parcelsSummaryPills`) — it keeps opening the `all` cohort with the `updated` label
   and no ring, decision A.6.)
3. **Container sized to its stage card, not the track.** The band's width fits content
   (`w-fit max-w-full`), `min-width` = the clicked stage card's width, centered under that card
   and clamped to the track; the amber notch stays on the band's top edge pointing at the pill.
   Mechanism: `PipelineSection` (which already measures `stageRefs`/`trackRef`,
   `:36-46`/`:193-199`) passes an `anchor = { cardLeft, cardWidth, pillCenter, trackWidth }`
   (px, relative to the track's left edge; pill center via
   `stageEl.querySelector('[data-carryover-pill]')`, falling back to the card center) replacing
   the bare `notchLeft` prop. `CarryoverDrilldown` measures its own rendered width
   (`useLayoutEffect` + `ResizeObserver`, guarded `typeof ResizeObserver !== "undefined"` for
   jsdom) and computes, exactly as mockup lines 529-536:
   `bw = min(band.offsetWidth, trackWidth)`;
   `left = clamp(cardLeft + cardWidth/2 − bw/2, 0, trackWidth − bw)`;
   `notch = clamp(pillCenter, left + 14, left + bw − 14)`.
   No resize re-anchoring beyond the observer (parity with today's behaviour: geometry recomputes
   on cohort change and band-content growth, not on window resize).
4. **Slim table replaces `PackingTable` inside the drill-down.** New
   `app/components/pipeline/CarryoverSlimTable.tsx`, columns
   `Tracking · Overnight stage · → · Today's events · Age` — **Order and Platform columns are
   dropped** (no Platform glyph). Compact: `text-xs`, reduced padding (th `px-2 py-1`, td
   `px-2 py-1.5`), `table-auto w-auto` inside the band with side margins (mockup `.tbl.slim`).
   Rows stay clickable → `onOpenParcel(trackingNumber)` (same shared detail panel as today).
   Data source unchanged: `useCarryoverParcels` with the active cohort — the existing
   `itemType=carryover-<stage>` list fetch IS the membership predicate ("parcel has that cohort's
   event in window"); default sort `createdAt asc` stays and is now FIXED (no sortable headers —
   the new columns are derived, not wire fields). Paging: keep the hook's `page`/`limit` (10)
   with a minimal footer pager `‹ Prev · Page {n} of {m} · Next ›` shown only when
   `total > limit` (cohorts run to hundreds; the mockup omits a pager but the data does not).
   The res.ok error row (`data-drilldown-error`, `Couldn't load parcels — retry`) is kept.
5. **Overnight stage = the stage the parcel was in when the window started** (last stage event
   strictly before `dateRange(fromDate, toDate).from`), derived client-side from `PackingItem`
   timestamps (`types.ts:72-99`): `createdAt` = Submitted, `checkedAt` = QC Passed
   (`allItemsCleared === true`) / QC Hold (false or null), `packedAt` = Packed. Precedence =
   furthest pre-window stage (pipeline order guarantees `createdAt ≤ checkedAt ≤ packedAt`).
   A parcel whose only pre-window event is creation shows **Submitted**. Special case
   **overnight-Shipped** (possible in the video cohort): `packingStatus ∈ {Shipped, Returned}`
   AND `updatedAt < from` (the row was last touched before the window — only possible when the
   in-window event lives in `packing_videos`) → badge Shipped/Returned with **`time n/a`** (no
   `shippedAt` on list rows — research §1.2). Highlight **style B**: stage badge + amber
   semibold time (`text-amber-700 dark:text-amber-400`) stacked under it, **no cell background**.
6. **Today's events = the parcel's in-window stage-event chain, in pipeline order**, each event
   a badge with its timestamp stacked underneath; the active cohort's event gets an amber ring
   (`ring-2 ring-[#D97706] dark:ring-amber-400`) + amber time. Derivation (cohort-independent
   except the noted fallback — rows are identical across cohorts; only membership + ring differ):
   - `checkedAt ∈ [from, to]` → QC Passed/QC Hold @ `checkedAt`
   - `packedAt ∈ [from, to]` → Packed @ `packedAt`
   - `packingStatus === "Shipped"` (or `"Returned"`) AND `updatedAt ≥ from` → Shipped/Returned
     @ `~updatedAt` (proxy, decision A.7)
   - `latestVideoStatus === "Completed"` AND (`updatedAt ≥ from` OR `cohort === "video"`) →
     Video @ `~updatedAt`. The `cohort === "video"` fallback guarantees the ringed event exists
     on every video-cohort row even when the list row's `updatedAt` predates the window (video
     completion lives in `packing_videos`); its proxy time is then stale — accepted, it wears
     the `~`.
   Ring target per cohort: `qc` → the QC event, `packed` → Packed, `shipped` → Shipped,
   `video` → Video, `all` → **no ring** (every in-window event is the membership reason).
7. **Timestamps — `updatedAt` proxy with `~`, accepted.** Shipped-event and video-completed
   times are missing from list rows; both render `~` + the `updatedAt` proxy. Overnight-Shipped
   shows `time n/a` (A.5). Formats: overnight time `d MMM HH:mm` (en-GB, browser tz — same
   convention as `PackingTable.fmt`, `:140-145`); chain times `HH:mm` when the window is a
   single day (`fromDate === toDate`), else `d MMM HH:mm`. Backend list-row `shippedAt` /
   video-completed fields are OUT OF SCOPE — optional post-merge enhancement (§6).
8. **Shipping + status filters leave the drill-down.** The `CohortFilters` slot
   (`PipelineSection.tsx:290-298`, `CarryoverDrilldown.tsx:128` `{filters}`) is removed with the
   chips row — mockup v4.2 shows none in the slim band; the compact table has no Status/Platform
   columns to filter against. `CohortFilters.tsx` itself STAYS (the backlog band keeps using it,
   `BacklogSection.tsx:163-169`). `useCarryoverParcels`' `shippingOption`/`status` params stay in
   the hook, permanently `"all"` from the drill-down — dead-but-harmless; cleanup is out of scope.

### B. Backlog behind a view tab on the main table card — no new route

9. **View tabs on the packing-lists table card — the dashboard header is untouched.** The h1
   "Order Pipeline" + subtitle "Real-time order fulfillment overview" (`Dashboard.tsx:61-62`)
   stay exactly as they are (the earlier header-tab direction is superseded). A new
   `TableViewTabs` component renders as the top row of the main-table card region
   (`Dashboard.tsx:138-154`): `Parcels | ⚠ Backlog <count>` (mockup
   `2026-07-15-backlog-tab.html` §1, `.dashhead`/`.dtab` lines 76-89 + 181-184 — borderless
   buttons, 2px bottom border on the active tab: accent for Parcels, rose for Backlog; same
   visual language as the superseded header-tab design, mounted on the card).
10. **In-place view swap inside the card — no `/backlog` route, no URL param.** Local `useState`
    in `Dashboard` (`view: "parcels" | "backlog"`). Parcels view = today's main `PackingTable`
    exactly as-is. Backlog view = the `BacklogSection` band content rendering in its place inside
    the card. CalendarStrip (`:75-94`), PipelineSection with the new slim drill-down (`:96-102`),
    and the FilterBar card (`:106-136`) ALL stay rendered on BOTH views; the date filter and
    FilterBar affect only the Parcels view — the backlog stays date-independent. UX nit (flagged,
    not redesigned): on the Backlog view the FilterBar above remains visible but is inert for the
    content below it. Mechanics: one card wrapper (`rounded-2xl border border-border bg-card
    shadow-sm`) owns the tab row + the swapped content; `PackingTable` gains an opt-in
    `frameless` boolean (its own outer card chrome at `PackingTable.tsx:220` drops when
    embedded) and `BacklogSection`'s outer chrome flattens likewise (border/rounding/shadow off,
    the 3px left accent kept — mockup line 185). The band never leaves `Dashboard.tsx` —
    `operators` + `onOpenParcel={openModal}` wiring stays exactly as today, so the shared detail
    panel and cancel flow keep working unmoved.
11. **Tab badge copy: count only** — `⚠ Backlog` (rose, uppercase eyebrow style) + the total,
    no "oldest" date (oldest stays inside the band header line). While the summary hasn't loaded:
    `Backlog —` in default muted styling.
12. **Summary hook lifts to `Dashboard`.** The card's tab badge needs the count before the band
    mounts, so `useBacklogSummary()` moves from `BacklogSection` (`BacklogSection.tsx:23`) to
    `Dashboard`; `BacklogSection` gains props `summary: BacklogSummary | null` +
    `onRefetchSummary: () => void` and drops its own hook call. `confirmCancel`'s
    `refetch()` (`:100`) becomes `onRefetchSummary()` — a successful cancel updates the tab badge
    and the band header from the same fetch. `useBacklogParcels` stays inside the section.
13. **BacklogSection renders always-expanded — its own toggle is removed.** The
    collapsed/expand button (`data-backlog-toggle`, `BacklogSection.tsx:121-139`) becomes a
    static header line (same eyebrow `Open backlog`, rose headline count, meta
    `parcels not yet shipped · submitted before today · oldest {d MMM yyyy}` — copy verbatim,
    chevron and `aria-expanded` gone); the `expanded` state and `{expanded && …}` gate
    (`:24`, `:141`) are deleted and `useBacklogParcels(stage)` runs whenever the section is
    mounted (i.e. when the tab is open — this preserves lazy fetching). Everything else renders
    VERBATIM from `40814c6`: stage chips (`:143-162`), `CohortFilters` (`:163-169`), selection +
    bulk bar (`:171-189`), partial-cancel amber notice (`:190-197`), error row (`:198-207`),
    `PackingTable` with `highlightSubmittedAt`/`showAge`/selection (`:209-234`), footer note
    (`:235-237`), cancel dialog incl. failure surface (`:241-282`). The footer's verbatim tail
    "independent of the date filter above" is literally accurate on the card tab (CalendarStrip
    and FilterBar render above the card on both views) — kept, no copy nit.
14. **Zero-backlog state.** The tab stays visible; its badge turns green **`✓ Backlog clear`**
    (no count; active underline green). Tab content: when `summary.total === 0`, the all-clear
    row IS the band — header line, chips, filters, bulk bar, table, and footer all hide
    (mockup `2026-07-15-backlog-tab.html` §2); one green row matching the pipeline's All-clear
    language (`PipelineStage.tsx:199-207` green family): pill `✓ All clear` +
    `every parcel submitted before today has shipped · checked at page load`. The section's
    left accent bar turns green.
15. **Count freshness: fetch-on-load only + refetch after cancel POST** — exactly today's
    behaviour (`useBacklogSummary`, `useBacklog.ts:49-70`). No WS piggyback, no polling; a
    long-lived tab shows a stale badge until reload or a cancel — accepted (#66; the zero
    state's "checked at page load" tail says so on screen).

## 3. Phasing / sequencing

- **Phase 1 — slim drill-down** (all in `frontend/.worktrees/analytics-debug`): pure row-derivation
  module → `CarryoverSlimTable` → `CarryoverDrilldown` conversion (header/sizing/notch, chips +
  def-copy + filters removal) → `PipelineSection` wiring + test updates.
- **Phase 2 — backlog tab**: `TableViewTabs` → `BacklogSection` always-expanded + lifted-summary
  props + zero state → `Dashboard` card-level view swap (+ `PackingTable` `frameless` opt-in).
- **Phase 3 — verification**: full suites + lint + build; live smoke vs `warehouse_snapshot`;
  final whole-branch review.
- **No deploy coupling**: frontend-only; ships against the already-built backend on this branch
  (BE tip f26965f line) with zero wire changes.

## 4. Oracle numbers (warehouse_snapshot — Friday 10 Jul window, unchanged from prior builds)

- Carryover pills: All **218** / QC **57** (55+2) / Packed **66** / Shipped **216** / Video **98**
  (`videoEvents` def line is gone with the def copy — the 107 raw-video figure no longer renders).
- Single-cohort header example: QC pill → `CARRYOVER 57 · QC-checked this period · submitted
  before today`; footer `… 339 stage events across 218 distinct parcels` on every cohort.
- Pill ↔ table parity holds per-cohort (smoked exact 9/9 on this branch, 2026-07-13).
- Open backlog as-of-now ≈ **1.6k** (re-derive at smoke time; the tab badge, the band headline,
  and `GET /dashboard/backlog` total must agree — same number, three renders).
- Zero-state is not reachable on the snapshot (backlog ≈1.6k) — covered by unit tests only.

## 5. Constraints

- Frontend worktree `frontend/.worktrees/analytics-debug`, branch `fix/analytics-post-shipped`,
  continue on tip **40814c6**. Commits go in the submodule worktree, never the monorepo root.
  Pushes and submodule-pointer bumps are USER-GATED.
- Tests: vitest 4 + jsdom, `react-dom/client` `createRoot` + `act` from `react`,
  `// @vitest-environment jsdom` per DOM test file, `(globalThis as any).IS_REACT_ACT_ENVIRONMENT
  = true`, `vi.mock("next/image")` where PackingTable renders — **NO @testing-library**. Run via
  `npx vitest run` (`npm test`); suite is GREEN at base (99/99) — it must stay green.
- Lint: `npm run lint` baseline is **36 pre-existing issues** — new code must add zero; do not
  chase the baseline. `npm run build` must pass (TS strict is the fixture-drift net).
- Tailwind v4 — every new colour style needs a `dark:` variant. Numbers via `toLocaleString()`.
- The worktree carries the uncommitted **DIAGNOSTIC(bkk)** patch in `app/lib/dateWindow.ts`
  (Bangkok-pinned `todayStr()`/`dateRange()`, marked "never commit"). Import and call freely;
  NEVER commit it — `git add` named files only, never `-A`; check `git diff --cached` first.
- Existing test files carrying expectations that MUST move with the design:
  `CarryoverDrilldown.test.tsx` (cohortDef + chip tests go), `PipelineSection.drilldown.test.tsx`
  (def-copy / "Updated this period:" assertions go), `BacklogSection.test.tsx` (collapsed→expand
  toggle test becomes always-expanded), `BacklogSection.cancel.test.tsx` (toggle click removed;
  summary arrives via props). `PipelineStage.carryover.test.tsx` is unaffected.
- `graphify update .` is currently broken (`NameError: name '_os' is not defined`) — skip it.

## 6. Out of scope

- Backend list-row `shippedAt` / video-completed timestamp fields — the `~ updatedAt` proxy and
  `time n/a` are accepted for this build; adding the fields is an optional post-merge enhancement
  (record in the release ledger, not here).
- Cancel-flow behaviour changes — just fixed (`40814c6`); it renders inside the tab verbatim.
- `/backlog` route, shareable URL, or `#backlog` deep link; `DashboardShell` extraction.
- Dashboard header changes — the h1 + subtitle stay; the header-tab placement (declutter mockup
  v4.2 §1) is superseded by the card tabs.
- Making the FilterBar tab-aware (hide/disable on the Backlog view) — flagged UX nit (§B.10),
  post-merge polish if the user wants it.
- WS piggyback / polling for the tab badge; live count updates.
- Standing a11y user-decision list (nested-interactive carryover pill, InfoTip tab stop,
  CohortFilters select labels) and a full WAI-ARIA tabs pattern for the new tab row.
- Band-level "oldest submitted" for carryover (superseded by per-row overnight stage —
  research §1.3 stands if it returns).
- `useCarryoverParcels` shipping/status param cleanup (dead from the drill-down, harmless).
- Funnel-copy rewording; leaderboard post-ship fix; post-merge minors batch from the Open
  Backlog final review; desktop app (MAUI) changes.
