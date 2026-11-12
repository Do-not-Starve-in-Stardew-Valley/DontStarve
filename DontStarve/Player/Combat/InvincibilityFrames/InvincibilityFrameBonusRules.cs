#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Combat.InvincibilityFrames;

internal readonly record struct InvincibilityFrameBonusContribution(
    string SourceId,
    int Milliseconds
);

internal static class InvincibilityFrameBonusRules
{
    internal const int MaximumSources = 16;

    internal static bool TryAggregate(
        IReadOnlyList<InvincibilityFrameBonusContribution> contributions,
        out int totalMilliseconds,
        out string reason
    )
    {
        totalMilliseconds = 0;
        reason = string.Empty;

        if (contributions is null)
        {
            reason = "invincibility-frame.bonus-list-null";
            return false;
        }

        if (contributions.Count > MaximumSources)
        {
            reason = "invincibility-frame.bonus-source-capacity";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            if (string.IsNullOrWhiteSpace(contribution.SourceId))
            {
                reason = "invincibility-frame.bonus-source-id-invalid";
                return false;
            }

            if (!seen.Add(contribution.SourceId))
            {
                reason = "invincibility-frame.bonus-source-duplicate";
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
