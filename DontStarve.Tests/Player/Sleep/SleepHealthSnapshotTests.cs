using System.Reflection;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepHealthSnapshotTests
{
    [Fact]
    public void NativeHealthReplacesStaleProgressButKeepsPaidFractionWhenUnchanged()
    {
        var reconcile = typeof(SleepRules).GetMethod(
            "ReconcileNativeHealth",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(reconcile);

        Assert.Equal(20.6d, Invoke(reconcile!, 20.6d, 20, 100), 10);
        Assert.Equal(19d, Invoke(reconcile!, 20.6d, 19, 100), 10);
    }

    [Fact]
    public void BlackoutSnapshotReconcilesTheLatestNativeHealth()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );

        Assert.Contains(
            "SleepRules.ReconcileNativeHealth(entry.Progress.Health, player.health, entry.Snapshot.MaximumHealth)",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void CancelingBeforeSleepDoesNotResetTheHungerCycle()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        ).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "var hadSleepContext = active.ContainsKey(playerKey) || clientSleeping.Contains(playerKey);",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "if (hadSleepContext && ResolvePlayer(playerKey) is Farmer farmer)\n            HungerExtensions.ResumeAfterSleep(farmer);",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void BlackoutCaptureRecordsTheCurrentStardewCalendarWithoutUsingItAsAFormula()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );

        Assert.Contains("CalendarYear = Game1.year", source, StringComparison.Ordinal);
        Assert.Contains("CalendarSeason = Game1.season.ToString()", source, StringComparison.Ordinal);
        Assert.Contains("CalendarDay = Game1.dayOfMonth", source, StringComparison.Ordinal);
    }

    private static double Invoke(MethodInfo method, double trackedHealth, int nativeHealth, int maximumHealth)
    {
        return (double)method.Invoke(null, new object[] { trackedHealth, nativeHealth, maximumHealth })!;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DontStarveInSDV.sln")))
                return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray()));

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
