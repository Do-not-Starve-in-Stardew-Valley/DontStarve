#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Display.UIElements;

internal enum SanityTooltipRowKind
{
    Hunger,
    Sanity,
    Buff,
}

internal enum SanityTooltipIconKind
{
    HungerIcon,
    SanityBrain,
    VanillaCursor,
}

/// <summary>
/// Controls wording only. The compact vanilla Tooltip keeps its icon-led rows short while
/// other surfaces retain the complete localized Hunger/Sanity descriptions.
/// </summary>
internal enum FoodBuffTooltipTextMode
{
    Descriptive,
    CompactVanillaTooltip,
}

internal readonly record struct SanityTooltipRow(
    SanityTooltipRowKind Kind,
    string Text,
    SanityTooltipIconKind IconKind,
    int VanillaLegacyIndex,
    int VanillaCursorSourceX
);

internal static class SanityTooltipLayoutContract
{
    internal const int CustomRowHeight = 39;
    internal const int CustomSectionPadding = 4;
    // Stardew's vanilla tooltip uses a 10x10 cursor region with a 3x draw scale.
    // TooltipIconPixels is the resulting on-screen target size, not either source texture size.
    internal const int VanillaTooltipIconSourcePixels = 10;
    internal const int VanillaTooltipIconScale = 3;
    internal const int TooltipIconPixels = VanillaTooltipIconSourcePixels * VanillaTooltipIconScale;
    internal const int VanillaBuffDurationLegacyIndex = 12;

    internal const int CombatLegacyIndex = 3;
    internal const int DefenseLegacyIndex = 10;
    internal const int AttackLegacyIndex = 11;

    internal static readonly IReadOnlyList<string> SupportedItemCategories =
        Array.AsReadOnly(
            new[]
            {
                "Food",
                "Hat",
                "Shirt",
                "Pants",
                "Boots",
                "Ring",
                "Trinket",
                "MeleeWeapon",
                "Tool",
                "Other",
            }
        );

    internal static readonly IReadOnlyList<string> SupportedBuffAttributes =
        Array.AsReadOnly(
            new[]
            {
                "CombatLevel",
                "AttackMultiplier",
                "Immunity",
                "KnockbackMultiplier",
                "WeaponSpeedMultiplier",
                "CriticalChanceMultiplier",
                "CriticalPowerMultiplier",
                "WeaponPrecisionMultiplier",
            }
        );

    internal static int GetAdditionalHeight(int rowCount)
    {
        return GetAdditionalHeight(rowCount, 0);
    }

    internal static int GetAdditionalHeight(int survivalRowCount, int extraBuffRowCount)
    {
        // The vanilla Buff section already owns its four-pixel section padding when present.
        return GetAdditionalHeight(survivalRowCount, extraBuffRowCount, vanillaBuffSectionPresent: true);
    }

    internal static int GetAdditionalHeight(
        int survivalRowCount,
        int extraBuffRowCount,
        bool vanillaBuffSectionPresent
    )
    {
        var survivalHeight = survivalRowCount <= 0
            ? 0
            : checked(CustomSectionPadding + survivalRowCount * CustomRowHeight);
        var extraBuffHeight = extraBuffRowCount <= 0
            ? 0
            : checked(
                extraBuffRowCount * CustomRowHeight
                + (vanillaBuffSectionPresent ? 0 : CustomSectionPadding)
            );
        return checked(survivalHeight + extraBuffHeight);
    }

    internal static bool NeedsExtraBuffFallback(string[]? vanillaBuffIcons)
    {
        if (vanillaBuffIcons is null)
            return false;

        if (vanillaBuffIcons.Length <= VanillaBuffDurationLegacyIndex)
            return true;

        var durationValue = vanillaBuffIcons[VanillaBuffDurationLegacyIndex];
        return string.IsNullOrEmpty(durationValue) || string.Equals(durationValue, "0", StringComparison.Ordinal);
    }
}

/// <summary>
/// The final row sequence used by tooltip measurement and drawing. Keeping the category counts
/// beside the sequence prevents the survival-section separator from being counted for Buff rows.
/// </summary>
internal sealed class SanityTooltipRowLayout
{
    internal static readonly SanityTooltipRowLayout Empty = new(
        Array.Empty<SanityTooltipRow>()
    );

    internal SanityTooltipRowLayout(IReadOnlyList<SanityTooltipRow> rows)
    {
        this.Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        var survivalRowCount = 0;
        var extraBuffRowCount = 0;
        foreach (var row in rows)
        {
            if (row.Kind == SanityTooltipRowKind.Buff)
                extraBuffRowCount++;
            else
                survivalRowCount++;
        }

        this.SurvivalRowCount = survivalRowCount;
        this.ExtraBuffRowCount = extraBuffRowCount;
    }

    internal IReadOnlyList<SanityTooltipRow> Rows { get; }

    internal int SurvivalRowCount { get; }

    internal int ExtraBuffRowCount { get; }

    internal int GetAdditionalHeight(bool vanillaBuffSectionPresent)
    {
        return SanityTooltipLayoutContract.GetAdditionalHeight(
            this.SurvivalRowCount,
            this.ExtraBuffRowCount,
            vanillaBuffSectionPresent
        );
    }
}

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

internal static class SanityTooltipRowFilter
{
    internal static IReadOnlyList<SanityTooltipRow> RemoveVanillaDuplicates(
        IReadOnlyList<SanityTooltipRow> rows,
        string[]? vanillaBuffIcons
    )
    {
        if (rows.Count == 0 || vanillaBuffIcons is null || vanillaBuffIcons.Length == 0)
            return rows;

        List<SanityTooltipRow>? filtered = null;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var isDuplicate =
                row.VanillaLegacyIndex >= 0
                && row.VanillaLegacyIndex < vanillaBuffIcons.Length
                && IsVanillaBuffDisplayed(vanillaBuffIcons[row.VanillaLegacyIndex]);
            if (isDuplicate)
            {
                filtered ??= CopyRowsBefore(rows, index);
                continue;
            }

            filtered?.Add(row);
        }

        return filtered ?? rows;
    }

    private static List<SanityTooltipRow> CopyRowsBefore(
        IReadOnlyList<SanityTooltipRow> rows,
        int count
    )
    {
        var copied = new List<SanityTooltipRow>(rows.Count);
        for (var index = 0; index < count; index++)
            copied.Add(rows[index]);
        return copied;
    }

    private static bool IsVanillaBuffDisplayed(string? value)
    {
        return !string.IsNullOrEmpty(value) && !string.Equals(value, "0", StringComparison.Ordinal);
    }
}
