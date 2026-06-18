using System;
using System.Collections.Generic;
using System.Globalization;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Buffs;

namespace DontStarve.Display.UIElements;

/// <summary>
/// 从 Stardew Data/Objects 的 Buff CustomAttributes 生成食物提示行，并按 ItemId 缓存结果。
/// </summary>
internal sealed class FoodBuffTooltipFormatter
{
    private const double Epsilon = 0.0001;

    private readonly IModHelper helper;
    private readonly Dictionary<string, IReadOnlyList<string>> cache = new();

    public FoodBuffTooltipFormatter(IModHelper helper)
    {
        this.helper = helper;
        // Content Patcher 或数据重载可能改 Data/Objects，缓存必须跟着失效。
        helper.Events.Content.AssetsInvalidated += this.OnAssetsInvalidated;
    }

    public IReadOnlyList<string> GetLines(StardewValley.Object item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ItemId))
            return Array.Empty<string>();

        if (this.cache.TryGetValue(item.ItemId, out var cached))
            return cached;

        var lines = this.BuildLines(item.ItemId);
        this.cache[item.ItemId] = lines;
        return lines;
    }

    private IReadOnlyList<string> BuildLines(string itemId)
    {
        if (
            !Game1.objectData.TryGetValue(itemId, out var objectData)
            || objectData.Buffs == null
            || objectData.Buffs.Count == 0
        )
        {
            return Array.Empty<string>();
        }

        var values = new FoodBuffAttributeValues();
        foreach (var buff in objectData.Buffs)
        {
            // 同一食物可以有多个 Buff 条目，显示时把数值字段加总成一组提示。
            values.Add(buff?.CustomAttributes);
        }

        if (!values.HasAnyValue)
            return Array.Empty<string>();

        var lines = new List<string>();
        this.AddNumberLine(lines, "combat-level", values.CombatLevel);
        this.AddNumberLine(lines, "attack", values.Attack);
        this.AddNumberLine(lines, "defense", values.Defense);
        this.AddNumberLine(lines, "immunity", values.Immunity);
        this.AddPercentLine(lines, "attack-multiplier", values.AttackMultiplier);
        this.AddPercentLine(lines, "knockback-multiplier", values.KnockbackMultiplier);
        this.AddPercentLine(lines, "weapon-speed-multiplier", values.WeaponSpeedMultiplier);
        this.AddPercentLine(lines, "critical-chance-multiplier", values.CriticalChanceMultiplier);
        this.AddPercentLine(lines, "critical-power-multiplier", values.CriticalPowerMultiplier);
        this.AddPercentLine(lines, "weapon-precision-multiplier", values.WeaponPrecisionMultiplier);

        return lines;
    }

    private void AddNumberLine(List<string> lines, string labelKey, double value)
    {
        if (!IsMeaningful(value))
            return;

        lines.Add(
            this.helper.Translation
                .Get(
                    "food-buff-tooltip.line",
                    new
                    {
                        label = this.GetLabel(labelKey),
                        value = FormatSigned(value)
                    }
                )
                .ToString()
        );
    }

    private void AddPercentLine(List<string> lines, string labelKey, double value)
    {
        if (!IsMeaningful(value))
            return;

        lines.Add(
            this.helper.Translation
                .Get(
                    "food-buff-tooltip.percent-line",
                    new
                    {
                        label = this.GetLabel(labelKey),
                        value = FormatSigned(value * 100)
                    }
                )
                .ToString()
        );
    }

    private string GetLabel(string key)
    {
        return this.helper.Translation.Get($"food-buff-tooltip.{key}").ToString();
    }

    private void OnAssetsInvalidated(object sender, AssetsInvalidatedEventArgs e)
    {
        foreach (var name in e.NamesWithoutLocale)
        {
            if (name.IsEquivalentTo("Data/Objects", true))
            {
                // 只在对象数据变化时清缓存，避免每帧重新解析 Data/Objects。
                this.cache.Clear();
                return;
            }
        }
    }

    private static bool IsMeaningful(double value)
    {
        return Math.Abs(value) >= Epsilon;
    }

    private static string FormatSigned(double value)
    {
        var normalized = Math.Round(value, 2);
        var text = normalized.ToString("0.##", CultureInfo.InvariantCulture);
        return normalized > 0 ? $"+{text}" : text;
    }

    private sealed class FoodBuffAttributeValues
    {
        public double CombatLevel { get; private set; }
        public double Attack { get; private set; }
        public double Defense { get; private set; }
        public double Immunity { get; private set; }
        public double AttackMultiplier { get; private set; }
        public double KnockbackMultiplier { get; private set; }
        public double WeaponSpeedMultiplier { get; private set; }
        public double CriticalChanceMultiplier { get; private set; }
        public double CriticalPowerMultiplier { get; private set; }
        public double WeaponPrecisionMultiplier { get; private set; }

        public bool HasAnyValue =>
            IsMeaningful(this.CombatLevel)
            || IsMeaningful(this.Attack)
            || IsMeaningful(this.Defense)
            || IsMeaningful(this.Immunity)
            || IsMeaningful(this.AttackMultiplier)
            || IsMeaningful(this.KnockbackMultiplier)
            || IsMeaningful(this.WeaponSpeedMultiplier)
            || IsMeaningful(this.CriticalChanceMultiplier)
            || IsMeaningful(this.CriticalPowerMultiplier)
            || IsMeaningful(this.WeaponPrecisionMultiplier);

        public void Add(BuffAttributesData attributes)
        {
            if (attributes == null)
                return;

            this.CombatLevel += attributes.CombatLevel;
            this.Attack += attributes.Attack;
            this.Defense += attributes.Defense;
            this.Immunity += attributes.Immunity;
            this.AttackMultiplier += attributes.AttackMultiplier;
            this.KnockbackMultiplier += attributes.KnockbackMultiplier;
            this.WeaponSpeedMultiplier += attributes.WeaponSpeedMultiplier;
            this.CriticalChanceMultiplier += attributes.CriticalChanceMultiplier;
            this.CriticalPowerMultiplier += attributes.CriticalPowerMultiplier;
            this.WeaponPrecisionMultiplier += attributes.WeaponPrecisionMultiplier;
        }
    }
}
