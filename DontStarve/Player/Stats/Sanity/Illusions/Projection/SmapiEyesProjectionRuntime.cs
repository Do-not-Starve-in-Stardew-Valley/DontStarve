#nullable enable

using System;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Exact-owner adapter over the existing environment-light service. The service remains the sole
/// sampler, classifier, and cadence/cache owner; this adapter adds no scan or alternate rule.
/// </summary>
internal sealed class SmapiEyesEnvironmentLightProbe
    : IEyesEnvironmentLightProbe
{
    private readonly EnvironmentLightService lightService;
    private readonly ITimeAPI timeApi;

    internal SmapiEyesEnvironmentLightProbe(
        EnvironmentLightService lightService,
        ITimeAPI timeApi
    )
    {
        this.lightService = lightService
            ?? throw new ArgumentNullException(nameof(lightService));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
    }

    public EnvironmentLightResult Observe(HarmlessProjectionOwnerContext owner)
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
            return EnvironmentLightResult.Fallback(
                EnvironmentLightReasonIds.SnapshotInvalid,
                minute
            );
        }

        // Final lightmap evidence and PitchBlack authorization remain upstream in the one service;
        // this consumer forwards its result unchanged and never creates a second classifier.
        return lightService.Evaluate(
            player,
            owner.ScreenId,
            location,
            minute
        );
    }
}
