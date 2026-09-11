using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class HostileShadowPushBoxMultiplayerProtocolTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Owner = "123456789";
    private const string OtherOwner = "223456789";

    [Fact]
    public void Intent_accepts_a_bounded_empty_batch_and_rejects_overflow()
    {
        var message = ValidIntent();

        Assert.True(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                Owner,
                "Farm",
                Session,
                out var validReason
            ),
            validReason
        );

        for (var index = 0; index <= HostileShadowProtocol.MaximumPushBoxEntriesPerBatch; index++)
            message.Entries.Add(IntentEntry($"projection-{index:D3}"));

        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                Owner,
                "Farm",
                Session,
                out var overflowReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-intent-envelope-invalid", overflowReason);
    }

    [Fact]
    public void Intent_rejects_duplicate_correlations_and_untrusted_entry_data()
    {
        var duplicate = ValidIntent();
        duplicate.Entries.Add(IntentEntry("same-correlation"));
        duplicate.Entries.Add(IntentEntry("same-correlation"));

        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                duplicate,
                Owner,
                "Farm",
                Session,
                out var duplicateReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-intent-entry-invalid", duplicateReason);

        var invalidSpecies = ValidIntent();
        invalidSpecies.Entries.Add(
            IntentEntry("invalid-species", speciesId: "sanity.projection.mr-skitts")
        );
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                invalidSpecies,
                Owner,
                "Farm",
                Session,
                out var speciesReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-intent-entry-invalid", speciesReason);

        var invalidCoordinates = ValidIntent();
        invalidCoordinates.Entries.Add(
            IntentEntry("invalid-coordinate", currentPositionX: double.NaN)
        );
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                invalidCoordinates,
                Owner,
                "Farm",
                Session,
                out var coordinateReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-intent-entry-invalid", coordinateReason);
    }

    [Fact]
    public void Intent_binds_session_owner_location_capability_and_nonce()
    {
        var message = ValidIntent();

        message.SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                Owner,
                "Farm",
                Session,
                out _
            )
        );

        message = ValidIntent();
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                OtherOwner,
                "Farm",
                Session,
                out _
            )
        );

        message = ValidIntent();
        message.LocationId = "Mine";
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                Owner,
                "Farm",
                Session,
                out _
            )
        );

        message = ValidIntent();
        message.CapabilityId = "other-capability";
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                Owner,
                "Farm",
                Session,
                out _
            )
        );

        message = ValidIntent();
        message.BatchNonce = 0;
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxIntentMessage(
                message,
                Owner,
                "Farm",
                Session,
                out _
            )
        );
    }

    [Fact]
    public void Result_accepts_empty_batch_and_only_fresh_host_ticks()
    {
        var message = ValidResult(hostTick: 100);

        Assert.True(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out var validReason
            ),
            validReason
        );
        Assert.True(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 130,
                out var boundaryReason
            ),
            boundaryReason
        );

        message.HostTick = 69;
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out var staleReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-result-host-tick-stale-or-future", staleReason);

        message.HostTick = 101;
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out var futureReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-result-host-tick-stale-or-future", futureReason);
    }

    [Fact]
    public void Result_rejects_duplicate_correlations_and_overflow()
    {
        var duplicate = ValidResult(hostTick: 100);
        duplicate.Entries.Add(ResultEntry("same-correlation"));
        duplicate.Entries.Add(ResultEntry("same-correlation"));
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                duplicate,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out var duplicateReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-result-entry-invalid", duplicateReason);

        var overflow = ValidResult(hostTick: 100);
        for (var index = 0; index <= HostileShadowProtocol.MaximumPushBoxEntriesPerBatch; index++)
            overflow.Entries.Add(ResultEntry($"projection-{index:D3}"));
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                overflow,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out var overflowReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-result-envelope-invalid", overflowReason);
    }

    [Fact]
    public void Result_binds_session_owner_location_capability_and_entry_contract()
    {
        var message = ValidResult(hostTick: 100);
        message.SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out _
            )
        );

        message = ValidResult(hostTick: 100);
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                OtherOwner,
                "Farm",
                Session,
                currentHostTick: 100,
                out _
            )
        );

        message = ValidResult(hostTick: 100);
        message.LocationId = "Mine";
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out _
            )
        );

        message = ValidResult(hostTick: 100);
        message.CapabilityId = "other-capability";
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out _
            )
        );

        message = ValidResult(hostTick: 100);
        message.Entries.Add(
            ResultEntry("invalid-species", speciesId: "sanity.projection.mr-skitts")
        );
        Assert.False(
            HostileShadowProtocol.IsValidPushBoxResultMessage(
                message,
                Owner,
                "Farm",
                Session,
                currentHostTick: 100,
                out var entryReason
            )
        );
        Assert.Equal("hostile-shadow.push-box-result-entry-invalid", entryReason);
    }

    private static ShadowProjectionPushBoxIntentMessage ValidIntent()
    {
        return new ShadowProjectionPushBoxIntentMessage
        {
            SessionId = Session,
            OwnerPlayerKey = Owner,
            LocationId = "Farm",
            CapabilityId = HostileShadowProtocol.ShadowPushBoxCapabilityId,
            BatchNonce = 1,
        };
    }

    private static ShadowProjectionPushBoxIntentEntry IntentEntry(
        string correlationId,
        string speciesId = "sanity.projection.creeper-fear",
        double currentPositionX = 100d
    )
    {
        return new ShadowProjectionPushBoxIntentEntry
        {
            CorrelationId = correlationId,
            SpeciesId = speciesId,
            Revision = 1,
            CurrentPositionX = currentPositionX,
            CurrentPositionY = 200d,
            NormalTargetPositionX = 110d,
            NormalTargetPositionY = 210d,
        };
    }

    private static ShadowProjectionPushBoxResultMessage ValidResult(long hostTick)
    {
        return new ShadowProjectionPushBoxResultMessage
        {
            SessionId = Session,
            OwnerPlayerKey = Owner,
            LocationId = "Farm",
            CapabilityId = HostileShadowProtocol.ShadowPushBoxCapabilityId,
            BatchNonce = 1,
            HostTick = hostTick,
        };
    }

    private static ShadowProjectionPushBoxResultEntry ResultEntry(
        string correlationId,
        string speciesId = "sanity.projection.creeper-fear"
    )
    {
        return new ShadowProjectionPushBoxResultEntry
        {
            CorrelationId = correlationId,
            SpeciesId = speciesId,
            Revision = 1,
            FinalPositionX = 100d,
            FinalPositionY = 200d,
        };
    }
}
