#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Pure presentation policy for Stardew's generated MineShaft wall sconces. It deliberately
/// decides only whether the native local light is drawn; it never changes the map tile, shared
/// light collection, or the mine's base-light plan.
/// </summary>
internal static class MineWallSconcePolicy
{
    // Standard SOS occupies 27 Morse units: S (5), letter gap (3), O (11), letter gap (3), S (5).
    internal const double SosSignalDurationSeconds = 3d;
    internal const double SosCooldownDurationSeconds = 3d;
    internal const double SosUnitDurationSeconds = SosSignalDurationSeconds / 27d;

    internal static bool ShouldDraw(MineWallSconceInput input)
    {
        return input.Phase switch
        {
            NaturalDarknessPhase.None => true,
            NaturalDarknessPhase.MineDusk => IsSosLightOn(
                input.PhaseElapsedRealSeconds
            ),
            NaturalDarknessPhase.MineNight => false,
            NaturalDarknessPhase.FullDark => ShouldDrawInFullDarkMine(input),
            _ => true,
        };
    }

    private static bool ShouldDrawInFullDarkMine(MineWallSconceInput input)
    {
        // Dangerous Skull Cavern has no timed reprieve. The dusk boundary also immediately
        // cancels a currently-lit timed sconce, rather than allowing its old interval to finish.
        if (
            input.IsDangerousSkullCavern
            || IsAtOrAfter(input.TimeOfDay, input.StartingToGetDarkTime)
        )
        {
            return false;
        }

        if (!input.IsSkullCavern && input.MineLevel is >= 31 and <= 39)
        {
            return IsWithinHourlyWindow(input.TimeOfDay, durationMinutes: 10);
        }

        if (input.IsSkullCavern && input.MineLevel >= 1000)
        {
            return IsWithinEvenHourlyWindow(input.TimeOfDay, durationMinutes: 30);
        }

        // UndergroundMine77377 and any future FullDark mine not named above stay dark.
        return false;
    }

    private static bool IsSosLightOn(double phaseElapsedRealSeconds)
    {
        if (
            !double.IsFinite(phaseElapsedRealSeconds)
            || phaseElapsedRealSeconds < 0d
        )
        {
            return false;
        }

        var cycleDuration = SosSignalDurationSeconds + SosCooldownDurationSeconds;
        var cycleSeconds = phaseElapsedRealSeconds % cycleDuration;
        if (cycleSeconds >= SosSignalDurationSeconds)
            return false;

        // Multiplying by nine converts the three-second signal into its 27 Morse units without
        // allocating a per-light timer or pulse sequence.
        var unit = cycleSeconds * 9d;
        return unit is >= 0d and < 1d
            or >= 2d and < 3d
            or >= 4d and < 5d
            or >= 8d and < 11d
            or >= 12d and < 15d
            or >= 16d and < 19d
            or >= 22d and < 23d
            or >= 24d and < 25d
            or >= 26d and < 27d;
    }

    private static bool IsWithinHourlyWindow(int timeOfDay, int durationMinutes)
    {
        return TryGetHourAndMinute(timeOfDay, out _, out var minute)
            && minute < durationMinutes;
    }

    private static bool IsWithinEvenHourlyWindow(
        int timeOfDay,
        int durationMinutes
    )
    {
        return TryGetHourAndMinute(timeOfDay, out var hour, out var minute)
            && hour % 2 == 0
            && minute < durationMinutes;
    }

    private static bool TryGetHourAndMinute(
        int timeOfDay,
        out int hour,
        out int minute
    )
    {
        hour = timeOfDay / 100;
        minute = timeOfDay % 100;
        return timeOfDay >= 0 && minute is >= 0 and < 60;
    }

    private static bool IsAtOrAfter(int timeOfDay, int boundary)
    {
        return boundary >= 0 && timeOfDay >= boundary;
    }
}

/// <summary>
/// Facts supplied by the draw bridge for one generated mine wall sconce.
/// </summary>
internal readonly record struct MineWallSconceInput(
    NaturalDarknessPhase Phase,
    double PhaseElapsedRealSeconds,
    int TimeOfDay,
    int StartingToGetDarkTime,
    int MineLevel,
    bool IsSkullCavern,
    bool IsDangerousSkullCavern
);
