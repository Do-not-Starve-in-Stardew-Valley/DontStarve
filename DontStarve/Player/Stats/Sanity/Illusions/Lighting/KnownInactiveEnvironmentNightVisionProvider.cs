#nullable enable

using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Vanilla Stardew 1.6.15 has no special visibility ability outside its standard lightmap. That is
/// a known inactive state, not a missing capability. Explicit compatibility providers remain free
/// to report active, inactive, unavailable, or invalid post-lightmap vision separately.
/// </summary>
internal sealed class KnownInactiveEnvironmentNightVisionProvider
    : IEnvironmentNightVisionProvider
{
    public EnvironmentLightNightVisionSnapshot Probe(
        Farmer owner,
        int screenId,
        GameLocation location
    )
    {
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            owner.UniqueMultiplayerID
        );
        return new EnvironmentLightNightVisionSnapshot(
            EnvironmentLightCapabilityStatus.Available,
            false,
            EnvironmentLightReasonIds.NightVisionKnownInactive,
            playerKey,
            screenId,
            location.NameOrUniqueName ?? string.Empty,
            EnvironmentLightLocationIdentity.Get(location)
        );
    }
}
