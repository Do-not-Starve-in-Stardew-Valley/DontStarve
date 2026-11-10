#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using DontStarve.Player.Death;
using DontStarve.Player.Stats;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// 按统一生存状态时间 owner 的普通分钟消耗饥饿；饥饿归零后按玩家自己的节奏扣生命。
/// 睡眠分钟由 SleepSystem 联动结算，重复区间由 owner 去重。
/// </summary>
internal sealed class HungerCycle : ISurvivalStateTimeBehavior<Farmer>
{
    private const string SAVE_KEY = "DontStarve.Hunger.HungerCycle";

    private readonly HungerChangeService changeService;
    private readonly IMonitor monitor;
    private readonly HungerEquipmentRuntime equipmentRuntime;
    private readonly SmapiHungerDamageExecutor damageExecutor;
    private readonly Dictionary<string, HungerCycleRuntimeState> states =
        new(StringComparer.Ordinal);
    private Action<Farmer>? lowWarningHandler;
    private bool persistenceWriteEnabled = true;

    internal HungerCycle(
        HungerChangeService changeService,
        IMonitor monitor,
        HungerEquipmentRuntime? equipmentRuntime = null,
        Func<HungerDamageMode>? damageModeProvider = null,
        PlayerDeathCauseLedger? deathCauseLedger = null
    )
    {
        this.changeService = changeService;
        this.monitor = monitor;
        this.equipmentRuntime = equipmentRuntime ?? new HungerEquipmentRuntime();
        damageExecutor = new SmapiHungerDamageExecutor(
            damageModeProvider ?? (() => HungerDamageMode.Default),
            deathCauseLedger is null ? null : deathCauseLedger.MarkHunger
        );
    }

    internal void SetLowWarningHandler(Action<Farmer>? handler)
    {
        lowWarningHandler = handler;
    }

    internal void ResumeAfterSleep(Farmer player)
    {
        if (!HungerExtensions.IsEnabled || changeService.Role != HungerAuthorityRole.Host
            || player is null)
            return;
        var playerKey = player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);
        if (!HungerProtocol.IsValidPlayerKey(playerKey))
            return;
        var state = GetOrCreateState(playerKey);
        var boundary = HungerStarvationRules.ResumeAfterSleep(player.GetHunger());
        state.IsStarving = boundary.IsStarving;
        state.ZeroDiagnosticLogged = boundary.IsStarving;
        state.DamageDiagnosticLogged = false;
    }

    public void OnMinute(
        Farmer player,
        string playerKey,
        long time,
        SurvivalStateTimePhase phase
    )
    {
        if (phase != SurvivalStateTimePhase.Ordinary)
            return;

        if (changeService.Role != HungerAuthorityRole.Host)
        {
            states.Clear();
            return;
        }

        if (!HungerExtensions.IsEnabled)
        {
            if (
                HungerProtocol.IsValidPlayerKey(playerKey)
                && states.TryGetValue(playerKey, out var pausedState)
            )
                pausedState.Pause();
            return;
        }

        if (!HungerProtocol.IsValidPlayerKey(playerKey))
            return;

        changeService.TryEnsureHostPlayer(playerKey, out _, out _);
        var state = GetOrCreateState(playerKey);
        if (changeService.IsDebugLocked)
        {
            state.Reset();
            return;
        }

        var currentHunger = player.GetHunger();
        var loadout = equipmentRuntime.Capture(player);
        // The canonical natural rate remains HungerRules.NaturalDecayPerMinute (0.052d);
        // equipment only supplies the separate multiplier and fixed-recovery inputs here.
        var minute = HungerRules.ApplyNaturalMinute(
            currentHunger,
            player.GetMaxHunger(),
            HungerEquipmentRules.CalculateConsumptionMultiplier(loadout),
            HungerEquipmentRules.CalculateFixedRecoveryPerMinute(loadout)
        );
        player.SetHunger(minute.BoundedCurrent);
        var warning = HungerWarningRules.Evaluate(
            minute.BoundedCurrent,
            player.GetMaxHunger(),
            state.IsInLowValueRegion
        );
        state.IsInLowValueRegion = warning.IsLow;
        if (warning.ShouldWarn)
            lowWarningHandler?.Invoke(player);

        if (minute.BoundedCurrent <= 0d && !state.ZeroDiagnosticLogged)
        {
            state.ZeroDiagnosticLogged = true;
            monitor.Log(
                $"hunger.runtime.zero-evaluated player={playerKey} time={time} before={currentHunger:F3} after={minute.BoundedCurrent:F3}",
                LogLevel.Debug
            );
        }

        var starvation = HungerStarvationRules.Evaluate(
            state.IsStarving,
            minute.BoundedCurrent
        );
        state.IsStarving = starvation.IsStarving;
        if (!starvation.DamageDue)
        {
            if (!state.IsStarving)
            {
                state.ZeroDiagnosticLogged = false;
                state.DamageDiagnosticLogged = false;
            }
            return;
        }

        ApplyDamageWithDiagnostic(player, playerKey, time, state);
    }

    private void ApplyDamageWithDiagnostic(
        Farmer player,
        string playerKey,
        long time,
        HungerCycleRuntimeState state
    )
    {
        var applied = damageExecutor.TryApply(player, out var result);
        if (state.DamageDiagnosticLogged)
            return;

        state.DamageDiagnosticLogged = true;
        monitor.Log(
            $"hunger.runtime.damage-evaluated player={playerKey} time={time} applied={applied} damage={result.Damage} health={result.RemainingHealth}",
            LogLevel.Debug
        );
    }

    public void Load(IModHelper helper)
    {
        ResetRuntime();
        persistenceWriteEnabled = true;
        if (!Context.IsMainPlayer || changeService.Role != HungerAuthorityRole.Host)
            return;

        object? raw;
        try
        {
            raw = helper.Data.ReadSaveData<object>(SAVE_KEY);
        }
        catch (Exception ex)
        {
            persistenceWriteEnabled = false;
            monitor.Log(
                $"Hunger cycle data was unreadable; runtime cursor reset ({ex.GetType().Name}).",
                LogLevel.Error
            );
            return;
        }
        if (raw is null)
            return;

        HungerCycleSaveData? data;
        try
        {
            var rawJson = raw.ToString() ?? string.Empty;
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("cycle-root-is-not-an-object");
            if (!root.TryGetProperty("SchemaVersion", out _))
            {
                persistenceWriteEnabled = false;
                monitor.Log(
                    "Legacy Hunger cycle cursor has no player/time authority; runtime cursor was cleared and the old value will not be overwritten.",
                    LogLevel.Warn
                );
                return;
            }
            data = JsonSerializer.Deserialize<HungerCycleSaveData>(rawJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            persistenceWriteEnabled = false;
            monitor.Log(
                $"Hunger cycle data was invalid; runtime cursor reset and old value was preserved ({ex.GetType().Name}).",
                LogLevel.Error
            );
            return;
        }

        if (
            data is null
            || data.Players is null
            || data.SchemaVersion < 1
            || data.SchemaVersion > HungerCycleSaveData.CurrentSchemaVersion
        )
        {
            persistenceWriteEnabled = false;
            monitor.Log(
                $"Hunger cycle schema {(data is null ? "missing" : data.SchemaVersion.ToString(CultureInfo.InvariantCulture))} is unsupported; runtime cursor reset and old value was preserved.",
                LogLevel.Error
            );
            return;
        }

        foreach (var pair in data.Players)
        {
            if (
                !HungerProtocol.IsValidPlayerKey(pair.Key)
                || pair.Value is null
                || pair.Value.Wait < 0
            )
                continue;

            states[pair.Key] = new HungerCycleRuntimeState
            {
                // Schema 1/2 LastTime/LastHasHunger fields are intentionally ignored.
                // The new phase is safe to resume; an absolute old timestamp is not.
                IsStarving = data.SchemaVersion >= 3 && pair.Value.IsStarving,
            };
        }
    }

    public void Save(IModHelper helper)
    {
        if (
            !Context.IsMainPlayer
            || changeService.Role != HungerAuthorityRole.Host
            || !persistenceWriteEnabled
        )
            return;

        var data = new HungerCycleSaveData();
        foreach (var pair in states)
        {
            data.Players[pair.Key] = new HungerCyclePlayerSaveData
            {
                IsStarving = pair.Value.IsStarving,
                // Older files stored per-behavior rewind waits here. The session-local owner
                // now handles time de-duplication, so preserve the field as zero for schema compatibility.
                Wait = 0,
            };
        }
        helper.Data.WriteSaveData(SAVE_KEY, data);
    }

    internal void ResetRuntime()
    {
        states.Clear();
    }

    private HungerCycleRuntimeState GetOrCreateState(string playerKey)
    {
        if (states.TryGetValue(playerKey, out var existing))
            return existing;

        var created = new HungerCycleRuntimeState();
        states[playerKey] = created;
        return created;
    }

    private sealed class HungerCycleRuntimeState
    {
        internal bool IsStarving { get; set; }

        internal bool IsInLowValueRegion { get; set; }

        internal bool ZeroDiagnosticLogged { get; set; }

        internal bool DamageDiagnosticLogged { get; set; }

        // A disabled feature pauses its phase; only the minute cursor and diagnostic debounce reset.
        // Keep IsStarving so the first active zero-Hunger minute resumes damage without backfill.
        internal void Pause()
        {
            ZeroDiagnosticLogged = false;
            DamageDiagnosticLogged = false;
        }

        internal void Reset()
        {
            IsStarving = false;
            IsInLowValueRegion = false;
            ZeroDiagnosticLogged = false;
            DamageDiagnosticLogged = false;
        }
    }
}
