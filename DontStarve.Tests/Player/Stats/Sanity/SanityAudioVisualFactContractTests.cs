using System.Text.Json;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

/// <summary>
/// Stage 05-01 fact probes. These tests deliberately model contracts and inspect copied source
/// text only: they don't reference the game assemblies, construct XNA resources, or play audio.
/// </summary>
public sealed class SanityAudioVisualFactContractTests
{
    private static string ContractRoot =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", "AudioVisualFact");

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string AudioCueMetadataPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Audio", "audio-cues.json");

    [Fact]
    public void OwnerScreenClaimUsesLatestRevisionAndIgnoresDuplicateOrStaleUpdates()
    {
        var claims = SanityAudioVisualFactContract.Canonicalize(
            new[]
            {
                Claim(7, 1, 4, 0.40, ambience: true, whispers: true),
                Claim(7, 1, 4, 0.10, ambience: true, whispers: true, danger: true),
                Claim(7, 1, 3, 0.05, ambience: true, whispers: true, danger: true),
                Claim(7, 1, 5, 0.35, ambience: true, whispers: true),
                Claim(8, 0, 2, 0.80),
            }
        );

        Assert.Equal(2, claims.Count);
        var owner = Assert.Single(claims.Where(value => value.PlayerKey == 7));
        Assert.Equal(5, owner.Revision);
        Assert.Equal(0.35, owner.EffectiveRatio);
        Assert.False(owner.DangerActive);
    }

    [Fact]
    public void WinnerUsesLowestRatioThenScreenThenPlayerKey()
    {
        var result = SanityAudioVisualFactContract.Aggregate(
            new[]
            {
                Claim(30, 2, 1, 0.30, ambience: true, whispers: true),
                Claim(20, 1, 1, 0.30, ambience: true, whispers: true),
                Claim(10, 1, 1, 0.30, ambience: true, whispers: true),
                Claim(40, 0, 1, 0.20, ambience: true, whispers: true),
            }
        );

        Assert.NotNull(result.Winner);
        Assert.Equal(40, result.Winner!.PlayerKey);

        var tied = SanityAudioVisualFactContract.Aggregate(
            new[]
            {
                Claim(30, 2, 1, 0.30, ambience: true),
                Claim(20, 1, 1, 0.30, ambience: true),
                Claim(10, 1, 1, 0.30, ambience: true),
            }
        );
        Assert.Equal(10, tied.Winner!.PlayerKey);
    }

    [Fact]
    public void EmptyTruthTableHasNoPhysicalInstance()
    {
        AssertAggregate(Array.Empty<AudioVisualOwnerClaim>(), false, false, false, 0);
    }

    [Theory]
    [InlineData(true, false, false, 1)]
    [InlineData(true, true, false, 2)]
    [InlineData(true, true, true, 3)]
    public void SingleOwnerTruthTableMapsLogicalLanesToProcessSharedInstances(
        bool ambience,
        bool whispers,
        bool danger,
        int expectedInstances
    )
    {
        AssertAggregate(
            new[] { Claim(1, 0, 1, 0.10, ambience, whispers, danger) },
            ambience,
            whispers,
            danger,
            expectedInstances
        );
    }

    [Fact]
    public void ManyOwnersStillUseAtMostThreeProcessSharedInstances()
    {
        var claims = Enumerable
            .Range(1, 16)
            .Select(value =>
                Claim(value, value % 4, 1, value / 100d, true, true, true)
            );

        var aggregate = SanityAudioVisualFactContract.Aggregate(claims);

        Assert.Equal(3, aggregate.PhysicalInstanceCount);
        Assert.Equal(SanityAudioVisualFactContract.ProcessPhysicalInstanceCap, aggregate.PhysicalInstanceCount);
    }

    [Fact]
    public void DangerOneShotFiresOnlyOnAggregateEntryAndRearmsAfterLastExit()
    {
        var receipt = new DangerOneShotReceipt();

        Assert.False(receipt.Observe(anyDangerClaim: false));
        Assert.True(receipt.Observe(anyDangerClaim: true));
        Assert.False(receipt.Observe(anyDangerClaim: true));
        Assert.False(receipt.Observe(anyDangerClaim: true));
        Assert.False(receipt.Observe(anyDangerClaim: false));
        Assert.True(receipt.Observe(anyDangerClaim: true));
    }

    [Fact]
    public void LifecycleMatrixKeepsInstanceOwnershipAheadOfBorrowedSoundEffectRelease()
    {
        var rows = SanityAudioVisualFactContract.LifecycleRows;

        AssertLifecycle(rows, "local-menu", "ExcludeOwnerClaim;Reaggregate", false);
        AssertLifecycle(rows, "process-pause-or-focus-loss", "PausePhysical;RetainClaims", false);
        AssertLifecycle(rows, "event-entered", "StopPhysical;SuspendClaims;RetainReceipt", false);
        AssertLifecycle(rows, "owner-warped", "RemoveOwnerClaim;Reaggregate", false);
        AssertLifecycle(rows, "day-ending", "ClearClaims;StopDisposeInstances", false);
        AssertLifecycle(rows, "returned-title", "ClearClaims;StopDisposeInstances", true);
        AssertLifecycle(rows, "system-disabled", "ClearClaims;StopDisposeInstances", true);
        AssertLifecycle(rows, "dispose", "ClearClaims;StopDisposeInstances", true);
    }

    [Fact]
    public void AudioMetadataHasEightCueSetsSeventeenCuesAndThirtyFiveBorrowedEffects()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AudioCueMetadataPath));
        var cueSets = document.RootElement.GetProperty("CueSets").EnumerateArray().ToArray();
        var cues = cueSets.SelectMany(value => value.GetProperty("Cues").EnumerateArray()).ToArray();
        var clips = cues.SelectMany(value => value.GetProperty("Clips").EnumerateArray()).ToArray();

        Assert.Equal(8, cueSets.Length);
        Assert.Equal(17, cues.Length);
        Assert.Equal(35, clips.Length);
        AssertCueSet(cueSets, "sanity.cue.ambience", 1, "RandomContinuousOneShotPool", 11);
        AssertCueSet(cueSets, "sanity.cue.whispers", 1, "RandomContinuousOneShotPool", 11);
        AssertCueSet(cueSets, "sanity.cue.thresholds", 1, "OneShot", 1);
        AssertCueSet(cueSets, "sanity.cue.sanity-change", 0, "Disabled", 0);
    }

    [Fact]
    public void ResourcePreviewLoadsButNeverPlaysAndSignalsBeforeLoaderRelease()
    {
        var source = ReadSource("SanitySmapiResourceService.cs");

        Assert.Contains("Cues are loaded but not played", source, StringComparison.Ordinal);
        Assert.Contains("new SanityResourceDisposalGate(owningThreadId)", source, StringComparison.Ordinal);
        Assert.Contains("SoundEffect creation is allowed only on the SMAPI/XNA owning thread", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Play(", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "WorldResourcesReleasing?.Invoke(SanityResourceReleaseReason.Dispose)",
                StringComparison.Ordinal
            )
            < source.IndexOf("loader.Dispose()", StringComparison.Ordinal)
        );

        var processExitStart = source.IndexOf(
            "private void OnProcessExit",
            StringComparison.Ordinal
        );
        var processExitEnd = source.IndexOf(
            "private bool TryGetOwnerKey",
            processExitStart,
            StringComparison.Ordinal
        );
        var processExit = source[processExitStart..processExitEnd];
        Assert.Contains("SanityResourceDisposeOrigin.ProcessExit", processExit, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose();", processExit, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor.Log", processExit, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingMusicServicesAreStaticProcessSharedAndIndependentSoundEffects()
    {
        var manager = ReadSource("MusicManager.cs");
        var loader = ReadSource("MusicAudioLoader.cs");
        var dawn = ReadSource("DawnMusicService.cs");
        var dusk = ReadSource("DuskMusicService.cs");

        Assert.Contains("internal static class MusicManager", manager, StringComparison.Ordinal);
        Assert.Contains("SoundEffect.FromStream(stream)", loader, StringComparison.Ordinal);
        Assert.Contains("soundEffect.CreateInstance()", loader, StringComparison.Ordinal);
        Assert.Contains("Game1.currentSong", dawn, StringComparison.Ordinal);
        Assert.Contains("Game1.updateMusic()", dawn, StringComparison.Ordinal);
        Assert.Contains("playMorningSong", dawn, StringComparison.Ordinal);
        Assert.Contains("SoundEffectInstance", dusk, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.changeMusicTrack", dusk, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.updateMusic", dusk, StringComparison.Ordinal);
    }

    [Fact]
    public void Dusk_music_uses_stardews_start_dark_boundary_not_true_darkness()
    {
        var dusk = ReadSource("DuskMusicService.cs");

        Assert.Contains(
            "return Game1.getStartingToGetDarkTime(Game1.currentLocation);",
            dusk,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("\"fall\" => 1900", dusk, StringComparison.Ordinal);
        Assert.DoesNotContain("\"winter\" => 1800", dusk, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicSuppressionIsVersionGatedWithJukeboxOnlyExemption()
    {
        var capability = SanityAudioVisualFactContract.MusicSuppression;

        Assert.Equal("process-shared", capability.Scope);
        Assert.Equal("implemented-version-gated", capability.Status);
        Assert.Equal("fail-closed-version-or-signature-mismatch", capability.Fallback);
        Assert.True(capability.JukeboxIsAlwaysExempt);
        Assert.False(capability.IslandIsBlanketExempt);
        Assert.False(capability.ControlsIndependentSoundEffects);
    }

    [Fact]
    public void EventGateUsesOnlyExactPostfixAndUnknownOrBypassPathsFailOpen()
    {
        var eventGate = SanityAudioVisualFactContract.EventGate;

        Assert.Equal(
            "StardewValley.GameLocation.checkEventPrecondition(System.String,System.Boolean)",
            eventGate.Target
        );
        Assert.Equal("postfix-valid-id-to-minus-one-only", eventGate.Mutation);
        Assert.True(eventGate.UnknownConditionFailsOpen);
        Assert.True(eventGate.DirectStartFestivalWeddingAndModPathsFailOpen);
        Assert.True(eventGate.JsonOverrideIsVersionedAndLoadedOnce);
        Assert.DoesNotContain("checkForEvents", eventGate.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("startEvent", eventGate.Target, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveSanityOverlayIsOwnerScreenLocalAndNeverPersistsToV2()
    {
        var overlay = SanityAudioVisualFactContract.EffectiveOverlay;

        Assert.Equal("player-key+screen-id+session+revision", overlay.Key);
        Assert.Equal(1d, overlay.EventRatio);
        Assert.False(overlay.WritesBaseState);
        Assert.False(overlay.WritesV2);
        Assert.Equal(
            new[]
            {
                "event-normal-exit",
                "event-abnormal-disappearance",
                "festival-exit",
                "owner-warped",
                "returned-title",
                "system-disabled",
            },
            overlay.ClearSignals
        );
    }

    [Fact]
    public void DrawMatrixUsesInstanceFinalCompositionAndKeepsUiOutsideWorldEffect()
    {
        var rows = SanityAudioVisualFactContract.DrawRows;

        AssertDraw(rows, "instance-screen-final-composition", true, "owner-local-world-only");
        AssertDraw(rows, "RenderingHud", true, "uiViewport-screen-overlay");
        AssertDraw(rows, "uiScreen-after-world", true, "original-color-ui-cursor-border");
        AssertDraw(rows, "Game1.viewport-shake", false, "fail-closed-process-global");
        Assert.All(rows, row => Assert.False(row.AllocatesResourcePerFrame));

        var display = ReadSource("DisplayManager.cs");
        var uiContext = ReadSource("UIRenderContext.cs");
        Assert.Contains("Events.Display.RenderingHud", display, StringComparison.Ordinal);
        Assert.Contains("Game1.uiViewport.Width", uiContext, StringComparison.Ordinal);
        Assert.Contains("Game1.uiViewport.Height", uiContext, StringComparison.Ordinal);
    }

    [Fact]
    public void IdlePredicateRequiresControllableBasicFarmerSpriteAndResetsOnInterruptions()
    {
        var idle = SanityAudioVisualFactContract.Idle;

        Assert.Equal(
            new[]
            {
                "effectiveRatio<0.5&&ShadowCreatures",
                "!Farmer.IsBusyDoingSomething()",
                "!Farmer.isMoving()",
                "movementDirections.Count==0",
                "FarmerSprite.PauseForSingleAnimation==false",
                "FarmerSprite.IsPlayingBasicAnimation(FacingDirection,IsCarrying())",
            },
            idle.RequiredPredicates
        );
        Assert.Equal(
            new[] { "button-or-wheel", "event", "menu", "movement", "tool", "high-priority-animation", "warp" },
            idle.ResetSignals
        );
        Assert.True(idle.OwnsReversibleAnimationToken);
        Assert.False(idle.CallsVanillaPassOutSequence);
        Assert.Equal(1, idle.RealSecondsBeforeActive);
    }

    [Fact]
    public void PatchOwnersAreUniqueAndExactTargetUnpatchIsMandatory()
    {
        var rows = SanityAudioVisualFactContract.PatchRows;

        Assert.Equal(rows.Count, rows.Select(value => value.OwnerId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, row => Assert.True(row.ExactTargetUnpatch));
        Assert.Contains(rows, row => row.OwnerId.EndsWith(".Sanity4.EventGate", StringComparison.Ordinal));
        Assert.Contains(rows, row =>
            row.OwnerId.EndsWith(".Sanity4.MusicSuppression", StringComparison.Ordinal)
            && row.Target == "Game1.updateMusic()"
            && row.Status == "installed-exact-version-gated");
        Assert.Contains(rows, row =>
            row.OwnerId.EndsWith(".Sanity4.WorldComposition", StringComparison.Ordinal)
            && row.Target.Contains("renderScreenBuffer(RenderTarget2D)", StringComparison.Ordinal)
            && row.Status == "installed-exact-version-backend-gated");
    }

    private static AudioVisualOwnerClaim Claim(
        long playerKey,
        int screenId,
        long revision,
        double ratio,
        bool ambience = false,
        bool whispers = false,
        bool danger = false
    )
    {
        return new AudioVisualOwnerClaim(
            playerKey,
            screenId,
            revision,
            ratio,
            ambience,
            whispers,
            danger
        );
    }

    private static void AssertAggregate(
        IEnumerable<AudioVisualOwnerClaim> claims,
        bool ambience,
        bool whispers,
        bool danger,
        int physicalInstances
    )
    {
        var aggregate = SanityAudioVisualFactContract.Aggregate(claims);
        Assert.Equal(ambience, aggregate.AmbienceActive);
        Assert.Equal(whispers, aggregate.WhispersActive);
        Assert.Equal(danger, aggregate.DangerActive);
        Assert.Equal(physicalInstances, aggregate.PhysicalInstanceCount);
    }

    private static void AssertLifecycle(
        IReadOnlyList<AudioVisualLifecycleFact> rows,
        string signal,
        string action,
        bool beforeBorrowedEffectRelease
    )
    {
        var row = Assert.Single(rows.Where(value => value.Signal == signal));
        Assert.Equal(action, row.Action);
        Assert.Equal(beforeBorrowedEffectRelease, row.MustRunBeforeBorrowedEffectRelease);
    }

    private static void AssertDraw(
        IReadOnlyList<AudioVisualDrawFact> rows,
        string hook,
        bool supported,
        string policy
    )
    {
        var row = Assert.Single(rows.Where(value => value.Hook == hook));
        Assert.Equal(supported, row.Supported);
        Assert.Equal(policy, row.Policy);
    }

    private static void AssertCueSet(
        IReadOnlyList<JsonElement> cueSets,
        string cueSetId,
        int maxInstances,
        string playbackMode,
        int clipCount
    )
    {
        var cueSet = Assert.Single(
            cueSets.Where(value => value.GetProperty("CueSetId").GetString() == cueSetId)
        );
        Assert.Equal(maxInstances, cueSet.GetProperty("MaxConcurrentInstances").GetInt32());
        var cues = cueSet.GetProperty("Cues").EnumerateArray().ToArray();
        Assert.All(cues, cue => Assert.Equal(playbackMode, cue.GetProperty("PlaybackMode").GetString()));
        Assert.Equal(
            clipCount,
            cues.Sum(cue => cue.GetProperty("Clips").GetArrayLength())
        );
    }

    private static string ReadSource(string fileName)
    {
        return File.ReadAllText(Path.Combine(ContractRoot, fileName));
    }
}

internal sealed record AudioVisualOwnerClaim(
    long PlayerKey,
    int ScreenId,
    long Revision,
    double EffectiveRatio,
    bool AmbienceActive,
    bool WhispersActive,
    bool DangerActive
);

internal sealed record AudioVisualAggregate(
    IReadOnlyList<AudioVisualOwnerClaim> Claims,
    AudioVisualOwnerClaim? Winner,
    bool AmbienceActive,
    bool WhispersActive,
    bool DangerActive,
    int PhysicalInstanceCount
);

internal sealed record AudioVisualLifecycleFact(
    string Signal,
    string Action,
    bool MustRunBeforeBorrowedEffectRelease
);

internal sealed record AudioVisualCapabilityFact(
    string Scope,
    string Status,
    string Fallback,
    bool JukeboxIsAlwaysExempt,
    bool IslandIsBlanketExempt,
    bool ControlsIndependentSoundEffects
);

internal sealed record AudioVisualEventGateFact(
    string Target,
    string Mutation,
    bool UnknownConditionFailsOpen,
    bool DirectStartFestivalWeddingAndModPathsFailOpen,
    bool JsonOverrideIsVersionedAndLoadedOnce
);

internal sealed record AudioVisualOverlayFact(
    string Key,
    double EventRatio,
    bool WritesBaseState,
    bool WritesV2,
    IReadOnlyList<string> ClearSignals
);

internal sealed record AudioVisualDrawFact(
    string Hook,
    bool Supported,
    string Policy,
    bool AllocatesResourcePerFrame
);

internal sealed record AudioVisualIdleFact(
    IReadOnlyList<string> RequiredPredicates,
    IReadOnlyList<string> ResetSignals,
    bool OwnsReversibleAnimationToken,
    bool CallsVanillaPassOutSequence,
    int RealSecondsBeforeActive
);

internal sealed record AudioVisualPatchFact(
    string OwnerId,
    string Target,
    string Status,
    bool ExactTargetUnpatch
);

internal sealed class DangerOneShotReceipt
{
    private bool armed = true;

    internal bool Observe(bool anyDangerClaim)
    {
        if (!anyDangerClaim)
        {
            armed = true;
            return false;
        }

        if (!armed)
            return false;

        armed = false;
        return true;
    }
}

internal static class SanityAudioVisualFactContract
{
    internal const int ProcessPhysicalInstanceCap = 3;

    internal static readonly IReadOnlyList<AudioVisualLifecycleFact> LifecycleRows =
        new[]
        {
            new AudioVisualLifecycleFact("local-menu", "ExcludeOwnerClaim;Reaggregate", false),
            new AudioVisualLifecycleFact("process-pause-or-focus-loss", "PausePhysical;RetainClaims", false),
            new AudioVisualLifecycleFact("event-entered", "StopPhysical;SuspendClaims;RetainReceipt", false),
            new AudioVisualLifecycleFact("owner-warped", "RemoveOwnerClaim;Reaggregate", false),
            new AudioVisualLifecycleFact("day-ending", "ClearClaims;StopDisposeInstances", false),
            new AudioVisualLifecycleFact("returned-title", "ClearClaims;StopDisposeInstances", true),
            new AudioVisualLifecycleFact("system-disabled", "ClearClaims;StopDisposeInstances", true),
            new AudioVisualLifecycleFact("dispose", "ClearClaims;StopDisposeInstances", true),
        };

    internal static readonly AudioVisualCapabilityFact MusicSuppression =
        new(
            "process-shared",
            "implemented-version-gated",
            "fail-closed-version-or-signature-mismatch",
            JukeboxIsAlwaysExempt: true,
            IslandIsBlanketExempt: false,
            ControlsIndependentSoundEffects: false
        );

    internal static readonly AudioVisualEventGateFact EventGate =
        new(
            "StardewValley.GameLocation.checkEventPrecondition(System.String,System.Boolean)",
            "postfix-valid-id-to-minus-one-only",
            UnknownConditionFailsOpen: true,
            DirectStartFestivalWeddingAndModPathsFailOpen: true,
            JsonOverrideIsVersionedAndLoadedOnce: true
        );

    internal static readonly AudioVisualOverlayFact EffectiveOverlay =
        new(
            "player-key+screen-id+session+revision",
            EventRatio: 1d,
            WritesBaseState: false,
            WritesV2: false,
            new[]
            {
                "event-normal-exit",
                "event-abnormal-disappearance",
                "festival-exit",
                "owner-warped",
                "returned-title",
                "system-disabled",
            }
        );

    internal static readonly IReadOnlyList<AudioVisualDrawFact> DrawRows =
        new[]
        {
            new AudioVisualDrawFact("instance-screen-final-composition", true, "owner-local-world-only", false),
            new AudioVisualDrawFact("RenderingHud", true, "uiViewport-screen-overlay", false),
            new AudioVisualDrawFact("uiScreen-after-world", true, "original-color-ui-cursor-border", false),
            new AudioVisualDrawFact("Game1.viewport-shake", false, "fail-closed-process-global", false),
        };

    internal static readonly AudioVisualIdleFact Idle =
        new(
            new[]
            {
                "effectiveRatio<0.5&&ShadowCreatures",
                "!Farmer.IsBusyDoingSomething()",
                "!Farmer.isMoving()",
                "movementDirections.Count==0",
                "FarmerSprite.PauseForSingleAnimation==false",
                "FarmerSprite.IsPlayingBasicAnimation(FacingDirection,IsCarrying())",
            },
            new[] { "button-or-wheel", "event", "menu", "movement", "tool", "high-priority-animation", "warp" },
            OwnsReversibleAnimationToken: true,
            CallsVanillaPassOutSequence: false,
            RealSecondsBeforeActive: 1
        );

    internal static readonly IReadOnlyList<AudioVisualPatchFact> PatchRows =
        new[]
        {
            new AudioVisualPatchFact(
                "Yurin.DontStarve.Sanity4.EventGate",
                "GameLocation.checkEventPrecondition(string,bool)",
                "future-exact-postfix",
                true
            ),
            new AudioVisualPatchFact(
                "Yurin.DontStarve.Sanity4.MusicSuppression",
                "Game1.updateMusic()",
                "installed-exact-version-gated",
                true
            ),
            new AudioVisualPatchFact(
                "Yurin.DontStarve.Sanity4.WorldComposition",
                "Game1.ShouldDrawOnBuffer()+renderScreenBuffer(RenderTarget2D)+DrawSplitScreenWindow()",
                "installed-exact-version-backend-gated",
                true
            ),
        };

    internal static IReadOnlyList<AudioVisualOwnerClaim> Canonicalize(
        IEnumerable<AudioVisualOwnerClaim> claims
    )
    {
        var current = new Dictionary<(long PlayerKey, int ScreenId), AudioVisualOwnerClaim>();
        foreach (var claim in claims)
        {
            var key = (claim.PlayerKey, claim.ScreenId);
            if (!current.TryGetValue(key, out var known) || claim.Revision > known.Revision)
                current[key] = claim;
        }

        return current.Values
            .OrderBy(value => value.ScreenId)
            .ThenBy(value => value.PlayerKey)
            .ToArray();
    }

    internal static AudioVisualAggregate Aggregate(IEnumerable<AudioVisualOwnerClaim> claims)
    {
        var current = Canonicalize(claims);
        var winner = current
            .OrderBy(value => value.EffectiveRatio)
            .ThenBy(value => value.ScreenId)
            .ThenBy(value => value.PlayerKey)
            .FirstOrDefault();
        var ambience = current.Any(value => value.AmbienceActive);
        var whispers = current.Any(value => value.WhispersActive);
        var danger = current.Any(value => value.DangerActive);
        var physicalInstances = (ambience ? 1 : 0) + (whispers ? 1 : 0) + (danger ? 1 : 0);

        return new AudioVisualAggregate(
            current,
            winner,
            ambience,
            whispers,
            danger,
            physicalInstances
        );
    }
}
