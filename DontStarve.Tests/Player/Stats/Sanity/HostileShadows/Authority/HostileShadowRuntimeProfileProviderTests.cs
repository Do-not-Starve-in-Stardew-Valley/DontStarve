using DontStarve.Config;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Tests.Config;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowRuntimeProfileProviderTests
{
    [Fact]
    public void Provider_consumes_the_validated_catalog_adapter_and_current_typed_selection()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.6.15.24356",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );
        var registry = ConfigTestData.LoadShippedRegistry();
        var access = new MemoryFlatConfigFileAccess(
            "{\"MonsterDifficultyProfile\":\"Fusion\"}"
        );
        var load = FlatConfigValueStore.Load(registry, access);
        var store = Assert.IsType<FlatConfigValueStore>(load.Store);
        var resolver = new TypedConfigResolver(registry, store);
        var provider = new HostileShadowRuntimeProfileProvider(
            catalog,
            capability,
            resolver,
            tileSize: 64,
            unavailableReason: "test-unavailable"
        );

        var fusion = provider.Resolve(ShadowMonsterAssetBindingIds.CreeperFear);
        var saved = store.TrySave(
            new Dictionary<string, ConfigValue>
            {
                [ConfigKeys.MonsterDifficultyProfile] = ConfigValue.Enum(
                    ShadowMonsterDifficultyProfileIds.DontStarve
                ),
            }
        );
        resolver.Refresh();
        var dontStarve = provider.Resolve(
            ShadowMonsterAssetBindingIds.CreeperFear
        );

        Assert.True(fusion.Success, fusion.Reason);
        Assert.Equal(
            ShadowMonsterDifficultyProfileIds.Fusion,
            fusion.Profile!.DifficultyProfileId
        );
        Assert.True(saved.Success, saved.Reason);
        Assert.True(dontStarve.Success, dontStarve.Reason);
        Assert.Equal(
            ShadowMonsterDifficultyProfileIds.DontStarve,
            dontStarve.Profile!.DifficultyProfileId
        );
        Assert.Equal(1, access.ReadCount);
    }

    [Fact]
    public void Unknown_version_capability_fails_closed_without_fallback_profile()
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.7.0",
                "unknown",
                false,
                null
            )
        );
        var provider = new HostileShadowRuntimeProfileProvider(
            catalog,
            capability,
            config: null,
            tileSize: 64,
            unavailableReason: "test-unavailable"
        );

        var result = provider.Resolve(ShadowMonsterAssetBindingIds.CreeperFear);

        Assert.False(result.Success);
        Assert.Null(result.Profile);
        Assert.Equal(
            ShadowMonsterProfileContractIds.Stardew17UnavailableReason,
            result.Reason
        );
    }
}
