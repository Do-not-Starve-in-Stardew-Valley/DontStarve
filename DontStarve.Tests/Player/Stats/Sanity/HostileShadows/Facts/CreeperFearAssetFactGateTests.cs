using System.Security.Cryptography;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Facts;

public sealed class CreeperFearAssetFactGateTests
{
    private const string AssetBindingId = "sanity.binding.creeper-fear";
    private const string SpriteSlotId = "sanity.asset.creeper-fear.sprite";
    private const string SpritePath = "Asset/Sanity/Sprites/Monsters/creeper-fear.png";
    private const string SpriteSha256 =
        "12C96EDB8842EF50A9408273A5736E1AD201CE06B52C7A28A3E6270523C5FF4F";

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ManifestPath =>
        ResolveShippedPath("Asset/Sanity/Data/sanity-assets.json");

    private static string CreditsPath =>
        ResolveShippedPath("Asset/Sanity/Data/sanity-credits.json");

    [Fact]
    public void Manifest_keeps_creeper_slots_paths_hashes_and_placeholder_gates_explicit()
    {
        var parse = SanityAssetManifestParser.Parse(File.ReadAllText(ManifestPath));
        Assert.True(parse.Success, parse.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(parse.Manifest);
        var slots = manifest.Slots
            .Where(slot => slot.SlotId.Contains("creeper-fear", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(15, slots.Length);
        var sprite = Assert.Single(slots.Where(slot => slot.SlotId == SpriteSlotId));
        Assert.Equal(SpritePath, sprite.Path);
        Assert.Equal(SpriteSha256, sprite.Sha256);
        Assert.Equal(SpriteSha256, Sha256(ResolveShippedPath(SpritePath)));
        Assert.False(sprite.IsPlaceholder);
        Assert.True(sprite.RequiredForRelease);
        Assert.Equal("ART-05", sprite.CreditGroup);

        var animations = slots
            .Where(slot => slot.SlotId.StartsWith("sanity.animation.", StringComparison.Ordinal))
            .OrderBy(slot => slot.SlotId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "sanity.animation.creeper-fear.attack",
                "sanity.animation.creeper-fear.death",
                "sanity.animation.creeper-fear.despawn",
                "sanity.animation.creeper-fear.idle",
                "sanity.animation.creeper-fear.move",
                "sanity.animation.creeper-fear.spawn",
                "sanity.animation.creeper-fear.taunt",
            },
            animations.Select(slot => slot.SlotId)
        );
        Assert.All(animations, slot =>
        {
            Assert.Equal("Asset/Sanity/Data/animations.json", slot.Path);
            Assert.True(slot.IsPlaceholder);
            Assert.Equal("ART-05", slot.CreditGroup);
        });
        Assert.False(
            Assert.Single(
                animations.Where(slot => slot.SlotId.EndsWith(".despawn", StringComparison.Ordinal))
            ).RequiredForRelease
        );
        Assert.All(
            animations.Where(slot => !slot.SlotId.EndsWith(".despawn", StringComparison.Ordinal)),
            slot => Assert.True(slot.RequiredForRelease)
        );

        var cues = slots
            .Where(slot => slot.SlotId.StartsWith("sanity.cue.", StringComparison.Ordinal))
            .OrderBy(slot => slot.SlotId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "sanity.cue.creeper-fear.attack",
                "sanity.cue.creeper-fear.chase",
                "sanity.cue.creeper-fear.death",
                "sanity.cue.creeper-fear.hurt-dull",
                "sanity.cue.creeper-fear.hurt-sharp",
                "sanity.cue.creeper-fear.idle",
                "sanity.cue.creeper-fear.taunt",
            },
            cues.Select(slot => slot.SlotId)
        );
        Assert.All(cues, slot =>
        {
            Assert.Equal("Asset/Sanity/Audio/audio-cues.json", slot.Path);
            Assert.False(slot.IsPlaceholder);
            Assert.True(slot.RequiredForRelease);
            Assert.Equal("ART-05", slot.CreditGroup);
        });

        var development = SanityAssetValidator.ValidateFromFiles(
            ManifestPath,
            CreditsPath,
            ShippedModRoot,
            SanityAssetValidationGate.Development
        );
        Assert.True(development.Success);
        Assert.All(
            slots,
            slot => Assert.Contains(slot.SlotId, development.PendingReplacementSlotIds)
        );

        var release = SanityAssetValidator.ValidateFromFiles(
            ManifestPath,
            CreditsPath,
            ShippedModRoot,
            SanityAssetValidationGate.Release
        );
        Assert.False(release.Success);
        Assert.Contains(
            release.Issues,
            issue => issue.SlotId == SpriteSlotId && issue.Code == "release.credit-placeholder"
        );
        Assert.Contains(
            release.Issues,
            issue =>
                issue.SlotId == "sanity.cue.creeper-fear.attack"
                && issue.Code == "release.credit-placeholder"
        );
    }

    [Fact]
    public void Load_only_preview_exposes_the_frozen_frame_pivot_and_debug_rectangles()
    {
        var factory = new FactGateResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);
        var controller = new SanityResourcePreviewController(loader);

        var selection = controller.Select(
            "stage-08-01",
            "sanity.animation.creeper-fear.attack",
            frameIndex: 2
        );

        Assert.True(selection.Result.Success, selection.Result.Diagnostic.Reason);
        Assert.True(selection.Result.Diagnostic.IsPlaceholder);
        var preview = Assert.IsType<SanityVisualPreviewDefinition>(
            selection.Result.VisualPreview
        );
        Assert.Equal(SanityVisualPreviewKind.AnimationFrame, preview.Kind);
        Assert.Equal(new SanityResourceRectangle(128, 384, 64, 96), preview.SourceRectangle);
        Assert.Equal(new SanityResourcePoint(32, 48), preview.ActorOriginSourcePx);
        Assert.Equal(new SanityResourcePoint(32, 48), preview.PivotSourcePx);
        Assert.Equal(new SanityResourceRectangle(4, 48, 56, 48), preview.HurtBoxSourcePx);
        Assert.Equal(new SanityResourceRectangle(0, 64, 64, 64), preview.AttackBoxSourcePx);
        Assert.Equal(4d, preview.DrawScale);
        Assert.False(preview.OwnerLocalOnly);
        Assert.True(preview.IsProvisional);
        Assert.True(controller.TryGet("stage-08-01", out var current));
        Assert.Same(selection, current);
        Assert.Equal(1, factory.TextureCreates);
        Assert.Equal(0, factory.SoundCreates);
    }

    [Fact]
    public void Death_row_is_the_current_removal_visual_while_despawn_stays_unimplemented()
    {
        var factory = new FactGateResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var death = loader.LoadSlot("sanity.animation.creeper-fear.death", frameIndex: 0);
        var despawn = loader.LoadSlot("sanity.animation.creeper-fear.despawn");
        var cueSet = loader.LoadCueSet("sanity.cue.creeper-fear");

        Assert.True(death.Success, death.Diagnostic.Reason);
        var preview = Assert.IsType<SanityVisualPreviewDefinition>(death.VisualPreview);
        Assert.Equal(new SanityResourceRectangle(0, 768, 64, 96), preview.SourceRectangle);
        Assert.Equal(4, preview.FrameCount);
        Assert.False(despawn.Success);
        Assert.Equal(SanityResourceCapabilityStatus.DisabledOptional, despawn.Diagnostic.Status);
        Assert.Equal("resource.preview.visual-slot-not-described", despawn.Diagnostic.Code);

        Assert.True(cueSet.Success, cueSet.Diagnostic.Reason);
        var definition = Assert.IsType<SanityCueSetDefinition>(cueSet.CueSet!.Definition);
        Assert.Equal(
            new[]
            {
                "sanity.cue.creeper-fear.attack",
                "sanity.cue.creeper-fear.chase",
                "sanity.cue.creeper-fear.death",
                "sanity.cue.creeper-fear.hurt-dull",
                "sanity.cue.creeper-fear.hurt-sharp",
                "sanity.cue.creeper-fear.idle",
                "sanity.cue.creeper-fear.taunt",
            },
            definition.Cues.Select(cue => cue.CueId).OrderBy(id => id, StringComparer.Ordinal)
        );
        var expectedClipCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sanity.cue.creeper-fear.attack"] = 6,
            ["sanity.cue.creeper-fear.chase"] = 8,
            ["sanity.cue.creeper-fear.death"] = 9,
            ["sanity.cue.creeper-fear.hurt-dull"] = 5,
            ["sanity.cue.creeper-fear.hurt-sharp"] = 8,
            ["sanity.cue.creeper-fear.idle"] = 8,
            ["sanity.cue.creeper-fear.taunt"] = 6,
        };
        Assert.All(definition.Cues, cue =>
        {
            Assert.Equal("OneShot", cue.PlaybackMode);
            Assert.True(cue.Enabled);
            Assert.True(cue.RequiredForRelease);
            Assert.False(cue.IsPlaceholder);
            Assert.Equal("PendingRealMachine", cue.ListeningStatus);
            Assert.Equal(expectedClipCounts[cue.CueId], cue.Clips.Count);
            Assert.All(cue.Clips, clip =>
            {
                Assert.StartsWith(
                    "Asset/Sanity/Audio/Creatures/shadow_creeper_fear/",
                    clip.Path,
                    StringComparison.Ordinal
                );
                Assert.Equal("sanity.wav.pcm-s16-stereo-48000-v1", clip.FormatId);
                Assert.False(clip.IsPlaceholder);
                Assert.True(clip.DurationFrames > 0);
                Assert.True(clip.DurationSeconds > 0d);
            });
        });
        Assert.Equal(1, factory.TextureCreates);
        Assert.Equal(50, factory.SoundCreates);
    }

    [Fact]
    public void Stardew16_adapter_preserves_speed_2_5_and_targeting_applies_nominal_tick_travel()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(
            catalog.TryGetProfile(
                ShadowMonsterDifficultyProfileIds.Compatible,
                out var difficulty
            )
        );
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.6.15.24356",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );

        var adapted = capability.Adapter!.Adapt(difficulty!, AssetBindingId, tileSize: 64);

        Assert.True(adapted.Success, adapted.Reason);
        var profile = Assert.IsType<ShadowMonsterRuntimeProfile>(adapted.Profile);
        Assert.Equal(2.5d, profile.MovementSpeed);
        Assert.Equal(20d, profile.DetectionRadiusTiles);
        Assert.Equal(1280d, profile.DetectionRadiusPixels);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(128d, profile.AttackRangePixels);

        var normal = Advance(profile.MovementSpeed, elapsedSeconds: 0.1d);
        var capped = Advance(profile.MovementSpeed, elapsedSeconds: 1d);

        Assert.True(normal.Valid);
        Assert.Equal(15d, normal.PositionX, precision: 10);
        Assert.Equal(0d, normal.PositionY, precision: 10);
        Assert.True(capped.Valid);
        Assert.Equal(37.5d, capped.PositionX, precision: 10);
        Assert.Equal(0d, capped.PositionY, precision: 10);
    }

    private static HostileShadowMovementDecision Advance(
        double movementSpeed,
        double elapsedSeconds
    )
    {
        return HostileShadowTargetingEngine.AdvancePosition(
            positionX: 0d,
            positionY: 0d,
            standingX: 0d,
            standingY: 0d,
            targetStandingX: 1000d,
            targetStandingY: 0d,
            movementSpeed,
            stopDistancePixels: 0d,
            elapsedSeconds
        );
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
        return BitConverter
            .ToString(SHA256.HashData(File.ReadAllBytes(path)))
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    private sealed class FactGateResourceFactory : ISanityPhysicalResourceFactory
    {
        internal int TextureCreates { get; private set; }

        internal int SoundCreates { get; private set; }

        public SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes)
        {
            TextureCreates++;
            return SanityPhysicalResourceCreationResult.Created(
                new FactGateResource(SanityPhysicalResourceKind.Texture, path)
            );
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(
            string path,
            byte[] bytes
        )
        {
            SoundCreates++;
            return SanityPhysicalResourceCreationResult.Created(
                new FactGateResource(SanityPhysicalResourceKind.SoundEffect, path)
            );
        }
    }

    private sealed class FactGateResource : ISanityPhysicalResource
    {
        internal FactGateResource(SanityPhysicalResourceKind kind, string path)
        {
            Kind = kind;
            Path = path;
        }

        public SanityPhysicalResourceKind Kind { get; }

        public string Path { get; }

        public void Dispose() { }
    }
}
