# Duplicate Order? Card Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the amber "Duplicate order?" card to the user-approved mockup: enlarged card, one-row merged header (title + Shopee logo + order number + shipping chip), tracking-card-grammar leg headers with exact timestamps, professional hover language, Thai tooltips, and a neither-processed rule that ships the just-scanned parcel.

**Architecture:** All UI work in the MAUI desktop app (`app/`): card XAML (`OrderSearchPage.xaml` ~2907-3272), card logic (`OrderSearchPage.DuplicateOverlay.cs`). One new pure-policy class (`DuplicateMarkPolicy`) carries the mark-target flip + tooltip strings so the behavior change is unit-testable. No backend changes — the duplicate PATCH endpoint already accepts any 'To be packed' tracking.

**Tech Stack:** .NET 10 MAUI (Windows-only), xUnit tests in `app.Tests/`, PostgreSQL via `docker exec` into container `warehouse-postgres`.

**Spec:** `docs/mockups/2026-08-23-dup-overlay-redesign.html` (user-approved final state: A side-by-side layout, one-row header, exact times, Thai tooltips, neither-state flip). Design decisions grilled 2026-08-23; reference theme = `TrackingCardSection` (`OrderSearchPage.xaml:750`).

## Global Constraints

- Branch: `feat/maui-dup-overlay-followups`. **BLOCKER: 18 pre-existing uncommitted files (prior card tweaks + QA fixes A–D) must be committed first** (user approval needed; commit rules in memory `project_dup_overlay_card_tweaks.md`: never stage `app/appsettings.json`, exclude `test.html`/`docker/compose.override.yml`/`.playwright-cli/`, CRLF new files). Do not mix redesign edits into that commit.
- Build check: `dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64` → 0 errors (~9 pre-existing binding warnings OK).
- Tests: `dotnet test app.Tests/app.Tests.csproj` — builds the whole MAUI app: **stop Warehouse.exe first**; ~155 pre-existing warnings OK. 63 tests pass before this plan.
- **x:DataType gotcha:** page-level `x:DataType="views:OrderSearchPage"` makes any `{Binding}` outside a correct scope silently no-op with a CLEAN build. `DupSiblingColumn`/`DupScannedColumn` declare `models:PackingList`; tile DataTemplates declare `models:ProductItem`. Every kept/new `{Binding}` must sit under one of those scopes.
- **CRLF:** repo stores CRLF blobs. Prefer surgical `Edit`; convert any NEW file to CRLF before `git add`.
- Non-ASCII in XAML: XML numeric escapes (`&#x2713;` style) for symbols; literal Thai text is fine (file is UTF-8 — `⚡` already appears literally in DuplicateOverlay.cs).
- QA DB: seed only namespaced `QADUP*` rows via `Scripts/qa/seed-dup-overlay-matrix.sql` into whatever DB the running backend (:8080) points at; delete `QADUP%` rows from `import_rows` + `packing_lists` when done.
- After all code changes: `graphify update .` (project CLAUDE.md rule).
- Commit messages short (user rule); prefix shell commands with `rtk`.

## Verified codebase facts (do not re-derive)

- Card XAML region `app/Views/OrderSearchPage.xaml:2907-3272`: header VerticalStackLayout (2935-2989), banner (2992-2996), comparison Grid (2999-3223), footer (3226-3269). Card shell Border at 2916: `WidthRequest="720"`, `MaximumHeightRequest="760"`.
- `ShowDuplicateOverlay(scanned, sibling, siblingOperatorName)` at `OrderSearchPage.DuplicateOverlay.cs:75` fills all named elements; `CheckReissueAsync` (line 39) resolves ONE nickname (`CheckedBy ?? PackedBy`) at line 64-65.
- `OnDuplicateMarkTapped` (line 180) always marks `_dupScanned`; success sets `scanned.PackingStatus = "Duplicate"`.
- `PackingList.PlatformIcon` / `HasPlatformIcon` exist (`app/Models/PackingList.cs:834-842`) — same icon the header `TrackingCardSection` uses.
- `PackingList.TotalItemsDisplay`, `CheckedAtDisplay`/`CreatedAtDisplay`/`UpdatedAtDisplay` format `"yyyy-MM-dd HH:mm"`. There is **no PackedAt** property — packed-only time proxy is `UpdatedAtDisplay`.
- `ApiService.ResolveOperatorNicknameAsync(staffCode)` — cached, null on unknown.
- `ToolTipProperties.Text` works on Border (see `ResetButton`, xaml:895).
- Existing hover VSMs to REPLACE: order number Opacity 0.7 (2953-2964), sibling tracking (3022-3033), scanned tracking (3137-3148), tile Scale 1.05 (3064-3075, 3175-3186), Dismiss/Mark Opacity 0.85 (3231-3242, 3251-3262).
- Toast plumbing (`DupOrderCopiedToast`, `DupSiblingCopiedToast`, `DupScannedCopiedToast`, `ShowDupCopiedToastAsync`) stays as-is.
- Backend duplicate PATCH 409s unless parcel is 'To be packed' — in the neither state the sibling IS 'To be packed', so marking it works unchanged.

---

### Task 1: DuplicateMarkPolicy + unit tests

**Files:**
- Create: `app/Models/DuplicateMarkPolicy.cs`
- Create: `app.Tests/DuplicateMarkPolicyTests.cs`

**Interfaces:**
- Produces: `DuplicateMarkPolicy.MarksSibling(string? siblingStatus, string? scannedStatus) -> bool`; `DuplicateMarkPolicy.BuildMarkTooltip(string markTracking, string shipTracking) -> string`; `DuplicateMarkPolicy.DismissTooltip` const. Task 4 consumes all three.

- [ ] **Step 1: Write the failing tests**

```csharp
// app.Tests/DuplicateMarkPolicyTests.cs
using app.Models;
using Xunit;

public class DuplicateMarkPolicyTests
{
    [Theory]
    [InlineData("To be packed", "To be packed", true)]   // neither processed → mark sibling
    [InlineData("QC Passed",    "To be packed", false)]  // sibling processed → mark scanned
    [InlineData("Shipped",      "To be packed", false)]
    [InlineData("Packed",       "To be packed", false)]
    [InlineData("to be packed", "TO BE PACKED", true)]   // case-insensitive
    [InlineData(null,           "To be packed", false)]
    public void MarksSibling_OnlyWhenNeitherProcessed(string? sib, string? scan, bool expected)
        => Assert.Equal(expected, DuplicateMarkPolicy.MarksSibling(sib, scan));

    [Fact]
    public void BuildMarkTooltip_NamesMarkAndShipTrackings()
    {
        var tip = DuplicateMarkPolicy.BuildMarkTooltip("SCN1", "SIB1");
        Assert.Equal("ทำเครื่องหมาย SCN1 ว่าเป็นพัสดุซ้ำ และจัดส่งเฉพาะ SIB1", tip);
    }

    [Fact]
    public void DismissTooltip_IsThaiKeepBoth()
        => Assert.Equal("เก็บพัสดุทั้งสองไว้ และจัดส่งทั้งสองพัสดุ", DuplicateMarkPolicy.DismissTooltip);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `rtk dotnet test app.Tests/app.Tests.csproj --filter DuplicateMarkPolicy` (stop Warehouse.exe first)
Expected: FAIL — `DuplicateMarkPolicy` does not exist.

- [ ] **Step 3: Implement**

```csharp
// app/Models/DuplicateMarkPolicy.cs
namespace app.Models;

/// <summary>
/// Duplicate-card decision policy (spec: docs/mockups/2026-08-23-dup-overlay-redesign.html).
/// Normal case: the sibling was already processed, so the just-scanned parcel is
/// the duplicate. Neither-processed case: the just-scanned parcel is the one in
/// hand and ships — the OTHER (sibling) parcel gets marked.
/// </summary>
public static class DuplicateMarkPolicy
{
    public static bool MarksSibling(string? siblingStatus, string? scannedStatus) =>
        string.Equals(siblingStatus, "To be packed", StringComparison.OrdinalIgnoreCase)
        && string.Equals(scannedStatus, "To be packed", StringComparison.OrdinalIgnoreCase);

    public static string BuildMarkTooltip(string markTracking, string shipTracking) =>
        $"ทำเครื่องหมาย {markTracking} ว่าเป็นพัสดุซ้ำ และจัดส่งเฉพาะ {shipTracking}";

    public const string DismissTooltip = "เก็บพัสดุทั้งสองไว้ และจัดส่งทั้งสองพัสดุ";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `rtk dotnet test app.Tests/app.Tests.csproj --filter DuplicateMarkPolicy`
Expected: PASS (8 tests).

- [ ] **Step 5: Convert both new files to CRLF, then commit**

```bash
rtk git add app/Models/DuplicateMarkPolicy.cs app.Tests/DuplicateMarkPolicyTests.cs
rtk git commit -m "feat(app): duplicate mark-target policy + Thai tooltips"
```

---

### Task 2: One-row header + enlarged card (XAML)

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — card shell (~2916-2922), header block (~2934-2989)
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — `ShowDuplicateOverlay` platform-badge block (lines 82-105)

**Interfaces:**
- Produces: named elements `DupPlatformIcon` (Image), `DupOrderNumberWrap` (Border), `DupOrderCopyGlyph` (Label). Removes `DupPlatformBadge`/`DupPlatformLabel`. Keeps `DupOrderNumber`, `DupOrderCopiedToast`, `DupShipChip`/`DupShipLabel` names (Task 4 hovers attach to the wrap).

- [ ] **Step 1: Enlarge the card shell**

At the card Border (~2916): change `WidthRequest="720"` → `WidthRequest="1080"` and `MaximumHeightRequest="760"` → `MaximumHeightRequest="800"`.

- [ ] **Step 2: Replace the whole header block (2934-2989, the `<!-- Header -->` VerticalStackLayout) with the one-row Grid**

```xml
<!-- Header — one row: title left, order identity right (mockup 2026-08-23) -->
<Grid Grid.Row="0" ColumnDefinitions="Auto,Auto,*,Auto,Auto,Auto,Auto"
      ColumnSpacing="13" Padding="26,20">
    <Border Grid.Column="0" BackgroundColor="#fff7ed" Stroke="Transparent"
            StrokeShape="RoundRectangle 999"
            WidthRequest="46" HeightRequest="46">
        <Label Text="&#x29C9;" FontSize="23" FontAttributes="Bold" TextColor="#ea580c"
               HorizontalOptions="Center" VerticalOptions="Center"
               HorizontalTextAlignment="Center" VerticalTextAlignment="Center"/>
    </Border>
    <Label Grid.Column="1" Text="Duplicate order?" FontSize="26" FontAttributes="Bold"
           TextColor="#c2410c" VerticalOptions="Center"/>
    <Image Grid.Column="3" x:Name="DupPlatformIcon"
           WidthRequest="40" HeightRequest="40" Aspect="AspectFit"
           VerticalOptions="Center" IsVisible="False"/>
    <Border Grid.Column="4" x:Name="DupOrderNumberWrap"
            BackgroundColor="Transparent" Stroke="Transparent"
            StrokeShape="RoundRectangle 7" Padding="8,2" VerticalOptions="Center">
        <Border.GestureRecognizers>
            <TapGestureRecognizer Tapped="OnDuplicateCopyOrderTapped"/>
        </Border.GestureRecognizers>
        <HorizontalStackLayout Spacing="7">
            <Label x:Name="DupOrderNumber" FontSize="20" FontAttributes="Bold"
                   TextColor="#111827" FontFamily="Consolas" VerticalOptions="Center"/>
            <Label x:Name="DupOrderCopyGlyph" Text="&#x2398;" FontSize="14"
                   TextColor="#9ca3af" Opacity="0" VerticalOptions="Center"/>
        </HorizontalStackLayout>
    </Border>
    <Border Grid.Column="5" x:Name="DupOrderCopiedToast" BackgroundColor="#E6111827"
            Stroke="Transparent" StrokeShape="RoundRectangle 6" Padding="8,3"
            HorizontalOptions="Start" VerticalOptions="Center"
            IsVisible="False" Opacity="0" InputTransparent="True">
        <Label Text="&#x2713; Copied" FontSize="11" FontAttributes="Bold" TextColor="White"/>
    </Border>
    <Border Grid.Column="6" x:Name="DupShipChip"
            BackgroundColor="#ede9fe" Stroke="#ddd6fe" StrokeThickness="1"
            StrokeShape="RoundRectangle 999" Padding="12,6"
            VerticalOptions="Center" IsVisible="False">
        <Label x:Name="DupShipLabel" FontSize="13" FontAttributes="Bold"
               TextColor="#5b21b6"/>
    </Border>
</Grid>
```

(Ship chip restyled from solid `#512BD4` to the tracking-card soft-pill idiom. The old header's hover VSM on `DupOrderNumber` is gone with this replacement — Task 4 adds the new hover on the wrap.)

- [ ] **Step 3: Update `ShowDuplicateOverlay` — platform icon instead of text badge**

Replace the platform-badge block (DuplicateOverlay.cs lines 82-96, the `if (!string.IsNullOrWhiteSpace(scanned.Platform)) { DupPlatformBadge... }` block) with:

```csharp
        // Platform logo (same asset as the header tracking card) — no text badge.
        DupPlatformIcon.IsVisible = scanned.HasPlatformIcon;
        if (scanned.HasPlatformIcon)
            DupPlatformIcon.Source = scanned.PlatformIcon;
```

- [ ] **Step 4: Build**

Run: `rtk dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: 0 errors. (XAML x:Name removals surface as compile errors if any code still references `DupPlatformBadge`/`DupPlatformLabel` — Step 3 removed the only references.)

- [ ] **Step 5: Commit**

```bash
rtk git add app/Views/OrderSearchPage.xaml app/Views/OrderSearchPage.DuplicateOverlay.cs
rtk git commit -m "feat(app): dup card one-row header, platform logo, 1080px shell"
```

---

### Task 3: Leg headers in tracking-card grammar + exact times

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — sibling leg header (~3004-3058 after Task 2 shifts) and scanned leg header (~3119-3169)
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — `CheckReissueAsync` nickname resolution (lines 61-65, 72), `ShowDuplicateOverlay` signature + pill-row fill (lines 75, 110-132)

**Interfaces:**
- Consumes: `PackingList.TotalItemsDisplay`, `CheckedAtDisplay`, `CreatedAtDisplay`, `UpdatedAtDisplay` (all `"yyyy-MM-dd HH:mm"`).
- Produces: named elements `DupSiblingMetaLabel`, `DupScannedMetaLabel`, `DupSiblingTrackingWrap`, `DupScannedTrackingWrap`, `DupSiblingCopyGlyph`, `DupScannedCopyGlyph`; private static `MetaLine(params (string Label, string Value)[] parts) -> FormattedString`; new signature `ShowDuplicateOverlay(PackingList scanned, PackingList sibling, string? packedName, string? checkedName)`. Removes `DupSiblingOpPill/OpLabel/TimePill/TimeLabel/CountLabel`, `DupScannedCountLabel`.

- [ ] **Step 1: Replace the sibling leg header region**

Inside `DupSiblingColumn` (x:DataType `models:PackingList`), replace everything from the `<Grid ColumnDefinitions="*,Auto,Auto">` header grid down through the `</HorizontalStackLayout>` pill row (currently xaml ~3004-3058) — i.e. everything before the tiles `<FlexLayout` — with:

```xml
<Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
    <VerticalStackLayout Grid.Column="0" Spacing="2">
        <Label x:Name="DupSiblingHeaderLabel" Text="&#x2713; Already processed"
               FontSize="12" FontAttributes="Bold" TextColor="#166534"/>
        <Grid>
            <Border x:Name="DupSiblingTrackingWrap"
                    BackgroundColor="Transparent" Stroke="Transparent"
                    StrokeShape="RoundRectangle 7" Padding="6,1" Margin="-6,0,0,0"
                    HorizontalOptions="Start">
                <Border.GestureRecognizers>
                    <TapGestureRecognizer Tapped="OnDuplicateCopyTrackingTapped"/>
                </Border.GestureRecognizers>
                <HorizontalStackLayout Spacing="7">
                    <Label Text="{Binding TrackingNumber}" FontSize="19" FontAttributes="Bold"
                           TextColor="#111827" FontFamily="Consolas" LineBreakMode="CharacterWrap"
                           VerticalOptions="Center"/>
                    <Label x:Name="DupSiblingCopyGlyph" Text="&#x2398;" FontSize="13"
                           TextColor="#9ca3af" Opacity="0" VerticalOptions="Center"/>
                </HorizontalStackLayout>
            </Border>
            <Border x:Name="DupSiblingCopiedToast" BackgroundColor="#E6111827"
                    Stroke="Transparent" StrokeShape="RoundRectangle 6" Padding="8,3"
                    HorizontalOptions="End" VerticalOptions="Start"
                    IsVisible="False" Opacity="0" InputTransparent="True">
                <Label Text="&#x2713; Copied" FontSize="11" FontAttributes="Bold" TextColor="White"/>
            </Border>
        </Grid>
        <Label x:Name="DupSiblingMetaLabel" Margin="0,0,0,8"/>
    </VerticalStackLayout>
    <HorizontalStackLayout Grid.Column="1" Spacing="6" VerticalOptions="Center">
        <Border BackgroundColor="#dcfce7" Stroke="#86efac" StrokeThickness="1"
                StrokeShape="RoundRectangle 5" Padding="10,3"
                IsVisible="{Binding IsPackedComplete}">
            <Label Text="&#x2713; QC Passed" FontSize="12" FontAttributes="Bold" TextColor="#166534"/>
        </Border>
        <Border BackgroundColor="{Binding StatusBgColor}"
                Stroke="Transparent" StrokeShape="RoundRectangle 5" Padding="10,3">
            <Label Text="{Binding StatusDisplay}" FontSize="12" FontAttributes="Bold"
                   TextColor="{Binding StatusFgColor}"/>
        </Border>
    </HorizontalStackLayout>
</Grid>
```

**IMPORTANT — copy handler contract:** `OnDuplicateCopyTrackingTapped` reads `BindingContext` as `PackingList`; the wrap Border inherits `DupSiblingColumn`'s context, so the existing handler keeps working unchanged.

- [ ] **Step 2: Replace the scanned leg header region the same way**

Inside `DupScannedColumn`, replace the header grid + tracking grid + pill row (currently ~3119-3169, everything before its tiles `<FlexLayout`) with the same structure, substituting: eyebrow label has no `x:Name` and reads `Text="&#x25CF; Just scanned" TextColor="#c2410c"`; wrap Border is `x:Name="DupScannedTrackingWrap"`; glyph is `x:Name="DupScannedCopyGlyph"`; toast keeps `x:Name="DupScannedCopiedToast"`; meta label is `x:Name="DupScannedMetaLabel"`; the pills stack keeps ONLY the status pill (no QC Passed border — a just-scanned parcel is never IsPackedComplete, and the old XAML's inclusion was dead weight):

```xml
    <HorizontalStackLayout Grid.Column="1" Spacing="6" VerticalOptions="Center">
        <Border BackgroundColor="{Binding StatusBgColor}"
                Stroke="Transparent" StrokeShape="RoundRectangle 5" Padding="10,3">
            <Label Text="{Binding StatusDisplay}" FontSize="12" FontAttributes="Bold"
                   TextColor="{Binding StatusFgColor}"/>
        </Border>
    </HorizontalStackLayout>
```

- [ ] **Step 3: Rework the nickname resolution + meta fill in DuplicateOverlay.cs**

In `CheckReissueAsync`, replace lines 61-65 (single `opCode`/`opName`) with two cached lookups:

```csharp
        // Meta line shows both roles by nickname (falls back to the raw code).
        var packedName = string.IsNullOrWhiteSpace(sibling.PackedBy) ? null
            : await ApiService.ResolveOperatorNicknameAsync(sibling.PackedBy) ?? sibling.PackedBy;
        var checkedName = string.IsNullOrWhiteSpace(sibling.CheckedBy) ? null
            : await ApiService.ResolveOperatorNicknameAsync(sibling.CheckedBy) ?? sibling.CheckedBy;
```

and change the invoke (line 72) to `ShowDuplicateOverlay(scanned, sibling, packedName, checkedName)`.

Change the method signature (line 75) to:

```csharp
    private void ShowDuplicateOverlay(PackingList scanned, PackingList sibling,
        string? packedName, string? checkedName)
```

Replace the pill-row fill block (lines 110-132, from `var hasChecked = ...` through `DupScannedCountLabel.Text = ...`) with:

```csharp
        // Meta lines in tracking-card grammar (faint label + slate value),
        // exact timestamps per the 2026-08-23 mockup.
        DupSiblingMetaLabel.FormattedText = checkedName is not null
            ? MetaLine(("Packed:", packedName ?? "—"), ("Checked:", checkedName),
                       ("Checked at:", sibling.CheckedAtDisplay), ("Items:", sibling.TotalItemsDisplay))
            : packedName is not null
                ? MetaLine(("Packed:", packedName), ("Packed at:", sibling.UpdatedAtDisplay),
                           ("Items:", sibling.TotalItemsDisplay))
                : MetaLine(("Created:", sibling.CreatedAtDisplay), ("Items:", sibling.TotalItemsDisplay));

        // The scan moment IS the check moment for the parcel in hand.
        DupScannedMetaLabel.FormattedText = MetaLine(
            ("Checked at:", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
            ("Items:", scanned.TotalItemsDisplay));
```

Add the helper to the same partial class:

```csharp
    private static FormattedString MetaLine(params (string Label, string Value)[] parts)
    {
        var fs = new FormattedString();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) fs.Spans.Add(new Span { Text = "   " });
            fs.Spans.Add(new Span
            {
                Text = parts[i].Label + " ",
                TextColor = Color.FromArgb("#9ca3af"),
                FontSize = 11.5,
            });
            fs.Spans.Add(new Span
            {
                Text = parts[i].Value,
                TextColor = Color.FromArgb("#374151"),
                FontSize = 11.5,
                FontAttributes = FontAttributes.Bold,
            });
        }
        return fs;
    }
```

Keep the `DupSiblingHeaderLabel` honesty block (lines 137-142) — only its font size moved to 12 via XAML. Keep the banner visibility logic (143-144).

- [ ] **Step 4: Build**

Run: `rtk dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: 0 errors. Removed x:Names (`DupSiblingOpPill` etc.) must have no remaining C# references — Step 3 replaced them all.

- [ ] **Step 5: Commit**

```bash
rtk git add app/Views/OrderSearchPage.xaml app/Views/OrderSearchPage.DuplicateOverlay.cs
rtk git commit -m "feat(app): dup card leg headers in tracking-card grammar, exact times"
```

---

### Task 4: Hover system, tiles 110px, tooltips, neither-state flip

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — copyable wraps (Task 2/3 Borders), both tile DataTemplates, footer buttons + banner text
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — mark-target wiring

**Interfaces:**
- Consumes: `DuplicateMarkPolicy` (Task 1), `DupOrderNumberWrap`/`DupOrderCopyGlyph`/`Dup*TrackingWrap`/`Dup*CopyGlyph` (Tasks 2-3).
- Produces: field `_dupMarkTarget` used by `OnDuplicateMarkTapped`.

- [ ] **Step 1: Hover on the three copyable wraps**

Add inside `DupOrderNumberWrap` (and equivalently `DupSiblingTrackingWrap` / `DupScannedTrackingWrap`, each targeting its own glyph name):

```xml
<VisualStateManager.VisualStateGroups>
    <VisualStateGroupList>
        <VisualStateGroup x:Name="CommonStates">
            <VisualState x:Name="Normal">
                <VisualState.Setters>
                    <Setter Property="BackgroundColor" Value="Transparent"/>
                    <Setter TargetName="DupOrderCopyGlyph" Property="Label.Opacity" Value="0"/>
                </VisualState.Setters>
            </VisualState>
            <VisualState x:Name="PointerOver">
                <VisualState.Setters>
                    <Setter Property="BackgroundColor" Value="#f3f4f6"/>
                    <Setter TargetName="DupOrderCopyGlyph" Property="Label.Opacity" Value="1"/>
                </VisualState.Setters>
            </VisualState>
        </VisualStateGroup>
    </VisualStateGroupList>
</VisualStateManager.VisualStateGroups>
```

**Fallback:** if `Setter TargetName` misbehaves at runtime (verify by hovering in Task 5's QA), drop both TargetName setters and give each glyph a static `Opacity="0.4"`.

- [ ] **Step 2: Tiles — 110px, lift instead of scale (both DataTemplates)**

In BOTH tile DataTemplates (sibling ~3059 region and scanned ~3170 region): change every `86` to `110` (stack `WidthRequest`, tile `Grid` `WidthRequest`/`HeightRequest`, both image Borders' `HeightRequest`/`WidthRequest`). Replace each template's Scale VSM:

```xml
<VisualState x:Name="Normal">
    <VisualState.Setters><Setter Property="TranslationY" Value="0"/></VisualState.Setters>
</VisualState>
<VisualState x:Name="PointerOver">
    <VisualState.Setters><Setter Property="TranslationY" Value="-2"/></VisualState.Setters>
</VisualState>
```

(Do NOT touch the `TileBorderColor` stroke bindings — a VSM setter on Stroke would clobber the binding.)

- [ ] **Step 3: Footer buttons — bg-shift hovers + tooltips, banner copy**

`DupDismissButton`: add `ToolTipProperties.Text="เก็บพัสดุทั้งสองไว้ และจัดส่งทั้งสองพัสดุ"` to the Border and replace its VSM setters:

```xml
<VisualState x:Name="Normal">
    <VisualState.Setters>
        <Setter Property="BackgroundColor" Value="White"/>
        <Setter Property="Stroke" Value="#d1d5db"/>
    </VisualState.Setters>
</VisualState>
<VisualState x:Name="PointerOver">
    <VisualState.Setters>
        <Setter Property="BackgroundColor" Value="#f9fafb"/>
        <Setter Property="Stroke" Value="#9ca3af"/>
    </VisualState.Setters>
</VisualState>
```

`DupMarkButton` (tooltip is set dynamically in Step 4): replace its VSM setters:

```xml
<VisualState x:Name="Normal">
    <VisualState.Setters><Setter Property="BackgroundColor" Value="#ea580c"/></VisualState.Setters>
</VisualState>
<VisualState x:Name="PointerOver">
    <VisualState.Setters><Setter Property="BackgroundColor" Value="#c2410c"/></VisualState.Setters>
</VisualState>
```

Banner label text (xaml ~2994) becomes:

```
&#x26A0; Neither parcel has been processed — marking as duplicate voids the other parcel and ships the just-scanned one.
```

- [ ] **Step 4: Neither-state mark-target wiring in DuplicateOverlay.cs**

Add a field next to `_dupSibling` (line 23):

```csharp
    private PackingList? _dupMarkTarget;
```

In `ShowDuplicateOverlay`, right after the banner-visibility block (lines 137-144), add:

```csharp
        // Neither-processed: the parcel in hand ships; Mark targets the sibling.
        _dupMarkTarget = DuplicateMarkPolicy.MarksSibling(sibling.PackingStatus, scanned.PackingStatus)
            ? sibling : scanned;
        var shipSide = ReferenceEquals(_dupMarkTarget, sibling) ? scanned : sibling;
        ToolTipProperties.SetText(DupMarkButton,
            DuplicateMarkPolicy.BuildMarkTooltip(_dupMarkTarget.TrackingNumber, shipSide.TrackingNumber));
```

In `OnDuplicateMarkTapped` (line 180), change the body to act on the target:

```csharp
        var target = _dupMarkTarget ?? _dupScanned;
        if (target is null) { await DismissDuplicateOverlayAsync(); return; }

        DupMarkButtonLabel.Text = "Marking…";
        DupMarkButton.Opacity = 0.6;

        var result = await ApiService.MarkDuplicateAsync(
            target.TrackingNumber, EffectiveOperator, AppSettings.ResolvedStationId);

        if (result.Marked || result.AlreadyMarked)
        {
            target.PackingStatus = "Duplicate";
            UpdateHeaderOrderInfo();
            UpdateSearchStatus($"{target.TrackingNumber} marked as duplicate — QC locked, not billed.");
            await DismissDuplicateOverlayAsync();
        }
        else
        {
            DupMarkButtonLabel.Text = "Mark as duplicate";
            DupMarkButton.Opacity = 1;
            UpdateSearchStatus(result.Status == 409
                ? "Can't mark — parcel is no longer 'To be packed'."
                : "Mark failed — check the connection and try again.");
        }
```

(When the sibling is the target, `UpdateHeaderOrderInfo()` is a harmless no-op refresh of the scanned header; the scanned parcel keeps its status and stays workable — that is the point of the rule.)

- [ ] **Step 5: Build + full test suite**

Run: `rtk dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64`
Then (Warehouse.exe stopped): `rtk dotnet test app.Tests/app.Tests.csproj`
Expected: 0 errors; 63 + 8 = 71 tests pass.

- [ ] **Step 6: Commit**

```bash
rtk git add app/Views/OrderSearchPage.xaml app/Views/OrderSearchPage.DuplicateOverlay.cs
rtk git commit -m "feat(app): dup card hovers, 110px tiles, Thai tooltips, neither-state ship rule"
```

---

### Task 5: Driven QA sweep against the seed matrix

**Files:**
- Use: `Scripts/qa/seed-dup-overlay-matrix.sql` (idempotent, QADUP0002–0006)
- No code changes expected; fix-forward anything the sweep finds.

**Interfaces:**
- Consumes: running backend :8080 (`curl http://localhost:8080/health` → 200); the DB it points at must contain batch 354 (`SELECT EXISTS(SELECT 1 FROM import_batches WHERE id=354)` via `docker exec warehouse-postgres psql -U warehouse_user -d <db>`).

- [ ] **Step 1: Seed**

```bash
docker exec -i warehouse-postgres psql -U warehouse_user -d <backend-db> -v ON_ERROR_STOP=1 < Scripts/qa/seed-dup-overlay-matrix.sql
curl -s "http://127.0.0.1:8080/packing-lists?q=QADUP0002"   # expect non-empty JSON
```

- [ ] **Step 2: Launch the built app and drive the matrix**

Launch `app/bin/Release/net10.0-windows10.0.19041.0/win-x64/Warehouse.exe`. UI-driving gotchas (memory `project_dup_overlay_card_tweaks.md`): login overlay is COM-scanner-only — bypass with admin chord Ctrl+Shift+A+M sent as HELD keys via `keybd_event`; hover needs a `mouse_event` relative-move jiggle after `SetCursorPos`; QC keyboard shortcuts live on the page — dismiss the card via its button, not Esc.

Search each scan leg and screenshot the card:

| Scan | Verifies |
|---|---|
| QADUPSCN0002 | one-row header: ⧉ title left, Shopee logo + QADUP0002 + violet Instant Delivery pill right; sibling meta `Packed: จู  Checked: เน  Checked at: <exact>  Items: 3`; green 110px tiles; scanned meta `Checked at: <now>` |
| QADUPSCN0003 | dual pill (✓ QC Passed + Shipped) right-aligned in sibling header |
| QADUPSCN0004 | packed-only meta (`Packed: จู  Packed at: <exact>`), NO green tiles |
| QADUPSCN0005 | ◷ Other parcel eyebrow + new banner text; hover Mark → tooltip names QADUPSIB0005 as marked, QADUPSCN0005 as shipped; click Mark → **sibling** goes Duplicate, scanned stays To be packed |
| QADUPSCN0006 | mixed green/gray tiles |

Also verify on any card: number/tracking hover shows gray pill + ⎘ glyph (no opacity fade), tile hover lifts (no scale), Dismiss hover visibly tints, Dismiss tooltip is the Thai keep-both line, copy still toasts.

- [ ] **Step 3: Clean up seeds + update graph**

```bash
docker exec warehouse-postgres psql -U warehouse_user -d <backend-db> -c "DELETE FROM import_rows WHERE order_number LIKE 'QADUP%'; DELETE FROM packing_lists WHERE order_number LIKE 'QADUP%';"
graphify update .
```

- [ ] **Step 4: Commit any fix-forward changes; report**

Screenshots + pass/fail table to the user. Do NOT merge PRs — the #121→#117 fold is a separate user decision.
