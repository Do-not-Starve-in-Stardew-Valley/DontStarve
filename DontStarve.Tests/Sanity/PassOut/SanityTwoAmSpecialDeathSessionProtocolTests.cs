using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathSessionProtocolTests
{
    private const string Session = "11111111111141118111111111111111";

    [Fact]
    public void HostReceiptAcceptsExactRequestAndReturnsDuplicateDecision()
    {
        var receipts = new SanityTwoAmSpecialDeathHostRequestReceipts();
        var request = new SanityTwoAmSpecialDeathRequestMessage
        {
            SessionId = Session,
            CorrelationId = "22222222222242228222222222222222",
            PlayerKey = "1",
            ScreenId = 0,
            Nonce = 1,
            ExpectedAuthorityRevision = 7,
        };

        var accepted = receipts.Admit(request, Session, "1", 7);
        var decision = new SanityTwoAmSpecialDeathDecisionMessage
        {
            SessionId = Session,
            CorrelationId = request.CorrelationId,
            PlayerKey = request.PlayerKey,
            ScreenId = request.ScreenId,
            RequestNonce = request.Nonce,
            AuthorityRevision = request.ExpectedAuthorityRevision,
            RunOriginalPassOut = false,
            SpecialFlowActive = true,
            Reason = "started",
        };

        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Accepted, accepted.Status);
        Assert.True(receipts.RecordDecision(request, decision));

        var duplicate = receipts.Admit(request, Session, "1", 7);
        Assert.Equal(SanityTwoAmSpecialDeathProtocolStatus.Duplicate, duplicate.Status);
        Assert.Equal(decision.CorrelationId, duplicate.CachedDecision!.CorrelationId);
    }

    [Fact]
    public void ClientAcceptsOnlyNewOwnerLocalPresentationActions()
    {
        var receipts = new SanityTwoAmSpecialDeathClientReceipts();
        Assert.True(receipts.Reset(Session, "1", 0));

        var decision = new SanityTwoAmSpecialDeathDecisionMessage
        {
            SessionId = Session,
            CorrelationId = "22222222222242228222222222222222",
            PlayerKey = "1",
            ScreenId = 0,
            RequestNonce = 1,
            AuthorityRevision = 7,
            RunOriginalPassOut = false,
            SpecialFlowActive = true,
        };
        Assert.Equal(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            receipts.AcceptDecision(decision, decision.CorrelationId, 1, 7).Status
        );

        var entered = Action(SanityTwoAmSpecialDeathActionKind.ShowPromptEntered, 1);
        var old = Action((SanityTwoAmSpecialDeathActionKind)99, 2);

        Assert.Equal(
            SanityTwoAmSpecialDeathProtocolStatus.Accepted,
            receipts.AcceptAction(entered).Status
        );
        Assert.Equal(
            SanityTwoAmSpecialDeathProtocolStatus.Rejected,
            receipts.AcceptAction(old).Status
        );
    }

    [Fact]
    public void ClientProjectionMatchesFrozenTimelineForEachPhase()
    {
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.ShowPromptEntered,
            },
            SanityTwoAmSpecialDeathClientProjection.ForPhase(
                SanityTwoAmSpecialDeathPhase.PromptEntered
            )
        );
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.PlayWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptWarning,
            },
            SanityTwoAmSpecialDeathClientProjection.ForPhase(
                SanityTwoAmSpecialDeathPhase.PromptWarning
            )
        );
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack,
                SanityTwoAmSpecialDeathActionKind.PlayDeathSound,
                SanityTwoAmSpecialDeathActionKind.ShowPromptResolved,
            },
            SanityTwoAmSpecialDeathClientProjection.ForPhase(
                SanityTwoAmSpecialDeathPhase.PromptResolved
            )
        );
    }

    private static SanityTwoAmSpecialDeathActionMessage Action(
        SanityTwoAmSpecialDeathActionKind kind,
        long revision
    ) => new()
    {
        SessionId = Session,
        CorrelationId = "22222222222242228222222222222222",
        PlayerKey = "1",
        ScreenId = 0,
        AuthorityRevision = 7,
        Revision = revision,
        Kind = kind,
    };
}
