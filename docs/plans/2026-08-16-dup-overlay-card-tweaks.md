# Duplicate Order? Card Tweaks — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seven verified-in-grilling tweaks to the amber "Duplicate order?" card: QC-green product tiles, image hover, pilled tracking meta with operator nicknames, dual status pills, honest neither-processed display, working read-only nav arrows with view-only affordance, and copy-feedback toasts — plus a 5-order QA seed matrix to exercise every branch.

**Architecture:** All UI work is in the MAUI desktop app (`app/`), split across the card XAML (`OrderSearchPage.xaml` ~2898-3146), card logic (`OrderSearchPage.DuplicateOverlay.cs`), the shared image-peek overlay (`OrderSearchPage.ImageOverlay.cs` + XAML ~2287-2400), and computed display props on `ProductItem` (`app/Models/PackingList.cs`). No backend changes. Seeds go into the QA database `warehouse_snapshot_qa` only.

**Tech Stack:** .NET 10 MAUI (Windows-only), xUnit-style tests in `app.Tests/`, PostgreSQL via `docker exec` into container `warehouse-postgres`.

## Global Constraints

- Branch: work continues on the branch already carrying the 4 uncommitted QA fixes (`feat/maui-dup-overlay-followups` per handoff — verify with `rtk git branch` before starting; git snapshot earlier showed `fix/dashboard-alert-fixes`, so CHECK, don't assume).
- **NO git commits in any task.** Prior session left 4 verified fixes uncommitted in the same files by user preference ("not committing unless user asks"). Committing per-task would entangle them. Verification = build + tests + screenshots.
- `app/appsettings.json` is locally modified operator config — never stage it.
- Build check: `dotnet build app/app.csproj -c Debug -f net10.0-windows10.0.19041.0 -r win-x64` → expect 0 errors, 7 pre-existing warnings.
- Model tests: `dotnet test app.Tests/app.Tests.csproj` (48 pre-existing tests pass).
- **Fix D lesson (x:DataType):** the page-level `x:DataType="views:OrderSearchPage"` makes any `{Binding}` outside a correct scope silently no-op with a CLEAN build. `DupSiblingColumn`/`DupScannedColumn` declare `models:PackingList`; tile DataTemplates declare `models:ProductItem`. Every new `{Binding}` must sit under one of those scopes.
- Database: write ONLY to `warehouse_snapshot_qa`. NEVER touch `warehouse_snapshot` (pristine). Backend `http://127.0.0.1:8080` already points at the QA DB.
- Non-ASCII in XAML: use XML numeric escapes (`&#x2713;` style) for new symbols/emoji, matching file convention.
- Prefer `Edit` (surgical) over whole-file rewrites — repo stores CRLF blobs.
- After all code changes: `graphify update .` (project CLAUDE.md rule).

## Verified codebase facts (do not re-derive)

- `NavigateOverlayProduct` (`OrderSearchPage.ImageOverlay.cs:716-729`) early-returns when `_overlayReadOnly` — this is why arrows are dead on card peeks. It also walks `Results.SelectMany(...)`, which does NOT contain the sibling's products, and re-opens without `readOnly: true` (the #118 escape).
- `_overlayReadOnly` is declared at `app/Views/OrderSearchPage.xaml.cs:85`.
- `ProductItem` (in `app/Models/PackingList.cs`, starts ~line 134): `IsFullyPicked => Quantity <= 0`, `RequiredQuantity`, `VerifiedQuantity => RequiredQuantity - Quantity`. The `Quantity` setter raises a long list of `OnPropertyChanged` calls (~lines 146-173).
- `ParseProductsCore` (~line 862): for status QC Hold / QC Passed / Packed / Packing / Shipped WITH non-empty `updated_product_lists`, items come from the updated list (remaining-to-pick) with `RequiredQuantity` mapped from the original list. So a QC-passed parcel's items have `Quantity=0` → `IsFullyPicked=true`. A Packed-no-QC parcel with NULL `updated_product_lists` shows original quantities → NOT fully picked. This drives the green accent with zero new data plumbing.
- `PackingList`: `IsPackedComplete => (IsPacked || IsShipped) && CheckedBy set && all updated items zero` (line ~748). `CheckedAtDisplay`, `CreatedAtDisplay`, `UpdatedAtDisplay` exist; there is **no `PackedAt`** property — use `UpdatedAtDisplay` for packed-only time.
- `ApiService.ResolveOperatorNicknameAsync(staffCode)` (ApiService.cs:106) — cached, returns nickname or null (404/network). Endpoint `operator-lists/by-staff-code/{code}`.
- Backend fires `possibleReissue` (backend/src/api/packing.rs:380-385) whenever a sibling row EXISTS and summed parcel qty > ordered qty — **no check that the sibling was processed**. Detail endpoint: `GET /packing-lists/{tracking}`.
- QA DB existing seed: order `QADUP0001`, sibling `QADUPSIB0001` (Shipped, fake codes `26BKKQC099`/`26BKKWH099` — no operator rows, good nickname-fallback regression), scanned `QADUPSCN0001` (To be packed). `import_rows` row batch_id **354**, natural_key `QA:QADUP0001|seed`, qty 2. Products QASKU1 (rose PNG) / QASKU2 (blue) already enriched with MinIO images.
- Real active operators with nicknames (verified in QA DB): `25BKKPK049` = จู, `26BKKPK068` = เน.
- `trg_compute_all_items_cleared` computes `all_items_cleared` from `updated_product_lists` on insert — don't set manually.
- Peek overlay top bar (`OrderSearchPage.xaml` 2321-2395): left `HorizontalStackLayout` holds prev/next arrow Borders; right stack holds `OverlayNavHint` + Esc-close. `OverlayCard` at 2298 (`Stroke="Transparent"`, `StrokeThickness="0"`).
- Known pre-existing edge, OUT OF SCOPE: a bundle product tapped in read-only peek routes to `ShowBundleOverlay` whose pick path is only partially read-only-guarded (QC.cs:519-520). Seeds use simple products; do not expand scope.

---

### Task 1: Seed matrix (QADUP0002–QADUP0006)

**Files:**
- Create: `Scripts/qa/seed-dup-overlay-matrix.sql`

**Interfaces:**
- Produces: 5 orders, each `QADUPSIBnnnn` (sibling, varied status) + `QADUPSCNnnnn` (scan leg, To be packed). Later tasks screenshot against these trackings.

**Matrix (what each order verifies):**

| Order | Sibling status | Sibling fields | Verifies |
|---|---|---|---|
| QADUP0002 | QC Passed | packed_by จู, checked_by เน, updated zeroed | green tiles, nickname pill, green status pill |
| QADUP0003 | Shipped | + shipped_at/by, checked, updated zeroed | dual pill (QC Passed + Shipped) |
| QADUP0004 | Packed | packed_by จู only, updated NULL | "· packed" pill, NO green |
| QADUP0005 | To be packed | none | ◷ Other parcel header + amber banner + Created pill |
| QADUP0006 | QC Hold | checked_by เน, updated partial (QASKU1 done, QASKU2 not) | mixed green/gray tiles |

- [ ] **Step 1: Write the seed script**

Create `Scripts/qa/seed-dup-overlay-matrix.sql` with exactly:

```sql
-- Seed matrix for Duplicate Order? card tweaks (QADUP0002–QADUP0006).
-- Target DB: warehouse_snapshot_qa ONLY. Idempotent: wipes and re-creates
-- its own rows. QADUP0001 (original seed) is untouched.
BEGIN;

DELETE FROM import_rows   WHERE order_number IN ('QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006');
DELETE FROM packing_lists WHERE order_number IN ('QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006');

-- One order line per order: ordered_qty = 2. Parcel items sum to 6 (> 2) so
-- the reissue overflow fires for every pair.
INSERT INTO import_rows (batch_id, platform, raw_data, natural_key)
SELECT 354, 'Shopee',
       jsonb_build_object(
         'order_number', o,
         'quantity', '2',
         'seller_sku', 'QASKU1',
         'product_name', 'QA Duplicate Test Item',
         'shipping_options', 'Instant Delivery - QA seed (ส่งทันที)'),
       'QA:' || o || '|seed'
FROM unnest(ARRAY['QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006']) AS o;

-- Shared item payloads
-- original (3 units): QASKU1 x2 + QASKU2 x1
-- zeroed:   both quantities 0  (fully QC-verified)
-- partial:  QASKU1 0 (verified), QASKU2 1 (not yet)

-- QADUP0002 — sibling QC Passed
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, updated_product_lists, packed_by, packed_at, checked_by, checked_at,
   created_at, updated_at)
VALUES
('QADUPSIB0002','QADUP0002',3,'QC Passed','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '{"items": [{"quantity": 0, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 0, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '5 hours', '26BKKPK068', now() - interval '4 hours',
 now() - interval '6 hours', now() - interval '4 hours');

-- QADUP0003 — sibling Shipped with full QC trail (dual-pill case)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, updated_product_lists, packed_by, packed_at, checked_by, checked_at,
   shipped_by, shipped_at, created_at, updated_at)
VALUES
('QADUPSIB0003','QADUP0003',3,'Shipped','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '{"items": [{"quantity": 0, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 0, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '7 hours', '26BKKPK068', now() - interval '6 hours',
 '25BKKPK049', now() - interval '2 hours',
 now() - interval '8 hours', now() - interval '2 hours');

-- QADUP0004 — sibling Packed, never QC'd (no green expected)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, packed_by, packed_at, created_at, updated_at)
VALUES
('QADUPSIB0004','QADUP0004',3,'Packed','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '3 hours',
 now() - interval '4 hours', now() - interval '3 hours');

-- QADUP0005 — sibling ALSO To be packed (neither-processed banner case)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, created_at)
VALUES
('QADUPSIB0005','QADUP0005',3,'To be packed','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 now() - interval '90 minutes');

-- QADUP0006 — sibling QC Hold, partially verified (mixed tiles)
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, updated_product_lists, packed_by, packed_at, checked_by, checked_at,
   created_at, updated_at)
VALUES
('QADUPSIB0006','QADUP0006',3,'QC Hold','Shopee','Instant Delivery - QA seed (ส่งทันที)',
 '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '{"items": [{"quantity": 0, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
 '25BKKPK049', now() - interval '2 hours', '26BKKPK068', now() - interval '1 hour',
 now() - interval '3 hours', now() - interval '1 hour');

-- Scan legs: all To be packed, 3 units each, created "just now"
INSERT INTO packing_lists
  (tracking_number, order_number, total_items, packing_status, platform, shipping_options,
   product_lists, created_at)
SELECT 'QADUPSCN' || right(o, 4), o, 3, 'To be packed', 'Shopee',
       'Instant Delivery - QA seed (ส่งทันที)',
       '{"items": [{"quantity": 2, "seller_sku": "QASKU1", "product_name": "QA Duplicate Test Item", "product_variation": "Default"}, {"quantity": 1, "seller_sku": "QASKU2", "product_name": "QA Second Test Item", "product_variation": "Blue"}]}',
       now() - interval '10 minutes'
FROM unnest(ARRAY['QADUP0002','QADUP0003','QADUP0004','QADUP0005','QADUP0006']) AS o;

COMMIT;
```

- [ ] **Step 2: Run it (Bash tool — handles stdin redirect + UTF-8 cleanly)**

```bash
docker exec -i warehouse-postgres psql -U warehouse_user -d warehouse_snapshot_qa < Scripts/qa/seed-dup-overlay-matrix.sql
```

Expected: `BEGIN`, 2× `DELETE`, 7× `INSERT`, `COMMIT`, no errors.

- [ ] **Step 3: Verify reissue fires for all 5 scan legs**

```bash
for t in QADUPSCN0002 QADUPSCN0003 QADUPSCN0004 QADUPSCN0005 QADUPSCN0006; do
  curl -s "http://127.0.0.1:8080/packing-lists/$t" | grep -o '"possibleReissue":[a-z]*'
done
```

Expected: five lines of `"possibleReissue":true`. If any `false`: check `import_rows` row landed (ordered_qty must be > 0) and both legs exist.

---

### Task 2: ProductItem tile-accent properties (TDD)

**Files:**
- Modify: `app/Models/PackingList.cs` (ProductItem class, ~line 134+; Quantity setter ~146-173)
- Test: `app.Tests/ProductItemTileAccentTests.cs` (new)

**Interfaces:**
- Produces (consumed by Task 3 XAML bindings): on `ProductItem` —
  `Color TileBorderColor`, `double TileBorderWidth`, `Color TileQtyBadgeBg`, `Color TileQtyBadgeTextColor`, `string TileQtyDisplay`.

- [ ] **Step 1: Write the failing tests**

Create `app.Tests/ProductItemTileAccentTests.cs` (match the existing test framework in `app.Tests/` — expected xUnit; adapt asserts only if the project uses another):

```csharp
using app.Models;
using Microsoft.Maui.Graphics;
using Xunit;

namespace app.Tests;

public class ProductItemTileAccentTests
{
    private static ProductItem Item(int required, int remaining) =>
        new() { RequiredQuantity = required, Quantity = remaining };

    [Fact]
    public void Verified_item_gets_green_tile_accent()
    {
        var item = Item(2, 0);
        Assert.Equal(Color.FromArgb("#22c55e"), item.TileBorderColor);
        Assert.Equal(2d, item.TileBorderWidth);
        Assert.Equal(Color.FromArgb("#dcfce7"), item.TileQtyBadgeBg);
        Assert.Equal(Color.FromArgb("#166534"), item.TileQtyBadgeTextColor);
        Assert.Equal("✓ ×2", item.TileQtyDisplay);
    }

    [Fact]
    public void Unverified_item_keeps_neutral_tile()
    {
        var item = Item(2, 2);
        Assert.Equal(Color.FromArgb("#e5e7eb"), item.TileBorderColor);
        Assert.Equal(1d, item.TileBorderWidth);
        Assert.Equal(Color.FromArgb("#111827"), item.TileQtyBadgeBg);
        Assert.Equal(Colors.White, item.TileQtyBadgeTextColor);
        Assert.Equal("×2", item.TileQtyDisplay);
    }

    [Fact]
    public void Quantity_change_raises_tile_accent_notifications()
    {
        var item = Item(2, 2);
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        item.Quantity = 0;
        Assert.Contains(nameof(ProductItem.TileBorderColor), raised);
        Assert.Contains(nameof(ProductItem.TileBorderWidth), raised);
        Assert.Contains(nameof(ProductItem.TileQtyBadgeBg), raised);
        Assert.Contains(nameof(ProductItem.TileQtyBadgeTextColor), raised);
        Assert.Contains(nameof(ProductItem.TileQtyDisplay), raised);
    }
}
```

- [ ] **Step 2: Run tests — verify they FAIL**

Run: `dotnet test app.Tests/app.Tests.csproj`
Expected: compile errors — `TileBorderColor` etc. not defined. (Compile failure IS the red state here.)

- [ ] **Step 3: Implement the properties**

In `app/Models/PackingList.cs`, inside `ProductItem`, near the other card-color computed props (after `IsCompleted`, ~line 239), add:

```csharp
    // ── Duplicate-card tile accents (spec §13.6 tweaks): a QC-verified item's
    // photo tile goes green so the operator sees at a glance what was already
    // checked on the sibling parcel. ──────────────────────────────────────────
    [JsonIgnore] public Color TileBorderColor =>
        IsFullyPicked ? Color.FromArgb("#22c55e") : Color.FromArgb("#e5e7eb");

    [JsonIgnore] public double TileBorderWidth => IsFullyPicked ? 2 : 1;

    [JsonIgnore] public Color TileQtyBadgeBg =>
        IsFullyPicked ? Color.FromArgb("#dcfce7") : Color.FromArgb("#111827");

    [JsonIgnore] public Color TileQtyBadgeTextColor =>
        IsFullyPicked ? Color.FromArgb("#166534") : Colors.White;

    [JsonIgnore] public string TileQtyDisplay =>
        IsFullyPicked ? $"✓ ×{RequiredQuantity}" : $"×{RequiredQuantity}";
```

In the `Quantity` setter's notification block (after the existing `OnPropertyChanged(nameof(SkuPillText));` line, ~line 171), add:

```csharp
            OnPropertyChanged(nameof(TileBorderColor));
            OnPropertyChanged(nameof(TileBorderWidth));
            OnPropertyChanged(nameof(TileQtyBadgeBg));
            OnPropertyChanged(nameof(TileQtyBadgeTextColor));
            OnPropertyChanged(nameof(TileQtyDisplay));
```

- [ ] **Step 4: Run tests — verify PASS**

Run: `dotnet test app.Tests/app.Tests.csproj`
Expected: all pass (48 pre-existing + 3 new).

---

### Task 3: Card XAML — tile green accent + hover effects

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — BOTH tile DataTemplates (sibling ~2994-3027, scanned ~3059-3092) and copy targets (`DupOrderNumber` ~2945, tracking Labels ~2984 and ~3049)

**Interfaces:**
- Consumes: `TileBorderColor`, `TileBorderWidth`, `TileQtyBadgeBg`, `TileQtyBadgeTextColor`, `TileQtyDisplay` from Task 2.

All edits below apply **identically to both column templates** (they are duplicated blocks — apply twice).

- [ ] **Step 1: Bind the two image Borders' stroke**

In each tile template, both image Border variants (the `LocalImagePath` one and the `HasNoLocalImage` fallback) change from:

```xml
<Border StrokeShape="RoundRectangle 12" Stroke="#e5e7eb"
        StrokeThickness="1" BackgroundColor="#ffffff" Padding="0">
```

to:

```xml
<Border StrokeShape="RoundRectangle 12" Stroke="{Binding TileBorderColor}"
        StrokeThickness="{Binding TileBorderWidth}" BackgroundColor="#ffffff" Padding="0">
```

(4 Border edits total: 2 per template. The fallback Border keeps its `IsVisible="{Binding HasNoLocalImage}"` attribute.)

- [ ] **Step 2: Bind the qty badge**

In each template the badge block changes from:

```xml
<Border HorizontalOptions="End" VerticalOptions="End"
        BackgroundColor="#111827" Stroke="White" StrokeThickness="2"
        StrokeShape="RoundRectangle 999" Padding="6,1" Margin="0,0,-4,-4">
    <Label Text="{Binding RequiredQuantity, StringFormat='&#215;{0}'}"
           TextColor="White" FontFamily="Consolas"
           FontAttributes="Bold" FontSize="12"/>
</Border>
```

to:

```xml
<Border HorizontalOptions="End" VerticalOptions="End"
        BackgroundColor="{Binding TileQtyBadgeBg}" Stroke="White" StrokeThickness="2"
        StrokeShape="RoundRectangle 999" Padding="6,1" Margin="0,0,-4,-4">
    <Label Text="{Binding TileQtyDisplay}"
           TextColor="{Binding TileQtyBadgeTextColor}" FontFamily="Consolas"
           FontAttributes="Bold" FontSize="12"/>
</Border>
```

- [ ] **Step 3: Hover scale on tile root**

In each template, immediately inside the root `<VerticalStackLayout Spacing="6" Margin="0,0,14,14" WidthRequest="86">` (before its `GestureRecognizers`), add:

```xml
<VisualStateManager.VisualStateGroups>
    <VisualStateGroupList>
        <VisualStateGroup x:Name="CommonStates">
            <VisualState x:Name="Normal">
                <VisualState.Setters><Setter Property="Scale" Value="1"/></VisualState.Setters>
            </VisualState>
            <VisualState x:Name="PointerOver">
                <VisualState.Setters><Setter Property="Scale" Value="1.05"/></VisualState.Setters>
            </VisualState>
        </VisualStateGroup>
    </VisualStateGroupList>
</VisualStateManager.VisualStateGroups>
```

- [ ] **Step 4: Hover opacity on copy targets**

Add the same VSM structure but with `Opacity` `1` / `0.7` setters to three Labels: `DupOrderNumber` (header), and the two `{Binding TrackingNumber}` Labels (one per column):

```xml
<VisualStateManager.VisualStateGroups>
    <VisualStateGroupList>
        <VisualStateGroup x:Name="CommonStates">
            <VisualState x:Name="Normal">
                <VisualState.Setters><Setter Property="Opacity" Value="1"/></VisualState.Setters>
            </VisualState>
            <VisualState x:Name="PointerOver">
                <VisualState.Setters><Setter Property="Opacity" Value="0.7"/></VisualState.Setters>
            </VisualState>
        </VisualStateGroup>
    </VisualStateGroupList>
</VisualStateManager.VisualStateGroups>
```

- [ ] **Step 5: Build**

Run: `dotnet build app/app.csproj -c Debug -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: 0 errors, 7 pre-existing warnings.

- [ ] **Step 6: Visual spot-check (Task 8 does the full sweep)**

Launch `app\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Warehouse.exe`, search `QADUPSCN0002` (press `/`, type, Enter). Screenshot via uiact.ps1 (see Task 8 Step 1 for the script). Expect: sibling column tiles have green borders + green `✓ ×2` / `✓ ×1` badges; scanned column tiles stay gray `×2` / `×1`. Move cursor over a tile (`move` action) + screenshot → tile visibly enlarged.

---

### Task 4: Pill row with operator nickname

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — replace `DupSiblingMeta` (~2990) and `DupScannedMeta` (~3055) Labels
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — `CheckReissueAsync`, `ShowDuplicateOverlay`

**Interfaces:**
- Consumes: `ApiService.ResolveOperatorNicknameAsync(string?)` → `Task<string?>` (cached; null on unknown code).
- Produces: `ShowDuplicateOverlay(PackingList scanned, PackingList sibling, string? siblingOperatorName)` — new signature; Task 5 edits the same method body, apply in order.

- [ ] **Step 1: XAML — sibling pill row**

Replace `<Label x:Name="DupSiblingMeta" FontSize="11.5" TextColor="#6b7280" Margin="0,2,0,10"/>` with:

```xml
<HorizontalStackLayout Spacing="6" Margin="0,4,0,10">
    <Border x:Name="DupSiblingOpPill" BackgroundColor="#f3f4f6" Stroke="Transparent"
            StrokeShape="RoundRectangle 5" Padding="8,3" IsVisible="False">
        <Label x:Name="DupSiblingOpLabel" FontSize="11.5" FontAttributes="Bold" TextColor="#374151"/>
    </Border>
    <Border x:Name="DupSiblingTimePill" BackgroundColor="#f3f4f6" Stroke="Transparent"
            StrokeShape="RoundRectangle 5" Padding="8,3" IsVisible="False">
        <Label x:Name="DupSiblingTimeLabel" FontSize="11.5" FontAttributes="Bold" TextColor="#374151"/>
    </Border>
    <Border BackgroundColor="#f3f4f6" Stroke="Transparent"
            StrokeShape="RoundRectangle 5" Padding="8,3">
        <Label x:Name="DupSiblingCountLabel" FontSize="11.5" FontAttributes="Bold" TextColor="#374151"/>
    </Border>
</HorizontalStackLayout>
```

- [ ] **Step 2: XAML — scanned pill row**

Replace `<Label x:Name="DupScannedMeta" FontSize="11.5" TextColor="#6b7280" Margin="0,2,0,10"/>` with:

```xml
<HorizontalStackLayout Spacing="6" Margin="0,4,0,10">
    <Border BackgroundColor="#ffedd5" Stroke="Transparent"
            StrokeShape="RoundRectangle 5" Padding="8,3">
        <Label Text="&#x25CF; Just now" FontSize="11.5" FontAttributes="Bold" TextColor="#c2410c"/>
    </Border>
    <Border BackgroundColor="#f3f4f6" Stroke="Transparent"
            StrokeShape="RoundRectangle 5" Padding="8,3">
        <Label x:Name="DupScannedCountLabel" FontSize="11.5" FontAttributes="Bold" TextColor="#374151"/>
    </Border>
</HorizontalStackLayout>
```

- [ ] **Step 3: Resolve nickname in CheckReissueAsync**

In `OrderSearchPage.DuplicateOverlay.cs`, after `await EnrichProductItemsAsync(sibling.ParsedProducts);` (line ~59) and BEFORE the re-check, add:

```csharp
        // Operator pill shows the human nickname, not the raw staff_code.
        // Cached lookup; falls back to the code itself when unknown (e.g. the
        // QADUP0001 seed's fake codes).
        var opCode = !string.IsNullOrWhiteSpace(sibling.CheckedBy) ? sibling.CheckedBy : sibling.PackedBy;
        var opName = await ApiService.ResolveOperatorNicknameAsync(opCode) ?? opCode;
```

Change the raise line from:

```csharp
        MainThread.BeginInvokeOnMainThread(() => ShowDuplicateOverlay(scanned, sibling));
```

to:

```csharp
        MainThread.BeginInvokeOnMainThread(() => ShowDuplicateOverlay(scanned, sibling, opName));
```

- [ ] **Step 4: Populate pills in ShowDuplicateOverlay**

Change the signature to `private void ShowDuplicateOverlay(PackingList scanned, PackingList sibling, string? siblingOperatorName)` and replace the two meta lines:

```csharp
        DupSiblingMeta.Text =
            $"Checked by {sibling.CheckedByDisplay} · {sibling.CheckedAtDisplay} · {sibling.ParsedProducts.Count} products";
        DupScannedMeta.Text = $"Just now · {scanned.ParsedProducts.Count} products";
```

with:

```csharp
        // Pill row: operator (nickname) + time + product count. Unprocessed
        // sibling has no operator — show its creation time instead.
        var hasChecked = !string.IsNullOrWhiteSpace(sibling.CheckedBy);
        var hasPacked  = !string.IsNullOrWhiteSpace(sibling.PackedBy);
        if (hasChecked || hasPacked)
        {
            DupSiblingOpPill.IsVisible = true;
            DupSiblingOpLabel.Text = hasChecked
                ? $"\U0001F464 {siblingOperatorName}"
                : $"\U0001F464 {siblingOperatorName} · packed";
            DupSiblingTimePill.IsVisible = true;
            // No PackedAt on the model — updated_at is the pack-time proxy.
            DupSiblingTimeLabel.Text =
                "\U0001F550 " + (hasChecked ? sibling.CheckedAtDisplay : sibling.UpdatedAtDisplay);
        }
        else
        {
            DupSiblingOpPill.IsVisible = false;
            DupSiblingTimePill.IsVisible = true;
            DupSiblingTimeLabel.Text = $"Created {sibling.CreatedAtDisplay}";
        }
        DupSiblingCountLabel.Text = $"\U0001F4E6 {sibling.ParsedProducts.Count} products";
        DupScannedCountLabel.Text = $"\U0001F4E6 {scanned.ParsedProducts.Count} products";
```

- [ ] **Step 5: Build + spot-check**

Build (same command, 0 errors). Search `QADUPSCN0002` → sibling pills: `👤 เน`, `🕐 <time>`, `📦 3 products`; scanned: `● Just now`, `📦 3 products`. Search `QADUPSCN0004` → `👤 จู · packed`. Search `QADUPSCN0001` (regression) → op pill shows raw `26BKKQC099` (fallback path).

---

### Task 5: Dual QC pill + dynamic header + neither-processed banner

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — column header Grids (~2974-2983 sibling, ~3039-3047 scanned), card outer Grid (~2927), header label (~2975)
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — `ShowDuplicateOverlay` (after Task 4's edits)

**Interfaces:**
- Consumes: `PackingList.IsPackedComplete` (bool, existing), Task 4's `ShowDuplicateOverlay` 3-arg signature.
- Produces: `x:Name` elements `DupSiblingHeaderLabel`, `DupBothUnprocessedBanner`.

- [ ] **Step 1: Dual pill — both column header Grids**

Each column's header Grid changes from `ColumnDefinitions="*,Auto"` to `ColumnDefinitions="*,Auto,Auto"`. Insert a QC pill at Column 1 and move the status pill to Column 2. Sibling column (scanned is identical except it keeps its own `● Just scanned` label at Column 0):

```xml
<Grid ColumnDefinitions="*,Auto,Auto">
    <Label Grid.Column="0" x:Name="DupSiblingHeaderLabel" Text="&#x2713; Already processed"
           FontSize="15" FontAttributes="Bold" TextColor="#166534"
           VerticalOptions="Center"/>
    <Border Grid.Column="1" BackgroundColor="#dcfce7" Stroke="Transparent"
            StrokeShape="RoundRectangle 5" Padding="10,3" Margin="0,0,6,0"
            IsVisible="{Binding IsPackedComplete}">
        <Label Text="QC Passed" FontSize="12" FontAttributes="Bold" TextColor="#166534"/>
    </Border>
    <Border Grid.Column="2" BackgroundColor="{Binding StatusBgColor}"
            Stroke="Transparent" StrokeShape="RoundRectangle 5" Padding="10,3">
        <Label Text="{Binding StatusDisplay}" FontSize="12" FontAttributes="Bold"
               TextColor="{Binding StatusFgColor}"/>
    </Border>
</Grid>
```

(The scanned column gets the same two-pill right side; its Column 0 Label `● Just scanned` is unchanged and unnamed.) Both Grids sit under the columns' `x:DataType="models:PackingList"` scope, so `IsPackedComplete` binds correctly.

- [ ] **Step 2: Banner row**

Card outer Grid (~line 2927) changes `RowDefinitions="Auto,*,Auto"` → `RowDefinitions="Auto,Auto,*,Auto"`. The comparison Grid moves `Grid.Row="1"` → `Grid.Row="2"`; footer Grid `Grid.Row="2"` → `Grid.Row="3"`. Insert at Row 1:

```xml
<Border Grid.Row="1" x:Name="DupBothUnprocessedBanner" BackgroundColor="#fff7ed"
        Stroke="Transparent" Padding="26,10" IsVisible="False">
    <Label Text="&#x26A0; Neither parcel has been processed — check which label is live before marking one as duplicate."
           FontSize="12.5" FontAttributes="Bold" TextColor="#9a3412"/>
</Border>
```

- [ ] **Step 3: Drive header + banner from code**

In `ShowDuplicateOverlay`, after the pill-row block from Task 4, add:

```csharp
        // §13.6 honesty fix: the backend fires possibleReissue on qty overflow
        // alone — the sibling may itself be unprocessed. Don't claim
        // "Already processed" when it isn't.
        var siblingProcessed = !string.Equals(
            sibling.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase);
        DupSiblingHeaderLabel.Text = siblingProcessed
            ? "✓ Already processed"
            : "◷ Other parcel";
        DupSiblingHeaderLabel.TextColor = Color.FromArgb(siblingProcessed ? "#166534" : "#6b7280");
        DupBothUnprocessedBanner.IsVisible = !siblingProcessed && string.Equals(
            scanned.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 4: Build + spot-check**

Build (0 errors). `QADUPSCN0005` → left header gray `◷ Other parcel`, amber banner visible, time pill `Created <ts>`. `QADUPSCN0003` → dual pill `QC Passed` + `Shipped`, no banner, green header. `QADUPSCN0002` → single green `QC Passed` status pill (status is QC Passed, `IsPackedComplete` false — correct), no banner.

---

### Task 6: Read-only peek — working arrows, view-only pill, green stroke

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml.cs:85` (field)
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — `OnDuplicateProductTapped`
- Modify: `app/Views/OrderSearchPage.ImageOverlay.cs` — `ShowProductImageOverlay`, item-position block, `NavigateOverlayProduct`
- Modify: `app/Views/OrderSearchPage.xaml` — peek top bar (~2327-2356)

**Interfaces:**
- Consumes: `_dupSibling`/`_dupScanned` fields, `ProductItem.IsFullyPicked`.
- Produces: field `IList<ProductItem>? _overlayReadOnlyList`; `x:Name` `OverlayReadOnlyPill`.

- [ ] **Step 1: Field**

In `OrderSearchPage.xaml.cs` next to line 85's `private bool _overlayReadOnly;` add:

```csharp
    // Read-only peeks navigate within this fixed list (the card parcel's own
    // products) instead of Results, which never contains the sibling's items.
    private IList<ProductItem>? _overlayReadOnlyList;
```

- [ ] **Step 2: Capture the source list on card tap**

Replace `OnDuplicateProductTapped` in `OrderSearchPage.DuplicateOverlay.cs`:

```csharp
    // Click a product photo → re-open the existing QC image viewer on top of the
    // card (ZIndex 8 > 7). Read-only peek; the viewer's picking state doesn't
    // apply to a card parcel. Prev/next arrows browse the tapped parcel's own
    // products, staying read-only (#118).
    private void OnDuplicateProductTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement { BindingContext: ProductItem item })
        {
            var source = _dupSibling?.ParsedProducts.Contains(item) == true ? _dupSibling : _dupScanned;
            _overlayReadOnlyList = source?.ParsedProducts;
            ShowProductImageOverlay(item, "duplicate_card_peek", readOnly: true);
        }
    }
```

- [ ] **Step 3: ShowProductImageOverlay — clear stale list, pill, stroke**

In `OrderSearchPage.ImageOverlay.cs` `ShowProductImageOverlay`, directly after `_overlayReadOnly = readOnly;` (line 29):

```csharp
        if (!readOnly) _overlayReadOnlyList = null;
```

Replace the item-position block (lines ~79-90):

```csharp
        // Item position (e.g., "ITEM 03 of 14"). Read-only peeks position
        // within the card parcel's list — its items are not in Results.
        if (_overlayReadOnly && _overlayReadOnlyList is { Count: > 0 } roList)
        {
            OverlayItemPosition.Text = $"ITEM {roList.IndexOf(item) + 1:D2} of {roList.Count}";
        }
        else
        {
            var order = FindOrderForItem(item);
            if (order != null)
            {
                var idx = order.ParsedProducts.IndexOf(item) + 1;
                var total = order.ParsedProducts.Count;
                OverlayItemPosition.Text = $"ITEM {idx:D2} of {total}";
            }
            else
            {
                OverlayItemPosition.Text = "";
            }
        }
```

After the `OverlayMinusBtn/OverlayPlusBtn` lines (~147-148), add:

```csharp
        OverlayReadOnlyPill.IsVisible = readOnly;

        // QC-verified item peeked from the card: carry the green accent onto
        // the viewer itself. Non-verified/pick-mode opens reset to no stroke
        // (the completion flash manages its own stroke later).
        if (readOnly && item.IsFullyPicked)
        {
            OverlayCard.Stroke = Color.FromArgb("#22c55e");
            OverlayCard.StrokeThickness = 3;
        }
        else
        {
            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
        }
```

- [ ] **Step 4: NavigateOverlayProduct — read-only-safe nav**

Replace the method (lines ~716-729):

```csharp
    private void NavigateOverlayProduct(int direction)
    {
        if (_overlayItem == null) return;

        // #118: read-only peeks used to block nav outright because advancing
        // re-opened the overlay in pick mode. Navigate the card parcel's own
        // list instead, staying read-only.
        if (_overlayReadOnly)
        {
            if (_overlayReadOnlyList is not { Count: > 0 } list) return;
            var idx = list.IndexOf(_overlayItem);
            if (idx < 0) return;
            var next = idx + direction;
            if (next < 0) next = list.Count - 1;
            if (next >= list.Count) next = 0;
            ShowProductImageOverlay(list[next], readOnly: true);
            return;
        }

        var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
        var currentIdx = allProducts.IndexOf(_overlayItem);
        if (currentIdx < 0) return;

        int nextIdx = currentIdx + direction;
        if (nextIdx < 0) nextIdx = allProducts.Count - 1;
        if (nextIdx >= allProducts.Count) nextIdx = 0;

        ShowProductImageOverlay(allProducts[nextIdx]);
    }
```

- [ ] **Step 5: XAML — view-only pill**

In the peek top bar's LEFT `HorizontalStackLayout` (the one holding the two arrow Borders, ~2327-2356), after the second (next-arrow) Border, add:

```xml
<Border x:Name="OverlayReadOnlyPill" BackgroundColor="#f3f4f6" Stroke="#e5e7eb"
        StrokeThickness="1" StrokeShape="RoundRectangle 4" Padding="10,5"
        Margin="8,0,0,0" IsVisible="False">
    <Label Text="&#x1F441; View only" FontSize="13" FontAttributes="Bold" TextColor="#6b7280"/>
</Border>
```

Also check `OrderSearchPage.QC.cs:519-520` (bundle path re-applies `_overlayReadOnly` to +/-): add `OverlayReadOnlyPill.IsVisible = _overlayReadOnly;` beside those two lines so the pill stays consistent on that path too.

- [ ] **Step 6: Build + spot-check**

Build (0 errors). `QADUPSCN0002` → click a sibling tile: peek opens with green stroke + `👁 View only` pill + `ITEM 01 of 02`; arrows cycle both sibling products, stay read-only (+/- never appear), position updates. Click a SCANNED tile: no green stroke, arrows cycle scanned's 2 products only. Esc closes. Reopen a search-results product image normally (click product card outside the dup overlay) → no pill, nav works as before across Results.

---

### Task 7: Copy toasts

**Files:**
- Modify: `app/Views/OrderSearchPage.xaml` — order-number header (~2942-2951), both tracking Labels
- Modify: `app/Views/OrderSearchPage.DuplicateOverlay.cs` — copy handlers + helper

**Interfaces:**
- Produces: `x:Name` `DupOrderCopiedToast`, `DupSiblingCopiedToast`, `DupScannedCopiedToast`; helper `Task ShowDupCopiedToastAsync(VisualElement toast)`.

- [ ] **Step 1: XAML — order-number toast**

The header Grid (~2942) has `ColumnDefinitions="Auto,Auto,*,Auto,Auto"` with an unused `*` Column 2. Add there:

```xml
<Border Grid.Column="2" x:Name="DupOrderCopiedToast" BackgroundColor="#E6111827"
        Stroke="Transparent" StrokeShape="RoundRectangle 6" Padding="8,3"
        HorizontalOptions="Start" VerticalOptions="Center"
        IsVisible="False" Opacity="0" InputTransparent="True">
    <Label Text="&#x2713; Copied" FontSize="11" FontAttributes="Bold" TextColor="White"/>
</Border>
```

- [ ] **Step 2: XAML — tracking toasts**

Wrap each column's tracking Label (which now carries Task 3's hover VSM) in a Grid with an overlaid toast. Sibling column:

```xml
<Grid>
    <Label Text="{Binding TrackingNumber}" FontSize="19" FontAttributes="Bold"
           TextColor="#111827" FontFamily="Consolas" LineBreakMode="CharacterWrap">
        <!-- keep the existing GestureRecognizers + hover VSM exactly as-is -->
    </Label>
    <Border x:Name="DupSiblingCopiedToast" BackgroundColor="#E6111827"
            Stroke="Transparent" StrokeShape="RoundRectangle 6" Padding="8,3"
            HorizontalOptions="End" VerticalOptions="Start"
            IsVisible="False" Opacity="0" InputTransparent="True">
        <Label Text="&#x2713; Copied" FontSize="11" FontAttributes="Bold" TextColor="White"/>
    </Border>
</Grid>
```

Scanned column: identical wrap with `x:Name="DupScannedCopiedToast"`.

- [ ] **Step 3: Handlers + helper**

In `OrderSearchPage.DuplicateOverlay.cs`, replace both copy handlers and add the helper:

```csharp
    private CancellationTokenSource? _dupToastCts;

    // The search-status confirmation sits BEHIND the card backdrop, so copy
    // feedback surfaces as a tiny toast next to the tapped value instead.
    private async Task ShowDupCopiedToastAsync(VisualElement toast)
    {
        _dupToastCts?.Cancel();
        var cts = _dupToastCts = new CancellationTokenSource();
        try
        {
            toast.Opacity = 0;
            toast.IsVisible = true;
            await toast.FadeToAsync(1, 120, Easing.CubicOut);
            await Task.Delay(900, cts.Token);
            await toast.FadeToAsync(0, 180, Easing.CubicIn);
        }
        catch (TaskCanceledException) { }
        finally
        {
            toast.IsVisible = false;
            toast.Opacity = 0;
        }
    }

    private async void OnDuplicateCopyOrderTapped(object sender, TappedEventArgs e)
    {
        if (_dupScanned is null) return;
        await Clipboard.Default.SetTextAsync(_dupScanned.OrderNumber);
        UpdateSearchStatus($"Copied  {_dupScanned.OrderNumber}");
        await ShowDupCopiedToastAsync(DupOrderCopiedToast);
    }

    private async void OnDuplicateCopyTrackingTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement { BindingContext: PackingList pl })
        {
            await Clipboard.Default.SetTextAsync(pl.TrackingNumber);
            UpdateSearchStatus($"Copied  {pl.TrackingNumber}");
            await ShowDupCopiedToastAsync(
                ReferenceEquals(pl, _dupSibling) ? DupSiblingCopiedToast : DupScannedCopiedToast);
        }
    }
```

- [ ] **Step 4: Build + spot-check**

Build (0 errors). On any card: click sibling tracking → `✓ Copied` toast fades in/out at that label; click order number → toast beside it; rapid double-click → no stuck toast (cancellation path). Verify clipboard actually holds the value (paste into search bar or check with `Get-Clipboard`).

---

### Task 8: Full visual QA sweep

**Files:**
- Create: `<session-scratchpad>\uiact.ps1` (driver — recreate; shell state does not persist)

- [ ] **Step 1: Recreate the UI driver**

Write `<session-scratchpad>\uiact.ps1` (self-contained; screen is 1920×1080):

```powershell
param([string]$do, [int]$x, [int]$y, [string]$text, [string]$shot)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr i);
}
"@
$p = Get-Process Warehouse -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) { [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300 }
switch ($do) {
  'click' { [Win]::SetCursorPos($x,$y) | Out-Null; Start-Sleep -Milliseconds 120
            [Win]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [Win]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
  'type'  { [System.Windows.Forms.SendKeys]::SendWait($text) }
  'move'  { [Win]::SetCursorPos($x,$y) | Out-Null }
}
if ($shot) {
  Start-Sleep -Milliseconds 400
  $bmp = New-Object System.Drawing.Bitmap 1920,1080
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0,0,0,0,$bmp.Size)
  $bmp.Save($shot,[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
```

Flow per order: `uiact.ps1 -do type -text "/"` → `-do type -text "QADUPSCNnnnn{ENTER}"` → screenshot → Read the PNG to SEE it. **CAUTION (handoff):** stray keystrokes with the QC picking overlay open mutate QC state; if it happens, click Reset (~1859,154).

- [ ] **Step 2: Run the checklist — one screenshot (minimum) per row, READ each**

| Search | Must show |
|---|---|
| QADUPSCN0002 | Green header ✓ Already processed; QC Passed status pill; pills 👤 เน / 🕐 / 📦 3 products; all sibling tiles green border + ✓×N green badge; scanned tiles gray; hover (move to tile, reshoot) scales it; sibling-tile peek = green stroke + 👁 View only + arrows cycle 2 items read-only |
| QADUPSCN0003 | Dual pill QC Passed + Shipped on sibling; green tiles |
| QADUPSCN0004 | Packed pill only; 👤 จู · packed; UpdatedAt time pill; NO green tiles |
| QADUPSCN0005 | Gray ◷ Other parcel header; amber ⚠ neither-processed banner; Created time pill; no operator pill |
| QADUPSCN0006 | QC Hold pill; mixed tiles — QASKU1 green ✓×2, QASKU2 gray ×1 |
| QADUPSCN0001 | Regression: card still raises; operator pill falls back to raw 26BKKQC099; Dismiss + Mark hover states intact (Fix A) |

Also on any card: tracking-number copy toast, order-number copy toast, tracking-label hover dim, backdrop-tap dismiss still works.

- [ ] **Step 3: Full-suite verification**

Run: `dotnet test app.Tests/app.Tests.csproj` → all green.
Run: `dotnet build app/app.csproj -c Debug -f net10.0-windows10.0.19041.0 -r win-x64` → 0 errors, 7 pre-existing warnings.
Run: `graphify update .`

- [ ] **Step 4: Report**

Summarize per-checklist-row pass/fail with screenshot paths. Do NOT commit — user decides integration (superpowers:finishing-a-development-branch when asked).

---

## Self-review notes (already applied)

- Spec coverage: hover (T3), pilled info + nickname (T4), nav arrows (T6), seeds (T1), QC green incl. peek accent (T2+T3+T6), status pills incl. dual (T5), neither-processed display (T5 + seed 0005). Copy toasts + view-only pill + copy-target hover from grilling Q5b/Q9 (T3/T6/T7).
- Type consistency: `ShowDuplicateOverlay(scanned, sibling, siblingOperatorName)` defined in T4, reused in T5. `_overlayReadOnlyList` is `IList<ProductItem>` (needs `IndexOf`; `ObservableCollection<ProductItem>` satisfies it) — defined T6 Step 1, used Steps 2-4.
- Known accepted quirk: navigating read-only peek emits no `image_peek` telemetry per step (matches existing non-trigger nav behavior at line 728).
