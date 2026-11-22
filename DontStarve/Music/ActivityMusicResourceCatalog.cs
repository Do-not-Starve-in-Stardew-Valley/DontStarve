#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace DontStarve.Music;

/// <summary>
/// A fixed activity-track registration. The activity state chooses the logical key; this catalog
/// is the only layer allowed to turn that key into a physical file path.
///
/// The logical <see cref="TrackKey"/> and the physical <see cref="RelativePath"/> are independent by
/// design. The shipped OGG files are named after their source material (an origin prefix such as
/// <c>ds_</c>/<c>sw_</c> plus the work/fight/EFS role), so a file name is never derived from the key.
/// Every registration states its own file name, and several logical keys may deliberately share one
/// physical file when the source material is a single season-independent theme.
/// </summary>
internal sealed class ActivityMusicResourceEntry
{
    internal ActivityMusicResourceEntry(
        string trackKey,
        string locationFamily,
        ActivityMusicKind kind,
        string seasonToken,
        string fileName,
        bool required = true
    )
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("/", StringComparison.Ordinal)
            || fileName.Contains("\\", StringComparison.Ordinal)
            || fileName.Contains("..", StringComparison.Ordinal)
            || !fileName.EndsWith(".ogg", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"An activity music registration must name a bare .ogg file, but was: {fileName}",
                nameof(fileName)
            );
        }

        TrackKey = trackKey;
        LocationFamily = locationFamily;
        Kind = kind;
        SeasonToken = seasonToken;
        Required = required;
        RelativePath = $"{ActivityMusicResourceCatalog.ActivityRoot}/{fileName}";
    }

    internal string TrackKey { get; }

    internal string RelativePath { get; }

    internal string LocationFamily { get; }

    internal ActivityMusicKind Kind { get; }

    internal string SeasonToken { get; }

    internal bool Required { get; }

    internal bool IsLooped => true;
}

/// <summary>
/// Stage 06's 28 required physical slots and the four explicit greenhouse aliases.
///
/// There are deliberately no Boss or VolcanoDungeon entries here. A missing optional volcano
/// track must remain missing; registering a guessed asset would turn a location into a fake
/// eruption state or a cross-family fallback.
/// </summary>
internal static class ActivityMusicResourceCatalog
{
    internal const string ActivityRoot = "Asset/Music/Activity";

    // The physical names below are the shipped, already-renamed source files, not a TrackKey echo.
    // `_work` is the working theme, `_fight` the danger theme, and `_EFS` the epicfight / Boss
    // reserved theme; those roles are never mixed here.
    private static readonly IReadOnlyList<ActivityMusicResourceEntry> physicalEntries =
        new[]
        {
            // Ordinary surface/Farm: 4 work, 4 combat, 4 farming.
            Entry("work.surface.spring", "surface", ActivityMusicKind.Work, "spring", "ds_spring_work.ogg"),
            Entry("work.surface.summer", "surface", ActivityMusicKind.Work, "summer", "ds_summer_work.ogg"),
            Entry("work.surface.fall", "surface", ActivityMusicKind.Work, "fall", "ds_fall_work.ogg"),
            Entry("work.surface.winter", "surface", ActivityMusicKind.Work, "winter", "ds_winter_work.ogg"),
            Entry("combat.surface.spring", "surface", ActivityMusicKind.Combat, "spring", "ds_spring_fight.ogg"),
            Entry("combat.surface.summer", "surface", ActivityMusicKind.Combat, "summer", "ds_summer_fight.ogg"),
            Entry("combat.surface.fall", "surface", ActivityMusicKind.Combat, "fall", "ds_fall_fight.ogg"),
            Entry("combat.surface.winter", "surface", ActivityMusicKind.Combat, "winter", "ds_winter_fight.ogg"),
            // Farming is one season-independent DST busy theme ("farming/music/farming"), so all
            // four farming seasons intentionally resolve to the same physical file.
            Entry("farming.farm.spring", "farm", ActivityMusicKind.Farming, "spring", "ds_farming_work.ogg"),
            Entry("farming.farm.summer", "farm", ActivityMusicKind.Farming, "summer", "ds_farming_work.ogg"),
            Entry("farming.farm.fall", "farm", ActivityMusicKind.Farming, "fall", "ds_farming_work.ogg"),
            Entry("farming.farm.winter", "farm", ActivityMusicKind.Farming, "winter", "ds_farming_work.ogg"),

            // Season-independent mine and ruins families.
            Entry("work.cave", "cave", ActivityMusicKind.Work, "none", "ds_cave_work.ogg"),
            Entry("combat.cave", "cave", ActivityMusicKind.Combat, "none", "ds_cave_fight.ogg"),
            Entry("work.ruins", "ruins", ActivityMusicKind.Work, "none", "ds_ruins_work.ogg"),
            Entry("combat.ruins", "ruins", ActivityMusicKind.Combat, "none", "ds_ruins_fight.ogg"),

            // Ginger Island: the season token is the stable value from stage 02, not a display
            // name or a duplicated spring/summer/fall/winter mapping. The Shipwrecked prefixes
            // follow the frozen season slots (mild=1, rain/wet=2, hurricane/green=3, volcanic/dry=4)
            // and keep the on-disk names the user already chose.
            Entry("work.island.ginger_island_rain", "island", ActivityMusicKind.Work, "ginger_island_rain", "sw_monsoon_work.ogg"),
            Entry("work.island.ginger_island_volcanic", "island", ActivityMusicKind.Work, "ginger_island_volcanic", "sw_dry_work.ogg"),
            Entry("work.island.ginger_island_mild", "island", ActivityMusicKind.Work, "ginger_island_mild", "sw_mild_work.ogg"),
            Entry("work.island.ginger_island_hurricane", "island", ActivityMusicKind.Work, "ginger_island_hurricane", "sw_hurricane_work.ogg"),
            Entry("combat.island.ginger_island_rain", "island", ActivityMusicKind.Combat, "ginger_island_rain", "sw_monsoon_fight.ogg"),
            Entry("combat.island.ginger_island_volcanic", "island", ActivityMusicKind.Combat, "ginger_island_volcanic", "sw_dry_fight.ogg"),
            Entry("combat.island.ginger_island_mild", "island", ActivityMusicKind.Combat, "ginger_island_mild", "sw_mild_fight.ogg"),
            Entry("combat.island.ginger_island_hurricane", "island", ActivityMusicKind.Combat, "ginger_island_hurricane", "sw_hurricane_fight.ogg"),
            // 姜岛没有专用耕种曲（用户 2026-11-17 裁决）：姜岛农场（IslandWest）的耕种行为
            // 与普通农场共用同一首季节无关的 DST busy 主题，四个姜岛季节键只是选键别名。
            Entry("farming.farm.ginger_island_rain", "farm", ActivityMusicKind.Farming, "ginger_island_rain", "ds_farming_work.ogg"),
            Entry("farming.farm.ginger_island_volcanic", "farm", ActivityMusicKind.Farming, "ginger_island_volcanic", "ds_farming_work.ogg"),
            Entry("farming.farm.ginger_island_mild", "farm", ActivityMusicKind.Farming, "ginger_island_mild", "ds_farming_work.ogg"),
            Entry("farming.farm.ginger_island_hurricane", "farm", ActivityMusicKind.Farming, "ginger_island_hurricane", "ds_farming_work.ogg"),
        };

    private static readonly IReadOnlyDictionary<string, string> greenhouseAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["farming.greenhouse.spring"] = "farming.farm.spring",
            ["farming.greenhouse.summer"] = "farming.farm.summer",
            ["farming.greenhouse.fall"] = "farming.farm.fall",
            ["farming.greenhouse.winter"] = "farming.farm.winter",
        };

    private static readonly IReadOnlyDictionary<string, ActivityMusicResourceEntry> physicalByKey =
        CreatePhysicalIndex();

    internal static IReadOnlyList<ActivityMusicResourceEntry> PhysicalEntries => physicalEntries;

    internal static IReadOnlyDictionary<string, string> GreenhouseAliases => greenhouseAliases;

    internal static bool TryResolve(
        string? requestedTrackKey,
        out ActivityMusicResourceEntry? entry,
        out bool wasAlias
    )
    {
        entry = null;
        wasAlias = false;
        if (string.IsNullOrWhiteSpace(requestedTrackKey))
            return false;

        var physicalKey = requestedTrackKey;
        if (greenhouseAliases.TryGetValue(requestedTrackKey, out var aliasTarget))
        {
            physicalKey = aliasTarget;
            wasAlias = true;
        }

        return physicalByKey.TryGetValue(physicalKey, out entry);
    }

    internal static bool IsSafeRelativePath(string? relativePath)
    {
        return !string.IsNullOrWhiteSpace(relativePath)
            && relativePath.StartsWith(ActivityRoot + "/", StringComparison.Ordinal)
            && relativePath.EndsWith(".ogg", StringComparison.Ordinal)
            && !relativePath.Contains("..", StringComparison.Ordinal)
            && !relativePath.Contains("\\", StringComparison.Ordinal)
            && !relativePath.StartsWith("/", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private static ActivityMusicResourceEntry Entry(
        string trackKey,
        string locationFamily,
        ActivityMusicKind kind,
        string seasonToken,
        string fileName
    )
    {
        return new ActivityMusicResourceEntry(trackKey, locationFamily, kind, seasonToken, fileName);
    }

    private static IReadOnlyDictionary<string, ActivityMusicResourceEntry> CreatePhysicalIndex()
    {
        var result = new Dictionary<string, ActivityMusicResourceEntry>(StringComparer.Ordinal);
        foreach (var entry in physicalEntries)
            result.Add(entry.TrackKey, entry);
        return result;
    }
}
