using app.Models;
using System.Runtime.Versioning;

namespace app.Helpers;

[SupportedOSPlatform("windows")]
public static class CategoryBadgeHelper
{
    private static readonly Dictionary<string, (Color Bg, Color Fg)> CategoryColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tees"]        = (Color.FromArgb("#DBEAFE"), Color.FromArgb("#1E40AF")),
        ["Hoodies"]     = (Color.FromArgb("#FEF3C7"), Color.FromArgb("#92400E")),
        ["Pants"]       = (Color.FromArgb("#D1FAE5"), Color.FromArgb("#065F46")),
        ["Bags"]        = (Color.FromArgb("#FFE4E6"), Color.FromArgb("#9F1239")),
        ["Shirts"]      = (Color.FromArgb("#E0E7FF"), Color.FromArgb("#3730A3")),
        ["Accessories"] = (Color.FromArgb("#FEE2E2"), Color.FromArgb("#991B1B")),
        ["Shorts"]      = (Color.FromArgb("#CCFBF1"), Color.FromArgb("#134E4A")),
        ["Jackets"]     = (Color.FromArgb("#F3E8FF"), Color.FromArgb("#6B21A8")),
        ["Dresses"]     = (Color.FromArgb("#FCE7F3"), Color.FromArgb("#9D174D")),
    };

    private static readonly (Color Bg, Color Fg) DefaultColor = (Color.FromArgb("#F3F4F6"), Color.FromArgb("#374151"));

    private static string Abbreviate(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return "???";
        var name = categoryName.Trim().ToUpperInvariant();
        return name.Length <= 3 ? name : name[..3];
    }

    public static void AssignBadges(IEnumerable<ProductItem> items)
    {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var cat = item.CategoryName ?? "Other";
            var abbr = Abbreviate(cat);

            counters.TryGetValue(cat, out var count);
            count++;
            counters[cat] = count;

            item.CategoryBadge = $"{abbr}-{count:D2}";

            if (CategoryColors.TryGetValue(cat, out var colors))
            {
                item.CategoryBadgeBg = colors.Bg;
                item.CategoryBadgeFg = colors.Fg;
            }
            else
            {
                item.CategoryBadgeBg = DefaultColor.Bg;
                item.CategoryBadgeFg = DefaultColor.Fg;
            }
        }
    }
}
