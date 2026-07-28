#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

internal enum EnvironmentLightCapabilityStatus
{
    Available,
    Unavailable,
    Invalid,
}

internal enum EnvironmentLightBaseSource
{
    Ambient,
    Outdoor,
    MineLighting,
    Unknown,
}

internal enum EnvironmentLightCandidateOrigin
{
    Unknown,
    SharedLocation,
    MapLight,
    WindowLight,
    CurrentOnly,
}

internal enum EnvironmentLightCandidateContext
{
    Unknown,
    None,
    MapLight,
    WindowLight,
}

internal readonly record struct EnvironmentLightWorldPoint(double X, double Y)
{
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

internal readonly record struct EnvironmentLightColor(
    byte R,
    byte G,
    byte B,
    byte A
)
{
    internal bool IsOpaqueWhite =>
        R == byte.MaxValue
        && G == byte.MaxValue
        && B == byte.MaxValue
        && A == byte.MaxValue;

    internal string ToDiagnosticString()
    {
        return $"{R},{G},{B},{A}";
    }
}

internal sealed record EnvironmentLightCandidateSnapshot(
    string Id,
    EnvironmentLightCapabilityStatus CapabilityStatus,
    EnvironmentLightWorldPoint Position,
    double DistanceWorldPixels,
    float RawRadius,
    EnvironmentLightColor RawTint,
    string OnlyLocation,
    string Reason,
    EnvironmentLightCandidateOrigin Origin = EnvironmentLightCandidateOrigin.Unknown,
    EnvironmentLightCandidateContext LightContext = EnvironmentLightCandidateContext.Unknown,
    long AttachedPlayerId = 0,
    EnvironmentLightCapabilityStatus DrawEligibilityCapabilityStatus =
        EnvironmentLightCapabilityStatus.Unavailable,
    bool? IsDrawEligible = null,
    string DrawEligibilityReason = "environment-light.candidate-draw-eligibility-unavailable"
);

internal sealed record EnvironmentLightNightVisionSnapshot(
    EnvironmentLightCapabilityStatus CapabilityStatus,
    bool? IsActive,
    string Reason,
    string PlayerKey = "",
    int ScreenId = -1,
    string LocationNameOrUniqueName = "",
    long LocationInstanceId = 0
);

/// <summary>
/// Owner-local light evidence captured at one cadence boundary. This type deliberately stores no
/// Farmer, GameLocation, LightSource, texture, or other world-owned reference.
/// </summary>
internal sealed class EnvironmentLightSnapshot
{
    private readonly IReadOnlyList<EnvironmentLightCandidateSnapshot> candidates;

    internal EnvironmentLightSnapshot(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        EnvironmentLightLocationSnapshot location,
        EnvironmentLightLocationRuleResolution locationRule,
        EnvironmentLightWorldPoint standingPixel,
        EnvironmentLightCapabilityStatus baseCapabilityStatus,
        EnvironmentLightBaseSource baseSource,
        EnvironmentLightColor baseRawColor,
        EnvironmentLightCapabilityStatus locationLightLevelCapabilityStatus,
        float rawLocationLightLevel,
        bool isDarkOut,
        EnvironmentLightCapabilityStatus mineDarkAreaCapabilityStatus,
        bool? isMineDarkArea,
        EnvironmentLightNightVisionSnapshot nightVision,
        EnvironmentLightCapabilityStatus candidateCollectionCapabilityStatus,
        string candidateCollectionReason,
        int currentLightSourceCount,
        int sharedLightSourceCount,
        IEnumerable<EnvironmentLightCandidateSnapshot> candidates,
        long capturedAtMinute,
        long capturedAtTick,
        long revision,
        EnvironmentLightFinalVisibilitySnapshot? finalVisibility = null
    )
    {
        if (string.IsNullOrWhiteSpace(playerKey))
            throw new ArgumentException("A canonical player key is required.", nameof(playerKey));
        if (screenId < 0)
            throw new ArgumentOutOfRangeException(nameof(screenId));
        if (string.IsNullOrWhiteSpace(locationNameOrUniqueName))
        {
            throw new ArgumentException(
                "A location identity snapshot is required.",
                nameof(locationNameOrUniqueName)
            );
        }
        if (!standingPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(standingPixel));
        if (location is null)
            throw new ArgumentNullException(nameof(location));
        if (locationRule is null)
            throw new ArgumentNullException(nameof(locationRule));
        if (
            !string.Equals(
                locationNameOrUniqueName,
                location.InternalName,
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "The location evidence must match the snapshot identity.",
                nameof(location)
            );
        }
        if (nightVision is null)
            throw new ArgumentNullException(nameof(nightVision));
        if (string.IsNullOrWhiteSpace(candidateCollectionReason))
        {
            throw new ArgumentException(
                "A stable candidate capability reason is required.",
                nameof(candidateCollectionReason)
            );
        }
        if (currentLightSourceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(currentLightSourceCount));
        if (sharedLightSourceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sharedLightSourceCount));
        ArgumentNullException.ThrowIfNull(candidates);

        PlayerKey = playerKey;
        ScreenId = screenId;
        LocationNameOrUniqueName = locationNameOrUniqueName;
        LocationInstanceId = locationInstanceId;
        Location = location;
        LocationRule = locationRule;
        StandingPixel = standingPixel;
        BaseCapabilityStatus = baseCapabilityStatus;
        BaseSource = baseSource;
        BaseRawColor = baseRawColor;
        LocationLightLevelCapabilityStatus =
            locationLightLevelCapabilityStatus;
        RawLocationLightLevel = rawLocationLightLevel;
        IsDarkOut = isDarkOut;
        MineDarkAreaCapabilityStatus = mineDarkAreaCapabilityStatus;
        IsMineDarkArea = isMineDarkArea;
        NightVision = nightVision;
        CandidateCollectionCapabilityStatus =
            candidateCollectionCapabilityStatus;
        CandidateCollectionReason = candidateCollectionReason;
        CurrentLightSourceCount = currentLightSourceCount;
        SharedLightSourceCount = sharedLightSourceCount;
        this.candidates = Array.AsReadOnly(
            new List<EnvironmentLightCandidateSnapshot>(candidates).ToArray()
        );
        CapturedAtMinute = capturedAtMinute;
        CapturedAtTick = capturedAtTick;
        Revision = revision;
        FinalVisibility =
            finalVisibility
            ?? EnvironmentLightFinalVisibilitySnapshot.Unavailable(
                playerKey,
                screenId,
                locationNameOrUniqueName,
                locationInstanceId,
                Math.Max(0L, capturedAtTick),
                EnvironmentLightReasonIds.FinalVisibilityUnavailable
            );
    }

    internal string PlayerKey { get; }

    internal int ScreenId { get; }

    internal string LocationNameOrUniqueName { get; }

    internal long LocationInstanceId { get; }

    internal EnvironmentLightLocationSnapshot Location { get; }

    internal EnvironmentLightLocationRuleResolution LocationRule { get; }

    internal EnvironmentLightWorldPoint StandingPixel { get; }

    internal EnvironmentLightCapabilityStatus BaseCapabilityStatus { get; }

    internal EnvironmentLightBaseSource BaseSource { get; }

    internal EnvironmentLightColor BaseRawColor { get; }

    internal EnvironmentLightCapabilityStatus LocationLightLevelCapabilityStatus { get; }

    internal float RawLocationLightLevel { get; }

    internal bool IsDarkOut { get; }

    internal EnvironmentLightCapabilityStatus MineDarkAreaCapabilityStatus { get; }

    internal bool? IsMineDarkArea { get; }

    internal EnvironmentLightNightVisionSnapshot NightVision { get; }

    internal EnvironmentLightCapabilityStatus CandidateCollectionCapabilityStatus { get; }

    internal string CandidateCollectionReason { get; }

    internal int CurrentLightSourceCount { get; }

    internal int SharedLightSourceCount { get; }

    internal IReadOnlyList<EnvironmentLightCandidateSnapshot> Candidates => candidates;

    internal int UniqueCandidateCount => candidates.Count;

    internal long CapturedAtMinute { get; }

    internal long CapturedAtTick { get; }

    internal long Revision { get; }

    internal EnvironmentLightFinalVisibilitySnapshot FinalVisibility { get; }
}

internal sealed class EnvironmentLightSnapshotCaptureResult
{
    private EnvironmentLightSnapshotCaptureResult(
        EnvironmentLightCapabilityStatus capabilityStatus,
        EnvironmentLightSnapshot? snapshot,
        string reason
    )
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A stable capture reason is required.", nameof(reason));

        CapabilityStatus = capabilityStatus;
        Snapshot = snapshot;
        Reason = reason;
    }

    internal EnvironmentLightCapabilityStatus CapabilityStatus { get; }

    internal EnvironmentLightSnapshot? Snapshot { get; }

    internal string Reason { get; }

    internal bool Success =>
        CapabilityStatus == EnvironmentLightCapabilityStatus.Available
        && Snapshot is not null;

    internal static EnvironmentLightSnapshotCaptureResult Captured(
        EnvironmentLightSnapshot snapshot
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new EnvironmentLightSnapshotCaptureResult(
            EnvironmentLightCapabilityStatus.Available,
            snapshot,
            "environment-light.snapshot-captured"
        );
    }

    internal static EnvironmentLightSnapshotCaptureResult Failed(
        EnvironmentLightCapabilityStatus capabilityStatus,
        string reason
    )
    {
        if (capabilityStatus == EnvironmentLightCapabilityStatus.Available)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilityStatus),
                "A failed capture cannot report Available."
            );
        }
        return new EnvironmentLightSnapshotCaptureResult(
            capabilityStatus,
            null,
            reason
        );
    }
}
