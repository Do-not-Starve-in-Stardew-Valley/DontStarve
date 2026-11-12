using System.Text.Json;
using DontStarve.Audio.StatusFeedback;
using Xunit;

namespace DontStarve.Tests.Audio.StatusFeedback;

/// <summary>
/// cues.json 与六 ogg 资源的交付契约：文件存在、时长大于 0、方向字段正确，
/// 且与代码侧 StatusFeedbackCueCatalog 完全一致。
/// </summary>
public sealed class StatusFeedbackCuesContractTests
{
    private static string ShippedModPath(params string[] segments)
    {
        var rooted = new[] { AppContext.BaseDirectory, "ShippedMod" }
            .Concat(segments)
            .ToArray();
        return Path.Combine(rooted);
    }

    [Fact]
    public void CuesJsonExistsAndParsesAgainstCatalog()
    {
        var path = ShippedModPath(
            "Asset",
            "Audio",
            "StatusFeedback",
            StatusFeedbackCueCatalog.CuesFileName
        );
        Assert.True(File.Exists(path), $"Missing cues file: {path}");

        var document = StatusFeedbackCuesDocument.Parse(File.ReadAllText(path));
        Assert.True(document.IsAvailable, "cues.json did not parse into the full six-cue contract.");
        Assert.Equal(StatusFeedbackCueCatalog.ExpectedCueCount, document.Cues.Count);

        foreach (var cue in document.Cues)
        {
            Assert.True(
                StatusFeedbackCueCatalog.TryGetCue(cue.Id, out var catalogCue)
                    && catalogCue is not null
                    && catalogCue.System == cue.System
                    && catalogCue.Direction == cue.Direction,
                $"cues.json entry {cue.Id} disagrees with the code catalog."
            );
            Assert.True(cue.DurationSeconds > 0, $"Cue {cue.Id} must declare a positive duration.");
        }
    }

    [Fact]
    public void AllSixOggFilesExistAndAreNonEmpty()
    {
        Assert.Equal(6, StatusFeedbackCueCatalog.Cues.Count);
        foreach (var cue in StatusFeedbackCueCatalog.Cues)
        {
            var path = ShippedModPath(
                cue.RelativePath.Split('/')
            );
            Assert.True(File.Exists(path), $"Missing audio file: {path}");
            Assert.True(new FileInfo(path).Length > 0, $"Empty audio file: {path}");
        }
    }

    [Fact]
    public void CatalogCoversEachSystemInBothDirections()
    {
        foreach (var system in Enum.GetValues<StatusFeedbackSystem>())
        {
            foreach (var direction in Enum.GetValues<StatusFeedbackDirection>())
            {
                var match = StatusFeedbackCueCatalog.Cues.Any(
                    cue => cue.System == system && cue.Direction == direction
                );
                Assert.True(
                    match,
                    $"Catalog is missing a cue for {system}/{direction}."
                );
            }
        }
    }

    [Fact]
    public void CatalogDirectionsMatchCueIdNaming()
    {
        foreach (var cue in StatusFeedbackCueCatalog.Cues)
        {
            var expected = cue.Id.EndsWith("up", StringComparison.Ordinal)
                ? StatusFeedbackDirection.Up
                : StatusFeedbackDirection.Down;
            Assert.Equal(expected, cue.Direction);
        }
    }

    [Fact]
    public void CatalogDurationsMatchShippedCuesJson()
    {
        var path = ShippedModPath(
            "Asset",
            "Audio",
            "StatusFeedback",
            StatusFeedbackCueCatalog.CuesFileName
        );
        using var raw = JsonDocument.Parse(File.ReadAllText(path));
        var shipped = raw.RootElement.GetProperty("Cues").EnumerateArray()
            .ToDictionary(
                entry => entry.GetProperty("id").GetString()!,
                entry => entry.GetProperty("durationSeconds").GetDouble()
            );

        foreach (var cue in StatusFeedbackCueCatalog.Cues)
        {
            Assert.True(
                shipped.TryGetValue(cue.Id, out var duration)
                    && Math.Abs(duration - cue.DurationSeconds) <= 0.01,
                $"Catalog duration for {cue.Id} must mirror cues.json within 0.01s."
            );
        }
    }

    [Fact]
    public void ParseRejectsMalformedCuesDocuments()
    {
        Assert.False(StatusFeedbackCuesDocument.Parse("").IsAvailable);
        Assert.False(StatusFeedbackCuesDocument.Parse("{}").IsAvailable);
        Assert.False(StatusFeedbackCuesDocument.Parse("{\"FormatVersion\":1,\"Cues\":[]}").IsAvailable);
        Assert.False(
            StatusFeedbackCuesDocument.Parse(
                "{\"FormatVersion\":2,\"Cues\":[]}"
            ).IsAvailable
        );
    }
}
