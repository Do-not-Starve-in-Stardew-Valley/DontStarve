#nullable enable

using System.Collections.Generic;
using System;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 按季节日落时间计算夜间理智损耗；午夜到清晨户外损耗更高。
/// </summary>
internal class Night : IPlayerTimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.Night";
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly PlayerTimeBehaviorState state = new();

    internal Night(SanitySystemLifecycleCoordinator lifecycle)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public void Update(Farmer player, long time)
    {
        if (
            !PlayerTimeRelatedBehaviorPlayer.TryGetPlayerKey(player, out var playerKey)
            || lifecycle.IsPlayerInSleepContext(playerKey)
            || !state.TryEnterMinute(playerKey, player.isInBed.Value)
        )
            return;

        var location = player.currentLocation;
        if (location == null)
            return;

        var locationKind = location switch
        {
            MineShaftLocation => MineSanityLocationKind.MineShaft,
            VolcanoDungeon => MineSanityLocationKind.VolcanoDungeon,
            _ => MineSanityLocationKind.Other,
        };
        if (!SanityBehaviorRules.ShouldApplyNightLoss(locationKind))
            return;

        // Stardew 不同季节天黑时间不同；这里按 1 分钟粒度拆分“日落后”和“午夜后”两段损耗。
        var season = Game1.season switch
        {
            Season.Spring => SanitySeason.Spring,
            Season.Summer => SanitySeason.Summer,
            Season.Fall => SanitySeason.Fall,
            Season.Winter => SanitySeason.Winter,
            _ => throw new InvalidOperationException("unknown-season"),
        };
        var nightfallStart = SanityBehaviorRules.GetNightfallStartMinutes(season);

        var value = SanityBehaviorRules.CalculateNightLossForMinute(
            time,
            nightfallStart,
            location.IsOutdoors
        );
        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Night);
    }

    public void Sync(string playerKey, long time, long delta)
    {
        if (delta < 0)
            state.AddRewindWait(playerKey, delta);
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<NightData>(SAVE_KEY);
        var legacyPlayerKey = PlayerTimeRelatedBehaviorPlayer.TryGetPlayerKey(
            Game1.MasterPlayer,
            out var playerKey
        )
            ? playerKey
            : null;
        state.Load(data?.Players, legacyPlayerKey, data?.Wait ?? 0);
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(
            SAVE_KEY,
            new NightData { Players = state.CaptureWaits() }
        );
    }

    public void Reset() => state.Clear();
}

internal class NightData
{
    public long Wait { get; init; }

    public Dictionary<string, long>? Players { get; init; }
}
