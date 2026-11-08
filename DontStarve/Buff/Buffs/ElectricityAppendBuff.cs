#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 给每个拥有电击 Buff 的玩家追加攻击倍率。客机只报告归属玩家的 Buff，主机写回权威 Buff。
/// </summary>
internal sealed class ElectricityAppendBuff : INonTimeRelatedBuff
{
    private const string ELECTRICITY_APPEND_BUFF_ID = "DS_Electric";
    private const string LEGACY_ELECTRICITY_APPEND_BUFF_ID = "DS_BUFF_ELECTRICITY_APPEND";
    private const float BASE_BONUS = 0.5f;
    private const float WEATHER_BONUS = 1.0f;
    private static readonly string[] ELECTRICITY_APPEND_BUFF_IDS =
    {
        ELECTRICITY_APPEND_BUFF_ID,
        LEGACY_ELECTRICITY_APPEND_BUFF_ID,
    };

    private readonly BuffMultiplayerCoordinator multiplayer;
    private readonly Dictionary<string, ElectricityTracking> tracked =
        new(StringComparer.Ordinal);

    internal ElectricityAppendBuff(BuffMultiplayerCoordinator multiplayer)
    {
        this.multiplayer = multiplayer;
    }

    public void Init(IModHelper helper)
    {
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => Update();
        helper.Events.GameLoop.SaveLoaded += (_, _) => ResetTracking();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetTracking();
        multiplayer.ElectricityObservationReceived += OnRemoteObservation;
        multiplayer.ElectricityStateReceived += OnAuthoritativeState;
    }

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
                // 客机玩家由自己的进程报告；主机只直接处理本地玩家。
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
        if (!TryGetElectricBuff(player, out var electricBuff, out var buffId))
        {
            ClearTrackedBonus(playerKey, player);
            if (sendObservation)
            {
                multiplayer.ObserveElectricity(
                    new ElectricityBuffObservation
                    {
                        PlayerKey = playerKey,
                        BuffId = ELECTRICITY_APPEND_BUFF_ID,
                    }
                );
            }
            return;
        }

        if (sendObservation)
        {
            multiplayer.ObserveElectricity(
                new ElectricityBuffObservation
                {
                    PlayerKey = playerKey,
                    BuffId = buffId,
                }
            );
            return;
        }

        ApplyForPlayer(player, playerKey, electricBuff);
    }

    private void OnRemoteObservation(
        ElectricityBuffObservation observation,
        long senderPlayerId
    )
    {
        if (
            !Context.IsMainPlayer
            || !ContainsBuffId(observation.BuffId)
            || Game1.GetPlayer(senderPlayerId, onlyOnline: true) is not { } player
        )
            return;

        var playerKey = PlayerKey(player);
        if (!string.Equals(playerKey, observation.PlayerKey, StringComparison.Ordinal))
            return;

        if (!TryGetElectricBuff(player, out var electricBuff, out _))
        {
            ClearTrackedBonus(playerKey, player);
            multiplayer.PublishElectricityState(
                senderPlayerId,
                playerKey,
                ELECTRICITY_APPEND_BUFF_ID,
                active: false,
                bonus: 0f
            );
            return;
        }

        ApplyForPlayer(player, playerKey, electricBuff);
        multiplayer.PublishElectricityState(
            senderPlayerId,
            playerKey,
            ELECTRICITY_APPEND_BUFF_ID,
            active: true,
            bonus: GetTotalBonus()
        );
    }

    private void OnAuthoritativeState(ElectricityBuffStateMessage message)
    {
        if (
            Context.IsMainPlayer
            || Game1.player is not { } player
            || !message.Active
        )
        {
            if (!message.Active && Game1.player is { } localPlayer)
                ClearTrackedBonus(PlayerKey(localPlayer), localPlayer);
            return;
        }

        if (!TryGetElectricBuff(player, out var electricBuff, out _))
        {
            ClearTrackedBonus(PlayerKey(player), player);
            return;
        }

        ApplyForPlayer(
            player,
            PlayerKey(player),
            electricBuff,
            message.Bonus
        );
    }

    private void ApplyForPlayer(
        Farmer player,
        string playerKey,
        StardewValley.Buff electricBuff,
        float? authoritativeBonus = null
    )
    {
        if (!tracked.TryGetValue(playerKey, out var state))
        {
            state = new ElectricityTracking();
            tracked[playerKey] = state;
        }

        if (!ReferenceEquals(electricBuff, state.TrackedBuff))
        {
            ClearTrackedBonus(playerKey, player);
            state = new ElectricityTracking
            {
                TrackedBuff = electricBuff,
                TrackedBuffBaseAttackMultiplier =
                    electricBuff.effects.AttackMultiplier.Value,
            };
            tracked[playerKey] = state;
        }

        var totalBonus = authoritativeBonus ?? GetTotalBonus();
        var targetAttackMultiplier =
            state.TrackedBuffBaseAttackMultiplier + totalBonus;
        if (electricBuff.effects.AttackMultiplier.Value == targetAttackMultiplier)
            return;

        // 不直接写 player.buffs.GetValues() 的聚合结果；原版下一次 Dirty 重算会覆盖聚合值。
        electricBuff.effects.AttackMultiplier.Set(targetAttackMultiplier);
        state.AppliedBonus = totalBonus;
        player.buffs.Dirty = true;
    }

    private static bool TryGetElectricBuff(
        Farmer player,
        out StardewValley.Buff electricBuff,
        out string buffId
    )
    {
        foreach (var id in ELECTRICITY_APPEND_BUFF_IDS)
        {
            if (
                player.buffs.AppliedBuffs.TryGetValue(id, out var candidate)
                && candidate is not null
            )
            {
                electricBuff = candidate;
                buffId = id;
                return true;
            }
        }

        electricBuff = null!;
        buffId = string.Empty;
        return false;
    }

    private static bool ContainsBuffId(string buffId)
    {
        foreach (var id in ELECTRICITY_APPEND_BUFF_IDS)
        {
            if (string.Equals(id, buffId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static float GetTotalBonus()
    {
        var hasWeatherBuff =
            Game1.isRaining || Game1.isGreenRain || Game1.isLightning || Game1.isSnowing;
        return BASE_BONUS + (hasWeatherBuff ? WEATHER_BONUS : 0f);
    }

    private void ClearTrackedBonus(string playerKey, Farmer player)
    {
        if (!tracked.TryGetValue(playerKey, out var state))
            return;

        if (state.TrackedBuff is not null && state.AppliedBonus != 0f)
        {
            state.TrackedBuff.effects.AttackMultiplier.Set(
                state.TrackedBuffBaseAttackMultiplier
            );
            player.buffs.Dirty = true;
        }

        tracked.Remove(playerKey);
    }

    private void ResetTracking()
    {
        tracked.Clear();
    }

    private static string PlayerKey(Farmer player) =>
        player.UniqueMultiplayerID.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );

    private sealed class ElectricityTracking
    {
        internal StardewValley.Buff? TrackedBuff { get; init; }

        internal float TrackedBuffBaseAttackMultiplier { get; init; }

        internal float AppliedBonus { get; set; }
    }
}
