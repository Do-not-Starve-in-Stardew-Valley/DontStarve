#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace DontStarve.Player.Sleep;

/// <summary>Narrow native boundaries. HP is finalized after dayupdate, and remote HP before
/// NetWorldState clones a farmhand into the save. Native multiplayer stamina regen is retained.</summary>
internal sealed class SmapiSleepEngineBridge : IDisposable
{
    private static SmapiSleepEngineBridge? current;
    private readonly SmapiSleepLifecycleAdapter adapter;
    private readonly Harmony harmony;
    private readonly IMonitor monitor;
    internal bool Available { get; private set; }

    internal SmapiSleepEngineBridge(SmapiSleepLifecycleAdapter adapter, IMonitor monitor, string modId)
    {
        this.adapter = adapter;
        this.monitor = monitor;
        harmony = new Harmony(modId + ".Sleep.BeforeSave");
        if (current is not null)
        {
            monitor.Log("Sleep native hooks unavailable: another owner is already registered.", LogLevel.Error);
            return;
        }
        current = this;
        try
        {
            var day = AccessTools.Method(typeof(Farmer), nameof(Farmer.dayupdate), new[] { typeof(int) });
            var save = AccessTools.Method(typeof(NetWorldState), nameof(NetWorldState.SaveFarmhand), new[] { typeof(NetFarmerRoot) });
            var update = AccessTools.Method(typeof(Farmer), nameof(Farmer.Update), new[] { typeof(GameTime), typeof(GameLocation) });
            if (day is null || save is null || update is null)
                throw new InvalidOperationException("sleep-native-signature-is-unavailable");
            var updatePrefix = AccessTools.Method(
                typeof(SmapiSleepEngineBridge),
                nameof(BeforeFarmerUpdate)
            );
            if (updatePrefix is null)
                throw new InvalidOperationException("sleep-native-update-prefix-is-unavailable");
            harmony.Patch(
                update,
                prefix: new HarmonyMethod(updatePrefix) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(SmapiSleepEngineBridge), nameof(GuardBedHealthRecovery))
            );
            harmony.Patch(day, postfix: new HarmonyMethod(typeof(SmapiSleepEngineBridge), nameof(AfterDayUpdate)));
            harmony.Patch(save, prefix: new HarmonyMethod(typeof(SmapiSleepEngineBridge), nameof(BeforeFarmhandClone)));
            Available = true;
        }
        catch (Exception exception)
        {
            harmony.UnpatchAll(harmony.Id);
            monitor.Log($"Sleep native hooks unavailable ({exception.GetType().Name}: {exception.Message}); custom sleep stayed disabled.", LogLevel.Error);
        }
    }

    private static void AfterDayUpdate(Farmer __instance) => current?.adapter.OnNativeDayUpdated(__instance);

    private static void BeforeFarmerUpdate(Farmer __instance) =>
        current?.adapter.BeforeNativeFarmerUpdate(__instance);

    private static void BeforeFarmhandClone(NetFarmerRoot farmhand)
    {
        if (farmhand?.Value is Farmer farmer)
            current?.adapter.OnNativeFarmhandSaving(farmer);
    }

    private static IEnumerable<CodeInstruction> GuardBedHealthRecovery(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        var field = AccessTools.Field(typeof(Farmer), nameof(Farmer.health));
        var matches = new List<int>();
        for (var index = 5; index < codes.Count; index++)
        {
            if (codes[index].StoresField(field) && codes[index - 1].opcode == OpCodes.Add
                && codes[index - 2].LoadsConstant(1) && codes[index - 3].LoadsField(field)
                && codes[index - 5].opcode == OpCodes.Ldarg_0
                && (codes[index - 4].opcode == OpCodes.Ldarg_0 || codes[index - 4].opcode == OpCodes.Dup))
                matches.Add(index - 5);
        }
        if (matches.Count != 1)
            throw new InvalidOperationException("sleep-native-bed-health-increment-not-unique");
        var start = matches[0];
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        foreach (var code in codes.GetRange(start, 6))
        {
            load.labels.AddRange(code.labels);
            load.blocks.AddRange(code.blocks);
        }
        codes.RemoveRange(start, 6);
        codes.InsertRange(start, new[] { load, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(SmapiSleepEngineBridge), nameof(RecoverNativeBedHealth))) });
        return codes;
    }

    private static void RecoverNativeBedHealth(Farmer farmer)
    {
        if (current?.adapter.ShouldSuppressNativeBedHealth(farmer) != true)
            farmer.health++;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this))
            return;
        harmony.UnpatchAll(harmony.Id);
        current = null;
        Available = false;
    }
}
