#nullable enable

using System;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepLifecycleTests
{
    [Fact]
    public void BeginRequestRequiresMatchingSessionOwnerOnlineAndFreshNonce()
    {
        var session = Guid.NewGuid().ToString("N");
        var request = new SleepBeginRequest
        {
            SessionId = session,
            PlayerKey = "42",
            BedId = "FarmHouse:10:10",
            AttemptId = "attempt-1",
            Nonce = 2,
            ExpectedRevision = 3,
        };

        var accepted = SleepLifecycleProtocol.ValidateBeginRequest(
            request,
            senderPlayerId: 42,
            currentSessionId: session,
            playerIsOnline: true,
            highestSeenNonce: 1,
            currentRevision: 3
        );
        Assert.True(accepted.Accepted);

        var wrongOwner = SleepLifecycleProtocol.ValidateBeginRequest(
            request,
            senderPlayerId: 7,
            currentSessionId: session,
            playerIsOnline: true,
            highestSeenNonce: 1,
            currentRevision: 3
        );
        Assert.False(wrongOwner.Accepted);
        Assert.Equal("sleep-request-sender-does-not-own-player", wrongOwner.Reason);

        var replay = SleepLifecycleProtocol.ValidateBeginRequest(
            request,
            senderPlayerId: 42,
            currentSessionId: session,
            playerIsOnline: true,
            highestSeenNonce: 2,
            currentRevision: 3
        );
        Assert.False(replay.Accepted);
        Assert.Equal("sleep-request-nonce-is-replayed-or-invalid", replay.Reason);
    }

    [Fact]
    public void ConfirmationTokenIsOneShotAndCancellationInvalidatesIt()
    {
        var session = Guid.NewGuid().ToString("N");
        var state = new SleepLifecycleState();
        Assert.True(state.BeginHostSession(session, out _));

        Assert.True(state.IssueConfirmationToken("42", session, "attempt-1", out _));
        Assert.True(state.ConsumeConfirmationToken("42", session, "attempt-1", out _));
        Assert.False(state.ConsumeConfirmationToken("42", session, "attempt-1", out _));

        Assert.True(state.IssueConfirmationToken("42", session, "attempt-2", out _));
        Assert.True(state.CancelAttempt("42", session, "attempt-2", out _));
        Assert.False(state.ConsumeConfirmationToken("42", session, "attempt-2", out _));
    }

    [Fact]
    public void ClientSnapshotRejectsRevisionGapAndRecoversWithFullSnapshot()
    {
        var session = Guid.NewGuid().ToString("N");
        var host = new SleepLifecycleState();
        Assert.True(host.BeginHostSession(session, out _));
        Assert.True(host.UpsertHostSnapshot(ActiveSnapshot(session, 1), out var first, out _));

        var client = new SleepLifecycleState();
        Assert.True(client.BeginClientSession(session, "1", out _));
        Assert.Equal(
            SleepSnapshotApplyStatus.AppliedFull,
            client.ApplyClientSnapshot(host.CreateFullSnapshot()).Status
        );

        var inactive = ActiveSnapshot(session, 1);
        inactive.Active = false;
        Assert.True(host.UpsertHostSnapshot(inactive, out _, out _));
        var activeAgain = ActiveSnapshot(session, 1);
        Assert.True(host.UpsertHostSnapshot(activeAgain, out var third, out _));

        var gap = client.ApplyClientSnapshot(host.CreateDeltaSnapshot(third));
        Assert.Equal(SleepSnapshotApplyStatus.NeedsFullSnapshot, gap.Status);
        Assert.Equal("sleep-delta-revision-has-a-gap", gap.Reason);

        var recovered = client.ApplyClientSnapshot(host.CreateFullSnapshot());
        Assert.Equal(SleepSnapshotApplyStatus.AppliedFull, recovered.Status);
        Assert.True(client.TryGetSnapshot("1", out var current));
        Assert.Equal(third.Revision, current.Revision);
    }

    private static SleepPlayerSnapshot ActiveSnapshot(string session, long ignoredRevision)
    {
        return new SleepPlayerSnapshot
        {
            PlayerKey = "1",
            SessionId = session,
            Active = true,
            Confirmed = true,
            Mode = SleepMode.Voluntary,
            Phase = SleepPhase.Realtime,
            SettlementId = "settlement-1",
            BedId = "FarmHouse:10:10",
            CurrentMinute = 600,
            TargetMinute = 2040,
            Revision = ignoredRevision,
        };
    }

}
