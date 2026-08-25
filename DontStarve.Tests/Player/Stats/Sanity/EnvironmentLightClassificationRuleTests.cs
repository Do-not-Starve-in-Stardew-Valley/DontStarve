using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class EnvironmentLightClassificationRuleTests
{
    private static readonly EnvironmentLightClassifier Classifier = new(CreatePolicy());

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
    public void ShippedVersionFourCatalogLoadsOnceAndKeepsIndependentSemanticsSeparate()
    {
        var load = LoadShipped();

        Assert.True(load.IsAvailable, load.Reason);
        Assert.Equal(4, load.Catalog.ContractVersion);
        Assert.Equal(44, load.Catalog.Count);

        var farm = load.Catalog.Resolve(Location("StardewValley.Farm", "Farm", outdoors: true));
        Assert.Equal(EnvironmentLightLocationRuleStatus.Matched, farm.Status);
        Assert.Equal("vanilla.farm", farm.RuleId);
        Assert.Equal(EnvironmentLightLocationLightProfile.OpaqueWhiteBase, farm.LightProfile);
        Assert.False(farm.TwoAmSpecialDeathSafe);
        Assert.False(farm.HostileShadowSafe);
        Assert.True(farm.JunimoBlessingEligible);
        Assert.Equal(NaturalDarknessProfile.NightThreeSeconds, farm.NaturalDarknessProfile);

        var cellar = load.Catalog.Resolve(
            Location("StardewValley.Locations.Cellar", "Cellar")
        );
        Assert.Equal("vanilla.cellar", cellar.RuleId);
        Assert.False(cellar.TwoAmSpecialDeathSafe);
        Assert.False(cellar.HostileShadowSafe);
        Assert.True(cellar.JunimoBlessingEligible);
        Assert.Equal(NaturalDarknessProfile.FullDark, cellar.NaturalDarknessProfile);

        var lewisBasement = load.Catalog.Resolve(
            Location("StardewValley.GameLocation", "LewisBasement")
        );
        Assert.Equal("vanilla.lewis-basement", lewisBasement.RuleId);
        Assert.False(lewisBasement.TwoAmSpecialDeathSafe);
        Assert.False(lewisBasement.HostileShadowSafe);
        Assert.False(lewisBasement.JunimoBlessingEligible);
        Assert.Equal(NaturalDarknessProfile.FullDark, lewisBasement.NaturalDarknessProfile);
    }

    [Theory]
    [InlineData(
        "StardewValley.Locations.Mine",
        "Mine",
        "vanilla.mine-entrance"
    )]
    [InlineData(
        "StardewValley.GameLocation",
        "SkullCave",
        "vanilla.skull-cave-entrance"
    )]
    public void MineEntranceMapsUseTheNightThreeSecondsPlan(
        string runtimeType,
        string internalName,
        string expectedRuleId
    )
    {
        var resolution = LoadShipped().Catalog.Resolve(Location(runtimeType, internalName));

        Assert.Equal(EnvironmentLightLocationRuleStatus.Matched, resolution.Status);
        Assert.Equal(expectedRuleId, resolution.RuleId);
        Assert.Equal(NaturalDarknessProfile.NightThreeSeconds, resolution.NaturalDarknessProfile);
        Assert.False(resolution.TwoAmSpecialDeathSafe);
        Assert.False(resolution.HostileShadowSafe);
        Assert.False(resolution.JunimoBlessingEligible);
    }

    [Theory]
    [InlineData("StardewValley.Farm", "Default", "Farm", true, true)]
    [InlineData("StardewValley.Locations.FarmHouse", "Default", "FarmHouse", false, true)]
    [InlineData("StardewValley.Locations.IslandFarmHouse", "Island", "IslandFarmHouse", false, true)]
    [InlineData("StardewValley.Locations.CommunityCenter", "Default", "CommunityCenter", false, true)]
    [InlineData("StardewValley.Locations.MineShaft", "Default", "UndergroundMine", false, false)]
    [InlineData("StardewValley.Locations.Cellar", "Default", "Cellar", false, true)]
    [InlineData("StardewValley.Locations.FarmCave", "Default", "FarmCave", false, true)]
    [InlineData("StardewValley.Locations.AbandonedJojaMart", "Default", "AbandonedJojaMart", false, true)]
    [InlineData("StardewValley.GameLocation", "Default", "Greenhouse", false, true)]
    public void ShippedJunimoBlessingEligibilityIsExplicitForEveryRequestedCoreInterior(
        string runtimeType,
        string contextId,
        string internalName,
        bool outdoors,
        bool expectedEligible
    )
    {
        var resolution = LoadShipped().Catalog.Resolve(
            Location(runtimeType, internalName, outdoors, contextId: contextId)
        );

        Assert.Equal(expectedEligible, resolution.JunimoBlessingEligible);
    }

    [Fact]
    public void PlayerBuiltInteriorRulesAreJunimoEligibleWithoutChangingTheirOtherSemantics()
    {
        var cabin = LoadShipped().Catalog.Resolve(
            Location(
                "StardewValley.Locations.Cabin",
                "Cabin0001",
                contextId: "Default",
                parentBuildingType: "Cabin"
            )
        );
        var coop = LoadShipped().Catalog.Resolve(
            Location(
                "StardewValley.AnimalHouse",
                "Coop0001",
                contextId: "Default",
                parentBuildingType: "Coop"
            )
        );

        Assert.True(cabin.JunimoBlessingEligible);
        Assert.True(coop.JunimoBlessingEligible);
        Assert.True(cabin.TwoAmSpecialDeathSafe);
        Assert.True(coop.TwoAmSpecialDeathSafe);
        Assert.Equal(NaturalDarknessProfile.NightThreeSeconds, cabin.NaturalDarknessProfile);
        Assert.Equal(NaturalDarknessProfile.NightThreeSeconds, coop.NaturalDarknessProfile);
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
        Assert.False(resolution.HostileShadowSafe);
        Assert.False(resolution.JunimoBlessingEligible);
        Assert.Equal(NaturalDarknessProfile.Unchanged, resolution.NaturalDarknessProfile);
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
          ""SchemaVersion"": 4,
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
              ""NaturalDarknessProfile"": ""FullDark"",
              ""TwoAmSpecialDeathSafe"": false,
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
        Assert.False(ambiguous.HostileShadowSafe);
        Assert.False(ambiguous.JunimoBlessingEligible);
    }

    [Fact]
    public void RetiredDarknessAttackSafeFieldIsRejected()
    {
        var json = string.Concat(
            "{\"SchemaVersion\":4,\"Rules\":[{",
            "\"Id\":\"bad\",\"Priority\":1,",
            "\"Match\":{\"RuntimeType\":\"Example.Location\"},",
            "\"LightProfile\":\"FallbackOnly\",",
            "\"NaturalDarknessProfile\":\"Unchanged\",",
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
    public void UnmatchedLocationWithConfirmedFinalLightmapCanAuthorizeWhenUnprotected()
    {
        var unknownLocation = Location("Example.Mod.Location", "ModRoom");
        var rule = LoadShipped().Catalog.Resolve(unknownLocation);
        var result = Classifier.Classify(
            Snapshot(
                rule,
                White(),
                nightVision: NightVision("1", 0, "ModRoom", 10, false),
                location: unknownLocation,
                locationName: "ModRoom",
                finalVisibility: FinalVisibility("ModRoom", score: 0.10d)
            )
        );

        Assert.Equal(EnvironmentLightLevel.PitchBlack, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, result.EvidenceStatus);
        Assert.Equal(EnvironmentLightReasonIds.FinalVisibilityPitchBlackConfirmed, result.Reason);
        Assert.True(result.PitchBlackAuthorized);
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
        string locationName = "Farm",
        EnvironmentLightFinalVisibilitySnapshot? finalVisibility = null
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
            1,
            finalVisibility
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
                    "\"NaturalDarknessProfile\":\"Unchanged\",",
                    "\"TwoAmSpecialDeathSafe\":false,",
                    "\"HostileShadowSafe\":false,\"JunimoBlessingEligible\":false}"
                )
            )
        );
        return "{\"SchemaVersion\":4,\"Rules\":[" + entries + "]}";
    }

    private static EnvironmentLightFinalVisibilitySnapshot FinalVisibility(
        string locationName,
        double score
    )
    {
        return EnvironmentLightFinalVisibilitySnapshot.Confirmed(
            "1",
            0,
            locationName,
            10,
            score,
            0.5d,
            0.5d,
            0.5d,
            standardLightingDrawn: true,
            rainOverlayApplied: false,
            lightingQuality: 2,
            zoomLevel: 1d,
            useUnscaledLighting: false,
            capturedAtTick: 100,
            rendererRevision: 1,
            "environment-light.final-visibility-owner-foot-lightmap"
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

    private static EnvironmentLightColor White() => new(255, 255, 255, 255);
    private static EnvironmentLightColor Dim() => new(80, 80, 80, 255);
}
