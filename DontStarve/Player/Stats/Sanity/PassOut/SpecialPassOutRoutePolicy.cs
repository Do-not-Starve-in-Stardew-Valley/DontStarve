#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.Darkness;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal readonly record struct SpecialPassOutRouteContext(
    bool HasDarknessDamageMode,
    DarknessDamageMode DarknessDamageMode,
    bool HasJunimoBlessing,
    bool JunimoBlessingEnabled,
    bool LocationSafe,
    bool JunimoBlessingEligible,
    bool NightOwlAllowsConflictingSpecialDeathChain,
    string NightOwlCompatibilityReason,
    bool IsMultiplayer,
    int TimeOfDay,
    bool ExternalLateNightOwnerDetected = false
);

/// <summary>
/// Decides whether the already-committed vanilla pass-out may enter the special darkness chain.
/// This is deliberately separate from the Harmony adapter: both the local player and the host
/// request path must evaluate the same facts, while ordinary pass-out still owns the -20 Sanity
/// consequence whenever this policy cannot authorize the special path.
/// </summary>
internal static class SpecialPassOutRoutePolicy
{
    internal static PassOutPolicyDecision Resolve(
        PassOutReason reason,
        SpecialPassOutRouteContext context
    )
    {
        if (reason is not (PassOutReason.TimeLimitPassOut or PassOutReason.ExhaustionPassOut))
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.reason-not-eligible"
            );
        }

        if (context.IsMultiplayer && context.TimeOfDay < 2600)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.multiplayer-daytime-pass-out"
            );
        }

        if (context.LocationSafe)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.location-safe"
            );
        }

        if (!context.HasDarknessDamageMode)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.mode-unavailable"
            );
        }

        if (!Enum.IsDefined(typeof(DarknessDamageMode), context.DarknessDamageMode))
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.mode-invalid"
            );
        }

        if (context.DarknessDamageMode == DarknessDamageMode.Off)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.mode-off"
            );
        }

        if (!context.HasJunimoBlessing)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.junimo-config-unavailable"
            );
        }

        if (context.JunimoBlessingEnabled && context.JunimoBlessingEligible)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                "passout.policy.special-route.junimo-blessing-safe"
            );
        }

        if (context.ExternalLateNightOwnerDetected)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                LateNightBehaviorEvidence.ClockRollbackReason
            );
        }

        if (!context.NightOwlAllowsConflictingSpecialDeathChain)
        {
            return PassOutReasonPolicy.ResolveOrdinaryForcedPassOut(
                reason,
                string.IsNullOrWhiteSpace(context.NightOwlCompatibilityReason)
                    ? "passout.policy.special-route.night-owl-plus-suppressed"
                    : context.NightOwlCompatibilityReason
            );
        }

        return new PassOutPolicyDecision(
            PassOutPolicyAction.DelegateToTwoAmSpecialDeath,
            SanityChangeSource.SanityDarknessSpecialDeath,
            0d,
            0d,
            false,
            reason == PassOutReason.ExhaustionPassOut
                ? "passout.policy.special-route.exhaustion-delegated"
                : context.DarknessDamageMode == DarknessDamageMode.NonLethal
                    ? "passout.policy.special-route.time-limit-nonlethal-delegated"
                    : "passout.policy.special-route.time-limit-default-delegated"
        );
    }
}
