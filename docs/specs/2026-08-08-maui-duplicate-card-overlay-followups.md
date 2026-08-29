# Spec — MAUI Duplicate-card overlay follow-ups (#118, #119)

**Date:** 2026-08-08
**Scope:** `app/` (MAUI). Two low-severity UX fixes to the Duplicate-order card shipped in #116 / PR #117.
**Tracking:** [#118](https://github.com/nongmelt/naff-warehouse-application/issues/118), [#119](https://github.com/nongmelt/naff-warehouse-application/issues/119)
**Branch base:** `dev-1.4` (where #117 lands). Feature branch to be cut off the #116 head or `dev-1.4` after #117 merges.

## Context

The Duplicate-order card (`OrderSearchPage.DuplicateOverlay.cs`, ZIndex 7) reuses the existing
QC product-image overlay (`OrderSearchPage.ImageOverlay.cs`, ZIndex 8) for two behaviours that
are wrong for a card context:

1. **#118** — tapping a product photo on the card opens the QC image viewer, which always shows
   its **+/- quantity buttons** and pick entry. That viewer is a *picking* surface, not a
   read-only viewer. Editing there does not persist (the card's sibling `ProductItem` is not in
   `Results`, so `FindOrderForItem` returns null and every edit handler no-ops), so this is a UX
   wart — misleading controls, not a data bug.
2. **#119** — after a scan, `ExecuteSearchAsync` may both (a) raise the Duplicate card via
   `CheckReissueAsync` and (b) auto-open the single-product image overlay via
   `AutoOpenSingleProductOverlayAsync`. For a single-product reissue parcel both fire; the image
   overlay (ZIndex 8) opens on top of the card (ZIndex 7), stealing focus from the card's decision.

Both are low severity. #118 is cosmetic (no persistence). #119 is an edge case (reissues are
rarely single-product).

## Non-goals

- No change to the reissue *trigger* (stays narrow: backend `possibleReissue`, Shopee + Instant +
  qty-overflow). See #116 as-built decision 1.
- No redesign of the image overlay into a dedicated read-only viewer. The mockup (§10.3) notes a
  distinct final design; that is deferred. This spec only suppresses the picking affordances.
- No unit tests in this pass (TDD deferred by request; neither fix has a clean pure-logic seam —
  see "Testing").

---

## #118 — Read-only duplicate-card peek

### Behaviour

When the image overlay is opened *from the Duplicate card*, it renders read-only:
- the **+/-** quantity buttons are hidden,
- the pick entry is hidden (already the default open state),
- image / plus / minus taps do nothing (no picking, no button-flash animation).

When opened from any normal path (product card click, auto-open, nav), behaviour is unchanged
(editable).

### Mechanism

Add a `bool readOnly = false` parameter to `ShowProductImageOverlay(ProductItem, string?, bool)`.
Persist it into a new page field `_overlayReadOnly`. Because the parameter defaults to `false` and
every normal call omits it, `_overlayReadOnly` auto-resets to `false` on each normal open — no
explicit teardown needed.

In `ShowProductImageOverlay`, when `readOnly` is true:
- set `OverlayMinusBtn.IsVisible = false` and `OverlayPlusBtn.IsVisible = false`
  (instead of the current unconditional `true` at lines ~146–147).

Early-return on `_overlayReadOnly` at the top of the picking entry points so a stray tap produces
no visual feedback on the now-hidden buttons:
- `OnOverlayImageTapped`
- `OnOverlayPlusTapped`
- `OnOverlayMinusTapped`

`OnDuplicateProductTapped` (`OrderSearchPage.DuplicateOverlay.cs`) passes `readOnly: true`.

### Why this matters — the two card columns are not equivalent

The card shows two columns: a sibling `ProductItem` and the SCANNED parcel's `ProductItem`, which
`CheckReissueAsync` (`OrderSearchPage.DuplicateOverlay.cs`) pulls directly from `Results`. Only the
sibling column gets a free pass from the pre-existing null-owner safety net:

- **Sibling column** — not a member of any order in `Results`, so `FindOrderForItem` returns null
  and `DoOverlayPlus` / `DoOverlayMinus` already early-return on `order == null`, independent of
  this change.
- **Scanned column** — *is* in `Results`, so `FindOrderForItem` returns a real order, and that
  order is not QC-passed (its `packing_status` is `'To be packed'`). The null-owner / QC-passed
  safety net that protects every other read-only surface does not apply here.

So for the scanned column the read-only mechanism is **load-bearing, not cosmetic**: the hidden
+/- buttons, the three tap-handler early-returns (`OnOverlayImageTapped` / `OnOverlayPlusTapped` /
`OnOverlayMinusTapped`), the keyboard pick-entry guard, the keyboard +/- guard, and the
`NavigateOverlayProduct` guard are what close real mutation-and-persistence paths against a live,
non-null-owner order — not a redundant layer on top of an already-safe no-op.

### Files

- `app/Views/OrderSearchPage.ImageOverlay.cs` — new param, `_overlayReadOnly` set, conditional
  button visibility, three early-returns.
- `app/Views/OrderSearchPage.DuplicateOverlay.cs` — pass `readOnly: true` in `OnDuplicateProductTapped`.
- `app/Views/OrderSearchPage.xaml.cs` — declare `private bool _overlayReadOnly;`.

---

## #119 — Suppress auto-open co-opening with the card

### Behaviour

When the Duplicate card is raised, any image overlay currently open (in practice the
single-product auto-open from the same scan) is dismissed first, so the card is the top and only
surface demanding the operator's decision.

### Mechanism — reactive dismiss

In `ShowDuplicateOverlayAnimatedAsync` (`OrderSearchPage.DuplicateOverlay.cs`), before fading the
card in:

```csharp
if (ProductImageOverlay.IsVisible)
    await DismissImageOverlayAsync("duplicate_card_raised");
```

This sequences dismiss → show. The card is ZIndex 7 and the overlay ZIndex 8, so without this the
auto-opened overlay renders on top of the card (the #119 bug). Dismissing it first removes the
overlap.

`DismissImageOverlayAsync` emits its normal `image_collapse` telemetry with the
`duplicate_card_raised` trigger — an honest record of why the overlay closed. Its `auto_complete`
side-effect (advancing to the next unfinished product) does not fire for this trigger.

### Rejected alternatives

- **Cheap synchronous pre-gate** — skip `AutoOpenSingleProductOverlayAsync` when the sole product's
  order `IsShopeeInstant`. Rejected: over-suppresses. Shopee Instant Delivery single-item orders are
  common (quick-commerce), and most are not reissues, so this would kill the auto-open convenience
  for the common case. Reactive dismiss only intervenes on a *true* reissue.
- **Deferred-open flag** (`_reissueCheckPending`; move the auto-open into `CheckReissueAsync`'s
  not-reissue branch). Rejected: removes the brief flicker entirely but restructures the scan
  control flow for a low-severity, "rarely single-product" edge. Not worth the added surface.

### Accepted trade-off

On the rare single-product reissue, the auto-opened photo appears briefly, then closes as the card
raises (the reissue answer is async and lands after the auto-open). This flicker is acceptable and
mildly informative.

### Files

- `app/Views/OrderSearchPage.DuplicateOverlay.cs` only.

---

## Testing

No unit tests in this pass. `app.Tests` (net10.0, darwin-runnable) only links **MAUI-free** pure
files; both fixes are MAUI-dependent UI-timing glue:

- #118 is view-state wiring (button visibility, tap guards) — no extractable decision.
- #119's decision depends on the async `possibleReissue` answer, which lands *after*
  `SingleProductOverlayPolicy.PickSoleProduct` (the existing pure seam) has already run. It cannot
  be expressed as a synchronous pure rule at that layer; reactive dismiss is the correct layer.

Verification: **Windows "Compile Check" CI** (the only real build verifier — MAUI cannot build on
darwin) plus manual/visual check on a Shopee+Instant reissue parcel.

## As-built note (to fold into spec §13.6 at next freeze)

These fixes refine #116 as-built decisions 4 (ZIndex-7 card / ZIndex-8 shared viewer) and 5
(read-only peek deferral). Once shipped, record: peek is now genuinely read-only (#118), and the
card dismisses a co-open overlay on raise (#119).
