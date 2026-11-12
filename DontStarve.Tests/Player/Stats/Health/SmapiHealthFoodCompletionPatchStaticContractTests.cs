using System;
using System.IO;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Health;

/// <summary>
/// 静态契约测试（参照 SmapiVanillaKillRingEffectsStaticContractTests 的源码字符串风格）：
/// 锁定进食补丁的安装目标与转发边界，以及 Health 宿主的主机结算门与存档通道。
/// 契约被破坏时这里先红，避免重演接缝漂移。
/// </summary>
public sealed class SmapiHealthFoodCompletionPatchStaticContractTests
{
    private const string PatchRelativePath =
        "DontStarve/Player/Stats/Health/SmapiHealthFoodCompletionPatch.cs";

    private const string HealthRelativePath =
        "DontStarve/Player/Stats/Health/Health.cs";

    [Fact]
    public void Patch_targets_vanilla_doneEating_with_empty_argument_types()
    {
        var source = ReadSource(PatchRelativePath);

        // 安装目标必须锁死在 Farmer.doneEating() 无参重载（镜像 Hunger 接缝）。
        Assert.Contains("AccessTools.DeclaredMethod(", source, StringComparison.Ordinal);
        Assert.Contains("typeof(Farmer)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(Farmer.doneEating)", source, StringComparison.Ordinal);
        Assert.Contains("Type.EmptyTypes", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_only_snapshots_and_forwards_it_never_mutates_health_or_logic()
    {
        var source = ReadSource(PatchRelativePath);

        Assert.Contains("__instance.health - __state", source, StringComparison.Ordinal);
        Assert.Contains("__instance.itemToEat", source, StringComparison.Ordinal);
        // 补丁类不写业务逻辑：不写血、不调死亡/战斗链路、不直接抓全局玩家。
        Assert.DoesNotContain(".health =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.player", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_settlement_happens_only_on_the_host()
    {
        var source = ReadSource(HealthRelativePath);

        // 决议 6 四轮定稿：结算只发生在主机。进食门 = IsMainPlayer + GetPlayer(onlyOnline)
        // 在线校验（镜像 HungerFoodCompletionPatch 形态）；分钟结算 = 主机门（SurvivalStateTimeOwner
        // 逐在线玩家派发，不再只认 Game1.player）；旧的"完全本地"门已废除。
        Assert.Contains("ShouldHostSettleFood(", source, StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(Game1.GetPlayer(who.UniqueMultiplayerID, onlyOnline: true), who)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("ShouldHostSettleMinute(", source, StringComparison.Ordinal);
        // 旧的"完全本地"结算门（!ReferenceEquals → 直接返回）必须不存在；
        // SettleEvent 内保留的 isSelf 分流（主机自己走本地路径 vs 农场工走下达）是决议 6 的落点分流。
        Assert.DoesNotContain("!ReferenceEquals(who, Game1.player)", source, StringComparison.Ordinal);
        Assert.Contains(
            "isSelf = ReferenceEquals(who, Game1.player)",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Health_persistence_uses_the_save_level_drawer_not_mod_data()
    {
        var source = ReadSource(HealthRelativePath);

        // 决议 6：余数持久化走 save 级抽屉 DontStarve.Health（主机读写），modData 版 store
        // 已删除；旧 modData 余数只在 SaveLoaded/PeerConnected 一次性迁移并入权威字典。
        Assert.Contains("persistenceStore.Load", source, StringComparison.Ordinal);
        Assert.Contains("persistenceStore.Save", source, StringComparison.Ordinal);
        Assert.Contains("SmapiHealthSaveDataAccess", source, StringComparison.Ordinal);
        Assert.Contains("MigrateLegacyModDataRemainder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("player.modData[", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_death_path_never_invokes_a_death_function()
    {
        var source = ReadSource(HealthRelativePath);

        // 决议 5：吃死/穿死只写 0 血 + 清 0 余数，交回原版死亡链路；
        // 不新增死亡入口，不碰 PlayerDeathCauseLedger。
        Assert.DoesNotContain("takeDamage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerDeathCauseLedger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("passOutFromTired", source, StringComparison.Ordinal);
        Assert.Contains("交回原版死亡链路", source, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate the production source: {relativePath}"
        );
    }
}
