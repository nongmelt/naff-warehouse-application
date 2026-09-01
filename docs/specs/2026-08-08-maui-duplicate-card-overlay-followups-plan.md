# MAUI Duplicate-card overlay follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Duplicate-card product-photo peek genuinely read-only (#118) and stop the single-product auto-open overlay from co-opening the Duplicate card (#119).

**Architecture:** Both fixes live in the `OrderSearchPage` partial classes. #118 threads a `readOnly` flag through `ShowProductImageOverlay` and hides/guards the picking affordances. #119 dismisses any open image overlay when the Duplicate card is raised. No new files, no new services.

**Tech Stack:** .NET 10 MAUI (C#), Windows-only target `net10.0-windows10.0.19041.0`.

## Global Constraints

- **Cannot build or test on darwin.** MAUI targets Windows only. The real compile verifier is the repo's **Build Windows Installer / Compile Check** CI, which runs on push / PR. Local `dotnet build` is not available. (See spec "Testing".)
- **No unit tests this pass.** TDD deferred by request; `app.Tests` (net10.0) links only MAUI-free pure files and neither fix has a pure-logic seam. Verification is CI compile + manual visual on a Shopee+Instant reissue parcel.
- **Trigger stays narrow.** Do not touch the reissue trigger (backend `possibleReissue`, Shopee+Instant+qty-overflow). #116 as-built decision 1.
- **Keep the #117 PR clean.** This work is on branch `feat/maui-dup-overlay-followups` (already cut off the #116 head `5f20bec`); do not commit to `feat/maui-duplicate-order-116`.
- **Commit convention:** end commit messages with `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

**Verification note for every task below:** the "verify" step is a self-review by re-reading the diff (no local build). The compile gate is deferred to a single push at the end of Task 3, where Windows CI compiles the branch.

---

### Task 1: Read-only flag on the image overlay (#118)

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml.cs` — add `_overlayReadOnly` field.
- Modify: `app/Views/OrderSearchPage.ImageOverlay.cs` — add `readOnly` param, set field, conditional button visibility, three tap-handler guards.

**Interfaces:**
- Produces: `ShowProductImageOverlay(ProductItem item, string? openTrigger = null, bool readOnly = false)` — new optional third parameter; when `true`, the overlay hides +/- buttons and ignores picking taps. Consumed by Task 2.
- Produces: `private bool _overlayReadOnly;` page field.

- [ ] **Step 1: Declare the field**

In `app/Views/OrderSearchPage.xaml.cs`, next to `private bool _isFirstItemScan;` (~line 84), add:

```csharp
private bool _overlayReadOnly;
```

- [ ] **Step 2: Add the `readOnly` parameter and set the field**

In `app/Views/OrderSearchPage.ImageOverlay.cs`, change the signature:

```csharp
private void ShowProductImageOverlay(ProductItem item, string? openTrigger = null, bool readOnly = false)
{
    _completionDismissCts?.Cancel();
    _overlayReadOnly = readOnly;
```

(Insert `_overlayReadOnly = readOnly;` immediately after the `_completionDismissCts?.Cancel();` line, before the `if (item.IsBundle)` block, so it is set for both bundle and standard paths.)

- [ ] **Step 3: Hide the +/- buttons in read-only mode**

In the same method, replace the unconditional visibility (currently ~lines 146–147):

```csharp
        OverlayMinusBtn.IsVisible = true;
        OverlayPlusBtn.IsVisible = true;
```

with:

```csharp
        OverlayMinusBtn.IsVisible = !readOnly;
        OverlayPlusBtn.IsVisible = !readOnly;
```

- [ ] **Step 4: Guard the picking tap handlers**

Add an early-return as the first line of each of these three handlers in `app/Views/OrderSearchPage.ImageOverlay.cs`:

`OnOverlayImageTapped` (before `_ = AnimateScanButtonAsync();`):

```csharp
        if (_overlayReadOnly) return;
```

`OnOverlayPlusTapped` (before `_ = AnimateOverlayBtnAsync(OverlayPlusBtn, ...);`):

```csharp
        if (_overlayReadOnly) return;
```

`OnOverlayMinusTapped` (before `_ = AnimateOverlayBtnAsync(OverlayMinusBtn, ...);`):

```csharp
        if (_overlayReadOnly) return;
```

Rationale: persistence is already blocked (card sibling item is not in `Results`, so `FindOrderForItem` is null and the deduction paths no-op). These guards additionally suppress the button-flash animation on the now-hidden buttons.

- [ ] **Step 5: Verify by reading the diff**

Run: `git -C /Users/nongmelt/Workspace/naff-warehouse-application diff app/Views/OrderSearchPage.ImageOverlay.cs app/Views/OrderSearchPage.xaml.cs`
Expected: exactly — one field added; signature gains `bool readOnly = false`; `_overlayReadOnly = readOnly;` set once; two `IsVisible` lines now `!readOnly`; three `if (_overlayReadOnly) return;` guards. No other lines changed. Confirm no normal call site broke (all existing calls omit the third arg → default `false`).

- [ ] **Step 6: Commit**

```bash
git add app/Views/OrderSearchPage.ImageOverlay.cs app/Views/OrderSearchPage.xaml.cs
git commit -m "$(cat <<'EOF'
feat(app): read-only image overlay flag for duplicate-card peek (#118)

ShowProductImageOverlay gains readOnly; hides +/- buttons and guards
picking taps. No behaviour change for normal opens (default false).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Open the duplicate-card peek read-only (#118)

**Files:**
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — pass `readOnly: true` in `OnDuplicateProductTapped`.

**Interfaces:**
- Consumes: `ShowProductImageOverlay(item, "duplicate_card_peek", readOnly: true)` from Task 1.

- [ ] **Step 1: Pass the flag**

In `app/Views/OrderSearchPage.DuplicateOverlay.cs`, `OnDuplicateProductTapped`, change:

```csharp
        if (sender is VisualElement { BindingContext: ProductItem item })
            ShowProductImageOverlay(item, "duplicate_card_peek");
```

to:

```csharp
        if (sender is VisualElement { BindingContext: ProductItem item })
            ShowProductImageOverlay(item, "duplicate_card_peek", readOnly: true);
```

- [ ] **Step 2: Verify by reading the diff**

Run: `git -C /Users/nongmelt/Workspace/naff-warehouse-application diff app/Views/OrderSearchPage.DuplicateOverlay.cs`
Expected: single line change adding `, readOnly: true`.

- [ ] **Step 3: Commit**

```bash
git add app/Views/OrderSearchPage.DuplicateOverlay.cs
git commit -m "$(cat <<'EOF'
feat(app): open duplicate-card photo peek read-only (#118)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Dismiss a co-open overlay when the Duplicate card raises (#119)

**Files:**
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — dismiss the image overlay in `ShowDuplicateOverlayAnimatedAsync`.

**Interfaces:**
- Consumes: `DismissImageOverlayAsync(string closeTrigger = "close_tap")` and the `ProductImageOverlay` view (both existing in `OrderSearchPage.ImageOverlay.cs`).

- [ ] **Step 1: Dismiss before fading the card in**

In `app/Views/OrderSearchPage.DuplicateOverlay.cs`, change `ShowDuplicateOverlayAnimatedAsync`:

```csharp
    private async Task ShowDuplicateOverlayAnimatedAsync()
    {
        DuplicateOrderOverlay.Opacity = 0;
        DuplicateOrderOverlay.IsVisible = true;
        await DuplicateOrderOverlay.FadeToAsync(1, 220, Easing.CubicOut);
    }
```

to:

```csharp
    private async Task ShowDuplicateOverlayAnimatedAsync()
    {
        // #119: an auto-opened single-product image overlay (ZIndex 8) would sit
        // on top of this card (ZIndex 7). Dismiss it first so the card is the
        // only surface demanding the reissue decision.
        if (ProductImageOverlay.IsVisible)
            await DismissImageOverlayAsync("duplicate_card_raised");

        DuplicateOrderOverlay.Opacity = 0;
        DuplicateOrderOverlay.IsVisible = true;
        await DuplicateOrderOverlay.FadeToAsync(1, 220, Easing.CubicOut);
    }
```

- [ ] **Step 2: Verify by reading the diff**

Run: `git -C /Users/nongmelt/Workspace/naff-warehouse-application diff app/Views/OrderSearchPage.DuplicateOverlay.cs`
Expected: the two-line dismiss guard (plus comment) added at the top of `ShowDuplicateOverlayAnimatedAsync`, nothing else. Confirm `DismissImageOverlayAsync` and `ProductImageOverlay` are in scope (same partial class — yes).

- [ ] **Step 3: Commit**

```bash
git add app/Views/OrderSearchPage.DuplicateOverlay.cs
git commit -m "$(cat <<'EOF'
feat(app): dismiss co-open image overlay when duplicate card raises (#119)

Card is ZIndex 7 below the image overlay's 8; on a single-product reissue
the auto-opened overlay rendered on top of the card. Dismiss it first.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: Push and let Windows CI compile**

```bash
git push -u origin feat/maui-dup-overlay-followups
```

Then open the PR (or wait for the compile check) — the **Build Windows Installer / Compile Check** job is the compile gate (no local darwin build). Expected: green. If it fails, read the log; only *new* errors in the three edited files are ours (pre-existing `DisplayAlert`-obsolete warnings elsewhere are not).

---

## Self-Review

**Spec coverage:**
- #118 read-only peek → Tasks 1 + 2 (flag + call site). ✓
- #119 suppress co-open → Task 3 (reactive dismiss). ✓
- Rejected alternatives (pre-gate, deferred-flag) → not implemented, by design. ✓
- Testing = CI compile + manual → Global Constraints + Task 3 Step 4. ✓

**Placeholder scan:** none — every step shows the exact before/after code.

**Type consistency:** `ShowProductImageOverlay(ProductItem, string?, bool)` defined in Task 1, consumed with matching arg order in Task 2. `DismissImageOverlayAsync(string)` and `ProductImageOverlay` are existing members used as-is in Task 3. `_overlayReadOnly` declared (Task 1 Step 1), set (Task 1 Step 2), read (Task 1 Step 4). Consistent.

## Execution Handoff

Deferred by request ("only plans first"). When ready, execute via superpowers:subagent-driven-development or superpowers:executing-plans. Because there is no local build, each task's compile verification collapses into the single Windows-CI push at Task 3 Step 4.
