# Spec: "Not invoiced" packing-list type filter

**Status:** build-ready
**Base:** builds atop `feat/warehouse-invoice` (backend PR [#53](https://github.com/nongmelt/naff-warehouse-backend/pull/53) head `f248e9c`, frontend PR [#50](https://github.com/nongmelt/naff-warehouse-frontend/pull/50) head `eccacb0`).
**Origin:** post-10.7-test tweak, session 2026-07-10. Two sibling requests were dropped by the user during design: a QC Passed export condition (`all_items_cleared`/`checked_at`), and an invoice-export dedupe (skip already-invoiced parcels) — re-downloading already-invoiced windows must keep working.

## 1. Summary

One change: a new `itemType` value on `GET /packing-lists/list` plus its filter-bar option, matching every parcel not yet on an issued invoice (`invoiced_at IS NULL`). Exact complement of the existing `invoiced` type. Invoice export behavior is untouched.

## 2. Semantics (user-decided)

`invoiced_at IS NULL` — plain, no packed/shipped precondition. Composable with the Status filter (e.g. Status=Shipped + Type=Not invoiced = invoice-ready backlog). Types OR-combine as today; selecting Invoiced + Not invoiced legitimately matches everything.

## 3. Backend (`src/api/packing_list.rs`)

New match arm in **both** type-clause builders, placed after `"invoiced"` (main query aliased `pl.`, count query unaliased):

```rust
"not-invoiced" => Some("(pl.invoiced_at IS NULL)".to_string()),   // main
"not-invoiced" => Some("(invoiced_at IS NULL)".to_string()),      // count
```

Wire value is `not-invoiced` (matches existing kebab `multi-parcel`). Unknown value on a stale backend stays a silent no-op — the existing soft version-skew contract.

## 4. Frontend

- `app/types.ts`: `TypeFilter` union adds `"not-invoiced"`.
- `app/components/FilterBar.tsx` `TYPE_OPTIONS`, inserted directly after Invoiced:

```ts
{ label: "Not invoiced", value: "not-invoiced", cls: "bg-slate-100 text-slate-700 dark:bg-slate-800/60 dark:text-slate-400" },
```

No other UI change; `TypeTagFilter` search/toggle handles the new entry for free.

## 5. Tests

- **Backend** — extend `tests/packing_list_types.rs` (live-DB harness; gate on this test file only):
  - `not-invoiced` returns the complement of `invoiced` within the same window;
  - combined `invoiced,not-invoiced` equals the no-type-filter row count.
- **Frontend** — vitest (repo-wide lint has 10 pre-existing errors; gate on touched files):
  - `FilterBar.types.test.tsx` option-set lock updated: values/labels arrays include `not-invoiced` / `Not invoiced` after Invoiced (toggle behavior is generic `TypeTagFilter` code, unchanged — not re-tested).

## 6. Constraints

- Lands on the `feat/warehouse-invoice` stacks in both submodules (worktrees `backend/.worktrees/warehouse-invoice`, `frontend/.worktrees/warehouse-invoice`); commits go to the submodule repos, PRs #53/#50.
- Backend `cargo build`/test needs a live migrated `DATABASE_URL` (no `.sqlx` cache on this branch). Never `sqlx migrate run` against the shared dev DB without `--ignore-missing`.
- No migration; no schema change.
- Deploy both repos together as usual; skew fails soft per §3.

## 7. Out of scope

- QC Passed export condition (dropped by user).
- Invoice-export dedupe / `invoicedExcluded` preview count (dropped by user — already-invoiced parcels must remain re-downloadable).
- Orders export — untouched.
