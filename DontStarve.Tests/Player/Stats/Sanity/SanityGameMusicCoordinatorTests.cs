using DontStarve.Player.Stats.Sanity.Audio;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityGameMusicCoordinatorTests
{
    [Fact]
    public void Capability_records_exact_patch_and_release_contract()
    {
        var capability = SanityGameMusicCapability.Available(
            "Yurin.DontStarve.Sanity4.MusicSuppression",
            "1.6.15",
            "StardewValley.Game1.updateMusic():System.Void"
        );

        Assert.Equal("sanity.game-music-suppression", capability.Capability);
        Assert.Equal(SanityGameMusicCapabilityStatus.Available, capability.Status);
        Assert.True(capability.PatchInstalled);
        Assert.True(capability.MiniJukeboxIsAlwaysExempt);
        Assert.False(capability.IslandIsBlanketExempt);
        Assert.False(capability.ControlsIndependentAudio);
        Assert.True(capability.OwnsDawnDuskLifecycle);
        Assert.False(capability.RestoresInterruptedTrack);
        Assert.Equal("original-reselect-current-state", capability.ReleasePolicy);
    }

    [Fact]
    public void Capability_gate_accepts_only_current_version_and_exact_target_shape()
    {
        Assert.Null(SanityGameMusicCapabilityGate.ValidateVersion("1.6.15"));
        Assert.Null(
            SanityGameMusicCapabilityGate.ValidateTarget(
                methodFound: true,
                isStatic: true,
                returnsVoid: true,
                parameterCount: 0
            )
        );

        Assert.Equal(
            "music.patch.game-version-mismatch",
            SanityGameMusicCapabilityGate.ValidateVersion("1.6.16")
        );
        Assert.Equal(
            "music.patch.target-signature-mismatch",
            SanityGameMusicCapabilityGate.ValidateTarget(
                methodFound: false,
                isStatic: true,
                returnsVoid: true,
                parameterCount: 0
            )
        );
        Assert.Equal(
            "music.patch.target-signature-mismatch",
            SanityGameMusicCapabilityGate.ValidateTarget(
                methodFound: true,
                isStatic: false,
                returnsVoid: true,
                parameterCount: 0
            )
        );
        Assert.Equal(
            "music.patch.target-signature-mismatch",
            SanityGameMusicCapabilityGate.ValidateTarget(
                methodFound: true,
                isStatic: true,
                returnsVoid: false,
                parameterCount: 0
            )
        );
        Assert.Equal(
            "music.patch.target-signature-mismatch",
            SanityGameMusicCapabilityGate.ValidateTarget(
                methodFound: true,
                isStatic: true,
                returnsVoid: true,
                parameterCount: 1
            )
        );
    }

    [Fact]
    public void Unique_process_winner_drives_suppression_without_a_second_claim_authority()
    {
        using var process = new SanityProcessAudioCoordinator(new NoOpProcessOutput());
        process.SubmitClaim(Claim("10", 1, 1, 0.1d, danger: true));
        process.SubmitClaim(Claim("2", 0, 1, 0.1d, danger: true));
        var adapter = new FakeMusicAdapter();
        using var music = new SanityGameMusicCoordinator(adapter);

        var decision = music.Reconcile(process.MusicState(), false, false);

        Assert.Equal(SanityGameMusicDecisionKind.Suppressed, decision.Kind);
        Assert.True(decision.WantsSuppression);
        Assert.True(decision.SuppressionApplied);
        Assert.Equal("2", decision.Winner?.PlayerKey);
        Assert.Equal(0, decision.Winner?.ScreenId);
        Assert.Equal(1, adapter.ApplyCount);
    }

    [Fact]
    public void Winner_outside_danger_releases_suppression()
    {
        using var process = new SanityProcessAudioCoordinator(new NoOpProcessOutput());
        var adapter = new FakeMusicAdapter();
        using var music = new SanityGameMusicCoordinator(adapter);
        process.SubmitClaim(Claim("1", 0, 1, 0.1d, danger: true));
        music.Reconcile(process.MusicState(), false, false);

        process.SubmitClaim(Claim("1", 0, 2, 0.2d, ambience: true, whispers: true));
        var released = music.Reconcile(process.MusicState(), false, false);

        Assert.Equal(SanityGameMusicDecisionKind.WinnerOutsideDanger, released.Kind);
        Assert.False(released.WantsSuppression);
        Assert.False(released.SuppressionApplied);
        Assert.Equal(1, adapter.ReleaseCount);
    }

    [Fact]
    public void Any_local_jukebox_is_process_exempt_and_exit_reclaims_current_danger()
    {
        using var process = DangerProcess();
        var adapter = new FakeMusicAdapter();
        using var music = new SanityGameMusicCoordinator(adapter);
        music.Reconcile(process.MusicState(), false, false);

        var exempt = music.Reconcile(process.MusicState(), true, false);
        Assert.Equal(SanityGameMusicDecisionKind.MiniJukeboxExempt, exempt.Kind);
        Assert.False(exempt.SuppressionApplied);

        var reclaimed = music.Reconcile(process.MusicState(), false, false);
        Assert.Equal(SanityGameMusicDecisionKind.Suppressed, reclaimed.Kind);
        Assert.True(reclaimed.SuppressionApplied);
        Assert.Equal(2, adapter.ApplyCount);
        Assert.Equal(1, adapter.ReleaseCount);
    }

    [Fact]
    public void Island_is_observed_but_never_a_blanket_exemption()
    {
        using var process = DangerProcess();
        var adapter = new FakeMusicAdapter();
        using var music = new SanityGameMusicCoordinator(adapter);

        var decision = music.Reconcile(process.MusicState(), false, true);

        Assert.Equal(SanityGameMusicDecisionKind.Suppressed, decision.Kind);
        Assert.True(decision.IslandContext);
        Assert.True(decision.SuppressionApplied);
    }

    [Theory]
    [InlineData(true, false, (int)SanityGameMusicDecisionKind.ProcessPaused)]
    [InlineData(false, true, (int)SanityGameMusicDecisionKind.EventSuspended)]
    public void Global_pause_or_event_suspension_releases_without_dropping_owner(
        bool processPaused,
        bool eventSuspended,
        int expectedKind
    )
    {
        using var process = DangerProcess();
        var adapter = new FakeMusicAdapter();
        using var music = new SanityGameMusicCoordinator(adapter);
        music.Reconcile(process.MusicState(), false, false);

        process.SetProcessPaused(processPaused);
        process.SetEventSuspended(eventSuspended);
        var decision = music.Reconcile(process.MusicState(), false, false);

        Assert.Equal((SanityGameMusicDecisionKind)expectedKind, decision.Kind);
        Assert.False(decision.SuppressionApplied);
        Assert.NotNull(decision.Winner);
        Assert.Single(process.Snapshot().Claims);
    }

    [Fact]
    public void Unavailable_adapter_reports_stable_reason_and_never_claims_physical_success()
    {
        using var process = DangerProcess();
        var adapter = new FakeMusicAdapter(available: false);
        using var music = new SanityGameMusicCoordinator(adapter);

        var decision = music.Reconcile(process.MusicState(), false, false);

        Assert.Equal(SanityGameMusicDecisionKind.AdapterUnavailable, decision.Kind);
        Assert.Equal("music.patch.target-signature-mismatch", decision.Reason);
        Assert.True(decision.WantsSuppression);
        Assert.False(decision.SuppressionApplied);
        Assert.Equal(0, adapter.ApplyCount);
    }

    [Fact]
    public void Physical_failure_is_explicit_and_does_not_claim_suppression()
    {
        using var process = DangerProcess();
        var adapter = new FakeMusicAdapter { FailNextSet = true };
        using var music = new SanityGameMusicCoordinator(adapter);

        var decision = music.Reconcile(process.MusicState(), false, false);

        Assert.Equal(SanityGameMusicDecisionKind.PhysicalFailure, decision.Kind);
        Assert.Equal("music.test.physical-failure", decision.Reason);
        Assert.False(decision.SuppressionApplied);
    }

    [Fact]
    public void Independent_sound_effect_or_audio_engine_is_explicitly_out_of_scope()
    {
        var adapter = new FakeMusicAdapter();
        using var music = new SanityGameMusicCoordinator(adapter);

        var decision = music.EvaluateIndependentAudio();

        Assert.Equal(SanityGameMusicDecisionKind.IndependentAudioOutOfScope, decision.Kind);
        Assert.False(decision.WantsSuppression);
        Assert.False(decision.SuppressionApplied);
    }

    [Fact]
    public void Clear_and_dispose_release_once_and_are_idempotent()
    {
        using var process = DangerProcess();
        var adapter = new FakeMusicAdapter();
        var music = new SanityGameMusicCoordinator(adapter);
        music.Reconcile(process.MusicState(), false, false);

        music.Clear();
        music.Clear();
        Assert.False(music.Snapshot().SuppressionApplied);
        Assert.Equal(1, adapter.ReleaseCount);

        music.Dispose();
        music.Dispose();
        Assert.True(music.Snapshot().Disposed);
        Assert.Equal(1, adapter.DisposeCount);
    }

    [Fact]
    public void Production_adapter_is_exact_version_gated_and_unpatches_only_owned_prefix()
    {
        var source = ReadSource("GameMusic", "SanityGameMusicRuntimeAdapter.cs");
        var policy = ReadSource("GameMusic", "SanityGameMusicCoordinator.cs");

        Assert.Contains("ExpectedGameVersion = \"1.6.15\"", policy, StringComparison.Ordinal);
        Assert.Contains("SanityGameMusicCapabilityGate.ValidateVersion", source, StringComparison.Ordinal);
        Assert.Contains("SanityGameMusicCapabilityGate.ValidateTarget", source, StringComparison.Ordinal);
        Assert.Contains("AccessTools.DeclaredMethod(", source, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.updateMusic)", source, StringComparison.Ordinal);
        Assert.Contains("Type.EmptyTypes", source, StringComparison.Ordinal);
        Assert.Contains("Priority.First", source, StringComparison.Ordinal);
        Assert.Contains("IsOwnedPrefixInstalled", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Prefix", source, StringComparison.Ordinal);
        Assert.Contains("patchOwnerId", source, StringComparison.Ordinal);
        Assert.Contains("Game1.requestedMusicDirty = true", source, StringComparison.Ordinal);
        Assert.Contains("Game1.currentSong = null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UnpatchAll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.changeMusicTrack(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("musicVolumeLevel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dawn_and_dusk_join_suppression_without_resuming_crossed_cues()
    {
        var manager = ReadSource("AudioVisualFact", "MusicManager.cs");
        var dawn = ReadSource("AudioVisualFact", "DawnMusicService.cs");
        var dusk = ReadSource("AudioVisualFact", "DuskMusicService.cs");

        Assert.Contains("SetSanityMusicSuppressed", manager, StringComparison.Ordinal);
        Assert.Contains("DawnMusicService.SetSuppressed", manager, StringComparison.Ordinal);
        Assert.Contains("DuskMusicService.SetSuppressed", manager, StringComparison.Ordinal);
        Assert.Contains("allowVanillaReselect: false", dawn, StringComparison.Ordinal);
        Assert.Contains("if (_suppressed)", dawn, StringComparison.Ordinal);
        Assert.Contains("if (_suppressed)", dusk, StringComparison.Ordinal);
        Assert.DoesNotContain("Resume()", dawn, StringComparison.Ordinal);
        Assert.DoesNotContain("Resume()", dusk, StringComparison.Ordinal);
    }

    [Fact]
    public void Smapi_service_consumes_unique_music_state_and_keeps_policy_dimensions_separate()
    {
        var source = ReadSource("AudioPool", "SanitySmapiAudioService.cs");
        var coordinatorSource = ReadSource("GameMusic", "SanityGameMusicCoordinator.cs");

        Assert.Contains("coordinator.MusicState()", source, StringComparison.Ordinal);
        Assert.Contains("location.IsMiniJukeboxPlaying()", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.SetClaimPlaybackPaused", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.SetProcessPaused", source, StringComparison.Ordinal);
        Assert.Contains("miniJukeboxUnknownScreens", source, StringComparison.Ordinal);
        Assert.Contains("!player.IsLocalPlayer", source, StringComparison.Ordinal);
        Assert.Contains("screenId = Context.ScreenId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gameMusicCoordinator.SubmitObservation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gameMusicCoordinator.RemoveClaim", source, StringComparison.Ordinal);
        Assert.DoesNotContain("helper.Multiplayer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IComparer<", coordinatorSource, StringComparison.Ordinal);

        var updateStart = source.IndexOf(
            "private void OnUpdateTicked",
            StringComparison.Ordinal
        );
        var updateEnd = source.IndexOf(
            "private void OnDayEnding",
            updateStart,
            StringComparison.Ordinal
        );
        var update = source[updateStart..updateEnd];
        Assert.True(
            update.IndexOf("TryBindCurrentOwner", StringComparison.Ordinal)
            < update.IndexOf("UpdateCurrentScreenPlaybackContext", StringComparison.Ordinal)
        );
        Assert.True(
            update.IndexOf("UpdateCurrentScreenPlaybackContext", StringComparison.Ordinal)
            < update.IndexOf("SeedClaim", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Steady_music_tick_has_no_reflection_loading_linq_or_unbounded_collection_work()
    {
        var service = ReadSource("AudioPool", "SanitySmapiAudioService.cs");
        var adapter = ReadSource("GameMusic", "SanityGameMusicRuntimeAdapter.cs");
        var update = Slice(service, "private void OnUpdateTicked", "private void OnDayEnding");
        var adapterTick = Slice(adapter, "public SanityGameMusicPhysicalResult Tick()", "public void Dispose()");

        foreach (
            var forbidden in new[]
            {
                "AccessTools",
                "GetMethod(",
                "LoadAudioCueSet",
                "new List<",
                "new Dictionary<",
                ".ToList(",
                ".ToArray(",
                "monitor.Log",
            }
        )
        {
            Assert.DoesNotContain(forbidden, update, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, adapterTick, StringComparison.Ordinal);
        }
    }

    private static SanityProcessAudioCoordinator DangerProcess()
    {
        var process = new SanityProcessAudioCoordinator(new NoOpProcessOutput());
        process.SubmitClaim(Claim("1", 0, 1, 0.1d, danger: true));
        return process;
    }

    private static SanityAudioOwnerClaim Claim(
        string playerKey,
        int screenId,
        long revision,
        double ratio,
        bool ambience = true,
        bool whispers = true,
        bool danger = false
    ) => new(playerKey, screenId, revision, ratio, ambience, whispers, danger);

    private static string ReadSource(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private sealed class FakeMusicAdapter : ISanityGameMusicPhysicalAdapter
    {
        internal FakeMusicAdapter(bool available = true)
        {
            Capability = available
                ? SanityGameMusicCapability.Available(
                    "test.music",
                    "1.6.15",
                    "StardewValley.Game1.updateMusic():System.Void"
                )
                : SanityGameMusicCapability.Unavailable(
                    "test.music",
                    "1.6.15",
                    "StardewValley.Game1.updateMusic():System.Void",
                    "music.patch.target-signature-mismatch"
                );
        }

        public SanityGameMusicCapability Capability { get; }

        public bool SuppressionApplied { get; private set; }

        internal int ApplyCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal bool FailNextSet { get; set; }

        public SanityGameMusicPhysicalResult SetSuppression(bool suppress)
        {
            if (FailNextSet)
            {
                FailNextSet = false;
                SuppressionApplied = false;
                return new SanityGameMusicPhysicalResult(
                    false,
                    false,
                    "music.test.physical-failure"
                );
            }
            if (
                suppress
                && Capability.Status == SanityGameMusicCapabilityStatus.Unavailable
            )
            {
                return new SanityGameMusicPhysicalResult(
                    false,
                    false,
                    Capability.Reason
                );
            }
            if (SuppressionApplied == suppress)
            {
                return new SanityGameMusicPhysicalResult(
                    true,
                    suppress,
                    suppress
                        ? "music.test.already-applied"
                        : "music.test.already-released"
                );
            }

            SuppressionApplied = suppress;
            if (suppress)
                ApplyCount++;
            else
                ReleaseCount++;
            return new SanityGameMusicPhysicalResult(
                true,
                SuppressionApplied,
                suppress ? "music.test.applied" : "music.test.released"
            );
        }

        public SanityGameMusicPhysicalResult Tick()
        {
            return new SanityGameMusicPhysicalResult(
                true,
                SuppressionApplied,
                "music.test.tick"
            );
        }

        public void Dispose()
        {
            DisposeCount++;
            SuppressionApplied = false;
        }
    }

    private sealed class NoOpProcessOutput : ISanityProcessAudioOutput
    {
        public int PhysicalInstanceCount => 0;

        public void SetPoolActive(SanityAudioLaneKind lane, bool active) { }

        public void SetDangerActive(bool active) { }

        public void TriggerDanger() { }

        public void SetDarknessWarningActive(bool active) { }

        public void SetPaused(bool paused) { }

        public void SetSuspended(bool suspended) { }

        public void InvalidateResources() { }

        public void Tick() { }

        public void Clear() { }

        public void Dispose() { }
    }
}
