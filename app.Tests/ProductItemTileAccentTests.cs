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
