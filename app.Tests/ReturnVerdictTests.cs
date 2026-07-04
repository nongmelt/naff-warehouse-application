using app.Services;
using Xunit;

namespace app.Tests;

public class ReturnVerdictTests
{
    [Fact]
    public void Shipped_parcel_is_returnable()
    {
        var v = ReturnVerdict.Evaluate(found: true, packingStatus: "Shipped");
        Assert.Equal(ReturnOutcome.Return, v.Outcome);
        Assert.True(v.ShouldWrite);
    }

    [Fact]
    public void Unknown_parcel_not_found()
    {
        var v = ReturnVerdict.Evaluate(found: false, packingStatus: null);
        Assert.Equal(ReturnOutcome.NotFound, v.Outcome);
        Assert.False(v.ShouldWrite);
    }

    [Fact]
    public void Already_returned_is_no_op()
    {
        var v = ReturnVerdict.Evaluate(found: true, packingStatus: "Returned");
        Assert.Equal(ReturnOutcome.AlreadyReturned, v.Outcome);
        Assert.False(v.ShouldWrite);
    }

    [Theory]
    [InlineData("To be packed")]
    [InlineData("Packing")]
    [InlineData("Packed")]
    [InlineData("QC Passed")]
    [InlineData("QC Hold")]
    [InlineData(null)]
    public void Not_shipped_statuses_blocked(string? status)
    {
        var v = ReturnVerdict.Evaluate(found: true, packingStatus: status);
        Assert.Equal(ReturnOutcome.NotShipped, v.Outcome);
        Assert.False(v.ShouldWrite);
    }

    [Fact]
    public void Status_compare_is_case_insensitive_and_trimmed()
    {
        var v = ReturnVerdict.Evaluate(found: true, packingStatus: " shipped ");
        Assert.Equal(ReturnOutcome.Return, v.Outcome);
    }
}
