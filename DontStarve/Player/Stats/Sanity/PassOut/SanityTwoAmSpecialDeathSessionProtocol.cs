#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Damage;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum SanityTwoAmSpecialDeathProtocolStatus
{
    Accepted,
    Duplicate,
    Rejected,
}

internal sealed record SanityTwoAmSpecialDeathProtocolResult(
    SanityTwoAmSpecialDeathProtocolStatus Status,
    string Reason,
    SanityTwoAmSpecialDeathDecisionMessage? CachedDecision = null
);

/// <summary>
/// Bounded host-side transport receipts. This is not a second gameplay authority: the lifecycle
/// session, base Sanity revision and special-death state machine remain the decision sources.
/// Receipts only make an exact request replay return its original decision and reject stale or
/// conflicting nonces before they can reach gameplay settlement.
/// </summary>
internal sealed class SanityTwoAmSpecialDeathHostRequestReceipts
{
    private readonly Dictionary<string, Receipt> receipts =
        new(StringComparer.Ordinal);

    internal int Count => receipts.Count;

    internal SanityTwoAmSpecialDeathProtocolResult Admit(
        SanityTwoAmSpecialDeathRequestMessage? request,
        string expectedSessionId,
        string expectedPlayerKey,
        long currentAuthorityRevision
    )
    {
        var invalid = ValidateRequest(
            request,
            expectedSessionId,
            expectedPlayerKey
        );
        if (invalid is not null)
            return Rejected(invalid);

        var valid = request!;
        if (receipts.TryGetValue(valid.PlayerKey, out var existing))
        {
            if (valid.Nonce < existing.Nonce)
                return Rejected("passout.two-am.request-nonce-stale");
            if (valid.Nonce == existing.Nonce)
            {
                if (!existing.Matches(valid))
                    return Rejected("passout.two-am.request-nonce-conflict");
                return existing.Decision is null
                    ? Rejected("passout.two-am.request-decision-pending")
                    : new SanityTwoAmSpecialDeathProtocolResult(
                        SanityTwoAmSpecialDeathProtocolStatus.Duplicate,
                        "passout.two-am.request-duplicate",
                        Clone(existing.Decision)
                    );
            }
        }
        else if (receipts.Count >= SanityTwoAmSpecialDeathContract.MaximumFlows)
        {
            return Rejected("passout.two-am.request-receipt-capacity-exceeded");
        }

        if (
            currentAuthorityRevision < 0
            || valid.ExpectedAuthorityRevision != currentAuthorityRevision
        )
        {
            return Rejected("passout.two-am.request-authority-revision-stale");
        }

        receipts[valid.PlayerKey] = new Receipt(valid);
        return Accepted("passout.two-am.request-admitted");
    }

    internal bool RecordDecision(
        SanityTwoAmSpecialDeathRequestMessage request,
        SanityTwoAmSpecialDeathDecisionMessage decision
    )
    {
        if (
            !receipts.TryGetValue(request.PlayerKey, out var receipt)
            || !receipt.Matches(request)
            || !DecisionMatchesRequest(decision, request)
        )
        {
            return false;
        }

        receipt.Decision = Clone(decision);
        return true;
    }

    internal void ForgetPlayer(string playerKey)
    {
        if (SanityPlayerKey.IsCanonical(playerKey))
            receipts.Remove(playerKey);
    }

    internal void Clear()
    {
        receipts.Clear();
    }

    private static string? ValidateRequest(
        SanityTwoAmSpecialDeathRequestMessage? request,
        string expectedSessionId,
        string expectedPlayerKey
    )
    {
        if (request is null)
            return "passout.two-am.request-missing";
        if (request.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion)
            return "passout.two-am.protocol-version-unsupported";
        if (
            !SanityProtocol.IsValidSessionId(expectedSessionId)
            || !string.Equals(request.SessionId, expectedSessionId, StringComparison.Ordinal)
        )
        {
            return "passout.two-am.request-session-mismatch";
        }
        if (
            !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(request.PlayerKey, expectedPlayerKey, StringComparison.Ordinal)
        )
        {
            return "passout.two-am.request-player-mismatch";
        }
        if (request.ScreenId < 0)
            return "passout.two-am.request-screen-invalid";
        if (!NonLethalDamageContract.IsValidCorrelationId(request.CorrelationId))
            return "passout.two-am.request-correlation-invalid";
        if (request.Nonce <= 0)
            return "passout.two-am.request-nonce-invalid";
        if (request.ExpectedAuthorityRevision < 0)
            return "passout.two-am.request-authority-revision-invalid";
        return null;
    }

    private static bool DecisionMatchesRequest(
        SanityTwoAmSpecialDeathDecisionMessage decision,
        SanityTwoAmSpecialDeathRequestMessage request
    )
    {
        return decision.ProtocolVersion == SanityTwoAmSpecialDeathContract.ProtocolVersion
            && string.Equals(decision.SessionId, request.SessionId, StringComparison.Ordinal)
            && string.Equals(decision.CorrelationId, request.CorrelationId, StringComparison.Ordinal)
            && string.Equals(decision.PlayerKey, request.PlayerKey, StringComparison.Ordinal)
            && decision.ScreenId == request.ScreenId
            && decision.RequestNonce == request.Nonce
            && decision.AuthorityRevision == request.ExpectedAuthorityRevision
            && decision.RunOriginalPassOut != decision.SpecialFlowActive;
    }

    private static SanityTwoAmSpecialDeathDecisionMessage Clone(
        SanityTwoAmSpecialDeathDecisionMessage decision
    )
    {
        return new SanityTwoAmSpecialDeathDecisionMessage
        {
            ProtocolVersion = decision.ProtocolVersion,
            SessionId = decision.SessionId,
            CorrelationId = decision.CorrelationId,
            PlayerKey = decision.PlayerKey,
            ScreenId = decision.ScreenId,
            RequestNonce = decision.RequestNonce,
            AuthorityRevision = decision.AuthorityRevision,
            RunOriginalPassOut = decision.RunOriginalPassOut,
            SpecialFlowActive = decision.SpecialFlowActive,
            Reason = decision.Reason,
        };
    }

    private static SanityTwoAmSpecialDeathProtocolResult Accepted(string reason)
    {
        return new SanityTwoAmSpecialDeathProtocolResult(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            reason
        );
    }

    private static SanityTwoAmSpecialDeathProtocolResult Rejected(string reason)
    {
        return new SanityTwoAmSpecialDeathProtocolResult(
            SanityTwoAmSpecialDeathProtocolStatus.Rejected,
            reason
        );
    }

    private sealed class Receipt
    {
        internal Receipt(SanityTwoAmSpecialDeathRequestMessage request)
        {
            SessionId = request.SessionId;
            CorrelationId = request.CorrelationId;
            PlayerKey = request.PlayerKey;
            ScreenId = request.ScreenId;
            Nonce = request.Nonce;
            AuthorityRevision = request.ExpectedAuthorityRevision;
        }

        internal string SessionId { get; }

        internal string CorrelationId { get; }

        internal string PlayerKey { get; }

        internal int ScreenId { get; }

        internal long Nonce { get; }

        internal long AuthorityRevision { get; }

        internal SanityTwoAmSpecialDeathDecisionMessage? Decision { get; set; }

        internal bool Matches(SanityTwoAmSpecialDeathRequestMessage request)
        {
            return request.ProtocolVersion == SanityTwoAmSpecialDeathContract.ProtocolVersion
                && string.Equals(request.SessionId, SessionId, StringComparison.Ordinal)
                && string.Equals(request.CorrelationId, CorrelationId, StringComparison.Ordinal)
                && string.Equals(request.PlayerKey, PlayerKey, StringComparison.Ordinal)
                && request.ScreenId == ScreenId
                && request.Nonce == Nonce
                && request.ExpectedAuthorityRevision == AuthorityRevision;
        }
    }
}

/// <summary>
/// Owner-local client receipts accept only the host's current correlation and monotonic flow
/// revision. Retired correlations remain bounded tombstones for the session so delayed snapshot
/// or action messages cannot resurrect presentation or replay clinic recovery.
/// </summary>
internal sealed class SanityTwoAmSpecialDeathClientReceipts
{
    private readonly HashSet<string> actionKindsAtRevision =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> retiredCorrelations =
        new(StringComparer.Ordinal);
    private string sessionId = string.Empty;
    private string playerKey = string.Empty;
    private int screenId = -1;
    private string correlationId = string.Empty;
    private long authorityRevision = -1;
    private long highestFlowRevision;

    internal string CorrelationId => correlationId;

    internal long HighestFlowRevision => highestFlowRevision;

    internal bool MatchesContext(
        string currentSessionId,
        string currentPlayerKey,
        int currentScreenId
    )
    {
        return MatchesOwner(currentSessionId, currentPlayerKey, currentScreenId);
    }

    internal bool Reset(string currentSessionId, string currentPlayerKey, int currentScreenId)
    {
        Clear();
        if (
            !SanityProtocol.IsValidSessionId(currentSessionId)
            || !SanityPlayerKey.IsCanonical(currentPlayerKey)
            || currentScreenId < 0
        )
        {
            return false;
        }
        sessionId = currentSessionId;
        playerKey = currentPlayerKey;
        screenId = currentScreenId;
        return true;
    }

    internal SanityTwoAmSpecialDeathProtocolResult AcceptDecision(
        SanityTwoAmSpecialDeathDecisionMessage? decision,
        string pendingCorrelationId,
        long pendingNonce,
        long pendingAuthorityRevision
    )
    {
        if (
            decision is null
            || decision.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !MatchesOwner(decision.SessionId, decision.PlayerKey, decision.ScreenId)
            || !string.Equals(decision.CorrelationId, pendingCorrelationId, StringComparison.Ordinal)
            || decision.RequestNonce != pendingNonce
            || decision.AuthorityRevision != pendingAuthorityRevision
            || decision.RunOriginalPassOut == decision.SpecialFlowActive
        )
        {
            return Rejected("passout.two-am.decision-invalid");
        }

        if (decision.SpecialFlowActive)
        {
            if (!BeginFlow(decision.CorrelationId, decision.AuthorityRevision))
                return Rejected("passout.two-am.decision-correlation-retired");
        }
        return Accepted("passout.two-am.decision-accepted");
    }

    internal SanityTwoAmSpecialDeathProtocolResult AcceptSnapshot(
        SanityTwoAmSpecialDeathSnapshotMessage? snapshot
    )
    {
        if (
            snapshot is null
            || snapshot.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !MatchesOwner(snapshot.SessionId, snapshot.PlayerKey, snapshot.ScreenId)
            || !NonLethalDamageContract.IsValidCorrelationId(snapshot.CorrelationId)
            || snapshot.AuthorityRevision < 0
            || snapshot.Revision <= 0
            || snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic
            || !Enum.IsDefined(typeof(SanityTwoAmSpecialDeathPhase), snapshot.Phase)
        )
        {
            return Rejected("passout.two-am.snapshot-invalid");
        }
        if (retiredCorrelations.Contains(snapshot.CorrelationId))
            return Rejected("passout.two-am.snapshot-correlation-retired");
        if (
            !string.IsNullOrEmpty(correlationId)
            && !string.Equals(correlationId, snapshot.CorrelationId, StringComparison.Ordinal)
        )
        {
            return Rejected("passout.two-am.snapshot-correlation-conflict");
        }
        if (string.IsNullOrEmpty(correlationId))
        {
            if (!BeginFlow(snapshot.CorrelationId, snapshot.AuthorityRevision))
                return Rejected("passout.two-am.snapshot-correlation-retired");
        }
        else if (snapshot.AuthorityRevision != authorityRevision)
        {
            return Rejected("passout.two-am.snapshot-authority-revision-mismatch");
        }
        if (snapshot.Revision < highestFlowRevision)
            return Rejected("passout.two-am.snapshot-revision-stale");
        if (snapshot.Revision == highestFlowRevision)
        {
            return new SanityTwoAmSpecialDeathProtocolResult(
                SanityTwoAmSpecialDeathProtocolStatus.Duplicate,
                "passout.two-am.snapshot-duplicate"
            );
        }

        highestFlowRevision = snapshot.Revision;
        actionKindsAtRevision.Clear();
        return Accepted("passout.two-am.snapshot-accepted");
    }

    internal SanityTwoAmSpecialDeathProtocolResult AcceptAction(
        SanityTwoAmSpecialDeathActionMessage? action
    )
    {
        if (
            action is null
            || action.ProtocolVersion != SanityTwoAmSpecialDeathContract.ProtocolVersion
            || !MatchesOwner(action.SessionId, action.PlayerKey, action.ScreenId)
            || !string.Equals(action.CorrelationId, correlationId, StringComparison.Ordinal)
            || action.AuthorityRevision != authorityRevision
            || action.Revision <= 0
            || !IsClientPresentationAction(action.Kind)
        )
        {
            return Rejected("passout.two-am.action-invalid");
        }
        if (action.Revision < highestFlowRevision)
            return Rejected("passout.two-am.action-revision-stale");
        if (action.Revision > highestFlowRevision)
        {
            highestFlowRevision = action.Revision;
            actionKindsAtRevision.Clear();
        }

        var actionKey = ((int)action.Kind).ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        if (!actionKindsAtRevision.Add(actionKey))
        {
            return new SanityTwoAmSpecialDeathProtocolResult(
                SanityTwoAmSpecialDeathProtocolStatus.Duplicate,
                "passout.two-am.action-duplicate"
            );
        }
        return Accepted("passout.two-am.action-accepted");
    }

    internal void Retire(string completedCorrelationId)
    {
        if (
            string.IsNullOrEmpty(correlationId)
            || !string.Equals(correlationId, completedCorrelationId, StringComparison.Ordinal)
        )
        {
            return;
        }
        if (retiredCorrelations.Count < SanityTwoAmSpecialDeathContract.MaximumFlows)
            retiredCorrelations.Add(correlationId);
        correlationId = string.Empty;
        authorityRevision = -1;
        highestFlowRevision = 0;
        actionKindsAtRevision.Clear();
    }

    internal void Clear()
    {
        sessionId = string.Empty;
        playerKey = string.Empty;
        screenId = -1;
        correlationId = string.Empty;
        authorityRevision = -1;
        highestFlowRevision = 0;
        actionKindsAtRevision.Clear();
        retiredCorrelations.Clear();
    }

    private bool BeginFlow(string nextCorrelationId, long nextAuthorityRevision)
    {
        if (
            !NonLethalDamageContract.IsValidCorrelationId(nextCorrelationId)
            || nextAuthorityRevision < 0
            || retiredCorrelations.Contains(nextCorrelationId)
        )
        {
            return false;
        }
        if (
            !string.IsNullOrEmpty(correlationId)
            && !string.Equals(correlationId, nextCorrelationId, StringComparison.Ordinal)
        )
        {
            if (retiredCorrelations.Count < SanityTwoAmSpecialDeathContract.MaximumFlows)
                retiredCorrelations.Add(correlationId);
        }
        correlationId = nextCorrelationId;
        authorityRevision = nextAuthorityRevision;
        highestFlowRevision = 0;
        actionKindsAtRevision.Clear();
        return true;
    }

    private bool MatchesOwner(string candidateSessionId, string candidatePlayerKey, int candidateScreenId)
    {
        return string.Equals(candidateSessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(candidatePlayerKey, playerKey, StringComparison.Ordinal)
            && candidateScreenId == screenId;
    }

    private static bool IsClientPresentationAction(
        SanityTwoAmSpecialDeathActionKind kind
    )
    {
        return kind
            is SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay
                or SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay
                or SanityTwoAmSpecialDeathActionKind.ShowPromptA
                or SanityTwoAmSpecialDeathActionKind.PlayWarningCue
                or SanityTwoAmSpecialDeathActionKind.StopWarningCue
                or SanityTwoAmSpecialDeathActionKind.ShowPromptB
                or SanityTwoAmSpecialDeathActionKind.ShowPromptC
                or SanityTwoAmSpecialDeathActionKind.ReportMissingDeathCue
                or SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic;
    }

    private static SanityTwoAmSpecialDeathProtocolResult Accepted(string reason)
    {
        return new SanityTwoAmSpecialDeathProtocolResult(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            reason
        );
    }

    private static SanityTwoAmSpecialDeathProtocolResult Rejected(string reason)
    {
        return new SanityTwoAmSpecialDeathProtocolResult(
            SanityTwoAmSpecialDeathProtocolStatus.Rejected,
            reason
        );
    }
}

internal static class SanityTwoAmSpecialDeathClientProjection
{
    internal static IReadOnlyList<SanityTwoAmSpecialDeathActionKind> ForPhase(
        SanityTwoAmSpecialDeathPhase phase
    )
    {
        return phase switch
        {
            SanityTwoAmSpecialDeathPhase.PromptA => new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.ShowPromptA,
            },
            SanityTwoAmSpecialDeathPhase.WarningCue => new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.PlayWarningCue,
            },
            SanityTwoAmSpecialDeathPhase.PromptB => new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptB,
            },
            SanityTwoAmSpecialDeathPhase.PromptC => new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptC,
            },
            SanityTwoAmSpecialDeathPhase.AwaitingDayEnding
                or SanityTwoAmSpecialDeathPhase.AwaitingSaving
                or SanityTwoAmSpecialDeathPhase.AwaitingRecovery => new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
            },
            SanityTwoAmSpecialDeathPhase.Recovered => new[]
            {
                SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic,
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
            },
            SanityTwoAmSpecialDeathPhase.Completed => new[]
            {
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
            },
            _ => Array.Empty<SanityTwoAmSpecialDeathActionKind>(),
        };
    }
}
