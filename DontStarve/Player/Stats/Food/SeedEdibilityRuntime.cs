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
    private static MethodInfo? patchedStaminaRecovered;
    private static MethodInfo? patchedHealthRecovered;
    private static bool patchInstalled;
    private static bool actionButtonGuardInstalled;
    private static bool numericSeamInstalled;
    private static bool staminaCorrectionLogged;
    private static bool healthCorrectionLogged;
    private static IMonitor? monitor;

    [ThreadStatic]
    private static SObject? actionButtonSuppressionTarget;

    internal static bool IsEnabled { get; private set; }

    internal static bool IsOperational => patchInstalled && IsEnabled;

    internal static bool VanillaActionButtonGuardInstalled => actionButtonGuardInstalled;

    /// <summary>
    /// True when the corrected Energy/Health seams are installed. They only exist to keep the
    /// numeric answer consistent with the eligibility gate if a third-party mod displaces our
    /// <c>Object.Edibility</c> getter prefix.
    /// </summary>
    internal static bool NumericSeamInstalled => numericSeamInstalled;

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
        var getterPostfix = AccessTools.Method(
            typeof(SeedEdibilityRuntime),
            nameof(EdibilityGetterPostfix)
        );
        // DeclaredMethod, not Method: AccessTools.Method searches base types too and could bind to
        // Item's virtual stub, which would patch every Item in the game instead of only objects.
        var staminaTarget = AccessTools.DeclaredMethod(
            typeof(SObject),
            nameof(SObject.staminaRecoveredOnConsumption)
        );
        var healthTarget = AccessTools.DeclaredMethod(
            typeof(SObject),
            nameof(SObject.healthRecoveredOnConsumption)
        );
        var staminaPostfix = AccessTools.Method(
            typeof(SeedEdibilityRuntime),
            nameof(StaminaRecoveredPostfix)
        );
        var healthPostfix = AccessTools.Method(
            typeof(SeedEdibilityRuntime),
            nameof(HealthRecoveredPostfix)
        );
        if (getter is null || prefix is null || getterPostfix is null)
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
            SeedEdibilityRuntime.monitor = monitor;

            candidate.Patch(
                getter,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(getterPostfix) { priority = Priority.Last }
            );

            // Numeric seam (method 2 / belt-and-braces). The eligibility gate in
            // IClickableMenu reads the raw backing field, while staminaRecoveredOnConsumption
            // reads the Edibility property. If a third-party mod displaces our getter prefix,
            // those two disagree and vanilla renders a negative Energy row (raw -300 * 2.5).
            // These postfixes re-derive both numbers from the same single source of truth.
            if (staminaTarget is not null && healthTarget is not null && staminaPostfix is not null && healthPostfix is not null)
            {
                patchedStaminaRecovered = staminaTarget;
                patchedHealthRecovered = healthTarget;
                candidate.Patch(
                    staminaTarget,
                    postfix: new HarmonyMethod(staminaPostfix) { priority = Priority.Last }
                );
                candidate.Patch(
                    healthTarget,
                    postfix: new HarmonyMethod(healthPostfix) { priority = Priority.Last }
                );
                numericSeamInstalled = true;
            }
            else
            {
                monitor.Log(
                    "Seed Edibility numeric seam is unavailable (Object.staminaRecoveredOnConsumption/healthRecoveredOnConsumption target missing).",
                    LogLevel.Warn
                );
            }
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
            monitor.Log(
                $"Seed Edibility capability: patch-owner={harmony.Id}, patch-installed=True, getter-prefix-priority=First, getter-postfix-priority=Last, numeric-seam-installed={numericSeamInstalled}, action-button-guard-installed={actionButtonGuardInstalled}.",
                LogLevel.Info
            );
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
    /// Single authoritative answer to "what is this object's effective Edibility right now?".
    /// Every seam (the vanilla Tooltip eligibility reads, the <c>Object.Edibility</c> getter prefix
    /// and the corrected Energy/Health seams) resolves through this one method, so the eligibility
    /// gate and the numeric result can never disagree again.
    /// </summary>
    /// <remarks>
    /// This deliberately reads the raw <see cref="SObject.edibility"/> backing field, not the
    /// <see cref="SObject.Edibility"/> property, so it stays correct even when a third-party mod
    /// wins the Harmony race for the property getter, and so it cannot recurse into our own prefix.
    /// </remarks>
    internal static int ResolveEffectiveEdibility(SObject? objectItem)
    {
        if (objectItem is null)
            return SeedEdibilityRules.NativeInedibleEdibility;

        return SeedEdibilityRules.GetEffectiveEdibility(
            Classify(objectItem),
            enabled: IsOperational,
            nativeEdibility: objectItem.edibility.Value
        );
    }

    /// <summary>
    /// Returns the value that Stardew's vanilla Tooltip should observe without mutating the
    /// networked <see cref="SObject.edibility"/> field. Vanilla Tooltip code reads that field
    /// directly instead of calling <see cref="SObject.Edibility"/>, so the getter bridge alone
    /// cannot make a target seed enter the original Energy/Health branch.
    /// </summary>
    internal static int GetEffectiveTooltipEdibility(SObject objectItem)
    {
        return ResolveEffectiveEdibility(objectItem);
    }

    /// <summary>
    /// Effective Energy this object should report, using the vanilla formula with our effective
    /// Edibility. Used by the corrected numeric seam so a seed reports the same number the Tooltip
    /// eligibility gate promised, even when the getter prefix loses the Harmony race.
    /// </summary>
    internal static int ResolveEffectiveStaminaRecovered(SObject? objectItem)
    {
        if (objectItem is null)
            return 0;

        return SeedEdibilityRules.GetVanillaStaminaRecovered(
            objectItem.QualifiedItemId,
            ResolveEffectiveEdibility(objectItem),
            objectItem.Quality
        );
    }

    /// <summary>
    /// Effective Health this object should report, using the vanilla formula with our effective
    /// Edibility. Vanilla's own <c>Edibility &lt; 0 =&gt; 0</c> guard is preserved by the pure
    /// helper, which is what keeps a negative row from ever rendering a Health line.
    /// </summary>
    internal static int ResolveEffectiveHealthRecovered(SObject? objectItem)
    {
        if (objectItem is null)
            return 0;

        return SeedEdibilityRules.GetVanillaHealthRecovered(
            objectItem.QualifiedItemId,
            ResolveEffectiveEdibility(objectItem),
            objectItem.Quality
        );
    }

    internal static bool EdibilityGetterPrefix(SObject __instance, ref int __result)
    {
        if (!IsOperational || __instance is null)
            return true;

        if (ReferenceEquals(__instance, actionButtonSuppressionTarget))
            return true;

        var nativeEdibility = __instance.edibility.Value;
        var effectiveEdibility = ResolveEffectiveEdibility(__instance);
        if (effectiveEdibility == nativeEdibility)
            return true;

        __result = effectiveEdibility;
        return false;
    }

    /// <summary>
    /// Last-word correction for the <c>Object.Edibility</c> property. Runs after every prefix
    /// (including a failing one that skipped the original), so a third-party mod can no longer
    /// make the property disagree with the single source of truth.
    /// </summary>
    internal static void EdibilityGetterPostfix(SObject __instance, ref int __result)
    {
        if (!IsOperational || __instance is null)
            return;

        if (ReferenceEquals(__instance, actionButtonSuppressionTarget))
            return;

        var effectiveEdibility = ResolveEffectiveEdibility(__instance);
        if (effectiveEdibility == __result)
            return;

        __result = effectiveEdibility;
    }

    /// <summary>
    /// Corrects Energy to match the eligibility gate. Vanilla computes
    /// <c>Ceiling(Edibility * 2.5) + Quality * Edibility</c>; when the getter prefix loses the
    /// Harmony race the property reports the raw -300 sentinel and a target seed would render a
    /// negative Energy row. Re-deriving from the single source keeps the promise the gate made.
    /// </summary>
    internal static void StaminaRecoveredPostfix(SObject __instance, ref int __result)
    {
        CorrectNumericSeam(
            __instance,
            ref __result,
            ResolveEffectiveStaminaRecovered,
            ref staminaCorrectionLogged,
            "energy"
        );
    }

    /// <summary>
    /// Corrects Health to match the eligibility gate. Vanilla returns 0 whenever
    /// <c>Edibility &lt; 0</c>, which is why a raw sentinel never produced a negative Health row.
    /// </summary>
    internal static void HealthRecoveredPostfix(SObject __instance, ref int __result)
    {
        CorrectNumericSeam(
            __instance,
            ref __result,
            ResolveEffectiveHealthRecovered,
            ref healthCorrectionLogged,
            "health"
        );
    }

    /// <summary>
    /// Shared guard for both numeric seams. Non-target objects are never touched, so vanilla and
    /// other mods keep full authority outside the seed boundary. Corrections are logged once per
    /// kind, which makes a displaced getter prefix observable in the fan's SMAPI log instead of
    /// silently degrading.
    /// </summary>
    private static void CorrectNumericSeam(
        SObject __instance,
        ref int __result,
        Func<SObject?, int> resolve,
        ref bool alreadyLogged,
        string dimension
    )
    {
        if (!IsOperational || __instance is null)
            return;

        if (Classify(__instance) != SeedClassification.TargetCropSeed)
            return;

        var corrected = resolve(__instance);
        if (corrected == __result)
            return;

        // Safety rail: a target seed must never report a negative gain. This is what guarantees the
        // fan can never be shown (or given) a negative number, even if every other seam is defeated.
        var floored = Math.Max(0, corrected);
        if (!alreadyLogged)
        {
            alreadyLogged = true;
            monitor?.Log(
                $"Seed Edibility numeric seam corrected {dimension} for a target seed (observed={__result}, corrected={floored}, item-id={__instance.ItemId}). "
                    + "This usually means another mod displaced the Object.Edibility getter prefix; the seed now reports the designed value instead.",
                LogLevel.Warn
            );
        }

        __result = floored;
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
        {
            harmony.Unpatch(patchedGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(patchedGetter, HarmonyPatchType.Postfix, harmony.Id);
        }
        if (harmony is not null && patchedActionButton is not null)
        {
            harmony.Unpatch(patchedActionButton, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(patchedActionButton, HarmonyPatchType.Postfix, harmony.Id);
        }
        if (harmony is not null && patchedStaminaRecovered is not null)
            harmony.Unpatch(patchedStaminaRecovered, HarmonyPatchType.Postfix, harmony.Id);
        if (harmony is not null && patchedHealthRecovered is not null)
            harmony.Unpatch(patchedHealthRecovered, HarmonyPatchType.Postfix, harmony.Id);

        harmony = null;
        patchedGetter = null;
        patchedActionButton = null;
        patchedStaminaRecovered = null;
        patchedHealthRecovered = null;
        patchInstalled = false;
        actionButtonGuardInstalled = false;
        numericSeamInstalled = false;
        staminaCorrectionLogged = false;
        healthCorrectionLogged = false;
        actionButtonSuppressionTarget = null;
        IsEnabled = false;
    }
}
