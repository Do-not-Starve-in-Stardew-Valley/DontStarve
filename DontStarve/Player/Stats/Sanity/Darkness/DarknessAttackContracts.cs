#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;

namespace DontStarve.Player.Stats.Sanity.Darkness;

internal static class DarknessAttackContract
{
    internal const string ContractVersion = "darkness-countdown-v1";
    internal const string WarningCueId = "sanity.cue.darkness.warning";
    internal const int InitialMinimumSeconds = 5;
    internal const int InitialMaximumSeconds = 10;
    internal const int RepeatMinimumSeconds = 5;
    internal const int RepeatMaximumSeconds = 11;
    internal const int MaximumOwnerStates = 16;
    internal const int MaximumRequestIdLength = 128;
}

internal enum DarknessAttackOwnerState
{
    Inactive,
    Countdown,
    Warned,
    ExpiredAwaitingReceipt,
}

internal enum DarknessAttackMutationStatus
{
    Applied,
    NoChange,
    IgnoredDuplicate,
    IgnoredStale,
    RequiresHostAuthority,
    Invalid,
    CapacityExceeded,
    Disposed,
}

internal enum DarknessAttackPromptKind
{
    None,
    EnteredDarkness,
    Warning,
    EscapedDarkness,
}

internal enum DarknessWarningClaimAction
{
    None,
    Activate,
    Release,
}

internal enum DarknessAttackReceiptDisposition
{
    Applied,
    Rejected,
}

internal readonly record struct DarknessAttackOwnerKey(
    string PlayerKey,
    int ScreenId,
    string SessionId
);

/// <summary>
/// The runtime supplies one real-game update duration per observation. Implementations must not
/// substitute wall-clock time because menus and game suspension have different progression rules.
/// </summary>
internal interface IDarknessAttackClock
{
    TimeSpan ElapsedGameTime { get; }
}

internal interface IDarknessAttackRandom
{
    int NextInclusive(int minimum, int maximum);
}

internal interface IDarknessAttackRequestIdSource
{
    string NextRequestId(DarknessAttackOwnerKey key);
}

/// <summary>
/// Consumes only the already-authorized stage-02 light result. It deliberately has no raw light,
/// weather, location, night-vision API, health, Sanity, or damage fields.
/// </summary>
internal readonly record struct DarknessAttackObservation(
    DarknessAttackOwnerKey Key,
    long Revision,
    SanityAuthorityRole AuthorityRole,
    bool GameplaySettleable,
    bool Paused,
    EnvironmentLightLevel LightLevel,
    EnvironmentLightEvidenceStatus EvidenceStatus,
    bool PitchBlackAuthorized,
    string LightReason,
    string UnsettleableReason
)
{
    internal bool IsAuthorizedPitchBlack =>
        GameplaySettleable
        && LightLevel == EnvironmentLightLevel.PitchBlack
        && EvidenceStatus == EnvironmentLightEvidenceStatus.Confirmed
        && PitchBlackAuthorized;
}

internal readonly record struct DarknessAttackExpiryIntent(
    DarknessAttackOwnerKey Key,
    string RequestId,
    long Revision,
    string LightReason,
    string ContractVersion
);

internal readonly record struct DarknessAttackReceipt(
    DarknessAttackOwnerKey Key,
    string RequestId,
    DarknessAttackReceiptDisposition Disposition,
    string Reason
);

internal readonly record struct DarknessAttackUpdateResult(
    DarknessAttackMutationStatus Status,
    string Reason,
    DarknessAttackPromptKind Prompt,
    DarknessWarningClaimAction WarningClaimAction,
    string WarningRequestId,
    DarknessAttackExpiryIntent? ExpiryIntent
)
{
    internal static DarknessAttackUpdateResult NoChange(string reason) =>
        new(
            DarknessAttackMutationStatus.NoChange,
            reason,
            DarknessAttackPromptKind.None,
            DarknessWarningClaimAction.None,
            string.Empty,
            null
        );
}

internal sealed class DarknessAttackStateSnapshot
{
    internal DarknessAttackStateSnapshot(
        DarknessAttackOwnerKey key,
        DarknessAttackOwnerState state,
        long revision,
        EnvironmentLightLevel lightLevel,
        EnvironmentLightEvidenceStatus evidenceStatus,
        string lightReason,
        double remainingSeconds,
        double warningLeadSeconds,
        bool warningClaimActive,
        string requestId,
        int sampledSeconds,
        string rngBranch,
        string cancelReason
    )
    {
        Key = key;
        State = state;
        Revision = revision;
        LightLevel = lightLevel;
        EvidenceStatus = evidenceStatus;
        LightReason = lightReason;
        RemainingSeconds = remainingSeconds;
        WarningLeadSeconds = warningLeadSeconds;
        WarningClaimActive = warningClaimActive;
        RequestId = requestId;
        SampledSeconds = sampledSeconds;
        RngBranch = rngBranch;
        CancelReason = cancelReason;
    }

    internal DarknessAttackOwnerKey Key { get; }

    internal DarknessAttackOwnerState State { get; }

    internal long Revision { get; }

    internal EnvironmentLightLevel LightLevel { get; }

    internal EnvironmentLightEvidenceStatus EvidenceStatus { get; }

    internal string LightReason { get; }

    internal double RemainingSeconds { get; }

    internal double WarningLeadSeconds { get; }

    internal bool WarningClaimActive { get; }

    internal string RequestId { get; }

    internal int SampledSeconds { get; }

    internal string RngBranch { get; }

    internal string CancelReason { get; }

    internal string ContractVersion => DarknessAttackContract.ContractVersion;
}
