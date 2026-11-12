#nullable enable

using System;
using System.Globalization;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 新生命值系统的余数账本纯规则（20261112 设计方案决议 4 的「事件落账」）。
/// 真值 = 原版 Farmer.health（对外 int 窗口）+ 余数（本 mod 的小数零头账本）。
/// 这里不引用任何 Stardew/SMAPI 类型，所有边界用参数表达，便于纯规则测试。
/// 外部改动（怪物伤害、睡觉回血、升级、其它 mod）零折叠：它们直接动 Farmer.health，
/// 不变量使真值自动吸收；本文件绝不提供「把外部差额折进账本」的入口（会双计）。
/// </summary>
internal static class HealthLedgerRules
{
    // 余数进位的防御上限：正常事件每次最多跨 1 个整点，上限只为脏数据兜底
    // （例如存档里被写入 1e300 这类可解析但荒谬的值），防止 while 进位挂死循环；
    // 超出预算的残余由随后的余数钳制拉回真值域。
    private const int MaxRemainderCarries = 4096;

    /// <summary>
    /// 小数模式事件落账：效果 Δ 先 Truncate 取向零整数落到血量，零头进余数按方向累计，
    /// |余数| ≥ 1 立即向整数部分兑现；写血钳 0..maxHealth；真值 ≤ 0 判死（血 0、余数 0）。
    /// </summary>
    internal static (int NewHealth, double NewRemainder, bool IsLethal) ApplyEvent(
        int currentHealth,
        double remainder,
        double delta,
        int maxHealth
    )
    {
        // 边界：非有限输入一律视为中性效果，避免 NaN/Infinity 永久污染账本。
        if (!double.IsFinite(delta))
            delta = 0;
        if (!double.IsFinite(remainder))
            remainder = 0;

        var ceiling = maxHealth < 0 ? 0 : maxHealth;
        var integerDelta = (int)ClampToIntRange(Math.Truncate(delta));
        var newRemainder = remainder + (delta - integerDelta);
        var newHealth = currentHealth + integerDelta;

        // 余数按方向累计并立即兑现；while 防御一次事件跨多个整点（先例形态，预算封顶）。
        var carryBudget = MaxRemainderCarries;
        while (newRemainder >= 1d && carryBudget > 0)
        {
            newRemainder -= 1d;
            newHealth += 1;
            carryBudget--;
        }

        while (newRemainder <= -1d && carryBudget > 0)
        {
            newRemainder += 1d;
            newHealth -= 1;
            carryBudget--;
        }

        // 写血钳制 0..maxHealth：外部抬高上限必须保留（影怪棘轮精神），压低由钳制吸收。
        var clampedHealth = Math.Clamp(newHealth, 0, ceiling);

        // 死亡看真值（血 + 余数 ≤ 0），不看显示。只返回标记：调用方写 0 血、清 0 余数后
        // 什么都不调用，原版 Farmer.update 的 killScreen 链自己完成死亡（不新增死亡入口）。
        if (clampedHealth + newRemainder <= 0)
            return (0, 0d, true);

        // 余数钳制回真值域 [-health, maxHealth - health]，保证真值 ∈ [0, maxHealth]。
        newRemainder = ClampRemainder(newRemainder, clampedHealth, ceiling);
        return (clampedHealth, newRemainder, false);
    }

    /// <summary>
    /// 整数模式（决议 8：「饥荒风格生命值」关）：无余数账本，效果向零取整直接落血并钳制。
    /// </summary>
    internal static (int NewHealth, bool IsLethal) ApplyIntegerEvent(
        int currentHealth,
        double delta,
        int maxHealth
    )
    {
        if (!double.IsFinite(delta))
            delta = 0;

        var ceiling = maxHealth < 0 ? 0 : maxHealth;
        var newHealth = Math.Clamp(
            currentHealth + (int)ClampToIntRange(Math.Truncate(delta)),
            0,
            ceiling
        );
        return (newHealth, newHealth <= 0);
    }

    /// <summary>
    /// 把余数钳制回真值域 [-health, maxHealth - health]（真值 = 血 + 余数 ∈ [0, maxHealth]）。
    /// 读档播种与每次写血后都必须调用；health ≤ 0 的病态区间整体平移，仍保持真值语义。
    /// </summary>
    internal static double ClampRemainder(double remainder, int health, int maxHealth)
    {
        if (!double.IsFinite(remainder))
            return 0;

        var ceiling = maxHealth < 0 ? 0 : maxHealth;
        var lower = -(double)health;
        var upper = (double)(ceiling - health);
        return Math.Clamp(remainder, Math.Min(lower, upper), Math.Max(lower, upper));
    }

    internal static string FormatRemainder(double remainder)
    {
        // InvariantCulture + "R" 往返格式：存档读写必须无损失、无文化漂移。
        return remainder.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static bool TryParseRemainder(string? raw, out double remainder)
    {
        return double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out remainder
        );
    }

    // Truncate 后的整数可能超出 int 表示范围（脏 JSON 数值）；钳进 int 范围防止
    // double→int 强转溢出变成未定义值，随后由写血钳制收敛。
    private static double ClampToIntRange(double value)
    {
        if (value > int.MaxValue)
            return int.MaxValue;
        if (value < int.MinValue)
            return int.MinValue;
        return value;
    }
}
