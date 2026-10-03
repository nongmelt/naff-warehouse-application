# Plan — dup-overlay-followups QA fixes

Branch: `feat/maui-dup-overlay-followups` · **app/ only, no backend changes** · local test build (not committing unless asked).

Three bugs found during QA against `warehouse_snapshot_qa`:

1. **Undo "nothing happens"** — app shows a stale `Duplicate` status; when the parcel is no longer Duplicate server-side, every Undo click 409s silently.
2. **Order-number search shows "two products"** — a reissue order returns both legs; `CurrentOrder = Results.FirstOrDefault()` can pick the wrong (Shipped) leg, hiding the Duplicate leg's Undo.
3. **No hover** on the Dismiss / Mark-as-duplicate buttons.

---

## Fix A — #3 hover (XAML, trivial, zero behavior risk)

**File:** `app/Views/OrderSearchPage.xaml` — `DupDismissButton` (~3100), `DupMarkButton` (~3108).

Both are `<Border>` + `TapGestureRecognizer` with no VisualStateManager. `HoverButton` can't be reused (it's `TargetType="Button"`). Add an inline VSM to each Border:

```xml
<VisualStateManager.VisualStateGroups>
  <VisualStateGroupList>
    <VisualStateGroup x:Name="CommonStates">
      <VisualState x:Name="Normal">      <VisualState.Setters><Setter Property="Opacity" Value="1"/></VisualState.Setters></VisualState>
      <VisualState x:Name="PointerOver"> <VisualState.Setters><Setter Property="Opacity" Value="0.85"/></VisualState.Setters></VisualState>
    </VisualStateGroup>
  </VisualStateGroupList>
</VisualStateManager.VisualStateGroups>
```

VSM `PointerOver` already fires on Borders here (`HomePage.xaml:29`, `SettingsPage.xaml:42/113`).

**Verify:** pointer over each button dims it; leaving restores.

---

## Fix B — #1 undo self-heal (C#, small, low risk)

**File:** `app/Views/OrderSearchPage.DuplicateOverlay.cs`

**B1 — Undo appears immediately after Mark.** In `OnDuplicateMarkTapped` success branch (after `scanned.PackingStatus = "Duplicate";`) add `UpdateHeaderOrderInfo();` so the header Undo button shows without needing a re-search.

**B2 — Self-heal on 409.** In `OnUndoDuplicateButtonTapped`, split the failure path:

```csharp
else if (status == 409)
{
    // Server says it's no longer a duplicate — our cached status is stale.
    // Re-sync so the Undo button clears instead of 409-ing on every click.
    var fresh = await ApiService.GetDetailAsync(order.TrackingNumber);
    if (fresh is not null) { order.PackingStatus = fresh.PackingStatus; UpdateHeaderOrderInfo(); }
    UpdateSearchStatus($"{order.TrackingNumber} is no longer a duplicate — view refreshed.");
}
else
{
    UpdateSearchStatus("Undo failed — check the connection and try again.");
}
```

**Rationale:** kills the silent-409 loop — the button clears and the operator gets clear feedback the moment state has drifted.
**Risk:** low; `GetDetailAsync` already used; only the two duplicate handlers touched.
**Verify:** mark → Undo shows at once; undo an already-undone parcel → button clears + message, no repeat 409s.

---

## Fix C — #2 multi-leg CurrentOrder selection (C#, targeted)

**File:** `app/Views/OrderSearchPage.Search.cs` — `UpdateHeaderOrderInfo` (~602).

```csharp
// Multi-leg (reissue) order: prefer the actionable leg so its Undo/QC is reachable,
// rather than whichever row sorts first (often the already-Shipped sibling).
var order = Results.FirstOrDefault(r => r.IsDuplicate)
         ?? Results.FirstOrDefault(r => !r.IsShipped)
         ?? Results.FirstOrDefault();
```

**Scope:** fixes *which leg* is current on a multi-row search (single-row unaffected). It does **not** stop the product checklist merging both legs' items — a per-tracking parcel picker is a bigger UI change, left as an optional follow-up.
**Risk:** low-moderate (changes current-leg only for multi-row/reissue orders).
**Verify:** order-number search `QADUP0001` → header + Undo target the Duplicate leg.

---

## Build & verify
- `dotnet build app/app.csproj -c Debug -f net10.0-windows10.0.19041.0 -r win-x64`, relaunch `Warehouse.exe`.
- Re-run `test.html` TC-1…TC-4 + order-number search + hover.
- Backend / DB / seed unchanged (`warehouse_snapshot_qa`).

## Out of scope / flags
- Full multi-leg parcel-picker UI (distinct trackings) — larger, separate.
- No backend change (reset already guards `Duplicate`; undo endpoint is correct).
- Not committing; local test build only unless you ask.
