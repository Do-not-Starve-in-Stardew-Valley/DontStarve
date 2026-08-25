using System.Reflection;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class EnvironmentLightFoundationTests
{
    private static readonly EnvironmentLightClassifier Classifier = new(CreatePolicy());

    [Fact]
    public void WhiteBaseIsTheOnlyBuiltInConfirmedLitRule()
    {
        var result = Classifier.Classify(
            Snapshot(baseColor: White(), nightVision: NightVisionUnavailable())
        );

        Assert.Equal(EnvironmentLightLevel.Lit, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.BaseWhiteConfirmed, result.Reason);
    }

    [Fact]
    public void IsDarkOutSignalAloneNeverGuessesPitchBlack()
    {
        var result = Classifier.Classify(
            Snapshot(
                baseColor: DimColor(),
                isDarkOut: true,
                nightVision: NightVision(active: false)
            )
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.FinalBrightnessUnavailable, result.Reason);
    }

    [Theory]
    [InlineData(64d)]
    [InlineData(6400d)]
    public void NearAndFarLightsRemainRawFallbackEvidence(double distance)
    {
        var result = Classifier.Classify(
            Snapshot(
                baseColor: DimColor(),
                nightVision: NightVision(active: false),
                candidates: new[] { Candidate("lamp", distance) }
            )
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(
            EnvironmentLightReasonIds.LocalLightCoverageUnverified,
            result.Reason
        );
    }

    [Fact]
    public void ExplicitActiveNightVisionProviderCanConfirmLit()
    {
        var result = Classifier.Classify(
            Snapshot(
                baseColor: DimColor(),
                isDarkOut: true,
                nightVision: NightVision(active: true)
            )
        );

        Assert.Equal(EnvironmentLightLevel.Lit, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, result.EvidenceStatus);
        Assert.Equal(
            EnvironmentLightReasonIds.NightVisionActiveConfirmed,
            result.Reason
        );
    }

    [Fact]
    public void DarkMineDoesNotDefineTheFuturePitchBlackThreshold()
    {
        var result = Classifier.Classify(
            Snapshot(
                baseSource: EnvironmentLightBaseSource.MineLighting,
                baseColor: new EnvironmentLightColor(0, 0, 0, 255),
                mineCapability: EnvironmentLightCapabilityStatus.Available,
                isMineDarkArea: true,
                nightVision: NightVision(active: false)
            )
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(
            EnvironmentLightReasonIds.PitchBlackThresholdUnverified,
            result.Reason
        );
    }

    [Fact]
    public void UnknownBaseSourceAlwaysFailsClosed()
    {
        var result = Classifier.Classify(
            Snapshot(
                baseSource: EnvironmentLightBaseSource.Unknown,
                baseColor: White(),
                nightVision: NightVision(active: true)
            )
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.BaseUnknown, result.Reason);
    }

    [Fact]
    public void InvalidLightCandidateAlwaysFailsClosedBeforeConfirmedRules()
    {
        var invalid = Candidate(
            "broken",
            double.NaN,
            float.NaN,
            EnvironmentLightCapabilityStatus.Invalid,
            EnvironmentLightReasonIds.CandidateInvalid
        );
        var result = Classifier.Classify(
            Snapshot(
                baseColor: White(),
                nightVision: NightVision(active: true),
                candidates: new[] { invalid }
            )
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.CandidateInvalid, result.Reason);
    }

    [Fact]
    public void TruncatedCandidateSetCannotMasqueradeAsCompleteEvidence()
    {
        var result = Classifier.Classify(
            Snapshot(
                baseColor: White(),
                nightVision: NightVision(active: false),
                candidateCollectionStatus:
                    EnvironmentLightCapabilityStatus.Unavailable,
                candidateCollectionReason:
                    "environment-light.candidates-limit-exceeded"
            )
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(
            "environment-light.candidates-limit-exceeded",
            result.Reason
        );
    }

    [Fact]
    public void DefaultNightVisionUnavailableReasonIsStableAndHarmless()
    {
        var result = Classifier.Classify(
            Snapshot(baseColor: DimColor(), nightVision: NightVisionUnavailable())
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.NightVisionUnavailable, result.Reason);
    }

    [Fact]
    public void ResultContractSeparatesPitchBlackAuthorizationFromClassification()
    {
        var properties = typeof(EnvironmentLightResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "CapturedAtMinute",
                "EvidenceStatus",
                "Level",
                "PitchBlackAuthorized",
                "Reason",
            },
            properties
        );
        Assert.False(Classifier.Classify(Snapshot(baseColor: White())).PitchBlackAuthorized);
        var declaredMembers = typeof(EnvironmentLightResult)
            .GetMembers(
                BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
            )
            .Select(member => member.Name)
            .ToArray();
        foreach (var forbidden in new[] { "Damage", "Harm", "Countdown", "Callback" })
        {
            Assert.DoesNotContain(
                declaredMembers,
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    [Fact]
    public void DiagnosticKeepsOnlyNearestRawCandidateAndBoundedCounts()
    {
        var snapshot = Snapshot(
            baseColor: DimColor(),
            nightVision: NightVision(active: false),
            candidates: new[] { Candidate("far", 500d), Candidate("near", 100d) },
            currentLightSourceCount: 3,
            sharedLightSourceCount: 1,
            revision: 44
        );
        var result = Classifier.Classify(snapshot);
        var diagnostic = EnvironmentLightDiagnostic.From(snapshot, result);

        Assert.Equal("near", diagnostic.NearestCandidateId);
        Assert.Equal(100d, diagnostic.NearestCandidateDistanceWorldPixels);
        Assert.Equal(2f, diagnostic.NearestCandidateRawRadius);
        Assert.Equal(3, diagnostic.CurrentLightSourceCount);
        Assert.Equal(1, diagnostic.SharedLightSourceCount);
        Assert.Equal(2, diagnostic.UniqueCandidateCount);
        Assert.Equal(EnvironmentLightCandidateOrigin.CurrentOnly, diagnostic.NearestCandidateOrigin);
        Assert.Equal(EnvironmentLightCandidateContext.None, diagnostic.NearestCandidateLightContext);
        Assert.Equal(0, diagnostic.NearestCandidateAttachedPlayerId);
        Assert.True(diagnostic.IsNearestCandidateDrawEligible);
        Assert.Equal("vanilla.farm", diagnostic.LocationRuleId);
        Assert.Equal(3, diagnostic.LocationRuleContractVersion);
        Assert.False(diagnostic.PitchBlackAuthorized);
        Assert.Equal(44, diagnostic.Revision);
    }

    [Fact]
    public void CacheUsesOwnerScreenLocationRevisionAndFifteenTickCadence()
    {
        var cache = new EnvironmentLightCache();
        var key = Key("1", 0, "Farm", 10);
        var (result, diagnostic) = Entry(revision: 77, tick: 100);
        cache.Store(key, 77, 100, result, diagnostic);

        Assert.True(cache.TryGetFresh(key, 114, out var fresh));
        Assert.Equal(77, fresh.LightRevision);
        Assert.False(cache.TryGetFresh(key, 115, out _));
        Assert.False(cache.TryGetFresh(Key("1", 0, "Farm", 11), 101, out _));
        Assert.False(cache.TryGetFresh(Key("1", 1, "Farm", 10), 101, out _));

        var (changedResult, changedDiagnostic) = Entry(revision: 78, tick: 115);
        cache.Store(key, 78, 115, changedResult, changedDiagnostic);
        Assert.True(cache.TryGetFresh(key, 115, out var changed));
        Assert.Equal(78, changed.LightRevision);
        Assert.Equal(15, EnvironmentLightCache.SampleCadenceTicks);
    }

    [Fact]
    public void WarpInvalidationClearsOnlyTheNamedOwnerAndScreen()
    {
        var cache = new EnvironmentLightCache();
        var (result, diagnostic) = Entry(1, 100);
        cache.Store(Key("1", 0, "Farm", 1), 1, 100, result, diagnostic);
        cache.Store(Key("1", 1, "Town", 2), 1, 100, result, diagnostic);
        cache.Store(Key("2", 0, "Farm", 1), 1, 100, result, diagnostic);

        Assert.Equal(1, cache.Invalidate("1", 0));
        Assert.False(cache.TryGetFresh(Key("1", 0, "Farm", 1), 101, out _));
        Assert.True(cache.TryGetFresh(Key("1", 1, "Town", 2), 101, out _));
        Assert.True(cache.TryGetFresh(Key("2", 0, "Farm", 1), 101, out _));
    }

    [Fact]
    public void ReturnedToTitleClearRemovesEveryCachedOwner()
    {
        var cache = new EnvironmentLightCache();
        var (result, diagnostic) = Entry(1, 100);
        cache.Store(Key("1", 0, "Farm", 1), 1, 100, result, diagnostic);
        cache.Store(Key("2", 1, "Town", 2), 1, 100, result, diagnostic);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetDiagnostic("1", 0, out _));
    }

    [Fact]
    public void CacheCapacityIsBounded()
    {
        var cache = new EnvironmentLightCache();
        var (result, diagnostic) = Entry(1, 100);
        for (var index = 0; index <= EnvironmentLightCache.MaximumEntries; index++)
        {
            cache.Store(
                Key((index + 1).ToString(), 0, $"Location{index}", index),
                index,
                100 + index,
                result,
                diagnostic
            );
        }

        Assert.Equal(EnvironmentLightCache.MaximumEntries, cache.Count);
        Assert.False(cache.TryGetFresh(Key("1", 0, "Location0", 0), 101, out _));
    }

    [Fact]
    public void SnapshotContractsContainNoStardewWorldReferenceTypes()
    {
        foreach (
            var type in new[]
            {
                typeof(EnvironmentLightSnapshot),
                typeof(EnvironmentLightLocationSnapshot),
                typeof(EnvironmentLightLocationRuleResolution),
                typeof(EnvironmentLightCandidateSnapshot),
                typeof(EnvironmentLightNightVisionSnapshot),
            }
        )
        {
            foreach (
                var property in type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                )
            )
            {
                Assert.DoesNotContain("StardewValley", property.PropertyType.FullName ?? "");
                Assert.DoesNotContain("LightSource", property.PropertyType.FullName ?? "");
                Assert.DoesNotContain("GameLocation", property.PropertyType.FullName ?? "");
            }
        }
    }

    private static EnvironmentLightSnapshot Snapshot(
        EnvironmentLightBaseSource baseSource = EnvironmentLightBaseSource.Outdoor,
        EnvironmentLightColor? baseColor = null,
        bool isDarkOut = false,
        EnvironmentLightCapabilityStatus mineCapability =
            EnvironmentLightCapabilityStatus.Unavailable,
        bool? isMineDarkArea = null,
        EnvironmentLightNightVisionSnapshot? nightVision = null,
        IReadOnlyList<EnvironmentLightCandidateSnapshot>? candidates = null,
        EnvironmentLightCapabilityStatus candidateCollectionStatus =
            EnvironmentLightCapabilityStatus.Available,
        string candidateCollectionReason = "environment-light.candidates-captured",
        int? currentLightSourceCount = null,
        int sharedLightSourceCount = 0,
        long revision = 1,
        EnvironmentLightLocationRuleResolution? locationRule = null
    )
    {
        candidates ??= Array.Empty<EnvironmentLightCandidateSnapshot>();
        locationRule ??= MatchedRule(
            baseSource == EnvironmentLightBaseSource.MineLighting
                ? EnvironmentLightLocationLightProfile.FallbackOnly
                : EnvironmentLightLocationLightProfile.OpaqueWhiteBase
        );
        return new EnvironmentLightSnapshot(
            "1",
            0,
            "Farm",
            10,
            new EnvironmentLightLocationSnapshot(
                baseSource == EnvironmentLightBaseSource.MineLighting
                    ? "StardewValley.Locations.MineShaft"
                    : "StardewValley.Farm",
                "Default",
                "Farm",
                isOutdoors: baseSource != EnvironmentLightBaseSource.MineLighting,
                isTemporary: false,
                isEventActive: false,
                isFestivalActive: false
            ),
            locationRule,
            new EnvironmentLightWorldPoint(0, 0),
            EnvironmentLightCapabilityStatus.Available,
            baseSource,
            baseColor ?? DimColor(),
            EnvironmentLightCapabilityStatus.Available,
            0f,
            isDarkOut,
            mineCapability,
            isMineDarkArea,
            nightVision ?? NightVisionUnavailable(),
            candidateCollectionStatus,
            candidateCollectionReason,
            currentLightSourceCount ?? candidates.Count,
            sharedLightSourceCount,
            candidates,
            600,
            100,
            revision
        );
    }

    private static EnvironmentLightCandidateSnapshot Candidate(
        string id,
        double distance,
        float radius = 2f,
        EnvironmentLightCapabilityStatus status =
            EnvironmentLightCapabilityStatus.Available,
        string reason = "environment-light.candidate-raw-evidence"
    )
    {
        return new EnvironmentLightCandidateSnapshot(
            id,
            status,
            new EnvironmentLightWorldPoint(distance, 0d),
            distance,
            radius,
            new EnvironmentLightColor(10, 20, 30, 255),
            string.Empty,
            reason,
            EnvironmentLightCandidateOrigin.CurrentOnly,
            EnvironmentLightCandidateContext.None,
            AttachedPlayerId: 0,
            EnvironmentLightCapabilityStatus.Available,
            IsDrawEligible: true,
            "environment-light.candidate-draw-eligible"
        );
    }

    private static EnvironmentLightNightVisionSnapshot NightVision(bool active)
    {
        return new EnvironmentLightNightVisionSnapshot(
            EnvironmentLightCapabilityStatus.Available,
            active,
            "environment-light.night-vision-provider-available",
            "1",
            0,
            "Farm",
            10
        );
    }

    private static EnvironmentLightNightVisionSnapshot NightVisionUnavailable()
    {
        return new EnvironmentLightNightVisionSnapshot(
            EnvironmentLightCapabilityStatus.Unavailable,
            null,
            EnvironmentLightReasonIds.NightVisionUnavailable
        );
    }

    private static EnvironmentLightColor White()
    {
        return new EnvironmentLightColor(255, 255, 255, 255);
    }

    private static EnvironmentLightColor DimColor()
    {
        return new EnvironmentLightColor(80, 80, 80, 255);
    }

    private static EnvironmentLightLocationRuleResolution MatchedRule(
        EnvironmentLightLocationLightProfile profile
    )
    {
        return new EnvironmentLightLocationRuleResolution(
            EnvironmentLightLocationRuleStatus.Matched,
            3,
            profile == EnvironmentLightLocationLightProfile.FallbackOnly
                ? "vanilla.mine-shaft"
                : "vanilla.farm",
            profile,
            TwoAmSpecialDeathSafe: false,
            HostileShadowSafe: false,
            JunimoBlessingEligible: profile
                == EnvironmentLightLocationLightProfile.OpaqueWhiteBase,
            EnvironmentLightReasonIds.LocationRuleMatched
        );
    }

    private static DarknessAttackLocationAuthorizationPolicy CreatePolicy()
    {
        return new DarknessAttackLocationAuthorizationPolicy(
            () => new EnvironmentLightJunimoBlessingState(
                IsAvailable: true,
                IsEnabled: false
            )
        );
    }

    private static EnvironmentLightCacheKey Key(
        string playerKey,
        int screenId,
        string location,
        int instance
    )
    {
        return new EnvironmentLightCacheKey(playerKey, screenId, location, instance);
    }

    private static (
        EnvironmentLightResult Result,
        EnvironmentLightDiagnostic Diagnostic
    ) Entry(long revision, long tick)
    {
        var snapshot = Snapshot(revision: revision);
        var result = Classifier.Classify(snapshot);
        var diagnostic = EnvironmentLightDiagnostic.From(snapshot, result);
        return (result, diagnostic);
    }
}
