#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace DontStarve.Config;

internal sealed class GameplayConfigFingerprint
{
    private static readonly string[] RequiredWorldStateKeys =
    {
        ConfigKeys.EnableSanitySystem,
        ConfigKeys.SanityMonsterIntensity,
        ConfigKeys.DarkHandMode,
        ConfigKeys.DarknessDamageMode,
        ConfigKeys.MonsterDifficultyProfile,
        ConfigKeys.EnableJunimoBlessing,
    };

    private GameplayConfigFingerprint(
        bool isAvailable,
        string publicIdentifier,
        string fullHash,
        string canonicalText,
        string reason
    )
    {
        IsAvailable = isAvailable;
        PublicIdentifier = publicIdentifier;
        FullHash = fullHash;
        CanonicalText = canonicalText;
        Reason = reason;
    }

    internal bool IsAvailable { get; }

    internal string PublicIdentifier { get; }

    internal string FullHash { get; }

    internal string CanonicalText { get; }

    internal string Reason { get; }

    internal static GameplayConfigFingerprint Create(
        ConfigRegistry registry,
        TypedConfigResolver resolver
    )
    {
        foreach (var requiredKey in RequiredWorldStateKeys)
        {
            if (
                !registry.TryGet(requiredKey, out var requiredOption)
                || requiredOption is null
                || !requiredOption.AffectsWorldState
            )
            {
                return Unavailable($"fingerprint.value-unavailable:{requiredKey}");
            }
        }

        var worldOptions = new List<ConfigOptionDefinition>();
        foreach (var option in registry.Options)
        {
            if (option.AffectsWorldState)
                worldOptions.Add(option);
        }

        worldOptions.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key)
        );
        var canonical = new StringBuilder();
        canonical.Append("SchemaVersion=");
        canonical.Append(registry.SchemaVersion);
        canonical.Append('\n');
        foreach (var option in worldOptions)
        {
            if (!resolver.TryGetCanonical(option.Key, out var value, out _))
                return Unavailable($"fingerprint.value-unavailable:{option.Key}");

            canonical.Append(option.Key);
            canonical.Append('=');
            canonical.Append(value);
            canonical.Append('\n');
        }

        var canonicalText = canonical.ToString();
        var fullHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))
        );
        return new GameplayConfigFingerprint(
            true,
            fullHash[..12],
            fullHash,
            canonicalText,
            "fingerprint.available"
        );
    }

    private static GameplayConfigFingerprint Unavailable(string reason)
    {
        return new GameplayConfigFingerprint(
            false,
            $"Unavailable:{reason}",
            string.Empty,
            string.Empty,
            reason
        );
    }
}
