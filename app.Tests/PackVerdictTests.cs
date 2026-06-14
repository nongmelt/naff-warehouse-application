using app.Services;
using Xunit;

namespace app.Tests;

public class PackVerdictTests
{
    [Theory]
    [InlineData("QC Passed")]
    [InlineData("Packing")]
    [InlineData("qc passed")] // case-insensitive
    public void QcCleared_status_seals_as_Packed(string status)
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: status);
        Assert.Equal(PackOutcome.Pack, v.Outcome);
        Assert.True(v.ShouldWrite);
        Assert.Equal("PACKED", v.Word);
        Assert.Equal(PackVerdict.ColorGreen, v.Color);
    }

    [Fact]
    public void Not_found_is_red_NotFound_no_write()
    {
        var v = PackVerdict.Evaluate(found: false, cancelled: false, packingStatus: null);
        Assert.Equal(PackOutcome.NotFound, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("NOT FOUND", v.Word);
        Assert.Equal(PackVerdict.ColorRed, v.Color);
    }

    [Fact]
    public void Cancelled_takes_precedence_over_a_packable_status()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: true, packingStatus: "QC Passed");
        Assert.Equal(PackOutcome.Cancelled, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("CANCELLED", v.Word);
        Assert.Equal(PackVerdict.ColorRed, v.Color);
    }

    [Fact]
    public void To_be_packed_is_amber_NotQcd()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "To be packed");
        Assert.Equal(PackOutcome.Blocked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("NOT QC'D", v.Word);
        Assert.Equal(PackVerdict.ColorAmber, v.Color);
    }

    [Fact]
    public void QcHold_is_amber_QcHold()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "QC Hold");
        Assert.Equal(PackOutcome.Blocked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("QC HOLD", v.Word);
        Assert.Equal(PackVerdict.ColorAmber, v.Color);
    }

    [Fact]
    public void Already_Packed_is_grey_softNoop()
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: "Packed");
        Assert.Equal(PackOutcome.AlreadyPacked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("ALREADY PACKED", v.Word);
        Assert.Equal(PackVerdict.ColorGrey, v.Color);
    }

    [Theory]
    [InlineData("Weird Status", "Weird Status")]
    [InlineData(null, "Unknown status")]
    [InlineData("", "Unknown status")]
    public void Unknown_status_blocks_amber_with_status_in_sub(string? status, string expectedSub)
    {
        var v = PackVerdict.Evaluate(found: true, cancelled: false, packingStatus: status);
        Assert.Equal(PackOutcome.Blocked, v.Outcome);
        Assert.False(v.ShouldWrite);
        Assert.Equal("BLOCKED", v.Word);
        Assert.Equal(expectedSub, v.Sub);
        Assert.Equal(PackVerdict.ColorAmber, v.Color);
    }

    [Fact]
    public void SaveFailed_is_red_no_write()
    {
        var v = PackVerdict.SaveFailed();
        Assert.False(v.ShouldWrite);
        Assert.Equal("SAVE FAILED", v.Word);
        Assert.Equal(PackVerdict.ColorRed, v.Color);
    }
}
