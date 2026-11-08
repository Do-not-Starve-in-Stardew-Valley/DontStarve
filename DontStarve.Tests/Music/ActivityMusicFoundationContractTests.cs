using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicFoundationContractTests
{
    [Fact]
    public void GingerIslandMappingIsIndependentAndUsesTheFourFrozenFamilies()
    {
        var source = ReadContract("ActivityMusicContracts.cs");

        Assert.Contains("Season.Spring => GingerIslandSeason.Rainy", source, StringComparison.Ordinal);
        Assert.Contains("Season.Summer => GingerIslandSeason.Volcano", source, StringComparison.Ordinal);
        Assert.Contains("Season.Fall => GingerIslandSeason.Mild", source, StringComparison.Ordinal);
        Assert.Contains("Season.Winter => GingerIslandSeason.Hurricane", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsErupting", source, StringComparison.Ordinal);
        Assert.DoesNotContain("active/dormant/erupt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackKeysKeepGreenhouseAsAnExplicitFarmAliasAndDoNotCrossFallback()
    {
        var source = ReadContract("ActivityMusicContracts.cs");

        Assert.Contains(
            "ActivityMusicLocationFamily.Greenhouse => kind == ActivityMusicKind.Farming",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ActivityMusicLocationFamily.IslandVolcano => location.GingerSeason.HasValue",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("return null;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("island-volcano.*island", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackKeysKeepSurfaceFarmCaveRuinsAndIslandFarmFamiliesDistinct()
    {
        var source = ReadContract("ActivityMusicContracts.cs");

        Assert.Contains("ActivityMusicLocationFamily.Default => \"surface\"", source, StringComparison.Ordinal);
        Assert.Contains("kind == ActivityMusicKind.Farming ? \"farm\" : \"surface\"", source, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicLocationFamily.Mine => \"cave\"", source, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicLocationFamily.QuarryMine => \"cave\"", source, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicLocationFamily.SkullCavern => \"ruins\"", source, StringComparison.Ordinal);
        Assert.Contains("? \"island-farm\" : \"island\"", source, StringComparison.Ordinal);
        Assert.Contains("return $\"{stateToken}.{regionToken}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VolcanoTracksRequireAValidIslandContext()
    {
        var source = ReadContract("ActivityMusicContracts.cs");

        Assert.Contains(
            "string.Equals(contextId, \"Island\", StringComparison.Ordinal)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ActivityMusicLocationFamily.IslandVolcano => location.GingerSeason.HasValue",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void StateStoreIsOwnerKeyedAndLifecycleServiceClearsBoundaries()
    {
        var store = ReadContract("ActivityMusicStateStore.cs");
        var service = ReadContract("ActivityMusicService.cs");

        Assert.Contains("Dictionary<long, ActivityMusicState>", store, StringComparison.Ordinal);
        Assert.Contains("UniqueMultiplayerID", service, StringComparison.Ordinal);
        Assert.Contains("StateStore.Clear();", service, StringComparison.Ordinal);
        Assert.DoesNotContain("StateStore.Remove(e.Player.UniqueMultiplayerID)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivityMusicAudioService.StopOwner(e.Player.UniqueMultiplayerID)", service, StringComparison.Ordinal);
        Assert.Contains("ActivityMusicActionBridge.ForgetFishingOwner(e.Player.UniqueMultiplayerID)", service, StringComparison.Ordinal);
        Assert.Contains("HasValidLocalOwner", service, StringComparison.Ordinal);
        Assert.Contains("UniqueMultiplayerID != 0", service, StringComparison.Ordinal);
        Assert.Contains("owner.UniqueMultiplayerID == 0", service, StringComparison.Ordinal);
    }

    [Fact]
    public void FarmingGateRequiresFarmIdentityOrExplicitGreenhouse()
    {
        var service = ReadContract("ActivityMusicService.cs");

        Assert.Contains("return location is Farm || location?.IsGreenhouse == true", service, StringComparison.Ordinal);
        Assert.DoesNotContain("location is IslandWest", service, StringComparison.Ordinal);
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ActivityMusic", fileName)
        );
    }
}
