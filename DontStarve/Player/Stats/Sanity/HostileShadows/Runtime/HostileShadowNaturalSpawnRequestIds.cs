#nullable enable

using System;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Issues unique natural-refresh request IDs for one host session.
/// The ID must not depend on the game clock, because CJB and similar tools can move that clock
/// backwards while the authority's duplicate-request receipts remain valid.
/// </summary>
internal sealed class HostileShadowNaturalSpawnRequestIds
{
    private long nextNonce;

    internal bool TryNext(
        string sessionId,
        string playerKey,
        out string requestId
    )
    {
        requestId = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(playerKey))
            return false;
        if (nextNonce == long.MaxValue)
            return false;

        var nonce = ++nextNonce;
        requestId = string.Concat(
            "hostile-shadow.interval.",
            sessionId,
            ".",
            playerKey,
            ".nonce.",
            nonce.ToString(CultureInfo.InvariantCulture)
        );
        return true;
    }

    internal void Reset()
    {
        nextNonce = 0;
    }
}
