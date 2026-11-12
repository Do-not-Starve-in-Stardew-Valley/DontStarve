using Xunit;

namespace DontStarve.Tests.Player.Stats.Food;

public sealed class SeedEdibilityRuntimeContractTests
{
    [Fact]
    public void DisposeRemovesBothGetterPatchesOwnedByTheRuntime()
    {
        var source = ReadContract("SeedEdibility", "SeedEdibilityRuntime.cs");
        var dispose = ExtractBlock(source, "internal static void Dispose()");

        Assert.Contains(
            "harmony.Unpatch(patchedGetter, HarmonyPatchType.Prefix, harmony.Id)",
            dispose,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "harmony.Unpatch(patchedGetter, HarmonyPatchType.Postfix, harmony.Id)",
            dispose,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("UnpatchAll", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void XnaShadowCreatureInstanceForwardsLoopingToTheNativeInstance()
    {
        // 2026-11-13 嵌套实例类随 ShadowCreatureSmapiSfxService 整类搬出 ModEntry.cs，快照跟随新文件。
        var source = ReadContract("ShadowProjection", "ShadowCreatureSmapiSfxService.cs");
        var adapter = ExtractBlock(source, "private sealed class XnaShadowCreatureSfxInstance");

        Assert.Contains("get => instance.LoopCount != 0", adapter, StringComparison.Ordinal);
        Assert.Contains("set => instance.LoopCount = value ? 255u : 0u", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("private bool isLooped", adapter, StringComparison.Ordinal);
    }

    private static string ReadContract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }

    private static string ExtractBlock(string source, string marker)
    {
        var markerStart = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerStart >= 0, $"Missing source marker: {marker}");

        var openingBrace = source.IndexOf('{', markerStart);
        Assert.True(openingBrace >= 0, $"Missing opening brace after: {marker}");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source.Substring(openingBrace, index - openingBrace + 1);
        }

        throw new InvalidOperationException($"Missing closing brace after: {marker}");
    }
}
