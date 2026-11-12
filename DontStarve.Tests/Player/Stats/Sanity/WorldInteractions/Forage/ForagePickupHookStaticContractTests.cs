using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.Forage;

/// <summary>
/// 锁定觅食拾取钩子的原版目标探测契约：GameLocation.checkAction 在 1.6.15 的第二参数是
/// xTile 地图库自己的 xTile.Dimensions.Rectangle，不是 Microsoft.Xna.Framework.Rectangle——
/// 两者无继承关系，类型点错会让 AccessTools 点名扑空，钩子按“签名漂移”fail-closed。
/// 该回归从 2026-07-25 首轮测试到 2026-11-10 启动日志一直存在（游戏版本期间未变），
/// 因此用源码契约在构建期拦下同类错误。
/// </summary>
public sealed class ForagePickupHookStaticContractTests
{
    private const string ServiceRelativePath =
        "DontStarve/Player/Stats/Sanity/WorldInteractions/Forage/SmapiForagePickupReplacementService.cs";

    [Fact]
    public void InstallPickupPatchProbesCheckActionWithTheXTileRectangle()
    {
        var source = ReadServiceSource();
        var install = ExtractMethod(source, "private void InstallPickupPatch()");

        Assert.Contains("nameof(GameLocation.checkAction)", install, StringComparison.Ordinal);
        Assert.Contains("typeof(TileLocation)", install, StringComparison.Ordinal);
        Assert.Contains("typeof(xTile.Dimensions.Rectangle)", install, StringComparison.Ordinal);
        Assert.Contains("typeof(Farmer)", install, StringComparison.Ordinal);
        // XNA Rectangle 是历史踩坑写法：与原版参数类型不同，点名永远扑空。
        Assert.DoesNotContain("typeof(Rectangle)", install, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPickupPatchStaysFailClosedOnTranspilerMarkerReadback()
    {
        var source = ReadServiceSource();
        var install = ExtractMethod(source, "private void InstallPickupPatch()");

        // 方法命中后仍以 transpiler 标记回读为准：标记不匹配必须卸载并保持禁用，
        // 不得因为方法存在就放行未插桩的补丁。
        Assert.Contains("!transpilerMarkerMatched", install, StringComparison.Ordinal);
        Assert.Contains("check-action-signature-drift", install, StringComparison.Ordinal);
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
