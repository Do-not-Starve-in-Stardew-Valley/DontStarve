#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal interface IHarmlessProjectionRandom
{
    double NextUnitDouble();
}

internal interface IHarmlessProjectionMapCapability
{
    bool IsTileOnMap(int tileX, int tileY);

    bool IsTileLocationOpen(int tileX, int tileY);

    bool IsTilePassable(int tileX, int tileY);

    bool IsAnchorVisible(HarmlessProjectionWorldPoint worldPixel);
}

internal interface IHarmlessProjectionPlacementPolicy
{
    int MinimumDistanceTiles { get; }

    int MaximumDistanceTiles { get; }

    int CandidateAttemptLimit { get; }

    HarmlessProjectionPlacementKind PlacementKind { get; }
}

internal sealed class HarmlessProjectionSpawnPointResult
{
    private HarmlessProjectionSpawnPointResult(
        bool success,
        string reason,
        int attempts,
        HarmlessProjectionWorldPoint? worldPixel
    )
    {
        Success = success;
        Reason = reason;
        Attempts = attempts;
        WorldPixel = worldPixel;
    }

    internal bool Success { get; }

    internal string Reason { get; }

    internal int Attempts { get; }

    internal HarmlessProjectionWorldPoint? WorldPixel { get; }

    internal static HarmlessProjectionSpawnPointResult Found(
        HarmlessProjectionWorldPoint worldPixel,
        int attempts
    )
    {
        return new HarmlessProjectionSpawnPointResult(
            true,
            "spawn.legal-point-found",
            attempts,
            worldPixel
        );
    }

    internal static HarmlessProjectionSpawnPointResult Failed(
        string reason,
        int attempts
    )
    {
        return new HarmlessProjectionSpawnPointResult(false, reason, attempts, null);
    }
}

/// <summary>
/// Selects at most 16 deterministic candidates around StandingPixel. It only asks the injected
/// current-location capability about those candidates and never scans a map, machine, or entity list.
/// </summary>
internal sealed class HarmlessProjectionSpawnPointSelector
{
    internal const int TileSize = 64;

    internal HarmlessProjectionSpawnPointResult Select(
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        IHarmlessProjectionPlacementPolicy policy,
        IHarmlessProjectionMapCapability map,
        IHarmlessProjectionRandom random
    )
    {
        if (!ownerStandingWorldPixel.IsFinite)
        {
            return HarmlessProjectionSpawnPointResult.Failed(
                "spawn.owner-standing-pixel-invalid",
                0
            );
        }
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(random);

        for (var attempt = 1; attempt <= policy.CandidateAttemptLimit; attempt++)
        {
            if (!TryNextUnit(random, out var angleUnit) || !TryNextUnit(random, out var radiusUnit))
            {
                return HarmlessProjectionSpawnPointResult.Failed(
                    "spawn.random-out-of-range",
                    attempt
                );
            }

            var angle = angleUnit * Math.PI * 2d;
            var radiusTiles = policy.MinimumDistanceTiles
                + (policy.MaximumDistanceTiles - policy.MinimumDistanceTiles) * radiusUnit;
            var radiusPixels = radiusTiles * TileSize;
            var candidate = new HarmlessProjectionWorldPoint(
                ownerStandingWorldPixel.X + Math.Cos(angle) * radiusPixels,
                ownerStandingWorldPixel.Y + Math.Sin(angle) * radiusPixels
            );
            if (!candidate.IsFinite)
                continue;

            var tileX = (int)Math.Floor(candidate.X / TileSize);
            var tileY = (int)Math.Floor(candidate.Y / TileSize);
            // Map bounds are always the first location query; layer APIs may index directly.
            if (!map.IsTileOnMap(tileX, tileY))
                continue;
            if (!map.IsAnchorVisible(candidate))
                continue;
            if (
                policy.PlacementKind == HarmlessProjectionPlacementKind.Ground
                && (!map.IsTileLocationOpen(tileX, tileY) || !map.IsTilePassable(tileX, tileY))
            )
            {
                continue;
            }

            return HarmlessProjectionSpawnPointResult.Found(candidate, attempt);
        }

        return HarmlessProjectionSpawnPointResult.Failed(
            "spawn.no-legal-point",
            policy.CandidateAttemptLimit
        );
    }

    private static bool TryNextUnit(
        IHarmlessProjectionRandom random,
        out double value
    )
    {
        value = random.NextUnitDouble();
        return double.IsFinite(value) && value >= 0d && value < 1d;
    }
}
