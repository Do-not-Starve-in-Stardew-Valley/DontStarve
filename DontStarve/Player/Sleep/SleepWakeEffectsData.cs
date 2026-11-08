#nullable enable
using System.Collections.Generic;

namespace DontStarve.Player.Sleep;

/// <summary>Only presentation/death work may remain after a native save. Numeric sleep
/// effects are already saved; this marker never subtracts Hunger, Sanity or HP again.</summary>
internal sealed class SleepWakeEffectsData
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> HungerDeaths { get; set; } = new();
    public Dictionary<string, int> HungerDeathDays { get; set; } = new();

    internal bool IsValid()
    {
        return SchemaVersion == 1 && HungerDeaths is not null && HungerDeathDays is not null;
    }

    internal Dictionary<string, int> Normalize(int currentDay)
    {
        return SleepWakeEffectsPolicy.Normalize(HungerDeaths, HungerDeathDays, currentDay);
    }

    internal static SleepWakeEffectsData From(
        IReadOnlyDictionary<string, int> markers,
        int currentDay
    )
    {
        var data = new SleepWakeEffectsData();
        foreach (var pair in SleepWakeEffectsPolicy.Normalize(markers.Keys, markers, currentDay))
        {
            data.HungerDeaths.Add(pair.Key);
            data.HungerDeathDays[pair.Key] = pair.Value;
        }
        return data;
    }
}
