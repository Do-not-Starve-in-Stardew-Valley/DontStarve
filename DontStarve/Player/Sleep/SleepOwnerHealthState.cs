#nullable enable

using System;

namespace DontStarve.Player.Sleep;

/// <summary>Owner-local HP delivery. A delayed night result must survive native full recovery,
/// but a late join must never reset today's HP from an old completed-night snapshot.</summary>
internal sealed class SleepOwnerHealthState
{
    private readonly object gate = new();
    private string expectedSettlement = string.Empty;
    private long revision;
    private SleepPlayerSnapshot? pending;
    private bool nativeRecoveryComplete;

    internal void Begin(string settlementId)
    {
        lock (gate)
        {
            if (!SleepLifecycleProtocol.IsValidText(settlementId))
            {
                expectedSettlement = string.Empty;
                revision = 0;
                pending = null;
                nativeRecoveryComplete = false;
                return;
            }
            expectedSettlement = settlementId;
            revision = 0;
            pending = null;
            nativeRecoveryComplete = false;
        }
    }

    internal bool TryAccept(
        SleepPlayerSnapshot snapshot,
        string ownerKey,
        out int health,
        out bool hungerDeath
    )
    {
        lock (gate)
        {
            health = 0;
            hungerDeath = false;
            if (!snapshot.HasHealth || snapshot.Health < 0 || snapshot.Health > snapshot.MaximumHealth
                || snapshot.MaximumHealth <= 0 || snapshot.Revision <= revision
                || !string.Equals(snapshot.PlayerKey, ownerKey, StringComparison.Ordinal)
                || !string.Equals(snapshot.SettlementId, expectedSettlement, StringComparison.Ordinal))
                return false;
            revision = snapshot.Revision;
            if (snapshot.Phase == SleepPhase.Completed && !nativeRecoveryComplete)
            {
                pending = snapshot.Clone();
                return false;
            }
            health = snapshot.Health;
            // Older peers omitted this optional bit and used zero HP as the only death evidence.
            hungerDeath = snapshot.HungerDeath ?? (snapshot.Health <= 0);
            return true;
        }
    }

    internal bool CompleteNativeRecovery(out int health, out bool hungerDeath)
    {
        lock (gate)
        {
            nativeRecoveryComplete = true;
            health = pending?.Health ?? 0;
            hungerDeath = pending is not null
                && (pending.HungerDeath ?? (pending.Health <= 0));
            if (pending is null)
                return false;
            pending = null;
            return true;
        }
    }

    internal void Clear() => Begin(string.Empty);
}
