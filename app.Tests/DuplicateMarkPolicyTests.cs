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
