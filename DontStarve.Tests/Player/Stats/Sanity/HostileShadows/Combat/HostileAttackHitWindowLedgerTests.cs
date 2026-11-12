using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

/// <summary>
/// 冻结决议 5 的账本语义：命中身份 = (攻击实例, 命中窗口, 玩家)。同一窗口内每个玩家只结算
/// 一次；不同窗口（连击第二记红框）允许再次命中同一玩家；nonce 仍按整个攻击实例去重。
/// </summary>
public sealed class HostileAttackHitWindowLedgerTests
{
    private static HostileAttackInstance Instance()
    {
        return new HostileAttackInstance(
            "attack",
            HostileAttackTestFactory.EntityId,
            HostileAttackTestFactory.AttackRevision,
            HostileAttackTestFactory.LocationId,
            HostileAttackTestFactory.PlayerOne,
            originPositionX: 0d,
            originPositionY: 0d,
            directionX: 1d,
            directionY: 0d,
            HostileAttackFacing.Down
        );
    }

    [Fact]
    public void Same_window_cannot_settle_the_same_player_twice()
    {
        var instance = Instance();

        Assert.True(
            instance.TryClaimHit(
                "first-1",
                HostileAttackTestFactory.PlayerOne,
                101,
                "first",
                out _
            )
        );
        Assert.False(
            instance.TryClaimHit(
                "first-2",
                HostileAttackTestFactory.PlayerOne,
                101,
                "first",
                out var reason
            )
        );
        Assert.Equal("hostile-shadow.attack-hit-player-already-settled", reason);
    }

    [Fact]
    public void Different_windows_of_one_instance_may_hit_the_same_player_again()
    {
        var instance = Instance();

        Assert.True(
            instance.TryClaimHit(
                "first-1",
                HostileAttackTestFactory.PlayerOne,
                101,
                "first",
                out _
            )
        );
        Assert.True(
            instance.TryClaimHit(
                "follow-up-1",
                HostileAttackTestFactory.PlayerOne,
                101,
                "follow-up",
                out _
            )
        );
        Assert.Equal(2, instance.HitPlayerCount);
    }

    [Fact]
    public void Settled_check_is_window_scoped_not_instance_wide()
    {
        var instance = Instance();

        instance.TryClaimHit(
            "first-1",
            HostileAttackTestFactory.PlayerOne,
            101,
            "first",
            out _
        );
        Assert.True(
            instance.HasSettledPlayerInWindow("first", 101)
        );
        Assert.False(
            instance.HasSettledPlayerInWindow("follow-up", 101)
        );
        // 零是“无玩家”哨兵，永远不算已结算。
        Assert.False(
            instance.HasSettledPlayerInWindow("first", 0)
        );
    }

    [Fact]
    public void Nonce_stays_unique_across_windows()
    {
        var instance = Instance();

        Assert.True(
            instance.TryClaimHit(
                "shared",
                HostileAttackTestFactory.PlayerOne,
                101,
                "first",
                out _
            )
        );
        Assert.False(
            instance.TryClaimHit(
                "shared",
                HostileAttackTestFactory.PlayerOne,
                101,
                "follow-up",
                out var reason
            )
        );
        Assert.Equal("hostile-shadow.attack-hit-nonce-replayed", reason);
    }

    [Fact]
    public void Window_id_is_required_bounded_and_player_key_still_guarded()
    {
        var instance = Instance();

        Assert.False(
            instance.TryClaimHit("n1", HostileAttackTestFactory.PlayerOne, 101, "", out var emptyWindow)
        );
        Assert.Equal("hostile-shadow.attack-hit-ledger-input-invalid", emptyWindow);
        Assert.False(
            instance.TryClaimHit("n1", HostileAttackTestFactory.PlayerOne, 101, new string('a', 257), out var longWindow)
        );
        Assert.Equal("hostile-shadow.attack-hit-ledger-input-invalid", longWindow);
        Assert.False(
            instance.TryClaimHit("n1", "", 101, "first", out var emptyPlayer)
        );
        Assert.Equal("hostile-shadow.attack-hit-ledger-input-invalid", emptyPlayer);
        // 负数玩家 ID 合法（仓库既定口径），按 (窗口, ID) 记账。
        Assert.True(
            instance.TryClaimHit("n2", "-101", -101, "first", out _)
        );
        Assert.True(instance.HasSettledPlayerInWindow("first", -101));
    }
}
