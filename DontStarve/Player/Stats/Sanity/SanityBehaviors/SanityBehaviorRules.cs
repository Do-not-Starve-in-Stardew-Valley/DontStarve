#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

internal enum FriendlyNpcSanityKind
{
    Villager,
    Spouse,
    ChildOrPet,
    Junimo,
}

internal enum MineSanityLocationKind
{
    Other,
    MineShaft,
    VolcanoDungeon,
}

internal readonly record struct EquipmentSanityLoadout(
    string? HatId,
    string? ShirtId,
    string? PantsId,
    string? BootsId,
    string? LeftRingId,
    string? RightRingId,
    string? TrinketId
);

internal readonly record struct MineSanityContext(
    MineSanityLocationKind LocationKind,
    int MineLevel,
    bool IsDarkArea,
    bool IsQuarryArea,
    bool IsSlimeArea,
    bool IsMonsterArea,
    bool IsDinoArea,
    int AdditionalDifficulty
);

internal readonly record struct SleepSanityResolution(
    bool Success,
    double Delta,
    string Reason
)
{
    internal static SleepSanityResolution Resolved(double delta, string reason)
    {
        return new SleepSanityResolution(true, delta, reason);
    }

    internal static SleepSanityResolution Rejected(string reason)
    {
        return new SleepSanityResolution(false, 0, reason);
    }
}

/// <summary>
/// 现有 Sanity 行为的纯逻辑规则。这里只冻结既有数值、覆盖顺序和普通睡眠时间语义，
/// 不读取 Stardew/SMAPI 状态，也不承担后续配置、特殊死亡或新平衡职责。
/// </summary>
internal static class SanityBehaviorRules
{
    private const double NearbyRangeTiles = 10d;
    private const int MidnightEndMinutes = 6 * 60;
    private const int MinutesPerDay = 24 * 60;
    private const int OrdinaryDayStart = 600;
    private const int OrdinaryDayEnd = 2600;
    private const int TwoAmTenMinuteScale = 26 * 6;

    internal static double GetDistancePercentage(double distanceTiles)
    {
        if (!double.IsFinite(distanceTiles) || distanceTiles < 0)
            return 0;

        return Math.Max(0, 1 - distanceTiles / NearbyRangeTiles);
    }

    internal static double CalculateFriendlyNpcRecovery(
        FriendlyNpcSanityKind kind,
        int friendshipHearts,
        double distanceTiles
    )
    {
        var percentage = GetDistancePercentage(distanceTiles);
        if (percentage <= 0)
            return 0;

        var baseRecovery = kind switch
        {
            FriendlyNpcSanityKind.Spouse => 1.176,
            FriendlyNpcSanityKind.Villager when friendshipHearts >= 8 => 0.588,
            FriendlyNpcSanityKind.Villager when friendshipHearts >= 5 => 0.294,
            // Child/Pet 的现有数据把 5 心作为最高档；不在本阶段猜测更高心数的新平衡。
            FriendlyNpcSanityKind.ChildOrPet when friendshipHearts == 5 => 0.588,
            FriendlyNpcSanityKind.ChildOrPet when friendshipHearts >= 3 => 0.294,
            FriendlyNpcSanityKind.ChildOrPet => 0.147,
            FriendlyNpcSanityKind.Junimo => 0.294,
            _ => 0,
        };

        return baseRecovery * percentage;
    }

    internal static double CalculateMonsterLoss(
        double configuredLoss,
        double distanceTiles
    )
    {
        if (!double.IsFinite(configuredLoss) || configuredLoss <= 0)
            return 0;

        return configuredLoss * GetDistancePercentage(distanceTiles);
    }

    internal static double CalculateEquipmentDelta(
        EquipmentSanityLoadout loadout,
        IReadOnlyDictionary<string, double> hatValues,
        IReadOnlyDictionary<string, double> shirtValues,
        IReadOnlyDictionary<string, double> pantsValues,
        IReadOnlyDictionary<string, double> bootsValues,
        IReadOnlyDictionary<string, double> ringValues,
        IReadOnlyDictionary<string, double> trinketValues
    )
    {
        var total = 0d;
        total += GetConfiguredValue(hatValues, loadout.HatId);
        total += GetConfiguredValue(shirtValues, loadout.ShirtId);
        total += GetConfiguredValue(pantsValues, loadout.PantsId);
        total += GetConfiguredValue(bootsValues, loadout.BootsId);
        total += GetConfiguredValue(ringValues, loadout.LeftRingId);
        total += GetConfiguredValue(ringValues, loadout.RightRingId);
        total += GetConfiguredValue(trinketValues, loadout.TrinketId);
        return double.IsFinite(total) ? total : 0;
    }

    internal static double CalculateNightLossForMinute(
        long time,
        int nightfallStartMinutes,
        bool isOutdoors
    )
    {
        if (
            nightfallStartMinutes < MidnightEndMinutes
            || nightfallStartMinutes >= MinutesPerDay
        )
        {
            return 0;
        }

        var timeOfDay = PositiveModulo(time, MinutesPerDay);
        var lastTimeOfDay = PositiveModulo(time - 1, MinutesPerDay);
        var nightfallMinutes = 0L;
        var midnightMinutes = 0L;

        if (timeOfDay < nightfallStartMinutes && timeOfDay >= MidnightEndMinutes)
        {
            if (lastTimeOfDay < MidnightEndMinutes)
                midnightMinutes = MidnightEndMinutes - lastTimeOfDay;
        }
        else if (timeOfDay >= nightfallStartMinutes)
        {
            if (
                lastTimeOfDay < nightfallStartMinutes
                && lastTimeOfDay >= MidnightEndMinutes
            )
            {
                nightfallMinutes = timeOfDay - nightfallStartMinutes;
            }
            else
            {
                nightfallMinutes = timeOfDay - lastTimeOfDay;
            }
        }
        else if (lastTimeOfDay >= nightfallStartMinutes)
        {
            nightfallMinutes = MinutesPerDay - lastTimeOfDay;
            midnightMinutes = timeOfDay;
        }
        else
        {
            midnightMinutes = timeOfDay - lastTimeOfDay;
        }

        var nightfallLoss = nightfallMinutes * 0.0588;
        var midnightLoss = midnightMinutes * (isOutdoors ? 0.1176 : 0.0588);
        return nightfallLoss + midnightLoss;
    }

    internal static double CalculateMineLoss(MineSanityContext context)
    {
        if (context.LocationKind == MineSanityLocationKind.VolcanoDungeon)
            return 0.1176;
        if (context.LocationKind != MineSanityLocationKind.MineShaft)
            return 0;

        // 覆盖与叠加顺序刻意保持旧行为：dark/level/quarry 依次覆盖，区域与难度再相加。
        var value = 0.0588;
        if (context.IsDarkArea)
            value = 0.588;
        if (context.MineLevel > 120)
            value = 0.1176;
        if (context.MineLevel > 1000)
            value = 0.2352;
        if (context.IsQuarryArea)
            value = 0.1764;
        if (context.IsSlimeArea)
            value += 0.1176;
        if (context.IsMonsterArea)
            value += 0.2352;
        if (context.IsDinoArea)
            value += 0.2352;
        if (context.AdditionalDifficulty > 0)
            value += context.MineLevel > 120 ? 0.2352 : 0.1176;

        return value;
    }

    internal static SleepSanityResolution ResolveOrdinarySleep(int timeOfDay)
    {
        if (!TryGetTenMinuteScale(timeOfDay, out var timescale))
        {
            return SleepSanityResolution.Rejected(
                "sleep-time-is-outside-the-ordinary-600-to-2600-range"
            );
        }

        if (timescale == TwoAmTenMinuteScale)
            return SleepSanityResolution.Resolved(-20, "ordinary-2am-time-boundary");

        return SleepSanityResolution.Resolved(
            (TwoAmTenMinuteScale - timescale) * 3,
            "ordinary-sleep-before-2am"
        );
    }

    internal static bool IsOrdinarySleepTime(int timeOfDay)
    {
        return TryGetTenMinuteScale(timeOfDay, out _);
    }

    private static bool TryGetTenMinuteScale(int timeOfDay, out int timescale)
    {
        var hour = timeOfDay / 100;
        var minute = timeOfDay % 100;
        if (
            timeOfDay < OrdinaryDayStart
            || timeOfDay > OrdinaryDayEnd
            || minute < 0
            || minute > 50
            || minute % 10 != 0
        )
        {
            timescale = 0;
            return false;
        }

        timescale = hour * 6 + minute / 10;
        return timescale <= TwoAmTenMinuteScale;
    }

    private static double GetConfiguredValue(
        IReadOnlyDictionary<string, double> values,
        string? itemId
    )
    {
        if (
            string.IsNullOrWhiteSpace(itemId)
            || !values.TryGetValue(itemId, out var value)
            || !double.IsFinite(value)
        )
        {
            return 0;
        }

        return value;
    }

    private static long PositiveModulo(long value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}

/// <summary>
/// Sleep 的会话级普通时钟游标。未初始化时明确拒绝结算，避免默认 0 被误算成巨额恢复。
/// </summary>
internal sealed class SleepSanityClock
{
    private int lastTime;

    internal bool HasObservedTime { get; private set; }

    internal int LastTime => lastTime;

    internal bool BeginSession(int timeOfDay, out string reason)
    {
        Clear();
        return Observe(timeOfDay, out reason);
    }

    internal bool Observe(int timeOfDay, out string reason)
    {
        if (!SanityBehaviorRules.IsOrdinarySleepTime(timeOfDay))
        {
            reason = "sleep-time-is-outside-the-ordinary-600-to-2600-range";
            return false;
        }

        lastTime = timeOfDay;
        HasObservedTime = true;
        reason = "sleep-time-observed";
        return true;
    }

    internal SleepSanityResolution Resolve()
    {
        return HasObservedTime
            ? SanityBehaviorRules.ResolveOrdinarySleep(lastTime)
            : SleepSanityResolution.Rejected("sleep-time-was-not-initialized");
    }

    internal void Clear()
    {
        lastTime = 0;
        HasObservedTime = false;
    }
}
