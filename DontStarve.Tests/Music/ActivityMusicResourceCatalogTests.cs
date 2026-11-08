using DontStarve.Music;
using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicResourceCatalogTests
{
    /// <summary>
    /// The frozen logical-key to physical-file binding. The activity state chooses the logical key;
    /// only the catalog may turn it into a shipped OGG. It is written out here so a code or asset
    /// rename cannot silently desynchronise the registry from the files on disk.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExpectedFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["work.surface.spring"] = "ds_spring_work.ogg",
            ["work.surface.summer"] = "ds_summer_work.ogg",
            ["work.surface.fall"] = "ds_fall_work.ogg",
            ["work.surface.winter"] = "ds_winter_work.ogg",
            ["combat.surface.spring"] = "ds_spring_fight.ogg",
            ["combat.surface.summer"] = "ds_summer_fight.ogg",
            ["combat.surface.fall"] = "ds_fall_fight.ogg",
            ["combat.surface.winter"] = "ds_winter_fight.ogg",
            // One season-independent DST farming busy theme is shared by all four farm seasons.
            ["farming.farm.spring"] = "ds_farming_work.ogg",
            ["farming.farm.summer"] = "ds_farming_work.ogg",
            ["farming.farm.fall"] = "ds_farming_work.ogg",
            ["farming.farm.winter"] = "ds_farming_work.ogg",
            ["work.cave"] = "ds_cave_work.ogg",
            ["combat.cave"] = "ds_cave_fight.ogg",
            ["work.ruins"] = "ds_ruins_work.ogg",
            ["combat.ruins"] = "ds_ruins_fight.ogg",
            // `ginger_island_rain` is frozen season slot 2; the on-disk `sw_monsoon_*` name is kept.
            ["work.island.ginger_island_rain"] = "sw_monsoon_work.ogg",
            ["work.island.ginger_island_volcanic"] = "sw_dry_work.ogg",
            ["work.island.ginger_island_mild"] = "sw_mild_work.ogg",
            ["work.island.ginger_island_hurricane"] = "sw_hurricane_work.ogg",
            ["combat.island.ginger_island_rain"] = "sw_monsoon_fight.ogg",
            ["combat.island.ginger_island_volcanic"] = "sw_dry_fight.ogg",
            ["combat.island.ginger_island_mild"] = "sw_mild_fight.ogg",
            ["combat.island.ginger_island_hurricane"] = "sw_hurricane_fight.ogg",
            ["farming.island-farm.ginger_island_rain"] = "sw_monsoon_work.ogg",
            ["farming.island-farm.ginger_island_volcanic"] = "sw_dry_work.ogg",
            ["farming.island-farm.ginger_island_mild"] = "sw_mild_work.ogg",
            ["farming.island-farm.ginger_island_hurricane"] = "sw_hurricane_work.ogg",
        };

    [Fact]
    public void CatalogContainsExactlyTheFrozenTwentyEightPhysicalSlots()
    {
        var entries = ActivityMusicResourceCatalog.PhysicalEntries;

        Assert.Equal(28, entries.Count);
        Assert.Equal(
            entries.Count,
            entries.Select(entry => entry.TrackKey).Distinct(StringComparer.Ordinal).Count()
        );
        Assert.All(entries, entry =>
        {
            Assert.True(entry.Required);
            Assert.True(ActivityMusicResourceCatalog.IsSafeRelativePath(entry.RelativePath));
            Assert.DoesNotContain("Asset/Sanity/Audio", entry.RelativePath, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EveryLogicalKeyResolvesToItsDeclaredShippedFile()
    {
        var entries = ActivityMusicResourceCatalog.PhysicalEntries;

        Assert.Equal(28, ExpectedFiles.Count);
        Assert.Equal(
            ExpectedFiles.Count,
            entries.Select(entry => entry.TrackKey).Distinct(StringComparer.Ordinal).Count()
        );

        foreach (var pair in ExpectedFiles)
        {
            var trackKey = pair.Key;
            var fileName = pair.Value;

            Assert.True(
                ActivityMusicResourceCatalog.TryResolve(trackKey, out var entry, out var wasAlias),
                $"The registry no longer resolves {trackKey}."
            );
            Assert.False(wasAlias);
            Assert.NotNull(entry);
            Assert.Equal(fileName, Path.GetFileName(entry!.RelativePath));
            Assert.Equal(
                $"{ActivityMusicResourceCatalog.ActivityRoot}/{fileName}",
                entry.RelativePath
            );
        }
    }

    [Fact]
    public void RegistryNeverPublishesTheBossReservedEfsSlots()
    {
        // `_EFS` is the epicfight / Boss-reserved role. There is no Boss or island-volcano
        // registration, so no shipped EFS file may be reachable through the catalog.
        Assert.DoesNotContain(
            ActivityMusicResourceCatalog.PhysicalEntries,
            entry => entry.RelativePath.Contains("_EFS", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void EveryRegisteredFileExistsInTheSourceAssetDirectory()
    {
        var assetDirectory = ResolveActivityAssetDirectory();
        var registeredNames = ActivityMusicResourceCatalog.PhysicalEntries
            .Select(entry => Path.GetFileName(entry.RelativePath))
            .ToArray();

        foreach (var entry in ActivityMusicResourceCatalog.PhysicalEntries)
        {
            var path = Path.Combine(assetDirectory, Path.GetFileName(entry.RelativePath));
            Assert.True(
                File.Exists(path),
                $"{entry.TrackKey} resolves to a file that is not shipped: {path}"
            );
        }

        // 21 distinct files cover the 28 logical keys: the 4 valley farm seasons share
        // `ds_farming_work.ogg`, and the 4 island-farm seasons reuse the 4 island working themes.
        Assert.Equal(21, registeredNames.Distinct(StringComparer.Ordinal).Count());

        // Every shipped activity file is a `ds_`/`sw_` OGG with a work/fight role. The four
        // Dawn/Dusk OGGs live one directory up and are not activity tracks.
        Assert.All(
            registeredNames,
            name => Assert.Matches("^(ds|sw)_[a-z]+_(work|fight)\\.ogg$", name)
        );
    }

    [Fact]
    public void GreenhouseAliasesAreExplicitAndRemainSameSeasonFarmAliases()
    {
        var aliases = ActivityMusicResourceCatalog.GreenhouseAliases;

        Assert.Equal(4, aliases.Count);
        Assert.Equal("farming.farm.spring", aliases["farming.greenhouse.spring"]);
        Assert.Equal("farming.farm.summer", aliases["farming.greenhouse.summer"]);
        Assert.Equal("farming.farm.fall", aliases["farming.greenhouse.fall"]);
        Assert.Equal("farming.farm.winter", aliases["farming.greenhouse.winter"]);

        foreach (var alias in aliases)
        {
            Assert.True(
                ActivityMusicResourceCatalog.TryResolve(alias.Key, out var entry, out var wasAlias)
            );
            Assert.True(wasAlias);
            Assert.Equal(
                $"{ActivityMusicResourceCatalog.ActivityRoot}/ds_farming_work.ogg",
                entry!.RelativePath
            );
        }
    }

    [Fact]
    public void MissingOrOptionalVolcanoKeysDoNotResolveToAnotherFamily()
    {
        Assert.False(
            ActivityMusicResourceCatalog.TryResolve(
                "work.surface.spring.missing",
                out _,
                out _
            )
        );
        Assert.False(
            ActivityMusicResourceCatalog.TryResolve(
                "work.island-volcano.ginger_island_rain",
                out _,
                out _
            )
        );
        Assert.DoesNotContain(
            ActivityMusicResourceCatalog.PhysicalEntries,
            entry => entry.TrackKey.Contains("island-volcano", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void GingerIslandPhysicalKeysConsumeStableGingerIslandSeasonIdentifiers()
    {
        var islandEntries = ActivityMusicResourceCatalog.PhysicalEntries
            .Where(entry => entry.LocationFamily is "island" or "island-farm")
            .ToArray();

        Assert.Equal(12, islandEntries.Length);
        Assert.All(
            islandEntries,
            entry => Assert.StartsWith("ginger_island_", entry.SeasonToken, StringComparison.Ordinal)
        );
    }

    private static string ResolveActivityAssetDirectory()
    {
        var relative = Path.Combine("DontStarve", "Asset", "Music", "Activity");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(relative);
    }
}
