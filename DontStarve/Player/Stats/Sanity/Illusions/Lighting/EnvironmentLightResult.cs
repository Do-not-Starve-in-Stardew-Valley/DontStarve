#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

internal enum EnvironmentLightLevel
{
    Lit,
    Dim,
    PitchBlack,
}

internal enum EnvironmentLightEvidenceStatus
{
    Confirmed,
    Fallback,
}

/// <summary>
/// A read-only classification result. It intentionally has no damage flag, callback, countdown,
/// Sanity mutation, or world mutation surface.
/// </summary>
internal sealed class EnvironmentLightResult
{
    internal EnvironmentLightResult(
        EnvironmentLightLevel level,
        EnvironmentLightEvidenceStatus evidenceStatus,
        string reason,
        long capturedAtMinute,
        bool pitchBlackAuthorized = false
    )
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A stable result reason is required.", nameof(reason));
        if (
            pitchBlackAuthorized
            && (
                level != EnvironmentLightLevel.PitchBlack
                || evidenceStatus != EnvironmentLightEvidenceStatus.Confirmed
            )
        )
        {
            throw new ArgumentException(
                "PitchBlack authorization requires a Confirmed PitchBlack result.",
                nameof(pitchBlackAuthorized)
            );
        }

        Level = level;
        EvidenceStatus = evidenceStatus;
        Reason = reason;
        CapturedAtMinute = capturedAtMinute;
        PitchBlackAuthorized = pitchBlackAuthorized;
    }

    internal EnvironmentLightLevel Level { get; }

    internal EnvironmentLightEvidenceStatus EvidenceStatus { get; }

    internal string Reason { get; }

    internal long CapturedAtMinute { get; }

    /// <summary>
    /// Separate safety capability for future harmful consumers. A caller must never infer this
    /// permission from the enum or evidence status alone.
    /// </summary>
    internal bool PitchBlackAuthorized { get; }

    internal static EnvironmentLightResult Confirmed(
        EnvironmentLightLevel level,
        string reason,
        long capturedAtMinute,
        bool pitchBlackAuthorized = false
    )
    {
        return new EnvironmentLightResult(
            level,
            EnvironmentLightEvidenceStatus.Confirmed,
            reason,
            capturedAtMinute,
            pitchBlackAuthorized
        );
    }

    internal static EnvironmentLightResult Fallback(
        string reason,
        long capturedAtMinute
    )
    {
        // There is no proven final-foot brightness threshold. Every fallback is therefore the
        // harmless middle state; callers cannot reinterpret missing evidence as PitchBlack.
        return new EnvironmentLightResult(
            EnvironmentLightLevel.Dim,
            EnvironmentLightEvidenceStatus.Fallback,
            reason,
            capturedAtMinute,
            pitchBlackAuthorized: false
        );
    }
}

internal static class EnvironmentLightReasonIds
{
    internal const string SnapshotUnavailable =
        "environment-light.snapshot-unavailable";
    internal const string SnapshotInvalid = "environment-light.snapshot-invalid";
    internal const string BaseUnavailable = "environment-light.base-unavailable";
    internal const string BaseInvalid = "environment-light.base-invalid";
    internal const string BaseUnknown = "environment-light.base-source-unknown";
    internal const string LocationLightLevelInvalid =
        "environment-light.location-light-level-invalid";
    internal const string MineDarknessUnavailable =
        "environment-light.mine-darkness-unavailable";
    internal const string MineDarknessInvalid =
        "environment-light.mine-darkness-invalid";
    internal const string CandidateCollectionUnavailable =
        "environment-light.candidates-unavailable";
    internal const string CandidateCollectionInvalid =
        "environment-light.candidates-invalid";
    internal const string CandidateInvalid =
        "environment-light.candidate-invalid";
    internal const string CandidateDrawEligibilityInvalid =
        "environment-light.candidate-draw-eligibility-invalid";
    internal const string LocationEvidenceInvalid =
        "environment-light.location-evidence-invalid";
    internal const string LocationRuleMatched =
        "environment-light.location-rule-matched";
    internal const string LocationRuleUnmatched =
        "environment-light.location-rule-unmatched";
    internal const string LocationRuleUnavailable =
        "environment-light.location-rules-unavailable";
    internal const string LocationRuleAmbiguous =
        "environment-light.location-rule-ambiguous";
    internal const string LocationProfileFallbackOnly =
        "environment-light.location-profile-fallback-only";
    internal const string BaseWhiteConfirmed =
        "environment-light.base-white-confirmed";
    internal const string NightVisionUnavailable =
        "environment-light.night-vision-api-unavailable";
    internal const string NightVisionKnownInactive =
        "environment-light.night-vision-known-inactive";
    internal const string NightVisionInvalid =
        "environment-light.night-vision-invalid";
    internal const string NightVisionActiveConfirmed =
        "environment-light.night-vision-active-confirmed";
    internal const string NightVisionOwnerContextMismatch =
        "environment-light.night-vision-owner-context-mismatch";
    internal const string LocalLightCoverageUnverified =
        "environment-light.local-light-coverage-unverified";
    internal const string LocalLightDrawIneligible =
        "environment-light.local-light-draw-ineligible";
    internal const string PitchBlackThresholdUnverified =
        "environment-light.pitch-black-threshold-unverified";
    internal const string FinalBrightnessUnavailable =
        "environment-light.final-brightness-unavailable";
    internal const string FinalVisibilityUnavailable =
        "environment-light.final-visibility-unavailable";
    internal const string FinalVisibilityInvalid =
        "environment-light.final-visibility-invalid";
    internal const string FinalVisibilityStale =
        "environment-light.final-visibility-stale";
    internal const string FinalVisibilityOwnerContextMismatch =
        "environment-light.final-visibility-owner-context-mismatch";
    internal const string FinalVisibilityLitConfirmed =
        "environment-light.final-visibility-lit-confirmed";
    internal const string FinalVisibilityDimConfirmed =
        "environment-light.final-visibility-dim-confirmed";
    internal const string FinalVisibilityPitchBlackConfirmed =
        "environment-light.final-visibility-pitch-black-confirmed";
    internal const string FinalVisibilityPitchBlackLocationUnsafe =
        "environment-light.final-visibility-pitch-black-location-unsafe";
}
