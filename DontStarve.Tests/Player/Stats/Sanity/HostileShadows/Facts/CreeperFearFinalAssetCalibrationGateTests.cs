using System.Security.Cryptography;
using System.Text.Json;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Facts;

public sealed class CreeperFearFinalAssetCalibrationGateTests
{
    private static readonly string[] StageSlotIds =
    {
        "sanity.animation.creeper-fear.attack",
        "sanity.animation.creeper-fear.death",
        "sanity.animation.creeper-fear.despawn",
        "sanity.animation.creeper-fear.idle",
        "sanity.animation.creeper-fear.move",
        "sanity.animation.creeper-fear.spawn",
        "sanity.animation.creeper-fear.taunt",
        "sanity.asset.creeper-fear.sprite",
        "sanity.cue.creeper-fear.attack",
        "sanity.cue.creeper-fear.death",
        "sanity.cue.creeper-fear.hurt",
        "sanity.cue.creeper-fear.taunt",
    };

    private static readonly string[] CueIds =
    {
        "sanity.cue.creeper-fear.attack",
        "sanity.cue.creeper-fear.death",
        "sanity.cue.creeper-fear.hurt",
        "sanity.cue.creeper-fear.taunt",
    };

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ManifestPath =>
        ResolveShippedPath("Asset/Sanity/Data/sanity-assets.json");

    private static string CreditsPath =>
        ResolveShippedPath("Asset/Sanity/Data/sanity-credits.json");

    private static string AnimationMetadataPath =>
        ResolveShippedPath("Asset/Sanity/Data/animations.json");

    private static string BindingMetadataPath =>
        ResolveShippedPath("Asset/Sanity/Data/resource-bindings.json");

    private static string AudioMetadataPath =>
        ResolveShippedPath("Asset/Sanity/Audio/audio-cues.json");

    [Fact]
    public void Final_delivery_is_absent_and_both_asset_gates_fail_closed_at_exact_boundaries()
    {
        var manifest = ReadManifest();
        var slots = manifest.Slots
            .Where(slot => StageSlotIds.Contains(slot.SlotId, StringComparer.Ordinal))
            .OrderBy(slot => slot.SlotId, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(StageSlotIds, slots.Select(slot => slot.SlotId));
        Assert.All(slots, slot =>
        {
            Assert.Equal(1, slot.ContractVersion);
            Assert.Equal(slot.Sha256, Sha256(ResolveShippedPath(slot.Path)));
        });

        var development = ValidateAssets(SanityAssetValidationGate.Development);
        Assert.True(
            development.Success,
            string.Join(Environment.NewLine, development.Issues.Select(issue => issue.Code))
        );
        Assert.All(
            StageSlotIds,
            slotId => Assert.Contains(slotId, development.PendingReplacementSlotIds)
        );

        var release = ValidateAssets(SanityAssetValidationGate.Release);
        Assert.False(release.Success);
        Assert.Equal(
            new[] { "release.credit-placeholder" },
            ReleaseCodes(release, "sanity.asset.creeper-fear.sprite")
        );
        foreach (var animationSlotId in StageSlotIds.Where(id =>
                     id.StartsWith("sanity.animation.", StringComparison.Ordinal)
                 ))
        {
            Assert.Equal(
                new[] { "release.asset-placeholder", "release.credit-placeholder" },
                ReleaseCodes(release, animationSlotId)
            );
        }
        foreach (var cueSlotId in CueIds)
        {
            Assert.Equal(
                new[]
                {
                    "release.asset-placeholder",
                    "release.credit-placeholder",
                    "release.dev-credit-group",
                    "release.permission-insufficient",
                },
                ReleaseCodes(release, cueSlotId)
            );
        }
    }

    [Fact]
    public void Four_by_twelve_sheet_is_machine_valid_but_visual_calibration_stays_provisional()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AnimationMetadataPath));
        var profile = Assert.Single(
            document.RootElement
                .GetProperty("AnimationProfiles")
                .EnumerateArray()
                .Where(profile =>
                    profile.GetProperty("AnimationProfileId").GetString()
                    == "sanity.animation.creeper-fear.profile"
                )
        );

        Assert.Equal("sanity.asset.creeper-fear.sprite", profile.GetProperty("TextureSlotId").GetString());
        Assert.Equal(64, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(64, profile.GetProperty("FrameHeight").GetInt32());
        Assert.Equal(12, profile.GetProperty("SheetRows").GetInt32());
        Assert.Equal("FourWayRows", profile.GetProperty("DirectionMode").GetString());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.Equal("ART-05", profile.GetProperty("CreditGroup").GetString());

        var collision = profile.GetProperty("Collision");
        Assert.Equal(
            "ActorOriginRelativeSourcePx",
            collision.GetProperty("CoordinateSpace").GetString()
        );
        Assert.True(collision.GetProperty("IsProvisional").GetBoolean());
        Assert.Equal(new[] { 3, 4 }, IntValues(collision.GetProperty("AttackActiveFrames")));

        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "sanity.animation.creeper-fear.move",
                "sanity.animation.creeper-fear.attack",
                "sanity.animation.creeper-fear.death",
                "sanity.animation.creeper-fear.spawn",
                "sanity.animation.creeper-fear.idle",
                "sanity.animation.creeper-fear.taunt",
            },
            states.Select(state => state.GetProperty("AnimationId").GetString())
        );
        Assert.Equal(new[] { 0, 4, 8, 9, 10, 11 }, states.Select(StateRow));
        Assert.Equal(new[] { 150, 125, 100, 90, 390, 488 }, states.Select(StateDuration));
        Assert.All(states, state =>
        {
            Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
            Assert.Equal(32, state.GetProperty("PivotSourcePx").GetProperty("X").GetInt32());
            Assert.Equal(48, state.GetProperty("PivotSourcePx").GetProperty("Y").GetInt32());
            Assert.Equal(4d, state.GetProperty("DrawScale").GetDouble());
            Assert.Equal("Actor", state.GetProperty("SortLayer").GetString());
            Assert.True(state.GetProperty("IsProvisional").GetBoolean());
        });

        var sheet = PngRgbaImage.Decode(
            ResolveShippedPath("Asset/Sanity/Sprites/Monsters/creeper-fear.png")
        );
        Assert.Equal(256, sheet.Width);
        Assert.Equal(768, sheet.Height);
    }

    [Fact]
    public void Four_actor_cues_are_valid_placeholder_wavs_not_final_audio_or_listening_evidence()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AudioMetadataPath));
        var cueSet = Assert.Single(
            document.RootElement
                .GetProperty("CueSets")
                .EnumerateArray()
                .Where(set =>
                    set.GetProperty("CueSetId").GetString() == "sanity.cue.creeper-fear"
                )
        );

        Assert.Equal("StopOnEntityOrWorldCleanup;ReleaseOnTitleDispose", cueSet.GetProperty("LifecyclePolicy").GetString());
        Assert.Equal(1, cueSet.GetProperty("MaxConcurrentInstances").GetInt32());
        var cues = cueSet
            .GetProperty("Cues")
            .EnumerateArray()
            .OrderBy(cue => cue.GetProperty("CueId").GetString(), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CueIds, cues.Select(cue => cue.GetProperty("CueId").GetString()));

        var expectedFrequencyByCue = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sanity.cue.creeper-fear.attack"] = 530,
            ["sanity.cue.creeper-fear.death"] = 610,
            ["sanity.cue.creeper-fear.hurt"] = 570,
            ["sanity.cue.creeper-fear.taunt"] = 490,
        };
        foreach (var cue in cues)
        {
            var cueId = cue.GetProperty("CueId").GetString()!;
            Assert.True(cue.GetProperty("Enabled").GetBoolean());
            Assert.True(cue.GetProperty("RequiredForRelease").GetBoolean());
            Assert.True(cue.GetProperty("IsPlaceholder").GetBoolean());
            Assert.Equal("PendingRealMachine", cue.GetProperty("ListeningStatus").GetString());

            var clip = Assert.Single(cue.GetProperty("Clips").EnumerateArray());
            Assert.True(clip.GetProperty("IsPlaceholder").GetBoolean());
            Assert.Equal("MissingFinalAsset", clip.GetProperty("SourceEvidence").GetProperty("Status").GetString());
            Assert.Equal("sanity4-missing-asset-ledger", clip.GetProperty("SourceEvidence").GetProperty("EvidenceId").GetString());
            Assert.Equal("PendingRealMachine", clip.GetProperty("ListeningEvidence").GetProperty("OverallStatus").GetString());
            var generator = clip.GetProperty("PlaceholderGenerator");
            Assert.Equal("DEV-PLACEHOLDER-TRIPLE-TRIANGLE-BEEP", generator.GetProperty("Kind").GetString());
            Assert.Equal(expectedFrequencyByCue[cueId], generator.GetProperty("BaseFrequencyHz").GetInt32());
            Assert.False(generator.GetProperty("FinalAssetEligible").GetBoolean());

            var inspection = SanityWavInspector.InspectFile(
                ResolveShippedPath(clip.GetProperty("Path").GetString()!)
            );
            Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Issues));
            Assert.Equal(1, inspection.FormatCode);
            Assert.Equal(2, inspection.Channels);
            Assert.Equal(44100, inspection.SampleRateHz);
            Assert.Equal(16, inspection.BitsPerSample);
            Assert.Equal(21168, inspection.Frames);
            Assert.Equal(clip.GetProperty("Sha256").GetString(), inspection.Sha256);
        }
    }

    [Fact]
    public void Frozen_binding_and_validators_do_not_promote_placeholder_status()
    {
        var animationJson = File.ReadAllText(AnimationMetadataPath);
        var bindingJson = File.ReadAllText(BindingMetadataPath);
        var visual = SanityHostileVisualContractValidator.Validate(animationJson, bindingJson);
        Assert.True(visual.Success, string.Join(Environment.NewLine, visual.Issues));

        var audio = SanityAudioContractValidator.ValidateFromFiles(
            AudioMetadataPath,
            ManifestPath,
            ShippedModRoot
        );
        Assert.True(audio.Success, string.Join(Environment.NewLine, audio.Issues));
        Assert.All(CueIds, cueId => Assert.Contains(cueId, audio.PlaceholderCueIds));
        Assert.All(CueIds, cueId => Assert.Contains(cueId, audio.PendingRealMachineCueIds));

        using var document = JsonDocument.Parse(bindingJson);
        var binding = Assert.Single(
            document.RootElement
                .GetProperty("Bindings")
                .EnumerateArray()
                .Where(binding =>
                    binding.GetProperty("AssetBindingId").GetString()
                    == "sanity.binding.creeper-fear"
                )
        );
        Assert.Equal("sanity.asset.creeper-fear.sprite", binding.GetProperty("SlotId").GetString());
        Assert.Equal("sanity.animation.creeper-fear.profile", binding.GetProperty("AnimationProfileId").GetString());
        Assert.Equal("sanity.cue.creeper-fear", binding.GetProperty("CueSetId").GetString());
        Assert.Equal("sanity.attack-motion.one-tile-v1", binding.GetProperty("AttackMotionPolicyId").GetString());

        var motion = Assert.Single(
            document.RootElement
                .GetProperty("AttackMotionPolicies")
                .EnumerateArray()
                .Where(policy =>
                    policy.GetProperty("AttackMotionPolicyId").GetString()
                    == "sanity.attack-motion.one-tile-v1"
                )
        );
        Assert.Equal(1d, motion.GetProperty("TotalAdvanceTiles").GetDouble());
        Assert.Equal(
            new[] { 0.5d, 0.5d, 0d, 0d },
            motion.GetProperty("FrameAdvanceTiles").EnumerateArray().Select(value => value.GetDouble())
        );
        Assert.True(motion.GetProperty("ResetAfterAnimation").GetBoolean());
    }

    [Fact]
    public void Art_05_remains_publicly_permitted_but_attribution_is_not_release_ready()
    {
        var parse = SanityCreditCatalogParser.Parse(File.ReadAllText(CreditsPath));
        Assert.True(parse.Success, parse.Reason);
        var catalog = Assert.IsType<SanityCreditCatalog>(parse.Catalog);
        var art05 = Assert.Single(
            catalog.Groups.Where(group => group.CreditGroup == "ART-05")
        );

        Assert.True(art05.IsPlaceholder);
        Assert.Equal("PENDING-PUBLIC-ATTRIBUTION", art05.AttributionText);
        Assert.Equal("sanity4-auth-ledger:ART-05", art05.SourceEvidenceId);
        Assert.Contains(
            "public mod distribution permitted",
            art05.PermissionScope,
            StringComparison.Ordinal
        );
    }

    private static SanityAssetManifest ReadManifest()
    {
        var parse = SanityAssetManifestParser.Parse(File.ReadAllText(ManifestPath));
        Assert.True(parse.Success, parse.Reason);
        return Assert.IsType<SanityAssetManifest>(parse.Manifest);
    }

    private static SanityAssetValidationResult ValidateAssets(SanityAssetValidationGate gate)
    {
        return SanityAssetValidator.ValidateFromFiles(
            ManifestPath,
            CreditsPath,
            ShippedModRoot,
            gate
        );
    }

    private static string[] ReleaseCodes(SanityAssetValidationResult result, string slotId)
    {
        return result.Issues
            .Where(issue =>
                issue.SlotId == slotId
                && issue.Code.StartsWith("release.", StringComparison.Ordinal)
            )
            .Select(issue => issue.Code)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
    }

    private static int[] IntValues(JsonElement array)
    {
        return array.EnumerateArray().Select(value => value.GetInt32()).ToArray();
    }

    private static int StateRow(JsonElement state)
    {
        return state.GetProperty("Row").GetInt32();
    }

    private static int StateDuration(JsonElement state)
    {
        return state.GetProperty("FrameDurationMs").GetInt32();
    }

    private static string ResolveShippedPath(string deploymentRelativePath)
    {
        Assert.True(
            SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                ShippedModRoot,
                deploymentRelativePath,
                out var path,
                out var reason
            ),
            reason
        );
        return path;
    }

    private static string Sha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
