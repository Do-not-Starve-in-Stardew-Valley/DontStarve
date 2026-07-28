using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class EnvironmentLightClassificationRuleTests
{
    private static readonly EnvironmentLightClassifier Classifier = new();

    private static string ShippedRulesPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data",
            "location-rules.json"
        );

    [Fact]
    public void ShippedVersionTwoCatalogLoadsOnceAndKeepsSafetySemanticsSeparate()
    {
        var load = LoadShipped();

        Assert.True(load.IsAvailable, load.Reason);
        Assert.Equal(2, load.Catalog.ContractVersion);
        Assert.Equal(21, load.Catalog.Count);

        var farm = load.Catalog.Resolve(Location("StardewValley.Farm", "Farm", outdoors: true));
        Assert.Equal(EnvironmentLightLocationRuleStatus.Matched, farm.Status);
        Assert.Equal("vanilla.farm", farm.RuleId);
        Assert.Equal(EnvironmentLightLocationLightProfile.OpaqueWhiteBase, farm.LightProfile);
        Assert.False(farm.TwoAmSpecialDeathSafe);
        Assert.True(farm.DarknessAttackSafe);
        Assert.False(farm.HostileShadowSafe);
        Assert.True(farm.JunimoBlessingEligible);

        var cellar = load.Catalog.Resolve(
            Location("StardewValley.Locations.Cellar", "Cellar")
        );
        Assert.Equal("vanilla.cellar", cellar.RuleId);
        Assert.False(cellar.TwoAmSpecialDeathSafe);
        Assert.False(cellar.DarknessAttackSafe);
        Assert.False(cellar.HostileShadowSafe);
        Assert.False(cellar.JunimoBlessingEligible);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EventOrFestivalNeverInheritsAnOrdinaryFarmRule(bool eventActive, bool festivalActive)
    {
        var resolution = LoadShipped().Catalog.Resolve(
            Location(
                "StardewValley.Farm",
                "Farm",
                outdoors: true,
                eventActive: eventActive,
                festivalActive: festivalActive
            )
        );

        Assert.Equal(EnvironmentLightLocationRuleStatus.Unmatched, resolution.Status);
        Assert.False(resolution.TwoAmSpecialDeathSafe);
        Assert.False(resolution.DarknessAttackSafe);
        Assert.False(resolution.HostileShadowSafe);
        Assert.False(resolution.JunimoBlessingEligible);
    }

    [Fact]
    public void UnknownOrModdedLocationFailsClosedWithoutLocalizedNameMatching()
    {
        var resolution = LoadShipped().Catalog.Resolve(
            Location("Example.Mod.CustomLocation", "Localized Name Must Not Matter")
        );

        Assert.Equal(EnvironmentLightLocationRuleStatus.Unmatched, resolution.Status);
        Assert.Equal(EnvironmentLightLocationRuleIds.Unmatched, resolution.RuleId);
        Assert.Equal(EnvironmentLightLocationLightProfile.FallbackOnly, resolution.LightProfile);
        Assert.False(resolution.TwoAmSpecialDeathSafe);
        Assert.False(resolution.DarknessAttackSafe);
        Assert.False(resolution.HostileShadowSafe);
        Assert.False(resolution.JunimoBlessingEligible);
        Assert.Equal(
            TwoAmSpecialDeathLocationReasonIds.UnmatchedDefaultUnsafe,
            resolution.TwoAmSpecialDeathReason
        );
    }

    [Fact]
    public void CustomFieldsAreExactStructuralEvidenceAndExposeOnlyConfiguredKeys()
    {
        const string json = @"{
          ""SchemaVersion"": 2,
          ""Rules"": [
            {
              ""Id"": ""mod.structured"",
              ""Priority"": 10,
              ""Match"": {
                ""RuntimeType"": ""Example.Mod.Location"",
                ""LocationCustomFields"": { ""Example/Lighting"": ""Windowless"" },
                ""ContextCustomFields"": { ""Example/Context"": ""Underground"" }
              },
              ""LightProfile"": ""FallbackOnly"",
              ""TwoAmSpecialDeathSafe"": false,
              ""DarknessAttackSafe"": true,
              ""HostileShadowSafe"": false,
              ""JunimoBlessingEligible"": false
            }
          ]
        }";
        var load = EnvironmentLightLocationRuleCatalog.Load(json);

        Assert.True(load.IsAvailable, load.Reason);
        Assert.Equal(new[] { "Example/Lighting" }, load.Catalog.LocationCustomFieldKeys);
        Assert.Equal(new[] { "Example/Context" }, load.Catalog.ContextCustomFieldKeys);
        var matched = load.Catalog.Resolve(
            new EnvironmentLightLocationSnapshot(
                "Example.Mod.Location",
                "Example",
                "ModRoom",
                false,
                false,
                false,
                false,
                new Dictionary<string, string> { ["Example/Lighting"] = "Windowless" },
                new Dictionary<string, string> { ["Example/Context"] = "Underground" }
            )
        );
        Assert.True(matched.IsMatched);
        Assert.True(matched.DarknessAttackSafe);

        var wrongCase = load.Catalog.Resolve(
            new EnvironmentLightLocationSnapshot(
                "Example.Mod.Location",
                "Example",
                "ModRoom",
                false,
                false,
                false,
                false,
                new Dictionary<string, string> { ["Example/Lighting"] = "windowless" },
                new Dictionary<string, string> { ["Example/Context"] = "Underground" }
            )
        );
        Assert.Equal(EnvironmentLightLocationRuleStatus.Unmatched, wrongCase.Status);
    }

    [Fact]
    public void HighestPriorityWinsButEqualPriorityMatchesAreAmbiguous()
    {
        var higher = EnvironmentLightLocationRuleCatalog.Load(
            CatalogJson(("high", 20), ("low", 10))
        );
        Assert.Equal("high", higher.Catalog.Resolve(Location("Example.Location", "Room")).RuleId);

        var equal = EnvironmentLightLocationRuleCatalog.Load(
            CatalogJson(("a", 20), ("b", 20))
        );
        var ambiguous = equal.Catalog.Resolve(Location("Example.Location", "Room"));
        Assert.Equal(EnvironmentLightLocationRuleStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(EnvironmentLightLocationRuleIds.Ambiguous, ambiguous.RuleId);
        Assert.False(ambiguous.TwoAmSpecialDeathSafe);
        Assert.False(ambiguous.DarknessAttackSafe);
        Assert.False(ambiguous.HostileShadowSafe);
        Assert.False(ambiguous.JunimoBlessingEligible);
    }

    [Fact]
    public void AggregateSafetyFieldIsRejected()
    {
        var json = string.Concat(
            "{\"SchemaVersion\":2,\"Rules\":[{",
            "\"Id\":\"bad\",\"Priority\":1,",
            "\"Match\":{\"RuntimeType\":\"Example.Location\"},",
            "\"LightProfile\":\"FallbackOnly\",",
            "\"TwoAmSpecialDeathSafe\":false,\"DarknessAttackSafe\":false,",
            "\"HostileShadowSafe\":false,\"JunimoBlessingEligible\":false,",
            "\"Safe\":true",
            "}]}"
        );

        var load = EnvironmentLightLocationRuleCatalog.Load(json);

        Assert.False(load.IsAvailable);
        Assert.Equal("environment-light.location-rule-invalid", load.Reason);
    }

    [Fact]
    public void OpaqueWhiteIsConfirmedOnlyForTheMatchedOpaqueWhiteProfile()
    {
        var farmRule = LoadShipped().Catalog.Resolve(
            Location("StardewValley.Farm", "Farm", outdoors: true)
        );
        var farm = Classifier.Classify(Snapshot(farmRule, White()));
        Assert.Equal(EnvironmentLightLevel.Lit, farm.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, farm.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.BaseWhiteConfirmed, farm.Reason);
        Assert.False(farm.PitchBlackAuthorized);

        var mineRule = LoadShipped().Catalog.Resolve(
            Location("StardewValley.Locations.MineShaft", "UndergroundMine")
        );
        var mine = Classifier.Classify(
            Snapshot(
                mineRule,
                White(),
                baseSource: EnvironmentLightBaseSource.MineLighting,
                mineCapability: EnvironmentLightCapabilityStatus.Available,
                isMineDarkArea: false,
                location: Location("StardewValley.Locations.MineShaft", "UndergroundMine"),
                locationName: "UndergroundMine"
            )
        );
        Assert.Equal(EnvironmentLightLevel.Dim, mine.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, mine.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.LocationProfileFallbackOnly, mine.Reason);
        Assert.False(mine.PitchBlackAuthorized);
    }

    [Fact]
    public void ActiveNightVisionRequiresExactOwnerScreenAndLocationIdentity()
    {
        var rule = LoadShipped().Catalog.Resolve(
            Location("StardewValley.Farm", "Farm", outdoors: true)
        );
        var matching = Classifier.Classify(
            Snapshot(rule, Dim(), nightVision: NightVision("1", 0, "Farm", 10, true))
        );
        Assert.Equal(EnvironmentLightLevel.Lit, matching.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, matching.EvidenceStatus);
        Assert.False(matching.PitchBlackAuthorized);

        var mismatched = Classifier.Classify(
            Snapshot(rule, Dim(), nightVision: NightVision("2", 0, "Farm", 10, true))
        );
        Assert.Equal(EnvironmentLightLevel.Dim, mismatched.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, mismatched.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.NightVisionOwnerContextMismatch, mismatched.Reason);
        Assert.False(mismatched.PitchBlackAuthorized);
    }

    [Theory]
    [InlineData(32d)]
    [InlineData(8192d)]
    public void DrawEligibleNearOrFarLightNeverTurnsRawRadiusIntoCoverage(double distance)
    {
        var rule = LoadShipped().Catalog.Resolve(
            Location("StardewValley.Farm", "Farm", outdoors: true)
        );
        var result = Classifier.Classify(
            Snapshot(rule, Dim(), candidates: new[] { Candidate(distance, drawEligible: true) })
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.LocalLightCoverageUnverified, result.Reason);
        Assert.False(result.PitchBlackAuthorized);
    }

    [Fact]
    public void DrawIneligibleLightIsDiagnosticEvidenceButNotStandingPixelCoverage()
    {
        var rule = LoadShipped().Catalog.Resolve(
            Location("StardewValley.Farm", "Farm", outdoors: true)
        );
        var result = Classifier.Classify(
            Snapshot(rule, Dim(), candidates: new[] { Candidate(32d, drawEligible: false) })
        );

        Assert.Equal(EnvironmentLightReasonIds.LocalLightDrawIneligible, result.Reason);
        Assert.False(result.PitchBlackAuthorized);
    }

    [Fact]
    public void UnmatchedLocationAlwaysStaysFallbackAndUnauthorized()
    {
        var unknownLocation = Location("Example.Mod.Location", "ModRoom");
        var rule = LoadShipped().Catalog.Resolve(unknownLocation);
        var result = Classifier.Classify(
            Snapshot(rule, White(), location: unknownLocation, locationName: "ModRoom")
        );

        Assert.Equal(EnvironmentLightLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.LocationRuleUnmatched, result.Reason);
        Assert.False(result.PitchBlackAuthorized);
    }

    [Fact]
    public void PitchBlackAuthorizationCannotBeAttachedToAnyOtherResultShape()
    {
        Assert.Throws<ArgumentException>(
            () => new EnvironmentLightResult(
                EnvironmentLightLevel.Dim,
                EnvironmentLightEvidenceStatus.Confirmed,
                "test.invalid-authorization",
                600,
                pitchBlackAuthorized: true
            )
        );
        Assert.Throws<ArgumentException>(
            () => new EnvironmentLightResult(
                EnvironmentLightLevel.PitchBlack,
                EnvironmentLightEvidenceStatus.Fallback,
                "test.invalid-authorization",
                600,
                pitchBlackAuthorized: true
            )
        );
    }

    private static EnvironmentLightLocationRuleLoadResult LoadShipped()
    {
        return EnvironmentLightLocationRuleCatalog.Load(File.ReadAllText(ShippedRulesPath));
    }

    private static EnvironmentLightLocationSnapshot Location(
        string runtimeType,
        string internalName,
        bool outdoors = false,
        bool eventActive = false,
        bool festivalActive = false,
        string contextId = "Default",
        string parentBuildingType = ""
    )
    {
        return new EnvironmentLightLocationSnapshot(
            runtimeType,
            contextId,
            internalName,
            outdoors,
            isTemporary: false,
            eventActive,
            festivalActive,
            parentBuildingType: parentBuildingType
        );
    }

    private static EnvironmentLightSnapshot Snapshot(
        EnvironmentLightLocationRuleResolution rule,
        EnvironmentLightColor baseColor,
        EnvironmentLightBaseSource baseSource = EnvironmentLightBaseSource.Outdoor,
        EnvironmentLightCapabilityStatus mineCapability = EnvironmentLightCapabilityStatus.Unavailable,
        bool? isMineDarkArea = null,
        EnvironmentLightNightVisionSnapshot? nightVision = null,
        IReadOnlyList<EnvironmentLightCandidateSnapshot>? candidates = null,
        EnvironmentLightLocationSnapshot? location = null,
        string locationName = "Farm"
    )
    {
        candidates ??= Array.Empty<EnvironmentLightCandidateSnapshot>();
        location ??= Location("StardewValley.Farm", locationName, outdoors: true);
        return new EnvironmentLightSnapshot(
            "1",
            0,
            locationName,
            10,
            location,
            rule,
            new EnvironmentLightWorldPoint(64d, 64d),
            EnvironmentLightCapabilityStatus.Available,
            baseSource,
            baseColor,
            EnvironmentLightCapabilityStatus.Available,
            0f,
            isDarkOut: false,
            mineCapability,
            isMineDarkArea,
            nightVision ?? new EnvironmentLightNightVisionSnapshot(
                EnvironmentLightCapabilityStatus.Unavailable,
                null,
                EnvironmentLightReasonIds.NightVisionUnavailable
            ),
            EnvironmentLightCapabilityStatus.Available,
            "environment-light.candidates-captured",
            candidates.Count,
            0,
            candidates,
            600,
            100,
            1
        );
    }

    private static EnvironmentLightCandidateSnapshot Candidate(
        double distance,
        bool drawEligible
    )
    {
        return new EnvironmentLightCandidateSnapshot(
            "light",
            EnvironmentLightCapabilityStatus.Available,
            new EnvironmentLightWorldPoint(64d + distance, 64d),
            distance,
            2f,
            new EnvironmentLightColor(10, 20, 30, 255),
            string.Empty,
            "environment-light.candidate-raw-evidence",
            EnvironmentLightCandidateOrigin.CurrentOnly,
            EnvironmentLightCandidateContext.None,
            0,
            EnvironmentLightCapabilityStatus.Available,
            drawEligible,
            drawEligible
                ? "environment-light.candidate-draw-eligible"
                : "environment-light.candidate-draw-ineligible"
        );
    }

    private static EnvironmentLightNightVisionSnapshot NightVision(
        string playerKey,
        int screenId,
        string location,
        long locationInstanceId,
        bool active
    )
    {
        return new EnvironmentLightNightVisionSnapshot(
            EnvironmentLightCapabilityStatus.Available,
            active,
            "environment-light.night-vision-provider-available",
            playerKey,
            screenId,
            location,
            locationInstanceId
        );
    }

    private static string CatalogJson(params (string Id, int Priority)[] rules)
    {
        var entries = string.Join(
            ",",
            rules.Select(
                rule => string.Concat(
                    "{\"Id\":\"",
                    rule.Id,
                    "\",\"Priority\":",
                    rule.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ",\"Match\":{\"RuntimeType\":\"Example.Location\"},",
                    "\"LightProfile\":\"FallbackOnly\",",
                    "\"TwoAmSpecialDeathSafe\":false,\"DarknessAttackSafe\":false,",
                    "\"HostileShadowSafe\":false,\"JunimoBlessingEligible\":false}"
                )
            )
        );
        return "{\"SchemaVersion\":2,\"Rules\":[" + entries + "]}";
    }

    private static EnvironmentLightColor White() => new(255, 255, 255, 255);
    private static EnvironmentLightColor Dim() => new(80, 80, 80, 255);
}
