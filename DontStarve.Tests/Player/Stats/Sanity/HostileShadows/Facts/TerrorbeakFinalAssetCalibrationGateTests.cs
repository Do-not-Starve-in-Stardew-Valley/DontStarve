using System.Security.Cryptography;
using System.Text.Json;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Facts;

public sealed class TerrorbeakFinalAssetCalibrationGateTests
{
    private static readonly string[] StageSlotIds =
    {
        "sanity.animation.terrorbeak.attack",
        "sanity.animation.terrorbeak.death",
        "sanity.animation.terrorbeak.idle",
        "sanity.animation.terrorbeak.move",
        "sanity.animation.terrorbeak.spawn",
        "sanity.animation.terrorbeak.taunt",
        "sanity.asset.terrorbeak.sprite",
        "sanity.cue.terrorbeak.attack",
        "sanity.cue.terrorbeak.chase",
        "sanity.cue.terrorbeak.death",
        "sanity.cue.terrorbeak.hurt",
        "sanity.cue.terrorbeak.idle",
        "sanity.cue.terrorbeak.taunt",
    };

    private static readonly string[] CueIds =
    {
        "sanity.cue.terrorbeak.attack",
        "sanity.cue.terrorbeak.chase",
        "sanity.cue.terrorbeak.death",
        "sanity.cue.terrorbeak.hurt",
        "sanity.cue.terrorbeak.idle",
        "sanity.cue.terrorbeak.taunt",
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
            ReleaseCodes(release, "sanity.asset.terrorbeak.sprite")
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
                new[] { "release.credit-placeholder" },
                ReleaseCodes(release, cueSlotId)
            );
        }
    }

    [Fact]
    public void Four_by_thirteen_sheet_is_machine_valid_but_visual_calibration_stays_provisional()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AnimationMetadataPath));
        var profile = Assert.Single(
            document.RootElement
                .GetProperty("AnimationProfiles")
                .EnumerateArray()
                .Where(profile =>
                    profile.GetProperty("AnimationProfileId").GetString()
                    == "sanity.animation.terrorbeak.profile"
                )
        );

        Assert.Equal("sanity.asset.terrorbeak.sprite", profile.GetProperty("TextureSlotId").GetString());
        Assert.Equal(48, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(64, profile.GetProperty("FrameHeight").GetInt32());
        Assert.Equal(13, profile.GetProperty("SheetRows").GetInt32());
        Assert.Equal("FourWayRows", profile.GetProperty("DirectionMode").GetString());
        Assert.False(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.Equal("ART-06", profile.GetProperty("CreditGroup").GetString());

        var actorOrigin = profile.GetProperty("ActorOriginSourcePx");
        Assert.Equal(24, actorOrigin.GetProperty("X").GetInt32());
        Assert.Equal(40, actorOrigin.GetProperty("Y").GetInt32());

        var collision = profile.GetProperty("Collision");
        Assert.Equal(
            "ActorOriginRelativeSourcePx",
            collision.GetProperty("CoordinateSpace").GetString()
        );
        AssertRect(collision.GetProperty("HurtBoxSourcePx"), 8, 32, 32, 32);
        AssertRect(collision.GetProperty("AttackBoxSourcePx"), -8, 16, 64, 64);
        Assert.True(collision.GetProperty("IsProvisional").GetBoolean());
        Assert.Equal(new[] { 3, 4 }, IntValues(collision.GetProperty("AttackActiveFrames")));

        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "sanity.animation.terrorbeak.move",
                "sanity.animation.terrorbeak.attack",
                "sanity.animation.terrorbeak.death",
                "sanity.animation.terrorbeak.spawn",
                "sanity.animation.terrorbeak.idle",
                "sanity.animation.terrorbeak.taunt",
            },
            states.Select(state => state.GetProperty("AnimationId").GetString())
        );
        Assert.Equal(new[] { 0, 4, 8, 9, 10, 11 }, states.Select(StateRow));
        Assert.Equal(new[] { 100, 100, 100, 100, 200, 300 }, states.Select(StateDuration));
        Assert.All(states, state =>
        {
            Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
            Assert.Equal(24, state.GetProperty("PivotSourcePx").GetProperty("X").GetInt32());
            Assert.Equal(48, state.GetProperty("PivotSourcePx").GetProperty("Y").GetInt32());
            Assert.Equal(4d, state.GetProperty("DrawScale").GetDouble());
            Assert.Equal("Actor", state.GetProperty("SortLayer").GetString());
            Assert.True(state.GetProperty("IsProvisional").GetBoolean());
        });

        var sheet = PngRgbaImage.Decode(
            ResolveShippedPath("Asset/Sanity/Sprites/Monsters/terrorbeak.png")
        );
        Assert.Equal(192, sheet.Width);
        Assert.Equal(832, sheet.Height);
    }

    [Fact]
    public void All_actor_cues_are_real_audio_while_listening_evidence_remains_pending()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AudioMetadataPath));
        var cueSet = Assert.Single(
            document.RootElement
                .GetProperty("CueSets")
                .EnumerateArray()
                .Where(set =>
                    set.GetProperty("CueSetId").GetString() == "sanity.cue.terrorbeak"
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

        var expectedClipCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sanity.cue.terrorbeak.attack"] = 6,
            ["sanity.cue.terrorbeak.chase"] = 14,
            ["sanity.cue.terrorbeak.death"] = 11,
            ["sanity.cue.terrorbeak.hurt"] = 6,
            ["sanity.cue.terrorbeak.idle"] = 16,
            ["sanity.cue.terrorbeak.taunt"] = 10,
        };
        foreach (var cue in cues)
        {
            var cueId = cue.GetProperty("CueId").GetString()!;
            Assert.True(cue.GetProperty("Enabled").GetBoolean());
            Assert.True(cue.GetProperty("RequiredForRelease").GetBoolean());
            Assert.False(cue.GetProperty("IsPlaceholder").GetBoolean());
            Assert.Equal("PendingRealMachine", cue.GetProperty("ListeningStatus").GetString());

            var clips = cue.GetProperty("Clips").EnumerateArray().ToArray();
            Assert.Equal(expectedClipCounts[cueId], clips.Length);
            Assert.All(clips, clip =>
            {
                Assert.StartsWith(
                    "Asset/Sanity/Audio/Creatures/shadow_terrorbeak/",
                    clip.GetProperty("Path").GetString(),
                    StringComparison.Ordinal
                );
                Assert.Equal("sanity.wav.pcm-s16-stereo-48000-v1", clip.GetProperty("FormatId").GetString());
                Assert.False(clip.GetProperty("IsPlaceholder").GetBoolean());
                Assert.Equal("AvailableReadOnlyEvidence", clip.GetProperty("SourceEvidence").GetProperty("Status").GetString());
                Assert.Equal("PendingRealMachine", clip.GetProperty("ListeningEvidence").GetProperty("OverallStatus").GetString());
            });
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
        Assert.All(CueIds, cueId =>
        {
            Assert.DoesNotContain(cueId, audio.PlaceholderCueIds);
            Assert.Contains(cueId, audio.PendingRealMachineCueIds);
        });

        using var document = JsonDocument.Parse(bindingJson);
        var binding = Assert.Single(
            document.RootElement
                .GetProperty("Bindings")
                .EnumerateArray()
                .Where(binding =>
                    binding.GetProperty("AssetBindingId").GetString()
                    == "sanity.binding.terrorbeak"
                )
        );
        Assert.Equal("sanity.asset.terrorbeak.sprite", binding.GetProperty("SlotId").GetString());
        Assert.Equal("sanity.animation.terrorbeak.profile", binding.GetProperty("AnimationProfileId").GetString());
        Assert.Equal("sanity.cue.terrorbeak", binding.GetProperty("CueSetId").GetString());
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
    public void Art_06_remains_publicly_permitted_but_attribution_is_not_release_ready()
    {
        var parse = SanityCreditCatalogParser.Parse(File.ReadAllText(CreditsPath));
        Assert.True(parse.Success, parse.Reason);
        var catalog = Assert.IsType<SanityCreditCatalog>(parse.Catalog);
        var art06 = Assert.Single(
            catalog.Groups.Where(group => group.CreditGroup == "ART-06")
        );

        Assert.True(art06.IsPlaceholder);
        Assert.Equal("PENDING-PUBLIC-ATTRIBUTION", art06.AttributionText);
        Assert.Equal("sanity4-auth-ledger:ART-06", art06.SourceEvidenceId);
        Assert.Contains(
            "public mod distribution permitted",
            art06.PermissionScope,
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

    private static void AssertRect(JsonElement rectangle, int x, int y, int width, int height)
    {
        Assert.Equal(x, rectangle.GetProperty("X").GetInt32());
        Assert.Equal(y, rectangle.GetProperty("Y").GetInt32());
        Assert.Equal(width, rectangle.GetProperty("Width").GetInt32());
        Assert.Equal(height, rectangle.GetProperty("Height").GetInt32());
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
