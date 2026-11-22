#nullable enable
using System.Collections.Generic;

namespace DontStarve.Player.Sleep;

/// <summary>Only presentation/death work may remain after a native save. Numeric sleep
/// effects are already saved; this marker never subtracts Hunger, Sanity or HP again.
/// `PendingDarknessDeaths` 是医院直醒方案的防逃课标记：存档落盘即锁定，读档必重演，直到
/// 死亡完成后的下一次存档覆盖。旧档残留的 HiddenHospitalDeaths 字段反序列化时被 Json.NET
/// 安全忽略、不再重演（2026-11-13 HiddenHospital 机器整体废弃）。</summary>
internal sealed class SleepWakeEffectsData
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> HungerDeaths { get; set; } = new();
    public Dictionary<string, int> HungerDeathDays { get; set; } = new();
    public List<string> PendingDarknessDeaths { get; set; } = new();
    public Dictionary<string, int> PendingDarknessDeathDays { get; set; } = new();

    internal bool IsValid()
    {
        return SchemaVersion == 1
            && HungerDeaths is not null
            && HungerDeathDays is not null
            && PendingDarknessDeaths is not null
            && PendingDarknessDeathDays is not null;
    }

    internal Dictionary<string, int> Normalize(int currentDay)
    {
        return SleepWakeEffectsPolicy.Normalize(HungerDeaths, HungerDeathDays, currentDay);
    }

    internal Dictionary<string, int> NormalizePendingDarkness(int currentDay)
    {
        return SleepWakeEffectsPolicy.Normalize(
            PendingDarknessDeaths,
            PendingDarknessDeathDays,
            currentDay
        );
    }

    internal static SleepWakeEffectsData From(
        IReadOnlyDictionary<string, int> markers,
        int currentDay
    ) => From(markers, new Dictionary<string, int>(), currentDay);

    internal static SleepWakeEffectsData From(
        IReadOnlyDictionary<string, int> hungerMarkers,
        IReadOnlyDictionary<string, int> pendingDarknessMarkers,
        int currentDay
    )
    {
        var data = new SleepWakeEffectsData();
        foreach (var pair in SleepWakeEffectsPolicy.Normalize(
            hungerMarkers.Keys,
            hungerMarkers,
            currentDay
        ))
        {
            data.HungerDeaths.Add(pair.Key);
            data.HungerDeathDays[pair.Key] = pair.Value;
        }
        foreach (var pair in SleepWakeEffectsPolicy.Normalize(
            pendingDarknessMarkers.Keys,
            pendingDarknessMarkers,
            currentDay
        ))
        {
            data.PendingDarknessDeaths.Add(pair.Key);
            data.PendingDarknessDeathDays[pair.Key] = pair.Value;
        }
        return data;
    }
}
