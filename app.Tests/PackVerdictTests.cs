using app.Services;
using Xunit;

namespace app.Tests;

public class PackVerdictTests
{
    [Theory]
    [InlineData("Packed")]     // ideal: QC done then packed
    [InlineData("QC Passed")]  // no-video: QC done, packing not recorded
    [InlineData("Packing")]    // no-video: mid-pack
    public void Cleared_shippable_status_ships(string status)
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: status, allItemsCleared: true);
        Assert.Equal(PackOutcome.Ship, v.Outcome);
        Assert.True(v.ShouldWrite);
        Assert.Equal("SHIPPED", v.Word);
        Assert.Equal(PackVerdict.ColorGreen, v.Color);
    }

    [Theory]
    [InlineData("To be packed")]
    [InlineData("Packed")]
    [InlineData("Packing")]
    [InlineData(null)]
    public void Not_cleared_is_blocked_awaiting_qc(string? status)
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: status, allItemsCleared: false);
        Assert.Equal(PackOutcome.Blocked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("AWAITING QC", v.Word);
        Assert.Equal(PackVerdict.ColorAmber, v.Color);
    }

    [Fact]
    public void Cleared_but_To_be_packed_is_blocked()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "To be packed", allItemsCleared: true);
        Assert.Equal(PackOutcome.Blocked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("AWAITING QC", v.Word);
    }

    [Fact]
    public void QcHold_is_blocked_even_if_cleared()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "QC Hold", allItemsCleared: true);
        Assert.Equal(PackOutcome.Blocked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("QC HOLD", v.Word);
        Assert.Equal(PackVerdict.ColorAmber, v.Color);
    }

    [Fact]
    public void Already_shipped_is_grey_noop()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "Shipped", allItemsCleared: true);
        Assert.Equal(PackOutcome.AlreadyShipped, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("ALREADY SHIPPED", v.Word);
        Assert.Equal(PackVerdict.ColorGrey, v.Color);
    }

    [Fact]
    public void Cancelled_takes_precedence()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: true, packingStatus: "QC Passed", allItemsCleared: true);
        Assert.Equal(PackOutcome.Cancelled, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("CANCELLED", v.Word);
        Assert.Equal(PackVerdict.ColorRed, v.Color);
    }

    [Fact]
    public void Not_found_is_red()
    {
        var v = PackVerdict.Evaluate(found: false, cancelled: false, packingStatus: null, allItemsCleared: false);
        Assert.Equal(PackOutcome.NotFound, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("NOT FOUND", v.Word);
        Assert.Equal(PackVerdict.ColorRed, v.Color);
    }

    [Fact]
    public void SaveFailed_is_red_no_write()
    {
        var v = PackVerdict.SaveFailed();
        Assert.Equal(PackOutcome.SaveFailed, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("SAVE FAILED", v.Word);
        Assert.Equal(PackVerdict.ColorRed, v.Color);
    }

    [Theory]
    [InlineData("To be packed")]
    [InlineData(null)]
    public void Awaiting_qc_is_forceable(string? status)
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: status, allItemsCleared: false);
        Assert.True(PackVerdict.IsForceable(v));
    }

    [Fact]
    public void Qc_hold_is_not_forceable()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "QC Hold", allItemsCleared: true);
        Assert.False(PackVerdict.IsForceable(v));
    }

    [Theory]
    [InlineData("Packed")]
    [InlineData("QC Passed")]
    public void Shippable_is_not_forceable(string status)
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: status, allItemsCleared: true);
        Assert.False(PackVerdict.IsForceable(v)); // ships normally, nothing to force
    }

    [Fact]
    public void Cancelled_and_notfound_are_not_forceable()
    {
        Assert.False(PackVerdict.IsForceable(PackVerdict.Evaluate(found: false, cancelled: false, packingStatus: null, allItemsCleared: false)));
        Assert.False(PackVerdict.IsForceable(PackVerdict.Evaluate(found: true, cancelled: true, packingStatus: "Packing", allItemsCleared: false)));
    }
}
