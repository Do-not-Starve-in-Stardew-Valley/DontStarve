using System.Text.Json.Nodes;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;

public sealed class ShadowMonsterProfileVersionAdapterTests
{
    [Fact]
    public void Stardew1615DictionaryReturnTypeCanonicalizesToFrozenShape()
    {
        Assert.Equal(
            ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
            ShadowMonsterProfileVersionFactIds.DescribeMonstersReturnType(
                typeof(Dictionary<string, string>)
            )
        );
    }

    [Fact]
    public void NonMatchingClrReturnTypesRemainDistinctAndFailClosed()
    {
        var wrongShapes = new[]
        {
            typeof(Dictionary<string, object>),
            typeof(IReadOnlyDictionary<string, string>),
            typeof(string),
        };

        foreach (var wrongShape in wrongShapes)
        {
            var description =
                ShadowMonsterProfileVersionFactIds.DescribeMonstersReturnType(wrongShape);
            Assert.NotEqual(
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                description
            );

            var capability = Resolve(
                new ShadowMonsterProfileVersionFacts(
                    "1.6.15.24356",
                    description,
                    false,
                    null
                )
            );
            Assert.False(capability.IsAvailable);
            Assert.Equal(
                "shadow-profile.adapter.stardew-1.6-shape-drift",
                capability.Reason
            );
        }
    }

    [Fact]
    public void FrozenStardew16FactsResolveSlashStringAdapter()
    {
        var capability = Resolve(
            new ShadowMonsterProfileVersionFacts(
                "1.6.15",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );

        Assert.True(capability.IsAvailable);
        Assert.Equal(ShadowMonsterProfileAdapterShape.Stardew16SlashString, capability.Shape);
        Assert.IsType<Stardew16ShadowMonsterProfileAdapter>(capability.Adapter);
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,System.Object]", false)]
    [InlineData("System.Collections.Generic.IReadOnlyDictionary`2[System.String,System.String]", false)]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,System.String]", true)]
    public void Stardew16ShapeDriftFailsClosed(string returnType, bool hasStardew17Type)
    {
        var capability = Resolve(
            new ShadowMonsterProfileVersionFacts(
                "1.6.15",
                returnType,
                hasStardew17Type,
                hasStardew17Type ? "StardewValley.GameData.Monsters.MonsterData" : null
            )
        );

        Assert.False(capability.IsAvailable);
        Assert.Equal(ShadowMonsterProfileAdapterShape.Unknown, capability.Shape);
        Assert.Null(capability.Adapter);
        Assert.Equal("shadow-profile.adapter.stardew-1.6-shape-drift", capability.Reason);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "StardewValley.GameData.Monsters.MonsterData")]
    public void Stardew17RemainsUnavailableUntilReleasedShapeIsFactFrozen(
        bool hasBindableType,
        string? typeName
    )
    {
        var capability = Resolve(
            new ShadowMonsterProfileVersionFacts(
                "1.7.0",
                "System.Collections.Generic.Dictionary`2[System.String,System.Object]",
                hasBindableType,
                typeName
            )
        );

        Assert.False(capability.IsAvailable);
        Assert.Null(capability.Adapter);
        Assert.Equal(
            "shadow-profile.adapter.stardew-1.7-shape-unavailable",
            capability.Reason
        );
    }

    [Theory]
    [InlineData("not-a-version", "shadow-profile.adapter.game-version-invalid")]
    [InlineData("1.5.6", "shadow-profile.adapter.game-version-unsupported")]
    [InlineData("2.0.0", "shadow-profile.adapter.game-version-unsupported")]
    public void InvalidOrUnsupportedGameVersionFailsClosed(string version, string reason)
    {
        var capability = Resolve(
            new ShadowMonsterProfileVersionFacts(
                version,
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );

        Assert.False(capability.IsAvailable);
        Assert.Equal(reason, capability.Reason);
    }

    [Fact]
    public void Stardew16AdapterConvertsTileUnitsExactlyOnceWithoutGameClrTypes()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(catalog.TryGetProfile(ShadowMonsterDifficultyProfileIds.Compatible, out var difficulty));
        var adapter = Assert.IsType<Stardew16ShadowMonsterProfileAdapter>(
            ResolveCurrent().Adapter
        );

        var result = adapter.Adapt(
            difficulty!,
            ShadowMonsterAssetBindingIds.CreeperFear,
            tileSize: 64
        );

        Assert.True(result.Success);
        var runtime = Assert.IsType<ShadowMonsterRuntimeProfile>(result.Profile);
        Assert.Equal(20d, runtime.DetectionRadiusTiles);
        Assert.Equal(1280d, runtime.DetectionRadiusPixels);
        Assert.Equal(2d, runtime.AttackRangeTiles);
        Assert.Equal(128d, runtime.AttackRangePixels);
        Assert.Equal(300, runtime.MaxHealth);
        Assert.Equal("DirectThroughTerrain", runtime.WallTraversalMode);
        Assert.Equal("sanity.drop.void-essence-v1", runtime.DropTable.DropTableId);
        Assert.DoesNotContain(
            typeof(ShadowMonsterRuntimeProfile).GetProperties(),
            property =>
                property.PropertyType.FullName?.StartsWith("StardewValley.", StringComparison.Ordinal)
                == true
        );
    }

    [Fact]
    public void Stardew16AdapterAppliesOnlyItsCanonicalOverride()
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster =>
                monster["GameVersionOverrides"] = new JsonArray(
                    new JsonObject
                    {
                        ["GameVersionId"] = "Stardew1.6",
                        ["MaxHealth"] = 333,
                        ["AttackRangeTiles"] = 1.5d,
                    },
                    new JsonObject
                    {
                        ["GameVersionId"] = "Stardew1.7",
                        ["MaxHealth"] = 999,
                    }
                )
        );
        var validation = ShadowMonsterProfileTestFixture.Validate(documents);
        Assert.True(validation.Success, ShadowMonsterProfileTestFixture.FormatIssues(validation.Issues));
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(catalog.TryGetProfile(ShadowMonsterDifficultyProfileIds.Compatible, out var difficulty));
        var adapter = Assert.IsType<Stardew16ShadowMonsterProfileAdapter>(ResolveCurrent().Adapter);

        var result = adapter.Adapt(difficulty!, ShadowMonsterAssetBindingIds.CreeperFear, 64);

        Assert.True(result.Success);
        Assert.Equal(333, result.Profile!.MaxHealth);
        Assert.Equal(1.5d, result.Profile.AttackRangeTiles);
        Assert.Equal(96d, result.Profile.AttackRangePixels);
    }

    [Fact]
    public void AdapterRejectsUnknownBindingAndInvalidTileSize()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(catalog.TryGetProfile(ShadowMonsterDifficultyProfileIds.Compatible, out var difficulty));
        var adapter = Assert.IsType<Stardew16ShadowMonsterProfileAdapter>(ResolveCurrent().Adapter);

        var missing = adapter.Adapt(difficulty!, "sanity.binding.unknown", 64);
        var invalidTileSize = adapter.Adapt(
            difficulty!,
            ShadowMonsterAssetBindingIds.CreeperFear,
            0
        );

        Assert.False(missing.Success);
        Assert.Equal("shadow-profile.adapter.monster-profile-missing", missing.Reason);
        Assert.False(invalidTileSize.Success);
        Assert.Equal("shadow-profile.adapter.tile-size-invalid", invalidTileSize.Reason);
    }

    private static ShadowMonsterProfileAdapterCapability ResolveCurrent()
    {
        return Resolve(
            new ShadowMonsterProfileVersionFacts(
                "1.6.15",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );
    }

    private static ShadowMonsterProfileAdapterCapability Resolve(
        ShadowMonsterProfileVersionFacts facts
    )
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        return ShadowMonsterProfileVersionAdapterFactory.Resolve(catalog.Schema, facts);
    }
}
