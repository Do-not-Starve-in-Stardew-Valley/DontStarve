using System.Security.Cryptography;
using System.Text.Json;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Tests.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Facts;

public sealed class TerrorbeakAssetFactGateTests
{
    private const string AssetBindingId = "sanity.binding.terrorbeak";
    private const string SpriteSlotId = "sanity.asset.terrorbeak.sprite";
    private const string SpritePath = "Asset/Sanity/Sprites/Monsters/terrorbeak.png";
    private const string SpriteSha256 =
        "E1D8805044ED3E8C692D46530A9AAEE94E9C968DB03D9D4770A7F3D6ADB3E3CF";

    private static readonly string[] AnimationSlotIds =
    {
        "sanity.animation.terrorbeak.attack",
        "sanity.animation.terrorbeak.death",
        "sanity.animation.terrorbeak.idle",
        "sanity.animation.terrorbeak.move",
        "sanity.animation.terrorbeak.spawn",
        "sanity.animation.terrorbeak.taunt",
    };

    private static readonly string[] CueIds =
    {
        "sanity.cue.terrorbeak.attack",
        "sanity.cue.terrorbeak.death",
        "sanity.cue.terrorbeak.hurt",
        "sanity.cue.terrorbeak.taunt",
    };

    private static readonly IReadOnlyDictionary<string, (string Path, string Sha256)> CueFiles =
        new Dictionary<string, (string Path, string Sha256)>(StringComparer.Ordinal)
        {
            ["sanity.cue.terrorbeak.attack"] =
                (
                    "Asset/Sanity/Audio/Creatures/terrorbeak/attack.wav",
                    "C22047BA51A96EA39E591CF6FE1CCC94C097195342368C3FC8773E5D481E0103"
                ),
            ["sanity.cue.terrorbeak.death"] =
                (
                    "Asset/Sanity/Audio/Creatures/terrorbeak/death.wav",
                    "1336902E7DBE5350956AE2439AB3942FFE9E99D1B59B616744279D5E19B93736"
                ),
            ["sanity.cue.terrorbeak.hurt"] =
                (
                    "Asset/Sanity/Audio/Creatures/terrorbeak/hurt.wav",
                    "FE2208701DF5A05EF754E223DF388DE22A24078B9A78AC16AFCAFA2912B1EE53"
                ),
            ["sanity.cue.terrorbeak.taunt"] =
                (
                    "Asset/Sanity/Audio/Creatures/terrorbeak/taunt.wav",
                    "2F61A04979830A5811FE9246B7BD9F5F3AFFED8CC2E30229F1FB51AF0B96566C"
                ),
        };

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ManifestPath =>
        ResolveShippedPath("Asset/Sanity/Data/sanity-assets.json");

    private static string CreditsPath =>
        ResolveShippedPath("Asset/Sanity/Data/sanity-credits.json");

    private static string AnimationMetadataPath =>
        ResolveShippedPath("Asset/Sanity/Data/animations.json");

    [Fact]
    public void Manifest_and_sheet_keep_the_exact_candidate_and_provisional_release_gates()
    {
        var parse = SanityAssetManifestParser.Parse(File.ReadAllText(ManifestPath));
        Assert.True(parse.Success, parse.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(parse.Manifest);
        var slots = manifest.Slots
            .Where(slot => slot.SlotId.Contains("terrorbeak", StringComparison.Ordinal))
            .OrderBy(slot => slot.SlotId, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(11, slots.Length);
        var sprite = Assert.Single(slots.Where(slot => slot.SlotId == SpriteSlotId));
        Assert.Equal(SpritePath, sprite.Path);
        Assert.Equal(SpriteSha256, sprite.Sha256);
        Assert.Equal(SpriteSha256, Sha256(ResolveShippedPath(SpritePath)));
        Assert.False(sprite.IsPlaceholder);
        Assert.True(sprite.RequiredForRelease);
        Assert.Equal("ART-06", sprite.CreditGroup);

        var animations = slots
            .Where(slot => slot.SlotId.StartsWith("sanity.animation.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(AnimationSlotIds, animations.Select(slot => slot.SlotId));
        Assert.All(animations, slot =>
        {
            Assert.Equal("Asset/Sanity/Data/animations.json", slot.Path);
            Assert.True(slot.IsPlaceholder);
            Assert.True(slot.RequiredForRelease);
            Assert.Equal("ART-06", slot.CreditGroup);
        });

        var cues = slots
            .Where(slot => slot.SlotId.StartsWith("sanity.cue.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(CueIds, cues.Select(slot => slot.SlotId));
        Assert.All(cues, slot =>
        {
            Assert.Equal("Asset/Sanity/Audio/audio-cues.json", slot.Path);
            Assert.True(slot.IsPlaceholder);
            Assert.True(slot.RequiredForRelease);
            Assert.Equal("DEV-PLACEHOLDER", slot.CreditGroup);
        });

        var sheet = PngRgbaImage.Decode(ResolveShippedPath(SpritePath));
        Assert.Equal(192, sheet.Width);
        Assert.Equal(768, sheet.Height);
        Assert.Equal(45155, sheet.CountNonTransparentPixels(0, 0, sheet.Width, sheet.Height));
        for (var row = 0; row < 12; row++)
        {
            for (var frame = 0; frame < 4; frame++)
            {
                var count = sheet.CountNonTransparentPixels(frame * 48, row * 64, 48, 64);
                Assert.InRange(count, 1, (48 * 64) - 1);
            }
        }

        // ART-06 supplies independent baked left rows; a runtime mirror would silently replace authored pixels.
        for (var frame = 0; frame < 4; frame++)
        {
            Assert.False(
                sheet.RegionsAreHorizontalMirrors(frame * 48, 64, frame * 48, 192, 48, 64)
            );
            Assert.False(
                sheet.RegionsAreHorizontalMirrors(frame * 48, 320, frame * 48, 448, 48, 64)
            );
        }

        var development = ValidateAssets(SanityAssetValidationGate.Development);
        Assert.True(development.Success);
        Assert.All(slots, slot => Assert.Contains(slot.SlotId, development.PendingReplacementSlotIds));

        var release = ValidateAssets(SanityAssetValidationGate.Release);
        Assert.False(release.Success);
        Assert.Contains(
            release.Issues,
            issue => issue.SlotId == SpriteSlotId && issue.Code == "release.credit-placeholder"
        );
        Assert.Contains(
            release.Issues,
            issue =>
                issue.SlotId == "sanity.cue.terrorbeak.attack"
                && issue.Code == "release.dev-credit-group"
        );

        var creditParse = SanityCreditCatalogParser.Parse(File.ReadAllText(CreditsPath));
        Assert.True(creditParse.Success, creditParse.Reason);
        var credits = Assert.IsType<SanityCreditCatalog>(creditParse.Catalog);
        var art06 = Assert.Single(credits.Groups.Where(group => group.CreditGroup == "ART-06"));
        Assert.True(art06.IsPlaceholder);
        Assert.Equal("PENDING-PUBLIC-ATTRIBUTION", art06.AttributionText);
        Assert.Equal("sanity4-auth-ledger:ART-06", art06.SourceEvidenceId);
        Assert.Contains(
            "public mod distribution permitted",
            art06.PermissionScope,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Twelve_rows_pivot_boxes_hit_frames_and_fallback_cadence_match_load_only_preview()
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

        Assert.Equal(SpriteSlotId, profile.GetProperty("TextureSlotId").GetString());
        Assert.Equal(48, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(64, profile.GetProperty("FrameHeight").GetInt32());
        Assert.Equal(12, profile.GetProperty("SheetRows").GetInt32());
        Assert.Equal("FourWayRows", profile.GetProperty("DirectionMode").GetString());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.Equal("ART-06", profile.GetProperty("CreditGroup").GetString());
        AssertPoint(profile.GetProperty("ActorOriginSourcePx"), 0, 0);

        var collision = profile.GetProperty("Collision");
        Assert.Equal(
            "ActorOriginRelativeSourcePx",
            collision.GetProperty("CoordinateSpace").GetString()
        );
        AssertRectangle(collision.GetProperty("HurtBoxSourcePx"), 8, 32, 32, 32);
        AssertRectangle(collision.GetProperty("AttackBoxSourcePx"), -16, 8, 80, 80);
        Assert.Equal(
            new[] { 3, 4 },
            collision
                .GetProperty("AttackActiveFrames")
                .EnumerateArray()
                .Select(value => value.GetInt32())
        );
        Assert.True(collision.GetProperty("IsProvisional").GetBoolean());

        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        Assert.Equal(AnimationSlotIds, states.Select(StateId).OrderBy(id => id, StringComparer.Ordinal));
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
            states.Select(StateId)
        );
        Assert.Equal(new[] { 0, 4, 8, 9, 10, 11 }, states.Select(StateRow));
        Assert.Equal(new[] { true, false, false, false, true, false }, states.Select(StateLoops));
        Assert.All(states, state =>
        {
            Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
            Assert.Equal(100, state.GetProperty("FrameDurationMs").GetInt32());
            AssertPoint(state.GetProperty("PivotSourcePx"), 24, 48);
            Assert.Equal(4d, state.GetProperty("DrawScale").GetDouble());
            Assert.Equal("Actor", state.GetProperty("SortLayer").GetString());
            Assert.True(state.GetProperty("IsProvisional").GetBoolean());
            Assert.Contains(
                "no GIF timing evidence",
                state.GetProperty("ProvisionalReason").GetString(),
                StringComparison.Ordinal
            );
            Assert.Contains(
                "development fallback",
                state.GetProperty("ProvisionalReason").GetString(),
                StringComparison.Ordinal
            );
        });

        var move = states[0];
        var attack = states[1];
        Assert.Equal("BakedFourWayRows", move.GetProperty("Mirror").GetString());
        Assert.Equal("BakedFourWayRows", attack.GetProperty("Mirror").GetString());
        AssertDirectionRows(move, "Down:0:None", "Right:1:None", "Up:2:None", "Left:3:Baked");
        AssertDirectionRows(attack, "Down:4:None", "Right:5:None", "Up:6:None", "Left:7:Baked");
        Assert.Empty(move.GetProperty("HitFrames").EnumerateArray());
        Assert.Equal(
            new[] { 3, 4 },
            attack.GetProperty("HitFrames").EnumerateArray().Select(value => value.GetInt32())
        );
        Assert.All(states.Skip(2), state => Assert.Equal("None", state.GetProperty("Mirror").GetString()));

        var factory = new FactGateResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);
        var controller = new SanityResourcePreviewController(loader);
        var selection = controller.Select(
            "stage-09-01",
            "sanity.animation.terrorbeak.attack",
            frameIndex: 2
        );

        Assert.True(selection.Result.Success, selection.Result.Diagnostic.Reason);
        Assert.True(selection.Result.Diagnostic.IsPlaceholder);
        var preview = Assert.IsType<SanityVisualPreviewDefinition>(selection.Result.VisualPreview);
        Assert.Equal(SanityVisualPreviewKind.AnimationFrame, preview.Kind);
        Assert.Equal(new SanityResourceRectangle(96, 256, 48, 64), preview.SourceRectangle);
        Assert.Equal(new SanityResourcePoint(0, 0), preview.ActorOriginSourcePx);
        Assert.Equal(new SanityResourcePoint(24, 48), preview.PivotSourcePx);
        Assert.Equal(new SanityResourceRectangle(8, 32, 32, 32), preview.HurtBoxSourcePx);
        Assert.Equal(new SanityResourceRectangle(-16, 8, 80, 80), preview.AttackBoxSourcePx);
        Assert.Equal(4d, preview.DrawScale);
        Assert.False(preview.OwnerLocalOnly);
        Assert.True(preview.IsProvisional);
        Assert.True(controller.TryGet("stage-09-01", out var current));
        Assert.Same(selection, current);
        Assert.Equal(1, factory.TextureCreates);
        Assert.Equal(0, factory.SoundCreates);
    }

    [Fact]
    public void Death_is_the_current_removal_visual_while_despawn_is_absent_and_four_cues_stay_dev_only()
    {
        var factory = new FactGateResourceFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var death = loader.LoadSlot("sanity.animation.terrorbeak.death", frameIndex: 0);
        var despawn = loader.LoadSlot("sanity.animation.terrorbeak.despawn");
        var cueSet = loader.LoadCueSet("sanity.cue.terrorbeak");

        Assert.True(death.Success, death.Diagnostic.Reason);
        var preview = Assert.IsType<SanityVisualPreviewDefinition>(death.VisualPreview);
        Assert.Equal(new SanityResourceRectangle(0, 512, 48, 64), preview.SourceRectangle);
        Assert.Equal(4, preview.FrameCount);
        Assert.False(despawn.Success);
        Assert.Equal(SanityResourceCapabilityStatus.InvalidMetadata, despawn.Diagnostic.Status);
        Assert.Equal("resource.slot.unknown", despawn.Diagnostic.Code);

        Assert.True(cueSet.Success, cueSet.Diagnostic.Reason);
        var definition = Assert.IsType<SanityCueSetDefinition>(cueSet.CueSet!.Definition);
        Assert.Equal("terrorbeak-actor", definition.Group);
        Assert.Equal("StopOnEntityOrWorldCleanup;ReleaseOnTitleDispose", definition.LifecyclePolicy);
        Assert.Equal(1, definition.MaxConcurrentInstances);
        var cues = definition.Cues.OrderBy(cue => cue.CueId, StringComparer.Ordinal).ToArray();
        Assert.Equal(CueIds, cues.Select(cue => cue.CueId));
        Assert.All(cues, cue =>
        {
            Assert.Equal("OneShot", cue.PlaybackMode);
            Assert.True(cue.Enabled);
            Assert.True(cue.RequiredForRelease);
            Assert.True(cue.IsPlaceholder);
            Assert.Equal("PendingRealMachine", cue.ListeningStatus);
            var clip = Assert.Single(cue.Clips);
            Assert.Equal(CueFiles[cue.CueId].Path, clip.Path);
            Assert.Equal(CueFiles[cue.CueId].Sha256, clip.Sha256);
            Assert.Equal("sanity.wav.pcm-s16-stereo-44100-v1", clip.FormatId);
            Assert.True(clip.IsPlaceholder);
            Assert.Equal(21168, clip.DurationFrames);
            Assert.Equal(0.48d, clip.DurationSeconds);
        });
        Assert.Equal(1, factory.TextureCreates);
        Assert.Equal(4, factory.SoundCreates);
    }

    [Fact]
    public void Stardew16_adapter_preserves_speed_6_and_targeting_applies_nominal_tick_travel()
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
        Assert.Equal(6d, profile.MovementSpeed);
        Assert.Equal(20d, profile.DetectionRadiusTiles);
        Assert.Equal(1280d, profile.DetectionRadiusPixels);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(128d, profile.AttackRangePixels);

        var normal = Advance(profile.MovementSpeed, elapsedSeconds: 0.1d);
        var capped = Advance(profile.MovementSpeed, elapsedSeconds: 1d);

        Assert.True(normal.Valid);
        Assert.Equal(36d, normal.PositionX, precision: 10);
        Assert.Equal(0d, normal.PositionY, precision: 10);
        Assert.True(capped.Valid);
        Assert.Equal(90d, capped.PositionX, precision: 10);
        Assert.Equal(0d, capped.PositionY, precision: 10);
    }

    private static void AssertDirectionRows(JsonElement state, params string[] expected)
    {
        var actual = state
            .GetProperty("DirectionRows")
            .EnumerateArray()
            .Select(row =>
                $"{row.GetProperty("Direction").GetString()}:{row.GetProperty("Row").GetInt32()}:{row.GetProperty("Mirror").GetString()}"
            );
        Assert.Equal(expected, actual);
    }

    private static void AssertPoint(JsonElement point, int x, int y)
    {
        Assert.Equal(x, point.GetProperty("X").GetInt32());
        Assert.Equal(y, point.GetProperty("Y").GetInt32());
    }

    private static void AssertRectangle(
        JsonElement rectangle,
        int x,
        int y,
        int width,
        int height
    )
    {
        Assert.Equal(x, rectangle.GetProperty("X").GetInt32());
        Assert.Equal(y, rectangle.GetProperty("Y").GetInt32());
        Assert.Equal(width, rectangle.GetProperty("Width").GetInt32());
        Assert.Equal(height, rectangle.GetProperty("Height").GetInt32());
    }

    private static string StateId(JsonElement state)
    {
        return state.GetProperty("AnimationId").GetString()!;
    }

    private static int StateRow(JsonElement state)
    {
        return state.GetProperty("Row").GetInt32();
    }

    private static bool StateLoops(JsonElement state)
    {
        return state.GetProperty("Loop").GetBoolean();
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

    private static SanityAssetValidationResult ValidateAssets(SanityAssetValidationGate gate)
    {
        return SanityAssetValidator.ValidateFromFiles(
            ManifestPath,
            CreditsPath,
            ShippedModRoot,
            gate
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
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
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

        public SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes)
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
