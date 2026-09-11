using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class MinigameDangerGateStaticContractTests
{
    private const string ServiceRelativePath =
        "DontStarve/Player/Stats/Sanity/Minigames/MinigameDangerGateService.cs";

    [Fact]
    public void AbigailStoryExceptionIsBoundToTheVanillaSeedShopCutscenePath()
    {
        var source = ReadServiceSource();
        var method = ExtractMethod(source, "private static bool IsAbigailStoryEvent(Farmer player)");

        Assert.Contains(
            "private const string AbigailStoryEventAssetName = \"Data\\\\Events\\\\SeedShop\";",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "private const string AbigailStoryEventId = \"1\";",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "private const string AbigailStoryCutsceneCommand = \"cutscene AbigailGame\";",
            source,
            StringComparison.Ordinal
        );

        Assert.Contains("Game1.eventUp", method, StringComparison.Ordinal);
        Assert.Contains("!currentEvent.isFestival", method, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(currentEvent.farmer, player)", method, StringComparison.Ordinal);
        Assert.Contains("currentEvent.fromAssetName", method, StringComparison.Ordinal);
        Assert.Contains("currentEvent.id", method, StringComparison.Ordinal);
        Assert.Contains("currentEvent.GetCurrentCommand()", method, StringComparison.Ordinal);
        Assert.Contains("Game1.currentMinigame is AbigailGame", method, StringComparison.Ordinal);
        Assert.Contains("AbigailGame.playingWithAbigail", method, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "!currentEvent.isFestival || Game1.currentMinigame is AbigailGame",
            method,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void DangerGateDoesNotAddAnEventCutsceneHarmony入口()
    {
        var source = ReadServiceSource();
        var installPatches = ExtractMethod(source, "private void InstallPatches()");

        Assert.DoesNotContain("typeof(Event)", installPatches, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultCommands.Cutscene", installPatches, StringComparison.Ordinal);
        Assert.Contains("nameof(GameLocation.showPrairieKingMenu)", installPatches, StringComparison.Ordinal);
    }

    [Fact]
    public void First_blocked_message_is_not_suppressed_by_tick_deduplication()
    {
        var source = ReadServiceSource();
        var method = ExtractMethod(source, "private void ShowBlockedMessage()");

        Assert.Contains("lastBlockedMessageTick != long.MinValue", method, StringComparison.Ordinal);
        Assert.Contains("helper.Translation.Get(BlockedMessageKey)", method, StringComparison.Ordinal);
        Assert.Contains("HUDMessage.error_type", method, StringComparison.Ordinal);
    }

    private static string ReadServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ServiceRelativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate the production source for the static contract: {ServiceRelativePath}"
        );
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature: {signature}");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0, $"Could not find method body: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[start..(index + 1)];
                    break;
            }
        }

        throw new InvalidOperationException($"Could not close method body: {signature}");
    }
}
