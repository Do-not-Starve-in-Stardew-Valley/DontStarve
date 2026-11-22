#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 「自然理智流失规则」三选一（2026-11-17 用户决议，默认 ByLight）。配置缺失/损坏/值非法
/// 一律回落默认 ByLight（用户 2026-11-17 追加拍板：与 GMCM 默认值及项目其它配置的失败
/// 语义对齐，见 <see cref="SanityNaturalDrainRulePolicy.ResolveOrDefault"/>）：
/// <list type="bullet">
/// <item>TimePlace：按时间地点降低理智——即既有 Night/MineShaft 行为，原样保留。</item>
/// <item>ByLight：按光照程度降低理智——规则照搬饥荒不改光照：地面白天 0；黄昏/夜晚/矿井
/// 按脚下可见度分档，&lt;0.1（DST SANITY_LOW_LIGHT）快档 10 倍，其余慢档（DST 亮档
/// &gt;0.6 与昏档数值相同）。光照证据不可确认时不掉（用户决议 6）。</item>
/// <item>Off：两条自然流失全停；近怪物/装备/NPC/温泉等其它来源不受影响。</item>
/// </list>
/// </summary>
internal enum SanityNaturalDrainRule
{
    TimePlace = 0,
    ByLight = 1,
    Off = 2,
}

internal static class SanityNaturalDrainRulePolicy
{
    internal const string TimePlaceConfigValue = "TimePlace";
    internal const string ByLightConfigValue = "ByLight";
    internal const string OffConfigValue = "Off";

    // 【2026-11-18 换算决议】对齐基准 = 一天总长度：星露谷一天 1440 游戏分钟（24 小时口径，
    // 非玩家可活动的 1200 分钟）≈ 饥荒一天 480 真实秒 ⇒ 1 游戏分钟 ≙ 1/3 饥荒秒，
    // DST 每秒速率 ÷ 3 = 每游戏分钟速率（快慢 10 倍比不变）。旧 0.0588/0.588 是
    // 「每真实秒同速」错换算（偏大 2.1 倍）已废弃；决议全文见 AGENTS.md 与工程规则.md。
    /// <summary>DST 慢档 0.0833/秒 ÷ 3（一天长度对齐，2026-11-18 决议）。</summary>
    internal const double SlowPerGameMinute = 0.02778d;

    /// <summary>DST 快档 0.8333/秒 ÷ 3（= 慢档 × 10，10 倍比保持）。</summary>
    internal const double FastPerGameMinute = 0.2778d;

    /// <summary>DST SANITY_LOW_LIGHT：低于此可见度进快档。</summary>
    internal const double FastThreshold = 0.1d;

    /// <summary>
    /// 一天分钟数与一天起点（星露谷时钟从 6:00 开始）。time 是 TimeApi 跨天累计分钟
    /// （TimeApi.Parse = 天数×1440 + 一天内分钟），必须取模取一天内位置；
    /// 直接拿 time 和 timeOfDay 格式的 startDarkTime 比较是单位错配（2026-11-18 回归根因：
    /// 累计超过 startDark 后 isDayOnSurface 永假，白天也按光值档持续掉 san）。
    /// </summary>
    internal const int MinutesPerDay = 24 * 60;
    internal const int DayStartMinutesOfDay = 6 * 60;

    /// <summary>星露谷 timeOfDay（100 进制，1500=15:00）→ 一天内分钟。非法/负值返回 -1。</summary>
    internal static int TimeOfDayToMinutes(int timeOfDay)
    {
        if (timeOfDay < 0 || timeOfDay % 100 >= 60)
            return -1;
        return timeOfDay / 100 * 60 + timeOfDay % 100;
    }

    /// <summary>
    /// 地面/室内的白天分支判定。time 为 TimeApi 跨天累计分钟；startDarkTimeOfDay 为
    /// 原版 getStartingToGetDarkTime（timeOfDay 格式）。白天 = 一天内位置落在
    /// [6:00, startDark)；凌晨 0:00–6:00 与黄昏及以后都算非白天（按光值档，
    /// 对齐饥荒深夜=光值制语义）。startDark 非法时保守视为非白天。
    /// </summary>
    internal static bool IsDayOnSurface(long time, int startDarkTimeOfDay)
    {
        var startDarkMinutes = TimeOfDayToMinutes(startDarkTimeOfDay);
        if (startDarkMinutes < 0)
            return false;
        var minuteOfDay = PositiveModulo(time, MinutesPerDay);
        return minuteOfDay >= DayStartMinutesOfDay && minuteOfDay < startDarkMinutes;
    }

    private static long PositiveModulo(long value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    /// <summary>
    /// 按光照档位的每游戏分钟流失。证据未确认 → 0（用户决议 6：读不到就不掉）；
    /// 地面/室内白天 → 0（对齐 DST 白天分支；矿井/火山是洞类，永远按光值）；
    /// score &lt; 0.1 → 快档；其余（&gt;0.6 亮档与 0.1~0.6 昏档在 DST 数值相同）→ 慢档。
    /// </summary>
    internal static double CalculateLightBasedDrain(
        bool isDayOnSurface,
        bool isCaveLike,
        bool hasConfirmedScore,
        double score
    )
    {
        if (!hasConfirmedScore)
            return 0d;
        if (!double.IsFinite(score) || score < 0d)
            return 0d;
        if (!isCaveLike && isDayOnSurface)
            return 0d;
        return score < FastThreshold ? FastPerGameMinute : SlowPerGameMinute;
    }

    internal static bool TryParse(string? value, out SanityNaturalDrainRule rule)
    {
        switch (value)
        {
            case TimePlaceConfigValue:
                rule = SanityNaturalDrainRule.TimePlace;
                return true;
            case ByLightConfigValue:
                rule = SanityNaturalDrainRule.ByLight;
                return true;
            case OffConfigValue:
                rule = SanityNaturalDrainRule.Off;
                return true;
            default:
                rule = SanityNaturalDrainRule.TimePlace;
                return false;
        }
    }

    /// <summary>
    /// 行为入口的唯一规则取值口：配置缺失（resolver 不可用 / 键不存在）与值非法同义处理，
    /// 一律回落 shipped 默认 ByLight，保证「玩家没有表达偏好 = 按默认」与 GMCM 默认值一致。
    /// TryParse 只做纯解析（失败回落 TimePlace），策略回落集中在本方法。
    /// </summary>
    internal static SanityNaturalDrainRule ResolveOrDefault(string? value)
    {
        return TryParse(value, out var rule) ? rule : SanityNaturalDrainRule.ByLight;
    }
}
