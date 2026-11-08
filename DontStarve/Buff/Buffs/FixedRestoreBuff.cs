using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 按 active Stardew Buff 的毫秒生命周期释放固定总量恢复，不跟随游戏内分钟速度变化。
/// 客机只报告本地 Buff，主机按 player key 结算，避免主客机各恢复一次。
/// </summary>
internal abstract class FixedRestoreBuff : INonTimeRelatedBuff
{
    private readonly BuffMultiplayerCoordinator multiplayer;
    private readonly Dictionary<string, FixedRestoreBuffTiming> timings =
        new(StringComparer.Ordinal);

    protected FixedRestoreBuff(BuffMultiplayerCoordinator multiplayer)
    {
        this.multiplayer = multiplayer;
    }

    protected abstract IReadOnlyList<string> BuffIds { get; }
    protected abstract int AmountPerPulse { get; }

    public void Init(IModHelper helper)
    {
        // 恢复计量不读写独立存档；active Buff 的总时长/剩余时长足以重建周期。
        helper.Events.GameLoop.SaveLoaded += (_, _) => ResetTracking();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetTracking();
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => Update();
        multiplayer.RestoreObservationReceived += OnRemoteObservation;
    }

    protected abstract void ApplyRestore(Farmer player, int amount);

    private void Update()
    {
        if (!Context.IsWorldReady)
        {
            ResetTracking();
            return;
        }

        if (Context.IsMainPlayer)
        {
            foreach (var player in Game1.getOnlineFarmers())
            {
                // 远程玩家由其客户端报告 Buff，主机只结算本地玩家，避免同一生命周期双算。
                if (player.IsLocalPlayer)
                    ObserveLocalPlayer(player, sendObservation: false);
            }
            return;
        }

        if (Game1.player is not null)
            ObserveLocalPlayer(Game1.player, sendObservation: true);
    }

    private void ObserveLocalPlayer(Farmer player, bool sendObservation)
    {
        var playerKey = PlayerKey(player);
        if (!TryGetBuff(player, out var buff, out var buffId))
        {
            ResetTiming(playerKey);
            if (sendObservation)
            {
                multiplayer.ObserveRestore(
                    new RestoreBuffObservation
                    {
                        PlayerKey = playerKey,
                        BuffId = BuffIds[0],
                        TotalMilliseconds = 0,
                        RemainingMilliseconds = 0,
                    }
                );
            }
            return;
        }

        var totalMilliseconds = GetFiniteDuration(
            buff.totalMillisecondsDuration,
            buff.millisecondsDuration
        );
        var remainingMilliseconds = GetFiniteDuration(
            buff.millisecondsDuration,
            totalMilliseconds
        );
        if (totalMilliseconds <= 0 || remainingMilliseconds <= 0)
        {
            ResetTiming(playerKey);
            if (sendObservation)
            {
                multiplayer.ObserveRestore(
                    new RestoreBuffObservation
                    {
                        PlayerKey = playerKey,
                        BuffId = buffId,
                        TotalMilliseconds = 0,
                        RemainingMilliseconds = 0,
                    }
                );
            }
            return;
        }

        if (sendObservation)
        {
            multiplayer.ObserveRestore(
                new RestoreBuffObservation
                {
                    PlayerKey = playerKey,
                    BuffId = buffId,
                    TotalMilliseconds = totalMilliseconds,
                    RemainingMilliseconds = remainingMilliseconds,
                }
            );
            return;
        }

        ApplyDueRestore(player, playerKey, buffId, totalMilliseconds, remainingMilliseconds);
    }

    private void OnRemoteObservation(
        RestoreBuffObservation observation,
        long senderPlayerId
    )
    {
        if (!Context.IsMainPlayer || !ContainsBuffId(observation.BuffId))
            return;
        if (Game1.GetPlayer(senderPlayerId, onlyOnline: true) is not { } player)
            return;

        var playerKey = PlayerKey(player);
        if (!string.Equals(playerKey, observation.PlayerKey, StringComparison.Ordinal))
            return;

        if (observation.TotalMilliseconds <= 0 || observation.RemainingMilliseconds <= 0)
        {
            ResetTiming(playerKey);
            return;
        }

        ApplyDueRestore(
            player,
            playerKey,
            observation.BuffId,
            observation.TotalMilliseconds,
            observation.RemainingMilliseconds
        );
    }

    private void ApplyDueRestore(
        Farmer player,
        string playerKey,
        string buffId,
        int totalMilliseconds,
        int remainingMilliseconds
    )
    {
        if (!timings.TryGetValue(playerKey, out var timing))
        {
            timing = new FixedRestoreBuffTiming();
            timings[playerKey] = timing;
        }

        var pulsesToApply = timing.Observe(
            buffId,
            totalMilliseconds,
            remainingMilliseconds
        );
        if (pulsesToApply > 0)
        {
            // 记录“已释放脉冲”而不是实际恢复量：上限吃掉的恢复不延后补发。
            ApplyRestore(player, pulsesToApply * AmountPerPulse);
        }
    }

    private bool TryGetBuff(
        Farmer player,
        out StardewValley.Buff buff,
        out string buffId
    )
    {
        foreach (var id in BuffIds)
        {
            if (player.buffs.AppliedBuffs.TryGetValue(id, out buff))
            {
                buffId = id;
                return true;
            }
        }

        buff = null;
        buffId = string.Empty;
        return false;
    }

    private static int GetFiniteDuration(int duration, int fallback)
    {
        // ENDLESS Buff 没有固定总量语义，直接忽略；0 或负值再尝试另一个 duration 字段兜底。
        if (duration == StardewValley.Buff.ENDLESS)
            return 0;

        return duration > 0 ? duration : Math.Max(0, fallback);
    }

    private void ResetTiming(string playerKey)
    {
        if (timings.TryGetValue(playerKey, out var timing))
            timing.Reset();
    }

    private void ResetTracking()
    {
        timings.Clear();
    }

    private bool ContainsBuffId(string buffId)
    {
        foreach (var id in BuffIds)
        {
            if (string.Equals(id, buffId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string PlayerKey(Farmer player) =>
        player.UniqueMultiplayerID.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
}
