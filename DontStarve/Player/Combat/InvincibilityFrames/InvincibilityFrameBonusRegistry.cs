#nullable enable

using System;
using System.Collections.Generic;
using StardewValley;

namespace DontStarve.Player.Combat.InvincibilityFrames;

internal sealed class InvincibilityFrameBonusRegistry
{
    private readonly List<IInvincibilityFrameBonusSource> sources = new();

    internal bool TryRegister(IInvincibilityFrameBonusSource source, out string reason)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Id))
        {
            reason = "invincibility-frame.bonus-source-invalid";
            return false;
        }

        if (sources.Count >= InvincibilityFrameBonusRules.MaximumSources)
        {
            reason = "invincibility-frame.bonus-source-capacity";
            return false;
        }

        foreach (var existing in sources)
        {
            if (string.Equals(existing.Id, source.Id, StringComparison.Ordinal))
            {
                reason = "invincibility-frame.bonus-source-duplicate";
                return false;
            }
        }

        sources.Add(source);
        reason = "invincibility-frame.bonus-source-registered";
        return true;
    }

    internal bool TryResolve(
        Farmer target,
        out int totalMilliseconds,
        out string reason
    )
    {
        totalMilliseconds = 0;
        reason = string.Empty;
        if (target is null)
        {
            reason = "invincibility-frame.bonus-target-null";
            return false;
        }

        foreach (var source in sources)
        {
            InvincibilityFrameBonusContribution contribution;
            string sourceReason;
            try
            {
                if (
                    !source.TryGetBonusMilliseconds(
                        target,
                        out contribution,
                        out sourceReason
                    )
                )
                {
                    reason = string.IsNullOrWhiteSpace(sourceReason)
                        ? string.Concat("invincibility-frame.bonus-source-failed:", source.Id)
                        : sourceReason;
                    return false;
                }
            }
            catch (Exception exception)
            {
                reason = string.Concat(
                    "invincibility-frame.bonus-source-threw-",
                    source.Id,
                    "-",
                    exception.GetType().Name
                );
                return false;
            }

            if (
                !string.Equals(
                    contribution.SourceId,
                    source.Id,
                    StringComparison.Ordinal
                )
            )
            {
                reason = "invincibility-frame.bonus-source-identity-invalid";
                return false;
            }

            if (contribution.Milliseconds < 0)
            {
                reason = "invincibility-frame.bonus-negative";
                return false;
            }

            try
            {
                totalMilliseconds = checked(
                    totalMilliseconds + contribution.Milliseconds
                );
            }
            catch (OverflowException)
            {
                totalMilliseconds = 0;
                reason = "invincibility-frame.bonus-overflow";
                return false;
            }
        }

        reason = "invincibility-frame.bonus-aggregate-valid";
        return true;
    }
}
