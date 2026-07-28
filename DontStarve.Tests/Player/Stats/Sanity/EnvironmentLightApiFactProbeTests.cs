using System.Reflection;
using System.Text.Json;
using DontStarve.Player.Stats.Sanity.Audio;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

/// <summary>
/// Stage 06-01 fact snapshot. The external assembly facts below were captured from the pinned
/// local Stardew/SMAPI binaries and XML docs; these tests remain pure and never load a game
/// assembly, mutate a Farmer, classify PitchBlack, or play audio.
/// </summary>
public sealed class EnvironmentLightApiFactProbeTests
{
    private static string ContractRoot =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", "EnvironmentLightApiFact");

    private static string AudioPoolContractRoot =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", "AudioPool");

    private static string ResourceContractRoot =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", "AudioVisualFact");

    private static string AudioCueMetadataPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Audio",
            "audio-cues.json"
        );

    [Fact]
    public void PinnedLocalAssemblyEvidenceMatchesTheFactSnapshot()
    {
        Assert.Collection(
            EnvironmentLightApiFactContract.Assemblies,
            value => AssertAssembly(
                value,
                "Stardew Valley.dll",
                "1.6.15.24356",
                6_268_416,
                "7F1E5B8E58D2758B78570BA771BBEB03D33522F62188BF6C32EDF0CF626DEAEE"
            ),
            value => AssertAssembly(
                value,
                "StardewValley.GameData.dll",
                "1.6.15.24356",
                96_768,
                "9C03497C2D2AC24C94E2F25B3C2FC39ECDE1BC97341E514C5F9FDCC1E759CB81"
            ),
            value => AssertAssembly(
                value,
                "StardewModdingAPI.dll",
                "4.3.2.0",
                1_019_392,
                "2C03E8D3028977BBE8D128975FF091E1104DF31AE2B08724E72888FCA702B0A8"
            )
        );
        Assert.Equal(
            "7A75EAF6DA11AB0B1F1FA9C519DD58565FD1A34682FF5BA7D1421654566EE341",
            EnvironmentLightApiFactContract.StardewXmlSha256
        );
        Assert.Equal(
            "1F1A527BEB17637535B363B9826EE075F32261141533193598AE0FD6446FE129",
            EnvironmentLightApiFactContract.SmapiXmlSha256
        );
    }

    [Fact]
    public void RawLightFieldsKeepTheirRealUnitsAndOwnership()
    {
        var fields = EnvironmentLightApiFactContract.LightFields;

        AssertField(fields, "Character.StandingPixel", "map-pixel", "owner-local", false);
        AssertField(fields, "LightSource.position", "map-pixel", "light-source", false);
        AssertField(fields, "LightSource.radius", "raw-texture-scale", "light-source", false);
        AssertField(fields, "LightSource.color", "raw-tint", "light-source", false);
        AssertField(fields, "Game1.ambientLight/outdoorLight", "rgba-0..255", "current-screen", false);
        AssertField(fields, "Game1.currentLightSources", "current-draw-list", "current-screen", false);
        AssertField(fields, "GameLocation.sharedLights", "net-location-list", "location-shared", false);
        Assert.All(fields, value => Assert.False(value.IsFinalStandingPixelBrightness));
    }

    [Fact]
    public void CurrentAdapterIsOwnerLocalBoundedAndCapturesStage02EvidenceWithoutGuessingCoverage()
    {
        var source = ReadSource("SmapiEnvironmentLightSnapshotProvider.cs");

        Assert.Contains("internal const int MaximumCandidateSnapshots = 64", source, StringComparison.Ordinal);
        Assert.Contains("screenId != Context.ScreenId", source, StringComparison.Ordinal);
        Assert.Contains("!owner.IsLocalPlayer", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(owner, Game1.player)", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(location, Game1.currentLocation)", source, StringComparison.Ordinal);
        Assert.Contains("var standingPixel = owner.StandingPixel", source, StringComparison.Ordinal);
        Assert.Contains("var currentSources = Game1.currentLightSources", source, StringComparison.Ordinal);
        Assert.Contains("var sharedCount = location.sharedLights.Count()", source, StringComparison.Ordinal);
        Assert.Contains("source.onlyLocation.Value", source, StringComparison.Ordinal);
        Assert.Contains("source.position.Value", source, StringComparison.Ordinal);
        Assert.Contains("source.radius.Value", source, StringComparison.Ordinal);
        Assert.Contains("source.color.Value", source, StringComparison.Ordinal);
        Assert.Contains("source.playerID.Value", source, StringComparison.Ordinal);
        Assert.Contains("source.lightContext.Value", source, StringComparison.Ordinal);
        Assert.Contains("source.IsOnScreen()", source, StringComparison.Ordinal);
        Assert.Contains("location.GetLocationContextId()", source, StringComparison.Ordinal);
        Assert.Contains("locationRules.Resolve(locationEvidence)", source, StringComparison.Ordinal);
        Assert.Contains("EnvironmentLightCandidateOrigin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RawRadius *", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationScenarioMatrixFailsClosedUntilItsEvidenceStandardIsMet()
    {
        var rows = EnvironmentLightApiFactContract.LocationScenarios;

        AssertScenario(rows, "farm-outdoors", "IsOutdoors+context+internal-name", "PendingRealMachine");
        AssertScenario(rows, "indoor-window", "LightContext.WindowLight+internal-name", "PendingRealMachine");
        AssertScenario(rows, "indoor-windowless", "type+context+internal-name+custom-field", "PendingRealMachine");
        AssertScenario(rows, "mine-dark-layer", "MineShaft.isDarkArea+getLightingColor", "PendingRealMachine");
        AssertScenario(rows, "volcano", "VolcanoDungeon+shared-lava-lights", "PendingRealMachine");
        AssertScenario(rows, "event-or-festival", "CurrentEvent+festival+IsTemporary", "PendingRealMachine");
        AssertScenario(rows, "modded-location", "type+context+internal-name+custom-field", "FallbackRequired");
        Assert.All(rows, row => Assert.False(row.ApiRecognitionAloneAuthorizesPitchBlack));
    }

    [Fact]
    public void VanillaNightVisionIsKnownInactiveAndNeverGuessesBuff26()
    {
        var source = ReadSource("KnownInactiveEnvironmentNightVisionProvider.cs");

        Assert.Contains("EnvironmentLightCapabilityStatus.Available", source, StringComparison.Ordinal);
        Assert.Contains("false,", source, StringComparison.Ordinal);
        Assert.Contains("EnvironmentLightReasonIds.NightVisionKnownInactive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("hasBuff", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEffectsOfRingMultiplier", source, StringComparison.Ordinal);
        Assert.Equal(
            "Buff 26 is a darkness penalty; only an explicit injected provider may confirm night vision.",
            EnvironmentLightApiFactContract.NightVisionBoundary
        );
    }

    [Fact]
    public void ExistingEnvironmentLightServiceRemainsTheOnlyReadOnlyFacade()
    {
        var source = ReadSource("EnvironmentLightService.cs");

        Assert.Contains("internal sealed class EnvironmentLightService", source, StringComparison.Ordinal);
        Assert.Contains("internal EnvironmentLightResult Evaluate(", source, StringComparison.Ordinal);
        Assert.Contains("EnvironmentLightSnapshotCaptureResult capture", source, StringComparison.Ordinal);
        Assert.Contains("classifier.Classify(snapshot, previous?.Result.Level)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerDanger", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticIsDefaultOffOwnerLocalBoundedAndDisplaysStage02Fields()
    {
        var source = ReadSource("SmapiEnvironmentLightDebugOverlay.cs");

        Assert.Contains("private bool visible;", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaximumLoggedReasons = 32", source, StringComparison.Ordinal);
        Assert.Contains("ds_sanity_light on|off|status|refresh|clear", source, StringComparison.Ordinal);
        Assert.Contains("EnvironmentLightCache.SampleCadenceTicks", source, StringComparison.Ordinal);
        Assert.Contains("owner = Game1.player", source, StringComparison.Ordinal);
        Assert.Contains("RawLocationLightLevel", source, StringComparison.Ordinal);
        Assert.Contains("IsDarkOut", source, StringComparison.Ordinal);
        Assert.Contains("NearestCandidateOrigin", source, StringComparison.Ordinal);
        Assert.Contains("NearestCandidateLightContext", source, StringComparison.Ordinal);
        Assert.Contains("NearestCandidateAttachedPlayerId", source, StringComparison.Ordinal);
        Assert.Contains("IsNearestCandidateDrawEligible", source, StringComparison.Ordinal);
        Assert.Contains("LocationRuleId", source, StringComparison.Ordinal);
        Assert.Contains("LocationRuleContractVersion", source, StringComparison.Ordinal);
        Assert.Contains("FinalVisibilityScore", source, StringComparison.Ordinal);
        Assert.Contains("RendererRevision", source, StringComparison.Ordinal);
        Assert.Contains("EvaluatorRevision", source, StringComparison.Ordinal);
        Assert.Contains("PitchBlackAuthorized", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity", source, StringComparison.Ordinal);

        Assert.Equal(
            new[]
            {
                "raw-light-level",
                "is-dark-out",
                "candidate-origin",
                "light-context",
                "attached-player-id",
                "draw-eligible",
                "location-rule-id+version",
                "final-visibility-score",
                "renderer+evaluator-revision",
                "pitch-black-authorized",
            },
            EnvironmentLightApiFactContract.Stage02DiagnosticAdditions
        );
    }

    [Fact]
    public void VanillaTakeDamageOrderHasNoSafePublicNonLethalCutPoint()
    {
        Assert.Equal(
            new[]
            {
                "event/passout/bed guards",
                "parry decision",
                "CanBeDamaged guards",
                "damager contact callback",
                "base damage random jitter",
                "BuffManager.Defense plus Book_Defense",
                "high-defense random reduction",
                "thorns side effect",
                "Yoba cancellation",
                "defense clamp to at least one",
                "Desert Festival multiplier",
                "health write clamped to zero",
                "trinket OnReceiveDamage",
                "phoenix ring revive",
                "temporary invincibility and feedback",
            },
            EnvironmentLightApiFactContract.TakeDamageOrder
        );
        Assert.False(EnvironmentLightApiFactContract.HasPublicPreHealthFloorCutPoint);
        Assert.Equal("BuffManager.Defense", EnvironmentLightApiFactContract.DefenseApi);
        Assert.Equal("not-present-in-current-pipeline", EnvironmentLightApiFactContract.ResilienceStatus);
    }

    [Fact]
    public void VanillaLocationDamageBroadcastIsPrivateAndRunsOnEachLocalPlayer()
    {
        var fact = EnvironmentLightApiFactContract.LocationDamageBroadcast;

        Assert.Equal("private NetEvent1<DamagePlayersEventArg>", fact.Entry);
        Assert.Equal("Game1.player", fact.ApplicationTarget);
        Assert.True(fact.UsesTakeDamage);
        Assert.True(fact.OverrideParry);
        Assert.True(fact.NullDamager);
        Assert.False(fact.IsStablePublicModSeam);
    }

    [Fact]
    public void FuturePhysicalDamageMustFailClosedUntilTheOwnerReceiptSeamExists()
    {
        var fact = EnvironmentLightApiFactContract.DamageAuthority;

        Assert.Equal("host-mutating-authority", fact.Authority);
        Assert.Equal("owner-local-physical-application", fact.ApplicationScope);
        Assert.Equal("stage-03-common-idempotent-receipt", fact.FutureContractOwner);
        Assert.False(fact.RemoteHostTakeDamageProvenSafe);
        Assert.False(fact.CallThenRestoreHealthAllowed);
        Assert.False(fact.Stage01CreatesDamageService);
    }

    [Fact]
    public void DarknessWarningResourceUsesTheSingleProcessCancelablePlaybackHandle()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AudioCueMetadataPath));
        var cueSets = document.RootElement.GetProperty("CueSets").EnumerateArray().ToArray();
        var darkness = Assert.Single(
            cueSets.Where(value => value.GetProperty("CueSetId").GetString() == "sanity.cue.darkness")
        );
        var cue = Assert.Single(darkness.GetProperty("Cues").EnumerateArray());
        Assert.Equal("sanity.cue.darkness.warning", cue.GetProperty("CueId").GetString());
        Assert.Equal("CancelableOneShot", cue.GetProperty("PlaybackMode").GetString());
        Assert.True(cue.GetProperty("IsPlaceholder").GetBoolean());

        Assert.Equal(
            new[] { "Ambience", "Whispers", "Danger", "DarknessWarning" },
            Enum.GetNames(typeof(SanityAudioLaneKind))
        );
        var methods = typeof(ISanityProcessAudioOutput)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(value => !value.IsSpecialName)
            .Select(value => value.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "Clear",
                "InvalidateResources",
                "SetDangerActive",
                "SetDarknessWarningActive",
                "SetPaused",
                "SetPoolActive",
                "SetSpecialEventAudioAllowed",
                "SetSuspended",
                "Tick",
                "TriggerDanger",
            },
            methods
        );

        var audioSource = File.ReadAllText(
            Path.Combine(AudioPoolContractRoot, "SanitySmapiAudioService.cs")
        );
        var resourceSource = File.ReadAllText(
            Path.Combine(ResourceContractRoot, "SanitySmapiResourceService.cs")
        );
        Assert.Contains("sanity.cue.darkness", audioSource, StringComparison.Ordinal);
        Assert.Contains("CancelableOneShot", audioSource, StringComparison.Ordinal);
        Assert.Contains("internal SanitySlotResourceResult LoadAudioCueSet", resourceSource, StringComparison.Ordinal);
        Assert.Contains("internal SanitySlotResourceResult GetAudioCueMetadata", resourceSource, StringComparison.Ordinal);
        Assert.Equal(
            "extend-the-existing-process-output-and-coordinator-in-stage-06-06",
            EnvironmentLightApiFactContract.DarknessWarningExtension
        );
    }

    [Fact]
    public void LegacyNightAndMineSanityBehaviorsRemainIndependentOfDangerLighting()
    {
        var night = ReadSource("Night.cs");
        var mine = ReadSource("MineShaft.cs");

        Assert.Contains("location.IsOutdoors", night, StringComparison.Ordinal);
        Assert.Contains("SanityChangeSource.Night", night, StringComparison.Ordinal);
        Assert.Contains("mineShaft.isDarkArea()", mine, StringComparison.Ordinal);
        Assert.Contains("location is VolcanoDungeon", mine, StringComparison.Ordinal);
        Assert.Contains("SanityChangeSource.Mine", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentLightService", night, StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentLightService", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", night, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", mine, StringComparison.Ordinal);
    }

    private static void AssertAssembly(
        EnvironmentAssemblyEvidence value,
        string name,
        string version,
        long length,
        string sha256
    )
    {
        Assert.Equal(name, value.Name);
        Assert.Equal(version, value.FileVersion);
        Assert.Equal(length, value.Length);
        Assert.Equal(sha256, value.Sha256);
    }

    private static void AssertField(
        IReadOnlyList<EnvironmentLightFieldFact> fields,
        string api,
        string unit,
        string ownership,
        bool finalBrightness
    )
    {
        var fact = Assert.Single(fields.Where(value => value.Api == api));
        Assert.Equal(unit, fact.Unit);
        Assert.Equal(ownership, fact.Ownership);
        Assert.Equal(finalBrightness, fact.IsFinalStandingPixelBrightness);
    }

    private static void AssertScenario(
        IReadOnlyList<EnvironmentLocationScenarioFact> rows,
        string scenario,
        string recognition,
        string status
    )
    {
        var row = Assert.Single(rows.Where(value => value.Scenario == scenario));
        Assert.Equal(recognition, row.Recognition);
        Assert.Equal(status, row.Status);
    }

    private static string ReadSource(string fileName)
    {
        return File.ReadAllText(Path.Combine(ContractRoot, fileName));
    }
}

internal sealed record EnvironmentAssemblyEvidence(
    string Name,
    string FileVersion,
    long Length,
    string Sha256
);

internal sealed record EnvironmentLightFieldFact(
    string Api,
    string Unit,
    string Ownership,
    bool IsFinalStandingPixelBrightness
);

internal sealed record EnvironmentLocationScenarioFact(
    string Scenario,
    string Recognition,
    string Status,
    bool ApiRecognitionAloneAuthorizesPitchBlack
);

internal sealed record EnvironmentLocationDamageBroadcastFact(
    string Entry,
    string ApplicationTarget,
    bool UsesTakeDamage,
    bool OverrideParry,
    bool NullDamager,
    bool IsStablePublicModSeam
);

internal sealed record EnvironmentDamageAuthorityFact(
    string Authority,
    string ApplicationScope,
    string FutureContractOwner,
    bool RemoteHostTakeDamageProvenSafe,
    bool CallThenRestoreHealthAllowed,
    bool Stage01CreatesDamageService
);

internal static class EnvironmentLightApiFactContract
{
    internal const string StardewXmlSha256 =
        "7A75EAF6DA11AB0B1F1FA9C519DD58565FD1A34682FF5BA7D1421654566EE341";
    internal const string SmapiXmlSha256 =
        "1F1A527BEB17637535B363B9826EE075F32261141533193598AE0FD6446FE129";
    internal const string NightVisionBoundary =
        "Buff 26 is a darkness penalty; only an explicit injected provider may confirm night vision.";
    internal const string DefenseApi = "BuffManager.Defense";
    internal const string ResilienceStatus = "not-present-in-current-pipeline";
    internal const bool HasPublicPreHealthFloorCutPoint = false;
    internal const string DarknessWarningExtension =
        "extend-the-existing-process-output-and-coordinator-in-stage-06-06";

    internal static readonly IReadOnlyList<EnvironmentAssemblyEvidence> Assemblies =
        new[]
        {
            new EnvironmentAssemblyEvidence(
                "Stardew Valley.dll",
                "1.6.15.24356",
                6_268_416,
                "7F1E5B8E58D2758B78570BA771BBEB03D33522F62188BF6C32EDF0CF626DEAEE"
            ),
            new EnvironmentAssemblyEvidence(
                "StardewValley.GameData.dll",
                "1.6.15.24356",
                96_768,
                "9C03497C2D2AC24C94E2F25B3C2FC39ECDE1BC97341E514C5F9FDCC1E759CB81"
            ),
            new EnvironmentAssemblyEvidence(
                "StardewModdingAPI.dll",
                "4.3.2.0",
                1_019_392,
                "2C03E8D3028977BBE8D128975FF091E1104DF31AE2B08724E72888FCA702B0A8"
            ),
        };

    internal static readonly IReadOnlyList<EnvironmentLightFieldFact> LightFields =
        new[]
        {
            new EnvironmentLightFieldFact("Character.StandingPixel", "map-pixel", "owner-local", false),
            new EnvironmentLightFieldFact("LightSource.position", "map-pixel", "light-source", false),
            new EnvironmentLightFieldFact("LightSource.radius", "raw-texture-scale", "light-source", false),
            new EnvironmentLightFieldFact("LightSource.color", "raw-tint", "light-source", false),
            new EnvironmentLightFieldFact("Game1.ambientLight/outdoorLight", "rgba-0..255", "current-screen", false),
            new EnvironmentLightFieldFact("Game1.currentLightSources", "current-draw-list", "current-screen", false),
            new EnvironmentLightFieldFact("GameLocation.sharedLights", "net-location-list", "location-shared", false),
        };

    internal static readonly IReadOnlyList<EnvironmentLocationScenarioFact> LocationScenarios =
        new[]
        {
            Scenario("farm-outdoors", "IsOutdoors+context+internal-name", "PendingRealMachine"),
            Scenario("indoor-window", "LightContext.WindowLight+internal-name", "PendingRealMachine"),
            Scenario("indoor-windowless", "type+context+internal-name+custom-field", "PendingRealMachine"),
            Scenario("mine-dark-layer", "MineShaft.isDarkArea+getLightingColor", "PendingRealMachine"),
            Scenario("volcano", "VolcanoDungeon+shared-lava-lights", "PendingRealMachine"),
            Scenario("event-or-festival", "CurrentEvent+festival+IsTemporary", "PendingRealMachine"),
            Scenario("modded-location", "type+context+internal-name+custom-field", "FallbackRequired"),
        };

    internal static readonly IReadOnlyList<string> Stage02DiagnosticAdditions =
        new[]
        {
            "raw-light-level",
            "is-dark-out",
            "candidate-origin",
            "light-context",
            "attached-player-id",
            "draw-eligible",
            "location-rule-id+version",
            "final-visibility-score",
            "renderer+evaluator-revision",
            "pitch-black-authorized",
        };

    internal static readonly IReadOnlyList<string> TakeDamageOrder =
        new[]
        {
            "event/passout/bed guards",
            "parry decision",
            "CanBeDamaged guards",
            "damager contact callback",
            "base damage random jitter",
            "BuffManager.Defense plus Book_Defense",
            "high-defense random reduction",
            "thorns side effect",
            "Yoba cancellation",
            "defense clamp to at least one",
            "Desert Festival multiplier",
            "health write clamped to zero",
            "trinket OnReceiveDamage",
            "phoenix ring revive",
            "temporary invincibility and feedback",
        };

    internal static readonly EnvironmentLocationDamageBroadcastFact LocationDamageBroadcast =
        new(
            "private NetEvent1<DamagePlayersEventArg>",
            "Game1.player",
            UsesTakeDamage: true,
            OverrideParry: true,
            NullDamager: true,
            IsStablePublicModSeam: false
        );

    internal static readonly EnvironmentDamageAuthorityFact DamageAuthority =
        new(
            "host-mutating-authority",
            "owner-local-physical-application",
            "stage-03-common-idempotent-receipt",
            RemoteHostTakeDamageProvenSafe: false,
            CallThenRestoreHealthAllowed: false,
            Stage01CreatesDamageService: false
        );

    private static EnvironmentLocationScenarioFact Scenario(
        string scenario,
        string recognition,
        string status
    )
    {
        return new EnvironmentLocationScenarioFact(
            scenario,
            recognition,
            status,
            ApiRecognitionAloneAuthorizesPitchBlack: false
        );
    }
}
