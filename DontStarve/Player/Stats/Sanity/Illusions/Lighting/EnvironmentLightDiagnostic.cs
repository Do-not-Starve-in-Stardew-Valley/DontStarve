#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Bounded immutable diagnostic copied from one owner-local snapshot. It keeps raw evidence,
/// location-rule semantics, the final display level, and harmful authorization as separate facts.
/// </summary>
internal sealed class EnvironmentLightDiagnostic
{
    private EnvironmentLightDiagnostic() { }

    internal string PlayerKey { get; private init; } = string.Empty;
    internal int ScreenId { get; private init; }
    internal string LocationNameOrUniqueName { get; private init; } = string.Empty;
    internal long LocationInstanceId { get; private init; }
    internal string LocationRuntimeTypeFullName { get; private init; } = string.Empty;
    internal string LocationContextId { get; private init; } = string.Empty;
    internal string LocationParentBuildingType { get; private init; } = string.Empty;
    internal bool IsLocationOutdoors { get; private init; }
    internal bool IsLocationTemporary { get; private init; }
    internal bool IsEventActive { get; private init; }
    internal bool IsFestivalActive { get; private init; }
    internal EnvironmentLightWorldPoint StandingPixel { get; private init; }
    internal EnvironmentLightBaseSource BaseSource { get; private init; }
    internal EnvironmentLightColor BaseRawColor { get; private init; }
    internal EnvironmentLightCapabilityStatus LocationLightLevelCapabilityStatus { get; private init; }
    internal float RawLocationLightLevel { get; private init; }
    internal bool IsDarkOut { get; private init; }
    internal EnvironmentLightLocationRuleStatus LocationRuleStatus { get; private init; }
    internal int LocationRuleContractVersion { get; private init; }
    internal string LocationRuleId { get; private init; } = string.Empty;
    internal EnvironmentLightLocationLightProfile LocationLightProfile { get; private init; }
    internal bool TwoAmSpecialDeathSafe { get; private init; }
    internal bool HostileShadowSafe { get; private init; }
    internal bool JunimoBlessingEligible { get; private init; }
    internal string TwoAmSpecialDeathReason { get; private init; } = string.Empty;
    internal string LocationRuleReason { get; private init; } = string.Empty;
    internal int CurrentLightSourceCount { get; private init; }
    internal int SharedLightSourceCount { get; private init; }
    internal int UniqueCandidateCount { get; private init; }
    internal string NearestCandidateId { get; private init; } = string.Empty;
    internal EnvironmentLightWorldPoint? NearestCandidatePosition { get; private init; }
    internal double? NearestCandidateDistanceWorldPixels { get; private init; }
    internal float? NearestCandidateRawRadius { get; private init; }
    internal EnvironmentLightColor? NearestCandidateRawTint { get; private init; }
    internal string NearestCandidateOnlyLocation { get; private init; } = string.Empty;
    internal EnvironmentLightCandidateOrigin NearestCandidateOrigin { get; private init; }
    internal EnvironmentLightCandidateContext NearestCandidateLightContext { get; private init; }
    internal long? NearestCandidateAttachedPlayerId { get; private init; }
    internal EnvironmentLightCapabilityStatus NearestCandidateDrawEligibilityCapabilityStatus { get; private init; }
    internal bool? IsNearestCandidateDrawEligible { get; private init; }
    internal string NearestCandidateDrawEligibilityReason { get; private init; } = string.Empty;
    internal EnvironmentLightCapabilityStatus MineDarkAreaCapabilityStatus { get; private init; }
    internal bool? IsMineDarkArea { get; private init; }
    internal EnvironmentLightCapabilityStatus NightVisionCapabilityStatus { get; private init; }
    internal bool? IsNightVisionActive { get; private init; }
    internal EnvironmentLightCapabilityStatus FinalVisibilityCapabilityStatus { get; private init; }
    internal double? FinalVisibilityScore { get; private init; }
    internal EnvironmentLightColor? FinalVisibilityDarknessColor { get; private init; }
    internal bool StandardLightingDrawn { get; private init; }
    internal bool RainOverlayApplied { get; private init; }
    internal int LightingQuality { get; private init; }
    internal double? ZoomLevel { get; private init; }
    internal bool UseUnscaledLighting { get; private init; }
    internal long FinalVisibilityCapturedAtTick { get; private init; }
    internal long RendererRevision { get; private init; }
    internal string EvaluatorRevision { get; private init; } = string.Empty;
    internal string FinalVisibilityReason { get; private init; } = string.Empty;
    internal EnvironmentLightLevel Level { get; private init; }
    internal EnvironmentLightEvidenceStatus EvidenceStatus { get; private init; }
    internal bool PitchBlackAuthorized { get; private init; }
    internal string Reason { get; private init; } = string.Empty;
    internal long CapturedAtMinute { get; private init; }
    internal long CapturedAtTick { get; private init; }
    internal long Revision { get; private init; }

    internal static EnvironmentLightDiagnostic From(
        EnvironmentLightSnapshot snapshot,
        EnvironmentLightResult result
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);

        EnvironmentLightCandidateSnapshot? nearest = null;
        foreach (var candidate in snapshot.Candidates)
        {
            if (
                candidate is null
                || !double.IsFinite(candidate.DistanceWorldPixels)
                || candidate.DistanceWorldPixels < 0d
            )
            {
                continue;
            }
            if (
                nearest is null
                || candidate.DistanceWorldPixels < nearest.DistanceWorldPixels
                || (
                    candidate.DistanceWorldPixels == nearest.DistanceWorldPixels
                    && string.CompareOrdinal(candidate.Id, nearest.Id) < 0
                )
            )
            {
                nearest = candidate;
            }
        }

        return new EnvironmentLightDiagnostic
        {
            PlayerKey = snapshot.PlayerKey,
            ScreenId = snapshot.ScreenId,
            LocationNameOrUniqueName = snapshot.LocationNameOrUniqueName,
            LocationInstanceId = snapshot.LocationInstanceId,
            LocationRuntimeTypeFullName = snapshot.Location.RuntimeTypeFullName,
            LocationContextId = snapshot.Location.ContextId,
            LocationParentBuildingType = snapshot.Location.ParentBuildingType,
            IsLocationOutdoors = snapshot.Location.IsOutdoors,
            IsLocationTemporary = snapshot.Location.IsTemporary,
            IsEventActive = snapshot.Location.IsEventActive,
            IsFestivalActive = snapshot.Location.IsFestivalActive,
            StandingPixel = snapshot.StandingPixel,
            BaseSource = snapshot.BaseSource,
            BaseRawColor = snapshot.BaseRawColor,
            LocationLightLevelCapabilityStatus = snapshot.LocationLightLevelCapabilityStatus,
            RawLocationLightLevel = snapshot.RawLocationLightLevel,
            IsDarkOut = snapshot.IsDarkOut,
            LocationRuleStatus = snapshot.LocationRule.Status,
            LocationRuleContractVersion = snapshot.LocationRule.ContractVersion,
            LocationRuleId = snapshot.LocationRule.RuleId,
            LocationLightProfile = snapshot.LocationRule.LightProfile,
            TwoAmSpecialDeathSafe = snapshot.LocationRule.TwoAmSpecialDeathSafe,
            HostileShadowSafe = snapshot.LocationRule.HostileShadowSafe,
            JunimoBlessingEligible = snapshot.LocationRule.JunimoBlessingEligible,
            TwoAmSpecialDeathReason = snapshot.LocationRule.TwoAmSpecialDeathReason,
            LocationRuleReason = snapshot.LocationRule.Reason,
            CurrentLightSourceCount = snapshot.CurrentLightSourceCount,
            SharedLightSourceCount = snapshot.SharedLightSourceCount,
            UniqueCandidateCount = snapshot.UniqueCandidateCount,
            NearestCandidateId = nearest?.Id ?? string.Empty,
            NearestCandidatePosition = nearest?.Position,
            NearestCandidateDistanceWorldPixels = nearest?.DistanceWorldPixels,
            NearestCandidateRawRadius = nearest?.RawRadius,
            NearestCandidateRawTint = nearest?.RawTint,
            NearestCandidateOnlyLocation = nearest?.OnlyLocation ?? string.Empty,
            NearestCandidateOrigin = nearest?.Origin ?? EnvironmentLightCandidateOrigin.Unknown,
            NearestCandidateLightContext = nearest?.LightContext ?? EnvironmentLightCandidateContext.Unknown,
            NearestCandidateAttachedPlayerId = nearest?.AttachedPlayerId,
            NearestCandidateDrawEligibilityCapabilityStatus = nearest?.DrawEligibilityCapabilityStatus
                ?? EnvironmentLightCapabilityStatus.Unavailable,
            IsNearestCandidateDrawEligible = nearest?.IsDrawEligible,
            NearestCandidateDrawEligibilityReason = nearest?.DrawEligibilityReason ?? string.Empty,
            MineDarkAreaCapabilityStatus = snapshot.MineDarkAreaCapabilityStatus,
            IsMineDarkArea = snapshot.IsMineDarkArea,
            NightVisionCapabilityStatus = snapshot.NightVision.CapabilityStatus,
            IsNightVisionActive = snapshot.NightVision.IsActive,
            FinalVisibilityCapabilityStatus =
                snapshot.FinalVisibility.CapabilityStatus,
            FinalVisibilityScore = snapshot.FinalVisibility.IsConfirmed
                ? snapshot.FinalVisibility.VisibilityScore
                : null,
            FinalVisibilityDarknessColor = snapshot.FinalVisibility.IsConfirmed
                ? new EnvironmentLightColor(
                    ToByte(snapshot.FinalVisibility.DarknessRed),
                    ToByte(snapshot.FinalVisibility.DarknessGreen),
                    ToByte(snapshot.FinalVisibility.DarknessBlue),
                    byte.MaxValue
                )
                : null,
            StandardLightingDrawn =
                snapshot.FinalVisibility.StandardLightingDrawn,
            RainOverlayApplied = snapshot.FinalVisibility.RainOverlayApplied,
            LightingQuality = snapshot.FinalVisibility.LightingQuality,
            ZoomLevel = double.IsFinite(snapshot.FinalVisibility.ZoomLevel)
                ? snapshot.FinalVisibility.ZoomLevel
                : null,
            UseUnscaledLighting =
                snapshot.FinalVisibility.UseUnscaledLighting,
            FinalVisibilityCapturedAtTick =
                snapshot.FinalVisibility.CapturedAtTick,
            RendererRevision = snapshot.FinalVisibility.RendererRevision,
            EvaluatorRevision = snapshot.FinalVisibility.EvaluatorRevision,
            FinalVisibilityReason = snapshot.FinalVisibility.Reason,
            Level = result.Level,
            EvidenceStatus = result.EvidenceStatus,
            PitchBlackAuthorized = result.PitchBlackAuthorized,
            Reason = result.Reason,
            CapturedAtMinute = result.CapturedAtMinute,
            CapturedAtTick = snapshot.CapturedAtTick,
            Revision = snapshot.Revision,
        };
    }

    internal static EnvironmentLightDiagnostic CaptureFailure(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        long capturedAtMinute,
        long capturedAtTick,
        string reason
    )
    {
        return new EnvironmentLightDiagnostic
        {
            PlayerKey = playerKey,
            ScreenId = screenId,
            LocationNameOrUniqueName = locationNameOrUniqueName,
            LocationInstanceId = locationInstanceId,
            BaseSource = EnvironmentLightBaseSource.Unknown,
            LocationLightLevelCapabilityStatus = EnvironmentLightCapabilityStatus.Unavailable,
            LocationRuleStatus = EnvironmentLightLocationRuleStatus.Unavailable,
            LocationRuleId = EnvironmentLightLocationRuleIds.Unavailable,
            LocationLightProfile = EnvironmentLightLocationLightProfile.FallbackOnly,
            TwoAmSpecialDeathReason =
                TwoAmSpecialDeathLocationReasonIds.RulesUnavailableDefaultUnsafe,
            LocationRuleReason = reason,
            NearestCandidateOrigin = EnvironmentLightCandidateOrigin.Unknown,
            NearestCandidateLightContext = EnvironmentLightCandidateContext.Unknown,
            NearestCandidateDrawEligibilityCapabilityStatus = EnvironmentLightCapabilityStatus.Unavailable,
            MineDarkAreaCapabilityStatus = EnvironmentLightCapabilityStatus.Unavailable,
            NightVisionCapabilityStatus = EnvironmentLightCapabilityStatus.Unavailable,
            FinalVisibilityCapabilityStatus =
                EnvironmentLightCapabilityStatus.Unavailable,
            FinalVisibilityReason =
                EnvironmentLightReasonIds.FinalVisibilityUnavailable,
            Level = EnvironmentLightLevel.Dim,
            EvidenceStatus = EnvironmentLightEvidenceStatus.Fallback,
            PitchBlackAuthorized = false,
            Reason = reason,
            CapturedAtMinute = capturedAtMinute,
            CapturedAtTick = capturedAtTick,
        };
    }

    private static byte ToByte(double normalized)
    {
        return (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(normalized, 0d, 1d) * byte.MaxValue),
            0,
            byte.MaxValue
        );
    }
}
