#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Runtime view of the optional Junimo blessing. The light classifier needs only this bounded
/// state, not the configuration implementation that produced it.
/// </summary>
internal readonly record struct EnvironmentLightJunimoBlessingState(
    bool IsAvailable,
    bool IsEnabled
);

/// <summary>
/// Decides whether a confirmed owner-foot darkness sample may start a darkness attack. Location
/// rules identify the small set of Junimo-protected places; they are not an attack whitelist.
/// </summary>
internal sealed class DarknessAttackLocationAuthorizationPolicy
{
    private readonly Func<EnvironmentLightJunimoBlessingState> blessingProvider;

    internal DarknessAttackLocationAuthorizationPolicy(
        Func<EnvironmentLightJunimoBlessingState> blessingProvider
    )
    {
        this.blessingProvider = blessingProvider
            ?? throw new ArgumentNullException(nameof(blessingProvider));
    }

    internal bool Allows(EnvironmentLightLocationRuleResolution? locationRule)
    {
        if (
            locationRule is null
            || locationRule.Status is not (
                EnvironmentLightLocationRuleStatus.Matched
                or EnvironmentLightLocationRuleStatus.Unmatched
            )
        )
        {
            return false;
        }

        var blessing = blessingProvider();
        return blessing.IsAvailable
            && (!blessing.IsEnabled || !locationRule.JunimoBlessingEligible);
    }

    /// <summary>
    /// Natural-light rendering and darkness attacks share this exact protected-place meaning.
    /// Rendering is not an attack authorization path, so an unavailable configuration remains
    /// unprotected here while <see cref="Allows" /> keeps the attack itself fail-closed.
    /// </summary>
    internal bool IsJunimoBlessingProtecting(
        EnvironmentLightLocationRuleResolution? locationRule
    )
    {
        if (locationRule?.Status != EnvironmentLightLocationRuleStatus.Matched)
            return false;

        var blessing = blessingProvider();
        return blessing.IsAvailable
            && blessing.IsEnabled
            && locationRule.JunimoBlessingEligible;
    }
}
