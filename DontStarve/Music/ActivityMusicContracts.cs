#nullable enable

using System;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Music;

/// <summary>
/// The logical activity lanes. The ordering is deliberately kept in one place so that later
/// action bridges cannot invent a second priority table.
/// </summary>
internal enum ActivityMusicLocationFamily
{
    Default = 0,
    Farm = 1,
    Greenhouse = 2,
    Mine = 3,
    QuarryMine = 4,
    SkullCavern = 5,
    Island = 6,
    IslandVolcano = 7
}

/// <summary>
/// Ginger Island seasons are a mod-level foundation, not a Stardew gameplay season. They are
/// intentionally named after the seasonal families used by the Shipwrecked music reference.
/// </summary>
internal enum GingerIslandSeason
{
    Rainy = 0,
    Volcano = 1,
    Mild = 2,
    Hurricane = 3
}

internal sealed class ActivityMusicLocationContext
{
    internal ActivityMusicLocationContext(
        ActivityMusicLocationFamily family,
        Season valleySeason,
        GingerIslandSeason? gingerSeason,
        string locationContextId
    )
    {
        Family = family;
        ValleySeason = valleySeason;
        GingerSeason = gingerSeason;
        LocationContextId = locationContextId;
    }

    internal ActivityMusicLocationFamily Family { get; }

    internal Season ValleySeason { get; }

    internal GingerIslandSeason? GingerSeason { get; }

    internal string LocationContextId { get; }

    internal bool IsIsland => GingerSeason.HasValue;
}

internal sealed class ActivityMusicState
{
    internal ActivityMusicState(
        long ownerId,
        ActivityMusicKind kind,
        ActivityMusicLocationContext location,
        string trackKey,
        long generation,
        int remainingSeconds
    )
    {
        OwnerId = ownerId;
        Kind = kind;
        Location = location;
        TrackKey = trackKey;
        Generation = generation;
        RemainingSeconds = remainingSeconds;
    }

    internal long OwnerId { get; }

    internal ActivityMusicKind Kind { get; }

    internal ActivityMusicLocationContext Location { get; }

    internal string TrackKey { get; }

    internal long Generation { get; }

    internal int RemainingSeconds { get; private set; }

    internal void Refresh(int remainingSeconds)
    {
        RemainingSeconds = Math.Max(RemainingSeconds, remainingSeconds);
    }

    internal bool Advance(int seconds)
    {
        RemainingSeconds = Math.Max(0, RemainingSeconds - Math.Max(0, seconds));
        return RemainingSeconds == 0;
    }
}

internal static class GingerIslandSeasonPolicy
{
    internal static GingerIslandSeason FromValleySeason(Season season)
    {
        return season switch
        {
            Season.Spring => GingerIslandSeason.Rainy,
            Season.Summer => GingerIslandSeason.Volcano,
            Season.Fall => GingerIslandSeason.Mild,
            Season.Winter => GingerIslandSeason.Hurricane,
            _ => GingerIslandSeason.Rainy
        };
    }

    internal static string ToToken(GingerIslandSeason season)
    {
        return season switch
        {
            GingerIslandSeason.Rainy => "ginger_island_rain",
            GingerIslandSeason.Volcano => "ginger_island_volcanic",
            GingerIslandSeason.Mild => "ginger_island_mild",
            GingerIslandSeason.Hurricane => "ginger_island_hurricane",
            _ => ""
        };
    }
}

internal static class ActivityMusicLocationResolver
{
    internal static ActivityMusicLocationContext Resolve(
        GameLocation location,
        Season valleySeason
    )
    {
        var contextId = GetContextId(location);

        // VolcanoDungeon inherits IslandLocation, so it must be checked before the broad island
        // context. This is a location family distinction, not a volcanic eruption state.
        if (location is VolcanoDungeon)
        {
            return new ActivityMusicLocationContext(
                ActivityMusicLocationFamily.IslandVolcano,
                valleySeason,
                string.Equals(contextId, "Island", StringComparison.Ordinal)
                    ? GingerIslandSeasonPolicy.FromValleySeason(valleySeason)
                    : null,
                contextId
            );
        }

        if (location is MineShaft mine)
        {
            var family = mine.mineLevel == MineShaft.quarryMineShaft
                ? ActivityMusicLocationFamily.QuarryMine
                : mine.mineLevel >= MineShaft.desertArea
                    ? ActivityMusicLocationFamily.SkullCavern
                    : ActivityMusicLocationFamily.Mine;

            return new ActivityMusicLocationContext(
                family,
                valleySeason,
                null,
                contextId
            );
        }

        if (location is Farm)
        {
            return new ActivityMusicLocationContext(
                ActivityMusicLocationFamily.Farm,
                valleySeason,
                string.Equals(contextId, "Island", StringComparison.Ordinal)
                    ? GingerIslandSeasonPolicy.FromValleySeason(valleySeason)
                    : null,
                contextId
            );
        }

        if (location.IsGreenhouse
            || string.Equals(location?.Name, "Greenhouse", StringComparison.Ordinal))
        {
            return new ActivityMusicLocationContext(
                ActivityMusicLocationFamily.Greenhouse,
                valleySeason,
                null,
                contextId
            );
        }

        if (string.Equals(contextId, "Island", StringComparison.Ordinal))
        {
            return new ActivityMusicLocationContext(
                ActivityMusicLocationFamily.Island,
                valleySeason,
                GingerIslandSeasonPolicy.FromValleySeason(valleySeason),
                contextId
            );
        }

        return new ActivityMusicLocationContext(
            ActivityMusicLocationFamily.Default,
            valleySeason,
            null,
            contextId
        );
    }

    private static string GetContextId(GameLocation location)
    {
        if (location is null)
            return "Default";

        try
        {
            return location.GetLocationContextId() ?? "Default";
        }
        catch
        {
            // Classification must fail closed. A malformed third-party map must not become an
            // accidental Island location just because its name contains "Island".
            return "Unknown";
        }
    }
}

internal static class ActivityMusicTrackKey
{
    internal static string? TryCreate(
        ActivityMusicKind kind,
        ActivityMusicLocationContext location
    )
    {
        if (kind == ActivityMusicKind.None || location is null)
            return null;

        var stateToken = kind switch
        {
            ActivityMusicKind.Work => "work",
            ActivityMusicKind.Farming => "farming",
            ActivityMusicKind.Combat => "combat",
            ActivityMusicKind.Boss => "boss",
            _ => null
        };

        if (stateToken is null)
            return null;

        var regionToken = GetRegionToken(kind, location);
        if (regionToken is null)
            return null;

        // Cave/ruins resources are season-independent. This is also why their location family
        // mapping must happen before the seasonal token is appended.
        if (location.Family is ActivityMusicLocationFamily.Mine
            or ActivityMusicLocationFamily.QuarryMine
            or ActivityMusicLocationFamily.SkullCavern)
        {
            return $"{stateToken}.{regionToken}";
        }

        var seasonToken = location.GingerSeason.HasValue
            ? GingerIslandSeasonPolicy.ToToken(location.GingerSeason.Value)
            : location.ValleySeason switch
            {
                Season.Spring => "spring",
                Season.Summer => "summer",
                Season.Fall => "fall",
                Season.Winter => "winter",
                _ => null
            };

        return seasonToken is null
            ? null
            : $"{stateToken}.{regionToken}.{seasonToken}";
    }

    private static string? GetRegionToken(
        ActivityMusicKind kind,
        ActivityMusicLocationContext location
    )
    {
        return location.Family switch
        {
            // Ordinary surface and Farm use different resources only for Farming. Work and
            // Combat on a Farm remain part of the surface family.
            ActivityMusicLocationFamily.Default => "surface",
            ActivityMusicLocationFamily.Farm => location.GingerSeason.HasValue
                ? kind == ActivityMusicKind.Farming ? "island-farm" : "island"
                : kind == ActivityMusicKind.Farming ? "farm" : "surface",
            // Greenhouse farming is an explicit same-season Farm alias; other activity lanes use
            // the ordinary surface family and never infer a greenhouse-specific work track.
            ActivityMusicLocationFamily.Greenhouse => kind == ActivityMusicKind.Farming
                ? "greenhouse"
                : "surface",
            ActivityMusicLocationFamily.Mine => "cave",
            ActivityMusicLocationFamily.QuarryMine => "cave",
            ActivityMusicLocationFamily.SkullCavern => "ruins",
            ActivityMusicLocationFamily.Island => location.GingerSeason.HasValue
                ? "island"
                : null,
            ActivityMusicLocationFamily.IslandVolcano => location.GingerSeason.HasValue
                ? "island-volcano"
                : null,
            _ => null
        };
    }
}
