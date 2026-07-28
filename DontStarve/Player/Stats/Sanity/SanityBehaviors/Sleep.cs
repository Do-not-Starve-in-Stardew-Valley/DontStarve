#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.PassOut;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 只消费引擎入口已经提交的结束原因；DayEnding 本身没有权限猜睡眠、劳累或死亡。
/// </summary>
internal class Sleep : INonTimeRelatedBehavior
{
    private readonly PassOutReasonLedger ledger;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly Action<string>? diagnostic;
    private string reportedReason = string.Empty;

    internal Sleep(
        PassOutReasonLedger ledger,
        SanitySystemLifecycleCoordinator lifecycle,
        Action<string>? diagnostic = null
    )
    {
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.lifecycle = lifecycle
            ?? throw new ArgumentNullException(nameof(lifecycle));
        this.diagnostic = diagnostic;
    }

    public void Init(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += (_, _) => BeginSession();
        helper.Events.GameLoop.DayStarted += (_, _) => BeginDay();
        helper.Events.GameLoop.DayEnding += (_, _) => SettleDayEnding();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ClearSession();
    }

    private void BeginSession()
    {
        ledger.Clear();
        reportedReason = string.Empty;
    }

    private void BeginDay()
    {
        reportedReason = string.Empty;
        if (SanityProtocol.IsValidSessionId(lifecycle.SessionId))
            ledger.PruneBeforeDay(lifecycle.SessionId, Math.Max(0, Game1.Date.TotalDays));
    }

    private void SettleDayEnding()
    {
        if (
            !Context.IsWorldReady
            || !Context.IsMainPlayer
            || !lifecycle.IsEnabled
            || lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !SanityProtocol.IsValidSessionId(lifecycle.SessionId)
        )
        {
            return;
        }

        foreach (var farmer in Game1.getOnlineFarmers())
        {
            var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                farmer.UniqueMultiplayerID
            );
            if (
                !ledger.TryConsumeDayEnding(
                    lifecycle.SessionId,
                    playerKey,
                    Math.Max(0, Game1.Date.TotalDays),
                    out var evidence,
                    out var reason
                )
            )
            {
                ReportOnce(reason);
                continue;
            }

            var decision = PassOutReasonPolicy.ResolveDayEnding(evidence);
            // DayEnding owns the terminal reason settlement. Remove the consumed receipt now so
            // a repeated callback cannot retain session evidence into save/day boundaries.
            ledger.ForgetPlayer(playerKey);
            if (decision.Action != PassOutPolicyAction.ApplyDelta)
            {
                if (decision.Action == PassOutPolicyAction.None)
                    ReportOnce(decision.StableReason);
                continue;
            }

            var result = farmer.ChangeSanity(decision.Delta, decision.Source);
            if (result.Status == SanityChangeStatus.Rejected)
                ReportOnce(result.Reason);
        }
    }

    private void ClearSession()
    {
        ledger.Clear();
        reportedReason = string.Empty;
    }

    private void ReportOnce(string reason)
    {
        if (
            diagnostic is null
            || string.IsNullOrWhiteSpace(reason)
            || string.Equals(reportedReason, reason, StringComparison.Ordinal)
        )
        {
            return;
        }

        reportedReason = reason;
        diagnostic(reason);
    }
}
