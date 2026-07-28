#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Player.Stats.Sanity.Visual;

internal readonly record struct SanityIdleFrameStep(
    int DurationMilliseconds,
    int PositionOffset
);

/// <summary>
/// Frozen non-passout sequence contract. The runtime supplies the already-proven basic-idle frame
/// and arm/flip metadata, then loops these presentation-only offsets under a private negative
/// token. No frame callback can reach fatigue, sleep, health, save, or CanMove state.
/// </summary>
internal static class SanityIdlePresentationContract
{
    internal const int AnimationToken = -410315;
    internal const int VanillaPassOutAnimationToken = 293;
    internal const int VanillaPassOutFinalFrame = 5;
    internal const int MaximumOwners = SanityVisualController.MaximumOwners;

    private static readonly IReadOnlyList<SanityIdleFrameStep> FrozenSteps =
        new ReadOnlyCollection<SanityIdleFrameStep>(
            new[]
            {
                new SanityIdleFrameStep(260, 0),
                new SanityIdleFrameStep(260, -1),
                new SanityIdleFrameStep(260, 0),
                new SanityIdleFrameStep(260, 0),
            }
        );

    internal static IReadOnlyList<SanityIdleFrameStep> Steps => FrozenSteps;

    internal static bool IsSafeBasicFrame(int frame)
    {
        return frame >= 0 && frame != VanillaPassOutFinalFrame;
    }

    internal static bool IsOwnedToken(int token)
    {
        return token == AnimationToken;
    }
}
