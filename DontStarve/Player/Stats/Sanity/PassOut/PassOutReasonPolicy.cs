#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum PassOutReason
{
    Unknown,
    VoluntarySleep,
    TimeLimitPassOut,
    ExhaustionPassOut,
    HealthDeath,
    SanityDarknessSpecialDeath,
}

internal enum PassOutReasonClassificationStatus
{
    Available,
    Ambiguous,
    Unavailable,
}

internal readonly record struct PassOutReasonClassification(
    PassOutReasonClassificationStatus Status,
    PassOutReason Reason,
    int TimeOfDay,
    string StableReason
)
{
    internal bool IsAvailable =>
        Status == PassOutReasonClassificationStatus.Available
        && Reason != PassOutReason.Unknown;
}

internal enum PassOutPolicyAction
{
    None,
    ApplyDelta,
    SetToMaximumFraction,
    DelegateToTwoAmSpecialDeath,
}

internal readonly record struct PassOutPolicyDecision(
    PassOutPolicyAction Action,
    SanityChangeSource Source,
    double Delta,
    double TargetMaximumFraction,
    bool EffectiveTwoAmSpecialDeathSafe,
    string StableReason
);

internal readonly record struct PassOutTimeLimitPolicyContext(
    bool HasDarknessDamageMode,
    DarknessDamageMode DarknessDamageMode,
    bool HasJunimoBlessing,
    bool JunimoBlessingEnabled,
    bool TwoAmSpecialDeathSafe,
    bool JunimoBlessingEligible,
    string LocationReason,
    bool NightOwlPlusAllowsConflictingSpecialDeathChain,
    string NightOwlPlusCompatibilityReason
);

internal readonly record struct PassOutReasonEvidence(
    string SessionId,
    string EvidenceId,
    string PlayerKey,
    int GameDay,
    PassOutReason Reason,
    int TimeOfDay,
    string StableReason
);

internal enum PassOutReasonCaptureStatus
{
    Captured,
    Duplicate,
    Conflict,
    Rejected,
}

internal readonly record struct PassOutReasonCaptureResult(
    PassOutReasonCaptureStatus Status,
    PassOutReasonEvidence Evidence,
    string StableReason
);

/// <summary>
/// Pure reason classifier and behavior matrix. It never infers a reason from DayEnding; callers
/// must provide evidence captured at the engine entry which actually committed the outcome.
/// </summary>
internal static class PassOutReasonPolicy
{
    internal const double OrdinaryPassOutSanityDelta = -20d;
    internal const double HealthDeathMaximumFraction = 0.5d;

    internal static PassOutReasonClassification ClassifyStartToPassOut(
        int timeOfDay,
        float stamina
    )
    {
        if (!float.IsFinite(stamina))
        {
            return new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Unavailable,
                PassOutReason.Unknown,
                timeOfDay,
                "passout.reason.start-to-pass-out.stamina-non-finite"
            );
        }

        var timeLimit = timeOfDay >= 2600;
        var exhaustion = stamina <= -15f;
        if (timeLimit && exhaustion)
        {
            return new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Ambiguous,
                PassOutReason.Unknown,
                timeOfDay,
                "passout.reason.time-exhaustion-overlap.ambiguous-shared-branch"
            );
        }
        if (timeLimit)
        {
            return new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Available,
                PassOutReason.TimeLimitPassOut,
                timeOfDay,
                "passout.reason.time-limit.exclusive-predicate"
            );
        }
        if (exhaustion)
        {
            return new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Available,
                PassOutReason.ExhaustionPassOut,
                timeOfDay,
                "passout.reason.exhaustion.exclusive-predicate"
            );
        }

        return new PassOutReasonClassification(
            PassOutReasonClassificationStatus.Unavailable,
            PassOutReason.Unknown,
            timeOfDay,
            "passout.reason.start-to-pass-out.no-exclusive-predicate"
        );
    }

    internal static PassOutReasonClassification ClassifyVoluntarySleep(int timeOfDay)
    {
        if (
            timeOfDay >= 2600
            || !SanityBehaviorRules.IsOrdinarySleepTime(timeOfDay)
        )
        {
            return new PassOutReasonClassification(
                PassOutReasonClassificationStatus.Unavailable,
                PassOutReason.Unknown,
                timeOfDay,
                "passout.reason.voluntary-sleep.committed-time-invalid"
            );
        }

        return new PassOutReasonClassification(
            PassOutReasonClassificationStatus.Available,
            PassOutReason.VoluntarySleep,
            timeOfDay,
            "passout.reason.voluntary-sleep.committed"
        );
    }

    internal static PassOutPolicyDecision ResolveTimeLimitRoute(
        PassOutTimeLimitPolicyContext context
    )
    {
        if (context.TwoAmSpecialDeathSafe)
            return OrdinaryTimeLimit("passout.policy.time-limit.location-safe");

        if (!context.HasDarknessDamageMode)
        {
            return OrdinaryTimeLimit(
                "passout.policy.time-limit.mode-unavailable-kept-ordinary"
            );
        }
        if (!Enum.IsDefined(typeof(DarknessDamageMode), context.DarknessDamageMode))
        {
            return OrdinaryTimeLimit(
                "passout.policy.time-limit.mode-invalid-kept-ordinary"
            );
        }
        if (context.DarknessDamageMode == DarknessDamageMode.Off)
            return OrdinaryTimeLimit("passout.policy.time-limit.mode-off");

        // A missing/bad configuration must not authorize a dangerous special flow. The ordinary
        // vanilla pass-out remains available and still owns its normal -20 Sanity policy.
        if (!context.HasJunimoBlessing)
        {
            return OrdinaryTimeLimit(
                "passout.policy.time-limit.junimo-config-unavailable-kept-ordinary"
            );
        }
        if (context.JunimoBlessingEnabled && context.JunimoBlessingEligible)
        {
            return new PassOutPolicyDecision(
                PassOutPolicyAction.ApplyDelta,
                SanityChangeSource.TimeLimitPassOut,
                OrdinaryPassOutSanityDelta,
                0,
                true,
                "passout.policy.time-limit.junimo-blessing-safe"
            );
        }

        if (!context.NightOwlPlusAllowsConflictingSpecialDeathChain)
        {
            return OrdinaryTimeLimit(
                string.IsNullOrWhiteSpace(context.NightOwlPlusCompatibilityReason)
                    ? "passout.compatibility.night-owl-plus.facts-unavailable-special-chain-suppressed"
                    : context.NightOwlPlusCompatibilityReason
            );
        }

        return new PassOutPolicyDecision(
            PassOutPolicyAction.DelegateToTwoAmSpecialDeath,
            SanityChangeSource.SanityDarknessSpecialDeath,
            0,
            0,
            false,
            context.DarknessDamageMode == DarknessDamageMode.NonLethal
                ? "passout.policy.time-limit.delegate-special-nonlethal"
                : "passout.policy.time-limit.delegate-special-default"
        );
    }

    internal static PassOutPolicyDecision ResolveDayEnding(
        PassOutReasonEvidence evidence
    )
    {
        switch (evidence.Reason)
        {
            case PassOutReason.VoluntarySleep:
                var sleep = SanityBehaviorRules.ResolveOrdinarySleep(evidence.TimeOfDay);
                if (!sleep.Success || evidence.TimeOfDay >= 2600)
                {
                    return NoChange(
                        "passout.policy.voluntary-sleep-time-invalid"
                    );
                }
                return new PassOutPolicyDecision(
                    PassOutPolicyAction.ApplyDelta,
                    SanityChangeSource.VoluntarySleep,
                    sleep.Delta,
                    0,
                    false,
                    "passout.policy.voluntary-sleep-restore"
                );
            case PassOutReason.TimeLimitPassOut:
                return OrdinaryTimeLimit("passout.policy.time-limit-minus-20");
            case PassOutReason.ExhaustionPassOut:
                return new PassOutPolicyDecision(
                    PassOutPolicyAction.ApplyDelta,
                    SanityChangeSource.ExhaustionPassOut,
                    OrdinaryPassOutSanityDelta,
                    0,
                    false,
                    "passout.policy.exhaustion-minus-20"
                );
            case PassOutReason.SanityDarknessSpecialDeath:
                return new PassOutPolicyDecision(
                    PassOutPolicyAction.DelegateToTwoAmSpecialDeath,
                    SanityChangeSource.SanityDarknessSpecialDeath,
                    0,
                    0,
                    false,
                    "passout.policy.special-death-owned-by-two-am-flow"
                );
            default:
                return NoChange("passout.policy.reason-unknown-fail-closed");
        }
    }

    internal static PassOutPolicyDecision ResolveHealthDeathRecovery(
        bool wasKillScreen,
        bool isKillScreen,
        int currentHealth,
        int maximumHealth
    )
    {
        // Phoenix completes inside takeDamage and never enters killScreen. Only the engine's
        // final true->false death-screen transition is evidence that real death recovery won.
        if (
            !wasKillScreen
            || isKillScreen
            || currentHealth <= 0
            || maximumHealth <= 0
            || currentHealth > maximumHealth
        )
        {
            return NoChange("passout.policy.health-death-recovery-not-confirmed");
        }
        return new PassOutPolicyDecision(
            PassOutPolicyAction.SetToMaximumFraction,
            SanityChangeSource.HealthDeath,
            0,
            HealthDeathMaximumFraction,
            false,
            "passout.policy.health-death-set-current-maximum-50-percent"
        );
    }

    private static PassOutPolicyDecision OrdinaryTimeLimit(string reason)
    {
        return new PassOutPolicyDecision(
            PassOutPolicyAction.ApplyDelta,
            SanityChangeSource.TimeLimitPassOut,
            OrdinaryPassOutSanityDelta,
            0,
            true,
            reason
        );
    }

    private static PassOutPolicyDecision NoChange(string reason)
    {
        return new PassOutPolicyDecision(
            PassOutPolicyAction.None,
            SanityChangeSource.Unknown,
            0,
            0,
            false,
            reason
        );
    }
}

/// <summary>
/// Bounded per-player evidence ledger. Conflicting entry evidence is retained as Unknown so a
/// later DayEnding cannot silently choose whichever callback happened to run last.
/// </summary>
internal sealed class PassOutReasonLedger
{
    internal const int MaximumEntries = 16;

    private readonly Dictionary<string, LedgerEntry> entries =
        new(StringComparer.Ordinal);

    internal int Count => entries.Count;

    internal PassOutReasonCaptureResult Capture(PassOutReasonEvidence evidence)
    {
        if (!IsValid(evidence))
        {
            return new PassOutReasonCaptureResult(
                PassOutReasonCaptureStatus.Rejected,
                evidence,
                "passout.reason.evidence-invalid"
            );
        }

        if (entries.TryGetValue(evidence.PlayerKey, out var existing))
        {
            if (
                !string.Equals(
                    existing.Evidence.SessionId,
                    evidence.SessionId,
                    StringComparison.Ordinal
                )
                || existing.Evidence.GameDay != evidence.GameDay
            )
            {
                entries[evidence.PlayerKey] = new LedgerEntry(evidence, false);
                return Captured(evidence);
            }

            if (existing.Consumed)
            {
                return new PassOutReasonCaptureResult(
                    PassOutReasonCaptureStatus.Duplicate,
                    existing.Evidence,
                    "passout.reason.evidence-already-consumed"
                );
            }
            if (
                existing.Evidence.Reason == evidence.Reason
                && existing.Evidence.TimeOfDay == evidence.TimeOfDay
                && string.Equals(
                    existing.Evidence.StableReason,
                    evidence.StableReason,
                    StringComparison.Ordinal
                )
            )
            {
                return new PassOutReasonCaptureResult(
                    PassOutReasonCaptureStatus.Duplicate,
                    existing.Evidence,
                    "passout.reason.evidence-duplicate"
                );
            }

            var conflict = existing.Evidence with
            {
                Reason = PassOutReason.Unknown,
                StableReason = "passout.reason.evidence-conflict",
            };
            entries[evidence.PlayerKey] = new LedgerEntry(conflict, false);
            return new PassOutReasonCaptureResult(
                PassOutReasonCaptureStatus.Conflict,
                conflict,
                conflict.StableReason
            );
        }

        if (entries.Count >= MaximumEntries)
        {
            return new PassOutReasonCaptureResult(
                PassOutReasonCaptureStatus.Rejected,
                evidence,
                "passout.reason.evidence-capacity-exceeded"
            );
        }

        entries.Add(evidence.PlayerKey, new LedgerEntry(evidence, false));
        return Captured(evidence);
    }

    internal bool TryConsumeDayEnding(
        string sessionId,
        string playerKey,
        int gameDay,
        out PassOutReasonEvidence evidence,
        out string stableReason
    )
    {
        if (
            !entries.TryGetValue(playerKey, out var entry)
            || entry.Consumed
            || !string.Equals(
                entry.Evidence.SessionId,
                sessionId,
                StringComparison.Ordinal
            )
            || entry.Evidence.GameDay != gameDay
        )
        {
            evidence = default;
            stableReason = "passout.reason.day-ending-evidence-unavailable";
            return false;
        }

        entries[playerKey] = entry with { Consumed = true };
        evidence = entry.Evidence;
        stableReason = "passout.reason.day-ending-evidence-consumed";
        return true;
    }

    internal bool TryGet(string playerKey, out PassOutReasonEvidence evidence)
    {
        if (entries.TryGetValue(playerKey, out var entry))
        {
            evidence = entry.Evidence;
            return true;
        }

        evidence = default;
        return false;
    }

    internal void PruneBeforeDay(string sessionId, int gameDay)
    {
        var remove = new List<string>();
        foreach (var pair in entries)
        {
            if (
                !string.Equals(
                    pair.Value.Evidence.SessionId,
                    sessionId,
                    StringComparison.Ordinal
                )
                || pair.Value.Evidence.GameDay < gameDay
            )
            {
                remove.Add(pair.Key);
            }
        }
        foreach (var playerKey in remove)
            entries.Remove(playerKey);
    }

    internal void ForgetPlayer(string playerKey)
    {
        if (SanityPlayerKey.IsCanonical(playerKey))
            entries.Remove(playerKey);
    }

    internal void Clear()
    {
        entries.Clear();
    }

    private static PassOutReasonCaptureResult Captured(
        PassOutReasonEvidence evidence
    )
    {
        return new PassOutReasonCaptureResult(
            PassOutReasonCaptureStatus.Captured,
            evidence,
            "passout.reason.evidence-captured"
        );
    }

    private static bool IsValid(PassOutReasonEvidence evidence)
    {
        return Guid.TryParseExact(evidence.SessionId, "N", out _)
            && Guid.TryParseExact(evidence.EvidenceId, "N", out _)
            && SanityPlayerKey.IsCanonical(evidence.PlayerKey)
            && evidence.GameDay >= 0
            && evidence.Reason != PassOutReason.HealthDeath
            && !string.IsNullOrWhiteSpace(evidence.StableReason);
    }

    private readonly record struct LedgerEntry(
        PassOutReasonEvidence Evidence,
        bool Consumed
    );
}
