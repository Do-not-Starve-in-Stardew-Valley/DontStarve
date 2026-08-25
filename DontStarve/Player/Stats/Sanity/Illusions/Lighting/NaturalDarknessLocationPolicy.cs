#nullable enable

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// The current stage of a location's natural-light override. A plan describes only the native
/// base light; individual torches and other LightSource instances remain Stardew-owned.
/// </summary>
internal enum NaturalDarknessPhase
{
    None,
    NightThreeSeconds,
    FullDark,
    MineDusk,
    MineNight,
}

internal readonly record struct NaturalDarknessScenePlan(
    NaturalDarknessPhase Phase,
    double TargetWhiteBlend,
    double TransitionDurationSeconds
)
{
    internal static NaturalDarknessScenePlan None => new(
        NaturalDarknessPhase.None,
        0d,
        0d
    );

    internal bool IsActive => Phase != NaturalDarknessPhase.None;
}

/// <summary>
/// Read-only current scene state consumed by draw-only native-light presentation patches.
/// </summary>
internal readonly record struct NaturalDarknessSceneState(
    NaturalDarknessPhase Phase,
    double PhaseElapsedRealSeconds,
    float LightmapWhiteBlend = 0f
)
{
    internal static NaturalDarknessSceneState None => new(
        NaturalDarknessPhase.None,
        0d,
        0f
    );

    internal bool IsActive => Phase != NaturalDarknessPhase.None;
}

/// <summary>
/// Adapter-neutral facts needed to choose one native-light plan. The SMAPI service supplies these
/// from Stardew's current location, while tests exercise the rule without game assemblies.
/// </summary>
internal readonly record struct NaturalDarknessLocationInput(
    bool HasMatchedLocationRule,
    NaturalDarknessProfile Profile,
    bool IsOutdoors,
    bool IsJunimoBlessingProtected,
    bool IsMineShaft,
    int MineLevel,
    bool IsSkullCavern,
    bool IsDangerousSkullCavern,
    int TimeOfDay,
    int StartingToGetDarkTime,
    int TrulyDarkTime
);

/// <summary>
/// Chooses the requested location-specific natural-light behavior. Unmatched outdoor locations
/// retain the pre-existing true-dark behavior, so this catalog is not a visual whitelist.
/// </summary>
internal static class NaturalDarknessLocationPolicy
{
    internal static NaturalDarknessScenePlan Resolve(
        NaturalDarknessLocationInput input
    )
    {
        if (input.IsJunimoBlessingProtected)
            return NaturalDarknessScenePlan.None;

        var profile = input.HasMatchedLocationRule
            ? input.Profile
            : input.IsOutdoors
                ? NaturalDarknessProfile.NightThreeSeconds
                : NaturalDarknessProfile.Unchanged;

        return profile switch
        {
            NaturalDarknessProfile.FullDark => FullDarkPlan(),
            NaturalDarknessProfile.NightThreeSeconds => NightPlan(input),
            NaturalDarknessProfile.MineTwoStage => MinePlan(input),
            _ => NaturalDarknessScenePlan.None,
        };
    }

    private static NaturalDarknessScenePlan NightPlan(
        NaturalDarknessLocationInput input
    )
    {
        return IsAtOrAfter(input.TimeOfDay, input.TrulyDarkTime)
            ? new NaturalDarknessScenePlan(
                NaturalDarknessPhase.NightThreeSeconds,
                1d,
                NaturalDarknessTransitionPolicy.NightTransitionDurationSeconds
            )
            : NaturalDarknessScenePlan.None;
    }

    private static NaturalDarknessScenePlan MinePlan(
        NaturalDarknessLocationInput input
    )
    {
        if (!input.IsMineShaft)
            return NaturalDarknessScenePlan.None;

        if (
            input.MineLevel == 77377
            || (input.IsSkullCavern && input.IsDangerousSkullCavern)
            || (input.IsSkullCavern && input.MineLevel >= 1000)
            || (!input.IsSkullCavern && input.MineLevel is >= 31 and <= 39)
        )
        {
            return FullDarkPlan();
        }

        if (IsAtOrAfter(input.TimeOfDay, input.TrulyDarkTime))
        {
            return new NaturalDarknessScenePlan(
                NaturalDarknessPhase.MineNight,
                1d,
                NaturalDarknessTransitionPolicy.MineNightTransitionDurationSeconds
            );
        }

        if (IsAtOrAfter(input.TimeOfDay, input.StartingToGetDarkTime))
        {
            return new NaturalDarknessScenePlan(
                NaturalDarknessPhase.MineDusk,
                NaturalDarknessTransitionPolicy.MineDuskWhiteBlend,
                NaturalDarknessTransitionPolicy.MineDuskTransitionDurationSeconds
            );
        }

        return NaturalDarknessScenePlan.None;
    }

    private static NaturalDarknessScenePlan FullDarkPlan()
    {
        return new NaturalDarknessScenePlan(
            NaturalDarknessPhase.FullDark,
            1d,
            0d
        );
    }

    private static bool IsAtOrAfter(int timeOfDay, int boundary)
    {
        return boundary >= 0 && timeOfDay >= boundary;
    }
}
