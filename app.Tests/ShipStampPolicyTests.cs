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
