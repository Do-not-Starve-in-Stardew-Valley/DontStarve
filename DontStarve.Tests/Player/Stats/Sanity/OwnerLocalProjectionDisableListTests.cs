using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

/// <summary>
/// 占位素材停用门（2026-11-10 设计师盘点）回归测试：四种 owner-local 幻觉生物
/// （eyes/dark-watcher/dark-hand/mr-skitts）在素材定稿前必须保持停用；
/// 恢复时从 OwnerLocalProjectionDisableList 删除对应物种 ID，并同步更新本测试与计划文档。
/// </summary>
public sealed class OwnerLocalProjectionDisableListTests
{
    private static string ModEntrySourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "ShadowProjection",
            "ModEntry.cs"
        );

    [Fact]
    public void DisableListCoversExactlyTheFourPlaceholderSpecies()
    {
        var expected = new[]
        {
            EyesProjectionContract.SpeciesId,
            DarkWatcherProjectionContract.SpeciesId,
            DarkHandProjectionContract.SpeciesId,
            MrSkittsProjectionContract.SpeciesId,
        };

        Assert.Equal(4, OwnerLocalProjectionDisableList.DisabledSpeciesIds.Count);
        Assert.All(
            expected,
            speciesId => Assert.True(
                OwnerLocalProjectionDisableList.IsDisabled(speciesId),
                speciesId
            )
        );
    }

    [Fact]
    public void DangerTierShadowSpeciesAndUnknownIdsAreNotAffected()
    {
        // creeper-fear/terrorbeak 走 RegisterShadowCreatureSpecies 独立 seam，
        // 不在本清单管辖范围；未知物种同样不受影响。
        Assert.False(OwnerLocalProjectionDisableList.IsDisabled("sanity.projection.creeper-fear"));
        Assert.False(OwnerLocalProjectionDisableList.IsDisabled("sanity.projection.terrorbeak"));
        Assert.False(OwnerLocalProjectionDisableList.IsDisabled("sanity.projection.unknown"));
        Assert.False(OwnerLocalProjectionDisableList.IsDisabled(string.Empty));
    }

    [Fact]
    public void DisabledReasonStaysStableForLogsAndFailClosedReasons()
    {
        Assert.Equal(
            "projection.species-disabled-pending-placeholder-assets",
            OwnerLocalProjectionDisableList.DisabledReason
        );
    }

    [Fact]
    public void ModEntryKeepsConsultingTheDisableListBeforeRegisteringSpecies()
    {
        // 静态事实 probe：ModEntry 的普通物种注册必须继续经过停用清单，
        // 防止恢复/重构时把四种占位物种绕过清单直接注册回去。
        var modEntrySource = File.ReadAllText(ModEntrySourcePath);

        Assert.Contains(
            "OwnerLocalProjectionDisableList.IsDisabled(",
            modEntrySource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "MrSkittsProjectionContract.SpeciesId",
            modEntrySource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "DarkHandProjectionContract.SpeciesId",
            modEntrySource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "DarkWatcherProjectionContract.SpeciesId",
            modEntrySource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "EyesProjectionContract.SpeciesId",
            modEntrySource,
            StringComparison.Ordinal
        );
    }
}
