using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerFishingCastGateStaticContractTests
{
    private const string ServiceRelativePath =
        "DontStarve/Player/Stats/Hunger/HungerFishingCastGateService.cs";

    [Fact]
    public void CastGateIsBoundToTheVanillaFishingRodBeginUsingSeam()
    {
        var source = ReadServiceSource();

        Assert.Contains("typeof(FishingRod)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(FishingRod.beginUsing)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedCastMirrorsTheVanillaExhaustedStaminaReset()
    {
        // performBeginUsingTool 已先置 CanMove=false / UsingTool=true / canReleaseTool=true，
        // 拦截必须完整镜像原版体力耗尽分支的复位，否则玩家卡在持杆状态。
        var source = ReadServiceSource();
        var method = ExtractMethod(source, "private void BlockCast(FishingRod rod, Farmer player)");

        Assert.Contains("player.isEmoting", method, StringComparison.Ordinal);
        Assert.Contains("player.doEmote(36)", method, StringComparison.Ordinal);
        Assert.Contains("player.CanMove = !Game1.eventUp;", method, StringComparison.Ordinal);
        Assert.Contains("player.UsingTool = false;", method, StringComparison.Ordinal);
        Assert.Contains("player.canReleaseTool = false;", method, StringComparison.Ordinal);
        Assert.Contains("rod.doneFishing(null);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CastGateKeepsTheSharedHungerBoundaryAndLocalPlayerScope()
    {
        var source = ReadServiceSource();

        // 饥饿系统关闭时不判定；数值边界必须走共享纯规则，禁止复制 hunger <= 0 表达式。
        Assert.Contains("HungerExtensions.IsEnabled", source, StringComparison.Ordinal);
        Assert.Contains(
            "HungerStarvationRules.IsStarving(who.GetHunger())",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("hunger <= 0d", source, StringComparison.Ordinal);
        // 只拦本地玩家；远端挥杆由其所在机器的同一规则拦截。
        Assert.Contains("who.IsLocalPlayer", source, StringComparison.Ordinal);
        // 剧情事件内放行，避免脚本软锁。
        Assert.Contains("Game1.eventUp", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StarvingCastUsesItsOwnTranslatedMessage()
    {
        var source = ReadServiceSource();

        Assert.Contains(
            "private const string StarvingCastBlockedMessageKey = \"fishing-starving-blocked\";",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "helper.Translation.Get(StarvingCastBlockedMessageKey)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("HUDMessage.error_type", source, StringComparison.Ordinal);
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
