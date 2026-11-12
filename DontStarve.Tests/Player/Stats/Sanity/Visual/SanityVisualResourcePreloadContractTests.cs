using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.Visual;

public sealed class SanityVisualResourcePreloadContractTests
{
    [Fact]
    public void PermanentlyRetiredDangerBorderIsNeverRequestedByRuntime()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Stats",
            "Sanity",
            "Visual",
            "SanitySmapiVisualService.cs"
        ).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("SaveLoaded += OnSaveLoaded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadDangerBorder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawDangerBorder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadVisualSlot(DangerBorderSlotId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmlessProjectionVisualsAreWarmedFromRegisteredPoliciesAfterSaveLoaded()
    {
        var source = ReadSource(
            "DontStarve",
            "Player",
            "Stats",
            "Sanity",
            "Illusions",
            "Projection",
            "SmapiHarmlessProjectionHost.cs"
        ).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;",
            source,
            StringComparison.Ordinal
        );
        var start = source.IndexOf("private void OnSaveLoaded", StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void OnUpdateTicked",
            start,
            StringComparison.Ordinal
        );

        Assert.True(start >= 0);
        Assert.True(end > start);
        var preload = source[start..end];
        Assert.Contains(
            "if (disposed || !lifecycle.IsEnabled)",
            preload,
            StringComparison.Ordinal
        );
        Assert.Contains("scheduler.Policies", preload, StringComparison.Ordinal);
        Assert.Contains("shadowCoordinator.Policies", preload, StringComparison.Ordinal);
        Assert.Contains("TryLoadSpawnVisuals", preload, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
