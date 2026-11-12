using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicRuntimeContractTests
{
    [Fact]
    public void ResourceRuntimeLoadsSharedEffectsAndCreatesIndependentInstances()
    {
        var source = ReadContract("ActivityMusicResourceRuntime.cs");

        Assert.Contains("SoundEffect.FromStream", source, StringComparison.Ordinal);
        Assert.Contains("soundEffect.CreateInstance()", source, StringComparison.Ordinal);
        Assert.Contains("PreloadAll", source, StringComparison.Ordinal);
        Assert.Contains("no replacement will be used", source, StringComparison.Ordinal);
        Assert.Contains("effects", source, StringComparison.Ordinal);
        Assert.Contains("effect.Dispose()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioRuntimeIsOwnerLocalAndDoesNotUseTheVanillaMusicChannel()
    {
        var source = ReadContract("ActivityMusicAudioService.cs");

        Assert.Contains("owner.IsLocalPlayer", source, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicFadePolicy.Advance", source, StringComparison.Ordinal);
        Assert.Contains("retiringPlaybacks", source, StringComparison.Ordinal);
        Assert.Contains("GetFadeInSeconds(state.Kind) <= 0f", source, StringComparison.Ordinal);
        Assert.Contains("TryReviveRetiringPlayback", source, StringComparison.Ordinal);
        Assert.Contains("RevivePlayback", source, StringComparison.Ordinal);
        Assert.Contains("IsSameTrack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAttachWindowFocusEvents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowInactive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("windowInactive", source, StringComparison.Ordinal);
        Assert.Contains("StopAndDispose", source, StringComparison.Ordinal);
        Assert.Contains("IsMiniJukeboxPlaying", source, StringComparison.Ordinal);
        Assert.Contains("Game1.CurrentEvent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.currentSong", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Activity_audio_has_no_native_sanity_volume_gate()
    {
        var audio = ReadContract("ActivityMusicAudioService.cs");
        var service = ReadContract("ActivityMusicService.cs");

        Assert.DoesNotContain("sanityWorkMusicSuppressed", audio, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSanityWorkMusicSuppressed", audio, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSanityWorkMusicSuppressed", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Activity_cleanup_uses_selective_work_farming_stop_instead_of_zeroing_volume()
    {
        var source = ReadContract("ActivityMusicAudioService.cs");

        Assert.Contains("StopWorkAndFarming", source, StringComparison.Ordinal);
        Assert.DoesNotContain("playback.CurrentVolume = 0f", source, StringComparison.Ordinal);
        Assert.DoesNotContain("playback.Instance.Volume = 0f", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Insane_cooldown_blocks_new_work_and_farming_records()
    {
        var source = ReadContract("ActivityMusicService.cs");
        var workStart = source.IndexOf(
            "internal static void RecordWork(Farmer? owner, GameLocation? location",
            StringComparison.Ordinal
        );
        var farmingStart = source.IndexOf(
            "internal static void RecordFarming",
            workStart,
            StringComparison.Ordinal
        );
        var combatStart = source.IndexOf(
            "internal static void RecordCombat",
            farmingStart,
            StringComparison.Ordinal
        );

        Assert.True(workStart >= 0);
        Assert.True(farmingStart > workStart);
        Assert.True(combatStart > farmingStart);
        Assert.Contains(
            "IsActivitySuppressedByInsane(owner)",
            source[workStart..farmingStart],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "IsActivitySuppressedByInsane(owner)",
            source[farmingStart..combatStart],
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Selective_work_farming_cleanup_uses_the_existing_fade_path()
    {
        var source = ReadContract("ActivityMusicAudioService.cs");
        var start = source.IndexOf(
            "internal static void StopWorkAndFarming",
            StringComparison.Ordinal
        );
        var end = source.IndexOf(
            "internal static void FadeOutOwner",
            start,
            StringComparison.Ordinal
        );

        Assert.True(start >= 0);
        Assert.True(end > start);
        var method = source[start..end];
        Assert.Contains("BeginFadeOut(playback)", method, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StopAndDispose(playback.Instance)",
            method,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "StopAndDispose(retiring.Instance)",
            method,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Dawn_dusk_external_branch_advances_existing_work_farming_fades()
    {
        var service = ReadContract("ActivityMusicService.cs");
        var audio = ReadContract("ActivityMusicAudioService.cs");

        var externalBranchStart = service.IndexOf(
            "var externalMusicActive = ActivityMusicAudioService.IsExternalMusicActive()",
            StringComparison.Ordinal
        );
        var externalBranchEnd = service.IndexOf(
            "externalMusicWasActive = false",
            externalBranchStart,
            StringComparison.Ordinal
        );
        Assert.True(externalBranchStart >= 0);
        Assert.True(externalBranchEnd > externalBranchStart);
        var externalBranch = service[externalBranchStart..externalBranchEnd];
        Assert.Contains("IsDawnOrDuskMusicActive()", externalBranch, StringComparison.Ordinal);
        Assert.Contains(
            "UpdateDawnDuskWorkAndFarmingFadeOuts()",
            externalBranch,
            StringComparison.Ordinal
        );

        var fadeUpdateStart = audio.IndexOf(
            "internal static void UpdateDawnDuskWorkAndFarmingFadeOuts()",
            StringComparison.Ordinal
        );
        var fadeUpdateEnd = audio.IndexOf(
            "internal static bool IsExternalMusicActive()",
            fadeUpdateStart,
            StringComparison.Ordinal
        );
        Assert.True(fadeUpdateStart >= 0);
        Assert.True(fadeUpdateEnd > fadeUpdateStart);
        var fadeUpdate = audio[fadeUpdateStart..fadeUpdateEnd];
        Assert.Contains("ShouldAdvanceDuringDawnDusk(playback.Kind)", fadeUpdate, StringComparison.Ordinal);
        Assert.Contains("AdvancePlayback(playback, elapsedSeconds)", fadeUpdate, StringComparison.Ordinal);
        Assert.Contains("ReleaseVanillaMusicIfIdle(resumeIfStillOwned: true)", fadeUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartIfStillActive", fadeUpdate, StringComparison.Ordinal);
    }

    [Fact]
    public void Work_music_uses_dusk_gate_while_farming_uses_only_dusk_cooldown()
    {
        var source = ReadContract("ActivityMusicService.cs");
        var workStart = source.IndexOf(
            "internal static void RecordWork(Farmer? owner, GameLocation? location",
            StringComparison.Ordinal
        );
        var farmingStart = source.IndexOf(
            "internal static void RecordFarming",
            workStart,
            StringComparison.Ordinal
        );
        var combatStart = source.IndexOf(
            "internal static void RecordCombat",
            farmingStart,
            StringComparison.Ordinal
        );

        Assert.True(workStart >= 0);
        Assert.True(farmingStart > workStart);
        Assert.True(combatStart > farmingStart);
        Assert.Contains(
            "IsWorkBlockedByDusk(location)",
            source[workStart..farmingStart],
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "IsWorkBlockedByTrueDark",
            source[farmingStart..combatStart],
            StringComparison.Ordinal
        );
        Assert.Contains("Game1.getStartingToGetDarkTime(location)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.getTrulyDarkTime(location)", source, StringComparison.Ordinal);
        Assert.Contains("TimeChanged += OnTimeChanged", source, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicPhasePolicy.IsDuskCrossing", source, StringComparison.Ordinal);
        Assert.Contains("StartDusk", source, StringComparison.Ordinal);
        Assert.Contains("RemoveWorkAndFarming", source, StringComparison.Ordinal);
        Assert.Contains("StopWorkAndFarming", source, StringComparison.Ordinal);
        Assert.Contains("GameLoop.DayStarted += OnDayStarted", source, StringComparison.Ordinal);
        Assert.Contains("GameLoop.DayStarted -= OnDayStarted", source, StringComparison.Ordinal);
        Assert.Contains("previousTimeOfDay = Game1.timeOfDay", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dawn_and_dusk_phase_edges_add_a_shared_work_farming_protection_window()
    {
        var source = ReadContract("ActivityMusicService.cs");
        Assert.Contains("ActivityMusicPhasePolicy.IsDawnCrossing", source, StringComparison.Ordinal);
        Assert.Contains("StartDawn", source, StringComparison.Ordinal);
        Assert.Contains("StartDusk", source, StringComparison.Ordinal);
        Assert.Contains("IsActivitySuppressedByPhase(owner)", source, StringComparison.Ordinal);
        Assert.Contains("GameLoop.DayStarted += OnDayStarted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dawn_keeps_the_original_native_music_suppression_and_release_chain()
    {
        var source = ReadSource("AudioVisualFact", "DawnMusicService.cs");

        Assert.Contains("Game1.currentSong?.Stop(AudioStopOptions.Immediate)", source, StringComparison.Ordinal);
        Assert.Contains("Game1.currentSong.Stop(AudioStopOptions.Immediate)", source, StringComparison.Ordinal);
        Assert.Contains("PatchMorningSong(true)", source, StringComparison.Ordinal);
        Assert.Contains("PatchMorningSong(false)", source, StringComparison.Ordinal);
        Assert.Contains("RestoreMusicSystem()", source, StringComparison.Ordinal);
        Assert.Contains("Game1.updateMusic()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dusk_uses_its_own_native_suppression_without_dawn_restore_logic()
    {
        var source = ReadSource("AudioVisualFact", "DuskMusicService.cs");

        Assert.Contains("Game1.currentSong?.Stop(AudioStopOptions.Immediate)", source, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicService.IsLocalCombatOrBossActive()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateTicked += OnUpdateTicked", source, StringComparison.Ordinal);
        Assert.DoesNotContain("duskNativeMusicSuppressed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DayStarted += OnDayStarted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreMusicSystem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.updateMusic()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.playMorningSong", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dusk_and_activity_phase_gates_exclude_all_dungeon_families()
    {
        var dusk = ReadSource("AudioVisualFact", "DuskMusicService.cs");
        var service = ReadContract("ActivityMusicService.cs");

        Assert.Contains("Game1.currentLocation is MineShaft", dusk, StringComparison.Ordinal);
        Assert.Contains("Game1.currentLocation is VolcanoDungeon", dusk, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicLocationFamily.Mine", service, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicLocationFamily.QuarryMine", service, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicLocationFamily.SkullCavern", service, StringComparison.Ordinal);
        Assert.Contains("MineShaft or VolcanoDungeon", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Dawn_and_dusk_do_not_treat_combat_or_boss_as_external_music_to_clear()
    {
        var audio = ReadContract("ActivityMusicAudioService.cs");
        var service = ReadContract("ActivityMusicService.cs");

        Assert.Contains("IsDawnOrDuskMusicActive", audio, StringComparison.Ordinal);
        Assert.Contains(
            "state.Kind is ActivityMusicKind.Combat or ActivityMusicKind.Boss",
            audio,
            StringComparison.Ordinal
        );
        Assert.Contains("IsDawnOrDuskMusicActive", service, StringComparison.Ordinal);
        Assert.Contains("RemoveWorkAndFarming", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_and_insane_cleanup_are_scoped_to_work_and_farming()
    {
        var audio = ReadContract("ActivityMusicAudioService.cs");
        var state = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ActivityMusic", "ActivityMusicStateStore.cs")
        );

        Assert.Contains("StopWorkAndFarming", audio, StringComparison.Ordinal);
        Assert.Contains(
            "playback.Kind is ActivityMusicKind.Work or ActivityMusicKind.Farming",
            audio,
            StringComparison.Ordinal
        );
        Assert.Contains("RemoveWorkAndFarming", state, StringComparison.Ordinal);
        Assert.DoesNotContain("StopAll()", audio, StringComparison.Ordinal);
    }

    [Fact]
    public void VanillaMusicLeasePausesAndResumesOnlyTheSameCue()
    {
        var source = ReadContract("ActivityMusicVanillaMusicLease.cs");

        Assert.Contains("Game1.currentSong", source, StringComparison.Ordinal);
        Assert.Contains("bool TryAcquire()", source, StringComparison.Ordinal);
        Assert.Contains("pausedCue.Pause()", source, StringComparison.Ordinal);
        Assert.Contains("pausedCue.Resume()", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseImmediately", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(Game1.currentSong, pausedCue)", source, StringComparison.Ordinal);
        Assert.Contains("Game1.requestedMusicDirty = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.currentSong.Stop", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedInstancesReplayOnlyWhileTheSameLogicalOwnerStateIsActive()
    {
        var source = ReadContract("ActivityMusicAudioService.cs");

        Assert.Contains("SoundState.Stopped", source, StringComparison.Ordinal);
        Assert.Contains("StateStore.TryGet", source, StringComparison.Ordinal);
        Assert.Contains("state.Generation != completed.Generation", source, StringComparison.Ordinal);
        Assert.Contains("resources.TryCreateInstance(state.TrackKey", source, StringComparison.Ordinal);
        Assert.Contains("new List<Playback>(playbacks.Values)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioUpdateSkipsPlaybackSnapshotAllocationWhenThereIsNothingToAdvance()
    {
        var source = ReadContract("ActivityMusicAudioService.cs");

        Assert.Contains("if (playbacks.Count > 0)", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (retiringPlaybacks.Count == 0)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new List<Playback>(playbacks.Values)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new List<Playback>(retiringPlaybacks)",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ServiceUsesHardCleanupForExternalMusicAndFadedStateExpiry()
    {
        var service = ReadContract("ActivityMusicService.cs");

        Assert.Contains("UpdateTicked", service, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicAudioService.StopAll();", service, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicActionBridge.ClearActiveFishing();", service, StringComparison.Ordinal);
        Assert.Contains("foreach (var ownerId in StateStore.Advance(1))", service, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicAudioService.FadeOutOwner(ownerId)", service, StringComparison.Ordinal);
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ActivityMusic", fileName)
        );
    }

    private static string ReadSource(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
