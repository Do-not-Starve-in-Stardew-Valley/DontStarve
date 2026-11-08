#nullable enable

using System;
using DontStarve.Player.Stats.Hunger;

namespace DontStarve.Player.Sleep;

/// <summary>
/// Sleep 的唯一纯结算入口。实时和黑屏批算都逐分钟调用同一个步进，避免两套边界公式漂移。
/// </summary>
internal static class SleepRules
{
    internal static SleepProgress SettleRealtime(
        SleepSnapshot snapshot,
        long startMinute,
        long targetMinute,
        HungerDamageMode damageMode = HungerDamageMode.Default
    )
    {
        return Settle(snapshot, startMinute, targetMinute, SleepPhase.Realtime, damageMode);
    }

    internal static SleepProgress SettleBlackoutBatch(
        SleepSnapshot snapshot,
        long blackoutStartMinute,
        long targetMinute,
        HungerDamageMode damageMode = HungerDamageMode.Default
    )
    {
        return Settle(
            snapshot,
            blackoutStartMinute,
            targetMinute,
            SleepPhase.BlackoutBatch,
            damageMode
        );
    }

    internal static bool ShouldConfirmHungerWarning(
        SleepRecoveryBalance balance,
        SleepProgress prediction
    )
    {
        return balance.HungerEnabled && prediction.Hunger <= 0d;
    }

    /// <summary>
    /// Retain fractional sleep recovery while the native integer HP is unchanged; use the
    /// observed value when another game system has healed or damaged the farmer meanwhile.
    /// </summary>
    internal static double ReconcileNativeHealth(
        double trackedHealth,
        int nativeHealth,
        int maximumHealth
    )
    {
        var maximum = Math.Max(0, maximumHealth);
        var observed = Math.Clamp(nativeHealth, 0, maximum);
        var tracked = Math.Clamp(NormalizeFinite(trackedHealth, observed), 0d, maximum);
        return (int)Math.Floor(tracked + 1e-9) == observed ? tracked : observed;
    }

    private static SleepProgress Settle(
        SleepSnapshot snapshot,
        long startMinute,
        long targetMinute,
        SleepPhase phase,
        HungerDamageMode damageMode
    )
    {
        var progress = SleepProgress.Start(snapshot, startMinute) with { Phase = phase };
        if (targetMinute <= startMinute)
            return progress with { Phase = SleepPhase.Completed };

        for (var minute = startMinute; minute < targetMinute; minute++)
            progress = AdvanceMinute(snapshot, progress, damageMode, phase);

        return progress with
        {
            LastProcessedMinute = targetMinute,
            Phase = SleepPhase.Completed,
        };
    }

    private static SleepProgress AdvanceMinute(
        SleepSnapshot snapshot,
        SleepProgress progress,
        HungerDamageMode damageMode,
        SleepPhase phase
    )
    {
        var balance = snapshot.RecoveryBalance;
        var naturalLoss = balance.HungerEnabled
            ? NormalizePositive(balance.NaturalHungerPerMinute, 0.052d) : 0d;
        var hungerBefore = NormalizeFinite(progress.Hunger, 0d);
        var hungerAfterNatural = Math.Max(0d, hungerBefore - naturalLoss);
        var health = Math.Clamp(NormalizeFinite(progress.Health, 0d), 0d, Math.Max(0, snapshot.MaximumHealth));
        var sanityBefore = NormalizeFinite(progress.Sanity, 0d);
        var maximumSanity = NormalizePositive(snapshot.MaximumSanity, 0d);
        var sanityRecovery = 0d;
        var hungerPaid = 0d;

        if (snapshot.Mode == SleepMode.Voluntary)
        {
            var bedMultiplier = NormalizePositive(balance.BedSanityMultiplier, 1d);
            var baseRecovery = NormalizePositive(
                balance.BaseSanityRecoveryPerMinute,
                0.3d
            );
            var sanityMissing = balance.SanityEnabled ? Math.Max(0d, maximumSanity - sanityBefore) : 0d;
            if (balance.Enabled && balance.HungerEnabled)
            {
                var costPerSanity = NormalizePositive(balance.HungerCostPerSanity, 1d);
                // One shared recovery unit restores 1 Sanity AND 2 health. Either deficit
                // can keep the payment active after the other stat reaches its own cap.
                var requestedRecovery = Math.Min(baseRecovery * bedMultiplier,
                    Math.Max(sanityMissing, (snapshot.MaximumHealth - health) / 2d));
                var availableHunger = hungerAfterNatural;
                hungerPaid = Math.Min(availableHunger, requestedRecovery * costPerSanity);
                var recoveryUnits = hungerPaid / costPerSanity;
                sanityRecovery = Math.Min(sanityMissing, recoveryUnits);
                health = Math.Min(snapshot.MaximumHealth, health + 2d * recoveryUnits);
            }
            else
            {
                // 关闭平衡只移除 Hunger 对睡眠恢复的支付限制；主动睡眠的原有
                // Sanity 恢复和床倍率仍然生效，避免把配置开关误当成睡眠恢复总开关。
                sanityRecovery = Math.Min(baseRecovery * bedMultiplier, sanityMissing);
            }
        }

        var hungerDamage = 0;
        if (balance.HungerEnabled && hungerBefore <= 0d)
        {
            var floor = damageMode == HungerDamageMode.NonLethal
                ? Math.Ceiling(snapshot.MaximumHealth * 0.2d) : 0d;
            if (damageMode != HungerDamageMode.Disabled && health > floor)
            {
                health = Math.Max(floor, health - 1d);
                hungerDamage = 1;
            }
        }

        return new SleepProgress(
            ProcessedMinutes: progress.ProcessedMinutes + 1,
            LastProcessedMinute: progress.LastProcessedMinute + 1,
            Hunger: hungerAfterNatural - hungerPaid < 1e-10 ? 0d : hungerAfterNatural - hungerPaid,
            Sanity: Math.Min(maximumSanity, sanityBefore + sanityRecovery),
            Health: health,
            NaturalHungerConsumed: progress.NaturalHungerConsumed + naturalLoss,
            HungerPaidForRecovery: progress.HungerPaidForRecovery + hungerPaid,
            SanityRecovered: progress.SanityRecovered + sanityRecovery,
            HungerDamage: progress.HungerDamage + hungerDamage,
            Phase: phase
        );
    }

    private static double NormalizeFinite(double value, double fallback)
    {
        return double.IsFinite(value) ? value : fallback;
    }

    private static double NormalizePositive(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0d ? value : fallback;
    }
}
