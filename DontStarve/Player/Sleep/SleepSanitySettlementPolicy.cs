#nullable enable

using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;

namespace DontStarve.Player.Sleep;

/// <summary>
/// Shared snapshot seam for special pass-out nights. Both snapshot creation and DayEnding use
/// this method so the live Sanity value cannot diverge from the value written to overnight state.
/// </summary>
internal static class SleepSanitySettlementPolicy
{
    internal static SleepSnapshot Apply(
        SleepSnapshot snapshot,
        SpecialPassOutSanitySettlement settlement,
        string sessionId,
        string playerKey,
        int gameDay
    )
    {
        if (
            !settlement.TryResolve(
                sessionId,
                playerKey,
                gameDay,
                snapshot.Sanity,
                out var overrideValue
            )
        )
            return snapshot;

        return snapshot with
        {
            Sanity = overrideValue.Sanity,
            RecoveryBalance = snapshot.RecoveryBalance with
            {
                SanityRecoveryEnabled = !overrideValue.DisableSleepRecovery,
                SanitySettlementSource = overrideValue.Source,
            },
        };
    }
}
