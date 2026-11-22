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
    public void Hoe_swing_routes_to_work_outside_farm_identity_and_gated_farming_on_farm()
    {
        // 锄地分流（用户 2026-11-17 两次裁决）：
        // ① 农场/温室：维持"翻出新耕地"成功信号门 → 耕种车道。
        // ② 其余地点（含姜岛农场）：锄头是挖掘工具，"只要挖了就发声"——每次实际挥锄
        //    都记工作，不要求翻出土、挖出斑点或粘土。
        // ③ 姜岛农场仅锄地算工作；种植/浇水/收获仍走耕种车道（IslandWest 已纳入
        //    IsFarmingLocation，见 FarmingGateRequiresFarmIdentityGreenhouseOrIslandWest）。
        var service = ReadContract("ActivityMusicService.cs");
        Assert.Contains(
            "internal static bool IsHoeFarmingLocation(GameLocation? location)",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "IsFarmingLocation(location) && location is not IslandWest",
            service,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "internal static void RecordHoe(",
            service,
            StringComparison.Ordinal
        );

        var bridge = ReadContract("ActivityMusicActionBridge.cs");
        var hoePostfixStart = bridge.IndexOf(
            "private static void HoePostfix(",
            StringComparison.Ordinal
        );
        var hoeDirtPrefixStart = bridge.IndexOf(
            "private static void HoeDirtToolActionPrefix(",
            hoePostfixStart < 0 ? 0 : hoePostfixStart,
            StringComparison.Ordinal
        );
        Assert.True(hoePostfixStart >= 0);
        Assert.True(hoeDirtPrefixStart > hoePostfixStart);
        var hoePostfix = bridge[hoePostfixStart..hoeDirtPrefixStart];
        Assert.Contains("IsHoeFarmingLocation(location)", hoePostfix, StringComparison.Ordinal);
        Assert.Contains(
            "RecordWork(who, location, \"tool.hoe.swing\")",
            hoePostfix,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "RecordFarming(who, location, \"farming.hoe.success\")",
            hoePostfix,
            StringComparison.Ordinal
        );
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
    public void VanillaMusicLease_uses_shared_background_ambient_exclusion_before_acquiring_fade_lease()
    {
        var source = ReadContract("ActivityMusicVanillaMusicLease.cs");

        Assert.Contains(
            "VanillaBackgroundAmbientMusicExclusionPolicy.ShouldPreserve",
            source,
            StringComparison.Ordinal
        );
        Assert.True(
            source.IndexOf(
                "VanillaBackgroundAmbientMusicExclusionPolicy.ShouldPreserve",
                StringComparison.Ordinal
            )
            < source.IndexOf("pausedCue = cue", StringComparison.Ordinal)
        );
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

    [Fact]
    public void Disabled_farming_music_downgrades_farming_triggers_to_work_triggers()
    {
        // EnableFarmingMusic（用户 2026-11-21 裁决）：四季音乐总开关下的半独立子开关。
        // 关闭时耕种触发在 RecordFarming 入口整体降级为工作触发——播当前地点的工作曲并
        // 遵守工作车道天黑封印；关闭瞬间立即退役所有耕种逻辑态与耕耘曲实例，不留旧行为尾巴。
        var source = ReadContract("ActivityMusicService.cs");
        var farmingStart = source.IndexOf(
            "internal static void RecordFarming",
            StringComparison.Ordinal
        );
        var combatStart = source.IndexOf(
            "internal static void RecordCombat",
            farmingStart,
            StringComparison.Ordinal
        );
        Assert.True(farmingStart >= 0);
        Assert.True(combatStart > farmingStart);
        var farming = source[farmingStart..combatStart];

        Assert.Contains("if (!farmingMusicEnabled)", farming, StringComparison.Ordinal);
        Assert.Contains("RecordWork(owner, location, reason)", farming, StringComparison.Ordinal);
        Assert.Contains(
            "internal static void SetFarmingMusicEnabled(bool value)",
            farming,
            StringComparison.Ordinal
        );
        Assert.Contains("StateStore.RemoveFarmingAll()", farming, StringComparison.Ordinal);
        Assert.Contains(
            "ActivityMusicAudioService.StopFarmingPlayback()",
            farming,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Farming_music_toggle_off_reuses_the_fade_path_for_farming_playbacks()
    {
        // 关闭耕种音乐不得硬切正在播的耕耘曲：必须走既有 BeginFadeOut 淡出路径，
        // 且只动 Farming 车道实例，Work/Combat/Boss 不受牵连。
        var audio = ReadContract("ActivityMusicAudioService.cs");
        var start = audio.IndexOf(
            "internal static void StopFarmingPlayback",
            StringComparison.Ordinal
        );
        var end = audio.IndexOf(
            "internal static void FadeOutOwner",
            start,
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        Assert.True(end > start);
        var method = audio[start..end];

        Assert.Contains(
            "playback.Kind != ActivityMusicKind.Farming",
            method,
            StringComparison.Ordinal
        );
        Assert.Contains("BeginFadeOut(playback)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("StopAndDispose", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Farming_music_toggle_off_only_removes_farming_states()
    {
        // 状态侧同样只清耕种态：RemoveFarmingAll 不得吞掉 Work/Combat/Boss 逻辑态。
        var state = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ActivityMusic", "ActivityMusicStateStore.cs")
        );

        Assert.Contains(
            "internal void RemoveFarmingAll()",
            state,
            StringComparison.Ordinal
        );
        var start = state.IndexOf("internal void RemoveFarmingAll()", StringComparison.Ordinal);
        var end = state.IndexOf(
            "internal IReadOnlyList<long> Advance",
            start,
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        Assert.True(end > start);
        var method = state[start..end];
        Assert.Contains(
            "pair.Value.Kind != ActivityMusicKind.Farming",
            method,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("states.Clear()", method, StringComparison.Ordinal);
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
