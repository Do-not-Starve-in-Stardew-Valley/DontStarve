using DontStarve.Player.Stats.Sanity.Audio;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityProcessAudioCoordinatorTests
{
    private const string SessionId = "11111111111141118111111111111111";

    [Fact]
    public void Higher_revision_replaces_claim_while_duplicate_and_stale_are_idempotent()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        Assert.Equal(SanityAudioClaimUpdateStatus.Applied, coordinator.SubmitClaim(Claim("1", 0, 4, 0.4d, ambience: true)).Status);
        Assert.Equal(SanityAudioClaimUpdateStatus.IgnoredDuplicate, coordinator.SubmitClaim(Claim("1", 0, 4, 0.9d)).Status);
        Assert.Equal(SanityAudioClaimUpdateStatus.IgnoredStale, coordinator.SubmitClaim(Claim("1", 0, 3, 0.9d)).Status);
        Assert.True(output.AmbiencePhysical);

        Assert.Equal(SanityAudioClaimUpdateStatus.Applied, coordinator.SubmitClaim(Claim("1", 0, 5, 0.9d)).Status);
        Assert.False(output.AmbiencePhysical);
        Assert.Equal(5, coordinator.Snapshot().Winner?.Revision);
    }

    [Fact]
    public void Winner_is_lowest_ratio_then_screen_then_numeric_player_key()
    {
        using var coordinator = new SanityProcessAudioCoordinator(new FakeProcessOutput());
        coordinator.SubmitClaim(Claim("10", 1, 1, 0.4d, ambience: true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.4d, ambience: true));

        Assert.Equal("2", coordinator.Snapshot().Winner?.PlayerKey);

        coordinator.SubmitClaim(Claim("99", 0, 1, 0.4d, ambience: true));
        var winner = Assert.IsType<SanityAudioOwnerClaim>(coordinator.Snapshot().Winner);
        Assert.Equal("99", winner.PlayerKey);
        Assert.Equal(0, winner.ScreenId);
    }

    [Fact]
    public void Winner_switch_does_not_restart_a_pool_while_another_claim_keeps_it_active()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.4d, ambience: true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.3d, ambience: true));
        Assert.Equal("2", coordinator.Snapshot().Winner?.PlayerKey);

        coordinator.SubmitClaim(Claim("2", 1, 2, 0.6d));

        Assert.Equal("1", coordinator.Snapshot().Winner?.PlayerKey);
        Assert.True(output.AmbiencePhysical);
        Assert.Equal(1, output.AmbienceTransitionCount);
    }

    [Fact]
    public void Two_screens_share_one_instance_per_lane_and_can_run_all_three_lanes()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        coordinator.SubmitClaim(Claim("1", 0, 1, 0.4d, ambience: true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.1d, ambience: true, whispers: true, danger: true));

        var snapshot = coordinator.Snapshot();
        Assert.Equal(2, snapshot.Claims.Count);
        Assert.True(snapshot.AmbienceActive);
        Assert.True(snapshot.WhispersActive);
        Assert.True(snapshot.DangerActive);
        Assert.Equal(3, snapshot.PhysicalInstanceCount);
        Assert.Equal(1, output.DangerTriggerCount);
    }

    [Fact]
    public void Danger_rearms_only_after_the_last_active_claim_exits()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.1d, true, true, true));
        Assert.Equal(1, output.DangerTriggerCount);

        coordinator.SubmitClaim(Claim("1", 0, 2, 0.2d, true, true));
        coordinator.SubmitClaim(Claim("2", 1, 2, 0.2d, true, true));
        Assert.True(coordinator.Snapshot().DangerArmed);

        coordinator.SubmitClaim(Claim("1", 0, 3, 0.1d, true, true, true));
        Assert.Equal(2, output.DangerTriggerCount);
    }

    [Fact]
    public void Paused_danger_edge_consumes_receipt_without_late_replay()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SetProcessPaused(true);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));

        coordinator.SetProcessPaused(false);
        Assert.Equal(0, output.DangerTriggerCount);
        Assert.False(coordinator.Snapshot().DangerArmed);

        coordinator.SubmitClaim(Claim("1", 0, 2, 0.2d, true, true));
        coordinator.SubmitClaim(Claim("1", 0, 3, 0.1d, true, true, true));
        Assert.Equal(1, output.DangerTriggerCount);
    }

    [Fact]
    public void Local_menu_excludes_only_its_owner_and_reselects_another_screen()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.2d, true, true));

        coordinator.SetClaimPlaybackPaused("1", 0, true);

        var snapshot = coordinator.Snapshot();
        Assert.Equal("2", snapshot.Winner?.PlayerKey);
        Assert.Equal(1, snapshot.LocallyPausedClaimCount);
        Assert.False(snapshot.ProcessPaused);
        Assert.True(snapshot.AmbienceActive);
        Assert.True(snapshot.WhispersActive);
        Assert.False(snapshot.DangerActive);
        Assert.True(output.AmbiencePhysical);
        Assert.True(output.WhispersPhysical);

        coordinator.SetClaimPlaybackPaused("2", 1, true);
        snapshot = coordinator.Snapshot();
        Assert.Null(snapshot.Winner);
        Assert.Equal(2, snapshot.LocallyPausedClaimCount);
        Assert.False(snapshot.AmbienceActive);
        Assert.False(snapshot.WhispersActive);

        coordinator.SetClaimPlaybackPaused("2", 1, false);
        Assert.Equal("2", coordinator.Snapshot().Winner?.PlayerKey);
        Assert.True(output.AmbiencePhysical);
        Assert.True(output.WhispersPhysical);
    }

    [Fact]
    public void Danger_entered_behind_local_menu_consumes_edge_without_playing_on_close()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        coordinator.SubmitClaim(
            Claim("1", 0, 1, 0.1d, true, true, true),
            playbackPaused: true
        );
        Assert.Equal(0, output.DangerTriggerCount);
        Assert.False(coordinator.Snapshot().DangerArmed);

        coordinator.SetClaimPlaybackPaused("1", 0, false);
        Assert.Equal(0, output.DangerTriggerCount);
        Assert.True(coordinator.Snapshot().DangerActive);

        coordinator.SubmitClaim(Claim("1", 0, 2, 0.2d, true, true));
        coordinator.SubmitClaim(Claim("1", 0, 3, 0.1d, true, true, true));
        Assert.Equal(1, output.DangerTriggerCount);
    }

    [Fact]
    public void Process_pause_and_focus_style_resume_do_not_replay_consumed_entry()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));

        coordinator.SetProcessPaused(true);
        coordinator.SetProcessPaused(false);

        Assert.Equal(1, output.DangerTriggerCount);
        Assert.True(coordinator.Snapshot().DangerActive);
        Assert.False(coordinator.Snapshot().ProcessPaused);
    }

    [Fact]
    public void Event_suspension_stops_physical_audio_but_retains_claim_and_receipt()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));
        coordinator.SetEventSuspended(true);

        Assert.Equal(0, coordinator.Snapshot().PhysicalInstanceCount);
        Assert.Single(coordinator.Snapshot().Claims);

        coordinator.SetEventSuspended(false);
        Assert.Equal(2, coordinator.Snapshot().PhysicalInstanceCount);
        Assert.Equal(1, output.DangerTriggerCount);
    }

    [Fact]
    public void Owned_two_am_event_suspends_tiers_but_keeps_only_warning_lane_eligible()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));
        coordinator.SetSpecialEventAudioAllowed(true);
        coordinator.SubmitDarknessWarningClaim(
            new SanityDarknessWarningClaim("1", 0, SessionId, "special", 2)
        );
        coordinator.SetEventSuspended(true);

        Assert.False(output.AmbiencePhysical);
        Assert.False(output.WhispersPhysical);
        Assert.False(output.DangerPhysical);
        Assert.True(output.DarknessWarningPhysical);

        coordinator.SetSpecialEventAudioAllowed(false);
        Assert.False(output.DarknessWarningPhysical);
    }

    [Fact]
    public void Warp_removal_retains_revision_receipt_until_a_higher_revision_arrives()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 7, 0.4d, ambience: true));

        Assert.Equal(SanityAudioClaimUpdateStatus.Removed, coordinator.RemoveClaim("1", 0).Status);
        Assert.Equal(SanityAudioClaimUpdateStatus.IgnoredDuplicate, coordinator.SubmitClaim(Claim("1", 0, 7, 0.4d, ambience: true)).Status);
        Assert.False(output.AmbiencePhysical);
        Assert.Equal(SanityAudioClaimUpdateStatus.Applied, coordinator.SubmitClaim(Claim("1", 0, 8, 0.4d, ambience: true)).Status);
        Assert.True(output.AmbiencePhysical);
    }

    [Fact]
    public void Removing_one_owner_keeps_shared_pool_for_the_other_owner()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.4d, ambience: true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.4d, ambience: true));

        Assert.Equal(1, coordinator.RemoveOwner("1"));
        Assert.True(output.AmbiencePhysical);
        Assert.Equal(1, coordinator.RemoveOwner("2"));
        Assert.False(output.AmbiencePhysical);
    }

    [Fact]
    public void Invalid_screen_cleanup_removes_only_invalid_screen_claims()
    {
        using var coordinator = new SanityProcessAudioCoordinator(new FakeProcessOutput());
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.4d, ambience: true));
        coordinator.SubmitClaim(Claim("2", 1, 1, 0.4d, ambience: true));

        Assert.Equal(1, coordinator.RemoveInvalidScreens(screenId => screenId == 1));
        var remaining = Assert.Single(coordinator.Snapshot().Claims);
        Assert.Equal(1, remaining.ScreenId);
    }

    [Fact]
    public void Day_or_session_clear_resets_claims_receipts_and_output_idempotently()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 3, 0.4d, ambience: true));

        coordinator.ClearClaims();
        coordinator.ClearClaims();
        Assert.Empty(coordinator.Snapshot().Claims);
        Assert.Equal(2, output.ClearCount);
        Assert.Equal(SanityAudioClaimUpdateStatus.Applied, coordinator.SubmitClaim(Claim("1", 0, 3, 0.4d, ambience: true)).Status);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void Invalid_nested_tier_flags_fail_closed(bool ambience, bool whispers, bool danger)
    {
        var diagnostics = new List<SanityAudioDiagnostic>();
        using var coordinator = new SanityProcessAudioCoordinator(new FakeProcessOutput(), diagnostics.Add);

        var result = coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, ambience, whispers, danger));

        Assert.Equal(SanityAudioClaimUpdateStatus.Invalid, result.Status);
        Assert.Empty(coordinator.Snapshot().Claims);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void Claim_capacity_is_bounded_and_overflow_is_rejected()
    {
        using var coordinator = new SanityProcessAudioCoordinator(new FakeProcessOutput());
        for (var index = 0; index < SanityProcessAudioCoordinator.MaximumLocalClaims; index++)
            Assert.Equal(SanityAudioClaimUpdateStatus.Applied, coordinator.SubmitClaim(Claim((index + 1).ToString(), index, 1, 1d)).Status);

        Assert.Equal(SanityAudioClaimUpdateStatus.Invalid, coordinator.SubmitClaim(Claim("99", 99, 1, 1d)).Status);
        Assert.Equal(SanityProcessAudioCoordinator.MaximumLocalClaims, coordinator.Snapshot().Claims.Count);
    }

    [Fact]
    public void Coordinator_dispose_is_idempotent_and_rejects_future_claims()
    {
        var output = new FakeProcessOutput();
        var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.4d, ambience: true));

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Equal(1, output.DisposeCount);
        Assert.Equal(SanityAudioClaimUpdateStatus.Disposed, coordinator.SubmitClaim(Claim("1", 0, 2, 0.4d, ambience: true)).Status);
        Assert.True(coordinator.Snapshot().Disposed);
    }

    [Fact]
    public void Continuous_lane_uses_injected_random_and_advances_after_clip_stops()
    {
        var first = new FakeEffect("first");
        var second = new FakeEffect("second");
        var lane = Lane(SanityAudioLaneKind.Ambience, new[] { first, second }, true, new SequenceRandom(1, 0));

        lane.SetContinuousActive(true, 0.5f);
        var secondInstance = Assert.Single(second.Instances);
        secondInstance.SetStopped();
        lane.Tick();

        Assert.True(secondInstance.Disposed);
        Assert.Single(first.Instances);
        Assert.Equal(SanityAudioPlaybackState.Playing, first.Instances[0].State);
        Assert.Equal(1, lane.PhysicalInstanceCount);
    }

    [Fact]
    public void Continuous_lane_never_owns_more_than_one_instance_and_stop_disposes_it()
    {
        var effect = new FakeEffect("pool");
        var lane = Lane(SanityAudioLaneKind.Whispers, new[] { effect }, true);
        lane.SetContinuousActive(true, 1f);
        lane.SetContinuousActive(true, 1f);

        Assert.Single(effect.Instances);
        Assert.Equal(1, lane.PhysicalInstanceCount);

        lane.SetContinuousActive(false, 1f);
        Assert.True(effect.Instances[0].Disposed);
        Assert.Equal(0, lane.PhysicalInstanceCount);
    }

    [Fact]
    public void Lane_pause_resume_and_instance_volume_are_local_and_deterministic()
    {
        var effect = new FakeEffect("pool");
        var lane = Lane(SanityAudioLaneKind.Ambience, new[] { effect }, true);
        lane.SetContinuousActive(true, 0.35f);
        var instance = Assert.Single(effect.Instances);

        lane.Pause();
        Assert.Equal(SanityAudioPlaybackState.Paused, instance.State);
        lane.Resume();
        Assert.Equal(SanityAudioPlaybackState.Playing, instance.State);
        Assert.Equal(0.35f, instance.Volume);
    }

    [Fact]
    public void One_shot_stops_and_disposes_after_playback_finishes_without_restarting()
    {
        var effect = new FakeEffect("danger");
        var lane = Lane(SanityAudioLaneKind.Danger, new[] { effect }, false);
        lane.TriggerOneShot(0.8f);
        var instance = Assert.Single(effect.Instances);
        instance.SetStopped();

        lane.Tick();
        lane.Tick();

        Assert.True(instance.Disposed);
        Assert.Single(effect.Instances);
        Assert.Equal(0, lane.PhysicalInstanceCount);
    }

    [Fact]
    public void Wrong_thread_fails_closed_with_one_stable_diagnostic_and_no_create_call()
    {
        var thread = new FakeThreadContext { IsOnOwningThread = false };
        var diagnostics = new List<SanityAudioDiagnostic>();
        var effect = new FakeEffect("pool");
        var lane = Lane(SanityAudioLaneKind.Ambience, new[] { effect }, true, threadContext: thread, diagnostics: diagnostics);

        lane.SetContinuousActive(true, 1f);
        lane.Tick();

        Assert.True(lane.IsFailed);
        Assert.Empty(effect.Instances);
        Assert.Single(diagnostics);
        Assert.Equal("audio.lane.wrong-thread", diagnostics[0].Code);
    }

    [Fact]
    public void Wrong_thread_stop_is_deferred_until_owning_thread_and_then_disposes()
    {
        var thread = new FakeThreadContext { IsOnOwningThread = true };
        var effect = new FakeEffect("pool");
        var lane = Lane(SanityAudioLaneKind.Ambience, new[] { effect }, true, threadContext: thread);
        lane.SetContinuousActive(true, 1f);
        var instance = Assert.Single(effect.Instances);

        thread.IsOnOwningThread = false;
        lane.StopPlayback();
        Assert.False(instance.Disposed);
        thread.IsOnOwningThread = true;
        lane.Tick();

        Assert.True(instance.Disposed);
        Assert.Equal(0, lane.PhysicalInstanceCount);
    }

    [Fact]
    public void Playback_failure_disables_only_that_lane_and_disposes_failed_instance()
    {
        var failingEffect = new FakeEffect("bad") { ThrowOnPlay = true };
        var healthyEffect = new FakeEffect("good");
        var diagnostics = new List<SanityAudioDiagnostic>();
        var failing = Lane(SanityAudioLaneKind.Ambience, new[] { failingEffect }, true, diagnostics: diagnostics);
        var healthy = Lane(SanityAudioLaneKind.Whispers, new[] { healthyEffect }, true);

        failing.SetContinuousActive(true, 1f);
        healthy.SetContinuousActive(true, 1f);

        Assert.True(failing.IsFailed);
        Assert.True(Assert.Single(failingEffect.Instances).Disposed);
        Assert.Equal(0, failing.PhysicalInstanceCount);
        Assert.Equal(1, healthy.PhysicalInstanceCount);
        Assert.Equal("audio.lane.play-failed", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Lane_dispose_is_idempotent_and_never_disposes_borrowed_effect()
    {
        var effect = new FakeEffect("pool");
        var lane = Lane(SanityAudioLaneKind.Ambience, new[] { effect }, true);
        lane.SetContinuousActive(true, 1f);

        lane.Dispose();
        lane.Dispose();

        Assert.True(lane.IsDisposed);
        Assert.Equal(1, Assert.Single(effect.Instances).DisposeCount);
    }

    private static SanityAudioOwnerClaim Claim(
        string playerKey,
        int screenId,
        long revision,
        double ratio,
        bool ambience = false,
        bool whispers = false,
        bool danger = false
    ) => new(playerKey, screenId, revision, ratio, ambience, whispers, danger);

    private static SanityAudioInstanceLane Lane(
        SanityAudioLaneKind kind,
        IReadOnlyList<ISanityAudioEffect> effects,
        bool continuous,
        ISanityAudioRandom? random = null,
        FakeThreadContext? threadContext = null,
        List<SanityAudioDiagnostic>? diagnostics = null
    ) => new(
        kind,
        effects,
        continuous,
        random ?? new SequenceRandom(0),
        threadContext ?? new FakeThreadContext { IsOnOwningThread = true },
        diagnostics is null ? null : new Action<SanityAudioDiagnostic>(diagnostics.Add)
    );

    private sealed class FakeProcessOutput : ISanityProcessAudioOutput
    {
        private bool suspended;
        private bool specialEventAudioAllowed;
        private bool ambienceDesired;
        private bool whispersDesired;
        private bool dangerDesired;
        private bool darknessWarningDesired;

        internal bool AmbiencePhysical { get; private set; }

        internal bool WhispersPhysical { get; private set; }

        internal bool DangerPhysical { get; private set; }

        internal bool DarknessWarningPhysical { get; private set; }

        internal int DangerTriggerCount { get; private set; }

        internal int AmbienceTransitionCount { get; private set; }

        internal int ClearCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public int PhysicalInstanceCount =>
            (AmbiencePhysical ? 1 : 0)
            + (WhispersPhysical ? 1 : 0)
            + (DangerPhysical ? 1 : 0)
            + (DarknessWarningPhysical ? 1 : 0);

        public void SetPoolActive(SanityAudioLaneKind lane, bool active)
        {
            if (lane == SanityAudioLaneKind.Ambience)
            {
                AmbienceTransitionCount++;
                ambienceDesired = active;
                AmbiencePhysical = active && !suspended;
            }
            else if (lane == SanityAudioLaneKind.Whispers)
            {
                whispersDesired = active;
                WhispersPhysical = active && !suspended;
            }
        }

        public void SetDangerActive(bool active)
        {
            dangerDesired = active;
            if (!active)
                DangerPhysical = false;
        }

        public void TriggerDanger()
        {
            if (!suspended && dangerDesired)
            {
                DangerTriggerCount++;
                DangerPhysical = true;
            }
        }

        public void SetDarknessWarningActive(bool active)
        {
            darknessWarningDesired = active;
            DarknessWarningPhysical =
                active && (!suspended || specialEventAudioAllowed);
        }

        public void SetPaused(bool paused) { }

        public void SetSuspended(bool value)
        {
            suspended = value;
            if (value)
            {
                AmbiencePhysical = false;
                WhispersPhysical = false;
                DangerPhysical = false;
                DarknessWarningPhysical = specialEventAudioAllowed
                    && darknessWarningDesired;
            }
            else
            {
                AmbiencePhysical = ambienceDesired;
                WhispersPhysical = whispersDesired;
                DarknessWarningPhysical = darknessWarningDesired;
            }
        }

        public void SetSpecialEventAudioAllowed(bool allowed)
        {
            specialEventAudioAllowed = allowed;
            DarknessWarningPhysical = darknessWarningDesired
                && (!suspended || specialEventAudioAllowed);
        }

        public void InvalidateResources() => SetSuspended(true);

        public void Tick() { }

        public void Clear()
        {
            ClearCount++;
            ambienceDesired = false;
            whispersDesired = false;
            dangerDesired = false;
            darknessWarningDesired = false;
            suspended = false;
            specialEventAudioAllowed = false;
            AmbiencePhysical = false;
            WhispersPhysical = false;
            DangerPhysical = false;
            DarknessWarningPhysical = false;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeThreadContext : ISanityAudioThreadContext
    {
        public bool IsOnOwningThread { get; set; }
    }

    private sealed class SequenceRandom : ISanityAudioRandom
    {
        private readonly int[] values;
        private int index;

        internal SequenceRandom(params int[] values) => this.values = values;

        public int NextIndex(int exclusiveUpperBound)
        {
            var value = values[Math.Min(index, values.Length - 1)];
            index++;
            return value;
        }
    }

    private sealed class FakeEffect : ISanityAudioEffect
    {
        internal FakeEffect(string resourceId) => ResourceId = resourceId;

        public string ResourceId { get; }

        internal bool ThrowOnPlay { get; set; }

        internal List<FakeInstance> Instances { get; } = new();

        public ISanityAudioInstance CreateInstance()
        {
            var instance = new FakeInstance { ThrowOnPlay = ThrowOnPlay };
            Instances.Add(instance);
            return instance;
        }
    }

    private sealed class FakeInstance : ISanityAudioInstance
    {
        public SanityAudioPlaybackState State { get; private set; } = SanityAudioPlaybackState.Stopped;

        public float Volume { get; set; }

        internal bool ThrowOnPlay { get; set; }

        internal bool Disposed { get; private set; }

        internal int DisposeCount { get; private set; }

        public void Play()
        {
            if (ThrowOnPlay)
                throw new InvalidOperationException("play failed");
            State = SanityAudioPlaybackState.Playing;
        }

        public void Pause() => State = SanityAudioPlaybackState.Paused;

        public void Resume() => State = SanityAudioPlaybackState.Playing;

        public void Stop() => State = SanityAudioPlaybackState.Stopped;

        public void Dispose()
        {
            DisposeCount++;
            Disposed = true;
        }

        internal void SetStopped() => State = SanityAudioPlaybackState.Stopped;
    }
}
