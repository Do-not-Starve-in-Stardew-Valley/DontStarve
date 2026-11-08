#nullable enable

using System.Collections.Generic;
using System;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据矿洞、火山和危险层级降低理智；只影响当前所在地点。
/// </summary>
internal class MineShaft : IPlayerTimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.MineShaft";
    private SanitySystemLifecycleCoordinator? lifecycle;
    private readonly PlayerTimeBehaviorState state = new();

    internal MineShaft() { }

    internal MineShaft(SanitySystemLifecycleCoordinator lifecycle)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    internal void BindLifecycle(SanitySystemLifecycleCoordinator lifecycle)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public void Update(Farmer player, long time)
    {
        if (
            !PlayerTimeRelatedBehaviorPlayer.TryGetPlayerKey(player, out var playerKey)
            || lifecycle?.IsPlayerInSleepContext(playerKey) == true
            || !state.TryEnterMinute(playerKey, player.isInBed.Value)
        )
            return;

        var location = player.currentLocation;
        if (location == null)
            return;

        double value;
        if (location is MineShaftLocation mineShaft)
        {
            value = SanityBehaviorRules.CalculateMineLoss(
                new MineSanityContext(
                    MineSanityLocationKind.MineShaft,
                    mineShaft.mineLevel,
                    mineShaft.isDarkArea(),
                    mineShaft.isQuarryArea,
                    mineShaft.isSlimeArea,
                    mineShaft.isMonsterArea,
                    mineShaft.isDinoArea,
                    mineShaft.GetAdditionalDifficulty()
                )
            );
        }
        else if (location is VolcanoDungeon)
        {
            value = SanityBehaviorRules.CalculateMineLoss(
                new MineSanityContext(
                    MineSanityLocationKind.VolcanoDungeon,
                    0,
                    false,
                    false,
                    false,
                    false,
                    false,
                    0
                )
            );
        }
        else
        {
            value = 0;
        }

        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Mine);
    }

    public void Sync(string playerKey, long time, long delta)
    {
        if (delta < 0)
            state.AddRewindWait(playerKey, delta);
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<MineShaftData>(SAVE_KEY);
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
            new MineShaftData { Players = state.CaptureWaits() }
        );
    }

    public void Reset() => state.Clear();
}

internal class MineShaftData
{
    public long Wait { get; init; }

    public Dictionary<string, long>? Players { get; init; }
}
