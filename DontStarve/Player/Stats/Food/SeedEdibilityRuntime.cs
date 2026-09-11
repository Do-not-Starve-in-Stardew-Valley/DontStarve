#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Food;

/// <summary>
/// Bridges the pure seed policy to Stardew's native Object.Edibility getter. The backing NetInt is
/// deliberately left untouched, so disabling the feature restores the native value immediately and
/// no synthetic Edibility is serialized or synchronized.
/// </summary>
internal static class SeedEdibilityRuntime
{
    private static Harmony? harmony;
    private static MethodInfo? patchedGetter;
    private static MethodInfo? patchedActionButton;
    private static bool patchInstalled;
    private static bool actionButtonGuardInstalled;

    [ThreadStatic]
    private static SObject? actionButtonSuppressionTarget;

    internal static bool IsEnabled { get; private set; }

    internal static bool IsOperational => patchInstalled && IsEnabled;

    internal static bool VanillaActionButtonGuardInstalled => actionButtonGuardInstalled;

    internal static bool TryInstall(string modId, IMonitor monitor)
    {
        if (patchInstalled)
            return true;

        if (string.IsNullOrWhiteSpace(modId))
        {
            monitor.Log("Seed Edibility runtime is disabled (empty patch owner ID).", LogLevel.Warn);
            return false;
        }

        var getter = AccessTools.PropertyGetter(typeof(SObject), nameof(SObject.Edibility));
        var prefix = AccessTools.Method(
            typeof(SeedEdibilityRuntime),
            nameof(EdibilityGetterPrefix)
        );
        var actionButton = AccessTools.Method(
            typeof(Game1),
            nameof(Game1.pressActionButton)
        );
        var actionPrefix = AccessTools.Method(
            typeof(SeedEdibilityRuntime),
            nameof(VanillaActionButtonPrefix)
        );
        var actionPostfix = AccessTools.Method(
            typeof(SeedEdibilityRuntime),
            nameof(VanillaActionButtonPostfix)
        );
        if (getter is null || prefix is null)
        {
            monitor.Log(
                "Seed Edibility runtime is disabled (Object.Edibility getter target unavailable).",
                LogLevel.Warn
            );
            return false;
        }

        try
        {
            var candidate = new Harmony(string.Concat(modId, ".SeedEdibility"));
            harmony = candidate;
            patchedGetter = getter;
            candidate.Patch(
                getter,
                prefix: new HarmonyMethod(prefix)
            );
            if (actionButton is not null && actionPrefix is not null && actionPostfix is not null)
            {
                patchedActionButton = actionButton;
                candidate.Patch(
                    actionButton,
                    prefix: new HarmonyMethod(actionPrefix),
                    postfix: new HarmonyMethod(actionPostfix)
                );
                actionButtonGuardInstalled = true;
            }
            else
            {
                monitor.Log(
                    "Seed Edibility action-button guard is unavailable (Game1.pressActionButton target or guard methods missing).",
                    LogLevel.Warn
                );
            }
            patchInstalled = true;
            return true;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Seed Edibility runtime is disabled ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
            actionButtonGuardInstalled = false;
            return false;
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
    }

    internal static SeedClassification Classify(Item? item)
    {
        if (item is not SObject objectItem)
            return SeedClassification.NotTarget;

        if (
            string.Equals(
                objectItem.ItemId,
                SeedEdibilityRules.ExcludedSproutingStoneFruitItemId,
                StringComparison.Ordinal
            )
        )
        {
            return SeedClassification.ExplicitlyExcluded;
        }

        if (
            Game1.objectData is null
            || !Game1.objectData.TryGetValue(objectItem.ItemId, out var objectData)
            || objectData is null
        )
        {
            return SeedEdibilityRules.Classify(
                new SeedMetadataFacts(objectItem.ItemId, null, HasObjectData: false)
            );
        }

        return SeedEdibilityRules.Classify(
            new SeedMetadataFacts(objectItem.ItemId, objectData.Type, HasObjectData: true)
        );
    }

    internal static bool IsTargetSeed(Item? item)
    {
        return Classify(item) == SeedClassification.TargetCropSeed;
    }

    internal static bool ShouldExposeFoodRules(Item? item)
    {
        return IsOperational && IsTargetSeed(item);
    }

    /// <summary>
    /// Returns the value that Stardew's vanilla Tooltip should observe without mutating the
    /// networked <see cref="SObject.edibility"/> field. Vanilla Tooltip code reads that field
    /// directly instead of calling <see cref="SObject.Edibility"/>, so the getter bridge alone
    /// cannot make a target seed enter the original Energy/Health branch.
    /// </summary>
    internal static int GetEffectiveTooltipEdibility(SObject objectItem)
    {
        if (objectItem is null)
            return -300;

        var nativeEdibility = objectItem.edibility.Value;
        return SeedEdibilityRules.GetEffectiveEdibility(
            Classify(objectItem),
            enabled: IsOperational,
            nativeEdibility
        );
    }

    internal static bool EdibilityGetterPrefix(SObject __instance, ref int __result)
    {
        if (!IsOperational || __instance is null)
            return true;

        if (ReferenceEquals(__instance, actionButtonSuppressionTarget))
            return true;

        var nativeEdibility = __instance.edibility.Value;
        var effectiveEdibility = SeedEdibilityRules.GetEffectiveEdibility(
            Classify(__instance),
            enabled: true,
            nativeEdibility
        );
        if (effectiveEdibility == nativeEdibility)
            return true;

        __result = effectiveEdibility;
        return false;
    }

    internal static void VanillaActionButtonPrefix()
    {
        actionButtonSuppressionTarget = null;
        if (!IsOperational || Game1.player?.ActiveObject is not SObject activeObject)
            return;

        if (IsTargetSeed(activeObject))
            actionButtonSuppressionTarget = activeObject;
    }

    internal static void VanillaActionButtonPostfix()
    {
        actionButtonSuppressionTarget = null;
    }

    internal static void Dispose()
    {
        if (harmony is not null && patchedGetter is not null)
            harmony.Unpatch(patchedGetter, HarmonyPatchType.Prefix, harmony.Id);
        if (harmony is not null && patchedActionButton is not null)
        {
            harmony.Unpatch(patchedActionButton, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(patchedActionButton, HarmonyPatchType.Postfix, harmony.Id);
        }

        harmony = null;
        patchedGetter = null;
        patchedActionButton = null;
        patchInstalled = false;
        actionButtonGuardInstalled = false;
        actionButtonSuppressionTarget = null;
        IsEnabled = false;
    }
}
