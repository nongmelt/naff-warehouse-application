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
    /// <summary>True only when a scan actually transitioned the parcel to Shipped (a real seal).</summary>
    public static bool IsSeal(PackOutcome outcome) => outcome == PackOutcome.Ship;

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
        // No known carrier keyword — fall back to the first word, but only if it is ASCII
        // (e.g. "Instant"); a pure-Thai segment is not a usable carrier label.
        var w = shipping.Trim().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        var first = w.Length > 0 ? w[0] : null;
        return first != null && first.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            ? first
            : null;
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

    /// <summary>
    /// Build seal scans from a list of platform strings — today's already-shipped parcels
    /// at a station, fetched from the backend. Each becomes a <see cref="PackOutcome.Ship"/>
    /// with no tracking, no sequence and no shipping_options: enough to seed the shipped
    /// total and the platform breakdown, but not the carrier breakdown (the source list
    /// endpoint does not expose shipping_options).
    /// </summary>
    public static IReadOnlyList<ShipScan> SeedScans(IEnumerable<string?> platforms)
    {
        var list = new List<ShipScan>();
        foreach (var platform in platforms)
            list.Add(new ShipScan(0, "", platform, null, PackOutcome.Ship));
        return list;
    }
}
