using System;
using System.Collections.Generic;
using System.Linq;

namespace app.Services;

/// <summary>
/// One belt entry per parcel scan this session, plus pure tallies/parsers for the
/// Shipping page history belt. Zero MAUI dependencies so it can be unit-tested
/// (mirrors PackVerdict).
/// </summary>
public readonly record struct ShipScan(
    int Seq,
    string Tracking,
    string? Platform,
    string? Shipping,
    PackOutcome Outcome);

public static class ShippingHistory
{
    /// <summary>True only when a scan actually transitioned the parcel to Packed (a real seal).</summary>
    public static bool IsSeal(PackOutcome outcome) => outcome == PackOutcome.Pack;

    /// <summary>Canonical platform name, or null if unrecognised.</summary>
    public static string? PlatformKey(string? platform)
    {
        var p = (platform ?? "").ToLowerInvariant();
        if (p.Contains("shopee")) return "Shopee";
        if (p.Contains("lazada")) return "Lazada";
        if (p.Contains("tiktok")) return "TikTok";
        return null;
    }

    /// <summary>
    /// Short carrier token pulled out of the (long, often Thai) shipping_options string.
    /// Returns null when the input is blank.
    /// </summary>
    public static string? CarrierToken(string? shipping)
    {
        if (string.IsNullOrWhiteSpace(shipping)) return null;
        var s = shipping.ToUpperInvariant();
        if (s.Contains("J&T"))   return "J&T";
        if (s.Contains("SPX"))   return "SPX";
        if (s.Contains("FLASH")) return "Flash";
        if (s.Contains("LEX"))   return "LEX";
        if (s.Contains("KERRY")) return "Kerry";
        if (s.Contains("DHL"))   return "DHL";
        if (s.Contains("NINJA")) return "Ninja";
        // No known carrier keyword — fall back to the first ASCII word ("Instant", "Express", …).
        var w = shipping.Trim().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return w.Length > 0 ? w[0] : null;
    }

    /// <summary>Per-platform packed counts — only sealed scans count.</summary>
    public static (int Shopee, int Lazada, int TikTok) PlatformTally(IEnumerable<ShipScan> scans)
    {
        int shopee = 0, lazada = 0, tiktok = 0;
        foreach (var s in scans)
        {
            if (!IsSeal(s.Outcome)) continue;
            switch (PlatformKey(s.Platform))
            {
                case "Shopee": shopee++; break;
                case "Lazada": lazada++; break;
                case "TikTok": tiktok++; break;
            }
        }
        return (shopee, lazada, tiktok);
    }

    /// <summary>
    /// Per-carrier packed counts — only sealed scans with a carrier token, ordered by
    /// count desc then name. This is the persisted carrier breakdown on the belt.
    /// </summary>
    public static IReadOnlyList<(string Carrier, int Count)> CarrierTally(IEnumerable<ShipScan> scans)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scans)
        {
            if (!IsSeal(s.Outcome)) continue;
            var token = CarrierToken(s.Shipping);
            if (token is null) continue;
            counts[token] = counts.TryGetValue(token, out var c) ? c + 1 : 1;
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Total parcels sealed this session.</summary>
    public static int SealedCount(IEnumerable<ShipScan> scans) => scans.Count(s => IsSeal(s.Outcome));
}
