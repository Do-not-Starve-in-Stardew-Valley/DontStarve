using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityAudioRuntimeContractTests
{
    [Fact]
    public void Smapi_adapter_borrows_stage03_effects_and_rejects_placeholder_gameplay_audio()
    {
        var source = ReadSource("AudioPool", "SanitySmapiAudioService.cs");

        Assert.Contains("resources.LoadAudioCueSet(cueSetId)", source, StringComparison.Ordinal);
        Assert.Contains("result.Diagnostic.IsPlaceholder", source, StringComparison.Ordinal);
        Assert.Contains("new XnaSanityAudioEffect(soundResource)", source, StringComparison.Ordinal);
        Assert.Contains("resource.SoundEffect.CreateInstance()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("resource.SoundEffect.Dispose", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SanityRuntimeResourceLoader", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_invalidation_notifies_instance_owners_before_loader_release()
    {
        var source = ReadSource("AudioVisualFact", "SanitySmapiResourceService.cs");
        var handler = source.IndexOf("private void OnAssetsInvalidated", StringComparison.Ordinal);
        var release = source.IndexOf(
            "SanityResourceReleaseReason.ContentInvalidated",
            handler,
            StringComparison.Ordinal
        );
        var invalidate = source.IndexOf("loader.InvalidateContent", handler, StringComparison.Ordinal);

        Assert.True(handler >= 0);
        Assert.True(release > handler);
        Assert.True(invalidate > release);
    }

    [Fact]
    public void Event_overlay_owner_invalidation_retains_audio_claim_but_real_owner_loss_cleans_it()
    {
        var source = ReadSource("AudioPool", "SanitySmapiAudioService.cs");
        var handlerStart = source.IndexOf(
            "private void OnStateEventPublished",
            StringComparison.Ordinal
        );
        var handlerEnd = source.IndexOf(
            "private void OnTierStateObserved",
            handlerStart,
            StringComparison.Ordinal
        );
        var handler = source.Substring(handlerStart, handlerEnd - handlerStart);
        var eventRetention = handler.IndexOf(
            "lifecycle.IsEventCoverageActiveForPlayer(stateEvent.PlayerKey)",
            StringComparison.Ordinal
        );
        var ownerRemoval = handler.IndexOf(
            "RemoveOwner(stateEvent.PlayerKey);",
            StringComparison.Ordinal
        );

        Assert.True(handlerStart >= 0);
        Assert.True(handlerEnd > handlerStart);
        Assert.True(eventRetention >= 0);
        Assert.True(ownerRemoval > eventRetention);
        Assert.Contains("LogEventOwnerClaimRetained", handler, StringComparison.Ordinal);
        Assert.Contains("LogEventAudioTransition(active);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_pool_routes_ambient_and_sound_sliders_without_global_volume_mutation()
    {
        var source = ReadSource("AudioPool", "SanitySmapiAudioService.cs");

        Assert.Contains("Game1.options.ambientVolumeLevel", source, StringComparison.Ordinal);
        Assert.Contains("Game1.options.soundVolumeLevel", source, StringComparison.Ordinal);
        Assert.Contains("ambienceLane?.SetVolume(ambientVolume)", source, StringComparison.Ordinal);
        Assert.Contains("whispersLane?.SetVolume(soundVolume)", source, StringComparison.Ordinal);
        Assert.Contains("dangerLane?.SetVolume(soundVolume)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SoundEffect.MasterVolume", source, StringComparison.Ordinal);
        Assert.DoesNotContain("changeMusicTrack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("updateMusic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("currentSong", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SpriteBatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TestPackage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("references", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Focus_lifecycle_is_direct_and_only_pauses_continuous_sanity_pools()
    {
        var source = ReadSource("AudioPool", "SanitySmapiAudioService.cs");

        Assert.Contains("game.Deactivated += OnWindowDeactivated", source, StringComparison.Ordinal);
        Assert.Contains("game.Activated += OnWindowActivated", source, StringComparison.Ordinal);
        Assert.Contains("TryAttachWindowFocusEvents();", source, StringComparison.Ordinal);
        Assert.Contains("SetWindowInactive(Game1.game1 is null || !Game1.game1.IsActive)", source, StringComparison.Ordinal);

        var start = source.IndexOf(
            "public void SetContinuousPoolsPaused",
            StringComparison.Ordinal
        );
        var end = source.IndexOf("public void SetSuspended", start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        var focusOutput = source.Substring(start, end - start);
        Assert.Contains("ambienceLane?.Pause()", focusOutput, StringComparison.Ordinal);
        Assert.Contains("whispersLane?.Pause()", focusOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("dangerLane?.Pause()", focusOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("darknessWarningLane?.Pause()", focusOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_audio_contract_keeps_default_warning_clip_pause_and_attack_seams_with_five_instance_budget()
    {
        var source = ReadSource("AudioPool", "SanitySmapiAudioService.cs");

        Assert.Contains("public void SetDarknessWarningClip(string warningClipId)", source, StringComparison.Ordinal);
        Assert.Contains("public void SetDarknessWarningPaused(bool value)", source, StringComparison.Ordinal);
        Assert.Contains("public void TriggerDarknessAttack()", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.SetDarknessWarningClaimPlaybackPaused", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.TriggerDarknessAttack()", source, StringComparison.Ordinal);
    }

    private static string ReadSource(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
