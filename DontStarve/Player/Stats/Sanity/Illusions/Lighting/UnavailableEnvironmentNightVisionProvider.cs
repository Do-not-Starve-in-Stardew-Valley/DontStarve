#nullable enable

using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Stardew 1.6.15 and SMAPI 4.3.2 expose no named night-vision capability. Buff 26 is a darkness
/// penalty, so this default provider must not infer night vision from buffs, equipment, or mods.
/// </summary>
internal sealed class UnavailableEnvironmentNightVisionProvider
    : IEnvironmentNightVisionProvider
{
    public EnvironmentLightNightVisionSnapshot Probe(
        Farmer owner,
        int screenId,
        GameLocation location
    )
    {
        return new EnvironmentLightNightVisionSnapshot(
            EnvironmentLightCapabilityStatus.Unavailable,
            null,
            EnvironmentLightReasonIds.NightVisionUnavailable
        );
    }
}
