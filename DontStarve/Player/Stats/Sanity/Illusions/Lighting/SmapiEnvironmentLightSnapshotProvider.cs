#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// The only Stardew/SMAPI adapter for environment-light evidence. It reads the current location's
/// already-built draw list at a bounded cadence; it never scans locations, tiles, machines, or
/// shared entity collections.
/// </summary>
internal sealed class SmapiEnvironmentLightSnapshotProvider
    : IEnvironmentLightSnapshotProvider,
        IEnvironmentLightSnapshotInvalidationSource
{
    internal const int MaximumCandidateSnapshots = 64;

    private readonly IEnvironmentNightVisionProvider nightVisionProvider;
    private readonly EnvironmentLightLocationRuleCatalog locationRules;
    private readonly IEnvironmentLightFinalVisibilityProvider finalVisibilityProvider;

    internal SmapiEnvironmentLightSnapshotProvider(
        IEnvironmentNightVisionProvider nightVisionProvider,
        EnvironmentLightLocationRuleCatalog locationRules
    )
        : this(
            nightVisionProvider,
            locationRules,
            new UnavailableEnvironmentLightFinalVisibilityProvider()
        ) { }

    internal SmapiEnvironmentLightSnapshotProvider(
        IEnvironmentNightVisionProvider nightVisionProvider,
        EnvironmentLightLocationRuleCatalog locationRules,
        IEnvironmentLightFinalVisibilityProvider finalVisibilityProvider
    )
    {
        this.nightVisionProvider = nightVisionProvider
            ?? throw new ArgumentNullException(nameof(nightVisionProvider));
        this.locationRules = locationRules
            ?? throw new ArgumentNullException(nameof(locationRules));
        this.finalVisibilityProvider = finalVisibilityProvider
            ?? throw new ArgumentNullException(nameof(finalVisibilityProvider));
    }

    public event Action<string, int>? SnapshotInvalidated
    {
        add => finalVisibilityProvider.SampleUpdated += value;
        remove => finalVisibilityProvider.SampleUpdated -= value;
    }

    /// <summary>
    /// Host-side pass-out requests reuse the exact stage-02 location evidence and catalog without
    /// borrowing the local-screen-only light snapshot path or creating a second whitelist.
    /// </summary>
    internal static EnvironmentLightLocationRuleResolution ResolveLocationRule(
        GameLocation location,
        EnvironmentLightLocationRuleCatalog locationRules
    )
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(locationRules);
        return locationRules.Resolve(CaptureLocationEvidence(location, locationRules));
    }

    public EnvironmentLightSnapshotCaptureResult Capture(
        Farmer owner,
        int screenId,
        GameLocation location,
        long gameMinute
    )
    {
        if (owner is null)
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.owner-invalid"
            );
        }
        if (location is null)
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.location-invalid"
            );
        }
        if (
            !Context.IsWorldReady
            || screenId < 0
            || screenId != Context.ScreenId
            || !Context.HasScreenId(screenId)
        )
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Unavailable,
                "environment-light.screen-unavailable"
            );
        }
        if (
            !owner.IsLocalPlayer
            || !ReferenceEquals(owner, Game1.player)
            || !ReferenceEquals(location, Game1.currentLocation)
        )
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.owner-location-mismatch"
            );
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            owner.UniqueMultiplayerID
        );
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.owner-key-invalid"
            );
        }
        var locationName = location.NameOrUniqueName;
        if (string.IsNullOrWhiteSpace(locationName))
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.location-identity-invalid"
            );
        }

        var standingPixel = owner.StandingPixel;
        var standing = new EnvironmentLightWorldPoint(
            standingPixel.X,
            standingPixel.Y
        );
        if (!standing.IsFinite)
        {
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.standing-pixel-invalid"
            );
        }

        try
        {
            var locationInstanceId = EnvironmentLightLocationIdentity.Get(location);
            var runtimeType = location.GetType().FullName ?? string.Empty;
            var contextId = location.GetLocationContextId() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runtimeType) || string.IsNullOrWhiteSpace(contextId))
            {
                return Failed(
                    EnvironmentLightCapabilityStatus.Invalid,
                    EnvironmentLightReasonIds.LocationEvidenceInvalid
                );
            }
            var locationEvidence = CaptureLocationEvidence(location, locationRules);
            var locationRule = locationRules.Resolve(locationEvidence);

            var baseSource = EnvironmentLightBaseSource.Unknown;
            var baseStatus = EnvironmentLightCapabilityStatus.Available;
            Color baseColor;
            var mineStatus = EnvironmentLightCapabilityStatus.Unavailable;
            bool? isMineDarkArea = null;

            if (location is MineShaft mine)
            {
                baseSource = EnvironmentLightBaseSource.MineLighting;
                baseColor = mine.getLightingColor(Game1.currentGameTime);
                mineStatus = EnvironmentLightCapabilityStatus.Available;
                isMineDarkArea = mine.isDarkArea();
            }
            else
            {
                // This mirrors Game1.DrawLighting's public base-color branch exactly. It is still
                // raw lightmap evidence, not a final brightness sample at StandingPixel.
                var usesOutdoor =
                    Game1.ambientLight.Equals(Color.White)
                    || (location.IsOutdoors && location.IsRainingHere());
                baseSource = usesOutdoor
                    ? EnvironmentLightBaseSource.Outdoor
                    : EnvironmentLightBaseSource.Ambient;
                baseColor = usesOutdoor ? Game1.outdoorLight : Game1.ambientLight;
            }

            var rawLightLevel = location.LightLevel;
            var locationLightLevelStatus = float.IsFinite(rawLightLevel)
                ? EnvironmentLightCapabilityStatus.Available
                : EnvironmentLightCapabilityStatus.Invalid;
            var isDarkOut = Game1.isDarkOut(location);
            var nightVision = ProbeNightVision(owner, screenId, location);

            var currentSources = Game1.currentLightSources;
            if (currentSources is null)
            {
                return Failed(
                    EnvironmentLightCapabilityStatus.Unavailable,
                    EnvironmentLightReasonIds.CandidateCollectionUnavailable
                );
            }

            var currentCount = currentSources.Count;
            var sharedCount = location.sharedLights.Count();
            var candidates = new List<EnvironmentLightCandidateSnapshot>(
                Math.Min(currentCount, MaximumCandidateSnapshots)
            );
            var candidateStatus = EnvironmentLightCapabilityStatus.Available;
            var candidateReason = "environment-light.candidates-captured";

            if (currentCount > MaximumCandidateSnapshots)
            {
                // Refuse an incomplete prefix instead of pretending it represents the nearest
                // light. Count is O(1); no source is enumerated on this fallback path.
                candidateStatus = EnvironmentLightCapabilityStatus.Unavailable;
                candidateReason = "environment-light.candidates-limit-exceeded";
            }
            else
            {
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in currentSources)
                {
                    var source = pair.Value;
                    if (source is null)
                    {
                        candidateStatus = EnvironmentLightCapabilityStatus.Invalid;
                        candidateReason = EnvironmentLightReasonIds.CandidateInvalid;
                        continue;
                    }

                    var onlyLocation = source.onlyLocation.Value ?? string.Empty;

                    var id = string.IsNullOrWhiteSpace(source.Id)
                        ? pair.Key
                        : source.Id;
                    if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                    {
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            candidateStatus = EnvironmentLightCapabilityStatus.Invalid;
                            candidateReason = EnvironmentLightReasonIds.CandidateInvalid;
                        }
                        continue;
                    }

                    var position = source.position.Value;
                    var rawRadius = source.radius.Value;
                    var rawTint = source.color.Value;
                    var attachedPlayerId = source.playerID.Value;
                    var lightContext = ToSnapshotContext(source.lightContext.Value);
                    var candidatePosition = new EnvironmentLightWorldPoint(
                        position.X,
                        position.Y
                    );
                    var valid =
                        candidatePosition.IsFinite
                        && float.IsFinite(rawRadius)
                        && rawRadius >= 0f
                        && attachedPlayerId >= 0
                        && lightContext != EnvironmentLightCandidateContext.Unknown;
                    var distance = valid
                        ? Math.Sqrt(
                            Math.Pow(candidatePosition.X - standing.X, 2d)
                                + Math.Pow(candidatePosition.Y - standing.Y, 2d)
                        )
                        : double.NaN;
                    var reason = valid
                        ? "environment-light.candidate-raw-evidence"
                        : EnvironmentLightReasonIds.CandidateInvalid;
                    var drawEligibilityStatus = EnvironmentLightCapabilityStatus.Available;
                    bool? isDrawEligible;
                    string drawEligibilityReason;
                    try
                    {
                        isDrawEligible = source.IsOnScreen();
                        drawEligibilityReason = isDrawEligible.Value
                            ? "environment-light.candidate-draw-eligible"
                            : "environment-light.candidate-draw-ineligible";
                    }
                    catch (Exception)
                    {
                        drawEligibilityStatus = EnvironmentLightCapabilityStatus.Invalid;
                        isDrawEligible = null;
                        drawEligibilityReason =
                            EnvironmentLightReasonIds.CandidateDrawEligibilityInvalid;
                    }
                    if (!valid)
                    {
                        candidateStatus = EnvironmentLightCapabilityStatus.Invalid;
                        candidateReason = EnvironmentLightReasonIds.CandidateInvalid;
                    }
                    if (drawEligibilityStatus == EnvironmentLightCapabilityStatus.Invalid)
                    {
                        candidateStatus = EnvironmentLightCapabilityStatus.Invalid;
                        candidateReason =
                            EnvironmentLightReasonIds.CandidateDrawEligibilityInvalid;
                    }
                    candidates.Add(
                        new EnvironmentLightCandidateSnapshot(
                            id,
                            valid
                                ? EnvironmentLightCapabilityStatus.Available
                                : EnvironmentLightCapabilityStatus.Invalid,
                            candidatePosition,
                            distance,
                            rawRadius,
                            ToSnapshotColor(rawTint),
                            onlyLocation,
                            reason,
                            GetCandidateOrigin(location, pair.Key, id, lightContext),
                            lightContext,
                            attachedPlayerId,
                            drawEligibilityStatus,
                            isDrawEligible,
                            drawEligibilityReason
                        )
                    );
                }
                candidates.Sort(CompareCandidates);
            }

            var snapshotColor = ToSnapshotColor(baseColor);
            var capturedAtTick = Game1.ticks;
            var finalVisibility = GetFinalVisibility(
                playerKey,
                screenId,
                locationName,
                locationInstanceId,
                capturedAtTick
            );
            var revision = ComputeRevision(
                locationEvidence,
                locationRule,
                standing,
                baseStatus,
                baseSource,
                snapshotColor,
                locationLightLevelStatus,
                rawLightLevel,
                isDarkOut,
                mineStatus,
                isMineDarkArea,
                nightVision,
                candidateStatus,
                currentCount,
                sharedCount,
                candidates,
                finalVisibility
            );

            return EnvironmentLightSnapshotCaptureResult.Captured(
                new EnvironmentLightSnapshot(
                    playerKey,
                    screenId,
                    locationName,
                    locationInstanceId,
                    locationEvidence,
                    locationRule,
                    standing,
                    baseStatus,
                    baseSource,
                    snapshotColor,
                    locationLightLevelStatus,
                    rawLightLevel,
                    isDarkOut,
                    mineStatus,
                    isMineDarkArea,
                    nightVision,
                    candidateStatus,
                    candidateReason,
                    currentCount,
                    sharedCount,
                    candidates,
                    gameMinute,
                    capturedAtTick,
                    revision,
                    finalVisibility
                )
            );
        }
        catch (Exception)
        {
            // A modded location getter may throw. The stable result remains inspectable through the
            // service diagnostic; no exception is silently treated as valid light evidence.
            return Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.snapshot-read-failed"
            );
        }
    }

    private EnvironmentLightNightVisionSnapshot ProbeNightVision(
        Farmer owner,
        int screenId,
        GameLocation location
    )
    {
        try
        {
            var result = nightVisionProvider.Probe(owner, screenId, location);
            return result
                ?? new EnvironmentLightNightVisionSnapshot(
                    EnvironmentLightCapabilityStatus.Invalid,
                    null,
                    EnvironmentLightReasonIds.NightVisionInvalid
                );
        }
        catch (Exception)
        {
            return new EnvironmentLightNightVisionSnapshot(
                EnvironmentLightCapabilityStatus.Invalid,
                null,
                "environment-light.night-vision-provider-failed"
            );
        }
    }

    private static int CompareCandidates(
        EnvironmentLightCandidateSnapshot left,
        EnvironmentLightCandidateSnapshot right
    )
    {
        var leftDistance = double.IsFinite(left.DistanceWorldPixels)
            ? left.DistanceWorldPixels
            : double.PositiveInfinity;
        var rightDistance = double.IsFinite(right.DistanceWorldPixels)
            ? right.DistanceWorldPixels
            : double.PositiveInfinity;
        var distance = leftDistance.CompareTo(rightDistance);
        return distance != 0 ? distance : string.CompareOrdinal(left.Id, right.Id);
    }

    private static EnvironmentLightColor ToSnapshotColor(Color color)
    {
        return new EnvironmentLightColor(color.R, color.G, color.B, color.A);
    }

    private static EnvironmentLightCandidateContext ToSnapshotContext(
        LightSource.LightContext context
    )
    {
        return context switch
        {
            LightSource.LightContext.None => EnvironmentLightCandidateContext.None,
            LightSource.LightContext.MapLight => EnvironmentLightCandidateContext.MapLight,
            LightSource.LightContext.WindowLight => EnvironmentLightCandidateContext.WindowLight,
            _ => EnvironmentLightCandidateContext.Unknown,
        };
    }

    private static EnvironmentLightCandidateOrigin GetCandidateOrigin(
        GameLocation location,
        string dictionaryId,
        string effectiveId,
        EnvironmentLightCandidateContext context
    )
    {
        if (
            location.sharedLights.ContainsKey(effectiveId)
            || (
                !string.Equals(dictionaryId, effectiveId, StringComparison.Ordinal)
                && location.sharedLights.ContainsKey(dictionaryId)
            )
        )
        {
            return EnvironmentLightCandidateOrigin.SharedLocation;
        }

        return context switch
        {
            EnvironmentLightCandidateContext.MapLight =>
                EnvironmentLightCandidateOrigin.MapLight,
            EnvironmentLightCandidateContext.WindowLight =>
                EnvironmentLightCandidateOrigin.WindowLight,
            EnvironmentLightCandidateContext.None =>
                EnvironmentLightCandidateOrigin.CurrentOnly,
            _ => EnvironmentLightCandidateOrigin.Unknown,
        };
    }

    private static IReadOnlyDictionary<string, string> CaptureRelevantFields(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string>? source
    )
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null)
            return result;

        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value) && value is not null)
                result[key] = value;
        }
        return result;
    }

    private static EnvironmentLightLocationSnapshot CaptureLocationEvidence(
        GameLocation location,
        EnvironmentLightLocationRuleCatalog locationRules
    )
    {
        var currentEvent = Game1.CurrentEvent;
        return new EnvironmentLightLocationSnapshot(
            location.GetType().FullName ?? string.Empty,
            location.GetLocationContextId() ?? string.Empty,
            location.NameOrUniqueName ?? string.Empty,
            location.IsOutdoors,
            location.IsTemporary,
            currentEvent is not null || Game1.eventUp,
            currentEvent?.isFestival == true,
            CaptureRelevantFields(
                locationRules.LocationCustomFieldKeys,
                location.GetData()?.CustomFields
            ),
            CaptureRelevantFields(
                locationRules.ContextCustomFieldKeys,
                location.GetLocationContext()?.CustomFields
            ),
            location.ParentBuilding?.buildingType.Value ?? string.Empty
        );
    }

    private static EnvironmentLightSnapshotCaptureResult Failed(
        EnvironmentLightCapabilityStatus status,
        string reason
    )
    {
        return EnvironmentLightSnapshotCaptureResult.Failed(status, reason);
    }

    private EnvironmentLightFinalVisibilitySnapshot GetFinalVisibility(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        long currentTick
    )
    {
        try
        {
            return finalVisibilityProvider.GetLatest(
                playerKey,
                screenId,
                locationNameOrUniqueName,
                locationInstanceId,
                currentTick
            );
        }
        catch (Exception)
        {
            return EnvironmentLightFinalVisibilitySnapshot.Unavailable(
                playerKey,
                screenId,
                locationNameOrUniqueName,
                locationInstanceId,
                Math.Max(0L, currentTick),
                "environment-light.final-visibility-provider-failed",
                EnvironmentLightCapabilityStatus.Invalid
            );
        }
    }

    private static long ComputeRevision(
        EnvironmentLightLocationSnapshot location,
        EnvironmentLightLocationRuleResolution locationRule,
        EnvironmentLightWorldPoint standing,
        EnvironmentLightCapabilityStatus baseStatus,
        EnvironmentLightBaseSource baseSource,
        EnvironmentLightColor baseColor,
        EnvironmentLightCapabilityStatus lightLevelStatus,
        float rawLightLevel,
        bool isDarkOut,
        EnvironmentLightCapabilityStatus mineStatus,
        bool? isMineDarkArea,
        EnvironmentLightNightVisionSnapshot nightVision,
        EnvironmentLightCapabilityStatus candidateStatus,
        int currentCount,
        int sharedCount,
        IReadOnlyList<EnvironmentLightCandidateSnapshot> candidates,
        EnvironmentLightFinalVisibilitySnapshot finalVisibility
    )
    {
        const ulong offset = 14695981039346656037UL;
        var hash = offset;
        Add(ref hash, location.RuntimeTypeFullName);
        Add(ref hash, location.ContextId);
        Add(ref hash, location.InternalName);
        Add(ref hash, location.ParentBuildingType);
        Add(ref hash, location.IsOutdoors ? 1 : 0);
        Add(ref hash, location.IsTemporary ? 1 : 0);
        Add(ref hash, location.IsEventActive ? 1 : 0);
        Add(ref hash, location.IsFestivalActive ? 1 : 0);
        foreach (var pair in location.LocationCustomFields)
        {
            Add(ref hash, pair.Key);
            Add(ref hash, pair.Value);
        }
        foreach (var pair in location.ContextCustomFields)
        {
            Add(ref hash, pair.Key);
            Add(ref hash, pair.Value);
        }
        Add(ref hash, (int)locationRule.Status);
        Add(ref hash, locationRule.ContractVersion);
        Add(ref hash, locationRule.RuleId);
        Add(ref hash, (int)locationRule.LightProfile);
        Add(ref hash, locationRule.TwoAmSpecialDeathSafe ? 1 : 0);
        Add(ref hash, locationRule.HostileShadowSafe ? 1 : 0);
        Add(ref hash, locationRule.JunimoBlessingEligible ? 1 : 0);
        Add(ref hash, (int)locationRule.NaturalDarknessProfile);
        Add(ref hash, locationRule.TwoAmSpecialDeathReason);
        Add(ref hash, locationRule.Reason);
        Add(ref hash, BitConverter.DoubleToInt64Bits(standing.X));
        Add(ref hash, BitConverter.DoubleToInt64Bits(standing.Y));
        Add(ref hash, (int)baseStatus);
        Add(ref hash, (int)baseSource);
        Add(ref hash, baseColor.R);
        Add(ref hash, baseColor.G);
        Add(ref hash, baseColor.B);
        Add(ref hash, baseColor.A);
        Add(ref hash, (int)lightLevelStatus);
        Add(ref hash, BitConverter.SingleToInt32Bits(rawLightLevel));
        Add(ref hash, isDarkOut ? 1 : 0);
        Add(ref hash, (int)mineStatus);
        Add(ref hash, isMineDarkArea.HasValue ? (isMineDarkArea.Value ? 2 : 1) : 0);
        Add(ref hash, (int)nightVision.CapabilityStatus);
        Add(ref hash, nightVision.IsActive.HasValue ? (nightVision.IsActive.Value ? 2 : 1) : 0);
        Add(ref hash, nightVision.Reason);
        Add(ref hash, nightVision.PlayerKey);
        Add(ref hash, nightVision.ScreenId);
        Add(ref hash, nightVision.LocationNameOrUniqueName);
        Add(ref hash, nightVision.LocationInstanceId);
        Add(ref hash, (int)candidateStatus);
        Add(ref hash, currentCount);
        Add(ref hash, sharedCount);
        foreach (var candidate in candidates)
        {
            Add(ref hash, candidate.Id);
            Add(ref hash, (int)candidate.CapabilityStatus);
            Add(ref hash, BitConverter.DoubleToInt64Bits(candidate.Position.X));
            Add(ref hash, BitConverter.DoubleToInt64Bits(candidate.Position.Y));
            Add(ref hash, BitConverter.SingleToInt32Bits(candidate.RawRadius));
            Add(ref hash, candidate.RawTint.R);
            Add(ref hash, candidate.RawTint.G);
            Add(ref hash, candidate.RawTint.B);
            Add(ref hash, candidate.RawTint.A);
            Add(ref hash, candidate.OnlyLocation);
            Add(ref hash, (int)candidate.Origin);
            Add(ref hash, (int)candidate.LightContext);
            Add(ref hash, candidate.AttachedPlayerId);
            Add(ref hash, (int)candidate.DrawEligibilityCapabilityStatus);
            Add(
                ref hash,
                candidate.IsDrawEligible.HasValue
                    ? candidate.IsDrawEligible.Value
                        ? 2
                        : 1
                    : 0
            );
            Add(ref hash, candidate.DrawEligibilityReason);
        }
        Add(ref hash, (int)finalVisibility.CapabilityStatus);
        Add(ref hash, finalVisibility.PlayerKey);
        Add(ref hash, finalVisibility.ScreenId);
        Add(ref hash, finalVisibility.LocationNameOrUniqueName);
        Add(ref hash, finalVisibility.LocationInstanceId);
        Add(
            ref hash,
            double.IsFinite(finalVisibility.VisibilityScore)
                ? BitConverter.DoubleToInt64Bits(finalVisibility.VisibilityScore)
                : long.MinValue
        );
        Add(ref hash, finalVisibility.StandardLightingDrawn ? 1 : 0);
        Add(ref hash, finalVisibility.RainOverlayApplied ? 1 : 0);
        Add(ref hash, finalVisibility.LightingQuality);
        Add(
            ref hash,
            double.IsFinite(finalVisibility.ZoomLevel)
                ? BitConverter.DoubleToInt64Bits(finalVisibility.ZoomLevel)
                : long.MinValue
        );
        Add(ref hash, finalVisibility.UseUnscaledLighting ? 1 : 0);
        Add(ref hash, finalVisibility.CapturedAtTick);
        Add(ref hash, finalVisibility.RendererRevision);
        Add(ref hash, finalVisibility.EvaluatorRevision);
        Add(ref hash, finalVisibility.Reason);
        return unchecked((long)hash);
    }

    private static void Add(ref ulong hash, long value)
    {
        Add(ref hash, unchecked((ulong)value));
    }

    private static void Add(ref ulong hash, int value)
    {
        Add(ref hash, unchecked((ulong)(uint)value));
    }

    private static void Add(ref ulong hash, byte value)
    {
        Add(ref hash, (ulong)value);
    }

    private static void Add(ref ulong hash, string value)
    {
        foreach (var character in value)
            Add(ref hash, character);
    }

    private static void Add(ref ulong hash, ulong value)
    {
        const ulong prime = 1099511628211UL;
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= prime;
        }
    }
}
