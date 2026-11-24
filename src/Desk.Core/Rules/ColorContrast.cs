using System.Globalization;
using System.Text.RegularExpressions;

namespace Desk.Core.Rules;

/// <summary>WCAG 2.x contrast computation on #RRGGBB colors (relative luminance, ratio from 1:1 to 21:1).</summary>
public static partial class ColorContrast
{
    /// <summary>Minimum ratio for non-text elements (buttons, borders, icons): WCAG 1.4.11.</summary>
    public const double MinimumAccentRatio = 3.0;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    public static bool IsHex(string? value) => value is not null && HexPattern().IsMatch(value);

    /// <summary>Relative luminance of a #RRGGBB color (0 = black, 1 = white).</summary>
    public static double RelativeLuminance(string hex)
    {
        if (!IsHex(hex))
        {
            throw new ArgumentException("Expected a #RRGGBB color.", nameof(hex));
        }

        var r = Channel(hex, 1);
        var g = Channel(hex, 3);
        var b = Channel(hex, 5);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>Contrast ratio between two colors, symmetric, between 1 and 21.</summary>
    public static double Ratio(string first, string second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var (light, dark) = a >= b ? (a, b) : (b, a);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Channel(string hex, int start)
    {
        var value = int.Parse(hex.AsSpan(start, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
