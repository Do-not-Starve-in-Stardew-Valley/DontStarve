using System.Security.Cryptography;
using System.Text.Json;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityHarmlessVisualAssetTests
{
    private const string TemplateVersion = "sanity-harmless-visuals-v1";
    private const string AnimationMetadataRelativePath = "Asset/Sanity/Data/animations.json";

    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ShippedManifestPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-assets.json");

    private static string AnimationMetadataPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "animations.json");

    [Fact]
    public void MetadataPreservesTheFourHarmlessAnimationProfiles()
    {
        using var document = ReadMetadata();
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("ContractVersion").GetInt32());
        Assert.Equal(TemplateVersion, root.GetProperty("TemplateVersion").GetString());
        Assert.Equal(
            new[]
            {
                "AnimationProfiles",
                "ContractVersion",
                "HostileTemplateVersion",
                "OverlayProfiles",
                "SchemaVersion",
                "StaticSpriteProfiles",
                "TemplateVersion",
            },
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
        );

        var profiles = root.GetProperty("AnimationProfiles").EnumerateArray().ToArray();
        var harmlessProfileIds = new HashSet<string>(
            new[]
            {
                "sanity.animation.dark-hand.profile",
                "sanity.animation.dark-watcher.profile",
                "sanity.animation.eyes.profile",
                "sanity.animation.mr-skitts.profile",
            },
            StringComparer.Ordinal
        );
        var harmlessProfiles = profiles
            .Where(profile => harmlessProfileIds.Contains(profile.GetProperty("AnimationProfileId").GetString()!))
            .ToArray();
        Assert.Equal(
            harmlessProfileIds.OrderBy(value => value, StringComparer.Ordinal),
            harmlessProfiles
                .Select(profile => profile.GetProperty("AnimationProfileId").GetString())
                .OrderBy(value => value, StringComparer.Ordinal)
        );

        var animationIds = harmlessProfiles
            .SelectMany(profile => profile.GetProperty("States").EnumerateArray())
            .Select(state => state.GetProperty("AnimationId").GetString())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "sanity.animation.dark-hand.appear",
                "sanity.animation.dark-hand.disappear",
                "sanity.animation.dark-hand.interact",
                "sanity.animation.dark-hand.move",
                "sanity.animation.dark-hand.retreat",
                "sanity.animation.dark-watcher.appear",
                "sanity.animation.dark-watcher.disappear",
                "sanity.animation.dark-watcher.idle",
                "sanity.animation.eyes.blink",
                "sanity.animation.mr-skitts.disappear",
                "sanity.animation.mr-skitts.idle",
            },
            animationIds
        );

        var text = File.ReadAllText(AnimationMetadataPath);
        Assert.Contains("creeper-fear", text, StringComparison.Ordinal);
        Assert.Contains("terrorbeak", text, StringComparison.Ordinal);
        Assert.DoesNotContain("audio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sanity.animation.mr-skitts.profile", "Asset/Sanity/Sprites/Illusions/mr-skitts.png", 184, 112, 2)]
    [InlineData("sanity.animation.dark-hand.profile", "Asset/Sanity/Sprites/Illusions/dark-hand.png", 192, 176, 5)]
    [InlineData("sanity.animation.dark-watcher.profile", "Asset/Sanity/Sprites/Illusions/dark-watcher.png", 904, 128, 3)]
    [InlineData("sanity.animation.eyes.profile", "Asset/Sanity/Sprites/Illusions/eyes.png", 64, 32, 1)]
    public void AnimationSheetsMatchTheirGridPivotAndAlphaContracts(
        string profileId,
        string relativePath,
        int frameWidth,
        int frameHeight,
        int expectedRows
    )
    {
        using var document = ReadMetadata();
        var profile = FindById(
            document.RootElement.GetProperty("AnimationProfiles"),
            "AnimationProfileId",
            profileId
        );
        Assert.Equal(frameWidth, profile.GetProperty("FrameWidth").GetInt32());
        Assert.Equal(frameHeight, profile.GetProperty("FrameHeight").GetInt32());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());

        var image = PngRgbaImage.Decode(ResolveShippedPath(relativePath));
        Assert.Equal(frameWidth * 4, image.Width);
        Assert.Equal(frameHeight * expectedRows, image.Height);

        var states = profile.GetProperty("States").EnumerateArray().ToArray();
        Assert.Equal(expectedRows, states.Length);
        Assert.Equal(
            Enumerable.Range(0, expectedRows),
            states.Select(state => state.GetProperty("Row").GetInt32()).OrderBy(row => row)
        );
        foreach (var state in states)
        {
            Assert.Equal(4, state.GetProperty("FrameCount").GetInt32());
            Assert.True(state.GetProperty("FrameDurationMs").GetInt32() > 0);
            Assert.Equal(1.0, state.GetProperty("DrawScale").GetDouble());
            Assert.Equal("None", state.GetProperty("Mirror").GetString());
            Assert.True(state.GetProperty("IsProvisional").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("ProvisionalReason").GetString()));

            var pivot = state.GetProperty("PivotSourcePx");
            Assert.InRange(pivot.GetProperty("X").GetInt32(), 0, frameWidth - 1);
            Assert.InRange(pivot.GetProperty("Y").GetInt32(), 0, frameHeight - 1);
            var row = state.GetProperty("Row").GetInt32();
            var allowedEmpty = state
                .GetProperty("AllowedEmptyFrames")
                .EnumerateArray()
                .Select(value => value.GetInt32())
                .ToHashSet();
            for (var column = 0; column < 4; column++)
            {
                var alphaPixels = image.CountNonTransparentPixels(
                    column * frameWidth,
                    row * frameHeight,
                    frameWidth,
                    frameHeight
                );
                if (allowedEmpty.Contains(column))
                    Assert.Equal(0, alphaPixels);
                else
                    Assert.True(alphaPixels > 0, $"{profileId} row {row} frame {column} is unexpectedly alpha-empty.");
            }
        }
    }

    [Fact]
    public void EveryAnimationFieldThatNeedsGamePreviewRemainsExplicitlyProvisional()
    {
        using var document = ReadMetadata();
        foreach (
            var profile in document.RootElement
                .GetProperty("AnimationProfiles")
                .EnumerateArray()
                .Where(profile => profile.GetProperty("OwnerLocalOnly").GetBoolean())
        )
        {
            Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
            Assert.Equal(1, profile.GetProperty("ContractVersion").GetInt32());
            Assert.Equal("None", profile.GetProperty("DirectionMode").GetString());
            Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
            foreach (var state in profile.GetProperty("States").EnumerateArray())
            {
                Assert.True(state.GetProperty("IsProvisional").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("ProvisionalReason").GetString()));
            }
        }
    }

    [Fact]
    public void DangerBorderIsAViewportSafeNineSlicePlaceholder()
    {
        using var document = ReadMetadata();
        var overlays = document.RootElement.GetProperty("OverlayProfiles").EnumerateArray().ToArray();
        var profile = Assert.Single(overlays);
        Assert.Equal("sanity.overlay.danger-border.profile", profile.GetProperty("OverlayProfileId").GetString());
        Assert.Equal("sanity.asset.danger-border.overlay", profile.GetProperty("TextureSlotId").GetString());
        Assert.Equal("NineSlice", profile.GetProperty("StretchMode").GetString());
        Assert.True(profile.GetProperty("ViewportSafe").GetBoolean());
        Assert.True(profile.GetProperty("UiScaleSafe").GetBoolean());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());

        var image = PngRgbaImage.Decode(ResolveShippedPath("Asset/Sanity/Overlays/danger-border.png"));
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        var slice = profile.GetProperty("SliceSourcePx");
        var left = slice.GetProperty("Left").GetInt32();
        var top = slice.GetProperty("Top").GetInt32();
        var right = slice.GetProperty("Right").GetInt32();
        var bottom = slice.GetProperty("Bottom").GetInt32();
        Assert.True(left > 0 && top > 0 && right > 0 && bottom > 0);
        Assert.True(left + right < image.Width && top + bottom < image.Height);
        Assert.Equal(
            0,
            image.CountNonTransparentPixels(
                left,
                top,
                image.Width - left - right,
                image.Height - top - bottom
            )
        );
        Assert.True(image.CountNonTransparentPixels(0, 0, left, top) > 0);
        Assert.True(image.CountNonTransparentPixels(image.Width - right, 0, right, top) > 0);
        Assert.True(image.CountNonTransparentPixels(0, image.Height - bottom, left, bottom) > 0);
        Assert.True(image.CountNonTransparentPixels(image.Width - right, image.Height - bottom, right, bottom) > 0);
        Assert.True(image.ContainsColor(255, 0, 255, 255));
    }

    [Theory]
    [InlineData("sanity.static.beard-rabbit.profile", "Asset/Sanity/Sprites/World/beard-rabbit.png", 64, 64)]
    [InlineData("sanity.static.beard-item.profile", "Asset/Sanity/Sprites/Items/beard.png", 16, 16)]
    public void StaticWorldAndItemPlaceholdersStayOwnerLocal(
        string profileId,
        string relativePath,
        int width,
        int height
    )
    {
        using var document = ReadMetadata();
        var profile = FindById(
            document.RootElement.GetProperty("StaticSpriteProfiles"),
            "StaticSpriteProfileId",
            profileId
        );
        Assert.Equal(width, profile.GetProperty("Width").GetInt32());
        Assert.Equal(height, profile.GetProperty("Height").GetInt32());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.False(profile.GetProperty("SharedObjectIdMutation").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.True(profile.GetProperty("IsProvisional").GetBoolean());

        var image = PngRgbaImage.Decode(ResolveShippedPath(relativePath));
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.True(image.CountNonTransparentPixels(0, 0, width, height) > 0);
        Assert.True(image.ContainsColor(255, 0, 255, 255));
    }

    [Fact]
    public void StageThreePngSlotsHaveExactManifestHashesAndPlaceholderOwnership()
    {
        var manifestResult = SanityAssetManifestParser.Parse(File.ReadAllText(ShippedManifestPath));
        Assert.True(manifestResult.Success, manifestResult.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(manifestResult.Manifest);
        var expected = new Dictionary<string, (bool IsPlaceholder, string CreditGroup)>(StringComparer.Ordinal)
        {
            ["sanity.asset.mr-skitts.sprite"] = (false, "ART-01"),
            ["sanity.asset.dark-hand.sprite"] = (false, "ART-02"),
            ["sanity.asset.dark-watcher.sprite"] = (false, "ART-03"),
            ["sanity.asset.eyes.sprite"] = (true, "DEV-PLACEHOLDER"),
            ["sanity.asset.danger-border.overlay"] = (true, "DEV-PLACEHOLDER"),
            ["sanity.asset.beard-rabbit.sprite"] = (true, "DEV-PLACEHOLDER"),
            ["sanity.asset.beard-item.icon"] = (true, "DEV-PLACEHOLDER"),
        };

        foreach (var pair in expected)
        {
            var slot = Assert.Single(manifest.Slots.Where(slot => slot.SlotId == pair.Key));
            Assert.Equal(pair.Value.IsPlaceholder, slot.IsPlaceholder);
            Assert.Equal(pair.Value.CreditGroup, slot.CreditGroup);
            Assert.NotNull(slot.Sha256);
            var path = ResolveShippedPath(slot.Path);
            Assert.True(File.Exists(path), slot.Path);
            Assert.Equal(slot.Sha256, Sha256(path));
        }
    }

    [Fact]
    public void SharedAnimationFileHashCoversTheImplementedHarmlessAndHostileRows()
    {
        var manifestResult = SanityAssetManifestParser.Parse(File.ReadAllText(ShippedManifestPath));
        Assert.True(manifestResult.Success, manifestResult.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(manifestResult.Manifest);
        var animationSlots = manifest.Slots
            .Where(slot => slot.Path == AnimationMetadataRelativePath)
            .ToArray();
        Assert.Equal(24, animationSlots.Length);
        Assert.Single(animationSlots.Select(slot => slot.Sha256).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(Sha256(AnimationMetadataPath), animationSlots[0].Sha256);

        using var document = ReadMetadata();
        var implementedIds = document.RootElement
            .GetProperty("AnimationProfiles")
            .EnumerateArray()
            .SelectMany(profile => profile.GetProperty("States").EnumerateArray())
            .Select(state => state.GetProperty("AnimationId").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(23, implementedIds.Count);
        Assert.Contains("sanity.animation.creeper-fear.attack", implementedIds);
        Assert.Contains("sanity.animation.terrorbeak.attack", implementedIds);
        Assert.DoesNotContain("sanity.animation.creeper-fear.despawn", implementedIds);
    }

    private static JsonDocument ReadMetadata()
    {
        return JsonDocument.Parse(
            File.ReadAllText(AnimationMetadataPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }
        );
    }

    private static JsonElement FindById(JsonElement array, string propertyName, string id)
    {
        return Assert.Single(
            array.EnumerateArray().Where(value => value.GetProperty(propertyName).GetString() == id)
        );
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
