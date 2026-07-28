#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

internal readonly record struct HostileShadowConfigFingerprintSnapshot(
    int ConfigSchemaVersion,
    string Hash
)
{
    internal static HostileShadowConfigFingerprintSnapshot Unavailable => new(0, string.Empty);
}

/// <summary>
/// Diagnostic-only fingerprint envelope. It intentionally excludes canonical config text, keys,
/// values, defaults, and any instruction to synchronize or change either peer's configuration.
/// </summary>
internal sealed class HostileShadowConfigFingerprintReport
{
    public int ProtocolVersion { get; set; } = HostileShadowProtocol.CurrentProtocolVersion;

    public int SchemaVersion { get; set; } = HostileShadowProtocol.CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string PlayerKey { get; set; } = string.Empty;

    public int ConfigSchemaVersion { get; set; }

    public string Hash { get; set; } = string.Empty;
}

internal enum HostileShadowFingerprintComparison
{
    Unavailable,
    Match,
    Mismatch,
}

internal static class HostileShadowConfigFingerprintProtocol
{
    internal const int Sha256HexLength = 64;

    internal static bool IsValidReport(
        HostileShadowConfigFingerprintReport? report,
        string expectedPlayerKey,
        string expectedSessionId,
        out string reason
    )
    {
        if (
            report is null
            || !HostileShadowProtocol.IsValidEnvelope(
                report.ProtocolVersion,
                report.SchemaVersion,
                report.SessionId
            )
            || !string.Equals(report.SessionId, expectedSessionId, StringComparison.Ordinal)
            || !SanityPlayerKey.IsCanonical(expectedPlayerKey)
            || !string.Equals(report.PlayerKey, expectedPlayerKey, StringComparison.Ordinal)
            || report.ConfigSchemaVersion < 0
            || !IsValidHashOrUnavailable(report.ConfigSchemaVersion, report.Hash)
        )
        {
            reason = "hostile-shadow.config-fingerprint-invalid";
            return false;
        }

        reason = "hostile-shadow.config-fingerprint-valid";
        return true;
    }

    internal static HostileShadowFingerprintComparison Compare(
        HostileShadowConfigFingerprintSnapshot local,
        HostileShadowConfigFingerprintReport remote
    )
    {
        if (
            local.ConfigSchemaVersion <= 0
            || string.IsNullOrEmpty(local.Hash)
            || remote.ConfigSchemaVersion <= 0
            || string.IsNullOrEmpty(remote.Hash)
        )
        {
            return HostileShadowFingerprintComparison.Unavailable;
        }
        return local.ConfigSchemaVersion == remote.ConfigSchemaVersion
            && string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase)
            ? HostileShadowFingerprintComparison.Match
            : HostileShadowFingerprintComparison.Mismatch;
    }

    private static bool IsValidHashOrUnavailable(int schemaVersion, string hash)
    {
        if (schemaVersion == 0)
            return string.IsNullOrEmpty(hash);
        if (hash is null || hash.Length != Sha256HexLength)
            return false;
        foreach (var character in hash)
        {
            var hexadecimal = character is >= '0' and <= '9'
                || character is >= 'a' and <= 'f'
                || character is >= 'A' and <= 'F';
            if (!hexadecimal)
                return false;
        }
        return true;
    }
}
