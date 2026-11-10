using System;
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
                "Update(Farmer player, string playerKey, long time)",
                source,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain("TryEnterMinute", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AddRewindWait", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IsPlayerInSleepContext", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Game1.player", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SurvivalTimeOwnerDispatchesEachOnlinePlayerToRegisteredBehaviors()
    {
        var source = ReadSource("DontStarve", "Player", "Stats", "SurvivalStateTimeOwner.cs");
        var sanity = ReadSource("DontStarve", "Player", "Stats", "Sanity", "Sanity.cs");

        Assert.Contains("getOnlinePlayers()", source, StringComparison.Ordinal);
        Assert.Contains(
            "DispatchMinute(player, playerKey, minute, SurvivalStateTimePhase.Ordinary)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "survivalTime.Register(this)",
            sanity,
            StringComparison.Ordinal
        );
        Assert.Contains("behavior.Update(player, playerKey, time)", sanity, StringComparison.Ordinal);
    }

    [Fact]
    public void SanityDeclaresItsExistingSleepAndBedPolicyAtTheStatusBoundary()
    {
        var source = ReadSource("DontStarve", "Player", "Stats", "Sanity", "Sanity.cs");
        Assert.Contains("phase != SurvivalStateTimePhase.Ordinary", source, StringComparison.Ordinal);
        Assert.Contains("player.isInBed.Value", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SleepAdapterRoutesIntervalsAndCleansTheSharedOwnerAtLifecycleBoundaries()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );

        Assert.Contains("survivalTime.AdvanceSleepInterval(", source, StringComparison.Ordinal);
        Assert.Contains("SurvivalStateTimePhase.RealtimeSleep", source, StringComparison.Ordinal);
        Assert.Contains("SurvivalStateTimePhase.BlackoutSleep", source, StringComparison.Ordinal);
        Assert.Contains("survivalTime.EndAllSleepContexts()", source, StringComparison.Ordinal);
        Assert.Contains("survivalTime.ForgetPlayer(playerKey)", source, StringComparison.Ordinal);
        Assert.Contains("survivalTime.EndSleep(playerKey, entry.Context.SessionId)", source, StringComparison.Ordinal);
        Assert.Contains("survivalTime.EndSleep(snapshot.PlayerKey, snapshot.SessionId)", source, StringComparison.Ordinal);
        Assert.Contains("survivalTime.ClearSession()", source, StringComparison.Ordinal);

        var savePreflight = source.IndexOf(
            "survivalTime.CanAdvanceSleepInterval(",
            StringComparison.Ordinal
        );
        var finalValueCommit = savePreflight < 0
            ? -1
            : source.IndexOf(
                "ApplyFinalHealth(farmer, result);",
                savePreflight,
                StringComparison.Ordinal
            );
        var saveIntervalDispatch = savePreflight < 0
            ? -1
            : source.IndexOf(
                "survivalTime.AdvanceSleepInterval(",
                savePreflight + 1,
                StringComparison.Ordinal
            );
        Assert.True(savePreflight >= 0);
        Assert.True(finalValueCommit > savePreflight);
        Assert.True(saveIntervalDispatch > finalValueCommit);
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
