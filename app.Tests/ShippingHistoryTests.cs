using System.Collections.Generic;
using app.Services;
using Xunit;

namespace app.Tests;

public class ShippingHistoryTests
{
    [Theory]
    [InlineData("Standard Delivery - ส่งธรรมดาในประเทศ-SPX Express", "SPX")]
    [InlineData("J&T Express", "J&T")]
    [InlineData("LEX TH - STANDARD", "LEX")]
    [InlineData("Standard Delivery - ส่งธรรมดาในประเทศ-Flash Express", "Flash")]
    [InlineData("Kerry - STANDARD", "Kerry")]
    [InlineData("Standard Delivery Bulky - ส่งสินค้าขนาดใหญ่-DHL Domestic Bulky", "DHL")]
    [InlineData("Instant Delivery - ส่งทันที (แพ็ก 2 ชั่วโมง)", "Instant")]
    public void CarrierToken_extracts_known_carrier_or_first_word(string input, string expected)
        => Assert.Equal(expected, ShippingHistory.CarrierToken(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CarrierToken_blank_is_null(string? input)
        => Assert.Null(ShippingHistory.CarrierToken(input));

    [Theory]
    [InlineData("Shopee", "Shopee")]
    [InlineData("shopee-th", "Shopee")]
    [InlineData("Lazada", "Lazada")]
    [InlineData("Tiktok", "TikTok")]
    [InlineData("Amazon", null)]
    [InlineData(null, null)]
    public void PlatformKey_canonicalises(string? input, string? expected)
        => Assert.Equal(expected, ShippingHistory.PlatformKey(input));

    [Fact]
    public void IsSeal_only_true_for_Pack()
    {
        Assert.True(ShippingHistory.IsSeal(PackOutcome.Pack));
        Assert.False(ShippingHistory.IsSeal(PackOutcome.AlreadyPacked));
        Assert.False(ShippingHistory.IsSeal(PackOutcome.Blocked));
        Assert.False(ShippingHistory.IsSeal(PackOutcome.NotFound));
        Assert.False(ShippingHistory.IsSeal(PackOutcome.Cancelled));
        Assert.False(ShippingHistory.IsSeal(PackOutcome.SaveFailed));
    }

    [Fact]
    public void CarrierToken_pure_thai_with_no_known_carrier_is_null()
    {
        Assert.Null(ShippingHistory.CarrierToken("ส่งธรรมดาในประเทศ"));
    }

    private static ShipScan Scan(int seq, string plat, string ship, PackOutcome o) =>
        new(seq, $"TK{seq}", plat, ship, o);

    [Fact]
    public void PlatformTally_counts_only_sealed_scans()
    {
        var scans = new List<ShipScan>
        {
            Scan(1, "Shopee", "SPX Express", PackOutcome.Pack),
            Scan(2, "Shopee", "SPX Express", PackOutcome.Pack),
            Scan(3, "Shopee", "SPX Express", PackOutcome.AlreadyPacked), // not a seal
            Scan(4, "Lazada", "LEX TH",      PackOutcome.Pack),
            Scan(5, "Tiktok", "J&T Express", PackOutcome.Blocked),       // not a seal
        };
        var (shopee, lazada, tiktok) = ShippingHistory.PlatformTally(scans);
        Assert.Equal(2, shopee);
        Assert.Equal(1, lazada);
        Assert.Equal(0, tiktok);
    }

    [Fact]
    public void CarrierTally_counts_only_sealed_scans_ordered_by_count()
    {
        var scans = new List<ShipScan>
        {
            Scan(1, "Shopee", "SPX Express", PackOutcome.Pack),
            Scan(2, "Shopee", "SPX Express", PackOutcome.Pack),
            Scan(3, "Tiktok", "J&T Express", PackOutcome.Pack),
            Scan(4, "Shopee", "SPX Express", PackOutcome.AlreadyPacked), // excluded
            Scan(5, "Lazada", "",            PackOutcome.Pack),          // no carrier -> excluded
        };
        var tally = ShippingHistory.CarrierTally(scans);
        Assert.Equal(2, tally.Count);
        Assert.Equal(("SPX", 2), tally[0]);
        Assert.Equal(("J&T", 1), tally[1]);
    }

    [Fact]
    public void SealedCount_counts_only_Pack()
    {
        var scans = new List<ShipScan>
        {
            Scan(1, "Shopee", "SPX", PackOutcome.Pack),
            Scan(2, "Shopee", "SPX", PackOutcome.AlreadyPacked),
            Scan(3, "Lazada", "LEX", PackOutcome.Pack),
        };
        Assert.Equal(2, ShippingHistory.SealedCount(scans));
    }
}
