#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Minigames;

/// <summary>
/// Host-authored, target-private notification that a remote Farmer actually lost health to a
/// Monster while the host was resolving damage. The client still verifies its local BobberBar;
/// this message never carries a fishing result or a command to grant/revoke items.
/// </summary>
internal sealed class MinigameFishingInterruptionMessage
{
    public int ProtocolVersion { get; set; } = MinigameFishingInterruptionProtocol.ProtocolVersion;

    public string SessionId { get; set; } = string.Empty;

    public long EventId { get; set; }

    public long TargetPlayerId { get; set; }
}

internal static class MinigameFishingInterruptionProtocol
{
    internal const int ProtocolVersion = 1;

    private const int MaximumSessionIdLength = 128;

    internal static bool IsValid(
        MinigameFishingInterruptionMessage? message,
        string expectedSessionId,
        long expectedTargetPlayerId,
        out string reason
    )
    {
        if (message is null)
        {
            reason = "fishing-interruption.message-null";
            return false;
        }

        if (message.ProtocolVersion != ProtocolVersion)
        {
            reason = "fishing-interruption.protocol-version-invalid";
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(message.SessionId)
            || message.SessionId.Length > MaximumSessionIdLength
            || !string.Equals(message.SessionId, expectedSessionId, StringComparison.Ordinal)
        )
        {
            reason = "fishing-interruption.session-invalid";
            return false;
        }

        if (message.EventId <= 0)
        {
            reason = "fishing-interruption.event-id-invalid";
            return false;
        }

        if (
            expectedTargetPlayerId == 0
            || message.TargetPlayerId != expectedTargetPlayerId
        )
        {
            reason = "fishing-interruption.target-invalid";
            return false;
        }

        reason = "fishing-interruption.valid";
        return true;
    }
}
