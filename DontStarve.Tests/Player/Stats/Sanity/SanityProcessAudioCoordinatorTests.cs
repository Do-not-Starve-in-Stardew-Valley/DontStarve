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
    public void Direct_drop_to_zero_triggers_once_and_hysteresis_does_not_rearm_until_exit()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        coordinator.SubmitClaim(Claim("1", 0, 1, 1d));
        coordinator.SubmitClaim(
            Claim("1", 0, 2, 0d, ambience: true, whispers: true, danger: true, musicSuppression: true)
        );
        Assert.Equal(1, output.DangerTriggerCount);

        // The tier state machine keeps Danger active in the 15%-17.5% hysteresis band.
        coordinator.SubmitClaim(
            Claim("1", 0, 3, 0.16d, ambience: true, whispers: true, danger: true, musicSuppression: true)
        );
        coordinator.SubmitClaim(
            Claim("1", 0, 4, 0.15d, ambience: true, whispers: true, danger: true, musicSuppression: true)
        );
        Assert.Equal(1, output.DangerTriggerCount);

        coordinator.SubmitClaim(
            Claim("1", 0, 5, 0.176d, ambience: true, whispers: true, musicSuppression: true)
        );
        Assert.True(coordinator.Snapshot().DangerArmed);

        coordinator.SubmitClaim(
            Claim("1", 0, 6, 0.15d, ambience: true, whispers: true, danger: true, musicSuppression: true)
        );
        Assert.Equal(2, output.DangerTriggerCount);
    }

    [Fact]
    public void Fifty_percent_music_request_is_valid_without_the_danger_lane()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        var result = coordinator.SubmitClaim(
            Claim("1", 0, 1, 0.5d, ambience: true, musicSuppression: true)
        );

        Assert.Equal(SanityAudioClaimUpdateStatus.Applied, result.Status);
        Assert.True(output.AmbiencePhysical);
        Assert.False(coordinator.Snapshot().DangerActive);
        Assert.True(coordinator.Snapshot().Winner?.MusicSuppressionRequested);
    }

    [Fact]
    public void Focus_paused_danger_edge_plays_once_without_late_replay()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.8d, true, true));
        coordinator.SetProcessPaused(true);
        coordinator.SubmitClaim(Claim("1", 0, 2, 0d, true, true, true));

        Assert.Equal(1, output.DangerTriggerCount);
        Assert.False(coordinator.Snapshot().DangerArmed);

        coordinator.SetProcessPaused(false);
        Assert.Equal(1, output.DangerTriggerCount);

        coordinator.SubmitClaim(Claim("1", 0, 3, 0.16d, true, true, true));
        coordinator.SubmitClaim(Claim("1", 0, 4, 0.15d, true, true, true));
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
    public void Focus_pause_only_pauses_continuous_lanes_and_keeps_threshold_instance_active()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));

        Assert.True(output.DangerPhysical);

        coordinator.SetProcessPaused(true);

        Assert.True(output.ContinuousPoolsPaused);
        Assert.False(output.AmbiencePhysical);
        Assert.False(output.WhispersPhysical);
        Assert.True(output.DangerPhysical);
        Assert.Equal(0, output.FullPauseCallCount);

        coordinator.SetProcessPaused(false);

        Assert.False(output.ContinuousPoolsPaused);
        Assert.True(output.AmbiencePhysical);
        Assert.True(output.WhispersPhysical);
        Assert.True(output.DangerPhysical);
    }

    [Fact]
    public void Event_suspension_stops_physical_audio_but_retains_claim_and_receipt()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true, true));
        coordinator.SetEventSuspended(true);

        Assert.Equal(0, coordinator.Snapshot().PhysicalInstanceCount);
        Assert.Single(coordinator.Snapshot().Claims);

        coordinator.SetEventSuspended(false);
        var resumed = coordinator.Snapshot();
        Assert.Equal(2, resumed.PhysicalInstanceCount);
        Assert.True(resumed.Winner?.MusicSuppressionRequested);
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
    public void Darkness_warning_claim_selects_clip_and_pauses_resumes_same_physical_warning()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        var claim = new SanityDarknessWarningClaim(
            "1",
            0,
            SessionId,
            "warning-request",
            2,
            "sanity.clip.darkness.warning.alt",
            1.25d
        );

        Assert.Equal(
            SanityAudioClaimUpdateStatus.Applied,
            coordinator.SubmitDarknessWarningClaim(claim).Status
        );
        Assert.Equal(claim.WarningClipId, output.WarningClipId);
        Assert.Equal(1, output.DarknessWarningActivationCount);
        Assert.True(output.DarknessWarningPhysical);

        Assert.Equal(
            SanityAudioClaimUpdateStatus.Applied,
            coordinator.SetDarknessWarningClaimPlaybackPaused("1", 0, true).Status
        );
        Assert.True(output.DarknessWarningPaused);
        Assert.Equal(1, output.DarknessWarningActivationCount);

        Assert.Equal(
            SanityAudioClaimUpdateStatus.Applied,
            coordinator.SetDarknessWarningClaimPlaybackPaused("1", 0, false).Status
        );
        Assert.False(output.DarknessWarningPaused);
        Assert.True(output.DarknessWarningPhysical);
        Assert.Equal(1, output.DarknessWarningActivationCount);
    }

    [Fact]
    public void Darkness_attack_triggers_once_and_does_not_replay_after_process_menu_or_focus_pause()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);

        coordinator.TriggerDarknessAttack();
        coordinator.SetProcessPaused(true);
        coordinator.SetProcessPaused(false);
        coordinator.SetMenuPaused(true);
        coordinator.SetMenuPaused(false);

        Assert.Equal(1, output.DarknessAttackTriggerCount);
        Assert.True(output.DarknessAttackPhysical);
        Assert.Equal(1, coordinator.Snapshot().PhysicalInstanceCount);
    }

    [Fact]
    public void Physical_instance_budget_includes_the_dedicated_darkness_attack_lane()
    {
        Assert.Equal(5, SanityProcessAudioCoordinator.MaximumPhysicalInstances);

        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        coordinator.SubmitClaim(Claim("1", 0, 1, 0.1d, true, true, true));
        coordinator.SubmitDarknessWarningClaim(
            new SanityDarknessWarningClaim("1", 0, SessionId, "warning", 1, "clip", 1d)
        );
        coordinator.TriggerDarknessAttack();

        Assert.Equal(5, coordinator.Snapshot().PhysicalInstanceCount);
        Assert.Equal(1, output.DarknessAttackTriggerCount);
    }

    [Fact]
    public void Explicit_claim_removal_retains_revision_receipt_until_a_higher_revision_arrives()
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
    public void Continuous_lane_reuses_the_same_stopped_instance_when_focus_resume_races_backend()
    {
        var effect = new FakeEffect("pool");
        var lane = Lane(SanityAudioLaneKind.Whispers, new[] { effect }, true);
        lane.SetContinuousActive(true, 1f);
        var instance = Assert.Single(effect.Instances);

        lane.Pause();
        instance.SetStopped();
        lane.Resume();

        Assert.Single(effect.Instances);
        Assert.Same(instance, effect.Instances[0]);
        Assert.False(instance.Disposed);
        Assert.Equal(SanityAudioPlaybackState.Playing, instance.State);
    }

    [Fact]
    public void Wrong_thread_focus_pause_and_resume_are_deferred_without_failing_lane()
    {
        var thread = new FakeThreadContext { IsOnOwningThread = true };
        var effect = new FakeEffect("pool");
        var lane = Lane(
            SanityAudioLaneKind.Ambience,
            new[] { effect },
            true,
            threadContext: thread
        );
        lane.SetContinuousActive(true, 1f);
        var instance = Assert.Single(effect.Instances);

        thread.IsOnOwningThread = false;
        lane.Pause();
        Assert.False(lane.IsFailed);
        Assert.Equal(SanityAudioPlaybackState.Playing, instance.State);

        thread.IsOnOwningThread = true;
        lane.Tick();
        Assert.Equal(SanityAudioPlaybackState.Paused, instance.State);

        thread.IsOnOwningThread = false;
        lane.Resume();
        thread.IsOnOwningThread = true;
        lane.Tick();

        Assert.False(lane.IsFailed);
        Assert.Equal(SanityAudioPlaybackState.Playing, instance.State);
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
        bool danger = false,
        bool musicSuppression = false
    ) => new(
        playerKey,
        screenId,
        revision,
        ratio,
        ambience,
        whispers,
        danger,
        musicSuppression
    );

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

        internal bool DarknessWarningPaused { get; private set; }

        internal bool DarknessAttackPhysical { get; private set; }

        internal string WarningClipId { get; private set; } = string.Empty;

        internal bool ContinuousPoolsPaused { get; private set; }

        internal int DangerTriggerCount { get; private set; }

        internal int DarknessWarningActivationCount { get; private set; }

        internal int DarknessAttackTriggerCount { get; private set; }

        internal int AmbienceTransitionCount { get; private set; }

        internal int ClearCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal int FullPauseCallCount { get; private set; }

        public int PhysicalInstanceCount =>
            (AmbiencePhysical ? 1 : 0)
            + (WhispersPhysical ? 1 : 0)
            + (DangerPhysical ? 1 : 0)
            + (DarknessWarningPhysical ? 1 : 0)
            + (DarknessAttackPhysical ? 1 : 0);

        public void SetPoolActive(SanityAudioLaneKind lane, bool active)
        {
            if (lane == SanityAudioLaneKind.Ambience)
            {
                AmbienceTransitionCount++;
                ambienceDesired = active;
                AmbiencePhysical = active && !suspended && !ContinuousPoolsPaused;
            }
            else if (lane == SanityAudioLaneKind.Whispers)
            {
                whispersDesired = active;
                WhispersPhysical = active && !suspended && !ContinuousPoolsPaused;
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
            if (active && !DarknessWarningPhysical)
                DarknessWarningActivationCount++;
            darknessWarningDesired = active;
            DarknessWarningPhysical =
                active && !DarknessWarningPaused && (!suspended || specialEventAudioAllowed);
        }

        public void SetDarknessWarningClip(string warningClipId)
        {
            WarningClipId = warningClipId;
        }

        public void SetDarknessWarningPaused(bool paused)
        {
            DarknessWarningPaused = paused;
            DarknessWarningPhysical =
                darknessWarningDesired
                && !paused
                && (!suspended || specialEventAudioAllowed);
        }

        public void TriggerDarknessAttack()
        {
            DarknessAttackTriggerCount++;
            DarknessAttackPhysical = true;
        }

        public void SetPaused(bool paused) => FullPauseCallCount++;

        public void SetContinuousPoolsPaused(bool paused)
        {
            ContinuousPoolsPaused = paused;
            AmbiencePhysical = ambienceDesired && !suspended && !paused;
            WhispersPhysical = whispersDesired && !suspended && !paused;
        }

        public void SetSuspended(bool value)
        {
            suspended = value;
            if (value)
            {
                AmbiencePhysical = false;
                WhispersPhysical = false;
                DangerPhysical = false;
                DarknessWarningPhysical = specialEventAudioAllowed
                    && darknessWarningDesired
                    && !DarknessWarningPaused;
            }
            else
            {
                AmbiencePhysical = ambienceDesired && !ContinuousPoolsPaused;
                WhispersPhysical = whispersDesired && !ContinuousPoolsPaused;
                DarknessWarningPhysical = darknessWarningDesired && !DarknessWarningPaused;
            }
        }

        public void SetSpecialEventAudioAllowed(bool allowed)
        {
            specialEventAudioAllowed = allowed;
            DarknessWarningPhysical = darknessWarningDesired
                && !DarknessWarningPaused
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
            ContinuousPoolsPaused = false;
            AmbiencePhysical = false;
            WhispersPhysical = false;
            DangerPhysical = false;
            DarknessWarningPhysical = false;
            DarknessWarningPaused = false;
            DarknessAttackPhysical = false;
            WarningClipId = string.Empty;
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
