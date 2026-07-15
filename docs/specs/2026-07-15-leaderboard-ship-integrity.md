# Spec: Leaderboard post-Ship integrity — event-time counting, unregistered sidecar, single-day picker, live-cache revalidation

**Status:** build-ready
**Base:** `feat/warehouse-invoice` worktrees — `backend/.worktrees/warehouse-invoice` (@ `6678c5e`, PR [naff-warehouse-backend#53](https://github.com/nongmelt/naff-warehouse-backend/pull/53)) and `frontend/.worktrees/warehouse-invoice` (@ `b1a10c5`, PR [naff-warehouse-frontend#50](https://github.com/nongmelt/naff-warehouse-frontend/pull/50)).
**Origin:** wayfinder map [#68](https://github.com/nongmelt/naff-warehouse-application/issues/68) (tickets #69 quantification, #70 unregistered grilling, #71 day-picker prototype). Line refs are at the base commits above.

## 1. Problem

Every packing-discipline aggregate in `src/api/leaderboard.rs` gates on `packing_status = 'Packed'`. Ship/return scans rewrite that column (preserving `packed_at`/`packed_by`), so packed work retroactively drains from both leaderboard pages. Dev-DB quantification (#69): **76% of last-7-days and 99.5% of all-time timestamped packed work sits `Shipped` and is invisible**. The dashboard had the same bug and was fixed by ADR-0003 event-time counting; its spec (2026-07-11 §7) explicitly deferred the leaderboard — this spec is that deferred fix, plus the decisions grilled on map #68.

## 2. Feature A — event-time counting (ADR-0003 parity)

### 2.1 Fix shape (locked)

Replace `packing_status = 'Packed'` with **`packed_at IS NOT NULL`** at every site. ADR-0003 explicitly rejected status-set widening (`IN ('Packed','Shipped','Returned')`); the event-time gate matches `stage_window()`'s first conjunct — `dashboard.rs:158` at this base (`:218` on the sibling `fix/analytics-post-shipped` branch). The existing null-safe window bounds (`($1::timestamptz IS NULL OR pl.packed_at >= $1) AND (...)`) stay unchanged. Packed-event counting is **immutable**: later ship/return transitions never change tallies; Returned parcels still count (locked at charting).

`stage_window()` is module-private to `dashboard.rs` — do **not** promote it; edit the leaderboard predicates in place (they already inline the bounds half at 4 sites).

### 2.2 Gate sites (all 8, verified at base)

| # | Site | Line |
|---|------|------|
| 1 | operator packing agg | `leaderboard.rs:171` |
| 2 | `key_filter` (operators, packing) → `"packed_by IS NOT NULL AND packed_at IS NOT NULL"` | `:318` |
| 3 | `key_filter` (stations, packing) | `:323` |
| 4 | `videos_map` | `:434` |
| 5 | `videoed_parcels_map` | `:469` |
| 6 | `daily14_map` (operators, packing) | `:711` |
| 7 | `daily14_map` (stations, packing) | `:721` |
| 8 | station packing agg | `:808` |

Sites 2/3 automatically fix the three inheriting queries in `GET /leaderboard/operator` (totals `:619`, by_platform `:655`, trend `:669`) and `breakdown_map` (`:354-368`) — no separate edits there. QC queries have **no status gate anywhere** (verified `:179-194, :320, :325-327, :713-717, :723-727, :814-832`): zero QC changes. The no-video ship backfill (`packing.rs` stamps `packed_at = shipped_at`, `packed_by = shipped_by` via COALESCE) is accepted as-is — those rows count for the shipper.

### 2.3 NULL-`packed_at` semantics (pinned)

Legacy rows without `packed_at` (dev DB: 770 `Shipped` + 70 `Packed`-status rows) become invisible in **every** window including all-time — intended: no packed event was recorded. Consequences to leave as-is, now provably inert for the gated set: all five packing-side `COALESCE(packed_at, created_at)` fallbacks (`:155, :639` via `{win}`, `:710, :720, :790`) only ever see non-NULL `packed_at` post-fix — **do not refactor them** (minimal diff); QC analogs untouched.

## 3. Feature B — unregistered-operator sidecar (ticket #70, Option C)

The registered-only `EXISTS operator_lists` filter **stays** (`:172` packing, `:190` QC — the only two sites; stations/drill never had it, leave them). New sidecar so hidden work is observable:

### 3.1 Backend

`LeaderboardResponse` (`:21-25`) gains an optional field, populated in `leaderboard()` (`:106-111`) for `scope=operators` only (both disciplines; `null`/omitted for stations):

```json
"unregistered": { "parcels": 17, "codes": [{ "code": "OP-1", "count": 13 }] }
```

One extra query per request: `COUNT(*)` + `GROUP BY packed_by` (resp. `checked_by`) with **the same WHERE as the operator agg minus the EXISTS** — i.e. including `packed_by IS NOT NULL` (`:170`) and the event-time gate + window — plus `NOT EXISTS (SELECT 1 FROM operator_lists o WHERE o.staff_code = pl.packed_by)`. (Without the `packed_by IS NOT NULL` conjunct, NULL-packer rows pass `NOT EXISTS` and break the `String` key decode.) Codes sorted by count desc. Serde: `#[serde(skip_serializing_if = "Option::is_none")]`, camelCase like siblings.

### 3.2 Frontend

`types.ts`: `LeaderboardApiResponse` gains `unregistered?: { parcels: number; codes: { code: string; count: number }[] }`; thread it through `fetchBoard`/`useLeaderboard`. The cache entry becomes `{ rows, unregistered }` **in the existing single Map** (`boardKey` unchanged) — do not add a parallel map: the `rangeToken`/`weightToken` clears only wipe `cache`, so a second map keyed by `boardKey` would leak an entry per today-tick re-pin.

One muted line under the operator rankings, gated `scope === 'operators'`, absent when `parcels === 0`:

- **Live page**: in `LiveBoard.tsx` after the rows `</div>` (`:122`), before `</section>` — outside the FLIP container (`boardRef` animates `[data-key]` children, `:50`).
- **Settled page**: in `LeaderboardTab.tsx` directly after `<RankCards …/>` (`:126`) — not inside `RankCards` (it returns `null` on zero rows, `:18`).

Copy: `"{parcels} parcels by {codes.length} unregistered codes — hidden from board"` (the #70-decided string); click/hover expands per-code counts (raw staff codes appear only there). No chart, no panel.

## 4. Feature C — single-day picker (ticket #71, V1)

Mockup: `docs/mockups/2026-07-15-leaderboard-day-picker-v1-v2-v3.html` (V1 selected). Backend: **no changes** — `from`/`to` already arbitrary.

- `leaderboardWindow.ts`: `ShellPeriod` union gains `"day"` (`:9`); `resolveShellRange` gains a `day` branch — `startISO`/`endISO` of `v.anchor` (the `custom` branch with from = to = anchor is the template, `:48-53`). `prevShellRange` already yields the previous day generically (`:63-72`) — no change.
- `PeriodBar.tsx`: `LABELS` gains `day: "Day"` (`:8-15`, TS forces this); a `value.period === "day"` block (parallel to custom, `:80-85`) rendering **one** `DatePicker` bound to `anchor` (`onSelectDate={(d) => set({ anchor: d })}`) plus net-new ‹ › stepper buttons (`anchor ± 1 day` via `toLocalDateStr`; **› clamps at today**, matching `DatePicker`'s future-block, `DatePicker.tsx:227,233-234`). No stepper exists in either picker today — build them in `PeriodBar`.
- Chips: Live `["today","day","week","month","year"]` (`LiveLeaderboard.tsx:139`); settled `["day","week","month","year","custom"]` (`LeaderboardTab.tsx:80`). Custom free-range chip untouched.
- Labels: Live `periodLabel` (`:69-75`) gains day cases — anchor = today → `"Live · 15 Jul"`, past → `"Fri 3 Jul · static"`. The grey static pill needs a `PeriodBar` change too: the `rightLabel` wrapper hardcodes brand colors (`PeriodBar.tsx:95-99`) — add a `rightLabelTone?: 'brand' | 'static'` prop (default `'brand'`). Settled label (`LeaderboardTab.tsx:58-71`) renders `"Fri 3 Jul 2026"`.
- The range control stays **page-level** (locked in #71): one `PeriodBar` above the operators|stations scopes and both disciplines on each page — do not introduce per-scope/per-discipline controls during the picker work.
- Deltas: settled board day window gets deltas vs previous calendar day for free (`prevShellRange` + existing second fetch in `fetchBoard`, `leaderboardCache.ts:60-64`). Live board keeps `prevRange = undefined` (no deltas).
- Live semantics: day = today → board stays live via Feature D's revalidation (the fixed `[startOfDay, endOfDay]` window includes new events as they arrive); past day → static (revalidation refetches are no-ops on immutable history; acceptable).
- Timezone: browser-local day boundaries, consistent with every existing period (`insightsWindow.ts:1-3` documents the accepted local-vs-Bangkok skew). This deliberately overrides the `(Asia/Bangkok)` parenthetical in #71's resolution comment — that aside was wrong about the existing resolver, which is browser-local; #71's "consistent with existing window resolution" clause wins.

## 5. Feature D — live-board cache revalidation (fog item, decided here)

**Today:** `leaderboardCache.ts` is a module `Map` with no TTL; `boardKey = d|s|from|to`. Week/month/year windows resolve to byte-identical `from`/`to`, so `useLeaderboard`'s effect never re-runs and line `:39`'s cache-hit guard skips fetching — a mounted non-today board is stale forever. Only `today` re-pins (its `to = now()` changes per tick), and each re-pin's `cacheEnsureRange` **clears the entire Map** module-wide.

**Mechanism (decided): WS-keyed revision, stale-while-revalidate.**

- `leaderboardCache.ts` gains a module revision counter with `subscribe`/`getSnapshot`/`cacheBump()` (for `useSyncExternalStore`), and **every cache entry is revision-stamped at `cacheSet`**. An entry is *stale* when its stamp predates the current revision — checked on **every** effect run, not only on bumps. (Bump-only refetching leaves a hole: the sibling-discipline prefetch, `useLeaderboard.ts:51-58`, caches the other discipline at mount; on a later discipline switch the `:39` cache-hit guard would serve those hours-old rows without refetching. Stamping closes it.)
- `useLeaderboard` subscribes; revision joins the effect deps. When the current key is missing **or stale**: refetch (and its prev-window, as `fetchBoard` already does), `cacheSet` overwrite, swap rows on arrival — stale rows stay visible meanwhile. The sibling prefetch guard (`:54`) applies the same missing-or-stale test. No `Map.clear()` on bump.
- Producers: `LiveLeaderboard`'s existing ~500 ms-debounced WS handler + 10 s fallback interval (`:78-91`) call `cacheBump()` (they already drive the tick). The settled page adds the same `usePackingSocket`-driven debounced bump in `LeaderboardTab` (it has no WS today) — week/month/year/day/custom boards then update while mounted on both pages.
- `rangeToken`/`weightToken` full-clears stay as-is (they bound Map growth from today's re-pinning keys and invalidate weight-dependent computed rows).

## 6. Bundled minors

- **(a) `holds_by_status` terminal labels** (`:493-532`; **QC-discipline-only** — keyed on `checked_by`/`checking_station_id`, windowed on `checked_at`; packing rows hardcode `holds = 0` at `:153`): the bucket label is the row's *current* status, so force-shipped/returned never-cleared parcels surface as `Shipped`/`Returned` "holds". Fix in SQL (`:506`): `CASE WHEN packing_status IN ('Shipped','Returned') THEN 'Force-shipped' ELSE COALESCE(NULLIF(btrim(packing_status),''),'Unknown') END` — one honest bucket, no schema change.
- **(b) `daily14` timezone off-by-one** (`:733,:737,:752`): `days_ago` mixes session-TZ `CURRENT_DATE` with Bangkok day buckets — before 07:00 Bangkok, today's rows compute `days_ago = -1` and are dropped by the `:752` guard. Fix: `days_ago = ((now() AT TIME ZONE 'Asia/Bangkok')::date - (ts AT TIME ZONE 'Asia/Bangkok')::date)::int` and window `(ts AT TIME ZONE 'Asia/Bangkok')::date >= (now() AT TIME ZONE 'Asia/Bangkok')::date - 13`. `daily14` ignoring the page's from/to stays **by design** (rolling 14-day sparkline).
- **(c) coverage > 100%** (FE-only): `lib/leaderboard.ts:70` passes `row.videos` (file count) where the formula needs `row.videoedParcels` — one-token fix; `DrillPanel.tsx:348` already does it right (board row and its own drilldown currently disagree).

## 7. Tests

Backend — extend `tests/leaderboard_api.rs` (copy its `spawn_app` + prefix-cleanup pattern; live `DATABASE_URL` required; run `cargo test --no-fail-fast`):

- **Repair the 3 pre-existing failures** (`:243, :281, :358` — fixtures never register their operators; the EXISTS filter added in `6cf8fe8` one day after the tests): shorten fixture codes to ≤ 15 chars (**`operator_lists.staff_code` is `VARCHAR(15)`**; current codes are 17–19 chars) and register them via the existing `INSERT INTO operator_lists … ON CONFLICT DO NOTHING` helper pattern (`:379`). Test 1's staff-code-fallback assertions (`:249-250`) assert the pre-filter contract — rewrite to registered-name expectations.
- **Shipped/Returned coverage (zero exists today)**: pack → ship a parcel, assert it still counts in the packed window (operator + station scopes); pack → return likewise; drill endpoint (`/leaderboard/operator`) totals/trend include the shipped parcel; two adjacent windows return event-time-correct counts (the FE delta oracle).
- **Unregistered sidecar**: counts + per-code list + sort, zero-state omission, window-scoping, QC discipline (`checked_by`), absent for `scope=stations`. **Isolation**: `unregistered.parcels` is window-global and cannot be prefix-isolated on the shared DB (organic `OP-1` rows + leaked fixtures from the 3 panicking tests exist) — assert fixture codes inside `codes[]` only for recent windows; use a far-past window (organically empty) for exact totals and the zero-state case.
- **Minors**: force-shipped uncleared parcel buckets as `Force-shipped` — QC-side fixture required: `checked_by` + `checked_at` in window, `all_items_cleared = false`, `packing_status = 'Shipped'`, QC operator registered, query `discipline=qc`. daily14 slot-0 counts a row packed "today Bangkok" (seed via `now()`), no negative-days drop — note: only red pre-fix during 17:00–24:00 UTC (00:00–07:00 Bangkok); fine as regression coverage, don't rely on red-green at other hours.

Frontend — vitest (suite exists on this branch). **Gotcha:** `vitest.config.ts` includes only `app/ship/**`, `app/hooks/**`, `app/components/**` — `app/lib` is deliberately excluded (its existing `*.test.ts` use `node:test`, which has no npm script). For the window/coverage tests below, **extend the vitest `include` with the two new files** `app/lib/leaderboardWindow.test.ts` and `app/lib/leaderboard.test.ts` (vitest-style; leave the node:test files excluded):

- `resolveShellRange("day")` window + `prevShellRange` previous-day; `PeriodBar` day chip reveals one DatePicker + steppers, › clamped at today; chips arrays both pages.
- Unregistered line: renders count/codes, absent at zero, absent for stations scope.
- Coverage: `computeBoard` uses `videoedParcels` (≤ 100% with multi-file fixture).
- Cache: bump triggers refetch of mounted key; stale rows shown until swap.

## 8. Constraints

- Work in the **warehouse-invoice worktrees**; commits land in each submodule on `feat/warehouse-invoice` (per [[feedback_use-worktrees-for-branch-work]]). BE and FE changes are independently deployable (sidecar is additive; picker is FE-only) — no lockstep deploy.
- Backend tests need a live migrated `DATABASE_URL` (`warehouse_db_test`; shared dev DB needs `sqlx migrate run --ignore-missing`). **No `.sqlx` cache and no compile-time query macros exist** — builds don't need the DB; running tests does. Pre-existing failures on base: 3 leaderboard (fixed by this spec) + 1 `product_insights` (untouched) — use `--no-fail-fast`.
- `fix/analytics-post-shipped` (dashboard ADR-0003 implementation, BE `f26965f`) is a sibling branch atop the same base: no shared files with this spec's edits (`leaderboard.rs` untouched there); merge order free; trivial `types.ts` adjacency possible on FE.
- No DB migration; no schema change; no MAUI change.
- Dev-DB illustrative oracles (#69, 2026-07-15): last-7d packed = 17 (13 `Shipped` + 4 `Packed`); unregistered = `OP-1` ×35 all-time / ×13 last-7d (≡ the entire ship-backfill population); only 1 `Returned` row exists — **seed Returned fixtures**, don't rely on organic data.

## 9. Out of scope

- Shipping-scan work as its own leaderboard metric (`shipped_by` counted nowhere) — ruled out at charting.
- Leaderboard page redesign beyond the day picker + sidecar line; settled board's custom range chip stays.
- Adding the EXISTS filter to stations/drill/breakdown queries (never had it; station crew counts keep including unregistered packers).
- QC `avg_minutes_per_parcel` semantics (queue-latency in list vs video-derived in drill) — known divergence, untouched.
- Drill-endpoint holds vs list `holds = 0` mismatch for packing — untouched.
- Registering/migrating `OP-1` data — ops action via the new sidecar visibility.
- 3-failure seed/environment cleanup beyond what §7's repairs require.
