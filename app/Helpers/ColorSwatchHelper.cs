using System.Runtime.Versioning;

namespace app.Helpers;

[SupportedOSPlatform("windows")]
public static class ColorSwatchHelper
{
    private static readonly Dictionary<string, string> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"]     = "#000000", ["white"]      = "#FFFFFF", ["red"]       = "#EF4444",
        ["blue"]      = "#3B82F6", ["navy"]       = "#1E3A5F", ["green"]     = "#22C55E",
        ["grey"]      = "#9CA3AF", ["gray"]       = "#9CA3AF", ["charcoal"]  = "#374151",
        ["brown"]     = "#92400E", ["beige"]      = "#D4C5A9", ["cream"]     = "#FFFDD0",
        ["khaki"]     = "#C3B091", ["olive"]      = "#808000", ["sand"]      = "#C2B280",
        ["pink"]      = "#EC4899", ["purple"]     = "#A855F7", ["orange"]    = "#F97316",
        ["yellow"]    = "#EAB308", ["natural"]    = "#D4A373", ["heather"]   = "#B8A9C9",
        ["camel"]     = "#C19A6B", ["tan"]        = "#D2B48C", ["wine"]      = "#722F37",
        ["burgundy"]  = "#800020", ["coral"]      = "#FF7F50", ["teal"]      = "#14B8A6",
        ["mint"]      = "#98F5E1", ["lavender"]   = "#E9D5FF", ["ivory"]     = "#FFFFF0",
        ["silver"]    = "#C0C0C0", ["gold"]       = "#FFD700", ["rust"]      = "#B7410E",
        ["maroon"]    = "#800000", ["aqua"]       = "#06B6D4", ["indigo"]    = "#4F46E5",
        ["sage"]      = "#9CAF88", ["mustard"]    = "#D1AA00", ["peach"]     = "#FFCBA4",
        // Thai
        ["ดำ"]        = "#000000", ["ขาว"]       = "#FFFFFF", ["แดง"]      = "#EF4444",
        ["น้ำเงิน"]    = "#3B82F6", ["เขียว"]     = "#22C55E", ["เหลือง"]   = "#EAB308",
        ["ชมพู"]      = "#EC4899", ["ม่วง"]      = "#A855F7", ["ส้ม"]       = "#F97316",
        ["น้ำตาล"]    = "#92400E", ["เทา"]       = "#9CA3AF", ["ฟ้า"]       = "#38BDF8",
        ["ครีม"]      = "#FFFDD0", ["กรม"]       = "#1E3A5F", ["เบจ"]       = "#D4C5A9",
        ["ทอง"]       = "#FFD700", ["เงิน"]      = "#C0C0C0", ["กากี"]      = "#C3B091",
        ["มะนาว"]     = "#84CC16", ["พีช"]       = "#FFCBA4", ["แทน"]       = "#D2B48C",
        ["โอลด์โรส"]  = "#C08081", ["เขียวขี้ม้า"] = "#808000", ["บานเย็น"]   = "#FF69B4",
    };

    public static Color? ParseSwatchColor(string? variation)
    {
        if (string.IsNullOrWhiteSpace(variation)) return null;

        var colorPart = variation.Split('/')[0].Trim();

        var words = colorPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (ColorMap.TryGetValue(word, out var hex))
                return Color.FromArgb(hex);
        }

        if (ColorMap.TryGetValue(colorPart, out var fullHex))
            return Color.FromArgb(fullHex);

        return null;
    }
}
