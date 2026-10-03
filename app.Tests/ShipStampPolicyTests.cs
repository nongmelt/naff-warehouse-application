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

    // LegOutcomes pins the leg->verdict mapping in one place so a view-level
    // transposition (swapping the two SetSimStamp call sites) can't slip past
    // the test suite the way it could when the two legs were computed by two
    // independent LegShips calls at the call site.
    [Fact]
    public void LegOutcomes_DismissHover_BothLegsShip()
    {
        var outcome = ShipStampPolicy.LegOutcomes(markHover: false, siblingIsTarget: false);
        Assert.True(outcome.SiblingShips);
        Assert.True(outcome.ScannedShips);
    }

    [Fact]
    public void LegOutcomes_MarkHoverNormalCase_SiblingShipsScannedVoided()
    {
        // siblingIsTarget: false — the sibling is already processed, Mark
        // targets the just-scanned parcel.
        var outcome = ShipStampPolicy.LegOutcomes(markHover: true, siblingIsTarget: false);
        Assert.True(outcome.SiblingShips);
        Assert.False(outcome.ScannedShips);
    }

    [Fact]
    public void LegOutcomes_MarkHoverNeitherProcessedFlip_SiblingVoidedScannedShips()
    {
        // siblingIsTarget: true — neither leg is processed yet, so the parcel
        // in hand ships and Mark targets the sibling instead.
        var outcome = ShipStampPolicy.LegOutcomes(markHover: true, siblingIsTarget: true);
        Assert.False(outcome.SiblingShips);
        Assert.True(outcome.ScannedShips);
    }

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, ShipStampPolicy.DimmedOpacity)]
    public void OpacityFor_ShipsIsFullOpacityOtherwiseDimmed(bool ships, double expected)
        => Assert.Equal(expected, ShipStampPolicy.OpacityFor(ships), precision: 3);
}
