using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarknessAttackStateMachineTests
{
    private const string SessionId = "11111111111111111111111111111111";
    private static readonly DarknessAttackOwnerKey Owner = new("1", 0, SessionId);

    [Theory]
    [InlineData(0, "environment-light.base-white-confirmed")]
    [InlineData(1, "environment-light.local-light-coverage-unverified")]
    [InlineData(0, "environment-light.night-vision-active-confirmed")]
    public void Lit_dim_and_night_vision_do_not_start(
        int rawLevel,
        string reason
    )
    {
        var fixture = new Fixture(5);
        var level = (EnvironmentLightLevel)rawLevel;

        var result = fixture.Machine.Observe(
            Observation(
                Owner,
                revision: 1,
                level,
                authorized: false,
                lightReason: reason
            )
        );

        Assert.Equal(DarknessAttackMutationStatus.NoChange, result.Status);
        Assert.False(fixture.Machine.TryGetSnapshot(Owner, out _));
        Assert.Equal(0, fixture.Random.CallCount);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void Authorized_pitch_black_starts_once_at_initial_inclusive_bounds(int sampled)
    {
        var fixture = new Fixture(sampled);

        var result = fixture.Machine.Observe(Observation(Owner, 1));

        Assert.Equal(DarknessAttackMutationStatus.Applied, result.Status);
        Assert.Equal(DarknessAttackPromptKind.EnteredDarkness, result.Prompt);
        Assert.Equal(DarknessWarningClaimAction.None, result.WarningClaimAction);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal(DarknessAttackOwnerState.Countdown, snapshot.State);
        Assert.Equal(sampled, snapshot.SampledSeconds);
        Assert.Equal(sampled, snapshot.RemainingSeconds);
        Assert.Equal("initial-inclusive-5-10", snapshot.RngBranch);
        Assert.Equal("request-1", snapshot.RequestId);
        Assert.Equal(1, fixture.Random.CallCount);
        Assert.Equal(1, fixture.RequestIds.CallCount);
    }

    [Fact]
    public void Warning_uses_metadata_lead_and_repeated_revisions_never_replay()
    {
        var fixture = new Fixture(5);
        fixture.Machine.Observe(Observation(Owner, 1));
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(4.5d);
        Assert.Equal(
            DarknessWarningClaimAction.None,
            fixture.Machine.Observe(Observation(Owner, 2)).WarningClaimAction
        );

        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(0.03d);
        var warning = fixture.Machine.Observe(Observation(Owner, 3));
        Assert.Equal(DarknessAttackPromptKind.Warning, warning.Prompt);
        Assert.Equal(DarknessWarningClaimAction.Activate, warning.WarningClaimAction);
        Assert.Equal("request-1", warning.WarningRequestId);
        Assert.Null(warning.ExpiryIntent);

        var duplicate = fixture.Machine.Observe(Observation(Owner, 3));
        Assert.Equal(DarknessAttackMutationStatus.IgnoredDuplicate, duplicate.Status);
        Assert.Equal(DarknessWarningClaimAction.None, duplicate.WarningClaimAction);
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(0.01d);
        var later = fixture.Machine.Observe(Observation(Owner, 4));
        Assert.Equal(DarknessWarningClaimAction.None, later.WarningClaimAction);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal(DarknessAttackOwnerState.Warned, snapshot.State);
        Assert.True(snapshot.WarningClaimActive);
        Assert.Equal(0.48d, snapshot.WarningLeadSeconds, 6);
    }

    [Fact]
    public void Expiry_emits_one_stable_intent_and_waiting_never_resends()
    {
        var fixture = new Fixture(5);
        fixture.Machine.Observe(Observation(Owner, 1));
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(5d);

        var expired = fixture.Machine.Observe(Observation(Owner, 2));

        Assert.Equal(DarknessWarningClaimAction.Activate, expired.WarningClaimAction);
        Assert.Equal(DarknessAttackPromptKind.Warning, expired.Prompt);
        var intent = Assert.IsType<DarknessAttackExpiryIntent>(expired.ExpiryIntent);
        Assert.Equal("request-1", intent.RequestId);
        Assert.Equal(DarknessAttackContract.ContractVersion, intent.ContractVersion);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal(DarknessAttackOwnerState.ExpiredAwaitingReceipt, snapshot.State);

        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(20d);
        var waiting = fixture.Machine.Observe(Observation(Owner, 3));
        Assert.Equal(DarknessAttackMutationStatus.NoChange, waiting.Status);
        Assert.Null(waiting.ExpiryIntent);
        Assert.Equal(1, fixture.Random.CallCount);
        Assert.Equal(1, fixture.RequestIds.CallCount);
    }

    [Theory]
    [InlineData(0, 5, "damage-receipt-applied")]
    [InlineData(1, 11, "damage-rejected-explainable")]
    public void Explainable_receipt_starts_one_repeat_cycle_at_inclusive_bounds(
        int rawDisposition,
        int repeatSeconds,
        string reason
    )
    {
        var fixture = new Fixture(5, repeatSeconds);
        Expire(fixture);
        var disposition = (DarknessAttackReceiptDisposition)rawDisposition;
        var receipt = new DarknessAttackReceipt(Owner, "request-1", disposition, reason);

        var result = fixture.Machine.CompleteReceipt(Observation(Owner, 3), receipt);

        Assert.Equal(DarknessAttackMutationStatus.Applied, result.Status);
        Assert.Equal(DarknessWarningClaimAction.Release, result.WarningClaimAction);
        Assert.Equal("request-1", result.WarningRequestId);
        Assert.Equal(DarknessAttackPromptKind.None, result.Prompt);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal(DarknessAttackOwnerState.Countdown, snapshot.State);
        Assert.Equal(repeatSeconds, snapshot.SampledSeconds);
        Assert.Equal("repeat-inclusive-5-11", snapshot.RngBranch);
        Assert.Equal("request-2", snapshot.RequestId);
        Assert.Equal(2, fixture.Random.CallCount);

        var duplicate = fixture.Machine.CompleteReceipt(Observation(Owner, 4), receipt);
        Assert.Equal(DarknessAttackMutationStatus.NoChange, duplicate.Status);
        Assert.Equal(2, fixture.Random.CallCount);
    }

    [Fact]
    public void Unexplained_rejection_fails_closed_and_keeps_waiting()
    {
        var fixture = new Fixture(5, 11);
        Expire(fixture);

        var result = fixture.Machine.CompleteReceipt(
            Observation(Owner, 3),
            new DarknessAttackReceipt(
                Owner,
                "request-1",
                DarknessAttackReceiptDisposition.Rejected,
                string.Empty
            )
        );

        Assert.Equal(DarknessAttackMutationStatus.Invalid, result.Status);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal(DarknessAttackOwnerState.ExpiredAwaitingReceipt, snapshot.State);
        Assert.Equal(1, fixture.Random.CallCount);
    }

    [Fact]
    public void Light_cancels_warning_with_D_and_reentry_gets_a_new_initial_cycle()
    {
        var fixture = new Fixture(5, 10);
        fixture.Machine.Observe(Observation(Owner, 1));
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(4.53d);
        var warning = fixture.Machine.Observe(Observation(Owner, 2));
        Assert.Equal(DarknessWarningClaimAction.Activate, warning.WarningClaimAction);

        var cancelled = fixture.Machine.Observe(
            Observation(
                Owner,
                3,
                EnvironmentLightLevel.Lit,
                authorized: false,
                lightReason: "environment-light.base-white-confirmed"
            )
        );
        Assert.Equal(DarknessAttackPromptKind.EscapedDarkness, cancelled.Prompt);
        Assert.Equal(DarknessWarningClaimAction.Release, cancelled.WarningClaimAction);
        Assert.Equal("request-1", cancelled.WarningRequestId);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var inactive));
        Assert.Equal(DarknessAttackOwnerState.Inactive, inactive.State);
        Assert.Equal(string.Empty, inactive.RequestId);

        var reentered = fixture.Machine.Observe(Observation(Owner, 4));
        Assert.Equal(DarknessAttackPromptKind.EnteredDarkness, reentered.Prompt);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var restarted));
        Assert.Equal(10, restarted.SampledSeconds);
        Assert.Equal("initial-inclusive-5-10", restarted.RngBranch);
        Assert.Equal("request-2", restarted.RequestId);
    }

    [Fact]
    public void Menu_dialogue_or_pause_retains_progress_while_event_cancels()
    {
        var fixture = new Fixture(10);
        fixture.Machine.Observe(Observation(Owner, 1));
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(8d);

        var paused = fixture.Machine.Observe(
            Observation(Owner, 2) with { Paused = true }
        );

        Assert.Equal(DarknessAttackMutationStatus.NoChange, paused.Status);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var retained));
        Assert.Equal(10d, retained.RemainingSeconds);

        var eventResult = fixture.Machine.Observe(
            Observation(Owner, 3) with
            {
                GameplaySettleable = false,
                Paused = false,
                UnsettleableReason = "darkness.state.event-active",
            }
        );
        Assert.Equal(DarknessAttackPromptKind.EscapedDarkness, eventResult.Prompt);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var cancelled));
        Assert.Equal(DarknessAttackOwnerState.Inactive, cancelled.State);
        Assert.Equal("darkness.state.event-active", cancelled.CancelReason);
    }

    [Fact]
    public void Client_observation_cannot_advance_host_state()
    {
        var fixture = new Fixture(5);

        var result = fixture.Machine.Observe(
            Observation(Owner, 1) with { AuthorityRole = SanityAuthorityRole.Client }
        );

        Assert.Equal(DarknessAttackMutationStatus.RequiresHostAuthority, result.Status);
        Assert.False(fixture.Machine.TryGetSnapshot(Owner, out _));
        Assert.Equal(0, fixture.Random.CallCount);
    }

    [Fact]
    public void Invalid_rng_fails_closed_before_any_request_id_is_created()
    {
        var fixture = new Fixture(4);

        var result = fixture.Machine.Observe(Observation(Owner, 1));

        Assert.Equal(DarknessAttackMutationStatus.Invalid, result.Status);
        Assert.Equal("darkness.rng.out-of-range", result.Reason);
        Assert.Equal(1, fixture.Random.CallCount);
        Assert.Equal(0, fixture.RequestIds.CallCount);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal(DarknessAttackOwnerState.Inactive, snapshot.State);
    }

    [Fact]
    public void Warp_cancel_releases_warning_and_removed_owner_reenters_as_new_initial_cycle()
    {
        var fixture = new Fixture(5, 10);
        fixture.Machine.Observe(Observation(Owner, 1));
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(4.53d);
        fixture.Machine.Observe(Observation(Owner, 2));

        var cancelled = fixture.Machine.Cancel(Owner, "darkness.state.owner-warped");

        Assert.Equal(DarknessAttackPromptKind.EscapedDarkness, cancelled.Prompt);
        Assert.Equal(DarknessWarningClaimAction.Release, cancelled.WarningClaimAction);
        Assert.Equal("request-1", cancelled.WarningRequestId);
        Assert.True(fixture.Machine.Remove(Owner));
        var reentered = fixture.Machine.Observe(Observation(Owner, 3));
        Assert.Equal(DarknessAttackPromptKind.EnteredDarkness, reentered.Prompt);
        Assert.True(fixture.Machine.TryGetSnapshot(Owner, out var snapshot));
        Assert.Equal("initial-inclusive-5-10", snapshot.RngBranch);
        Assert.Equal(10, snapshot.SampledSeconds);
        Assert.Equal("request-2", snapshot.RequestId);
    }

    [Fact]
    public void Owner_state_is_bounded_and_clear_remove_are_session_safe()
    {
        var fixture = new Fixture(5);
        for (var index = 0; index < DarknessAttackContract.MaximumOwnerStates; index++)
        {
            var key = new DarknessAttackOwnerKey((index + 1).ToString(), index, SessionId);
            Assert.Equal(
                DarknessAttackMutationStatus.Applied,
                fixture.Machine.Observe(Observation(key, 1)).Status
            );
        }
        var overflow = new DarknessAttackOwnerKey("99", 99, SessionId);
        Assert.Equal(
            DarknessAttackMutationStatus.CapacityExceeded,
            fixture.Machine.Observe(Observation(overflow, 1)).Status
        );

        Assert.Equal(1, fixture.Machine.RemoveOwner("1"));
        Assert.Equal(
            DarknessAttackMutationStatus.Applied,
            fixture.Machine.Observe(Observation(overflow, 2)).Status
        );
        fixture.Machine.Clear();
        Assert.False(fixture.Machine.TryGetSnapshot(overflow, out _));
    }

    [Fact]
    public void Two_split_screen_warning_claims_share_one_physical_instance_until_last_release()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        var first = WarningClaim("1", 0, "request-a", 10);
        var second = WarningClaim("2", 1, "request-b", 20);

        Assert.Equal(
            SanityAudioClaimUpdateStatus.Applied,
            coordinator.SubmitDarknessWarningClaim(first).Status
        );
        Assert.Equal(
            SanityAudioClaimUpdateStatus.Applied,
            coordinator.SubmitDarknessWarningClaim(second).Status
        );
        Assert.True(output.DarknessWarningPhysical);
        Assert.Equal(1, output.DarknessWarningActivationCount);
        Assert.Equal(2, coordinator.Snapshot().DarknessWarningClaims.Count);
        Assert.Equal(1, coordinator.Snapshot().PhysicalInstanceCount);

        Assert.Equal(
            SanityAudioClaimUpdateStatus.Removed,
            coordinator.RemoveDarknessWarningClaim("1", 0, SessionId, "request-a").Status
        );
        Assert.True(output.DarknessWarningPhysical);
        Assert.Equal(
            SanityAudioClaimUpdateStatus.NoChange,
            coordinator.RemoveDarknessWarningClaim("2", 1, SessionId, "old-request").Status
        );
        Assert.True(output.DarknessWarningPhysical);
        Assert.Equal(
            SanityAudioClaimUpdateStatus.Removed,
            coordinator.RemoveDarknessWarningClaim("2", 1, SessionId, "request-b").Status
        );
        Assert.False(output.DarknessWarningPhysical);
        Assert.Equal(1, output.DarknessWarningReleaseCount);
    }

    [Fact]
    public void Warning_claim_duplicate_stale_and_correlation_drift_never_replay()
    {
        var output = new FakeProcessOutput();
        using var coordinator = new SanityProcessAudioCoordinator(output);
        var claim = WarningClaim("1", 0, "request-a", 10);
        coordinator.SubmitDarknessWarningClaim(claim);

        Assert.Equal(
            SanityAudioClaimUpdateStatus.IgnoredDuplicate,
            coordinator.SubmitDarknessWarningClaim(claim).Status
        );
        Assert.Equal(
            SanityAudioClaimUpdateStatus.IgnoredStale,
            coordinator.SubmitDarknessWarningClaim(claim with { Revision = 9 }).Status
        );
        Assert.Equal(
            SanityAudioClaimUpdateStatus.Invalid,
            coordinator.SubmitDarknessWarningClaim(
                claim with { RequestId = "request-drift" }
            ).Status
        );
        Assert.Equal(1, output.DarknessWarningActivationCount);
        Assert.Single(coordinator.Snapshot().DarknessWarningClaims);
    }

    [Fact]
    public void Darkness_warning_lane_plays_one_borrowed_effect_and_cancel_disposes_instance()
    {
        var effect = new FakeEffect();
        using var lane = new SanityAudioInstanceLane(
            SanityAudioLaneKind.DarknessWarning,
            new[] { effect },
            continuous: false,
            new FixedAudioRandom(),
            new OwningThreadContext(),
            diagnosticSink: null
        );

        lane.TriggerOneShot(0.75f);

        var instance = Assert.Single(effect.Instances);
        Assert.Equal(1, instance.PlayCount);
        Assert.Equal(SanityAudioPlaybackState.Playing, instance.State);
        Assert.Equal(0.75f, instance.Volume);
        lane.StopPlayback();
        Assert.Equal(SanityAudioPlaybackState.Stopped, instance.State);
        Assert.True(instance.Disposed);
        Assert.Equal(0, lane.PhysicalInstanceCount);
    }

    [Fact]
    public void Final_warning_metadata_is_available_without_physical_load()
    {
        var factory = new CountingPhysicalFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var result = loader.GetCueMetadata(DarknessAttackContract.WarningCueId);

        Assert.True(result.Success, result.Diagnostic.Reason);
        Assert.False(result.Diagnostic.IsPlaceholder);
        var cue = Assert.IsType<SanityCueDefinition>(result.Cue);
        Assert.Equal("CancelableOneShot", cue.PlaybackMode);
        Assert.Equal(4, cue.Clips.Count);
        Assert.Equal(99451, cue.Clips[0].DurationFrames);
        Assert.Equal(2.255125d, cue.Clips[0].DurationSeconds, 6);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, loader.Snapshot().PhysicalResourceCount);
    }

    [Fact]
    public void Runtime_uses_game_time_and_keeps_settlement_consumer_external()
    {
        var runtimeSource = File.ReadAllText(RuntimeContractPath);
        var stateSource = File.ReadAllText(StateMachineContractPath);
        var audioSource = File.ReadAllText(AudioContractPath);

        Assert.Contains("Game1.currentGameTime.ElapsedGameTime", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", stateSource, StringComparison.Ordinal);
        Assert.Contains("ExpiryIntentCreated", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NonLethalDamageService", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyDamageUpToFloor", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReduceToFloor", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SanityChangeService", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Game1.paused", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Game1.activeClickableMenu", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Game1.dialogueUp", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("lifecycle.IsEventCoverageActive", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Game1.isWarping", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Game1.currentMinigame", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("SanityStateEventKind.SystemDisabled", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("SanityStateEventKind.OwnerInvalidated", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("OnSessionClearing", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("allowPlaceholder", audioSource, StringComparison.Ordinal);
        Assert.Contains("DarknessWarningCueSetId", audioSource, StringComparison.Ordinal);
        Assert.Equal(5, SanityProcessAudioCoordinator.MaximumPhysicalInstances);
    }

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string RuntimeContractPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarknessAttack",
            "SmapiDarknessAttackService.cs"
        );

    private static string StateMachineContractPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarknessAttack",
            "DarknessAttackStateMachine.cs"
        );

    private static string AudioContractPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "AudioPool",
            "SanitySmapiAudioService.cs"
        );

    private static DarknessAttackObservation Observation(
        DarknessAttackOwnerKey key,
        long revision,
        EnvironmentLightLevel level = EnvironmentLightLevel.PitchBlack,
        bool authorized = true,
        string lightReason = "environment-light.pitch-black-confirmed"
    ) => new(
        key,
        revision,
        SanityAuthorityRole.Host,
        GameplaySettleable: true,
        Paused: false,
        level,
        EnvironmentLightEvidenceStatus.Confirmed,
        authorized,
        lightReason,
        string.Empty
    );

    private static SanityDarknessWarningClaim WarningClaim(
        string playerKey,
        int screenId,
        string requestId,
        long revision
    ) => new(playerKey, screenId, SessionId, requestId, revision);

    private static void Expire(Fixture fixture)
    {
        fixture.Machine.Observe(Observation(Owner, 1));
        fixture.Clock.ElapsedGameTime = TimeSpan.FromSeconds(5d);
        var result = fixture.Machine.Observe(Observation(Owner, 2));
        Assert.NotNull(result.ExpiryIntent);
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(params int[] randomValues)
        {
            Clock = new FakeClock();
            Random = new SequenceRandom(randomValues);
            RequestIds = new SequenceRequestIds();
            Machine = new DarknessAttackStateMachine(
                Clock,
                Random,
                RequestIds,
                warningLeadSeconds: 0.48d
            );
        }

        internal FakeClock Clock { get; }

        internal SequenceRandom Random { get; }

        internal SequenceRequestIds RequestIds { get; }

        internal DarknessAttackStateMachine Machine { get; }

        public void Dispose() => Machine.Dispose();
    }

    private sealed class FakeClock : IDarknessAttackClock
    {
        public TimeSpan ElapsedGameTime { get; set; }
    }

    private sealed class SequenceRandom : IDarknessAttackRandom
    {
        private readonly int[] values;

        internal SequenceRandom(params int[] values)
        {
            this.values = values.Length == 0 ? new[] { 5 } : values;
        }

        internal int CallCount { get; private set; }

        public int NextInclusive(int minimum, int maximum)
        {
            var index = Math.Min(CallCount, values.Length - 1);
            CallCount++;
            return values[index];
        }
    }

    private sealed class SequenceRequestIds : IDarknessAttackRequestIdSource
    {
        internal int CallCount { get; private set; }

        public string NextRequestId(DarknessAttackOwnerKey key)
        {
            CallCount++;
            return $"request-{CallCount}";
        }
    }

    private sealed class FakeProcessOutput : ISanityProcessAudioOutput
    {
        internal bool DarknessWarningPhysical { get; private set; }

        internal int DarknessWarningActivationCount { get; private set; }

        internal int DarknessWarningReleaseCount { get; private set; }

        public int PhysicalInstanceCount => DarknessWarningPhysical ? 1 : 0;

        public void SetPoolActive(SanityAudioLaneKind lane, bool active) { }

        public void SetDangerActive(bool active) { }

        public void TriggerDanger() { }

        public void SetDarknessWarningActive(bool active)
        {
            if (active && !DarknessWarningPhysical)
                DarknessWarningActivationCount++;
            if (!active && DarknessWarningPhysical)
                DarknessWarningReleaseCount++;
            DarknessWarningPhysical = active;
        }

        public void SetPaused(bool paused) { }

        public void SetContinuousPoolsPaused(bool paused) { }

        public void SetSuspended(bool suspended) { }

        public void SetSpecialEventAudioAllowed(bool allowed) { }

        public void InvalidateResources() { }

        public void Tick() { }

        public void Clear() => DarknessWarningPhysical = false;

        public void Dispose() => DarknessWarningPhysical = false;
    }

    private sealed class CountingPhysicalFactory : ISanityPhysicalResourceFactory
    {
        internal int CreateCount { get; private set; }

        public SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes)
        {
            CreateCount++;
            return SanityPhysicalResourceCreationResult.Failed("test.unexpected", path);
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes)
        {
            CreateCount++;
            return SanityPhysicalResourceCreationResult.Failed("test.unexpected", path);
        }
    }

    private sealed class FixedAudioRandom : ISanityAudioRandom
    {
        public int NextIndex(int exclusiveUpperBound) => 0;
    }

    private sealed class OwningThreadContext : ISanityAudioThreadContext
    {
        public bool IsOnOwningThread => true;
    }

    private sealed class FakeEffect : ISanityAudioEffect
    {
        public string ResourceId => "placeholder-warning";

        internal List<FakeInstance> Instances { get; } = new();

        public ISanityAudioInstance CreateInstance()
        {
            var instance = new FakeInstance();
            Instances.Add(instance);
            return instance;
        }
    }

    private sealed class FakeInstance : ISanityAudioInstance
    {
        public SanityAudioPlaybackState State { get; private set; } =
            SanityAudioPlaybackState.Stopped;

        public float Volume { get; set; }

        internal int PlayCount { get; private set; }

        internal bool Disposed { get; private set; }

        public void Play()
        {
            PlayCount++;
            State = SanityAudioPlaybackState.Playing;
        }

        public void Pause() => State = SanityAudioPlaybackState.Paused;

        public void Resume() => State = SanityAudioPlaybackState.Playing;

        public void Stop() => State = SanityAudioPlaybackState.Stopped;

        public void Dispose() => Disposed = true;
    }
}
