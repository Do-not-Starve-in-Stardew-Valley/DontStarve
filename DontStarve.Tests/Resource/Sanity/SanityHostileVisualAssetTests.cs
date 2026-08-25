using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityHostileVisualAssetTests
{
    private const string HostileTemplateVersion = "sanity-hostile-visuals-v1";
    private const string AttackMotionPolicyId = "sanity.attack-motion.one-tile-v1";

    private static string ShippedModRoot => Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ManifestPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-assets.json");

    private static string CreditsPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-credits.json");

    private static string AnimationPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "animations.json");

    private static string BindingPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "resource-bindings.json");

    [Fact]
    public void ShippedHostileContractsPassTheStrictMetadataValidator()
    {
        var result = SanityHostileVisualContractValidator.Validate(
            File.ReadAllText(AnimationPath),
            File.ReadAllText(BindingPath)
        );

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Reason}"))
        );
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(
        "sanity.animation.creeper-fear.profile",
        "Asset/Sanity/Sprites/Monsters/creeper-fear.png",
        64,
        96,
        32,
        48,
        32,
        48,
        13
    )]
    [InlineData(
        "sanity.animation.terrorbeak.profile",
        "Asset/Sanity/Sprites/Monsters/terrorbeak.png",
        48,
        64,
        24,
        48,
        24,
        40,
        13
    )]
    public void RuntimeSheetsMatchTheFrozenFourByThirteenGrid(
        string profileId,
        string deploymentPath,
        int frameWidth,
        int frameHeight,
        int pivotX,
        int pivotY,
        int actorOriginX,
        int actorOriginY,
        int rows
    )
    {
        using var document = ReadAnimationMetadata();
        var profile = FindById(
            document.RootElement.GetProperty("AnimationProfiles"),
            "AnimationProfileId",
            profileId
        );
        Assert.Equal(HostileTemplateVersion, profile.GetProperty("TemplateVersion").GetString());
        Assert.Equal(frameWidth, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(frameHeight, profile.GetProperty("FrameHeight").GetInt32());
        Assert.Equal(rows, profile.GetProperty("SheetRows").GetInt32());
        Assert.Equal("FourWayRows", profile.GetProperty("DirectionMode").GetString());
        Assert.False(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.Equal(actorOriginX, profile.GetProperty("ActorOriginSourcePx").GetProperty("X").GetInt32());
        Assert.Equal(actorOriginY, profile.GetProperty("ActorOriginSourcePx").GetProperty("Y").GetInt32());

        var image = PngRgbaImage.Decode(ResolveShippedPath(deploymentPath));
        Assert.Equal(frameWidth * 4, image.Width);
        Assert.Equal(frameHeight * rows, image.Height);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                Assert.True(
                    image.CountNonTransparentPixels(
                        column * frameWidth,
                        row * frameHeight,
                        frameWidth,
                        frameHeight
                    ) > 0,
                    $"{profileId} row {row} frame {column + 1} is alpha-empty."
                );
            }
        }

        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        Assert.Equal(6, states.Length);
        Assert.All(states, state =>
        {
            Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
            Assert.True(state.GetProperty("FrameDurationMs").GetInt32() > 0);
            Assert.Equal(4.0, state.GetProperty("DrawScale").GetDouble());
            Assert.True(state.GetProperty("IsProvisional").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("ProvisionalReason").GetString()));
            Assert.Equal(pivotX, state.GetProperty("PivotSourcePx").GetProperty("X").GetInt32());
            Assert.Equal(pivotY, state.GetProperty("PivotSourcePx").GetProperty("Y").GetInt32());
        });
    }

    [Fact]
    public void CreeperDirectionRowsDoNotRequestRuntimeMirroring()
    {
        using var document = ReadAnimationMetadata();
        var profile = FindById(
            document.RootElement.GetProperty("AnimationProfiles"),
            "AnimationProfileId",
            "sanity.animation.creeper-fear.profile"
        );
        foreach (
            var state in profile.GetProperty("States").EnumerateArray().Where(state =>
                state.GetProperty("AnimationId").GetString() is
                    "sanity.animation.creeper-fear.move"
                    or "sanity.animation.creeper-fear.attack"
            )
        )
        {
            var left = Assert.Single(
                state.GetProperty("DirectionRows").EnumerateArray().Where(direction =>
                    direction.GetProperty("Direction").GetString() == "Left"
                )
            );
            Assert.Equal("None", left.GetProperty("Mirror").GetString());
        }
    }

    [Theory]
    [InlineData("sanity.binding.creeper-fear")]
    [InlineData("sanity.binding.terrorbeak")]
    public void Static_left_facing_uses_the_shared_runtime_flip_rule(string bindingId)
    {
        Assert.False(string.IsNullOrWhiteSpace(bindingId));
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                "HostileShadowMonster.cs"
            )
        );

        Assert.Contains("ResolveSpriteEffects", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowStateIds.Idle", source, StringComparison.Ordinal);
        Assert.Contains("HostileShadowFacingIds.Left", source, StringComparison.Ordinal);
        Assert.Contains("SpriteEffects.FlipHorizontally", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sanity.animation.creeper-fear.profile", 4, 48, 56, 48, 0, 64, 64, 64)]
    [InlineData("sanity.animation.terrorbeak.profile", 8, 32, 32, 32, -8, 16, 64, 64)]
    public void CollisionBoxesStayInActorOriginRelativeSourcePixels(
        string profileId,
        int hurtX,
        int hurtY,
        int hurtWidth,
        int hurtHeight,
        int attackX,
        int attackY,
        int attackWidth,
        int attackHeight
    )
    {
        using var document = ReadAnimationMetadata();
        var profile = FindById(
            document.RootElement.GetProperty("AnimationProfiles"),
            "AnimationProfileId",
            profileId
        );
        var collision = profile.GetProperty("Collision");
        Assert.Equal("ActorOriginRelativeSourcePx", collision.GetProperty("CoordinateSpace").GetString());
        AssertRectangle(collision.GetProperty("HurtBoxSourcePx"), hurtX, hurtY, hurtWidth, hurtHeight);
        AssertRectangle(collision.GetProperty("AttackBoxSourcePx"), attackX, attackY, attackWidth, attackHeight);
        Assert.Equal(
            new[] { 3, 4 },
            collision.GetProperty("AttackActiveFrames").EnumerateArray().Select(value => value.GetInt32())
        );
        Assert.True(collision.GetProperty("IsProvisional").GetBoolean());

        var attack = FindById(
            profile.GetProperty("States"),
            "AnimationId",
            profileId.Replace(".profile", ".attack", StringComparison.Ordinal)
        );
        Assert.Equal(
            new[] { 3, 4 },
            attack.GetProperty("HitFrames").EnumerateArray().Select(value => value.GetInt32())
        );
        Assert.All(
            profile.GetProperty("States").EnumerateArray().Where(state => state.GetProperty("AnimationId").GetString() != attack.GetProperty("AnimationId").GetString()),
            state => Assert.Empty(state.GetProperty("HitFrames").EnumerateArray())
        );
    }

    [Fact]
    public void BothHostileProfilesUseTheSharedExplicitCadenceFallback()
    {
        using var document = ReadAnimationMetadata();
        var profiles = document.RootElement.GetProperty("AnimationProfiles");
        var creeper = FindById(profiles, "AnimationProfileId", "sanity.animation.creeper-fear.profile");
        var creeperDurations = creeper.GetProperty("States")
            .EnumerateArray()
            .ToDictionary(
                state => state.GetProperty("AnimationId").GetString()!,
                state => state.GetProperty("FrameDurationMs").GetInt32(),
                StringComparer.Ordinal
            );
        Assert.Equal(100, creeperDurations["sanity.animation.creeper-fear.move"]);
        Assert.Equal(100, creeperDurations["sanity.animation.creeper-fear.attack"]);
        Assert.Equal(100, creeperDurations["sanity.animation.creeper-fear.death"]);
        Assert.Equal(100, creeperDurations["sanity.animation.creeper-fear.spawn"]);
        Assert.Equal(200, creeperDurations["sanity.animation.creeper-fear.idle"]);
        Assert.Equal(300, creeperDurations["sanity.animation.creeper-fear.taunt"]);

        var terrorbeak = FindById(profiles, "AnimationProfileId", "sanity.animation.terrorbeak.profile");
        var terrorbeakDurations = terrorbeak.GetProperty("States")
            .EnumerateArray()
            .ToDictionary(
                state => state.GetProperty("AnimationId").GetString()!,
                state => state.GetProperty("FrameDurationMs").GetInt32(),
                StringComparer.Ordinal
            );
        Assert.Equal(100, terrorbeakDurations["sanity.animation.terrorbeak.move"]);
        Assert.Equal(100, terrorbeakDurations["sanity.animation.terrorbeak.attack"]);
        Assert.Equal(100, terrorbeakDurations["sanity.animation.terrorbeak.death"]);
        Assert.Equal(100, terrorbeakDurations["sanity.animation.terrorbeak.spawn"]);
        Assert.Equal(200, terrorbeakDurations["sanity.animation.terrorbeak.idle"]);
        Assert.Equal(300, terrorbeakDurations["sanity.animation.terrorbeak.taunt"]);
        Assert.All(terrorbeak.GetProperty("States").EnumerateArray(), state =>
        {
            Assert.Contains(
                "development fallback",
                state.GetProperty("ProvisionalReason").GetString(),
                StringComparison.OrdinalIgnoreCase
            );
        });
    }

    [Fact]
    public void ResourceBindingsExposeOnlyStableResourceIdsAndOneTilePolicy()
    {
        using var document = ReadBindings();
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("ContractVersion").GetInt32());
        var policy = Assert.Single(root.GetProperty("AttackMotionPolicies").EnumerateArray());
        Assert.Equal(AttackMotionPolicyId, policy.GetProperty("AttackMotionPolicyId").GetString());
        Assert.Equal(1.0, policy.GetProperty("TotalAdvanceTiles").GetDouble());
        Assert.Equal(
            new[] { 0.5, 0.5, 0.0, 0.0 },
            policy.GetProperty("FrameAdvanceTiles").EnumerateArray().Select(value => value.GetDouble())
        );
        Assert.True(policy.GetProperty("ResetAfterAnimation").GetBoolean());

        var bindings = root.GetProperty("Bindings").EnumerateArray().ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.Equal(
            new[] { "sanity.binding.creeper-fear", "sanity.binding.terrorbeak" },
            bindings.Select(binding => binding.GetProperty("AssetBindingId").GetString()).OrderBy(value => value, StringComparer.Ordinal)
        );
        Assert.All(bindings, binding =>
        {
            Assert.Equal(AttackMotionPolicyId, binding.GetProperty("AttackMotionPolicyId").GetString());
            Assert.StartsWith("sanity.asset.", binding.GetProperty("SlotId").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("sanity.animation.", binding.GetProperty("AnimationProfileId").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("sanity.cue.", binding.GetProperty("CueSetId").GetString(), StringComparison.Ordinal);
            Assert.Equal(1, binding.GetProperty("ContractVersion").GetInt32());
        });

        var text = File.ReadAllText(BindingPath);
        Assert.DoesNotContain("references", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TestPackage", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("32+32", text, StringComparison.Ordinal);
        Assert.DoesNotContain("64px", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostileSheetAndSharedAnimationSlotsCarryExactManifestHashes()
    {
        var parseResult = SanityAssetManifestParser.Parse(File.ReadAllText(ManifestPath));
        Assert.True(parseResult.Success, parseResult.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(parseResult.Manifest);
        foreach (
            var slotId in new[]
            {
                "sanity.asset.creeper-fear.sprite",
                "sanity.asset.terrorbeak.sprite",
            }
        )
        {
            var slot = Assert.Single(manifest.Slots.Where(item => item.SlotId == slotId));
            Assert.False(slot.IsPlaceholder);
            Assert.NotNull(slot.Sha256);
            Assert.True(SanityAssetPathPolicy.TryNormalize(slot.Path, out _, out var reason), reason);
            Assert.DoesNotContain("references", slot.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(slot.Sha256, Sha256(ResolveShippedPath(slot.Path)));
        }

        var animationSlots = manifest.Slots
            .Where(slot => slot.Path == "Asset/Sanity/Data/animations.json")
            .ToArray();
        Assert.Equal(24, animationSlots.Length);
        Assert.All(animationSlots, slot => Assert.Equal(Sha256(AnimationPath), slot.Sha256));

        var development = SanityAssetValidator.ValidateFromFiles(
            ManifestPath,
            CreditsPath,
            ShippedModRoot,
            SanityAssetValidationGate.Development
        );
        Assert.True(development.Success);
        Assert.DoesNotContain(development.Issues, issue => issue.Code == "asset.required-missing");
        Assert.DoesNotContain(development.Issues, issue => issue.Code == "asset.optional-missing");
        Assert.Equal(50, development.PendingReplacementSlotIds.Count);
    }

    [Fact]
    public void OptionalCreeperDespawnRemainsSemanticallyUnimplemented()
    {
        using var document = ReadAnimationMetadata();
        var ids = document.RootElement
            .GetProperty("AnimationProfiles")
            .EnumerateArray()
            .SelectMany(profile => profile.GetProperty("States").EnumerateArray())
            .Select(state => state.GetProperty("AnimationId").GetString())
            .ToArray();
        Assert.DoesNotContain("sanity.animation.creeper-fear.despawn", ids);
        Assert.False(File.Exists(Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "audio-cues.json")));
        Assert.True(File.Exists(Path.Combine(ShippedModRoot, "Asset", "Sanity", "Audio", "audio-cues.json")));
    }

    [Fact]
    public void ValidatorFailsClosedForOutOfRangeRowsInvalidBoxesHitFramesUnknownPoliciesAndGameplayFields()
    {
        AssertRejected(
            (animations, _) =>
                FindState(animations, "sanity.animation.creeper-fear.move")["DirectionRows"]!
                    .AsArray()[0]!["Row"] = 13,
            "hostile.animation.row-out-of-range"
        );
        AssertRejected(
            (animations, _) =>
                FindProfile(animations, "sanity.animation.creeper-fear.profile")["Collision"]![
                    "HurtBoxSourcePx"
                ]!["Width"] = -1,
            "hostile.collision.invalid-size"
        );
        AssertRejected(
            (animations, _) =>
                FindState(animations, "sanity.animation.creeper-fear.attack")["HitFrames"] = new JsonArray(0, 4),
            "hostile.animation.hit-frame-out-of-range"
        );
        AssertRejected(
            (_, bindings) =>
                bindings["Bindings"]!.AsArray()[0]!["AttackMotionPolicyId"] = "sanity.attack-motion.unknown",
            "hostile.binding.unknown-policy"
        );
        AssertRejected(
            (animations, _) =>
                FindProfile(animations, "sanity.animation.creeper-fear.profile")["Damage"] = 20,
            "hostile.animation.gameplay-field-forbidden"
        );
    }

    private static void AssertRejected(
        Action<JsonObject, JsonObject> mutate,
        string expectedIssueCode
    )
    {
        var animations = JsonNode.Parse(File.ReadAllText(AnimationPath))!.AsObject();
        var bindings = JsonNode.Parse(File.ReadAllText(BindingPath))!.AsObject();
        mutate(animations, bindings);
        var result = SanityHostileVisualContractValidator.Validate(
            animations.ToJsonString(),
            bindings.ToJsonString()
        );
        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == expectedIssueCode);
    }

    private static JsonObject FindProfile(JsonObject root, string profileId)
    {
        return Assert.Single(
                root["AnimationProfiles"]!
                    .AsArray()
                    .Where(node => node!["AnimationProfileId"]!.GetValue<string>() == profileId)
            )!
            .AsObject();
    }

    private static JsonObject FindState(JsonObject root, string animationId)
    {
        return Assert.Single(
                root["AnimationProfiles"]!
                    .AsArray()
                    .SelectMany(profile => profile!["States"]!.AsArray())
                    .Where(state => state!["AnimationId"]!.GetValue<string>() == animationId)
            )!
            .AsObject();
    }

    private static JsonDocument ReadAnimationMetadata()
    {
        return JsonDocument.Parse(
            File.ReadAllText(AnimationPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }
        );
    }

    private static JsonDocument ReadBindings()
    {
        return JsonDocument.Parse(
            File.ReadAllText(BindingPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }
        );
    }

    private static JsonElement FindById(JsonElement array, string propertyName, string id)
    {
        return Assert.Single(array.EnumerateArray().Where(value => value.GetProperty(propertyName).GetString() == id));
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

    private static string ResolveShippedPath(string deploymentRelativePath)
    {
        Assert.True(
            SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                ShippedModRoot,
                deploymentRelativePath,
                out var absolutePath,
                out var reason
            ),
            reason
        );
        return absolutePath;
    }

    private static string Sha256(string path)
    {
        return BitConverter.ToString(SHA256.HashData(File.ReadAllBytes(path))).Replace("-", string.Empty);
    }
}
