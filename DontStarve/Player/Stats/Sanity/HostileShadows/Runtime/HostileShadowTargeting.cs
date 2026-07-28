#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal static class HostileShadowTargetingLimits
{
    internal const uint RefreshCadenceTicks = 15;
    internal const int MaximumLocations = 64;
    internal const int MaximumPlayers = 256;
    internal const double MaximumMovementElapsedSeconds = 0.25d;
    internal const double NominalTicksPerSecond = 60d;
}

internal enum HostileShadowTargetSource
{
    None,
    RecentAttacker,
    Owner,
    NearestPlayer,
}

internal sealed record HostileShadowPlayerSample(
    string PlayerKey,
    string LocationId,
    double StandingX,
    double StandingY
);

internal readonly record struct HostileShadowPlayerIndexBuildResult(
    bool Success,
    string Reason,
    int PlayerCount,
    int LocationCount
);

/// <summary>
/// Rebuilt at a fixed cadence from online farmers. Targeting never scans game locations or the
/// world character graph; it only reads this bounded location-partitioned snapshot.
/// </summary>
internal sealed class HostileShadowLocationPlayerIndex
{
    private readonly Dictionary<string, List<HostileShadowPlayerSample>> byLocation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostileShadowPlayerSample> byPlayer =
        new(StringComparer.Ordinal);

    internal int PlayerCount => byPlayer.Count;
    internal int LocationCount => byLocation.Count;

    internal HostileShadowPlayerIndexBuildResult Rebuild(
        IEnumerable<HostileShadowPlayerSample>? samples
    )
    {
        byLocation.Clear();
        byPlayer.Clear();
        if (samples is null)
            return Failure("hostile-shadow.player-index.samples-missing");

        foreach (var sample in samples)
        {
            if (
                !SanityPlayerKey.IsCanonical(sample.PlayerKey)
                || !IsValidLocationId(sample.LocationId)
                || !double.IsFinite(sample.StandingX)
                || !double.IsFinite(sample.StandingY)
                || byPlayer.Count >= HostileShadowTargetingLimits.MaximumPlayers
                || !byPlayer.TryAdd(sample.PlayerKey, sample)
            )
            {
                byLocation.Clear();
                byPlayer.Clear();
                return Failure("hostile-shadow.player-index.sample-invalid-or-capacity");
            }

            if (!byLocation.TryGetValue(sample.LocationId, out var locationPlayers))
            {
                if (byLocation.Count >= HostileShadowTargetingLimits.MaximumLocations)
                {
                    byLocation.Clear();
                    byPlayer.Clear();
                    return Failure("hostile-shadow.player-index.location-capacity");
                }
                locationPlayers = new List<HostileShadowPlayerSample>();
                byLocation.Add(sample.LocationId, locationPlayers);
            }
            locationPlayers.Add(sample);
        }

        return new HostileShadowPlayerIndexBuildResult(
            true,
            "hostile-shadow.player-index-rebuilt",
            byPlayer.Count,
            byLocation.Count
        );
    }

    internal bool TryGetPlayer(
        string playerKey,
        out HostileShadowPlayerSample? sample
    )
    {
        sample = null;
        if (!byPlayer.TryGetValue(playerKey, out var found))
            return false;
        sample = found;
        return true;
    }

    internal bool TrySelectTarget(
        string locationId,
        string ownerPlayerKey,
        string recentAttackerPlayerKey,
        double originX,
        double originY,
        double detectionRadiusPixels,
        out HostileShadowPlayerSample? target,
        out HostileShadowTargetSource source
    )
    {
        target = null;
        source = HostileShadowTargetSource.None;
        if (
            !IsValidLocationId(locationId)
            || !SanityPlayerKey.IsCanonical(ownerPlayerKey)
            || !double.IsFinite(originX)
            || !double.IsFinite(originY)
            || !double.IsFinite(detectionRadiusPixels)
            || detectionRadiusPixels <= 0d
            || !byLocation.TryGetValue(locationId, out var candidates)
        )
        {
            return false;
        }

        if (
            SanityPlayerKey.IsCanonical(recentAttackerPlayerKey)
            && TryGetEligible(
                recentAttackerPlayerKey,
                locationId,
                originX,
                originY,
                detectionRadiusPixels,
                out target
            )
        )
        {
            source = HostileShadowTargetSource.RecentAttacker;
            return true;
        }

        if (
            TryGetEligible(
                ownerPlayerKey,
                locationId,
                originX,
                originY,
                detectionRadiusPixels,
                out target
            )
        )
        {
            source = HostileShadowTargetSource.Owner;
            return true;
        }

        var rangeSquared = detectionRadiusPixels * detectionRadiusPixels;
        var bestDistanceSquared = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            var distanceSquared = DistanceSquared(
                originX,
                originY,
                candidate.StandingX,
                candidate.StandingY
            );
            if (distanceSquared > rangeSquared)
                continue;
            if (
                target is null
                || distanceSquared < bestDistanceSquared
                || (
                    SameDouble(distanceSquared, bestDistanceSquared)
                    && string.CompareOrdinal(
                        candidate.PlayerKey,
                        target.PlayerKey
                    ) < 0
                )
            )
            {
                target = candidate;
                bestDistanceSquared = distanceSquared;
            }
        }

        if (target is null)
            return false;
        source = HostileShadowTargetSource.NearestPlayer;
        return true;
    }

    private bool TryGetEligible(
        string playerKey,
        string locationId,
        double originX,
        double originY,
        double detectionRadiusPixels,
        out HostileShadowPlayerSample? sample
    )
    {
        sample = null;
        if (
            !byPlayer.TryGetValue(playerKey, out var candidate)
            || !string.Equals(
                candidate.LocationId,
                locationId,
                StringComparison.Ordinal
            )
            || DistanceSquared(
                originX,
                originY,
                candidate.StandingX,
                candidate.StandingY
            ) > detectionRadiusPixels * detectionRadiusPixels
        )
        {
            return false;
        }
        sample = candidate;
        return true;
    }

    private HostileShadowPlayerIndexBuildResult Failure(string reason)
    {
        return new HostileShadowPlayerIndexBuildResult(
            false,
            reason,
            byPlayer.Count,
            byLocation.Count
        );
    }

    private static bool IsValidLocationId(string value)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > HostileShadowProtocol.MaximumIdentifierLength
        )
        {
            return false;
        }
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }

    private static double DistanceSquared(
        double leftX,
        double leftY,
        double rightX,
        double rightY
    )
    {
        var x = rightX - leftX;
        var y = rightY - leftY;
        return (x * x) + (y * y);
    }

    private static bool SameDouble(double left, double right)
    {
        return BitConverter.DoubleToInt64Bits(left)
            == BitConverter.DoubleToInt64Bits(right);
    }
}

internal sealed class HostileShadowTargetingInput
{
    internal long EntityId { get; init; }
    internal string OwnerPlayerKey { get; init; } = string.Empty;
    internal string LocationId { get; init; } = string.Empty;
    internal string RecentAttackerPlayerKey { get; init; } = string.Empty;
    internal double PositionX { get; init; }
    internal double PositionY { get; init; }
    internal double StandingX { get; init; }
    internal double StandingY { get; init; }
    internal double MovementSpeed { get; init; }
    internal double DetectionRadiusPixels { get; init; }
    internal double StopDistancePixels { get; init; }
    internal long SpawnGameMinute { get; init; }
    internal long CurrentGameMinute { get; init; }
    internal long NaturalTtlMinutes { get; init; }
    internal double ElapsedSeconds { get; init; }
}

internal readonly record struct HostileShadowTargetingDecision(
    bool Valid,
    bool NaturalTtlExpired,
    string StateId,
    string TargetPlayerKey,
    HostileShadowTargetSource TargetSource,
    double PositionX,
    double PositionY,
    string Reason
);

internal static class HostileShadowTargetingEngine
{
    internal static HostileShadowTargetingDecision Evaluate(
        HostileShadowTargetingInput? input,
        HostileShadowLocationPlayerIndex? index
    )
    {
        if (!IsValid(input) || index is null)
            return Invalid("hostile-shadow.targeting-input-invalid");

        var ageMinutes = input!.CurrentGameMinute >= input.SpawnGameMinute
            ? input.CurrentGameMinute - input.SpawnGameMinute
            : 0;
        if (ageMinutes >= input.NaturalTtlMinutes)
        {
            return new HostileShadowTargetingDecision(
                true,
                true,
                HostileShadowStateIds.Idle,
                string.Empty,
                HostileShadowTargetSource.None,
                input.PositionX,
                input.PositionY,
                "hostile-shadow.natural-ttl-expired"
            );
        }

        if (
            !index.TrySelectTarget(
                input.LocationId,
                input.OwnerPlayerKey,
                input.RecentAttackerPlayerKey,
                input.StandingX,
                input.StandingY,
                input.DetectionRadiusPixels,
                out var target,
                out var source
            )
            || target is null
        )
        {
            return new HostileShadowTargetingDecision(
                true,
                false,
                HostileShadowStateIds.Idle,
                string.Empty,
                HostileShadowTargetSource.None,
                input.PositionX,
                input.PositionY,
                "hostile-shadow.target-unavailable-idle"
            );
        }

        var advanced = AdvancePosition(
            input.PositionX,
            input.PositionY,
            input.StandingX,
            input.StandingY,
            target.StandingX,
            target.StandingY,
            input.MovementSpeed,
            input.StopDistancePixels,
            input.ElapsedSeconds
        );

        return new HostileShadowTargetingDecision(
            true,
            false,
            HostileShadowStateIds.Chase,
            target.PlayerKey,
            source,
            advanced.PositionX,
            advanced.PositionY,
            source == HostileShadowTargetSource.RecentAttacker
                ? "hostile-shadow.target-recent-attacker"
                : source == HostileShadowTargetSource.Owner
                    ? "hostile-shadow.target-owner"
                    : "hostile-shadow.target-nearest-player"
        );
    }

    internal static HostileShadowMovementDecision AdvancePosition(
        double positionX,
        double positionY,
        double standingX,
        double standingY,
        double targetStandingX,
        double targetStandingY,
        double movementSpeed,
        double stopDistancePixels,
        double elapsedSeconds
    )
    {
        if (
            !double.IsFinite(positionX)
            || !double.IsFinite(positionY)
            || !double.IsFinite(standingX)
            || !double.IsFinite(standingY)
            || !double.IsFinite(targetStandingX)
            || !double.IsFinite(targetStandingY)
            || !double.IsFinite(movementSpeed)
            || movementSpeed < 0d
            || !double.IsFinite(stopDistancePixels)
            || stopDistancePixels < 0d
            || !double.IsFinite(elapsedSeconds)
            || elapsedSeconds < 0d
        )
        {
            return new HostileShadowMovementDecision(
                false,
                positionX,
                positionY,
                "hostile-shadow.movement-input-invalid"
            );
        }

        var x = targetStandingX - standingX;
        var y = targetStandingY - standingY;
        var distance = Math.Sqrt((x * x) + (y * y));
        if (distance <= stopDistancePixels || distance <= 0d)
        {
            return new HostileShadowMovementDecision(
                true,
                positionX,
                positionY,
                "hostile-shadow.movement-at-stop-distance"
            );
        }

        var elapsed = Math.Min(
            elapsedSeconds,
            HostileShadowTargetingLimits.MaximumMovementElapsedSeconds
        );
        var maximumTravel = movementSpeed
            * elapsed
            * HostileShadowTargetingLimits.NominalTicksPerSecond;
        var travel = Math.Min(maximumTravel, distance - stopDistancePixels);
        return new HostileShadowMovementDecision(
            true,
            positionX + ((x / distance) * travel),
            positionY + ((y / distance) * travel),
            "hostile-shadow.movement-advanced-directly"
        );
    }

    private static bool IsValid(HostileShadowTargetingInput? input)
    {
        return input is not null
            && input.EntityId > 0
            && SanityPlayerKey.IsCanonical(input.OwnerPlayerKey)
            && !string.IsNullOrWhiteSpace(input.LocationId)
            && double.IsFinite(input.PositionX)
            && double.IsFinite(input.PositionY)
            && double.IsFinite(input.StandingX)
            && double.IsFinite(input.StandingY)
            && double.IsFinite(input.MovementSpeed)
            && input.MovementSpeed >= 0d
            && double.IsFinite(input.DetectionRadiusPixels)
            && input.DetectionRadiusPixels > 0d
            && double.IsFinite(input.StopDistancePixels)
            && input.StopDistancePixels >= 0d
            && input.StopDistancePixels <= input.DetectionRadiusPixels
            && input.SpawnGameMinute >= 0
            && input.CurrentGameMinute >= 0
            && input.NaturalTtlMinutes > 0
            && double.IsFinite(input.ElapsedSeconds)
            && input.ElapsedSeconds >= 0d;
    }

    private static HostileShadowTargetingDecision Invalid(string reason)
    {
        return new HostileShadowTargetingDecision(
            false,
            false,
            HostileShadowStateIds.Idle,
            string.Empty,
            HostileShadowTargetSource.None,
            0d,
            0d,
            reason
        );
    }
}

internal readonly record struct HostileShadowMovementDecision(
    bool Valid,
    double PositionX,
    double PositionY,
    string Reason
);
