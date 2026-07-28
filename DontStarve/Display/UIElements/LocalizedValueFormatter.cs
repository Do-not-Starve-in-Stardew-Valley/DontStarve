#nullable enable

using System;
using System.Globalization;

namespace DontStarve.Display.UIElements;

/// <summary>
/// 为 HUD/held-item 提示生成本地化数值文本；单位和句式仍由 i18n 模板负责。
/// </summary>
internal static class LocalizedValueFormatter
{
    internal static CultureInfo ResolveCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return CultureInfo.InvariantCulture;

        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    internal static bool TryFormatSigned(
        double value,
        CultureInfo? culture,
        out string formatted
    )
    {
        if (!double.IsFinite(value))
        {
            formatted = string.Empty;
            return false;
        }

        var normalized = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        formatted = normalized.ToString(
            "+0.##;-0.##;0",
            culture ?? CultureInfo.InvariantCulture
        );
        return true;
    }

    internal static string FormatWhole(double value, CultureInfo? culture)
    {
        return Math.Round(value).ToString(
            "0",
            culture ?? CultureInfo.InvariantCulture
        );
    }
}
