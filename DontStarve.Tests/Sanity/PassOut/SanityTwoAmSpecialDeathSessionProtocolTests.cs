using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathSessionProtocolTests
{
    private const string SessionA = "11111111111141118111111111111111";
    private const string SessionB = "22222222222242228222222222222222";
    private const string CorrelationA = "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa";
    private const string CorrelationB = "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb";

    [Fact]
    public void HostExactReplayReturnsCachedDecisionWithoutReadmittingGameplay()
    {
        var receipts = new SanityTwoAmSpecialDeathHostRequestReceipts();
        var request = Request();

        var first = receipts.Admit(request, SessionA, "1", 7);
        var decision = Decision();
        Assert.True(receipts.RecordDecision(request, decision));
        decision.Reason = "mutated-after-record";
        var replay = receipts.Admit(request, SessionA, "1", 99);

        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, first.Status);
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Duplicate, replay.Status);
        Assert.Equal("accepted", replay.CachedDecision!.Reason);
        Assert.Equal(1, receipts.Count);
    }

    [Fact]
    public void HostRejectsStaleAuthorityRevisionAndNonceConflicts()
    {
        var receipts = new SanityTwoAmSpecialDeathHostRequestReceipts();
        var staleRevision = receipts.Admit(Request(), SessionA, "1", 8);
        var admitted = receipts.Admit(Request(), SessionA, "1", 7);
        Assert.True(receipts.RecordDecision(Request(), Decision()));
        var sameNonceDifferentCorrelation = Request(correlationId: CorrelationB);
        var conflict = receipts.Admit(sameNonceDifferentCorrelation, SessionA, "1", 7);
        var oldNonce = Request(nonce: 0);
        var staleNonce = receipts.Admit(oldNonce, SessionA, "1", 7);

        Assert.Equal(
            "passout.two-am.request-authority-revision-stale",
            staleRevision.Reason
        );
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, admitted.Status);
        Assert.Equal("passout.two-am.request-nonce-conflict", conflict.Reason);
        Assert.Equal("passout.two-am.request-nonce-invalid", staleNonce.Reason);
    }

    [Fact]
    public void HostReceiptsAreBoundedAndClearAcrossSaveSessions()
    {
        var receipts = new SanityTwoAmSpecialDeathHostRequestReceipts();
        for (var player = 1; player <= SanityTwoAmSpecialDeathContract.MaximumFlows; player++)
        {
            var playerKey = player.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = Request(playerKey: playerKey, correlationId: GuidFor(player));
            Assert.Equal(
                SanityTwoAmSpecialDeathProtocolStatus.Accepted,
                receipts.Admit(request, SessionA, playerKey, 7).Status
            );
        }

        var overflow = Request(playerKey: "99", correlationId: GuidFor(99));
        Assert.Equal(
            "passout.two-am.request-receipt-capacity-exceeded",
            receipts.Admit(overflow, SessionA, "99", 7).Reason
        );

        receipts.Clear();
        var switchedSave = Request(sessionId: SessionB);
        Assert.Equal(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            receipts.Admit(switchedSave, SessionB, "1", 7).Status
        );
    }

    [Fact]
    public void ClientDecisionRequiresExactSessionScreenNonceAndAuthorityRevision()
    {
        var receipts = ClientReceipts();
        var wrongNonce = Decision(requestNonce: 2);
        var rejected = receipts.AcceptDecision(wrongNonce, CorrelationA, 1, 7);
        var accepted = receipts.AcceptDecision(Decision(), CorrelationA, 1, 7);

        Assert.Equal("passout.two-am.decision-invalid", rejected.Reason);
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, accepted.Status);
        Assert.Equal(CorrelationA, receipts.CorrelationId);
    }

    [Fact]
    public void ClientRejectsDuplicateOutOfOrderAndWrongCorrelationActions()
    {
        var receipts = ClientReceipts();
        Assert.Equal(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            receipts.AcceptDecision(Decision(), CorrelationA, 1, 7).Status
        );
        Assert.Equal(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            receipts.AcceptSnapshot(Snapshot(revision: 3)).Status
        );

        var first = receipts.AcceptAction(Action(revision: 3));
        var duplicate = receipts.AcceptAction(Action(revision: 3));
        var stale = receipts.AcceptAction(Action(revision: 2));
        var wrongCorrelation = receipts.AcceptAction(
            Action(revision: 4, correlationId: CorrelationB)
        );

        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, first.Status);
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Duplicate, duplicate.Status);
        Assert.Equal("passout.two-am.action-revision-stale", stale.Reason);
        Assert.Equal("passout.two-am.action-invalid", wrongCorrelation.Reason);
    }

    [Fact]
    public void ClientNeverAcceptsHostOnlySettlementActions()
    {
        var receipts = ClientReceipts();
        _ = receipts.AcceptDecision(Decision(), CorrelationA, 1, 7);

        foreach (var kind in new[]
        {
            SanityTwoAmSpecialDeathActionKind.QueueMail,
            SanityTwoAmSpecialDeathActionKind.BeginOfficialNewDay,
            SanityTwoAmSpecialDeathActionKind.ReduceToFloor,
        })
        {
            Assert.Equal(
                "passout.two-am.action-invalid",
                receipts.AcceptAction(Action(kind: kind)).Reason
            );
        }
    }

    [Fact]
    public void LateJoinRecoverySnapshotProjectsClinicOnceAndRetiresCleanly()
    {
        var receipts = ClientReceipts();
        var snapshot = Snapshot(
            revision: 8,
            phase: SanityTwoAmSpecialDeathPhase.Recovered
        );

        var accepted = receipts.AcceptSnapshot(snapshot);
        var duplicate = receipts.AcceptSnapshot(snapshot);
        var projected = SanityTwoAmSpecialDeathClientProjection.ForPhase(snapshot.Phase);
        var clinic = Action(
            revision: 8,
            kind: SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic
        );
        var firstClinic = receipts.AcceptAction(clinic);
        var duplicateClinic = receipts.AcceptAction(clinic);
        receipts.Retire(CorrelationA);
        var delayedSnapshot = receipts.AcceptSnapshot(snapshot);

        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, accepted.Status);
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Duplicate, duplicate.Status);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic,
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
            },
            projected
        );
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, firstClinic.Status);
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Duplicate, duplicateClinic.Status);
        Assert.Equal("passout.two-am.snapshot-correlation-retired", delayedSnapshot.Reason);
    }

    [Fact]
    public void NewDecisionRetiresPriorCorrelationWithinTheSameHostSession()
    {
        var receipts = ClientReceipts();
        _ = receipts.AcceptDecision(Decision(), CorrelationA, 1, 7);
        var next = Decision(correlationId: CorrelationB, requestNonce: 2);
        var accepted = receipts.AcceptDecision(next, CorrelationB, 2, 7);
        var delayedOldAction = receipts.AcceptAction(Action(correlationId: CorrelationA));

        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, accepted.Status);
        Assert.Equal(CorrelationB, receipts.CorrelationId);
        Assert.Equal("passout.two-am.action-invalid", delayedOldAction.Reason);
    }

    private static SanityTwoAmSpecialDeathClientReceipts ClientReceipts()
    {
        var receipts = new SanityTwoAmSpecialDeathClientReceipts();
        Assert.True(receipts.Reset(SessionA, "1", 0));
        return receipts;
    }

    private static SanityTwoAmSpecialDeathRequestMessage Request(
        string sessionId = SessionA,
        string correlationId = CorrelationA,
        string playerKey = "1",
        long nonce = 1
    )
    {
        return new SanityTwoAmSpecialDeathRequestMessage
        {
            SessionId = sessionId,
            CorrelationId = correlationId,
            PlayerKey = playerKey,
            ScreenId = 0,
            Nonce = nonce,
            ExpectedAuthorityRevision = 7,
        };
    }

    private static SanityTwoAmSpecialDeathDecisionMessage Decision(
        string correlationId = CorrelationA,
        long requestNonce = 1
    )
    {
        return new SanityTwoAmSpecialDeathDecisionMessage
        {
            SessionId = SessionA,
            CorrelationId = correlationId,
            PlayerKey = "1",
            ScreenId = 0,
            RequestNonce = requestNonce,
            AuthorityRevision = 7,
            SpecialFlowActive = true,
            Reason = "accepted",
        };
    }

    private static SanityTwoAmSpecialDeathSnapshotMessage Snapshot(
        long revision,
        SanityTwoAmSpecialDeathPhase phase = SanityTwoAmSpecialDeathPhase.PromptB
    )
    {
        return new SanityTwoAmSpecialDeathSnapshotMessage
        {
            SessionId = SessionA,
            CorrelationId = CorrelationA,
            PlayerKey = "1",
            ScreenId = 0,
            AuthorityRevision = 7,
            Revision = revision,
            Kind = SanityTwoAmSpecialDeathFlowKind.DefaultClinic,
            Phase = phase,
            Reason = "snapshot",
        };
    }

    private static SanityTwoAmSpecialDeathActionMessage Action(
        long revision = 3,
        string correlationId = CorrelationA,
        SanityTwoAmSpecialDeathActionKind kind = SanityTwoAmSpecialDeathActionKind.ShowPromptB
    )
    {
        return new SanityTwoAmSpecialDeathActionMessage
        {
            SessionId = SessionA,
            CorrelationId = correlationId,
            PlayerKey = "1",
            ScreenId = 0,
            AuthorityRevision = 7,
            Revision = revision,
            Kind = kind,
        };
    }

    private static string GuidFor(int value)
    {
        return value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
            + "000040008000000000000000";
    }
}
