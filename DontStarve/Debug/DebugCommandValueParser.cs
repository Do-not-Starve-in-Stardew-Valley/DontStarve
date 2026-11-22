#nullable enable

using System;
using System.Globalization;

namespace DontStarve.Debug;

internal readonly record struct DebugCommandValueParseResult(
    bool Success,
    bool IsPercentage,
    double Value,
    string Reason
);

internal static class DebugCommandValueParser
{
    internal static DebugCommandValueParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Invalid("debug-value-is-empty");

        var isPercentage = raw.EndsWith("%", StringComparison.Ordinal);
        var numericText = isPercentage ? raw[..^1] : raw;
        if (
            !double.TryParse(
                numericText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            )
            || !double.IsFinite(value)
        )
        {
            return Invalid("debug-value-is-not-finite-number");
        }

        if (value < 0d || (isPercentage && value > 100d))
            return Invalid(isPercentage ? "debug-percentage-is-out-of-range" : "debug-value-is-negative");

        return new DebugCommandValueParseResult(
            true,
            isPercentage,
            value,
            "debug-value-parsed"
        );
    }

    internal static bool TryResolveTarget(
        string? raw,
        double maximum,
        out double target,
        out string reason
    )
    {
        target = 0d;
        if (!double.IsFinite(maximum) || maximum < 0d)
        {
            reason = "debug-maximum-is-invalid";
            return false;
        }

        var parsed = Parse(raw);
        if (!parsed.Success)
        {
            reason = parsed.Reason;
            return false;
        }

        var requested = parsed.IsPercentage
            ? maximum * parsed.Value / 100d
            : parsed.Value;
        target = Math.Clamp(requested, 0d, maximum);
        reason = "debug-value-resolved";
        return true;
    }

    private static DebugCommandValueParseResult Invalid(string reason) =>
        new(false, false, 0d, reason);
}
