#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using HungerEatFood = DontStarve.Player.Stats.Hunger.HungerBehaviors.EatFood;
using SanityEatFood = DontStarve.Player.Stats.Sanity.SanityBehaviors.EatFood;
using Wearing = DontStarve.Player.Stats.Sanity.SanityBehaviors.Wearing;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buffs;
using SObject = StardewValley.Object;

namespace DontStarve.Display.UIElements;

/// <summary>
/// Generates Hunger/Sanity rows and the extra item-Buff rows which Stardew's legacy tooltip
/// cannot render itself. All Buff values come from the same runtime aggregation as the vanilla
/// tooltip so item-specific ModifyItemBuffs adjustments are preserved.
/// </summary>
internal sealed class FoodBuffTooltipFormatter : IDisposable
{
    internal const string ExtraMachineConfigUniqueId = "selph.ExtraMachineConfig";

    private const int AttackMultiplierIconSourceX = 120;
    private const int ImmunityIconSourceX = 150;
    private const int KnockbackMultiplierIconSourceX = 70;
    private const int WeaponSpeedMultiplierIconSourceX = 130;
    private const int CriticalMultiplierIconSourceX = 160;
    private const int WeaponPrecisionMultiplierIconSourceX = 40;

    private readonly IModHelper helper;
    private readonly bool externalExtraMachineConfigLoaded;
    private CultureInfo culture;
    private bool disposed;

    internal FoodBuffTooltipFormatter(IModHelper helper)
        : this(
            helper,
            helper.ModRegistry.IsLoaded(ExtraMachineConfigUniqueId)
        ) { }

    internal FoodBuffTooltipFormatter(
        IModHelper helper,
        bool externalExtraMachineConfigLoaded
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.externalExtraMachineConfigLoaded = externalExtraMachineConfigLoaded;
        this.culture = LocalizedValueFormatter.ResolveCulture(helper.Translation.Locale);
        helper.Events.Content.LocaleChanged += this.OnLocaleChanged;
    }

    internal IReadOnlyList<SanityTooltipRow> GetRows(
        Item? item,
        bool showSanity,
        string[]? vanillaBuffIcons = null,
        FoodBuffTooltipTextMode textMode = FoodBuffTooltipTextMode.Descriptive
    )
    {
        if (item == null || string.IsNullOrWhiteSpace(item.QualifiedItemId))
            return Array.Empty<SanityTooltipRow>();

        var rows = new List<SanityTooltipRow>();
        AddSurvivalRows(item, showSanity, textMode, rows);
        AddRows(rows, this.GetExtraMachineConfigRows(item));
        return SanityTooltipRowFilter.RemoveVanillaDuplicates(rows, vanillaBuffIcons);
    }

    internal IReadOnlyList<SanityTooltipRow> GetSurvivalRows(Item? item, bool showSanity)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.QualifiedItemId))
            return Array.Empty<SanityTooltipRow>();

        var rows = new List<SanityTooltipRow>();
        AddSurvivalRows(item, showSanity, FoodBuffTooltipTextMode.Descriptive, rows);
        return rows.Count == 0 ? Array.Empty<SanityTooltipRow>() : rows;
    }

    internal IReadOnlyList<SanityTooltipRow> GetExtraMachineConfigRows(Item? item)
    {
        if (
            this.externalExtraMachineConfigLoaded
            || item == null
            || !Game1.objectData.TryGetValue(item.ItemId, out var objectData)
        )
        {
            return Array.Empty<SanityTooltipRow>();
        }

        // This deliberately mirrors ExtraMachineConfig and the vanilla tooltip path. Reading
        // ObjectData.CustomAttributes directly misses BuffId resolution and ModifyItemBuffs.
        var effects = new BuffEffects();
        foreach (
            var buff in SObject.TryCreateBuffsFromData(
                objectData,
                item.Name,
                item.DisplayName,
                1f,
                item.ModifyItemBuffs
            )
        )
        {
            effects.Add(buff.effects);
        }

        var rows = new List<SanityTooltipRow>(7);
        this.AddExternalMultiplierRow(
            rows,
            "food-buff-tooltip.attack-multiplier",
            effects.AttackMultiplier.Value,
            AttackMultiplierIconSourceX
        );
        this.AddExternalImmunityRow(rows, effects.Immunity.Value);
        this.AddExternalMultiplierRow(
            rows,
            "food-buff-tooltip.knockback-multiplier",
            effects.KnockbackMultiplier.Value,
            KnockbackMultiplierIconSourceX
        );
        this.AddExternalMultiplierRow(
            rows,
            "food-buff-tooltip.weapon-speed-multiplier",
            effects.WeaponSpeedMultiplier.Value,
            WeaponSpeedMultiplierIconSourceX
        );
        this.AddExternalMultiplierRow(
            rows,
            "food-buff-tooltip.critical-chance-multiplier",
            effects.CriticalChanceMultiplier.Value,
            CriticalMultiplierIconSourceX
        );
        this.AddExternalMultiplierRow(
            rows,
            "food-buff-tooltip.critical-power-multiplier",
            effects.CriticalPowerMultiplier.Value,
            CriticalMultiplierIconSourceX
        );
        this.AddExternalMultiplierRow(
            rows,
            "food-buff-tooltip.weapon-precision-multiplier",
            effects.WeaponPrecisionMultiplier.Value,
            WeaponPrecisionMultiplierIconSourceX
        );

        return rows.Count == 0 ? Array.Empty<SanityTooltipRow>() : rows;
    }

    internal IReadOnlyList<string> GetLines(Item? item, bool showSanity)
    {
        var rows = this.GetRows(item, showSanity);
        if (rows.Count == 0)
            return Array.Empty<string>();

        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
            lines.Add(row.Text);
        return lines;
    }

    internal void ClearCache()
    {
        // Buffs intentionally have no presentation cache: ModifyItemBuffs can vary by item instance.
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        this.helper.Events.Content.LocaleChanged -= this.OnLocaleChanged;
    }

    private static void AddRows(
        List<SanityTooltipRow> destination,
        IReadOnlyList<SanityTooltipRow> source
    )
    {
        foreach (var row in source)
            destination.Add(row);
    }

    private void AddSurvivalRows(
        Item item,
        bool showSanity,
        FoodBuffTooltipTextMode textMode,
        List<SanityTooltipRow> rows
    )
    {
        if (
            HungerEatFood.FoodHunger is not null
            && HungerEatFood.FoodHunger.TryGetValue(item.ItemId, out var hungerValue)
            && this.TryFormatSigned(hungerValue, out var formattedHunger)
        )
        {
            rows.Add(
                new SanityTooltipRow(
                    SanityTooltipRowKind.Hunger,
                    this.FormatSurvivalText(
                        textMode,
                        "hunger-tooltip",
                        formattedHunger
                    ),
                    SanityTooltipIconKind.HungerIcon,
                    -1,
                    -1
                )
            );
        }

        if (!showSanity)
            return;

        if (
            SanityEatFood.FoodSanity is not null
            && SanityEatFood.FoodSanity.TryGetValue(item.ItemId, out var foodSanity)
            && this.TryFormatSigned(foodSanity, out var formattedFoodSanity)
        )
        {
            rows.Add(
                new SanityTooltipRow(
                    SanityTooltipRowKind.Sanity,
                    this.FormatSurvivalText(
                        textMode,
                        "sanity-tooltip.food-once",
                        formattedFoodSanity,
                        compactTranslationKey: "sanity-tooltip"
                    ),
                    SanityTooltipIconKind.SanityBrain,
                    -1,
                    -1
                )
            );
        }

        if (
            Wearing.TryGetPerMinuteSanity(item, out var equipmentSanity)
            && this.TryFormatSigned(equipmentSanity, out var formattedEquipmentSanity)
        )
        {
            rows.Add(
                new SanityTooltipRow(
                    SanityTooltipRowKind.Sanity,
                    this.FormatSurvivalText(
                        textMode,
                        "sanity-tooltip.equipment-per-minute",
                        formattedEquipmentSanity
                    ),
                    SanityTooltipIconKind.SanityBrain,
                    -1,
                    -1
                )
            );
        }
    }

    private string FormatSurvivalText(
        FoodBuffTooltipTextMode textMode,
        string descriptiveTranslationKey,
        string formattedValue,
        string? compactTranslationKey = null
    )
    {
        var translationKey = textMode == FoodBuffTooltipTextMode.CompactVanillaTooltip
            ? compactTranslationKey ?? descriptiveTranslationKey
            : descriptiveTranslationKey;

        return this.helper.Translation.Get(translationKey, new { value = formattedValue }).ToString();
    }

    private void AddExternalImmunityRow(List<SanityTooltipRow> rows, float value)
    {
        if (value == 0f)
            return;

        rows.Add(
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                this.helper.Translation
                    .Get(
                        "food-buff-tooltip.immunity",
                        new { value = FormatExternalImmunityValue(value) }
                    )
                    .ToString(),
                SanityTooltipIconKind.VanillaCursor,
                -1,
                ImmunityIconSourceX
            )
        );
    }

    private void AddExternalMultiplierRow(
        List<SanityTooltipRow> rows,
        string translationKey,
        float value,
        int iconSourceX
    )
    {
        if (value == 0f)
            return;

        rows.Add(
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                this.helper.Translation
                    .Get(translationKey, new { value = FormatExternalMultiplierValue(value) })
                    .ToString(),
                SanityTooltipIconKind.VanillaCursor,
                -1,
                iconSourceX
            )
        );
    }

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
    {
        this.culture = LocalizedValueFormatter.ResolveCulture(e.NewLocale);
    }

    private bool TryFormatSigned(double value, out string formatted)
    {
        return LocalizedValueFormatter.TryFormatSigned(value, this.culture, out formatted);
    }

    private static string FormatExternalMultiplierValue(float value)
    {
        return string.Concat(value > 0f ? "+" : string.Empty, Math.Round(value * 100f), "%");
    }

    private static string FormatExternalImmunityValue(float value)
    {
        return string.Concat(value > 0f ? "+" : string.Empty, Math.Round(value, 2));
    }
}
