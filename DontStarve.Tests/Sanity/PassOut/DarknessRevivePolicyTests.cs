using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class DarknessRevivePolicyTests
{
    [Fact]
    public void Without_candidates_the_vanilla_fallback_hospital_tile_is_used()
    {
        // 原版 updatePause 兜底 warpFarmer("Hospital", 20, 12)：无可用条目时必须落到同一 tile，
        // 保证清晨直醒位置与原版死亡传送完全一致（Data/LocationContexts.xnb 已核实 Default
        // 上下文的唯一无条件条目就是 Hospital (20,12)，常规路径与兜底 tile 相同）。
        var target = DarknessRevivePolicy.ResolveCore(null, _ => true);

        Assert.Equal("Hospital", target.Location);
        Assert.Equal(20, target.TileX);
        Assert.Equal(12, target.TileY);
    }

    [Fact]
    public void The_first_passing_entry_wins_and_failing_conditions_are_skipped()
    {
        var target = DarknessRevivePolicy.ResolveCore(
            new[]
            {
                new DarknessRevivePolicy.ReviveCandidate("FALSE", "Mine", 22, 9),
                new DarknessRevivePolicy.ReviveCandidate(null, "Hospital", 20, 12),
            },
            condition => !string.Equals(condition, "FALSE", StringComparison.Ordinal)
        );

        Assert.Equal("Hospital", target.Location);
        Assert.Equal(20, target.TileX);
        Assert.Equal(12, target.TileY);
    }

    [Fact]
    public void Every_entry_failing_falls_back_to_hospital()
    {
        var target = DarknessRevivePolicy.ResolveCore(
            new[]
            {
                new DarknessRevivePolicy.ReviveCandidate("FALSE", "Mine", 22, 9),
            },
            _ => false
        );

        Assert.Equal("Hospital", target.Location);
        Assert.Equal(20, target.TileX);
        Assert.Equal(12, target.TileY);
    }

    [Fact]
    public void Empty_candidate_list_falls_back_like_the_vanilla_warp_branch()
    {
        var target = DarknessRevivePolicy.ResolveCore(
            Array.Empty<DarknessRevivePolicy.ReviveCandidate>(),
            _ => true
        );

        Assert.Equal("Hospital", target.Location);
        Assert.Equal(20, target.TileX);
        Assert.Equal(12, target.TileY);
    }

    [Fact]
    public void The_stardew_wrapper_mirrors_the_vanilla_updatePause_resolution()
    {
        // 源码契约：Stardew 侧适配必须与 Game1.updatePause 的 killScreen warp 分支一致——
        // 当前地点上下文 ReviveLocations 优先（GSQ 的 location 参数传 null）、缺失回退
        // LocationContexts.Default.ReviveLocations、最终兜底 Hospital (20,12)。
        var service = ReadSource(
            "DontStarve",
            "Player",
            "Stats",
            "Sanity",
            "PassOut",
            "SmapiSanityTwoAmSpecialDeathService.cs"
        );
        var start = service.IndexOf(
            "private static DarknessRevivePolicy.ReviveTarget ResolveDarknessReviveTarget",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        var end = service.IndexOf("internal void BeginPendingDarknessWakeDeath", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = service[start..end];

        Assert.Contains("GetLocationContext().ReviveLocations", method, StringComparison.Ordinal);
        Assert.Contains("LocationContexts.Default.ReviveLocations", method, StringComparison.Ordinal);
        Assert.Contains("GameStateQuery.CheckConditions(condition, null, farmer)", method, StringComparison.Ordinal);
        Assert.Contains("entry.Position.X", method, StringComparison.Ordinal);
        Assert.Contains("entry.Position.Y", method, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.FullName;
            foreach (var part in parts)
                candidate = Path.Combine(candidate, part);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
