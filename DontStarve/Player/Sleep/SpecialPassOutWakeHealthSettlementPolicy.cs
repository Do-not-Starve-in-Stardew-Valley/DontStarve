#nullable enable

using System;

namespace DontStarve.Player.Sleep;

/// <summary>
/// 非致命黑暗昏迷"醒来血量上限"的保存前最终值规则，与 <see cref="SleepSanitySettlementPolicy"/>
/// 同属一条特殊昏迷睡眠记账缝，但写回点不同：Sanity 在 DayEnding 写回即可幸存（vanilla
/// dayupdate 不碰 Sanity），血量会被 vanilla dayupdate 满血回填（1.6.15 Farmer.dayupdate），必须
/// 在 dayupdate 之后写回。SmapiSleepLifecycleAdapter 的 dayupdate postfix、SaveFarmhand prefix
/// 和 OnSaving 三个写回点共用本规则，全部位于 SaveGame.Save 序列化之前，存档自然携带最终值。
/// <para>
/// DisableSleepRecovery 语义：饥饿不支付、无恢复单位，血量就是上限值本身；本规则不读取
/// SleepRecoveryBalance，与平衡开关（睡眠恢复平衡）互不干扰。有睡眠上下文的自愿睡眠走
/// SleepOvernightSettlement/SleepRules 既有结算，完全不经过本规则。
/// </para>
/// </summary>
internal static class SpecialPassOutWakeHealthSettlementPolicy
{
    /// <summary>
    /// 把登记的上限值换算为写回血量。上限值来自
    /// SanityTwoAmVanillaPassOutPolicy.ResolveNonLethalWakeHealth（min(捕获血量, ceil(上限×20%))），
    /// 本规则只做 dayupdate 满血回填之后的最终值裁剪：高于上限压到上限，本来就低保持原值。
    /// </summary>
    /// <returns>false 表示不写回：cap&lt;=0 或最大血量&lt;=0 属退化边界（血 0 双死守卫语义下
    /// 不应出现），保持原版晨间值，不写回 0 血。</returns>
    internal static bool TryResolveFinalHealth(
        int maximumHealth,
        int wakeHealthCap,
        out int finalHealth
    )
    {
        finalHealth = 0;
        if (maximumHealth <= 0 || wakeHealthCap <= 0)
            return false;
        finalHealth = Math.Min(maximumHealth, wakeHealthCap);
        return true;
    }
}
