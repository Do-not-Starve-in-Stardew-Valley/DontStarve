#nullable enable

using System;
using DontStarve.Config;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 「按光照程度降低理智」的执行行为（2026-11-17）。配置缺失/损坏/值非法回落默认
/// ByLight（用户追加拍板，与 GMCM 默认一致），即默认状态本行为生效。光照证据复用黑暗袭击的同一套
/// EnvironmentLightService（同一采样缓存、同一确认口径），不建立第二套读回。
/// </summary>
internal sealed class LightBasedSanityDrain : IPlayerTimeRelatedBehavior
{
    private readonly TypedConfigResolver? configResolver;
    private readonly Func<EnvironmentLightService?>? lightServiceProvider;
    private readonly Func<int>? screenIdProvider;

    internal LightBasedSanityDrain(
        TypedConfigResolver? configResolver,
        Func<EnvironmentLightService?>? lightServiceProvider,
        Func<int>? screenIdProvider
    )
    {
        this.configResolver = configResolver;
        this.lightServiceProvider = lightServiceProvider;
        this.screenIdProvider = screenIdProvider;
    }

    public void Update(Farmer player, string playerKey, long time)
    {
        var ruleResolution = configResolver?.GetEnum(ConfigKeys.SanityNaturalDrainRule);
        var ruleText = ruleResolution is { HasValue: true }
            ? ruleResolution.Value.Value
            : null;
        if (
            SanityNaturalDrainRulePolicy.ResolveOrDefault(ruleText)
            != SanityNaturalDrainRule.ByLight
        )
        {
            return;
        }

        var location = player.currentLocation;
        var lightService = lightServiceProvider?.Invoke();
        if (location is null || lightService is null)
            return;
        var screenId = screenIdProvider?.Invoke() ?? 0;

        // 洞类（矿井/火山）永远按光值；地面与室内有白天分支（室内跟随地表昼夜）。
        // time 是 TimeApi 跨天累计分钟：白天判定必须取模换算，绝不能直接和
        // timeOfDay 格式的 startDarkTime 比较（2026-11-18 白天持续掉 san 回归根因）。
        var isCaveLike =
            location is MineShaftLocation or VolcanoDungeon;
        var isDayOnSurface = SanityNaturalDrainRulePolicy.IsDayOnSurface(
            time,
            Game1.getStartingToGetDarkTime(location)
        );

        var result = lightService.Evaluate(
            player,
            screenId,
            location,
            time
        );
        if (
            result.EvidenceStatus != EnvironmentLightEvidenceStatus.Confirmed
            || !lightService.TryGetDiagnostic(
                SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
                screenId,
                out var diagnostic
            )
            || diagnostic.FinalVisibilityScore is not { } score
        )
        {
            return;
        }

        var drain = SanityNaturalDrainRulePolicy.CalculateLightBasedDrain(
            isDayOnSurface,
            isCaveLike,
            hasConfirmedScore: true,
            score
        );
        if (drain > 0d)
            player.ChangeSanity(-drain, SanityChangeSource.AmbientLight);
    }
}
