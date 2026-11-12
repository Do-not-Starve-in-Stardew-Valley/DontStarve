using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileAttackNormalPolicyStaticContractTests
{
    [Fact]
    public void Current_shadow_adapter_uses_shared_damage_service_with_normal_policy()
    {
        var adapter = Contract("SmapiHostileAttackDamageAdapter.cs");
        var damage = Contract("HostileAttackDamage.cs");

        Assert.Contains(
            "SmapiInvincibilityFrameDamageService",
            adapter,
            StringComparison.Ordinal
        );
        Assert.Contains("InvincibilityFramePolicy.Normal", damage, StringComparison.Ordinal);
        Assert.Contains(
            "ApplyOrdinaryDamage(",
            damage,
            StringComparison.Ordinal
        );
        Assert.Contains("InvincibilityFramePolicy policy", damage, StringComparison.Ordinal);
    }

    [Fact]
    public void Combat_service_resolves_policy_from_hit_window_not_from_hardcoded_literal()
    {
        // 冻结决议 6：策略唯一来源是当前帧所属命中窗口的资源声明。战斗服务里不得再出现
        // 硬编码 Normal/Strong/Weak 字面量；窗口解析失败必须 fail-closed 拒绝。
        var combat = Contract("SmapiHostileAttackCombatService.cs");

        Assert.Contains(
            "TryGetWindowForFrame",
            combat,
            StringComparison.Ordinal
        );
        Assert.Contains("HitWindowId = hitWindow.WindowId", combat, StringComparison.Ordinal);
        Assert.Contains(
            "hostile-shadow.attack-hit-window-missing",
            combat,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("InvincibilityFramePolicy.Normal", combat, StringComparison.Ordinal);
        Assert.DoesNotContain("InvincibilityFramePolicy.Strong", combat, StringComparison.Ordinal);
        Assert.DoesNotContain("InvincibilityFramePolicy.Weak", combat, StringComparison.Ordinal);
    }

    private static string Contract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                fileName
            )
        );
    }
}
