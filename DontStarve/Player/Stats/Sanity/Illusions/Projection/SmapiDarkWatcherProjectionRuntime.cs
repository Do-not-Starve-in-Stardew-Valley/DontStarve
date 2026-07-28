#nullable enable

using System;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Exact-owner adapter for the existing EnvironmentLightService. The service remains the only
/// sampler/classifier/cache; the current diagnostic has no candidate position, so this adapter
/// reports that capability gap instead of deriving or rescanning a direction.
/// </summary>
internal sealed class SmapiDarkWatcherEnvironmentLightProbe
    : IDarkWatcherEnvironmentLightProbe
{
    private readonly EnvironmentLightService lightService;
    private readonly ITimeAPI timeApi;

    internal SmapiDarkWatcherEnvironmentLightProbe(
        EnvironmentLightService lightService,
        ITimeAPI timeApi
    )
    {
        this.lightService = lightService
            ?? throw new ArgumentNullException(nameof(lightService));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
    }

    public DarkWatcherLightObservation Observe(
        HarmlessProjectionOwnerContext owner
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        var minute = timeApi.Time;
        var player = Game1.player;
        var location = Game1.currentLocation;
        if (
            !Context.IsWorldReady
            || Context.ScreenId != owner.ScreenId
            || !Context.HasScreenId(owner.ScreenId)
            || player is null
            || !player.IsLocalPlayer
            || location is null
            || !ReferenceEquals(owner.LocationReference, location)
            || !string.Equals(
                owner.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                owner.PlayerKey,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                StringComparison.Ordinal
            )
        )
        {
            return DarkWatcherLightObservation.WithoutDirection(
                EnvironmentLightResult.Fallback(
                    EnvironmentLightReasonIds.SnapshotInvalid,
                    minute
                ),
                "dark-watcher.light-owner-context-invalid"
            );
        }

        var result = lightService.Evaluate(
            player,
            owner.ScreenId,
            location,
            minute
        );
        if (
            result.Level != EnvironmentLightLevel.PitchBlack
            || result.EvidenceStatus != EnvironmentLightEvidenceStatus.Confirmed
        )
        {
            return DarkWatcherLightObservation.WithoutDirection(
                result,
                "dark-watcher.light-direction-not-applicable"
            );
        }

        if (
            !lightService.TryGetDiagnostic(
                owner.PlayerKey,
                owner.ScreenId,
                out var diagnostic
            )
        )
        {
            return DarkWatcherLightObservation.WithoutDirection(
                result,
                "dark-watcher.light-diagnostic-unavailable"
            );
        }

        return DarkWatcherLightObservation.WithoutDirection(
            result,
            string.IsNullOrWhiteSpace(diagnostic.NearestCandidateId)
                ? "dark-watcher.light-source-unavailable-owner-fallback"
                : "dark-watcher.light-direction-position-unavailable-owner-fallback"
        );
    }
}
