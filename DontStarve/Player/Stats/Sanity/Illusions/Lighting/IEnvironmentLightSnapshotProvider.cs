#nullable enable

using System;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

internal interface IEnvironmentLightSnapshotProvider
{
    EnvironmentLightSnapshotCaptureResult Capture(
        Farmer owner,
        int screenId,
        GameLocation location,
        long gameMinute
    );
}

internal interface IEnvironmentLightSnapshotInvalidationSource
{
    event Action<string, int>? SnapshotInvalidated;
}

internal interface IEnvironmentNightVisionProvider
{
    EnvironmentLightNightVisionSnapshot Probe(
        Farmer owner,
        int screenId,
        GameLocation location
    );
}
