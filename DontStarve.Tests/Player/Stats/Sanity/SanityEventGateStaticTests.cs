using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityEventGateStaticTests
{
    [Fact]
    public void Adapter_patches_only_exact_two_argument_precondition_postfix()
    {
        var source = ReadAdapter();

        Assert.Contains("nameof(GameLocation.checkEventPrecondition)", source, StringComparison.Ordinal);
        Assert.Contains("new[] { typeof(string), typeof(bool) }", source, StringComparison.Ordinal);
        Assert.Contains("postfix: new HarmonyMethod(postfix)", source, StringComparison.Ordinal);
        Assert.Contains("Event.SplitPreconditions(precondition)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("checkForEvents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("startEvent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HarmonyPatchType.Prefix", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_unpatches_only_its_exact_target_and_never_reopens_music_patch()
    {
        var source = ReadAdapter();

        Assert.Contains("harmony.Unpatch(", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Postfix", source, StringComparison.Ordinal);
        Assert.Contains("UninstallGatePatch(\"sanity-system-disabled\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UnpatchAll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("updateMusic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("changeMusicTrack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SoundEffectInstance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicManager", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_reads_base_snapshot_and_never_calls_base_set_change_or_save()
    {
        var source = ReadAdapter();

        Assert.Contains("lifecycle.TryGetBaseSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("SanityFriendshipEventGate.Evaluate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSanity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteSaveData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DontStarve.Sanity", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_has_bounded_diagnostics_load_once_json_and_no_localized_text_parser()
    {
        var source = ReadAdapter();

        Assert.Contains("MaximumLoggedDiagnostics = 64", source, StringComparison.Ordinal);
        Assert.Contains("File.ReadAllText(path)", source, StringComparison.Ordinal);
        Assert.Contains("private readonly SanityEventOverrideCatalog overrides", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizedContentManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Translation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ITranslationHelper", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_cleanup_is_owner_screen_scoped_instead_of_global()
    {
        var source = ReadContract("ShadowProjection", "SmapiHarmlessProjectionHost.cs");

        Assert.Contains("lifecycle.EventOwnerCoverageChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("scheduler.CleanupOwner(", source, StringComparison.Ordinal);
        Assert.Contains("change.Key.PlayerKey", source, StringComparison.Ordinal);
        Assert.Contains("ownerContextByScreen.Remove(change.Key.ScreenId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycle.EventCoverageChanged += OnEventCoverageChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_audio_and_music_policy_keep_the_existing_any_event_signal()
    {
        var source = ReadContract("AudioPool", "SanitySmapiAudioService.cs");

        Assert.Contains("lifecycle.EventCoverageChanged += OnEventCoverageChanged", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.SetEventSuspended(active)", source, StringComparison.Ordinal);
        Assert.Contains("ReconcileGameMusic()", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.MusicState()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("checkEventPrecondition", source, StringComparison.Ordinal);
    }

    private static string ReadAdapter()
    {
        return ReadContract("EventGate", "SanitySmapiEventService.cs");
    }

    private static string ReadContract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
