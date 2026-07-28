using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityRequestValidatorTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSession = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string MasterKey = "123456789";
    private const string FarmhandKey = "223456789";
    private const string ForgedKey = "323456789";
    private const long FarmhandId = 223456789L;

    [Fact]
    public void Valid_food_request_uses_host_recomputed_delta_and_increments_revision()
    {
        var service = Host();
        var changedEvents = 0;
        service.HostStateChanged += _ => changedEvents++;

        var result = service.HandleHostRequest(
            Request(nonce: 1, expectedRevision: 0),
            FarmhandId,
            Truth(-10d),
            nowMilliseconds: 1000
        );

        Assert.True(result.Accepted);
        Assert.Equal(190d, service.GetCurrent(FarmhandKey));
        Assert.Equal(1, result.Snapshot!.Revision);
        Assert.Equal(1, changedEvents);
    }

    [Fact]
    public void Forged_owner_is_rejected_before_creating_or_changing_that_player()
    {
        var service = Host();
        var request = Request(nonce: 1, expectedRevision: 0);
        request.PlayerKey = ForgedKey;

        var result = service.HandleHostRequest(
            request,
            FarmhandId,
            Truth(-10d),
            nowMilliseconds: 1000
        );

        Assert.False(result.Accepted);
        Assert.Equal("request-sender-does-not-own-player", result.Reason);
        Assert.False(service.TryGetSnapshot(ForgedKey, out _));
    }

    [Fact]
    public void Replayed_nonce_is_rejected_without_a_second_change()
    {
        var service = Host();
        var request = Request(nonce: 1, expectedRevision: 0);
        var first = service.HandleHostRequest(
            request,
            FarmhandId,
            Truth(-10d),
            nowMilliseconds: 1000
        );
        request.ExpectedRevision = 1;

        var replay = service.HandleHostRequest(
            request,
            FarmhandId,
            Truth(-10d),
            nowMilliseconds: 3000
        );

        Assert.True(first.Accepted);
        Assert.False(replay.Accepted);
        Assert.Equal("request-nonce-is-replayed-or-out-of-order", replay.Reason);
        Assert.Equal(190d, service.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Lower_out_of_order_nonce_is_rejected()
    {
        var service = Host();
        var first = Request(nonce: 2, expectedRevision: 0);
        service.HandleHostRequest(first, FarmhandId, Truth(-10d), 1000);
        var lower = Request(nonce: 1, expectedRevision: 1);

        var result = service.HandleHostRequest(
            lower,
            FarmhandId,
            Truth(-10d),
            3000
        );

        Assert.False(result.Accepted);
        Assert.Equal("request-nonce-is-replayed-or-out-of-order", result.Reason);
    }

    [Fact]
    public void Revision_mismatch_is_rejected_and_requests_authoritative_snapshot()
    {
        var service = Host();

        var result = service.HandleHostRequest(
            Request(nonce: 1, expectedRevision: 99),
            FarmhandId,
            Truth(-10d),
            1000
        );

        Assert.False(result.Accepted);
        Assert.True(result.NeedsSnapshot);
        Assert.Equal("request-expected-revision-does-not-match", result.Reason);
        Assert.Equal(200d, service.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Food_source_cooldown_is_enforced_and_a_later_nonce_can_succeed()
    {
        var service = Host();
        Assert.True(
            service.HandleHostRequest(
                Request(nonce: 1, expectedRevision: 0),
                FarmhandId,
                Truth(-10d),
                1000
            ).Accepted
        );

        var tooSoon = service.HandleHostRequest(
            Request(nonce: 2, expectedRevision: 1),
            FarmhandId,
            Truth(-10d),
            1500
        );
        var later = service.HandleHostRequest(
            Request(nonce: 3, expectedRevision: 1),
            FarmhandId,
            Truth(-10d),
            2000
        );

        Assert.False(tooSoon.Accepted);
        Assert.Equal("request-source-cooldown-is-active", tooSoon.Reason);
        Assert.True(later.Accepted);
        Assert.Equal(180d, service.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Host_context_rejection_does_not_change_state()
    {
        var service = Host();

        var result = service.HandleHostRequest(
            Request(nonce: 1, expectedRevision: 0),
            FarmhandId,
            RejectTruth("food-context-is-not-observable-by-host"),
            1000
        );

        Assert.False(result.Accepted);
        Assert.Contains("food-context-is-not-observable-by-host", result.Reason);
        Assert.Equal(200d, service.GetCurrent(FarmhandKey));
        Assert.True(service.TryGetSnapshot(FarmhandKey, out var snapshot));
        Assert.Equal(0, snapshot.Revision);
    }

    [Theory]
    [InlineData(double.NaN, "host-recomputed-delta-must-be-finite")]
    [InlineData(double.PositiveInfinity, "host-recomputed-delta-must-be-finite")]
    [InlineData(201d, "host-recomputed-delta-exceeds-source-range")]
    [InlineData(-201d, "host-recomputed-delta-exceeds-source-range")]
    public void Host_recomputed_delta_must_be_finite_and_source_bounded(
        double delta,
        string expectedReason
    )
    {
        var service = Host();

        var result = service.HandleHostRequest(
            Request(nonce: 1, expectedRevision: 0),
            FarmhandId,
            Truth(delta),
            1000
        );

        Assert.False(result.Accepted);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(200d, service.GetCurrent(FarmhandKey));
    }

    [Theory]
    [InlineData((int)SanityChangeSource.Unknown)]
    [InlineData((int)SanityChangeSource.Equipment)]
    [InlineData((int)SanityChangeSource.Npc)]
    [InlineData((int)SanityChangeSource.Junimo)]
    [InlineData((int)SanityChangeSource.Monster)]
    [InlineData((int)SanityChangeSource.Night)]
    [InlineData((int)SanityChangeSource.Mine)]
    [InlineData((int)SanityChangeSource.Sleep)]
    [InlineData((int)SanityChangeSource.Buff)]
    [InlineData((int)SanityChangeSource.Migration)]
    [InlineData((int)SanityChangeSource.Administration)]
    [InlineData((int)SanityChangeSource.HostileShadowKill)]
    [InlineData((int)SanityChangeSource.VoluntarySleep)]
    [InlineData((int)SanityChangeSource.TimeLimitPassOut)]
    [InlineData((int)SanityChangeSource.ExhaustionPassOut)]
    [InlineData((int)SanityChangeSource.HealthDeath)]
    [InlineData((int)SanityChangeSource.SanityDarknessSpecialDeath)]
    public void Host_only_sources_are_rejected_from_clients(
        int sourceValue
    )
    {
        var service = Host();
        var request = Request(nonce: 1, expectedRevision: 0);
        var source = (SanityChangeSource)sourceValue;
        request.Source = source;

        var result = service.HandleHostRequest(
            request,
            FarmhandId,
            Truth(1d),
            1000
        );

        Assert.False(result.Accepted);
        Assert.Equal("change-source-is-host-only", result.Reason);
        Assert.Equal(200d, service.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Wrong_session_is_rejected_before_state_change()
    {
        var service = Host();
        var request = Request(nonce: 1, expectedRevision: 0);
        request.SessionId = OtherSession;

        var result = service.HandleHostRequest(
            request,
            FarmhandId,
            Truth(-10d),
            1000
        );

        Assert.False(result.Accepted);
        Assert.True(result.NeedsSnapshot);
        Assert.Equal("request-session-does-not-match", result.Reason);
        Assert.Equal(200d, service.GetCurrent(FarmhandKey));
    }

    [Fact]
    public void Missing_interaction_is_rejected()
    {
        var service = Host();
        var request = Request(nonce: 1, expectedRevision: 0);
        request.InteractionId = string.Empty;

        var result = service.HandleHostRequest(
            request,
            FarmhandId,
            Truth(-10d),
            1000
        );

        Assert.False(result.Accepted);
        Assert.Equal("interaction-id-is-missing-or-too-long", result.Reason);
    }

    [Fact]
    public void Request_schema_has_no_arbitrary_set_or_delta_payload()
    {
        var properties = typeof(SanityChangeRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Delta", properties);
        Assert.DoesNotContain("Value", properties);
        Assert.DoesNotContain("Current", properties);
        Assert.Contains("InteractionId", properties);
        Assert.Contains("ExpectedRevision", properties);
    }

    private static SanityChangeService Host()
    {
        var data = SanitySaveDataCodec.NewData(MasterKey, 200d, 200d);
        var persistence = new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test",
            200d,
            data
        );
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        Assert.True(
            service.BeginHostSession(
                Session,
                persistence,
                MasterKey,
                out var reason
            ),
            reason
        );
        Assert.True(service.TryEnsureHostPlayer(FarmhandKey, out _, out _));
        return service;
    }

    private static SanityChangeRequest Request(
        long nonce,
        long expectedRevision
    )
    {
        return new SanityChangeRequest
        {
            SessionId = Session,
            PlayerKey = FarmhandKey,
            Source = SanityChangeSource.Food,
            InteractionId = "16",
            Nonce = nonce,
            ExpectedRevision = expectedRevision,
        };
    }

    private static ISanityRequestTruthSource Truth(double delta)
    {
        return new FakeTruthSource(SanityRequestTruth.Accepted(delta));
    }

    private static ISanityRequestTruthSource RejectTruth(string reason)
    {
        return new FakeTruthSource(SanityRequestTruth.Rejected(reason));
    }

    private sealed class FakeTruthSource : ISanityRequestTruthSource
    {
        private readonly SanityRequestTruth result;

        internal FakeTruthSource(SanityRequestTruth result)
        {
            this.result = result;
        }

        public SanityRequestTruth Resolve(
            SanityChangeRequest request,
            long senderPlayerId
        )
        {
            return result;
        }
    }
}
