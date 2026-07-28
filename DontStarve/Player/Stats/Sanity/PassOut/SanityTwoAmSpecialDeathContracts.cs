#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Darkness;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum SanityTwoAmSpecialDeathFlowKind
{
    DefaultClinic,
    NonLethalHome,
}

internal enum SanityTwoAmSpecialDeathPhase
{
    PromptA,
    WarningCue,
    PromptB,
    PromptC,
    AwaitingDayEnding,
    AwaitingSaving,
    AwaitingRecovery,
    Recovered,
    NonLethalHomePending,
    Completed,
}

internal enum SanityTwoAmSpecialDeathSignal
{
    AdvancePresentation,
    MailQueued,
    NonLethalSettled,
    DayEnding,
    Saving,
    DayStarted,
    LaterSaving,
}

internal enum SanityTwoAmSpecialDeathActionKind
{
    BeginSpecialOverlay,
    EndSpecialOverlay,
    ShowPromptA,
    PlayWarningCue,
    StopWarningCue,
    ShowPromptB,
    ShowPromptC,
    ReportMissingDeathCue,
    QueueMail,
    BeginOfficialNewDay,
    RecoverAtHarveyClinic,
    ReduceToFloor,
}

internal enum SanityTwoAmSpecialDeathMutationStatus
{
    Applied,
    Duplicate,
    NoChange,
    Rejected,
}

internal readonly record struct SanityTwoAmSpecialDeathStartRequest(
    string SessionId,
    string CorrelationId,
    string PlayerKey,
    int ScreenId,
    SanityAuthorityRole Authority,
    long AuthorityRevision,
    DarknessDamageMode Mode,
    bool IsLocationSafe,
    string LocationReason,
    int CurrentHealth,
    int MaximumHealth
);

internal readonly record struct SanityTwoAmSpecialDeathAction(
    SanityTwoAmSpecialDeathActionKind Kind,
    string PlayerKey,
    int ScreenId,
    string CorrelationId
);

internal sealed record SanityTwoAmSpecialDeathSnapshot(
    string SessionId,
    string CorrelationId,
    string PlayerKey,
    int ScreenId,
    SanityAuthorityRole Authority,
    long AuthorityRevision,
    SanityTwoAmSpecialDeathFlowKind Kind,
    SanityTwoAmSpecialDeathPhase Phase,
    int CurrentHealth,
    int MaximumHealth,
    bool MailQueued,
    string MailId,
    long Revision,
    string Reason
);

internal sealed class SanityTwoAmSpecialDeathMutation
{
    internal SanityTwoAmSpecialDeathMutation(
        SanityTwoAmSpecialDeathMutationStatus status,
        string reason,
        SanityTwoAmSpecialDeathSnapshot? snapshot,
        params SanityTwoAmSpecialDeathAction[] actions
    )
    {
        Status = status;
        Reason = reason;
        Snapshot = snapshot;
        Actions = Array.AsReadOnly(actions ?? Array.Empty<SanityTwoAmSpecialDeathAction>());
    }

    internal SanityTwoAmSpecialDeathMutationStatus Status { get; }

    internal string Reason { get; }

    internal SanityTwoAmSpecialDeathSnapshot? Snapshot { get; }

    internal IReadOnlyList<SanityTwoAmSpecialDeathAction> Actions { get; }
}

/// <summary>
/// Only recovery-relevant values cross the save boundary. Presentation callbacks and physical
/// audio instances remain session state and are reconstructed from Phase after loading.
/// </summary>
internal sealed class SanityTwoAmSpecialDeathSaveData
{
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<SanityTwoAmSpecialDeathPersistedFlow> Flows { get; set; } = new();

    internal const int CurrentSchemaVersion = 1;
}

internal sealed class SanityTwoAmSpecialDeathPersistedFlow
{
    public string SessionId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }

    public long AuthorityRevision { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public bool MailQueued { get; set; }

    public string MailId { get; set; } = string.Empty;

    public long Revision { get; set; }
}

internal sealed class SanityTwoAmSpecialDeathRequestMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }

    public long Nonce { get; set; }

    public long ExpectedAuthorityRevision { get; set; }
}

internal sealed class SanityTwoAmSpecialDeathDecisionMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }

    public long RequestNonce { get; set; }

    public long AuthorityRevision { get; set; }

    public bool RunOriginalPassOut { get; set; }

    public bool SpecialFlowActive { get; set; }

    public string Reason { get; set; } = string.Empty;
}

internal sealed class SanityTwoAmSpecialDeathActionMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }

    public long AuthorityRevision { get; set; }

    public long Revision { get; set; }

    public SanityTwoAmSpecialDeathActionKind Kind { get; set; }
}

internal sealed class SanityTwoAmSpecialDeathSnapshotMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }

    public long AuthorityRevision { get; set; }

    public long Revision { get; set; }

    public SanityTwoAmSpecialDeathFlowKind Kind { get; set; }

    public SanityTwoAmSpecialDeathPhase Phase { get; set; }

    public bool MailQueued { get; set; }

    public string Reason { get; set; } = string.Empty;
}

internal sealed class SanityTwoAmSpecialDeathSnapshotRequestMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }
}

internal static class SanityTwoAmSpecialDeathContract
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumFlows = 16;
    internal const string SaveKey = "DontStarve.Sanity.PassOut";
    internal const string MailId = "Yurin.DontStarve_SanityDarknessSpecialDeath";
    internal const string SpecialEventId = "sanity-two-am-special-death";
    internal const string MissingDeathCueReason =
        "passout.sanity-darkness-special-death.death-cue-placeholder-missing";
    internal const string RequestMessageType = "SanityTwoAmPassOutRequest.v1";
    internal const string DecisionMessageType = "SanityTwoAmPassOutDecision.v1";
    internal const string SnapshotRequestMessageType = "SanityTwoAmPassOutSnapshotRequest.v1";
    internal const string SnapshotMessageType = "SanityTwoAmPassOutSnapshot.v1";
    internal const string ActionMessageType = "SanityTwoAmPassOutAction.v1";
}
