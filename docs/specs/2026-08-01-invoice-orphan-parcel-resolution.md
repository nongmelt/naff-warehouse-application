# Spec: Invoice export — orphaned parcel resolution

Date: 2026-08-01. Decisions locked with the user 2026-08-01 (systematic-debugging session on
"why can't `2607290MY5Y1U8` be exported to invoice file"). Backend-only. Worktree
`backend/.worktrees/line-fingerprint`, branch `fix/product-line-fingerprint`, base =
`feat/export-drawer`.

Resolves the export half of the dual defect described in
`docs/specs/2026-07-31-product-line-fingerprint.md` §8, tracked as backend issue **#69**. The
quantity half is already shipped as PR backend #76.

Supersedes issue #69's stated preference (option 2, "backfill `import_rows.tracking_number`").
See §3 for why that option is now closed.

## 1. Problem

Shopee Express and Instant Delivery orders arrive with an **empty** tracking column — Shopee has
not yet assigned a courier tracking number.

`packing_lists.tracking_number` is the parcel primary key and cannot be empty, so
`aggregator.rs:75-88` substitutes the **order number** as a placeholder. That substitution is
in-memory and affects `packing_lists` only. `import_rows.tracking_number` is a **generated
column** (`raw_data->>'tracking_number'`) and retains the raw empty string.

The invoice export joins the two tables on `tracking_number` (`invoices.rs:307`, `:317`, `:452`).
The two sides therefore disagree, the join matches nothing, and the parcel is silently dropped
from the workbook.

Confirmed for `2607290MY5Y1U8` against `warehouse_snapshot`:

| table | `tracking_number` | `order_number` |
|---|---|---|
| `packing_lists` (packing_id 170618) | `2607290MY5Y1U8` | `2607290MY5Y1U8` |
| `import_rows` (526243, 526244, batch 642) | `''` | `2607290MY5Y1U8` |

Reproduced by running the export's own predicate: **1 parcel selected, 0 rows returned**.

### 1.1 Vocabulary

Two populations, both orphaned by the same mechanism. Both terms are existing codebase
vocabulary (`aggregator.rs` comment; `tests/import_placeholder_promotion.rs`).

- **Placeholder parcel** — `packing_lists.tracking_number = order_number`, the invented value.
  `import_rows.tracking_number` is `''`. This is `2607290MY5Y1U8`'s group.
- **Promoted parcel** — Shopee later issued real tracking and a subsequent import updated
  `packing_lists.tracking_number` from the placeholder to the real `TH...` value. In almost all
  cases that later import also wrote a **new** `import_rows` row carrying the real tracking, so
  the join heals itself. A small residue (6 parcels) never got that row and stays orphaned.

## 2. Domain rule (user-supplied, authoritative)

> **If a line has a valid tracking number from import, use it. Otherwise leave it blank.
> Never emit our generated placeholder.**

The invoice workbook's tracking column is filled from `import_rows.raw_data`, so this rule is
satisfied by rendering `raw_data` unchanged and choosing the **right rows**. No render-time
override, no output sanitisation pass.

The placeholder value lives only in `packing_lists`. Nothing in this design writes it into
`import_rows` or into the workbook, so leaking it is **structurally impossible**, not merely
avoided.

## 3. Rejected alternatives

**Backfill `import_rows.tracking_number` with the placeholder** (issue #69's stated preference).
**Closed.** `import_rows.tracking_number` is a generated column off `raw_data`; writing it writes
`raw_data`, and the Shopee header maps `*หมายเลขติดตามพัสดุ` → `tracking_number`, which
`rebuild_layout_xlsx` renders straight into the workbook. `scripts/issue_invoice_shopee.py` does
not drop that column. This option publishes our invented tracking number to the customer-facing
invoice — a direct violation of §2.

**Blanket `OR ir.order_number = pl.order_number` on the join.** One-line change, and wrong. A
promoted parcel has rows in *both* the empty-tracking batch and the real-tracking batch (e.g.
order `260714M5RQA7NA`: batch 426 empty, batch 435 real, same SKU, same quantity). A blanket `OR`
matches both and **double-bills** the line. 23,899 parcels are exposed. Strictly worse than the
current under-billing.

## 4. Design — resolution seam

Three call sites today independently hard-code `ir.tracking_number = ANY($1)`, and can drift:
preview's platform aggregation (`:307`), preview's missing-check (`:317`), and `generate`'s row
fetch (`:452`). Preview can report a parcel billable while `generate` finds nothing for it.

Define the parcel→rows mapping **once**, as a shared CTE constant in `src/api/exports/invoices.rs`,
referenced by all three.

```sql
WITH sel AS (
    SELECT tracking_number, order_number FROM packing_lists
    WHERE tracking_number = ANY($1)
),
direct AS (                                    -- healthy + self-healed promoted parcels
    SELECT s.tracking_number AS parcel, ir.*
    FROM sel s JOIN import_rows ir ON ir.tracking_number = s.tracking_number
),
orphan AS (
    SELECT * FROM sel s
    WHERE NOT EXISTS (SELECT 1 FROM direct d WHERE d.parcel = s.tracking_number)
),
fallback AS (
    SELECT o.tracking_number AS parcel, ir.*,
           rank() OVER (PARTITION BY o.tracking_number ORDER BY ir.batch_id DESC) AS rk
    FROM orphan o JOIN import_rows ir ON ir.order_number = o.order_number
    WHERE NOT EXISTS (                         -- ownership guard, §4.3
        SELECT 1 FROM packing_lists p WHERE p.tracking_number = ir.tracking_number
    )
)
SELECT parcel, raw_data, batch_id, platform, order_number, id FROM direct
UNION ALL
SELECT parcel, raw_data, batch_id, platform, order_number, id FROM fallback WHERE rk = 1
```

### 4.1 Orphan detection is structural, not heuristic

`orphan` is defined as *"has no directly-matched rows"* — **not** as *"tracking looks like an
order number"*. There is no pattern-matching that could misclassify a genuine tracking number,
and the fallback is inert whenever the direct join succeeds.

### 4.2 Newest batch wins

`rank() OVER (PARTITION BY parcel ORDER BY batch_id DESC) = 1` takes the newest batch's rows and
only those. This is what makes §2 hold: the newest batch carries the real `TH...` where Shopee has
issued one, and empty tracking where it has not. It is also what prevents double-billing — two
batches' copies of the same line cannot both survive.

Consistent with existing precedent: `rebuild::newest_batch_layout` already resolves layout by
newest batch, and the importer upsert is last-import-wins.

### 4.3 Ownership guard

`fallback` excludes any `import_rows` row whose `tracking_number` is owned by some `packing_lists`
parcel. `order_number` is not unique in `packing_lists` — **4,958** order numbers are shared across
multiple parcels. Today **zero** orphaned parcels have such a sibling, so the guard changes no
current result; it prevents a future split parcel from silently billing its sibling's lines.

The exclusion is applied **before** the rank, so an excluded newest batch correctly falls through
to the next batch rather than yielding nothing.

Safe against the degenerate case: `packing_lists` has **0** rows with empty-string
`tracking_number`, so the guard never excludes empty-tracking rows wholesale.

## 5. Call-site changes

`select_parcels` is **unchanged** — parcel selection, cancelled/returned exclusions, window mode
and number mode all stay as they are. Only the parcel→rows step changes.

### 5.1 `generate`

```sql
SELECT raw_data, batch_id, parcel FROM (…CTE…) r
WHERE r.platform = $2
ORDER BY r.order_number NULLS LAST, r.id
```

`order_number` and `id` are carried through the CTE rather than projected away, because that sort
is both the workbook's row order and the order `record_export` snapshots billed lines in.

`record_export` needs **no change**: it already keys off the third tuple element, now `parcel`
instead of `ir.tracking_number`. For every parcel that works today those two values are identical,
so the audit contract and existing `invoice_export_items` rows are unaffected.

### 5.2 `preview` platform aggregation

Moves onto the same CTE. Two counters are corrected in the process — both are latent bugs that
only become **reachable** once the fallback returns rows:

- `COUNT(DISTINCT ir.tracking_number) AS parcels` → `COUNT(DISTINCT parcel)`. On the old key,
  fallback rows would collapse into a single `''` bucket and report one parcel regardless of how
  many were resolved.
- `already_exported`'s `EXISTS (… iei.tracking_number = ir.tracking_number)` → `= parcel`.
  `invoice_export_items` stores the `packing_lists` key; matching it against
  `import_rows.tracking_number` only ever worked because the two coincide for healthy parcels.

`orders`, `batches` and `layout_mismatch` keep their current definitions; the
`JOIN import_batches ib ON ib.id = r.batch_id` that `layout_mismatch` depends on is retained,
now joined against the CTE's `batch_id` rather than `import_rows` directly.

### 5.3 `preview` missing-check

```sql
SELECT t FROM UNNEST($1::text[]) AS t
WHERE NOT EXISTS (SELECT 1 FROM (…CTE…) r WHERE r.parcel = t)
ORDER BY t
```

After this, `missing` means genuinely unrecoverable, instead of lumping recoverable orphans in
with deleted-batch parcels.

Because all three derive from one CTE, preview and `generate` cannot disagree about what is
billable.

## 6. Expected effect

Simulated against `warehouse_snapshot` on 2026-08-01, importer era (`created_at >= 2026-06-13`),
excluding `WHI-%` integration-test fixture rows and applying the live cancelled/returned
exclusions:

| | count |
|---|---|
| Parcels in scope | 61,254 |
| Orphaned today (silently dropped from invoices) | 731 |
| **Recovered by this fix** | **111** |
| Rows emitted | 169 |
| — with real tracking in the cell | 2 |
| — with blank tracking in the cell | 167 |
| Still missing after the fix (deleted batches) | 620 |

The 2 real-tracking rows are §2 firing as specified: an unowned import row carrying a genuine
`TH...`, so the cell is filled. Zero emitted rows carry a placeholder value.

`2607290MY5Y1U8` is among the 111, contributing 2 rows (qty 10 + qty 2, SKU `4895151549340`) with
a blank tracking cell.

### 6.1 Measurement caveat

`warehouse_snapshot` is contaminated with `WHI-*` rows written by integration-test runs. All
figures above exclude them. Earlier uncorrected counts in issue #69's comment thread are inflated;
the corrected placeholder total (**5,842**) matches issue #69's original body figure.

## 7. Non-goals

Stated explicitly so this spec cannot be read as promising them.

- **The 620 deleted-batch parcels are not fixed.** Their `import_batches` rows were deleted
  (confirmed by sequence gaps on 2026-07-16: IDs 452–456, 460–463, 472–479, 489), taking their
  `import_rows` with them via `ON DELETE CASCADE`. No `import_rows` exist under any key, so no
  join change reaches them. They remain in `missing` — which is now an honest signal rather than
  noise. Recovery would require re-importing the source files; out of scope.
- **The 5,735 pre-importer legacy placeholders are not fixed.** `import_rows` begins 2026-06-13;
  earlier parcels came from the legacy n8n pipeline and have no import rows at all.
- **The unguarded `packing_status` overwrite at `imports.rs:313`** (issue #69's adjacent bug,
  2,404 exposed rows) is untouched. Different defect, different fix.
- **No schema migration.** No new columns, no backfill, no data mutation of any kind. The change
  is read-path only.

## 8. Testing

Integration tests against a real DB, alongside `tests/warehouse_invoice.rs`, reusing the
placeholder fixtures already in `tests/import_placeholder_promotion.rs`.

1. **Placeholder parcel recovered** — empty-tracking rows only → appears in the workbook, tracking
   cell **blank**, quantities intact.
2. **No double-bill on promoted parcel** — rows in both the empty and the real batch → the line
   appears **once**.
3. **Real tracking preferred** — orphan whose newest batch carries real tracking → cell shows the
   real number (§2's positive case).
4. **Ownership guard** — sibling parcel shares `order_number` → fallback does not steal its rows.
5. **Deleted-batch parcel stays missing** — no import rows under any key → still reported in
   `missing`, not silently dropped.
6. **Preview/generate agreement** — `parcels` counted in preview equals parcels present in the
   generated file.
7. **Audit contract** — `record_export` keys `invoice_export_items` on the placeholder parcel key
   and stamps `invoiced_at` on it; re-export flags `already_exported`.
8. **Regression: healthy parcel unchanged** — a Standard Delivery parcel's workbook output is
   byte-identical to before the change.

Test 8 carries the most weight: it proves the fallback is inert for the 60,523 parcels that
already work.

## 9. Verification before merge

- `cargo test` green in the worktree. Note the known pre-existing failures on this base
  (`product_insights`, `dashboard_api`) — run with `--no-fail-fast` and compare against a
  baseline run on `feat/export-drawer`, do not attribute them to this change.
- Live check against `warehouse_snapshot`: preview the July window, confirm `missing` drops by
  111 and the recovered parcels appear with the row counts in §6.
- Generate the Shopee invoice for `2607290MY5Y1U8` in number mode and confirm the workbook
  contains its 2 lines with an **empty** `*หมายเลขติดตามพัสดุ` cell.
