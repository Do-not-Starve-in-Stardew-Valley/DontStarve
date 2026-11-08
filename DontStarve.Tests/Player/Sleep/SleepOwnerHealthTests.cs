using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepOwnerHealthTests
{
    [Fact]
    public void CompletedHostHealthWaitsForNativeRecoveryAndAppliesOnce()
    {
        var mirror = new SleepOwnerHealthState();
        mirror.Begin("night-1");
        var snapshot = Value(26, 1);
        Assert.False(mirror.TryAccept(snapshot, "42", out _, out _));
        Assert.True(mirror.CompleteNativeRecovery(out var health, out var hungerDeath));
        Assert.Equal(26, health);
        Assert.False(hungerDeath);
        Assert.False(mirror.CompleteNativeRecovery(out _, out _));
        Assert.False(mirror.TryAccept(snapshot, "42", out _, out _));
    }

    [Fact]
    public void RealtimeHealthIsImmediateAndRejectsOtherOwnerAndOldAttempt()
    {
        var mirror = new SleepOwnerHealthState();
        mirror.Begin("night-1");
        var snapshot = Value(21, 1);
        snapshot.Phase = SleepPhase.Realtime;
        Assert.False(mirror.TryAccept(snapshot, "7", out _, out _));
        snapshot.SettlementId = "old-night";
        Assert.False(mirror.TryAccept(snapshot, "42", out _, out _));
        snapshot.SettlementId = "night-1";
        Assert.True(mirror.TryAccept(snapshot, "42", out var health, out var hungerDeath));
        Assert.Equal(21, health);
        Assert.False(hungerDeath);
    }

    [Fact]
    public void ANewlyJoinedOwnerDoesNotReplayAnOldMorningHealthSnapshot()
    {
        var mirror = new SleepOwnerHealthState();
        Assert.False(mirror.TryAccept(Value(26, 9), "42", out _, out _));
    }

    [Fact]
    public void CompletedNativeRecoveryAllowsFollowingSnapshotImmediately()
    {
        var mirror = new SleepOwnerHealthState();
        mirror.Begin("night-1");
        Assert.False(mirror.CompleteNativeRecovery(out _, out _));
        Assert.True(mirror.TryAccept(Value(26, 1), "42", out var health, out _));
        Assert.Equal(26, health);
    }

    private static SleepPlayerSnapshot Value(int health, long revision) => new()
    {
        PlayerKey = "42", SettlementId = "night-1", Health = health,
        MaximumHealth = 100, HasHealth = true, Revision = revision,
        Phase = SleepPhase.Completed,
    };
}
