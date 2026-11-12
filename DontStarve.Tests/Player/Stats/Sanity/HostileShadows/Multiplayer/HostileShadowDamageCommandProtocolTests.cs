using DontStarve.Player.Combat.InvincibilityFrames;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Multiplayer;

public sealed class HostileShadowDamageCommandProtocolTests
{
    [Fact]
    public void Host_damage_path_dispatches_remote_targets_and_keeps_execution_local()
    {
        var combat = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                "SmapiHostileAttackCombatService.cs"
            )
        );
        var world = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                "SmapiHostileShadowWorldRuntime.cs"
            )
        );

        Assert.Contains("farmer.IsLocalPlayer", combat, StringComparison.Ordinal);
        Assert.Contains("SendAttackDamageCommand", combat, StringComparison.Ordinal);
        Assert.Contains("HandleAttackDamageCommand", world, StringComparison.Ordinal);
        Assert.Contains("damageService.Apply", world, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_host_damage_command_is_accepted_for_its_target_and_session()
    {
        var command = new ShadowAttackDamageCommand
        {
            SessionId = "11111111111111111111111111111111",
            Nonce = "entity:attack:target:1",
            EntityId = 10,
            TargetPlayerKey = "101",
            LocationId = "Farm",
            AttackInstanceId = "attack",
            ObservedEntityRevision = 4,
            ObservedAttackInstanceRevision = 2,
            ObservedFrameNumber = 3,
            Damage = 50,
            PolicyMode = 0,
            PolicyDurationMilliseconds = 0,
        };

        Assert.True(
            HostileShadowProtocol.IsValidAttackDamageCommand(
                command,
                "101",
                "11111111111111111111111111111111",
                out var reason
            ),
            reason
        );
    }

    [Theory]
    [InlineData("102", "11111111111111111111111111111111", "hostile-shadow.damage-command-target-invalid")]
    [InlineData("101", "22222222222222222222222222222222", "hostile-shadow.damage-command-session-invalid")]
    public void Damage_command_rejects_wrong_target_or_session(
        string expectedTarget,
        string expectedSession,
        string expectedReason
    )
    {
        var command = new ShadowAttackDamageCommand
        {
            SessionId = "11111111111111111111111111111111",
            Nonce = "nonce",
            EntityId = 10,
            TargetPlayerKey = "101",
            LocationId = "Farm",
            AttackInstanceId = "attack",
            ObservedEntityRevision = 4,
            ObservedAttackInstanceRevision = 2,
            ObservedFrameNumber = 3,
            Damage = 50,
            PolicyMode = 0,
            PolicyDurationMilliseconds = 0,
        };

        Assert.False(
            HostileShadowProtocol.IsValidAttackDamageCommand(
                command,
                expectedTarget,
                expectedSession,
                out var reason
            )
        );
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Hit_window_id_is_accepted_when_bounded_and_rejected_when_not()
    {
        var command = ValidCommand();
        command.HitWindowId = "follow-up";
        Assert.True(
            HostileShadowProtocol.IsValidAttackDamageCommand(
                command,
                "101",
                "11111111111111111111111111111111",
                out var acceptedReason
            ),
            acceptedReason
        );

        command.HitWindowId = new string('a', 257);
        Assert.False(
            HostileShadowProtocol.IsValidAttackDamageCommand(
                command,
                "101",
                "11111111111111111111111111111111",
                out _
            )
        );
    }

    [Fact]
    public void Legacy_peers_without_hit_window_id_stay_compatible()
    {
        // 混合版本边界：旧 peer 的命令没有窗口字段（空串/未序列化），校验必须放行，
        // 由本地策略值照常执行；客户端执行不依赖窗口 ID。
        var command = ValidCommand();
        command.HitWindowId = string.Empty;
        Assert.True(
            HostileShadowProtocol.IsValidAttackDamageCommand(
                command,
                "101",
                "11111111111111111111111111111111",
                out var commandReason
            ),
            commandReason
        );

        var result = new ShadowAttackDamageResult
        {
            SessionId = "11111111111111111111111111111111",
            Nonce = "nonce",
            EntityId = 10,
            TargetPlayerKey = "101",
            LocationId = "Farm",
            AttackInstanceId = "attack",
            ObservedEntityRevision = 4,
            ObservedAttackInstanceRevision = 2,
            ObservedFrameNumber = 3,
            Outcome = InvincibilityFrameDamageOutcome.Applied,
            HealthBefore = 100,
            HealthAfter = 80,
            DurationMilliseconds = 0,
            Reason = "hostile-shadow.attack-hit-applied",
            HitWindowId = string.Empty,
        };
        Assert.True(
            HostileShadowProtocol.IsValidAttackDamageResult(
                result,
                "101",
                "11111111111111111111111111111111",
                out var resultReason
            ),
            resultReason
        );

        result.HitWindowId = new string('a', 257);
        Assert.False(
            HostileShadowProtocol.IsValidAttackDamageResult(
                result,
                "101",
                "11111111111111111111111111111111",
                out _
            )
        );
    }

    private static ShadowAttackDamageCommand ValidCommand()
    {
        return new ShadowAttackDamageCommand
        {
            SessionId = "11111111111111111111111111111111",
            Nonce = "nonce",
            EntityId = 10,
            TargetPlayerKey = "101",
            LocationId = "Farm",
            AttackInstanceId = "attack",
            ObservedEntityRevision = 4,
            ObservedAttackInstanceRevision = 2,
            ObservedFrameNumber = 3,
            Damage = 50,
            PolicyMode = 0,
            PolicyDurationMilliseconds = 0,
        };
    }
}
