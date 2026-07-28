#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Assigns an ephemeral identity without retaining a strong reference to a departed location.
/// Name alone cannot distinguish two location instances across a warp or reload.
/// </summary>
internal static class EnvironmentLightLocationIdentity
{
    private static readonly ConditionalWeakTable<GameLocation, IdentityBox> Ids =
        new();
    private static long nextId;

    internal static long Get(GameLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return Ids.GetValue(
            location,
            _ => new IdentityBox(Interlocked.Increment(ref nextId))
        ).Value;
    }

    private sealed record IdentityBox(long Value);
}
