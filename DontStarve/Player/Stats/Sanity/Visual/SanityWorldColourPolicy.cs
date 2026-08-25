#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Visual;

internal enum SanityWorldColourPhase
{
    Day,
    Dusk,
    Night,
}

/// <summary>
/// Chooses only the independent low-Sanity colour profile. Stardew has already applied the
/// season, weather, location, and ambient-light result to the world render target, so this policy
/// must not replace or multiply a second seasonal palette into that source image.
/// </summary>
internal static class SanityWorldColourPolicy
{
    internal const byte DayVertexCode = 0;
    internal const byte DuskVertexCode = 127;
    internal const byte NightVertexCode = byte.MaxValue;

    internal static SanityWorldColourPhase ResolvePhase(
        int timeOfDay,
        int startingToGetDarkTime,
        int trulyDarkTime
    )
    {
        var duskStart = Math.Max(0, startingToGetDarkTime);
        var nightStart = Math.Max(duskStart, trulyDarkTime);
        if (timeOfDay >= nightStart)
            return SanityWorldColourPhase.Night;
        if (timeOfDay >= duskStart)
            return SanityWorldColourPhase.Dusk;
        return SanityWorldColourPhase.Day;
    }

    internal static byte ToVertexCode(SanityWorldColourPhase phase)
    {
        return phase switch
        {
            SanityWorldColourPhase.Day => DayVertexCode,
            SanityWorldColourPhase.Dusk => DuskVertexCode,
            _ => NightVertexCode,
        };
    }
}
