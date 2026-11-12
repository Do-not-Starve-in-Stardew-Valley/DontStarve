#nullable enable

using StardewValley;
using StardewValley.Objects;

namespace DontStarve.Player.Combat.InvincibilityFrames;

internal sealed class ProtectiveRingInvincibilityFrameBonusSource
    : IInvincibilityFrameBonusSource
{
    internal const int MillisecondsPerRing = 400;

    public string Id => "protective-ring";

    public bool TryGetBonusMilliseconds(
        Farmer target,
        out InvincibilityFrameBonusContribution contribution,
        out string reason
    )
    {
        if (target is null)
        {
            contribution = default;
            reason = "invincibility-frame.protective-ring-target-null";
            return false;
        }

        var multiplier = target.GetEffectsOfRingMultiplier(Ring.ProtectiveRingId);
        if (multiplier < 0)
        {
            contribution = default;
            reason = "invincibility-frame.protective-ring-multiplier-negative";
            return false;
        }

        try
        {
            contribution = new InvincibilityFrameBonusContribution(
                Id,
                checked(multiplier * MillisecondsPerRing)
            );
        }
        catch (System.OverflowException)
        {
            contribution = default;
            reason = "invincibility-frame.protective-ring-multiplier-overflow";
            return false;
        }
        reason = "invincibility-frame.protective-ring-resolved";
        return true;
    }
}
