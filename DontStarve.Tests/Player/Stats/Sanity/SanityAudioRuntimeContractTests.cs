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

    private static string ReadSource(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
