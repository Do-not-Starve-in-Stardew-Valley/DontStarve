#nullable enable

using System;

namespace DontStarve.Player.Combat.InvincibilityFrames;

/// <summary>
/// Pure rules for the per-hit invincibility policy. The game adapter owns Farmer fields; this
/// class only decides whether a hit may replace vanilla duration and what that duration is.
/// </summary>
internal static class InvincibilityFrameRules
{
    internal static InvincibilityFrameResolution ResolveDuration(
        InvincibilityFramePolicy policy,
        int bonusMilliseconds
    )
    {
        if (policy.Mode == InvincibilityFramePolicyMode.Normal)
            return InvincibilityFrameResolution.PreserveVanilla();

        if (policy.DurationMilliseconds < 0)
            return InvincibilityFrameResolution.Failed(
                "invincibility-frame.policy-duration-negative"
            );

        if (bonusMilliseconds < 0)
            return InvincibilityFrameResolution.Failed(
                "invincibility-frame.bonus-negative"
            );

        if (
            policy.Mode != InvincibilityFramePolicyMode.Strong
            && policy.Mode != InvincibilityFramePolicyMode.Weak
        )
        {
            return InvincibilityFrameResolution.Failed(
                "invincibility-frame.policy-mode-unknown"
            );
        }

        if (policy.Mode == InvincibilityFramePolicyMode.Strong)
            return InvincibilityFrameResolution.Override(
                policy.DurationMilliseconds
            );

        try
        {
            var duration = checked(
                (long)policy.DurationMilliseconds + bonusMilliseconds
            );
            if (duration > int.MaxValue)
            {
                return InvincibilityFrameResolution.Failed(
                    "invincibility-frame.duration-overflow"
                );
            }

            return InvincibilityFrameResolution.Override((int)duration);
        }
        catch (OverflowException)
        {
            return InvincibilityFrameResolution.Failed(
                "invincibility-frame.duration-overflow"
            );
        }
    }

    internal static InvincibilityFrameDamageOutcome Classify(
        InvincibilityFrameObservation observation
    )
    {
        if (
            observation.TemporarilyInvincibleBefore
            && !observation.CanBeDamagedBefore
        )
        {
            return InvincibilityFrameDamageOutcome.BlockedByExistingInvincibility;
        }

        if (!observation.YobaBuffBefore && observation.YobaBuffAfter)
            return InvincibilityFrameDamageOutcome.YobaPreventedDamage;

        if (
            observation.HealthAfter != observation.HealthBefore
            || observation.VanillaInvincibilityGranted
        )
        {
            return InvincibilityFrameDamageOutcome.Applied;
        }

        return InvincibilityFrameDamageOutcome.NoChange;
    }
}
