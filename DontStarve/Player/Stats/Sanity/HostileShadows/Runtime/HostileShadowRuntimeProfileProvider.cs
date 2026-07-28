#nullable enable

using System;
using DontStarve.Config;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal readonly record struct HostileShadowRuntimeProfileResolution(
    ShadowMonsterRuntimeProfile? Profile,
    string Reason
)
{
    internal bool Success => Profile is not null;
}

/// <summary>
/// Resolves the current typed difficulty selection against the one validated 07-02 catalog and
/// adapter. It never reads JSON and deliberately does not build a second profile registry.
/// </summary>
internal sealed class HostileShadowRuntimeProfileProvider
{
    private readonly ShadowMonsterProfileCatalog? catalog;
    private readonly ShadowMonsterProfileAdapterCapability capability;
    private readonly TypedConfigResolver? config;
    private readonly int tileSize;
    private readonly string unavailableReason;

    internal HostileShadowRuntimeProfileProvider(
        ShadowMonsterProfileCatalog? catalog,
        ShadowMonsterProfileAdapterCapability capability,
        TypedConfigResolver? config,
        int tileSize,
        string unavailableReason
    )
    {
        this.catalog = catalog;
        this.capability = capability;
        this.config = config;
        this.tileSize = tileSize;
        this.unavailableReason = string.IsNullOrWhiteSpace(unavailableReason)
            ? "hostile-shadow.profile-unavailable"
            : unavailableReason;
    }

    internal HostileShadowRuntimeProfileResolution Resolve(
        string assetBindingId
    )
    {
        if (catalog is null)
            return Failure(unavailableReason);
        if (!capability.IsAvailable || capability.Adapter is null)
            return Failure(capability.Reason);
        if (config is null)
            return Failure("hostile-shadow.profile-config-unavailable");

        var selected = config.GetEnum(ConfigKeys.MonsterDifficultyProfile);
        if (!selected.HasValue)
        {
            return Failure(
                string.Concat(
                    "hostile-shadow.difficulty-profile-unavailable:",
                    selected.Reason
                )
            );
        }
        if (!catalog.TryGetProfile(selected.Value, out var difficulty) || difficulty is null)
            return Failure("hostile-shadow.difficulty-profile-unknown");

        var adapted = capability.Adapter.Adapt(
            difficulty,
            assetBindingId,
            tileSize
        );
        return adapted.Success
            ? new HostileShadowRuntimeProfileResolution(
                adapted.Profile,
                "hostile-shadow.profile-resolved"
            )
            : Failure(adapted.Reason);
    }

    private static HostileShadowRuntimeProfileResolution Failure(string reason)
    {
        return new HostileShadowRuntimeProfileResolution(null, reason);
    }
}
