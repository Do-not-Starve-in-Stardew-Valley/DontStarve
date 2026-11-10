#nullable enable

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Per-call marker shared by HostileShadowAutoTargetCallerScopePatch (sets it) and
/// HostileShadowAutoTargetingPatch (reads it). Vanilla game logic is single-threaded, and the
/// caller finalizers clear the flag even when the original method throws, so the marker can never
/// outlive one marked Update call.
/// </summary>
internal static class HostileShadowAutoTargetCallScope
{
    internal static bool IgnoreAllHostileShadows;
}
