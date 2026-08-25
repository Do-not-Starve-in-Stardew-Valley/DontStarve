#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal interface IHostileShadowTeleportRandom
{
    int Seed { get; }

    int Next(int minimumInclusive, int maximumExclusive);
}

internal interface IHostileShadowTeleportMap
{
    bool IsLocationValid(string expectedLocationId);

    bool IsTileOnMap(int tileX, int tileY);

    bool IsTileLocationOpen(int tileX, int tileY);

    bool IsTilePassable(int tileX, int tileY);
}

internal enum HostileShadowTeleportSelectionStatus
{
    Selected,
    NoLegalPoint,
    LocationInvalid,
    Rejected,
}

internal readonly record struct HostileShadowTeleportPoint(
    double PositionX,
    double PositionY,
    int TileOffsetX,
    int TileOffsetY
);

internal readonly record struct HostileShadowTeleportSelectionResult(
    HostileShadowTeleportSelectionStatus Status,
    HostileShadowTeleportPoint Point,
    int RandomSeed,
    int Attempts,
    string Reason
)
{
    internal bool Selected => Status == HostileShadowTeleportSelectionStatus.Selected;
}

internal sealed class HostileShadowTeleportRandom : IHostileShadowTeleportRandom
{
    private readonly Random random;

    internal HostileShadowTeleportRandom(int seed)
    {
        Seed = seed;
        random = new Random(seed);
    }

    public int Seed { get; }

    public int Next(int minimumInclusive, int maximumExclusive)
    {
        return random.Next(minimumInclusive, maximumExclusive);
    }
}

internal static class HostileShadowTeleportSeed
{
    internal static int Derive(string sessionId, long entityId, long revision)
    {
        // FNV-1a is stable across processes; HashCode/string.GetHashCode are intentionally not.
        var hash = 2166136261u;
        foreach (var character in sessionId ?? string.Empty)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        Add(ref hash, unchecked((ulong)entityId));
        Add(ref hash, unchecked((ulong)revision));
        return unchecked((int)hash);
    }

    private static void Add(ref uint hash, ulong value)
    {
        for (var index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)(value & byte.MaxValue);
            hash *= 16777619u;
            value >>= 8;
        }
    }
}

/// <summary>
/// Samples at most sixteen candidates. It never scans a map and accepts only a still-valid
/// location, a 4-8 tile displacement, and a tile passing the map/on-map/open/passable gates.
/// </summary>
internal sealed class HostileShadowTeleportPointSelector
{
    internal const int MinimumDistanceTiles = 4;
    internal const int MaximumDistanceTiles = 16;
    internal const int MaximumAttempts = 16;

    internal HostileShadowTeleportSelectionResult Select(
        IHostileShadowTeleportMap? map,
        string expectedLocationId,
        double originPositionX,
        double originPositionY,
        double tileSizePixels,
        IHostileShadowTeleportRandom? random
    )
    {
        var seed = random?.Seed ?? 0;
        if (
            map is null
            || random is null
            || string.IsNullOrWhiteSpace(expectedLocationId)
            || !double.IsFinite(originPositionX)
            || !double.IsFinite(originPositionY)
            || !double.IsFinite(tileSizePixels)
            || tileSizePixels <= 0d
        )
        {
            return Failure(
                HostileShadowTeleportSelectionStatus.Rejected,
                seed,
                0,
                "hostile-shadow.hit-teleport-selection-input-invalid"
            );
        }
        if (!map.IsLocationValid(expectedLocationId))
        {
            return Failure(
                HostileShadowTeleportSelectionStatus.LocationInvalid,
                seed,
                0,
                "hostile-shadow.hit-teleport-location-invalid"
            );
        }

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            int distance;
            int direction;
            try
            {
                distance = random.Next(
                    MinimumDistanceTiles,
                    MaximumDistanceTiles + 1
                );
                direction = random.Next(0, 8);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Failure(
                    HostileShadowTeleportSelectionStatus.Rejected,
                    seed,
                    attempt,
                    "hostile-shadow.hit-teleport-rng-invalid"
                );
            }

            var (offsetX, offsetY) = Offset(distance, direction);
            var squaredDistance = (offsetX * offsetX) + (offsetY * offsetY);
            if (
                squaredDistance
                    < MinimumDistanceTiles * MinimumDistanceTiles
                || squaredDistance
                    > MaximumDistanceTiles * MaximumDistanceTiles
            )
            {
                continue;
            }

            var positionX = originPositionX + offsetX * tileSizePixels;
            var positionY = originPositionY + offsetY * tileSizePixels;
            var tileX = (int)Math.Floor(positionX / tileSizePixels);
            var tileY = (int)Math.Floor(positionY / tileSizePixels);
            if (
                !map.IsTileOnMap(tileX, tileY)
                || !map.IsTileLocationOpen(tileX, tileY)
                || !map.IsTilePassable(tileX, tileY)
            )
            {
                continue;
            }
            if (!map.IsLocationValid(expectedLocationId))
            {
                return Failure(
                    HostileShadowTeleportSelectionStatus.LocationInvalid,
                    seed,
                    attempt,
                    "hostile-shadow.hit-teleport-location-invalid"
                );
            }

            return new HostileShadowTeleportSelectionResult(
                HostileShadowTeleportSelectionStatus.Selected,
                new HostileShadowTeleportPoint(
                    positionX,
                    positionY,
                    offsetX,
                    offsetY
                ),
                seed,
                attempt,
                "hostile-shadow.hit-teleport-point-selected"
            );
        }

        return Failure(
            HostileShadowTeleportSelectionStatus.NoLegalPoint,
            seed,
            MaximumAttempts,
            "hostile-shadow.hit-teleport-no-legal-point"
        );
    }

    private static (int X, int Y) Offset(int distance, int direction)
    {
        var diagonal = (int)Math.Round(
            distance / Math.Sqrt(2d),
            MidpointRounding.AwayFromZero
        );
        return direction switch
        {
            0 => (distance, 0),
            1 => (diagonal, diagonal),
            2 => (0, distance),
            3 => (-diagonal, diagonal),
            4 => (-distance, 0),
            5 => (-diagonal, -diagonal),
            6 => (0, -distance),
            _ => (diagonal, -diagonal),
        };
    }

    private static HostileShadowTeleportSelectionResult Failure(
        HostileShadowTeleportSelectionStatus status,
        int seed,
        int attempts,
        string reason
    )
    {
        return new HostileShadowTeleportSelectionResult(
            status,
            default,
            seed,
            attempts,
            reason
        );
    }
}
