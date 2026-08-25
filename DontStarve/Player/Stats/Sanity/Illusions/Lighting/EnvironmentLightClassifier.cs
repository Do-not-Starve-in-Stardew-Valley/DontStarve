#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

internal sealed class EnvironmentLightClassifier
{
    private readonly DarknessAttackLocationAuthorizationPolicy
        darknessAttackLocationAuthorization;
    private readonly EnvironmentLightThresholds thresholds;

    internal EnvironmentLightClassifier(
        DarknessAttackLocationAuthorizationPolicy darknessAttackLocationAuthorization,
        EnvironmentLightThresholds? thresholds = null
    )
    {
        this.darknessAttackLocationAuthorization = darknessAttackLocationAuthorization
            ?? throw new ArgumentNullException(nameof(darknessAttackLocationAuthorization));
        this.thresholds = thresholds ?? EnvironmentLightThresholds.Default;
    }

    internal EnvironmentLightResult Classify(
        EnvironmentLightSnapshot snapshot,
        EnvironmentLightLevel? previousLevel = null
    )
    {
        if (snapshot is null)
        {
            return EnvironmentLightResult.Fallback(
                EnvironmentLightReasonIds.SnapshotUnavailable,
                0
            );
        }
        if (
            !SanityPlayerKey.IsCanonical(snapshot.PlayerKey)
            || snapshot.ScreenId < 0
            || string.IsNullOrWhiteSpace(snapshot.LocationNameOrUniqueName)
            || snapshot.Location is null
            || snapshot.LocationRule is null
            || !snapshot.StandingPixel.IsFinite
            || !string.Equals(
                snapshot.LocationNameOrUniqueName,
                snapshot.Location.InternalName,
                StringComparison.Ordinal
            )
        )
        {
            return Fallback(snapshot, EnvironmentLightReasonIds.SnapshotInvalid);
        }
        if (
            string.IsNullOrWhiteSpace(snapshot.Location.RuntimeTypeFullName)
            || string.IsNullOrWhiteSpace(snapshot.Location.ContextId)
            || string.IsNullOrWhiteSpace(snapshot.Location.InternalName)
        )
        {
            return Fallback(snapshot, EnvironmentLightReasonIds.LocationEvidenceInvalid);
        }

        var finalVisibility = snapshot.FinalVisibility;
        var hasConfirmedFinalVisibility = HasConfirmedFinalVisibility(
            snapshot,
            finalVisibility
        );
        if (
            finalVisibility.CapabilityStatus
                == EnvironmentLightCapabilityStatus.Available
            && !hasConfirmedFinalVisibility
        )
        {
            return Fallback(
                snapshot,
                MatchesOwnerContext(snapshot, finalVisibility)
                    ? EnvironmentLightReasonIds.FinalVisibilityInvalid
                    : EnvironmentLightReasonIds.FinalVisibilityOwnerContextMismatch
            );
        }
        if (
            finalVisibility.CapabilityStatus
            == EnvironmentLightCapabilityStatus.Invalid
        )
        {
            return Fallback(
                snapshot,
                StableOrDefault(
                    finalVisibility.Reason,
                    EnvironmentLightReasonIds.FinalVisibilityInvalid
                )
            );
        }

        // A confirmed final-lightmap sample is stronger than raw base/candidate metadata. The raw
        // path remains for diagnostics and safe fallback when no final sample exists.
        if (!hasConfirmedFinalVisibility)
        {
            if (
                snapshot.BaseCapabilityStatus
                == EnvironmentLightCapabilityStatus.Unavailable
            )
            {
                return Fallback(snapshot, EnvironmentLightReasonIds.BaseUnavailable);
            }
            if (
                snapshot.BaseCapabilityStatus
                == EnvironmentLightCapabilityStatus.Invalid
            )
            {
                return Fallback(snapshot, EnvironmentLightReasonIds.BaseInvalid);
            }
            if (snapshot.BaseSource == EnvironmentLightBaseSource.Unknown)
                return Fallback(snapshot, EnvironmentLightReasonIds.BaseUnknown);
            if (
                snapshot.LocationLightLevelCapabilityStatus
                    != EnvironmentLightCapabilityStatus.Available
                || !float.IsFinite(snapshot.RawLocationLightLevel)
            )
            {
                return Fallback(
                    snapshot,
                    EnvironmentLightReasonIds.LocationLightLevelInvalid
                );
            }

            if (snapshot.BaseSource == EnvironmentLightBaseSource.MineLighting)
            {
                if (
                    snapshot.MineDarkAreaCapabilityStatus
                        == EnvironmentLightCapabilityStatus.Unavailable
                    || !snapshot.IsMineDarkArea.HasValue
                )
                {
                    return Fallback(
                        snapshot,
                        EnvironmentLightReasonIds.MineDarknessUnavailable
                    );
                }
                if (
                    snapshot.MineDarkAreaCapabilityStatus
                    == EnvironmentLightCapabilityStatus.Invalid
                )
                {
                    return Fallback(
                        snapshot,
                        EnvironmentLightReasonIds.MineDarknessInvalid
                    );
                }
            }

            if (
                snapshot.CandidateCollectionCapabilityStatus
                == EnvironmentLightCapabilityStatus.Unavailable
            )
            {
                return Fallback(
                    snapshot,
                    StableOrDefault(
                        snapshot.CandidateCollectionReason,
                        EnvironmentLightReasonIds.CandidateCollectionUnavailable
                    )
                );
            }
            if (
                snapshot.CandidateCollectionCapabilityStatus
                == EnvironmentLightCapabilityStatus.Invalid
            )
            {
                return Fallback(
                    snapshot,
                    StableOrDefault(
                        snapshot.CandidateCollectionReason,
                        EnvironmentLightReasonIds.CandidateCollectionInvalid
                    )
                );
            }
            foreach (var candidate in snapshot.Candidates)
            {
                if (
                    candidate is null
                    || candidate.CapabilityStatus
                        != EnvironmentLightCapabilityStatus.Available
                    || string.IsNullOrWhiteSpace(candidate.Id)
                    || !candidate.Position.IsFinite
                    || !double.IsFinite(candidate.DistanceWorldPixels)
                    || candidate.DistanceWorldPixels < 0d
                    || !float.IsFinite(candidate.RawRadius)
                    || candidate.RawRadius < 0f
                    || candidate.Origin == EnvironmentLightCandidateOrigin.Unknown
                    || candidate.LightContext == EnvironmentLightCandidateContext.Unknown
                    || candidate.AttachedPlayerId < 0
                )
                {
                    return Fallback(
                        snapshot,
                        candidate is null
                            ? EnvironmentLightReasonIds.CandidateInvalid
                            : StableOrDefault(
                                candidate.Reason,
                                EnvironmentLightReasonIds.CandidateInvalid
                            )
                    );
                }
                if (
                    candidate.DrawEligibilityCapabilityStatus
                        != EnvironmentLightCapabilityStatus.Available
                    || !candidate.IsDrawEligible.HasValue
                )
                {
                    return Fallback(
                        snapshot,
                        StableOrDefault(
                            candidate.DrawEligibilityReason,
                            EnvironmentLightReasonIds.CandidateDrawEligibilityInvalid
                        )
                    );
                }
            }
        }

        var locationRule = snapshot.LocationRule;
        if (
            locationRule.Status is not (
                EnvironmentLightLocationRuleStatus.Matched
                or EnvironmentLightLocationRuleStatus.Unmatched
            )
        )
        {
            var fallback = locationRule.Status switch
            {
                EnvironmentLightLocationRuleStatus.Unmatched =>
                    EnvironmentLightReasonIds.LocationRuleUnmatched,
                EnvironmentLightLocationRuleStatus.Ambiguous =>
                    EnvironmentLightReasonIds.LocationRuleAmbiguous,
                _ => EnvironmentLightReasonIds.LocationRuleUnavailable,
            };
            return Fallback(snapshot, StableOrDefault(locationRule.Reason, fallback));
        }
        if (
            locationRule.ContractVersion <= 0
            || string.IsNullOrWhiteSpace(locationRule.RuleId)
            || string.IsNullOrWhiteSpace(locationRule.Reason)
        )
        {
            return Fallback(snapshot, EnvironmentLightReasonIds.LocationEvidenceInvalid);
        }

        var nightVision = snapshot.NightVision;
        if (nightVision.CapabilityStatus == EnvironmentLightCapabilityStatus.Invalid)
        {
            return Fallback(
                snapshot,
                StableOrDefault(
                    nightVision.Reason,
                    EnvironmentLightReasonIds.NightVisionInvalid
                )
            );
        }
        if (nightVision.CapabilityStatus == EnvironmentLightCapabilityStatus.Available)
        {
            if (!nightVision.IsActive.HasValue)
            {
                return Fallback(
                    snapshot,
                    EnvironmentLightReasonIds.NightVisionInvalid
                );
            }
            if (!MatchesOwnerContext(snapshot, nightVision))
            {
                return Fallback(
                    snapshot,
                    EnvironmentLightReasonIds.NightVisionOwnerContextMismatch
                );
            }
            if (nightVision.IsActive.Value)
            {
                return EnvironmentLightResult.Confirmed(
                    EnvironmentLightLevel.Lit,
                    EnvironmentLightReasonIds.NightVisionActiveConfirmed,
                    snapshot.CapturedAtMinute,
                    pitchBlackAuthorized: false
                );
            }
        }

        if (hasConfirmedFinalVisibility)
        {
            if (
                nightVision.CapabilityStatus
                != EnvironmentLightCapabilityStatus.Available
            )
            {
                // An explicitly configured compatibility provider which cannot report its state
                // may hide post-lightmap vision, so harmful classification remains fail-closed.
                return Fallback(
                    snapshot,
                    StableOrDefault(
                        nightVision.Reason,
                        EnvironmentLightReasonIds.NightVisionUnavailable
                    )
                );
            }

            var level = EnvironmentLightVisibilityMath.Classify(
                finalVisibility.VisibilityScore,
                thresholds,
                previousLevel
            );
            var canAuthorize =
                level == EnvironmentLightLevel.PitchBlack
                && darknessAttackLocationAuthorization.Allows(locationRule)
                && EnvironmentLightVisibilityMath.CanAuthorizePitchBlack(
                    finalVisibility.VisibilityScore,
                    thresholds
                );
            var reason = level switch
            {
                EnvironmentLightLevel.Lit =>
                    EnvironmentLightReasonIds.FinalVisibilityLitConfirmed,
                EnvironmentLightLevel.Dim =>
                    EnvironmentLightReasonIds.FinalVisibilityDimConfirmed,
                _ when canAuthorize =>
                    EnvironmentLightReasonIds.FinalVisibilityPitchBlackConfirmed,
                _ => EnvironmentLightReasonIds.FinalVisibilityPitchBlackAuthorizationDenied,
            };
            return EnvironmentLightResult.Confirmed(
                level,
                reason,
                snapshot.CapturedAtMinute,
                canAuthorize
            );
        }

        // Opaque white is only Confirmed for a versioned profile which explicitly preserves the
        // proven DrawLighting branch. Mine/Volcano/event/unknown profiles remain fallback-only.
        if (
            snapshot.BaseRawColor.IsOpaqueWhite
            && locationRule.LightProfile
                == EnvironmentLightLocationLightProfile.OpaqueWhiteBase
        )
        {
            return EnvironmentLightResult.Confirmed(
                EnvironmentLightLevel.Lit,
                EnvironmentLightReasonIds.BaseWhiteConfirmed,
                snapshot.CapturedAtMinute,
                pitchBlackAuthorized: false
            );
        }

        var hasDrawEligibleCandidate = false;
        foreach (var candidate in snapshot.Candidates)
        {
            if (candidate.IsDrawEligible == true)
            {
                hasDrawEligibleCandidate = true;
                break;
            }
        }
        if (hasDrawEligibleCandidate)
        {
            // Draw eligibility proves only that Stardew would draw the light into this screen's
            // lightmap. Raw texture scale still isn't standing-pixel coverage.
            return Fallback(
                snapshot,
                EnvironmentLightReasonIds.LocalLightCoverageUnverified
            );
        }
        if (snapshot.UniqueCandidateCount > 0)
        {
            return Fallback(
                snapshot,
                EnvironmentLightReasonIds.LocalLightDrawIneligible
            );
        }

        if (
            locationRule.LightProfile
            == EnvironmentLightLocationLightProfile.FallbackOnly
        )
        {
            if (
                snapshot.BaseSource == EnvironmentLightBaseSource.MineLighting
                && snapshot.IsMineDarkArea == true
            )
            {
                return Fallback(
                    snapshot,
                    EnvironmentLightReasonIds.PitchBlackThresholdUnverified
                );
            }
            return Fallback(
                snapshot,
                EnvironmentLightReasonIds.LocationProfileFallbackOnly
            );
        }

        if (
            nightVision.CapabilityStatus
                == EnvironmentLightCapabilityStatus.Unavailable
            || !nightVision.IsActive.HasValue
        )
        {
            return Fallback(
                snapshot,
                StableOrDefault(
                    nightVision.Reason,
                    EnvironmentLightReasonIds.NightVisionUnavailable
                )
            );
        }

        // isDarkOut, raw LightLevel, tint, and distance are deliberately not PitchBlack rules.
        // No current production combination has the paired real-machine evidence required to set
        // PitchBlackAuthorized=true.
        return Fallback(
            snapshot,
            EnvironmentLightReasonIds.FinalBrightnessUnavailable
        );
    }

    private static bool MatchesOwnerContext(
        EnvironmentLightSnapshot snapshot,
        EnvironmentLightNightVisionSnapshot nightVision
    )
    {
        return string.Equals(
                snapshot.PlayerKey,
                nightVision.PlayerKey,
                StringComparison.Ordinal
            )
            && snapshot.ScreenId == nightVision.ScreenId
            && string.Equals(
                snapshot.LocationNameOrUniqueName,
                nightVision.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
            && snapshot.LocationInstanceId == nightVision.LocationInstanceId;
    }

    private static bool HasConfirmedFinalVisibility(
        EnvironmentLightSnapshot snapshot,
        EnvironmentLightFinalVisibilitySnapshot finalVisibility
    )
    {
        return finalVisibility is not null
            && finalVisibility.IsConfirmed
            && MatchesOwnerContext(snapshot, finalVisibility)
            && finalVisibility.CapturedAtTick <= snapshot.CapturedAtTick
            && snapshot.CapturedAtTick - finalVisibility.CapturedAtTick
                <= EnvironmentLightProductionContract.MaximumSampleAgeTicks;
    }

    private static bool MatchesOwnerContext(
        EnvironmentLightSnapshot snapshot,
        EnvironmentLightFinalVisibilitySnapshot finalVisibility
    )
    {
        return string.Equals(
                snapshot.PlayerKey,
                finalVisibility.PlayerKey,
                StringComparison.Ordinal
            )
            && snapshot.ScreenId == finalVisibility.ScreenId
            && string.Equals(
                snapshot.LocationNameOrUniqueName,
                finalVisibility.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
            && snapshot.LocationInstanceId == finalVisibility.LocationInstanceId;
    }

    private static EnvironmentLightResult Fallback(
        EnvironmentLightSnapshot snapshot,
        string reason
    )
    {
        return EnvironmentLightResult.Fallback(reason, snapshot.CapturedAtMinute);
    }

    private static string StableOrDefault(string? reason, string fallback)
    {
        return string.IsNullOrWhiteSpace(reason) ? fallback : reason;
    }
}
