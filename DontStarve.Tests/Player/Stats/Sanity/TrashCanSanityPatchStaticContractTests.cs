using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

/// <summary>
/// 锁定垃圾桶 Harmony 补丁与 SMAPI 自带 Harmony 的兼容契约。
/// Harmony 前缀只允许 void / bool 返回类型；用 return 携带状态会让 harmony.Patch 在安装时
/// 直接抛异常，整个垃圾桶理智功能按 fail-closed 静默禁用（只在启动日志可见，游戏内无提示）。
/// 该回归曾在 2026-11-10 启动日志中实际发生，因此用源码契约在构建期拦下。
/// </summary>
public sealed class TrashCanSanityPatchStaticContractTests
{
    private const string ServiceRelativePath =
        "DontStarve/Player/Stats/Sanity/SmapiTrashCanSanityService.cs";

    [Fact]
    public void CheckGarbagePrefixPassesStateThroughRefStateInsteadOfReturnValue()
    {
        var source = ReadServiceSource();
        var prefix = ExtractMethod(source, "private static void CheckGarbagePrefix(");

        // 前缀必须声明 ref __state 参数并把状态赋给它，这是 Harmony 传递状态的唯一合法写法。
        Assert.Contains("ref SearchState __state", prefix, StringComparison.Ordinal);
        Assert.Contains("__state = new SearchState(", prefix, StringComparison.Ordinal);
        // 整个文件不得再出现“前缀返回 SearchState”的旧写法（Harmony 会拒装整个补丁）。
        Assert.DoesNotContain("SearchState CheckGarbagePrefix", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGarbagePostfixReadsThePrefixStateThroughTheStateParameter()
    {
        var source = ReadServiceSource();

        // 后缀按 Harmony __state 契约读取前缀状态；改签名时两侧类型必须同步一致。
        Assert.Contains(
            "private static void CheckGarbagePostfix(SearchState __state)",
            source,
            StringComparison.Ordinal
        );
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
