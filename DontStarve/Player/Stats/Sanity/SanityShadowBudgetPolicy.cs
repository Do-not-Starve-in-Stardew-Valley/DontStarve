#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Config;

namespace DontStarve.Player.Stats.Sanity;

internal static class SanityMonsterIntensityIds
{
    internal const string None = "None";
    internal const string Less = "Less";
    internal const string Default = "Default";
    internal const string More = "More";
    internal const string Many = "Many";
    internal const string Insane = "Insane";
}

internal enum SanityShadowSpecies
{
    CreeperFear,
    Terrorbeak,
}

[Flags]
internal enum SanityShadowEligibleSpecies
{
    None = 0,
    CreeperFear = 1 << 0,
    Terrorbeak = 1 << 1,
}

/// <summary>
/// The budget owns tier eligibility so an ineligible species cannot advance the shared owner
/// clock. Runtime bindings are mapped to these two stable species by the hostile authority.
/// </summary>
internal static class SanityShadowPoolEligibilityPolicy
{
    internal static SanityShadowEligibleSpecies GetEligibleSpecies(
        SanityShadowPoolTier tier
    )
    {
        return tier switch
        {
            SanityShadowPoolTier.Harmless50 =>
                SanityShadowEligibleSpecies.CreeperFear
                | SanityShadowEligibleSpecies.Terrorbeak,
            SanityShadowPoolTier.Hostile15 =>
                SanityShadowEligibleSpecies.CreeperFear,
            SanityShadowPoolTier.Hostile10 =>
                SanityShadowEligibleSpecies.CreeperFear
                | SanityShadowEligibleSpecies.Terrorbeak,
            _ => SanityShadowEligibleSpecies.None,
        };
    }

    internal static bool TryAuthorize(
        SanityShadowPoolTier tier,
        SanityShadowSpecies species,
        out string reason
    )
    {
        var requested = species switch
        {
            SanityShadowSpecies.CreeperFear =>
                SanityShadowEligibleSpecies.CreeperFear,
            SanityShadowSpecies.Terrorbeak =>
                SanityShadowEligibleSpecies.Terrorbeak,
            _ => SanityShadowEligibleSpecies.None,
        };
        if (
            requested != SanityShadowEligibleSpecies.None
            && (GetEligibleSpecies(tier) & requested) == requested
        )
        {
            reason = "budget.species-eligible";
            return true;
        }

        reason = tier == SanityShadowPoolTier.Hostile15
            && species == SanityShadowSpecies.Terrorbeak
                ? "budget.terrorbeak-requires-hostile10"
                : "budget.species-ineligible-for-pool-tier";
        return false;
    }
}

internal sealed class SanityShadowBudgetPolicy
{
    internal SanityShadowBudgetPolicy(
        string intensityId,
        long intervalMinutes,
        int baseCap,
        int terrorbeakCap
    )
    {
        if (string.IsNullOrWhiteSpace(intensityId))
            throw new ArgumentException("Intensity IDs must be non-empty.", nameof(intensityId));
        if (intervalMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        if (baseCap < 0 || terrorbeakCap < 0 || terrorbeakCap < baseCap)
            throw new ArgumentOutOfRangeException(nameof(baseCap));

        IntensityId = intensityId;
        IntervalMinutes = intervalMinutes;
        BaseCap = baseCap;
        TerrorbeakCap = terrorbeakCap;
    }

    internal string IntensityId { get; }

    internal long IntervalMinutes { get; }

    /// <summary>
    /// Shadow refreshes use wall-clock milliseconds. IntervalMinutes remains available for the
    /// legacy budget contract, diagnostics, and multiplayer protocol timestamps.
    /// </summary>
    internal long RealIntervalMilliseconds => IntensityId switch
    {
        SanityMonsterIntensityIds.None => 0L,
        SanityMonsterIntensityIds.Less => 84_000L,
        SanityMonsterIntensityIds.Many => 21_000L,
        SanityMonsterIntensityIds.Insane => 21_000L,
        _ => 42_000L,
    };

    /// <summary>
    /// 50% 无害池与 15% 敌对池共享这一列；10% tier 才切到
    /// <see cref="TerrorbeakCap"/>。两列都只约束同一 owner 的两种影怪。
    /// </summary>
    internal int BaseCap { get; }

    internal int TerrorbeakCap { get; }

    internal int GetCap(SanityShadowPoolTier tier)
    {
        return tier == SanityShadowPoolTier.Hostile10
            ? TerrorbeakCap
            : tier is SanityShadowPoolTier.Harmless50
                or SanityShadowPoolTier.Hostile15
                ? BaseCap
                : 0;
    }
}

/// <summary>
/// SanityMonsterIntensity 的唯一密度表。60 游戏分钟是内部分钟，不是现实秒数。
/// </summary>
internal static class SanityShadowBudgetPolicyCatalog
{
    internal const long BaseIntervalMinutes = 60;

    private static readonly IReadOnlyList<SanityShadowBudgetPolicy> FrozenPolicies =
        Array.AsReadOnly(
            new[]
            {
                new SanityShadowBudgetPolicy(
                    SanityMonsterIntensityIds.None,
                    BaseIntervalMinutes,
                    0,
                    0
                ),
                new SanityShadowBudgetPolicy(
                    SanityMonsterIntensityIds.Less,
                    BaseIntervalMinutes * 2,
                    1,
                    1
                ),
                new SanityShadowBudgetPolicy(
                    SanityMonsterIntensityIds.Default,
                    BaseIntervalMinutes,
                    1,
                    2
                ),
                new SanityShadowBudgetPolicy(
                    SanityMonsterIntensityIds.More,
                    BaseIntervalMinutes,
                    2,
                    3
                ),
                new SanityShadowBudgetPolicy(
                    SanityMonsterIntensityIds.Many,
                    BaseIntervalMinutes / 2,
                    3,
                    4
                ),
                new SanityShadowBudgetPolicy(
                    SanityMonsterIntensityIds.Insane,
                    BaseIntervalMinutes / 2,
                    4,
                    5
                ),
            }
        );

    private static readonly IReadOnlyDictionary<string, SanityShadowBudgetPolicy> ById =
        CreateIndex();

    internal static IReadOnlyList<SanityShadowBudgetPolicy> Policies => FrozenPolicies;

    internal static bool TryGet(
        string intensityId,
        out SanityShadowBudgetPolicy? policy
    )
    {
        return ById.TryGetValue(intensityId, out policy);
    }

    private static IReadOnlyDictionary<string, SanityShadowBudgetPolicy> CreateIndex()
    {
        var values = new Dictionary<string, SanityShadowBudgetPolicy>(StringComparer.Ordinal);
        foreach (var policy in FrozenPolicies)
            values.Add(policy.IntensityId, policy);
        return values;
    }
}

internal readonly record struct SanityMonsterIntensityResolution(
    bool HasValue,
    string Value,
    string Reason
);

internal interface ISanityMonsterIntensityProvider
{
    SanityMonsterIntensityResolution Resolve();
}

/// <summary>
/// 只读阶段 02 的 cached resolver；预算评估不会读盘、解析 schema 或反射。
/// </summary>
internal sealed class TypedConfigSanityMonsterIntensityProvider
    : ISanityMonsterIntensityProvider
{
    private readonly TypedConfigResolver resolver;

    internal TypedConfigSanityMonsterIntensityProvider(TypedConfigResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public SanityMonsterIntensityResolution Resolve()
    {
        var resolved = resolver.GetEnum(ConfigKeys.SanityMonsterIntensity);
        return new SanityMonsterIntensityResolution(
            resolved.HasValue,
            resolved.Value,
            resolved.Reason
        );
    }
}

internal sealed class UnavailableSanityMonsterIntensityProvider
    : ISanityMonsterIntensityProvider
{
    private readonly string reason;

    internal UnavailableSanityMonsterIntensityProvider(string reason)
    {
        this.reason = string.IsNullOrWhiteSpace(reason)
            ? "config.runtime-unavailable"
            : reason;
    }

    public SanityMonsterIntensityResolution Resolve()
    {
        return new SanityMonsterIntensityResolution(false, string.Empty, reason);
    }
}
