# Spec: Invoice blank-tracking domain — placeholder leak, natural_key identity, reissue duplicates

**Status:** Frozen 2026-08-06. Build-ready.
**Wayfinder map:** [#99](https://github.com/nongmelt/naff-warehouse-application/issues/99) · **Spec-freeze ticket:** [#109](https://github.com/nongmelt/naff-warehouse-application/issues/109)
**Implementation plan:** [`docs/plans/2026-08-06-invoice-blank-tracking.md`](../plans/2026-08-06-invoice-blank-tracking.md)

## 1. Scope

Shipped parcels were silently dropped from invoice exports because the export joins `packing_lists` to `import_rows` on `tracking_number` alone, and a placeholder parcel (`tracking_number = order_number`) matches no import row. ~234 Shopee parcels ran three weeks unbilled before a user spreadsheet caught it. This spec closes that permanently and fixes three adjacent defects it exposed.

Eight changes across the Rust backend, one MAUI scan flow, the Next.js export drawer + dashboard, and two one-time data migrations:

1. **`natural_key` identity** — drop `tracking_number` **and** `paid_at` so one order line is one row for its whole life (#101).
2. **Inert-row cleanup + load-bearing guard** — the identity change merges 48.5k duplicate rows; a generated `awaiting_tracking` marker protects the sole-copy survivors (#106).
3. **Leak closure** — resume wide `Order.all` imports + automatic in-place repair + a **placeholder-aware invoice join** + monitoring (#104).
4. **Stranded billing** — re-pull genuine `Order.all` files over the strand window; the 108 with no import rows are gated on an operator export-window test (#105, #111).
5. **Loud short runs** — the invoice generate step refuses a shortfall unless the operator acknowledges the exact missing set (#102).
6. **Reissue duplicates** — a QC-scan alert (Shopee + Instant only) stops one physical parcel becoming two rows, and the 25 already double-billed are credited (#107).
7. **Monitoring** — a new reconcile class alarms if stranding resumes (#108).
8. **Unpaid guard** — tracked separately as [#110](https://github.com/nongmelt/naff-warehouse-application/issues/110); out of this spec.

### Sources — read these, they are not restated here

Every decision below traces to a closed wayfinder ticket's resolution comment. Where this spec adds something it is marked **[spec-level derivation]** and an implementer with a better idea may override it; everything else is settled and must not be re-litigated.

| Input | Holds |
|---|---|
| [#101 natural_key](https://github.com/nongmelt/naff-warehouse-application/issues/101) | New key format, identity-stability evidence, merge-then-rekey migration, survivor rule, audit-trigger decision |
| [#106 inert rows](https://github.com/nongmelt/naff-warehouse-application/issues/106) | Inert→merge, retention moot, `awaiting_tracking` generated guard |
| [#104 leak closure](https://github.com/nongmelt/naff-warehouse-application/issues/104) | (a)+(c)+(#108)+ build (b); the placeholder join predicate; `record_export` re-key; blank tracking cell |
| [#105 stranded billing](https://github.com/nongmelt/naff-warehouse-application/issues/105) | Re-pull real `Order.all`; `recovery_manifest.sql`; the 108 fallback |
| [#102 short run](https://github.com/nongmelt/naff-warehouse-application/issues/102) | Ack-and-proceed gate, fingerprint binding, audit stamp |
| [#107 reissue](https://github.com/nongmelt/naff-warehouse-application/issues/107) | QC-scan detection, Shopee+Instant+qty-overflow, void extras + credit notes |
| [#108 monitoring](https://github.com/nongmelt/naff-warehouse-application/issues/108) | Input + outcome signals, thresholds, new reconcile class |
| [#100 prod migration audit](https://github.com/nongmelt/naff-warehouse-application/issues/100) | Tracking FKs are `ON UPDATE CASCADE` on prod → promotions are FK-safe |
| [#103 re-import proof](https://github.com/nongmelt/naff-warehouse-application/issues/103) | Wide `Order.all` re-import repairs the strand e2e; leak is operational |
| `recovery_manifest.sql` (repo root) | Which source files recover which parcels; the 108 orphan order numbers |

### Governing invariant (operator-stated — applies to everything below)

**An exported invoice cell equals the imported `raw_data` value, except where an explicit, named transform rule modifies it.** The placeholder join (§5) emits a blank tracking cell *because the source import row's tracking is blank* — not by choice. Any reconstruction of missing import rows (§9.3) is legal only as a named, provenance-marked rule.

### Non-goals (locked out of scope on the map)

- The 114 deleted-batch orphan parcels ruled unrecoverable 2026-08-06 (batches 452–456, 460–463, 465).
- The pre-2026-06-13 legacy era (106,894 parcels, invoiced off-system, no `import_rows`, no audit trail).
- Space-padded legacy `platform` values in reconcile — [naff-warehouse-backend#82](https://github.com/nongmelt/naff-warehouse-backend/issues/82).
- Unpaid-order QC/invoice guard — [#110](https://github.com/nongmelt/naff-warehouse-application/issues/110).
- The ">600" accounting-firm figure — dropped by the operator until the firm delivers a list.
- Reconstructing the 108 from `product_lists` **unless** #111 shows Shopee will not serve the window (§9.3 is conditional).

---

## 2. Domain model

- **Order line** — one (order, product, variation, sku, ordered_at, occurrence) tuple. The unit of import-row identity *after* this spec. It is **not** keyed on tracking or payment time.
- **Placeholder parcel** — a `packing_lists` row whose `tracking_number = order_number`: shipped/packed before Shopee issued a courier code.
- **Stranded parcel** — shipped, `invoiced_at IS NULL`, not cancelled/returned, and joins to no real-tracking import row. The population this spec bills.
- **Load-bearing row** — an `import_rows` row with blank tracking that is the *only* surviving copy of its order line (no real-tracking sibling). After §3 it is exactly `awaiting_tracking = true`.
- **Reissue** — Shopee voids a label and issues a new tracking for the *same physical parcel*. Legitimate two-tracking event; the bug is that the system makes a second parcel row.
- **Split** — one order genuinely shipped as two+ parcels. Distinguished from a reissue by **quantity**: a split's parcel quantities *sum to* the order-line quantity; a reissue's *exceed* it.

---

## 3. Database

Two new migrations. **Naming rule:** if a later timestamp already exists on the base branch, bump to the next free `YYYYMMDDHHMMSS`. **Never edit an applied migration** — prod ran every file from a CRLF checkout; its recorded checksums cannot survive an in-place edit. The shared dev DB carries an orphan `20260703220000` row with no file; `sqlx migrate run` there needs `--ignore-missing` and the row must not be deleted.

### 3.1 Migration A — `awaiting_tracking` generated column (#106)

```sql
-- A blank tracking_number means the order line's real courier code never arrived,
-- so this row is the ONLY surviving copy of that line. Self-maintaining: flips to
-- false the instant a tracked re-import fills raw_data->>'tracking_number'.
ALTER TABLE import_rows ADD COLUMN IF NOT EXISTS awaiting_tracking boolean
    GENERATED ALWAYS AS (coalesce(raw_data->>'tracking_number','') = '') STORED;

COMMENT ON COLUMN import_rows.awaiting_tracking IS
    'true = real tracking never arrived; this row may be the ONLY copy of the order line. Never bulk-delete import_rows by blank tracking.';
```

It **must** reference `raw_data->>'tracking_number'` directly — Postgres forbids a generated column referencing the generated `tracking_number` column. This is a **signpost, not a wall**: a context-free `DELETE WHERE tracking_number=''` still ignores it; the append-only `import_row_revisions` audit remains the true safety net (§9.1). A hard-block delete trigger was considered and declined.

### 3.2 The `natural_key` format change (#101)

`natural_key` is generated in the backend's `dedup` module (`natural_keys()`), **not** in SQL. The current Shopee format:

```
Shopee:<order>|<tracking>|<product_name>|<variation>|<sku>|<ordered_at>|<paid_at>\x1F<occurrence>
```

becomes:

```
Shopee:<order>|<product_name>|<variation>|<sku>|<ordered_at>\x1F<occurrence>
```

Both `<tracking>` and `<paid_at>` are removed. The test that governs this is **identity-stability across a line's life, not emptiness** (measured on the snapshot):

| field | always filled on real rows | stable value | in key |
|---|---|---|---|
| `tracking_number` | no — fills late by design | no | **drop** |
| `paid_at` | yes (0 empty on real rows) | **no** — 735 line-groups carry ≥2 different real paid timestamps (export-snapshot jitter) + 502 empty→real | **drop** |
| `ordered_at` | yes (2 empty / 127k) | **yes — 0 drift** | keep (timing anchor) |
| order / product / variation / sku / `\x1F occ` | yes | yes | keep |

`paid_at` remains a generated column on `import_rows` (and `packing_lists.paid_at` is untouched), so any payment-time reads are unaffected — it only leaves the *identity* key. Removing both fields collapses 100% of twinning (47,263 blank/real supersessions + 1,237 paid-jitter twins). The `\x1F<occurrence>` suffix is unchanged; it still separates genuine duplicate lines.

**Apply the same removal to every platform's key builder** that embeds tracking or paid time. Lazada/Tiktok showed **zero** natural_key collisions from dropping tracking, so the change is safe there; keep their formats otherwise identical to today minus those two fields. **[spec-level derivation — confirm each platform's `natural_keys()` arm; the measured collision analysis was Shopee-only but the identity principle is platform-general.]**

### 3.3 Migration B — merge-then-rekey the existing rows (#101)

`import_rows_natural_key_unique` + the `ON CONFLICT (natural_key) DO UPDATE` upsert make **forward-only impossible**: existing rows keep old-format keys, so a re-import computes a new-format key that matches nothing and inserts yet another twin. Existing rows must be re-keyed — and re-keying 48,500 collision groups would violate the UNIQUE constraint, so the migration must **merge first**.

One migration, in a transaction:

1. For each collision group under the new key, keep the survivor = **`MAX(batch_id)`** (last-import-wins — mirrors `ON CONFLICT DO UPDATE`; the newest batch carries the real tracking + filled paid_at). Delete the rest.
2. Rewrite `natural_key` on every surviving `import_rows` row to the new format.

The ~48,500 deletes **fire** `trg_import_rows_capture_delete`, snapshotting each pre-merge `raw_data` into append-only `import_row_revisions` (~2.7k → ~51k rows). **This is deliberate — do not suppress the trigger.** Suppressing it for a bulk delete would repeat the silent-bulk-loss pattern that motivated ADR 0005 / map #81. The 3,120 load-bearing rows (no real sibling) get a unique new key and survive untouched.

This migration is a **data migration run by hand on prod** (`psql -f`), gated behind a dry-run count. It is **not** a schema `sqlx` migration and must be idempotent-safe to re-run (guard on whether any old-format key remains).

---

## 4. Backend — import & parcel resolution

### 4.1 `natural_keys()` — the format (§3.2)

Change the Shopee arm (and siblings) to omit `<tracking>` and `<paid_at>`. `RESOLVE_PARCEL_SQL` (`imports.rs:62-79`) is unchanged for the exact/placeholder arms — its step-2 placeholder match must stay qualified to `tracking_number = order_number OR tracking_number = ''` (a bare `order_number` match collapses split-order parcels).

### 4.2 Automatic in-place repair (#104 mechanism c)

No new code: with the new key, a tracked re-import UPDATEs the existing placeholder row in place → the generated `tracking_number` column fills → the tracking-based invoice join works. This is the free half of leak closure and requires only §3.2 + §3.3.

### 4.3 Reissue detection at QC scan (#107)

At the scan/packing resolve endpoint (`packing.rs`, same hook that returns the `Cancelled` alert at `packing.rs:359`), when a tracking `T` is scanned for order `O`:

- Fire **only if** `platform = 'Shopee'` **AND** `shipping_options` matches Instant Delivery (`ILIKE '%Instant Delivery%'` / `ส่งทันที`) **AND** the order already has a parcel with a *different real tracking* for the same lineset **AND** admitting a new parcel would push summed parcel quantity **over the order-line quantity** (the reissue signature).
- On match, return `possibleReissue: true` with the existing parcel's tracking in the scan response body (extend the existing response shape; do not add a new endpoint).
- **Never auto-merge.** The backend only flags; the operator decides (§8).

**Why this exact guard (measured):** multi-real-tracking-per-order is *legitimate* on Lazada (164 orders, ~1%) and never happens on Tiktok, so triggering on "different tracking exists" would false-fire on Lazada splits — the trigger must be **qty-overflow**. Of the 61 Shopee reissue-candidate orders, **58 are Instant Delivery**; scoping to Shopee+Instant catches 95% at ~1/30th the check surface (Instant = 3,715 / 115k parcels). The 3 non-Instant stragglers degrade to the §3 merge + §5.2 gate safety net. **[spec-level derivation — the `ILIKE '%Instant Delivery%'` match string; confirm against the live `shipping_options` values, which include the Thai suffix `ส่งทันที (แพ็ก 2 ชั่วโมง)`.]**

---

## 5. Backend — invoice export (`src/api/exports/invoices.rs`)

### 5.1 Placeholder-aware join (#104 mechanism b)

Widen the `generate` join from bare tracking equality to also match a placeholder parcel's blank import rows. Verified safe on the snapshot: **0 orders have 2+ placeholder parcels**, so there is no cross-parcel over-assignment, and the blank-only clause never pulls a real-tracked sibling's rows:

```sql
JOIN import_rows ir
  ON ir.tracking_number = p.tracking_number
 OR ( p.tracking_number = p.order_number            -- parcel is a placeholder
      AND ir.order_number = p.order_number
      AND coalesce(ir.tracking_number,'') = '' )     -- its blank import row only
```

**Never bare `order_number`** (double-bills the 4,877 split orders). The same widening applies wherever `preview` computes `missing[]`, so preview and generate agree.

### 5.2 `record_export` re-key (#104)

`record_export` (`invoices.rs:518`) keys billed lines off `rows.tracking_number` — the import row's value, which is `''` for a placeholder. Re-key it to the **parcel's** `tracking_number` (= order SN for placeholders) so `invoice_export_items` and the `invoiced_at` stamp land on the parcel, not on an empty key.

### 5.3 Blank tracking cell (#104 + fidelity invariant)

A placeholder parcel bills off its blank import row, so its exported `tracking_number` cell is **empty** — faithfully, because the source `raw_data->>'tracking_number'` is empty. Emitting the order SN would invent a value and is disallowed absent a named rule. The parcel is now **present and billed** (empty tracking cell) rather than silently absent.

### 5.4 Loud short runs — ack-and-proceed gate (#102)

`generate` currently errors only when *every* parcel is missing (`invoices.rs:460`). Replace with a shortfall gate reacting to **any** `missing > 0` (no tolerance band — silence caused the leak):

- `preview` returns `missing[]` **plus a `fingerprint`** = a stable hash (e.g. SHA-256) of the sorted missing tracking-number list.
- `generate` accepts an optional `ackFingerprint`. It **recomputes** `missing[]`, then:
  - `missing = 0` → export.
  - `missing > 0` **and** `ackFingerprint` matches the freshly-computed fingerprint → export the recoverable parcels; **`invoice_exports` stamps `missing_count` and `acknowledged = true`**.
  - `missing > 0` **and** absent/stale fingerprint → **`409`** with the `missing[]` payload (reuse `preview`'s shape). No file.

The operator cannot acknowledge a set they did not see: if the shortfall changed since preview, the fingerprint mismatches and re-prompts. Add `missing_count int` and `acknowledged bool` to the `invoice_exports` audit row (a schema migration, or reuse an existing metadata column). **[spec-level derivation — hash function + whether `acknowledged` is a new column or folded into existing export metadata.]**

### 5.5 Monitoring reconcile class (#108)

Add a **new** reconcile class in `invoice_reconcile.rs`, distinct from the existing noisy, `LIST_CAP`-capped `shipped_not_exported`:

- **Input signal (leading):** days since the last `Order.all`-type batch > **N = 4** (tunable). `Order.all`-type = `original_filename ILIKE 'Order.all%'` **OR** the batch has **> 50%** of its rows carrying a real tracking (an `Order.all` populates tracking; `Order.toship` does not) — the fill-rate clause is the robust backstop to the fragile filename.
- **Outcome signal (lagging):** count of shipped, not-cancelled, not-returned, `invoiced_at IS NULL` parcels that join no import row **after** the §5.1 placeholder widening. `> 0` alarms. Post-(b) this is clean — a non-zero count is *genuinely* stranded.

Surface both as dashboard alerts; the warehouse manager acts by pulling and importing a wide `Order.all` (using `recovery_manifest.sql`). Export-time shortfalls stay covered by §5.4.

---

## 6. Frontend surface (`frontend/app/`)

### 6.1 Export-drawer shortfall gate (#102)

In the export drawer, before download becomes available: call `preview`, and if `missing[]` is non-empty, **surface the missing set** (count + a scrollable list of tracking / order numbers) and **disable the download button** until the operator explicitly acknowledges *this* set. On acknowledge, call `generate` with the `ackFingerprint` from that preview. If `generate` returns `409` (the set changed underneath), re-run preview and re-prompt. Requires the **[mockup ticket §10.1]**.

### 6.2 Monitoring alert (#108)

Surface the new reconcile class (§5.5) on the dashboard: a leading "no `Order.all` imported in N days" banner and a lagging "N parcels shipped but unbillable" count, each linking to the action (pull a wide `Order.all`). Distinct visual from the existing `shipped_not_exported` list. Requires the **[mockup ticket §10.2]**.

---

## 7. Reuse — don't reinvent

| Reference | Reused for |
|---|---|
| `invoices.rs` `preview` `missing[]` (L315) | The set §5.4 fingerprints and §6.1 renders |
| `packing.rs:359` Cancelled-scan alert shape | The reissue `possibleReissue` flag (§4.3) — extend, don't add an endpoint |
| Cancelled-order QC alert (MAUI) | The non-blocking scan-warning pattern reused by §8 |
| `invoice_reconcile.rs` `shipped_not_exported` | Structural sibling of the §5.5 outcome class (kept separate) |
| `import_row_revisions` + `capture_delete` trigger | The audit net the §3.3 merge relies on |
| `recovery_manifest.sql` (repo root) | The operator's file/orphan manifest for §5.5 / §9 |

---

## 8. MAUI surface (`app/`)

**Reissue Merge/New prompt (#107).** When a scan response carries `possibleReissue: true` (§4.3), show a **non-blocking** alert in `StationView` (reuse the Cancelled-order QC-alert pattern):

> Order `{order}` already has a parcel (`{existingTracking}`). Reissued label?
> **[Merge onto existing]** — updates that parcel's tracking to the scanned code, no second parcel.
> **[New parcel]** — proceed as a genuine split.

- **Merge** calls a backend action that UPDATEs the existing parcel's `tracking_number` to the scanned code (no new `packing_lists` row).
- **New parcel** proceeds exactly as today.
- The alert never blocks the recording flow; it is a decision surfaced to the operator who has the physical box. Requires the **[mockup ticket §10.3]**.

---

## 9. One-time data remediation

These are **local-only, hand-run** operations (standing preference), each gated behind a dry-run count, executed after the code ships.

### 9.1 Merge-then-rekey (§3.3)
Run Migration B on prod by hand. Verify: pre-count of old-format keys, post-count = 0; `import_row_revisions` grew by the delete count; `SELECT count(*) FROM import_rows WHERE awaiting_tracking` equals the load-bearing count.

### 9.2 Void + credit the double-billed (#107)
The authoritative double-billed set is **17 groups / 25 parcels**, measured from **`invoice_export_items`** (billed lines, same `export_id`) — **not** `packing_lists.invoiced_at`, which now shows only 5 (some `invoiced_at` were cleared). The draft `double_billed.sql` (in `/private/tmp`) **must be re-keyed to `invoice_export_items`** before use. For each group: mark the extra parcel(s) **void** (a new nullable `void_reason`/flag on `packing_lists`, or reuse the reissue path), clear their `invoiced_at`, record the correction, and emit the 25-line list for the accounting firm's credit notes. Void — never delete — so each parcel keeps its recorded video + workflow events.

### 9.3 The 108 orphans (conditional, gated on #111)
Run `recovery_manifest.sql`: Section A re-imports surviving source files; Section B lists the 108 orphan order numbers. Primary path = the operator pulls a genuine wide `Order.all` reaching 2026-07-14 ([#111](https://github.com/nongmelt/naff-warehouse-application/issues/111)). **Only if #111 shows Shopee will not serve that window** does reconstruction from `packing_lists.product_lists` apply — and then only as a **named, provenance-marked transform rule** (scan-truth, non-platform) so the export stays traceable under the fidelity invariant. Do not build reconstruction pre-emptively.

---

## 10. Mockup tickets (create before the UI tasks)

Three surfaces change visibly and need a mockup HTML reference committed to `docs/mockups/` (precedent: `docs/mockups/2026-08-02-support-settings-rebrand.html`), each its own `wayfinder:prototype`-style HITL ticket:

1. **§10.1 Export-drawer shortfall gate** (frontend) — the missing-set panel, disabled download, acknowledge affordance, and the re-prompt on a changed set.
2. **§10.2 Monitoring alert** (frontend dashboard) — leading "no `Order.all` in N days" banner + lagging "shipped-but-unbillable" count, visually distinct from `shipped_not_exported`.
3. **§10.3 MAUI reissue Merge/New prompt** (MAUI) — the non-blocking `StationView` alert with the two actions, matching the Cancelled-order alert styling.

---

## 11. Acceptance criteria

1. After §3.2/§3.3, no two `import_rows` share an order line; a tracked re-import of an existing line UPDATEs in place (no twin), and `import_rows_natural_key_unique` holds.
2. `awaiting_tracking` is true for exactly the blank-tracking rows and flips to false when a tracked re-import fills the tracking; the column comment is present.
3. A placeholder parcel (`tracking_number = order_number`) that previously landed in `preview.missing[]` is billed by `generate`, with an **empty** tracking cell and every other cell verbatim from `raw_data`.
4. `record_export` writes `invoice_export_items` and `invoiced_at` keyed to the parcel's tracking; no split order is double-billed.
5. `generate` on a selection with any missing parcel returns `409` + the missing list without a fingerprint; with the matching `ackFingerprint` it exports and stamps `missing_count` + `acknowledged`; a stale fingerprint re-prompts.
6. The export drawer disables download until the operator acknowledges the current missing set.
7. Scanning a reissued Instant-Delivery Shopee label whose quantity would exceed the order returns `possibleReissue`; MAUI shows Merge/New; Merge updates the existing parcel's tracking with no second row; a Lazada split never triggers it.
8. The double-billed remediation lists exactly the 17 groups / 25 parcels from `invoice_export_items`; voided extras keep their videos/events and lose `invoiced_at`.
9. The monitoring reconcile class alarms when no `Order.all`-type batch has landed in 4 days and when a post-(b) shipped parcel cannot be billed; both surface on the dashboard.
10. `cargo test` passes; `npm run lint` && `npm run build` pass; the MAUI project builds (`net10.0-windows10.0.19041.0`).

## 12. Execution notes

- Work spans **two submodules** (`backend/`, `frontend/`) and the **MAUI app** (`app/`, root repo); each needs its own commits. This spec and plan live in the **root** repo.
- Use `.worktrees/<topic>` inside each submodule; do not switch the main checkout's branch.
- Backend queries are SQLx compile-time checked — point `DATABASE_URL` at the dev DB or regenerate `.sqlx/` for `SQLX_OFFLINE=true`.
- The §3.3 merge is a **hand-run prod data migration**, not a `sqlx` migration; gate it behind a dry-run count and run it after the code ships. Never edit an applied migration.
- Execution is a **local-only SDD effort** by standing preference — do not cloud-schedule it.
- Never point `DATABASE_URL` at `warehouse_snapshot`; reading its schema is proof, its data a signal (`reference_snapshot-is-full-prod-dump`).
