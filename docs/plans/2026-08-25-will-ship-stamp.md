# "Will ship" Rubber Stamp (variation E) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the duplicate-card's small top-corner ship-outcome badge with a centered rubber stamp on each leg that stays visible no matter how far the leg is scrolled.

**Architecture:** The stamp copy/colour decision moves into a pure, unit-tested policy class (`app/Models/ShipStampPolicy.cs`) so the Thai strings and the green/rose mapping are locked by tests. The XAML changes ownership of the overlay: today each leg is `ScrollView > Grid > (content + badge)`, so the badge scrolls with the content; it becomes `Grid > (ScrollView > content) + stamp`, which anchors the stamp to the leg's *viewport* instead of its content. `OrderSearchPage.DuplicateOverlay.cs` keeps the same three entry points (`ShowShipSimulation` / `HideShipSimulation` / hover handlers) — only the element names and the dim constant change.

**Tech Stack:** .NET 10 MAUI (net10.0-windows10.0.19041.0), XAML, xUnit (`app.Tests`).

**Spec:** `docs/mockups/2026-08-23-will-ship-variations.html` — variation **E · Stamp** (CSS at lines 180–188, design note at line 360). Open the mockup and click "E · Stamp" to see the target. Predecessor spec: `docs/mockups/2026-08-23-dup-overlay-redesign.html`, plan `docs/plans/2026-08-23-dup-overlay-redesign.md`.

## Global Constraints

- **Stamp copy is Thai** (user decision, 2026-08-25 — overrides the mockup's English "WILL SHIP"/"DUPLICATE"): ships = `จัดส่ง`, duplicate = `พัสดุซ้ำ`. Same vocabulary as the existing footer hints in `DuplicateMarkPolicy`.
- **No letter-spacing on the stamp.** The mockup uses `letter-spacing:.14em`; Thai combining vowel/tone marks (ั ่ ุ ้) sit above and below their base glyph, and MAUI `CharacterSpacing` inserts advance between every glyph including those marks. Do NOT set `CharacterSpacing`. The rubber-stamp read comes from size + weight + 4px border + rotation.
- **Thai must not clip.** A Thai clipping complaint already forced the footer hint bubble to a fixed `42` row height (commit `3b384a3`). Give the stamp generous vertical padding and no `HeightRequest`. If QA sees clipped marks, raise the vertical padding — never shrink the font.
- **Instant, no animation** (user decision). The stamp appears/disappears with `IsVisible`; the leg content's dim is applied by assigning `.Opacity` directly, which MAUI renders instantly — there is no implicit `Opacity` transition to inherit. This is a deliberate divergence from the mockup's `transition:opacity .15s ease`. Do not add fade/scale.
- **Both legs stamp on Dismiss hover** (user decision, matches mockup): Dismiss hover = green `จัดส่ง` on both legs, no dim. Mark hover = green `จัดส่ง` on the surviving leg, rose `พัสดุซ้ำ` on the mark target, and the mark target's content dims.
- **Dim value is 0.45** (mockup E; the current build uses 0.55).
- Colours, verbatim from the mockup: ship ink `#15803d`, duplicate ink `#be123c`, stamp fill `rgba(255,255,255,.78)` → `#C7FFFFFF`, border 4px, corner radius 12, rotation −8°.
- **Deliberate deviation:** the mockup gives each stamp a tinted shadow (`rgba(22,101,52,.25)` / `rgba(159,18,57,.25)`). This build uses one neutral shadow declared in XAML so the hover path never allocates a `Shadow` per pointer event. If the user asks for tinted shadows later, swap `stamp.Shadow` inside `SetSimStamp`.
- Repo stores **CRLF**. Make surgical edits only and verify line endings before committing (see Task 2, Step 7).
- Commit style: short subject, no body, Conventional Commits.
- Green bar before this plan: **71/71** tests. This plan adds 7 → **78/78**. If your baseline isn't 71, the rule is baseline + 7, 0 failed.
- Branch: `feat/maui-dup-overlay-followups` (10 local commits, nothing pushed). Do NOT push; integration is the user's call.

---

## File Structure

| File | Responsibility |
|---|---|
| `app/Models/ShipStampPolicy.cs` (create) | Pure decision: given "does this leg ship?", what text and ink colour does its stamp carry. Also owns the dim constant and the leg-ships truth table. No MAUI types — keeps it unit-testable without a UI thread. |
| `app.Tests/ShipStampPolicyTests.cs` (create) | xUnit coverage of the copy, the ink mapping, and the four hover/target combinations. |
| `app/Views/OrderSearchPage.xaml` (modify) | Leg wrapper restructure + the two stamp `Border`s. |
| `app/Views/OrderSearchPage.DuplicateOverlay.cs` (modify) | Rewire `ShowShipSimulation` / `SetSimBadge` → `SetSimStamp` / `HideShipSimulation` onto the new element names and the policy. |

Note on colours-as-strings: `ShipStampPolicy` returns hex strings, not `Color`, so the test project doesn't need a UI context. The view converts with `Color.FromArgb(...)` — the same idiom the existing `SetSimBadge` uses.

---

### Task 1: Ship-stamp policy + tests

**Files:**
- Create: `app/Models/ShipStampPolicy.cs`
- Test: `app.Tests/ShipStampPolicyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `readonly record struct app.Models.ShipStampStyle(string Text, string Ink)`
  - `static ShipStampStyle app.Models.ShipStampPolicy.For(bool ships)`
  - `static bool app.Models.ShipStampPolicy.LegShips(bool markHover, bool isMarkTarget)`
  - `const string ShipText = "จัดส่ง"`, `const string DuplicateText = "พัสดุซ้ำ"`
  - `const string ShipInk = "#15803d"`, `const string DuplicateInk = "#be123c"`
  - `const double DimmedOpacity = 0.45`

- [ ] **Step 1: Write the failing test**

Create `app.Tests/ShipStampPolicyTests.cs`. Note the existing `DuplicateMarkPolicyTests.cs` has no namespace declaration (a known parked minor) — do NOT copy that; declare `namespace app.Tests;` here.

```csharp
using app.Models;
using Xunit;

namespace app.Tests;

public class ShipStampPolicyTests
{
    [Fact]
    public void For_ShippingLeg_IsThaiShipCopyInGreen()
    {
        var style = ShipStampPolicy.For(ships: true);
        Assert.Equal("จัดส่ง", style.Text);
        Assert.Equal("#15803d", style.Ink);
    }

    [Fact]
    public void For_DuplicateLeg_IsThaiDuplicateCopyInRose()
    {
        var style = ShipStampPolicy.For(ships: false);
        Assert.Equal("พัสดุซ้ำ", style.Text);
        Assert.Equal("#be123c", style.Ink);
    }

    // Dismiss hover (markHover: false) keeps both parcels — every leg ships.
    // Mark hover voids exactly the mark target; the other leg still ships.
    [Theory]
    [InlineData(false, false, true)]   // dismiss hover, non-target leg
    [InlineData(false, true,  true)]   // dismiss hover, the leg Mark would target
    [InlineData(true,  false, true)]   // mark hover, surviving leg
    [InlineData(true,  true,  false)]  // mark hover, the leg being voided
    public void LegShips_OnlyTheMarkTargetStopsShipping(bool markHover, bool isMarkTarget, bool expected)
        => Assert.Equal(expected, ShipStampPolicy.LegShips(markHover, isMarkTarget));

    [Fact]
    public void DimmedOpacity_MatchesVariationE()
        => Assert.Equal(0.45, ShipStampPolicy.DimmedOpacity, precision: 3);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Stop the app first — `app.Tests` references the MAUI app project, so a running `Warehouse.exe` locks the build output.

```powershell
Get-Process Warehouse -ErrorAction SilentlyContinue | Stop-Process
dotnet test app.Tests/app.Tests.csproj
```

Expected: **build failure**, `error CS0246: The type or namespace name 'ShipStampPolicy' could not be found`. (A compile error is the correct red here — there is no type yet.)

- [ ] **Step 3: Write the minimal implementation**

Create `app/Models/ShipStampPolicy.cs`:

```csharp
namespace app.Models;

/// <summary>Copy + ink for one leg's hover stamp. Hex strings, not Colors, so
/// the policy stays unit-testable without a MAUI context.</summary>
public readonly record struct ShipStampStyle(string Text, string Ink);

/// <summary>
/// Ship-outcome stamp policy for the duplicate card (spec:
/// docs/mockups/2026-08-23-will-ship-variations.html, variation E).
/// Hovering Dismiss keeps both parcels, so every leg ships. Hovering Mark
/// voids exactly one leg — the mark target chosen by <see cref="DuplicateMarkPolicy"/>.
/// </summary>
public static class ShipStampPolicy
{
    public const string ShipText = "จัดส่ง";
    public const string DuplicateText = "พัสดุซ้ำ";
    public const string ShipInk = "#15803d";
    public const string DuplicateInk = "#be123c";

    /// <summary>Opacity applied to the voided leg's content while Mark is hovered.</summary>
    public const double DimmedOpacity = 0.45;

    public static ShipStampStyle For(bool ships) =>
        ships ? new ShipStampStyle(ShipText, ShipInk)
              : new ShipStampStyle(DuplicateText, DuplicateInk);

    public static bool LegShips(bool markHover, bool isMarkTarget) => !markHover || !isMarkTarget;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
dotnet test app.Tests/app.Tests.csproj
```

Expected: `Passed!` with **78** passed, 0 failed. `rtk` truncates dotnet test summaries — run this one bare (no `rtk` prefix) or grep the raw output for `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add app/Models/ShipStampPolicy.cs app.Tests/ShipStampPolicyTests.cs
git commit -m "feat(app): ship-stamp copy + ink policy"
```

Convert both new files to CRLF before staging if your editor wrote LF (the repo stores CRLF blobs).

---

### Task 2: Centered stamp replaces the top badge

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — sibling leg `3005–3006` (open tags) and `3143–3151` (badge + close tags); scanned leg `3156–3157` (open tags) and `3289–3297` (badge + close tags). Line numbers are from HEAD `3b384a3`; match on the text, not the numbers.
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs:222–249` (`ShowShipSimulation`, `SetSimBadge`, `HideShipSimulation`) and `:140` (`HideShipSimulation()` call in `ShowDuplicateOverlay`, unchanged but re-verify).

**Interfaces:**
- Consumes: `ShipStampPolicy.For`, `ShipStampPolicy.LegShips`, `ShipStampPolicy.DimmedOpacity` (Task 1).
- Produces: XAML element names `DupSiblingStamp` / `DupSiblingStampLabel` / `DupScannedStamp` / `DupScannedStampLabel`. The names `DupSiblingSimBadge`, `DupSiblingSimLabel`, `DupScannedSimBadge`, `DupScannedSimLabel` and the method `SetSimBadge` cease to exist — grep the repo for them after the edit and expect zero hits.

Why the restructure: today the badge is a sibling of the content *inside* the `ScrollView`, so on a long leg it scrolls out of view (the known caveat recorded in the 2026-08-23 handoff). Moving the `ScrollView` down one level and hanging the stamp off the new wrapper `Grid` anchors the stamp to the visible viewport. The mockup's own note for E says exactly this: "centered label on the wrapper Grid, outside the ScrollView."

- [ ] **Step 1: Restructure the sibling (left) leg's opening tags**

In `app/Views/OrderSearchPage.xaml`, find:

```xml
                        <ScrollView Grid.Column="0" BackgroundColor="#fbfbfc">
                          <Grid>
```

Replace with:

```xml
                        <Grid Grid.Column="0">
                          <ScrollView BackgroundColor="#fbfbfc">
```

- [ ] **Step 2: Swap the sibling leg's badge for the stamp**

Find (the badge sits right after `</VerticalStackLayout>`, which closes `DupSiblingColumn`):

```xml
                            <Border x:Name="DupSiblingSimBadge" BackgroundColor="#dcfce7" Stroke="#86efac" StrokeThickness="1"
                                    StrokeShape="RoundRectangle 999" Padding="12,5" Margin="0,-6,-6,0"
                                    HorizontalOptions="End" VerticalOptions="Start"
                                    IsVisible="False" InputTransparent="True">
                                <Label x:Name="DupSiblingSimLabel" Text="&#x2713; Will ship" FontSize="12"
                                       FontAttributes="Bold" TextColor="#166534"/>
                            </Border>
                          </Grid>
                        </ScrollView>
```

Replace with:

```xml
                          </ScrollView>
                          <Border x:Name="DupSiblingStamp" BackgroundColor="#C7FFFFFF"
                                  Stroke="#15803d" StrokeThickness="4" StrokeShape="RoundRectangle 12"
                                  Padding="28,12" Rotation="-8"
                                  HorizontalOptions="Center" VerticalOptions="Center"
                                  IsVisible="False" InputTransparent="True">
                              <Border.Shadow>
                                  <Shadow Brush="#111827" Offset="0,4" Radius="18" Opacity="0.18"/>
                              </Border.Shadow>
                              <Label x:Name="DupSiblingStampLabel" Text="จัดส่ง" FontSize="32"
                                     FontAttributes="Bold" TextColor="#15803d" LineBreakMode="NoWrap"/>
                          </Border>
                        </Grid>
```

Note the closing-tag order flips: the `ScrollView` now closes *before* the stamp, and the wrapper `Grid` closes last.

- [ ] **Step 3: Restructure the scanned (right) leg's opening tags**

Find:

```xml
                        <ScrollView Grid.Column="2" BackgroundColor="White">
                          <Grid>
```

Replace with:

```xml
                        <Grid Grid.Column="2">
                          <ScrollView BackgroundColor="White">
```

- [ ] **Step 4: Swap the scanned leg's badge for the stamp**

Find:

```xml
                            <Border x:Name="DupScannedSimBadge" BackgroundColor="#dcfce7" Stroke="#86efac" StrokeThickness="1"
                                    StrokeShape="RoundRectangle 999" Padding="12,5" Margin="0,-6,-6,0"
                                    HorizontalOptions="End" VerticalOptions="Start"
                                    IsVisible="False" InputTransparent="True">
                                <Label x:Name="DupScannedSimLabel" Text="&#x2713; Will ship" FontSize="12"
                                       FontAttributes="Bold" TextColor="#166534"/>
                            </Border>
                          </Grid>
                        </ScrollView>
```

Replace with:

```xml
                          </ScrollView>
                          <Border x:Name="DupScannedStamp" BackgroundColor="#C7FFFFFF"
                                  Stroke="#15803d" StrokeThickness="4" StrokeShape="RoundRectangle 12"
                                  Padding="28,12" Rotation="-8"
                                  HorizontalOptions="Center" VerticalOptions="Center"
                                  IsVisible="False" InputTransparent="True">
                              <Border.Shadow>
                                  <Shadow Brush="#111827" Offset="0,4" Radius="18" Opacity="0.18"/>
                              </Border.Shadow>
                              <Label x:Name="DupScannedStampLabel" Text="จัดส่ง" FontSize="32"
                                     FontAttributes="Bold" TextColor="#15803d" LineBreakMode="NoWrap"/>
                          </Border>
                        </Grid>
```

- [ ] **Step 5: Rewire the code-behind**

In `app/Views/OrderSearchPage.DuplicateOverlay.cs`, replace the whole simulation block (the comment above `ShowShipSimulation` through the end of `HideShipSimulation`):

```csharp
    // Hover simulation (Dismiss/Mark) — previews which leg ships before the
    // operator commits. Dismiss ships both (status quo); Mark ships the
    // non-target leg and voids (stamps + dims) _dupMarkTarget. Variation E:
    // the stamp lives on the leg's wrapper Grid, OUTSIDE the ScrollView, so it
    // stays centered in the viewport however far the leg is scrolled.
    private void ShowShipSimulation(bool markHover)
    {
        if (_dupMarkTarget is null) return;
        var siblingIsTarget = ReferenceEquals(_dupMarkTarget, _dupSibling);

        SetSimStamp(DupSiblingStamp, DupSiblingStampLabel,
            ships: ShipStampPolicy.LegShips(markHover, siblingIsTarget));
        SetSimStamp(DupScannedStamp, DupScannedStampLabel,
            ships: ShipStampPolicy.LegShips(markHover, !siblingIsTarget));

        DupSiblingColumn.Opacity = markHover && siblingIsTarget ? ShipStampPolicy.DimmedOpacity : 1;
        DupScannedColumn.Opacity = markHover && !siblingIsTarget ? ShipStampPolicy.DimmedOpacity : 1;
    }

    private static void SetSimStamp(Border stamp, Label label, bool ships)
    {
        var style = ShipStampPolicy.For(ships);
        var ink = Color.FromArgb(style.Ink);
        label.Text = style.Text;
        label.TextColor = ink;
        stamp.Stroke = ink;
        stamp.IsVisible = true;
    }

    private void HideShipSimulation()
    {
        DupSiblingStamp.IsVisible = false;
        DupScannedStamp.IsVisible = false;
        DupSiblingColumn.Opacity = 1;
        DupScannedColumn.Opacity = 1;
    }
```

`_dupMarkTarget` is always one of the two legs when the card is up (`ShowDuplicateOverlay` assigns it), so `!siblingIsTarget` is exactly "the scanned leg is the target". The null guard covers the impossible-but-cheap case of a hover arriving before the card is populated.

- [ ] **Step 6: Verify the old names are gone and the build is clean**

```powershell
Get-Process Warehouse -ErrorAction SilentlyContinue | Stop-Process
dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64
```

Expected: `Build succeeded`, 0 errors. (~149 pre-existing warnings are normal and not yours.)

Then confirm no stale references survive:

```powershell
Select-String -Path app -Include *.cs,*.xaml -Recurse -Pattern 'SimBadge|SimLabel|SetSimBadge'
```

Expected: **no output**. Any hit means an edit was missed.

- [ ] **Step 7: Verify line endings survived**

```powershell
git diff --stat
git diff --numstat app/Views/OrderSearchPage.xaml app/Views/OrderSearchPage.DuplicateOverlay.cs
```

Expected: a handful of changed lines per file — roughly 20–30 added / 15–20 removed. If `numstat` reports the *whole file* rewritten, the editor converted CRLF→LF: re-emit the file with CRLF endings before staging.

- [ ] **Step 8: Run the tests**

```powershell
dotnet test app.Tests/app.Tests.csproj
```

Expected: `Passed!` — 78 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add app/Views/OrderSearchPage.xaml app/Views/OrderSearchPage.DuplicateOverlay.cs
git commit -m "feat(app): centered ship stamp, viewport-anchored"
```

Do NOT stage `app/appsettings.json`, `.claude/*`, submodule pointers, `test.html`, `docker/compose.override.yml`, `.playwright-cli/`, `Scripts/qa/enroll-smoke.sh`, or `docs/plans/2026-06-26-shipping-options-filter.md` — that worktree churn is deliberate.

---

### Task 3: Live QA on the seeded duplicate matrix

**Files:** none changed unless a check fails. Fixes found here fold into a follow-up commit.

**Interfaces:**
- Consumes: the built app from Task 2.
- Produces: a pass/fail record for each row below.

Preconditions — verify, don't assume (the handoff's live state is 2 days old):

```powershell
curl.exe -s http://127.0.0.1:8080/health
docker exec warehouse-postgres psql -U warehouse_user -d warehouse_snapshot -c "SELECT order_number FROM packing_lists WHERE order_number LIKE 'QADUP%' ORDER BY 1;"
```

If the seeds are gone, re-seed (idempotent; ignore the `_qa` DB name in the script header):

```powershell
docker exec -i warehouse-postgres psql -U warehouse_user -d warehouse_snapshot -v ON_ERROR_STOP=1 < Scripts/qa/seed-dup-overlay-matrix.sql
```

Launch the app, then reach the QC/scan page. UI-driving gotchas that cost time before: the badge `LoginOverlay` is COM-scanner-only — bypass with the admin chord `Ctrl+Shift+A+M` sent as *held* keys via `keybd_event` (`SendKeys` cannot chord). For hover, **`SendInput` with absolute normalized coordinates** is reliable; `SetCursorPos` + `mouse_event` jiggle went flaky. Dismiss the card via its button, never `Esc` — QC keyboard shortcuts live on the page and stray keys mutate state.

- [ ] **Step 1: Normal case — sibling already processed (scan `QADUPSCN0002`)**

| Check | Expected |
|---|---|
| Hover **Dismiss** | Green `จัดส่ง` stamp centered on BOTH legs; neither leg dims |
| Hover **Mark** | Green `จัดส่ง` on the left (sibling) leg; rose `พัสดุซ้ำ` on the right (just-scanned) leg; right leg's content at 0.45 while the stamp stays full-strength |
| Move off both buttons | Both stamps disappear; both legs back to full opacity |

- [ ] **Step 2: Neither-processed flip (scan `QADUPSCN0005`)**

Expected: hovering **Mark** puts the rose `พัสดุซ้ำ` stamp on the **left (sibling)** leg and the green `จัดส่ง` on the right — the flip, because the parcel in hand ships. The amber "Neither parcel has been processed" banner must be showing. If the stamps land on the wrong legs here, the `!siblingIsTarget` argument in `ShowShipSimulation` is inverted.

- [ ] **Step 3: The caveat this task exists to kill — scroll anchoring (scan `QADUPSCN0006`, the mixed-tile seed)**

Scroll one leg's product tiles down until the leg header is off-screen, then hover **Mark**. Expected: the stamp is centered in the visible area of the leg, NOT scrolled away. Scroll further while hovering: the stamp stays put, the tiles move under it.

- [ ] **Step 4: Thai rendering**

Zoom in on both stamps. Expected: the vowel/tone marks in `จัดส่ง` (ั above จ, ่ above ส) and `พัสดุซ้ำ` (ั, ุ below ด, ้ and ำ) are fully drawn, not clipped by the border, and the text is not wrapped. If anything is clipped, raise the stamp `Border`'s vertical padding (`Padding="28,12"` → `28,16`) and re-check; do not reduce `FontSize`.

- [ ] **Step 5: Re-fire and reset**

Dismiss the card via its Dismiss button, rescan the same tracking. Expected: card returns with no stamps showing (`ShowDuplicateOverlay` calls `HideShipSimulation()`), legs at full opacity. Then actually Mark one parcel and rescan the survivor: expected no card at all (the `if (sibling.IsDuplicate) return;` re-fire guard from `adb988a`).

- [ ] **Step 6: Record the result**

If every row passed, note it in the branch's memory file and stop — no commit needed. If a fix was required, commit it:

```bash
git add app/Views/OrderSearchPage.xaml
git commit -m "fix(app): stamp padding for Thai marks"
```

- [ ] **Step 7: Clean up the seeds when QA is done**

```powershell
docker exec warehouse-postgres psql -U warehouse_user -d warehouse_snapshot -c "DELETE FROM import_rows WHERE order_number LIKE 'QADUP%'; DELETE FROM packing_lists WHERE order_number LIKE 'QADUP%';"
```

---

## After the plan

Not tasks — the user's calls, in order:

1. **One scoped code review over `adb988a..HEAD`.** Commits `72569da` and `3b384a3` were user-directed and controller-eyeballed only, never given a reviewer seat; this plan's commits join them. Use `superpowers:requesting-code-review` with that range before any merge.
2. **Integration** via `superpowers:finishing-a-development-branch` — the branch is 10+ commits ahead of `origin/feat/maui-dup-overlay-followups` (still at base `04f71cf`) and nothing has been pushed. PR #121 → #117 fold is still pending.
3. **Parked minors** from the redesign, none blocking: header copied-toast shifts the ship chip; copy-toast CTS race with no dispose; `MetaLine` separator spans lack `FontSize 11.5`; `DuplicateMarkPolicyTests` has no namespace; backend hardening — exclude Duplicate siblings from the reissue sum in `packing.rs`.
4. **Stale spec note:** `docs/mockups/2026-08-23-dup-overlay-redesign.html` no longer matches the build on three points (tile hover is a shadow glow not a lift; tooltips became the instant dark bubble; the ship simulation isn't in it at all). This plan adds a fourth. Worth a mockup refresh if the mockup is meant to stay authoritative.
