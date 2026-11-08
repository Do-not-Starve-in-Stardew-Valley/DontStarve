using System;
using DontStarve.Player.Death;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepDeathCauseTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    [InlineData(10, 1, false)]
    public void Sleep_death_is_attributed_to_hunger_only_when_hunger_damage_reaches_zero(
        int finalHealth,
        int hungerDamage,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            PlayerDeathCausePolicy.ShouldMarkHungerDeath(finalHealth, hungerDamage)
        );
    }

    [Theory]
    [InlineData(false, 10, true)]
    [InlineData(true, 10, false)]
    [InlineData(false, 0, false)]
    public void Pending_death_cause_is_cleared_only_after_death_flow_ends_or_player_recovers(
        bool killScreenActive,
        int health,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            PlayerDeathCausePolicy.ShouldForgetPendingCause(killScreenActive, health)
        );
    }

    [Fact]
    public void Sleep_snapshot_carries_hunger_death_cause_and_rejects_a_positive_health_claim()
    {
        var snapshot = Snapshot();
        snapshot.HungerDeath = true;
        var clone = snapshot.Clone();

        Assert.True(clone.HungerDeath == true);
        Assert.True(SleepLifecycleProtocol.IsValidSnapshot(clone));

        clone.Health = 1;
        Assert.False(SleepLifecycleProtocol.IsValidSnapshot(clone));
    }

    [Fact]
    public void Deferred_owner_health_keeps_the_hunger_death_cause_until_native_recovery_finishes()
    {
        var snapshot = Snapshot();
        snapshot.HungerDeath = true;

        var owner = new SleepOwnerHealthState();
        owner.Begin(snapshot.SettlementId);
        Assert.False(owner.TryAccept(snapshot, snapshot.PlayerKey, out _, out _));
        Assert.True(owner.CompleteNativeRecovery(out var health, out var hungerDeath));
        Assert.Equal(0, health);
        Assert.True(hungerDeath);
    }

    [Fact]
    public void Snapshot_without_cause_metadata_preserves_older_peer_zero_health_behavior()
    {
        var snapshot = Snapshot();
        Assert.Null(snapshot.HungerDeath);

        var owner = new SleepOwnerHealthState();
        owner.Begin(snapshot.SettlementId);
        Assert.False(owner.TryAccept(snapshot, snapshot.PlayerKey, out _, out _));
        Assert.True(owner.CompleteNativeRecovery(out var health, out var hungerDeath));

        Assert.Equal(0, health);
        Assert.True(hungerDeath);
    }

    [Fact]
    public void Explicit_non_hunger_zero_health_snapshot_does_not_mark_hunger_death()
    {
        var snapshot = Snapshot();
        snapshot.HungerDeath = false;

        var owner = new SleepOwnerHealthState();
        owner.Begin(snapshot.SettlementId);
        Assert.False(owner.TryAccept(snapshot, snapshot.PlayerKey, out _, out _));
        Assert.True(owner.CompleteNativeRecovery(out var health, out var hungerDeath));

        Assert.Equal(0, health);
        Assert.False(hungerDeath);
    }

    private static SleepPlayerSnapshot Snapshot() =>
        new()
        {
            HasHealth = true,
            Health = 0,
            MaximumHealth = 100,
            SessionId = "00112233445566778899aabbccddeeff",
            PlayerKey = "42",
            Mode = SleepMode.Voluntary,
            Phase = SleepPhase.Completed,
            BedId = "bed",
            SettlementId = "sleep-settlement",
            CurrentMinute = 600,
            TargetMinute = 960,
            Revision = 1,
        };
}
