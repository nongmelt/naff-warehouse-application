# Spec: Open Backlog view + Carryover drill-down tweaks

Date: 2026-07-14. Decisions grilled with the user this session. Layers directly on top of the
carryover drill-down build (`2026-07-13-carryover-drilldown.md`, 12/12 tasks complete, final
review Ready-to-merge). Touches BOTH submodules: backend worktree
`backend/.worktrees/analytics-debug` and frontend worktree `frontend/.worktrees/analytics-debug`,
branch `fix/analytics-post-shipped` in each (tips at spec time: BE **f809089**, FE **6b635ee**,
both UNPUSHED). New commits continue on the same branch — one feature line, one review unit.

## 1. Problem

Three follow-ups from the drill-down live review, plus one structural gap:

1. **Small drill-down tweaks**: Submitted At deserves visual emphasis in the carryover table
   (every row is by definition pre-window); the `× close` control is small and wordy; the
   cohort table cannot be narrowed by shipping option or by current status.
2. **The video pill disagrees with its table**: pill shows 107 (counts completed *videos*,
   `dashboard.rs` carryover block — `COUNT(*)` over `packing_videos`), table shows 98
   (distinct *parcels*). Nine parcels have ≥2 completed videos in the Friday window. Spec
   §2.7a of the prior build documented this; user now wants it fixed.
3. **The stage pills read as a funnel and are not one.** User read Friday's "218 carryover ·
   QC 57 · packed 66 · shipped 216" as "161 parcels skipped QC". Reality: each pill counts
   *that stage's event inside the window* — a parcel QC'd Thursday and shipped Friday appears
   under Shipped only. Deeper: carryover membership *requires* an in-window event, so a parcel
   with **no** activity appears in **no** window's carryover — the dashboard structurally
   cannot show stuck parcels.
4. **Stuck parcels are invisible and unmanageable.** Orders cancelled on the platforms
   (Shopee/Lazada/TikTok) are not synced automatically; their parcels sit unshipped forever.
   Snapshot reality: 125,069 of 145,477 rows look "open" by event-time, but ~89k are
   pre-Shipping-mode legacy (physically shipped before `shipped_at` existed — column added
   2026-06-18, Shipping scan mode live 2026-07-03). Post-go-live open set ≈ **1,660 parcels**.
   The user needs to see them, know which stage they stopped at, how long they have sat, and
   manually cancel the platform-cancelled ones in bulk.

## 2. Decisions (locked)

### A. Carryover drill-down tweaks

1. **Submitted At highlight — colour only.** In the carryover drill-down table (and the new
   backlog table), the Submitted At `<td>` renders amber (`text-amber-700 dark:text-amber-400`,
   semibold); no relative-age suffix, no column reorder, breakpoint hiding unchanged. The main
   dashboard table is unchanged. Mechanism: `PackingTable` gains an opt-in boolean prop
   `highlightSubmittedAt` (default false); the drill-down and backlog instances pass `true`.
2. **Close control — bare `×`, bigger.** `CarryoverDrilldown.tsx:98` copy changes from
   `× close` to `×` alone, sized up (`text-2xl leading-none`), padded hit target ≥ 40px
   square (`px-3 py-1.5` or equivalent), `aria-label="Close"`. Same placement (`ml-auto`),
   same `data-drilldown-close` hook for tests. The new backlog panel reuses the same control.
3. **Shipping-options filter — table-only dropdown.** A `<select>` to the right of the cohort
   chips row, options = `All shipping` + `available_shipping_options` from the current list
   response (so options reflect the active cohort). Selecting sets the existing
   `shippingOption` wire param on `/packing-lists/list` (bind `$12`; `'Unknown'` matches
   NULL/empty per the existing COALESCE). Filters the table rows and its `{total}` only —
   amber pills and cohort chips are NOT re-queried (pill parity preserved). Resets to `All`
   on cohort switch and on panel close. Reused verbatim in the backlog table.
4. **Status filter — same pattern.** A second `<select>` beside the shipping one:
   `All statuses` + the `StatusFilter` union values (`To be packed`, `Packing`, `Packed`,
   `QC Hold`, `QC Passed`, `Shipped`, `Returned`) riding the existing `status` wire param
   (bind `$2`; note the endpoint's special-case for `To be packed` matching NULL). Table-only,
   resets on cohort switch. Answers "of Friday's 218, which are still not shipped NOW?" inline.
   NOTE: `QC Hold` / `QC Passed` are UI filter values — confirm how the endpoint's `$2`
   equality treats them (main dashboard passes these same values today via `useDashboard`;
   reuse whatever mapping it uses — behaviour parity with the main table is the requirement).
5. **Video cohort counts distinct parcels; raw video count moves to the def line.**
   - BE: in the `dashboard.rs` carryover block, `completed_videos` becomes the **distinct
     parcel count** whose predicate is IDENTICAL to the `carryover-video` itemType arm
     (`pl.created_at < from` AND EXISTS completed video with
     `COALESCE(pv.updated_at, pv.created_at)` in window) — pill ↔ table parity becomes exact.
   - BE: a NEW field `video_events` (wire `videoEvents`) carries the old raw video-row count.
   - BE: the carryover per-platform video counts (`by_platform[].completed_videos`, fed by the
     `co_video_rows` query) switch to the SAME distinct-parcel semantics, so the video pill's
     expandable platform breakdown sums to the pill headline.
   - The **window** (non-carryover) `completed_videos` stat is UNCHANGED — it still counts
     videos. Split semantics are deliberate (user decision): carryover = parcels, window = videos.
   - FE: `CarryoverBreakdown` type gains `videoEvents: number`; `cohortCount("video")` keeps
     reading `completedVideos` (now parcels — pill, chip, and table all agree); the video def
     string becomes:
     `of the {total}, these had a packing video completed in this period · {videoEvents} videos`
   - Release-comms ledger gains one line: carryover video pill semantics changed (107 → 98 on
     the Friday oracle).

### B. Open Backlog view (new feature)

6. **Definition — event-time, watermarked, as-of-now.** A parcel is *open backlog* iff:
   - `created_at >= BACKLOG_WATERMARK` — backend constant, `2026-07-03 00:00 Bangkok`
     (= `2026-07-02T17:00:00Z`), the Shipping-mode go-live. Legacy rows (89k status-Packed
     parcels that physically shipped untracked) can never appear. Changing the watermark
     later = 1-line edit + redeploy; a Settings-UI knob is explicitly deferred.
   - `created_at <` **start of today, Bangkok** — supplied by the FE as a `cutoff` param
     derived through `app/lib/dateWindow.ts` (keeps all Bangkok-boundary logic in one place).
     Today's normal WIP is excluded.
   - `shipped_at IS NULL AND returned_at IS NULL` — event-time membership, NOT status strings.
   - `order_status IS DISTINCT FROM 'Cancelled'` — canonical value; snapshot verified the
     only cancel spelling present is exactly `Cancelled` (import sweep + upsert both stamp it).
   - `packing_status IS DISTINCT FROM 'Cancelled'` — excludes legacy manual stale-cancels.
   The backlog is **independent of the dashboard date filter** — always "as of now".
7. **Stage grouping — event timestamps, mutually exclusive, exhaustive:**
   - `packed`: `packed_at IS NOT NULL`
   - `qc-passed`: `packed_at IS NULL AND checked_at IS NOT NULL AND all_items_cleared IS TRUE`
   - `qc-hold`: `packed_at IS NULL AND checked_at IS NOT NULL AND all_items_cleared IS NOT TRUE`
   - `submitted`: `checked_at IS NULL AND packed_at IS NULL`
   (Status-string `Packing` rows fall wherever their timestamps put them — usually
   `submitted`. `all_items_cleared IS NOT TRUE` folds NULL into hold so the partition sums
   to the total.)
8. **API mirrors the carryover pattern.**
   - Five new `itemType` wire values on `GET /packing-lists/list`: `backlog`,
     `backlog-submitted`, `backlog-qc-hold`, `backlog-qc-passed`, `backlog-packed` — added to
     BOTH match blocks (aliased `type_clause` and unaliased `type_clause_count`). Each arm
     encodes decision 6's membership (using `$3`/`from` as the start-of-today cutoff:
     `created_at < $3`, watermark as an inline constant) plus decision 7's stage predicate.
   - The all-carryover `updated_at` window suppression (spec 2026-07-13 §2.7) extends to the
     backlog family: when every selected type is carryover-family OR backlog-family, the base
     `updated_at` window is suppressed. (FE only ever sends one itemType at a time here.)
   - New `GET /dashboard/backlog?cutoff=<ISO>` returns
     `{ total, byStage: { submitted, qcHold, qcPassed, packed }, oldestCreatedAt }` — one
     grouped query over the same membership predicate. `oldestCreatedAt` = min created_at of
     the open set (null when empty).
9. **UI — red-accent BacklogSection on `/`, collapsed by default.** New
   `app/components/pipeline/BacklogSection.tsx` rendered by `Dashboard.tsx` between
   `PipelineSection` (line ~95) and the main `PackingTable` (line ~135). Visual language
   clones `CarryoverDrilldown` but in the red family (`#E11D48` rose-600 top bar / eyebrow,
   `dark:` variants throughout) so it reads "overdue", never amber (carryover's colour).
   - Collapsed: one summary line — eyebrow `Open backlog`, headline `{total}` parcels,
     `oldest {date}`, expand chevron. Data from `GET /dashboard/backlog` on mount and after
     every cancel action.
   - Expanded: stage chips `All · {n}` / `Submitted · {n}` / `QC Hold · {n}` /
     `QC Passed · {n}` / `Packed · {n}` (counts from the summary endpoint), active chip
     drives an inline `PackingTable` fed by `/packing-lists/list?itemType=backlog[-stage]`,
     default sort `createdAt asc` (oldest first), limit 10, shipping + status dropdowns
     (decisions 3–4), `highlightSubmittedAt`, and the Age column + row selection (10–11).
   - A new `useBacklogParcels` hook mirrors `useCarryoverParcels` (including the res.ok
     guard and abort-aware loading from decision 13).
10. **Age column — opt-in, one column, not sortable.** `PackingTable` gains boolean prop
    `showAge` (default false; backlog instance passes true): one extra column `Age`, cell
    renders `"{age}d · idle {idle}d"` where age = now − `createdAt`, idle = now −
    `max(createdAt, checkedAt, packedAt)` (all FE-computed; <1d renders `<1d`). Not
    sortable — the default `createdAt asc` sort already orders by age. The columns test
    (`PackingTable.columns.test.tsx`) is extended to cover both prop states.
11. **Manual cancel — checkbox multi-select + bulk bar + confirm dialog.**
    - `PackingTable` gains opt-in selection props (`selectable`, `selected: Set<number>`
      keyed by `packingId`, `onToggleRow`, `onToggleAll`): a leading checkbox column +
      select-all-on-page header checkbox. Only the backlog instance enables it. Row-click
      still opens the detail panel; checkbox clicks `stopPropagation()`.
    - When ≥1 selected, an action bar appears in the panel between chips and table:
      `Cancel {n} parcels` (red, rose family; n = selected parcel count) + `Clear selection`.
    - Confirm dialog: shows parcel count and distinct order-number count, an operator
      `<select>` (from `useOperators`), an optional note field, and warns verbatim:
      `Cancelling marks the whole order as cancelled on every parcel, including any already
      shipped. This cannot be undone here.` Confirm POSTs
      `/dashboard/alerts/backlog/resolve` `{ trackingNumbers, action: "cancel", operator,
      note }`; on success clears selection and refetches BOTH the summary and the table.
12. **Cancel semantics — `order_status` only, whole-order cascade.** New arm
    `("backlog", "cancel")` in `resolve_alert` (`dashboard.rs:878`):
    - Guard per tracking number: the SELECTED parcel must exist and have
      `packing_status NOT IN ('Shipped','Returned')` — else skip silently (no event, not
      counted), mirroring the stale/cancel arm's race semantics.
    - Stamp: `UPDATE packing_lists SET order_status = 'Cancelled', updated_at = NOW()
      WHERE order_number = (SELECT order_number FROM packing_lists WHERE tracking_number = $1)`
      — the WHOLE order, unconditionally across sibling parcels (mirrors the import sweep;
      an already-shipped sibling becoming cancelled-but-shipped is the existing QC-alert
      scenario and is handled there). `packing_status` is NOT touched (user decision —
      differs from the stale/cancel arm).
    - One `workflow_events` row per resolved tracking number (`AlertResolve` / `backlog` /
      `cancel` / operator / note payload) — the existing INSERT at the bottom of the loop
      covers this; no `alert_dismissals` interplay (backlog is not a dismissable alert type).
    - Response: existing `{ resolved }` count.
13. **Carried-in review fixes (all 4 + 3 optionals)** — from the 2026-07-13 final review:
    - `useCarryoverParcels.ts:78` — `res.ok` guard; non-OK → treat as error, surface an
      inline error state in the drill-down (small red text row: `Couldn't load parcels —
      retry`) instead of silently rendering `0 total` under a `218` header.
    - `PipelineSection.tsx` — gate `onCarryoverClick={co && fromDate && toDate ? … : undefined}`.
    - Hook `finally` — `setLoading(false)` only when `!controller.signal.aborted`.
    - BE `tests/packing_list_types.rs` `all_carryover_selection_ignores_updated_at_window` —
      add the 1-line `items` length assert so the rows-sql `{win_pl}` path is exercised.
    - Optionals: clear stale rows on cohort switch (kill the previous-cohort flash); BE
      itemType `.trim()` on split values; rationale comments on the two eslint-disables.
14. **Migration — one new stacked file** (NEVER edit applied migrations):
    `migrations/20260714<hhmmss>_open_backlog_index.sql` —
    `CREATE INDEX idx_packing_lists_open_backlog ON packing_lists (created_at)
    WHERE shipped_at IS NULL AND returned_at IS NULL;`
    Partial index serves both the summary count and the list arms. Shared dev DB has an
    orphan migration row — always `sqlx migrate run --ignore-missing`.

## 3. Phasing / sequencing

- **Phase 1 — backend** (worktree `backend/.worktrees/analytics-debug`): migration; video
  distinct-parcel count + `videoEvents`; backlog itemType arms + suppression extension;
  `/dashboard/backlog` summary; `("backlog","cancel")` resolve arm; carried-in BE test assert.
- **Phase 2 — frontend** (worktree `frontend/.worktrees/analytics-debug`): carried-in hook/
  section fixes; drill-down tweaks (×, amber Submitted At, shipping+status dropdowns, video
  def line); PackingTable props (highlight, Age, selection); `useBacklogParcels` +
  `BacklogSection` + cancel dialog; Dashboard wiring.
- **Deploy together:** old BE drops unknown `backlog*` itemTypes silently → full-window list
  under backlog headers; and FE reading `videoEvents` needs the new summary field. Same
  one-release rule as the drill-down build.

## 4. Oracle numbers (warehouse_snapshot, queried 2026-07-14)

- Cancel-string audit: the ONLY cancel spelling in `order_status` is canonical `Cancelled` —
  exact-match exclusion is safe.
- Open backlog, EXACT event-time oracle (watermark `2026-07-02T17:00:00Z`, cutoff
  `2026-07-13T17:00:00Z`, i.e. start of 14 Jul Bangkok, computed 2026-07-14 by the
  verification pass): total **1,660** = submitted 95 + qc-hold 4 + qc-passed 33 + packed
  1,528 (partition sums exactly); `oldestCreatedAt = 2026-07-03T01:13:02Z`. Live smoke runs
  on a later cutoff, so re-derive with the same SQL — the shape (≈1.6k, packed-dominated)
  must hold, not the exact figures.
- Without the watermark the open set is 125,069 of 145,477 rows (mostly March–June legacy) —
  the watermark is load-bearing; assert the headline is ~1.6k, not ~125k.
- Friday window carryover (unchanged oracle): All 218 / QC 57 (55+2) / Packed 66 / Shipped 216.
  After decision 5: video pill = video chip = video table total = **98**; def line shows
  `· 107 videos`.
- Cutoff sanity: parcels created today (Bangkok) never appear in backlog regardless of state.

## 5. Constraints

- Backend: integration tests in `tests/` against real Postgres (`spawn_app()`, disjoint
  tracking prefixes + cleanup); `cargo test --no-fail-fast`. PRE-EXISTING failures at base
  a452a0c (do NOT chase): dashboard_api `resolve_stale_cancel`, `resolve_packing_video_accept`,
  `resolve_packing_video_accept_backfills_packed_by_and_station`, `resolve_audit_trail_fields`;
  3 leaderboard; 1 product_insights; import_trace 1; product_images 5 (MinIO env);
  warehouse_invoice 1. New/changed tests must be green. NOTE: the four failing resolve tests
  overlap the file the new cancel arm's test lands in — the new test must pass in isolation
  (`cargo test resolve_backlog`), and the pre-existing four must not change verdict.
- Frontend: vitest + jsdom + `react-dom/client` `createRoot` (NO @testing-library),
  `// @vitest-environment jsdom` per file, `vi.mock("next/image")` where needed. Tailwind v4 —
  every new style needs a `dark:` variant. Numbers via `toLocaleString()`.
- The FE worktree carries the uncommitted DIAGNOSTIC(bkk) patch (`app/lib/dateWindow.ts`).
  NEVER commit it; `git add` named files only, never `-A`; verify `git diff --cached`.
- Commits go in each submodule worktree, never the monorepo root. Pushes and
  submodule-pointer bumps are USER-GATED.
- Migration discipline: stack new files only; `warehouse_db_test` takes
  `sqlx migrate run --ignore-missing` normally. `warehouse_snapshot` must NEVER be
  sqlx-migrated: its `_sqlx_migrations` checksums mismatch every current file (restored
  dump) and sqlx fails checksum validation regardless of `--ignore-missing` — create any
  needed index there directly via psql (`CREATE INDEX IF NOT EXISTS …`).
- `graphify update .` is currently broken (`NameError: name '_os' is not defined`) — skip it.

## 6. Out of scope

- A11y spec-locked items from the drill-down build (nested-interactive pill, InfoTip tab
  stop) — unchanged, user-decision items.
- PR/merge target for the analytics series — still an open user decision.
- Funnel-comprehension copy/InfoTip rewording — the backlog view itself answers the misread.
- Settings-UI watermark knob; automatic platform sync; un-cancel/undo; WebSocket live
  backlog updates; desktop app (MAUI) changes; export of the backlog list.
- Changing the WINDOW video stat semantics (still counts videos — only carryover changes).
- Retroactive data migration of legacy rows — the watermark makes them invisible instead.
