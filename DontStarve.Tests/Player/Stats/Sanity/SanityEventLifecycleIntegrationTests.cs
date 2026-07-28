using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Events;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityEventLifecycleIntegrationTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void One_owner_freezes_without_changing_other_owner_or_v2_save_inputs()
    {
        var service = Service();
        var lifecycle = Ready(service);
        Assert.True(service.TryEnsureHostPlayer("2", out _, out var ensureReason), ensureReason);
        Assert.True(service.TryGetSnapshot("1", out var beforeA));
        Assert.True(service.TryGetSnapshot("2", out var beforeB));
        var coverage = new SanityEventCoverageKey("1", 0, Session);

        Assert.True(lifecycle.TrySetEventCoverage(coverage, true, out var startReason), startReason);
        var frozenChange = service.Change("1", -50d, SanityChangeSource.Night);
        var otherChange = service.Change("2", -50d, SanityChangeSource.Night);

        Assert.Equal(SanityChangeStatus.NoChange, frozenChange.Status);
        Assert.Equal("sanity-change-frozen-by-effective-overlay", frozenChange.Reason);
        Assert.Equal(SanityChangeStatus.Applied, otherChange.Status);
        Assert.True(service.TryGetSnapshot("1", out var afterA));
        Assert.True(service.TryGetSnapshot("2", out var afterB));
        Assert.Equal((beforeA.Current, beforeA.Revision), (afterA.Current, afterA.Revision));
        Assert.Equal(beforeB.Current - 50d, afterB.Current);
        Assert.Equal(beforeB.Revision + 1, afterB.Revision);

        var saved = service.CaptureSaveInputs().ToDictionary(entry => entry.PlayerKey);
        Assert.Equal(beforeA.Current, saved["1"].Current);
        Assert.Equal(afterB.Current, saved["2"].Current);
    }

    [Fact]
    public void Two_screens_reference_count_one_owner_until_last_exit()
    {
        var service = Service();
        var lifecycle = Ready(service);
        var screen0 = new SanityEventCoverageKey("1", 0, Session);
        var screen1 = new SanityEventCoverageKey("1", 1, Session);
        var globalEdges = new List<bool>();
        lifecycle.EventCoverageChanged += globalEdges.Add;

        Assert.True(lifecycle.TrySetEventCoverage(screen0, true, out _));
        Assert.True(lifecycle.TrySetEventCoverage(screen1, true, out _));
        Assert.True(lifecycle.TrySetEventCoverage(screen0, false, out _));
        Assert.Equal(
            SanityChangeStatus.NoChange,
            service.Change("1", -1d, SanityChangeSource.Night).Status
        );
        Assert.True(lifecycle.TrySetEventCoverage(screen1, false, out _));
        Assert.Equal(
            SanityChangeStatus.Applied,
            service.Change("1", -1d, SanityChangeSource.Night).Status
        );
        Assert.Equal(new[] { true, false }, globalEdges);
    }

    [Fact]
    public void Session_mismatch_fails_closed_without_freezing_owner()
    {
        var service = Service();
        var lifecycle = Ready(service);
        var stale = new SanityEventCoverageKey(
            "1",
            0,
            "fedcba9876543210fedcba9876543210"
        );

        Assert.False(lifecycle.TrySetEventCoverage(stale, true, out var reason));
        Assert.Equal("event-coverage-session-does-not-match", reason);
        Assert.Equal(
            SanityChangeStatus.Applied,
            service.Change("1", -1d, SanityChangeSource.Night).Status
        );
    }

    [Fact]
    public void Disabled_and_clear_session_remove_freeze_without_restoring_old_value()
    {
        var service = Service();
        var lifecycle = Ready(service);
        var coverage = new SanityEventCoverageKey("1", 0, Session);
        Assert.True(lifecycle.TrySetEventCoverage(coverage, true, out _));
        Assert.True(service.TryGetSnapshot("1", out var before));

        lifecycle.ApplyConfiguredState(false);

        Assert.False(lifecycle.IsEventCoverageActive);
        Assert.True(service.TryGetSnapshot("1", out var disabled));
        Assert.Equal((before.Current, before.Revision), (disabled.Current, disabled.Revision));
        lifecycle.ClearSession(SanitySessionBoundary.ReturnedToTitle);
        Assert.False(service.HasActiveSession);
    }

    private static SanityChangeService Service()
    {
        return new SanityChangeService(
            new DefaultSanityMaximumProvider(),
            new DefaultIntensityProvider()
        );
    }

    private static SanitySystemLifecycleCoordinator Ready(
        SanityChangeService service
    )
    {
        var lifecycle = new SanitySystemLifecycleCoordinator(service);
        lifecycle.Initialize();
        var data = new SanitySaveData();
        data.Players["1"] = new SanityPlayerSaveData { Current = 80d, MaxAtSave = 200d };
        data.Players["2"] = new SanityPlayerSaveData { Current = 160d, MaxAtSave = 200d };
        var persistence = new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "event-lifecycle-test",
            80d,
            data
        );
        Assert.True(
            service.BeginHostSession(Session, persistence, "1", out var reason),
            reason
        );
        return lifecycle;
    }

    private sealed class DefaultIntensityProvider : ISanityMonsterIntensityProvider
    {
        public SanityMonsterIntensityResolution Resolve()
        {
            return new SanityMonsterIntensityResolution(
                true,
                SanityMonsterIntensityIds.Default,
                "test-intensity"
            );
        }
    }
}
