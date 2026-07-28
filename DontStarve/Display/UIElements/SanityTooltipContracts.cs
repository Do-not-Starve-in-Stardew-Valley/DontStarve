#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DontStarve.Display.UIElements;

internal enum SanityTooltipEffectKind
{
    FoodOnce,
    EquipmentPerMinute,
}

internal readonly record struct SanityTooltipEffect(
    SanityTooltipEffectKind Kind,
    double Value
);

internal static class SanityTooltipCoverageContract
{
    internal static readonly IReadOnlyList<string> StandardVanillaMenus =
        Array.AsReadOnly(
            new[]
            {
                "InventoryPage",
                "ItemGrabMenu",
                "ShopMenu",
                "CollectionsPage",
                "CraftingPage",
            }
        );

    internal const bool ThirdPartyFullyCustomDrawingCovered = false;
}

/// <summary>
/// Pure per-frame/reentrant guard for the final vanilla drawHoverText overload. Builder identity
/// is included so two distinct tooltips for the same item in one frame both remain complete.
/// </summary>
internal sealed class SanityTooltipDedupeGate
{
    private readonly record struct SeenEntry(object Item, object Builder);

    internal const int MaximumEntriesPerFrame = 32;

    private readonly List<SeenEntry> seen = new(MaximumEntriesPerFrame);
    private long frame = long.MinValue;

    internal bool TryEnter(long currentFrame, object itemReference, object builderReference)
    {
        ArgumentNullException.ThrowIfNull(itemReference);
        ArgumentNullException.ThrowIfNull(builderReference);
        if (frame != currentFrame)
        {
            frame = currentFrame;
            seen.Clear();
        }

        foreach (var entry in seen)
        {
            if (
                ReferenceEquals(entry.Item, itemReference)
                && ReferenceEquals(entry.Builder, builderReference)
            )
            {
                return false;
            }
        }
        if (seen.Count >= MaximumEntriesPerFrame)
            return false;

        seen.Add(new SeenEntry(itemReference, builderReference));
        return true;
    }

    internal void Clear()
    {
        frame = long.MinValue;
        seen.Clear();
    }
}

internal static class SanityTooltipTextAppender
{
    internal static bool TryAppendLine(StringBuilder text, string line)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(line) || ContainsExactLine(text, line))
            return false;

        if (text.Length > 0 && text[text.Length - 1] != '\n')
            text.Append('\n');
        text.Append(line);
        return true;
    }

    internal static bool TryFormatValue(
        double value,
        CultureInfo culture,
        out string formatted
    )
    {
        return LocalizedValueFormatter.TryFormatSigned(value, culture, out formatted);
    }

    private static bool ContainsExactLine(StringBuilder text, string line)
    {
        var lineStart = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] != '\n')
                continue;
            if (index - lineStart == line.Length)
            {
                var matches = true;
                for (var offset = 0; offset < line.Length; offset++)
                {
                    if (text[lineStart + offset] == line[offset])
                        continue;
                    matches = false;
                    break;
                }
                if (matches)
                    return true;
            }
            lineStart = index + 1;
        }
        return false;
    }
}
