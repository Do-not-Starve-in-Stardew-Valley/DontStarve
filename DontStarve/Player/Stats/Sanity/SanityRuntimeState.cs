#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

internal enum SanityAuthorityRole
{
    None,
    Host,
    Client,
}

internal interface ISanityMaximumProvider
{
    double GetMaximum(string playerKey);
}

/// <summary>
/// 4.0 当前默认角色上限。未来角色系统只替换 provider，不改 player key 或持久化比例契约。
/// </summary>
internal sealed class DefaultSanityMaximumProvider : ISanityMaximumProvider
{
    public double GetMaximum(string playerKey)
    {
        return SanitySaveData.CurrentDefaultMaxSanity;
    }
}

internal sealed class SanityPlayerRuntimeState
{
    internal SanityPlayerRuntimeState(
        string playerKey,
        double current,
        double maximum,
        long revision
    )
    {
        PlayerKey = playerKey;
        Current = current;
        Maximum = maximum;
        Revision = revision;
    }

    internal string PlayerKey { get; }

    internal double Current { get; set; }

    internal double Maximum { get; set; }

    internal long Revision { get; set; }

    internal SanityPlayerSnapshot Snapshot()
    {
        return new SanityPlayerSnapshot
        {
            PlayerKey = PlayerKey,
            Current = Current,
            Maximum = Maximum,
            Revision = Revision,
        };
    }
}

/// <summary>
/// 会话内唯一按玩家状态表。它不读写磁盘、不调用 SMAPI，只消费阶段 02 的已验证 DTO。
/// </summary>
internal sealed class SanityRuntimeStateStore
{
    private readonly Dictionary<string, SanityPlayerRuntimeState> players =
        new(StringComparer.Ordinal);
    private readonly ISanityMaximumProvider maximumProvider;

    private SanitySaveData? persistenceBase;
    private string masterPlayerKey = string.Empty;
    private string localPlayerKey = string.Empty;

    internal SanityRuntimeStateStore(ISanityMaximumProvider maximumProvider)
    {
        this.maximumProvider =
            maximumProvider ?? throw new ArgumentNullException(nameof(maximumProvider));
    }

    internal SanityAuthorityRole Role { get; private set; }

    internal string SessionId { get; private set; } = string.Empty;

    internal bool HasActiveSession => Role != SanityAuthorityRole.None;

    internal bool BeginHostSession(
        string sessionId,
        SanityPersistenceResult persistence,
        string masterKey,
        out string reason
    )
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
        {
            reason = "host-session-id-is-invalid";
            return false;
        }
        if (!SanityPlayerKey.IsCanonical(masterKey))
        {
            reason = "master-player-key-is-not-canonical";
            return false;
        }
        if (persistence is null)
        {
            reason = "persistence-session-is-missing";
            return false;
        }

        Clear();
        Role = SanityAuthorityRole.Host;
        SessionId = sessionId;
        masterPlayerKey = masterKey;
        persistenceBase = persistence.Data;

        if (!TryEnsureHostPlayer(masterKey, out _, out reason))
        {
            Clear();
            return false;
        }

        // ReadOnlyError 没有 v2 Data；此时阶段 02 的安全 Current 仍是唯一可用初值。
        if (persistenceBase is null && players.TryGetValue(masterKey, out var master))
            master.Current = Clamp(persistence.Current, master.Maximum);

        reason = "host-sanity-session-started";
        return true;
    }

    internal bool BeginClientSession(string playerKey, out string reason)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            reason = "client-player-key-is-not-canonical";
            return false;
        }

        Clear();
        Role = SanityAuthorityRole.Client;
        localPlayerKey = playerKey;
        var maximum = ResolveMaximum(playerKey, out _);
        players[playerKey] = new SanityPlayerRuntimeState(
            playerKey,
            maximum,
            maximum,
            0
        );
        reason = "client-sanity-session-awaiting-host-snapshot";
        return true;
    }

    internal void Clear()
    {
        players.Clear();
        persistenceBase = null;
        masterPlayerKey = string.Empty;
        localPlayerKey = string.Empty;
        SessionId = string.Empty;
        Role = SanityAuthorityRole.None;
    }

    internal bool TryEnsureHostPlayer(
        string playerKey,
        out SanityPlayerSnapshot snapshot,
        out string reason
    )
    {
        snapshot = new SanityPlayerSnapshot();
        if (Role != SanityAuthorityRole.Host)
        {
            reason = "host-authority-session-is-not-active";
            return false;
        }
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            reason = "player-key-is-not-canonical-invariant-decimal";
            return false;
        }
        if (players.TryGetValue(playerKey, out var existing))
        {
            snapshot = existing.Snapshot();
            reason = "runtime-player-already-exists";
            return true;
        }

        var maximum = ResolveMaximum(playerKey, out var maximumReason);
        var current = maximum;
        if (
            persistenceBase is not null
            && !SanitySaveDataCodec.TryResolvePlayerCurrent(
                persistenceBase,
                playerKey,
                maximum,
                out current,
                out reason
            )
        )
        {
            return false;
        }

        var state = new SanityPlayerRuntimeState(
            playerKey,
            Clamp(current, maximum),
            maximum,
            0
        );
        players.Add(playerKey, state);
        snapshot = state.Snapshot();
        reason = maximumReason;
        return true;
    }

    internal void UpdatePersistenceBase(SanityPersistenceResult persistence)
    {
        if (Role == SanityAuthorityRole.Host)
            persistenceBase = persistence.Data;
    }

    internal double GetCurrentOrDefault(string playerKey)
    {
        return players.TryGetValue(playerKey, out var state)
            ? state.Current
            : ResolveMaximum(playerKey, out _);
    }

    internal double GetMaximumOrDefault(string playerKey)
    {
        return players.TryGetValue(playerKey, out var state)
            ? state.Maximum
            : ResolveMaximum(playerKey, out _);
    }

    internal bool TryGetSnapshot(string playerKey, out SanityPlayerSnapshot snapshot)
    {
        if (players.TryGetValue(playerKey, out var state))
        {
            snapshot = state.Snapshot();
            return true;
        }

        snapshot = new SanityPlayerSnapshot();
        return false;
    }

    internal bool TrySetHostValue(
        string playerKey,
        double requestedValue,
        out SanityPlayerSnapshot snapshot,
        out bool changed,
        out string reason
    )
    {
        snapshot = new SanityPlayerSnapshot();
        changed = false;
        if (!double.IsFinite(requestedValue))
        {
            reason = "requested-sanity-value-must-be-finite";
            return false;
        }
        if (!TryEnsureHostPlayer(playerKey, out snapshot, out reason))
            return false;

        var state = players[playerKey];
        var clamped = Clamp(requestedValue, state.Maximum);
        if (clamped.Equals(state.Current))
        {
            snapshot = state.Snapshot();
            reason = "sanity-value-is-unchanged";
            return true;
        }
        if (state.Revision == long.MaxValue)
        {
            reason = "sanity-revision-is-exhausted";
            return false;
        }

        state.Current = clamped;
        state.Revision++;
        changed = true;
        snapshot = state.Snapshot();
        reason = "sanity-value-changed";
        return true;
    }

    internal SanitySnapshotMessage CreateFullSnapshot()
    {
        var message = new SanitySnapshotMessage
        {
            SessionId = SessionId,
            IsFull = true,
        };
        var keys = new List<string>(players.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
            message.Players.Add(players[key].Snapshot());
        return message;
    }

    internal SanitySnapshotMessage CreateDeltaSnapshot(SanityPlayerSnapshot snapshot)
    {
        return new SanitySnapshotMessage
        {
            SessionId = SessionId,
            IsFull = false,
            Players = new List<SanityPlayerSnapshot> { snapshot.Clone() },
        };
    }

    internal SanitySnapshotApplyResult ApplyClientSnapshot(
        SanitySnapshotMessage message
    )
    {
        if (Role != SanityAuthorityRole.Client)
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.Rejected,
                "client-authority-session-is-not-active"
            );
        }
        if (message is null || !SanityProtocol.IsValidSessionId(message.SessionId))
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.Rejected,
                "snapshot-session-id-is-invalid"
            );
        }
        if (
            message.Players is null
            || message.Players.Count == 0
            || !SanityProtocol.HasUniquePlayerKeys(message.Players)
        )
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.Rejected,
                "snapshot-player-set-is-empty-or-duplicated"
            );
        }
        foreach (var snapshot in message.Players)
        {
            if (!SanityProtocol.IsValidSnapshot(snapshot))
            {
                return new SanitySnapshotApplyResult(
                    SanitySnapshotApplyStatus.Rejected,
                    "snapshot-player-state-is-invalid"
                );
            }
        }

        if (message.IsFull)
            return ApplyFullSnapshot(message);

        if (!string.Equals(SessionId, message.SessionId, StringComparison.Ordinal))
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.NeedsFullSnapshot,
                "delta-snapshot-session-does-not-match"
            );
        }
        if (message.Players.Count != 1)
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.Rejected,
                "delta-snapshot-must-contain-one-player"
            );
        }

        var incoming = message.Players[0];
        if (!players.TryGetValue(incoming.PlayerKey, out var current))
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.NeedsFullSnapshot,
                "delta-snapshot-player-is-unknown"
            );
        }
        if (incoming.Revision < current.Revision)
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.IgnoredStaleOrDuplicate,
                "delta-snapshot-is-stale"
            );
        }
        if (incoming.Revision == current.Revision)
        {
            var identical = incoming.Current.Equals(current.Current)
                && incoming.Maximum.Equals(current.Maximum);
            return new SanitySnapshotApplyResult(
                identical
                    ? SanitySnapshotApplyStatus.IgnoredStaleOrDuplicate
                    : SanitySnapshotApplyStatus.NeedsFullSnapshot,
                identical
                    ? "delta-snapshot-is-an-identical-duplicate"
                    : "delta-snapshot-revision-conflicts-with-current-state"
            );
        }
        if (current.Revision == long.MaxValue || incoming.Revision != current.Revision + 1)
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.NeedsFullSnapshot,
                "delta-snapshot-revision-has-a-gap"
            );
        }

        players[incoming.PlayerKey] = FromSnapshot(incoming);
        return new SanitySnapshotApplyResult(
            SanitySnapshotApplyStatus.AppliedDelta,
            "delta-snapshot-applied"
        );
    }

    internal IReadOnlyList<SanityPlayerSaveInput> CaptureSaveInputs()
    {
        var values = new List<SanityPlayerSaveInput>(players.Count);
        if (Role != SanityAuthorityRole.Host)
            return values;

        var keys = new List<string>(players.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var player = players[key];
            values.Add(
                new SanityPlayerSaveInput(
                    player.PlayerKey,
                    player.Current,
                    player.Maximum
                )
            );
        }

        return values;
    }

    private SanitySnapshotApplyResult ApplyFullSnapshot(
        SanitySnapshotMessage message
    )
    {
        if (
            !string.IsNullOrEmpty(SessionId)
            && !string.Equals(SessionId, message.SessionId, StringComparison.Ordinal)
        )
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.Rejected,
                "full-snapshot-session-does-not-match"
            );
        }

        var containsLocalPlayer = false;
        foreach (var snapshot in message.Players)
        {
            if (string.Equals(snapshot.PlayerKey, localPlayerKey, StringComparison.Ordinal))
            {
                containsLocalPlayer = true;
                break;
            }
        }
        if (!containsLocalPlayer)
        {
            return new SanitySnapshotApplyResult(
                SanitySnapshotApplyStatus.NeedsFullSnapshot,
                "full-snapshot-does-not-contain-local-player"
            );
        }

        if (!string.IsNullOrEmpty(SessionId))
        {
            foreach (var snapshot in message.Players)
            {
                if (!players.TryGetValue(snapshot.PlayerKey, out var current))
                    continue;
                if (snapshot.Revision < current.Revision)
                {
                    return new SanitySnapshotApplyResult(
                        SanitySnapshotApplyStatus.IgnoredStaleOrDuplicate,
                        "full-snapshot-is-stale"
                    );
                }
                if (
                    snapshot.Revision == current.Revision
                    && (
                        !snapshot.Current.Equals(current.Current)
                        || !snapshot.Maximum.Equals(current.Maximum)
                    )
                )
                {
                    return new SanitySnapshotApplyResult(
                        SanitySnapshotApplyStatus.NeedsFullSnapshot,
                        "full-snapshot-revision-conflicts-with-current-state"
                    );
                }
            }
        }

        players.Clear();
        foreach (var snapshot in message.Players)
            players.Add(snapshot.PlayerKey, FromSnapshot(snapshot));
        SessionId = message.SessionId;
        return new SanitySnapshotApplyResult(
            SanitySnapshotApplyStatus.AppliedFull,
            "full-snapshot-applied"
        );
    }

    private double ResolveMaximum(string playerKey, out string reason)
    {
        if (string.Equals(playerKey, masterPlayerKey, StringComparison.Ordinal))
        {
            reason = "master-player-uses-4.0-default-maximum";
            return SanitySaveData.CurrentDefaultMaxSanity;
        }

        try
        {
            var maximum = maximumProvider.GetMaximum(playerKey);
            if (double.IsFinite(maximum) && maximum > 0)
            {
                reason = "player-maximum-resolved-by-provider";
                return maximum;
            }
        }
        catch (Exception)
        {
            // Provider 是可替换边界；异常与无效值都必须回退到同一个安全默认值。
        }

        reason = "player-maximum-provider-unavailable-use-safe-default";
        return SanitySaveData.CurrentDefaultMaxSanity;
    }

    private static SanityPlayerRuntimeState FromSnapshot(
        SanityPlayerSnapshot snapshot
    )
    {
        return new SanityPlayerRuntimeState(
            snapshot.PlayerKey,
            snapshot.Current,
            snapshot.Maximum,
            snapshot.Revision
        );
    }

    private static double Clamp(double value, double maximum)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0;
        if (value >= maximum)
            return maximum;
        return value;
    }
}
