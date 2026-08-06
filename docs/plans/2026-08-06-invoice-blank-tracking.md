# Invoice blank-tracking domain — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Spec:** [`docs/specs/2026-08-06-invoice-blank-tracking.md`](../specs/2026-08-06-invoice-blank-tracking.md) — read it before Task 1. Every "why" lives there; this document is the "how".

**Goal:** Close the placeholder-tracking invoice leak permanently and fix the three defects it exposed: re-key `import_rows` to stable order-line identity, add a placeholder-aware invoice join, make short invoice runs fail loudly, and stop reissue duplicate parcels at QC scan.

**Architecture:** Change the backend `dedup` key builder + one generated column + a hand-run merge migration; widen the invoice join and re-key `record_export`; add a fingerprint-bound shortfall gate to preview/generate; add a monitoring reconcile class; add a Shopee+Instant reissue flag to the scan endpoint. Frontend gets an export-drawer shortfall gate and a dashboard monitoring alert; MAUI gets a non-blocking reissue Merge/New prompt.

**Tech Stack:** Rust / Axum / SQLx / PostgreSQL / MinIO on the backend; Next.js 16 App Router / React / Tailwind v4 / Vitest on the frontend; .NET 10 MAUI (`net10.0-windows10.0.19041.0`) for the app.

## Global Constraints

- Work spans two submodules (`backend/`, `frontend/`) and the MAUI app (`app/`, root repo); each needs its own commits. Spec + plan live in the **root** repo. Use `.worktrees/<topic>` inside each submodule — do not switch the main checkout's branch.
- Backend models use `#[serde(rename_all = "camelCase")]`. Handlers return `Result<_, AppError>` (`NotFound` / `Forbidden` / `BadRequest` / `Conflict`).
- SQLx is compile-time checked. Point `DATABASE_URL` at the dev DB or regenerate `.sqlx/` and build with `SQLX_OFFLINE=true`.
- The shared dev DB has an orphan `20260703220000` migration row with no file. `sqlx migrate run` needs `--ignore-missing`. **Do not delete that row.**
- **Never edit an already-applied migration.** Prod ran every file from a CRLF checkout; stack a new file instead.
- `cargo test` needs the rotated MinIO creds exported (see `reference_local-dev-stack`); genuine baseline failures on a green tree are `warehouse_invoice` 2 / `dashboard_api` 4 / `product_insights` 1 — run `--no-fail-fast`.
- Domain vocabulary is fixed (spec §2): order line, placeholder parcel, stranded parcel, load-bearing row, reissue, split.
- **Never point `DATABASE_URL` at `warehouse_snapshot`.** Read its schema for reference only.

---

## File Structure

**Backend (`backend/`)**

| File | Responsibility |
|---|---|
| `src/dedup.rs` (or wherever `natural_keys()` lives) | New key format, all platform arms (Task 1) |
| `migrations/<ts>_awaiting_tracking.sql` | Generated column + comment (Task 2) |
| `scripts/merge_rekey_import_rows.sql` | Hand-run merge-then-rekey (Task 3) |
| `src/api/exports/invoices.rs` | Placeholder join, `record_export` re-key, shortfall gate (Tasks 4, 6) |
| `migrations/<ts>_invoice_export_ack.sql` | `missing_count` + `acknowledged` on `invoice_exports` (Task 6) |
| `src/api/invoice_reconcile.rs` | Monitoring reconcile class (Task 7) |
| `src/api/packing.rs` | Reissue `possibleReissue` flag + merge action (Task 8) |
| `scripts/double_billed.sql` | Re-keyed to `invoice_export_items` (Task 12) |
| `tests/import_rows_invariants.rs`, `tests/warehouse_invoice.rs`, `tests/*` | TDD targets |

**Frontend (`frontend/app/`)** — export-drawer gate (Task 10), dashboard monitoring alert (Task 11).

**MAUI (`app/`)** — `Controls/StationView.xaml.cs` reissue prompt (Task 9), `Services/ApiService.cs` merge call.

**Mockups (root, before UI tasks):** `docs/mockups/2026-08-06-invoice-blank-tracking.html` — one file, three sections (drawer gate, monitoring alert, MAUI prompt). Tracked by the three mockup tickets.

---

## Task 1: `natural_key` new format

**Depends on:** nothing. **Submodule:** `backend/`.

- [ ] **Step 1: Write the failing test** in `tests/import_rows_invariants.rs`: importing an order line blank then re-importing it with a real tracking yields **one** row (UPDATE in place), and its `natural_key` contains neither the tracking nor the paid_at segment. Assert the exact new format for a known fixture line.
- [ ] **Step 2: Run the test to verify it fails.**
- [ ] **Step 3: Change `natural_keys()`** — remove `<tracking>` and `<paid_at>` from the Shopee arm and every sibling arm that embeds them. Keep `\x1F<occurrence>`.
- [ ] **Step 4: Run the tests to verify they pass** (`--no-fail-fast`, MinIO creds exported).
- [ ] **Step 5: Commit** in `backend/` (`feat(import): drop tracking+paid_at from natural_key for stable line identity`).

## Task 2: `awaiting_tracking` generated column

**Depends on:** nothing (parallel with Task 1). **Submodule:** `backend/`.

- [ ] **Step 1: Check the timestamp is free** vs the base branch; bump if taken.
- [ ] **Step 2: Write the migration** `migrations/<ts>_awaiting_tracking.sql` exactly as spec §3.1 (generated column off `raw_data->>'tracking_number'` + `COMMENT`).
- [ ] **Step 3: Write the failing test**: after insert, a blank-tracking row has `awaiting_tracking = true`; after an UPDATE that fills tracking, it flips to `false`.
- [ ] **Step 4: Run to verify it fails.**
- [ ] **Step 5: Apply** (`sqlx migrate run --ignore-missing`).
- [ ] **Step 6: Run to verify it passes.**
- [ ] **Step 7: Commit** (`feat(import): awaiting_tracking marker guards sole-copy rows`).

## Task 3: Merge-then-rekey (hand-run data migration)

**Depends on:** Task 1 (new format), Task 2 (marker for verification). **Submodule:** `backend/` (script only; run by hand).

- [ ] **Step 1: Write `scripts/merge_rekey_import_rows.sql`** — in a transaction: (1) for each new-key collision group keep `MAX(batch_id)`, delete the rest (this fires `capture_delete` — intended); (2) rewrite `natural_key` to the new format on survivors. Idempotent guard: no-op if no old-format key remains. Include a **dry-run** SELECT block printing collision-group and delete counts *before* the mutating block.
- [ ] **Step 2: Verify on a clone** of the dev DB (never the snapshot): pre-count old-format keys; run; assert post old-format count = 0, `import_rows_natural_key_unique` holds, `import_row_revisions` grew by the delete count, `count(*) WHERE awaiting_tracking` = load-bearing count.
- [ ] **Step 3: Do NOT run on prod here** — §9.1 is a post-ship hand-run. Commit the script only (`chore(scripts): merge-then-rekey import_rows migration`).

## Task 4: Placeholder-aware invoice join + `record_export` re-key

**Depends on:** none strictly, but land after Task 1 conceptually. **Submodule:** `backend/`.

- [ ] **Step 1: Write the failing test** in `tests/warehouse_invoice.rs`: a placeholder parcel (`tracking_number = order_number`) with only blank import rows is billed by `generate` (was in `missing[]`), its tracking cell is empty, and a two-parcel split order is **not** double-billed. Assert `invoice_export_items` + `invoiced_at` are keyed to the parcel tracking.
- [ ] **Step 2: Run to verify it fails.**
- [ ] **Step 3: Implement** — widen the `generate` and `preview.missing[]` joins to spec §5.1's placeholder-qualified predicate (never bare `order_number`); re-key `record_export` (`invoices.rs:518`) to the parcel's `tracking_number`.
- [ ] **Step 4: Run to verify it passes.**
- [ ] **Step 5: Commit** (`fix(invoice): bill placeholder parcels via placeholder-aware join`).

## Task 5: (folded into Task 4)

Blank-cell fidelity is a consequence of the join (§5.3); its assertion lives in Task 4 Step 1. No separate task.

## Task 6: Short-run ack gate

**Depends on:** Task 4 (missing[] is computed post-(b)). **Submodule:** `backend/`.

- [ ] **Step 1: Write the migration** `<ts>_invoice_export_ack.sql`: add `missing_count int NOT NULL DEFAULT 0` and `acknowledged bool NOT NULL DEFAULT false` to `invoice_exports` (or confirm an existing metadata column can hold them).
- [ ] **Step 2: Write the failing tests**: `preview` returns a `fingerprint` = stable hash of the sorted missing tracking list; `generate` with `missing>0` and no/stale fingerprint → `409` + missing payload; with the matching fingerprint → 200 + export, and the audit row shows `missing_count` + `acknowledged = true`; `missing = 0` → 200 regardless.
- [ ] **Step 3: Run to verify they fail.**
- [ ] **Step 4: Implement** — add `fingerprint` to the `preview` response; add optional `ackFingerprint` to `generate`; recompute missing, compare, branch per spec §5.4; stamp the audit row.
- [ ] **Step 5: Apply migration; run to verify pass.**
- [ ] **Step 6: Commit** (`feat(invoice): fingerprint-bound shortfall gate on generate`).

## Task 7: Monitoring reconcile class

**Depends on:** Task 4 (outcome signal uses the post-(b) join). **Submodule:** `backend/`.

- [ ] **Step 1: Write the failing test**: the new class reports the input signal (no `Order.all`-type batch in N=4 days; `Order.all%` filename OR >50% rows real tracking) and the outcome signal (post-(b) shipped/unbillable count), and is distinct from `shipped_not_exported`.
- [ ] **Step 2: Run to verify it fails.**
- [ ] **Step 3: Implement** the class in `invoice_reconcile.rs`; expose it on the reconcile endpoint the dashboard reads. Make N and the fill-rate threshold constants.
- [ ] **Step 4: Run to verify it passes.**
- [ ] **Step 5: Commit** (`feat(reconcile): stranding-resumption monitoring class`).

## Task 8: Reissue detection at QC scan

**Depends on:** nothing (parallel). **Submodule:** `backend/`.

- [ ] **Step 1: Write the failing test** in the packing/scan test suite: scanning a new tracking for a Shopee **Instant** order whose summed parcel qty would exceed the order-line qty returns `possibleReissue: true` + the existing tracking; a Lazada split (qty sums to order) and a non-Instant Shopee order do **not**; and a `merge` action UPDATEs the existing parcel's tracking with no new row.
- [ ] **Step 2: Run to verify it fails.**
- [ ] **Step 3: Implement** — extend the scan/resolve response (near `packing.rs:359`) with the §4.3 guard; add the merge action endpoint/branch.
- [ ] **Step 4: Run to verify it passes.**
- [ ] **Step 5: Commit** (`feat(packing): flag Shopee Instant label reissues at scan`).

## Task 9: MAUI reissue Merge/New prompt

**Depends on:** Task 8 (the `possibleReissue` flag) + **mockup §10.3**. **App:** `app/` (Windows-only build check).

- [ ] **Step 1:** Open the committed mockup for the prompt styling.
- [ ] **Step 2: Implement** the non-blocking alert in `Controls/StationView.xaml.cs` reusing the Cancelled-order QC-alert pattern; wire **Merge** to a new `ApiService` call that hits the Task 8 merge action, **New parcel** to the current flow.
- [ ] **Step 3: Verify the build** (`dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64`).
- [ ] **Step 4: Commit** in the root repo (`feat(app): reissue Merge/New prompt on scan`).

## Task 10: Export-drawer shortfall gate (frontend)

**Depends on:** Task 6 + **mockup §10.1**. **Submodule:** `frontend/`.

- [ ] **Step 1:** Open the committed mockup.
- [ ] **Step 2: Write the failing test** (Vitest): when `preview` returns non-empty `missing[]`, the drawer shows the missing panel and the download button is disabled until acknowledge; acknowledging calls `generate` with the `ackFingerprint`; a `409` re-runs preview.
- [ ] **Step 3: Run to verify it fails.**
- [ ] **Step 4: Implement** in the export-drawer component + its hook.
- [ ] **Step 5: Run to verify it passes;** `npm run lint && npm run build`.
- [ ] **Step 6: Commit** (`feat(export): shortfall acknowledge gate in drawer`).

## Task 11: Dashboard monitoring alert (frontend)

**Depends on:** Task 7 + **mockup §10.2**. **Submodule:** `frontend/`.

- [ ] **Step 1:** Open the committed mockup.
- [ ] **Step 2: Implement** the leading + lagging banners from the new reconcile class, visually distinct from `shipped_not_exported`, each linking to the pull-`Order.all` action.
- [ ] **Step 3:** `npm run lint && npm run build`; add a render test.
- [ ] **Step 4: Commit** (`feat(dashboard): stranding-resumption alerts`).

## Task 12: Remediation scripts (data, hand-run post-ship)

**Depends on:** Tasks 4, 8. **Submodule:** `backend/` (scripts only).

- [ ] **Step 1: Re-key `scripts/double_billed.sql`** to `invoice_export_items` (the authoritative 17 groups / 25 parcels), not `packing_lists.invoiced_at`. Output the 25-line credit-note list.
- [ ] **Step 2:** Add a void step: mark the extra parcels void (new `void_reason` or the reissue path), clear `invoiced_at`, keep videos/events. Verify on a dev-DB clone.
- [ ] **Step 3:** Confirm `recovery_manifest.sql` (repo root) still returns Section A files + the 108 Section B orphans; leave §9.3 reconstruction unbuilt (gated on #111).
- [ ] **Step 4: Commit the scripts** (`chore(scripts): double-billed remediation + recovery manifest`).

---

## Sequencing

Backend TDD tasks 1–8 first (1, 2, 8 parallel; 3 after 1+2; 4 before 6 and 7). **Create the three mockup tickets and land the mockup HTML before Tasks 9–11.** Tasks 9–11 (UI) after their backend dep + mockup. Task 12 scripts anytime after 4 + 8. The §9 hand-run data migrations happen **after** all code ships, gated on dry-run counts; §9.3 waits on [#111](https://github.com/nongmelt/naff-warehouse-application/issues/111).

## Execution notes

- This is a **local-only SDD effort** — do not cloud-schedule it.
- The §3.3 / §9 data migrations are **hand-run on prod**, never `sqlx migrate run` (CRLF checksum drift); each gated behind a dry-run count.
- Verify subagent commits sit on the intended branch tip with the expected parent before review (`feedback_verify-subagent-commit-parent`).
