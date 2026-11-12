#nullable enable

using System;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 新生命值系统的公式与门控纯规则（20261112 设计方案决议 1/2/8）。
/// 不读取 Stardew/SMAPI 状态；游戏边界层负责取数后调用这里。
/// </summary>
internal static class HealthRules
{
    /// <summary>
    /// 原版体力→生命换算比（1.6 反编译：Object.healthRecoveredOnConsumption 的
    /// (int)((float)体力 × 0.45f)）。正食的刀与负食的镜像公式共用同一比率，故只留一个常量。
    /// </summary>
    internal const double StaminaHealthRatio = 0.45d;

    /// <summary>
    /// 决议 1：负食扣血 = |体力变化| × 0.45（原版正食生命公式的镜像推广，含品质加成，
    /// 小数保留进账本）。已查证原版 1.6 负食不扣血，本公式由本系统补上。
    /// staminaMirror 来自 SeedEdibilityRuntime.ResolveEffectiveStaminaRecovered
    /// （ceiling(edibility×2.5) + 品质 × edibility，负食为负数）；伤害恒为正数，方向由调用方取负。
    /// </summary>
    internal static double CalculateNegativeFoodHealthDamage(int staminaMirror)
    {
        return Math.Abs((double)staminaMirror) * StaminaHealthRatio;
    }

    /// <summary>
    /// 决议 2（三轮全线口径）：无表正食的刀前真值 = 体力变化 × 0.45，未截断。
    /// 原版刀 = (int)((float)体力 × 0.45f)：纯截断、零随机，刀前值因此可精确重算；
    /// 修正量 = 真值 − 原版已落的整数刀，走事件落账把零头捡回账本（「刀永远存在，
    /// 零头永远被捡回」）。如实代价（设计方案风险 10）：同一食物多吃时血条最终比
    /// 原版偶尔多回 1 点（零头兑现），属「保留小数」的必然结果。
    /// </summary>
    internal static double CalculateKnifeBeforeTrueValue(int staminaMirror)
    {
        return staminaMirror * StaminaHealthRatio;
    }

    /// <summary>
    /// 决议 2 覆盖抵扣：JSON 条目存在（且无特殊公式）时，该物品血量效果完全等于 JSON 值；
    /// 修正量 = JSON 值 − 原版实际结算量。原版实际结算量是 doneEating prefix/postfix 血量
    /// 快照差值，天然覆盖生命药水 773/星果 434/滋补水 349/虫肉块 874 特例与 Math.Min 钳制，
    /// 无需逐条复刻原版公式。
    /// </summary>
    internal static double CalculateFoodCorrection(double jsonValue, int appliedVanillaDelta)
    {
        return jsonValue - appliedVanillaDelta;
    }

    // 决议 8 门控矩阵（v1 两开关互不越界）：
    // | 效果             | 生命值效果平衡 OFF        | 生命值效果平衡 ON                              |
    // | 负食扣血公式     | 不扣血（原版现状）        | 扣（小数落账 / 整数取整）                      |
    // | JSON 食物血量    | 不应用                    | 应用                                           |
    // | 装备六槽效果     | —— 由「饥荒风格生命值」单独门控 ——                                          |
    // | 小数余数账本     | —— 由「饥荒风格生命值」单独门控：关 = 整数模式 ——                          |
    // 两开关全关 = 健康层面 mod 完全不介入，原版行为。

    internal static bool ShouldApplyFoodEffects(bool balanceEnabled)
    {
        return balanceEnabled;
    }

    internal static bool ShouldApplyEquipmentEffects(bool dontStarveHealthEnabled)
    {
        return dontStarveHealthEnabled;
    }

    internal static bool UseDecimalLedger(bool dontStarveHealthEnabled)
    {
        return dontStarveHealthEnabled;
    }
}
