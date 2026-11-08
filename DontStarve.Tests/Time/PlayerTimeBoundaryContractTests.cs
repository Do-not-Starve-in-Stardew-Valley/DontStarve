using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DontStarve.Tests.Time;

public sealed class PlayerTimeBoundaryContractTests
{
    private static readonly string[] OrdinarySanityBehaviors =
    {
        "Night.cs",
        "MineShaft.cs",
        "NearNpc.cs",
        "NearMonster.cs",
        "Wearing.cs",
    };

    [Fact]
    public void PublicTimeApiContractRemainsLongAndListBased()
    {
        var source = ReadSource("DontStarve", "Interface", "ITimeAPI.cs");

        Assert.Contains("public long Time", source, StringComparison.Ordinal);
        Assert.Contains("List<Action<long>> OnLoad", source, StringComparison.Ordinal);
        Assert.Contains("List<Action<long>> OnUpdate", source, StringComparison.Ordinal);
        Assert.Contains(
            "List<Action<long, long>> OnSync",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void OrdinarySanityBehaviorsUseThePlayerDimensionAndNeverGame1Player()
    {
        foreach (var fileName in OrdinarySanityBehaviors)
        {
            var source = ReadSource(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "SanityBehaviors",
                fileName
            );

            Assert.Contains(
                ": IPlayerTimeRelatedBehavior",
                source,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "Update(Farmer player, long time)",
                source,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "Sync(string playerKey, long time, long delta)",
                source,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain("Game1.player", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SanitySchedulerEnumeratesOnlineFarmersBeforeRunningTimeBehaviors()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Stats",
            "Sanity",
            "Sanity.cs"
        );

        Assert.Contains("Game1.getOnlineFarmers()", source, StringComparison.Ordinal);
        Assert.Contains(
            "behavior.Update(player, time)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "behavior.Sync(playerKey, time, delta)",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void OrdinarySanityBehaviorsExposeSleepingAsAnExplicitBoundary()
    {
        var sources = new List<string>();
        foreach (var fileName in OrdinarySanityBehaviors)
        {
            sources.Add(
                ReadSource(
                    "DontStarve",
                    "Player",
                    "Stats",
                    "Sanity",
                    "SanityBehaviors",
                    fileName
                )
            );
        }

        var combined = string.Join(Environment.NewLine, sources);
        Assert.Contains("isInBed.Value", combined, StringComparison.Ordinal);
        Assert.Contains(
            "Dictionary<string, long>",
            combined,
            StringComparison.Ordinal
        );
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solution = Path.Combine(directory.FullName, "DontStarveInSDV.sln");
            if (File.Exists(solution))
                return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray()));

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
