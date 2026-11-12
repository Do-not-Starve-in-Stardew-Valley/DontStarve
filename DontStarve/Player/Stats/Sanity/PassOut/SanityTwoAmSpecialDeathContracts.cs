#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Darkness;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum SanityTwoAmSpecialDeathFlowKind
{
    DefaultDeath,
    NonLethalHome,
}

internal enum SanityTwoAmSpecialDeathPhase
{
    PromptEntered,
    PromptWarning,
    PromptResolved,
    AwaitingOutcome,
    Completed,
}

internal enum SanityTwoAmSpecialDeathSignal
{
    AdvancePresentation,
}

internal enum SanityTwoAmSpecialDeathActionKind
{
    BeginSpecialOverlay,
    ShowPromptEntered,
    PlayWarningCue,
    ShowPromptWarning,
    PlayHurtAndAttack,
    PlayDeathSound,
    ShowPromptResolved,
    EndSpecialOverlay,
    BeginHiddenHospitalPassOut,
    BeginNonLethalPassOut,
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
/// The old save key remains readable for one migration boundary, but the new flow has no
/// recovery/mail state to persist. Legacy fields stay deserialization-only and never resume mail.
/// </summary>
internal sealed class SanityTwoAmSpecialDeathSaveData
{
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<SanityTwoAmSpecialDeathPersistedFlow> Flows { get; set; } = new();

    internal const int CurrentSchemaVersion = 2;
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

    public long Revision { get; set; }

    public bool MailQueued { get; set; }

    public string MailId { get; set; } = string.Empty;
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

    public int CurrentHealth { get; set; }

    public int MaximumHealth { get; set; }

    public string Reason { get; set; } = string.Empty;
}

internal sealed class SanityTwoAmSpecialDeathSnapshotRequestMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ScreenId { get; set; }
}

internal sealed class SanityTwoAmSpecialDeathStepMessage
{
    public int ProtocolVersion { get; set; } = SanityTwoAmSpecialDeathContract.ProtocolVersion;
    public string SessionId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string PlayerKey { get; set; } = string.Empty;
    public int ScreenId { get; set; }
    public long AuthorityRevision { get; set; }
    public string Step { get; set; } = string.Empty;
}

internal static class SanityTwoAmSpecialDeathContract
{
    internal const int ProtocolVersion = 2;
    internal const int MaximumFlows = 16;
    internal const string SaveKey = "DontStarve.Sanity.PassOut";
    internal const string MissingDeathCueReason = "passout.two-am.legacy-death-cue";
    internal const string SpecialEventId = "sanity-two-am-special-death";
    internal const string RequestMessageType = "SanityTwoAmPassOutRequest.v2";
    internal const string DecisionMessageType = "SanityTwoAmPassOutDecision.v2";
    internal const string SnapshotRequestMessageType = "SanityTwoAmPassOutSnapshotRequest.v2";
    internal const string SnapshotMessageType = "SanityTwoAmPassOutSnapshot.v2";
    internal const string ActionMessageType = "SanityTwoAmPassOutAction.v2";
    internal const string StepMessageType = "SanityTwoAmPassOutStep.v2";
}
