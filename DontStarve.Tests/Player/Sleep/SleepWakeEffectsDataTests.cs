using System.Collections.Generic;
using System.Linq;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepWakeEffectsDataTests
{
    [Fact]
    public void Legacy_markers_without_dates_are_assigned_the_current_day()
    {
        var data = new SleepWakeEffectsData
        {
            HungerDeaths = new List<string> { "42" },
        };

        var normalized = data.Normalize(currentDay: 100);

        Assert.Equal(100, normalized["42"]);
    }

    [Fact]
    public void Normalize_drops_old_invalid_and_duplicate_markers()
    {
        var data = new SleepWakeEffectsData
        {
            HungerDeaths = new List<string> { "42", "42", "bad", "7" },
            HungerDeathDays = new Dictionary<string, int>
            {
                ["42"] = 100,
                ["7"] = 98,
            },
        };

        var normalized = data.Normalize(currentDay: 100);

        Assert.Equal(new[] { "42" }, normalized.Keys);
    }

    [Fact]
    public void Normalize_caps_markers_to_the_persistence_limit_and_keeps_newest_days()
    {
        var data = new SleepWakeEffectsData();
        for (var index = 0; index < 130; index++)
        {
            var key = (index + 1).ToString();
            data.HungerDeaths.Add(key);
            data.HungerDeathDays[key] = index < 128 ? 99 : 100;
        }

        var normalized = data.Normalize(currentDay: 100);

        Assert.Equal(SleepWakeEffectsPolicy.MaxMarkers, normalized.Count);
        Assert.Contains("129", normalized.Keys);
        Assert.Contains("130", normalized.Keys);
    }

    [Fact]
    public void Readable_data_allows_oversized_input_so_load_can_prune_it()
    {
        var data = new SleepWakeEffectsData
        {
            HungerDeaths = Enumerable.Range(1, 129).Select(value => value.ToString()).ToList(),
        };

        Assert.True(data.IsValid());
        Assert.Equal(SleepWakeEffectsPolicy.MaxMarkers, data.Normalize(100).Count);
    }

    [Fact]
    public void From_also_enforces_the_persistence_limit()
    {
        var markers = Enumerable.Range(1, 130)
            .ToDictionary(value => value.ToString(), _ => 100);

        var data = SleepWakeEffectsData.From(markers, currentDay: 100);

        Assert.Equal(SleepWakeEffectsPolicy.MaxMarkers, data.HungerDeaths.Count);
        Assert.Equal(
            SleepWakeEffectsPolicy.MaxMarkers,
            data.HungerDeathDays.Count
        );
    }

    [Fact]
    public void Hidden_hospital_markers_are_read_without_changing_legacy_hunger_markers()
    {
        var data = new SleepWakeEffectsData
        {
            HungerDeaths = new List<string> { "1" },
            HiddenHospitalDeaths = new List<string> { "2" },
            HiddenHospitalDeathDays = new Dictionary<string, int> { ["2"] = 100 },
        };

        Assert.Equal(new[] { "1" }, data.Normalize(currentDay: 100).Keys);
        Assert.Equal(new[] { "2" }, data.NormalizeHiddenHospital(currentDay: 100).Keys);
    }

    [Fact]
    public void From_writes_hidden_hospital_markers_with_the_same_bounded_retention()
    {
        var data = SleepWakeEffectsData.From(
            new Dictionary<string, int> { ["1"] = 100 },
            new Dictionary<string, int> { ["2"] = 100 },
            currentDay: 100
        );

        Assert.Equal(new[] { "1" }, data.HungerDeaths);
        Assert.Equal(new[] { "2" }, data.HiddenHospitalDeaths);
        Assert.Equal(100, data.HiddenHospitalDeathDays["2"]);
    }
}
