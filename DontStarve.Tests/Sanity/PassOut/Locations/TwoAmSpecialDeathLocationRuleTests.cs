using System.Reflection;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut.Locations;

public sealed class TwoAmSpecialDeathLocationRuleTests
{
    private static string ShippedRulesPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data",
            "location-rules.json"
        );

    [Theory]
    [InlineData("StardewValley.Locations.FarmHouse", "Default", "FarmHouse", "", "vanilla.farm-house", true)]
    [InlineData("StardewValley.Locations.Cabin", "Default", "Cabin0001", "Cabin", "vanilla.cabin", false)]
    [InlineData("StardewValley.Locations.IslandFarmHouse", "Island", "IslandFarmHouse", "", "vanilla.island-farm-house", true)]
    [InlineData("StardewValley.Shed", "Default", "Shed0001", "Shed", "vanilla.shed", false)]
    [InlineData("StardewValley.AnimalHouse", "Default", "Coop0001", "Coop", "vanilla.coop", false)]
    [InlineData("StardewValley.AnimalHouse", "Default", "Barn0001", "Barn", "vanilla.barn", false)]
    [InlineData("StardewValley.SlimeHutch", "Default", "SlimeHutch0001", "Slime Hutch", "vanilla.slime-hutch", false)]
    [InlineData("StardewValley.GameLocation", "Default", "Greenhouse", "", "vanilla.greenhouse", false)]
    public void EveryRequiredVanillaLocationIsTwoAmSafeAndOtherSemanticsStayIndependent(
        string runtimeType,
        string contextId,
        string internalName,
        string parentBuildingType,
        string expectedRuleId,
        bool junimoEligible
    )
    {
        var resolution = LoadShipped().Catalog.Resolve(
            Location(runtimeType, contextId, internalName, parentBuildingType)
        );

        Assert.Equal(EnvironmentLightLocationRuleStatus.Matched, resolution.Status);
        Assert.Equal(expectedRuleId, resolution.RuleId);
        Assert.True(resolution.TwoAmSpecialDeathSafe);
        Assert.False(resolution.DarknessAttackSafe);
        Assert.False(resolution.HostileShadowSafe);
        Assert.Equal(junimoEligible, resolution.JunimoBlessingEligible);
        Assert.Equal(
            TwoAmSpecialDeathLocationReasonIds.SafeRuleMatched,
            resolution.TwoAmSpecialDeathReason
        );
    }

    [Theory]
    [InlineData("StardewValley.Locations.Cabin", "Cabin0001", "Log Cabin", "vanilla.cabin.log")]
    [InlineData("StardewValley.Locations.Cabin", "Cabin0001", "Plank Cabin", "vanilla.cabin.plank")]
    [InlineData("StardewValley.Locations.Cabin", "Cabin0001", "Stone Cabin", "vanilla.cabin.stone")]
    [InlineData("StardewValley.Shed", "Shed0001", "Big Shed", "vanilla.big-shed")]
    [InlineData("StardewValley.AnimalHouse", "Coop0001", "Big Coop", "vanilla.big-coop")]
    [InlineData("StardewValley.AnimalHouse", "Coop0001", "Deluxe Coop", "vanilla.deluxe-coop")]
    [InlineData("StardewValley.AnimalHouse", "Barn0001", "Big Barn", "vanilla.big-barn")]
    [InlineData("StardewValley.AnimalHouse", "Barn0001", "Deluxe Barn", "vanilla.deluxe-barn")]
    public void VanillaCabinAndBuildingUpgradeIdsRemainExplicitlyWhitelisted(
        string runtimeType,
        string internalName,
        string parentBuildingType,
        string expectedRuleId
    )
    {
        var resolution = LoadShipped().Catalog.Resolve(
            Location(runtimeType, "Default", internalName, parentBuildingType)
        );

        Assert.Equal(expectedRuleId, resolution.RuleId);
        Assert.True(resolution.TwoAmSpecialDeathSafe);
    }

    [Theory]
    [InlineData("StardewValley.Locations.FarmCave", "Default", "FarmCave", "")]
    [InlineData("StardewValley.GameLocation", "Default", "Hospital", "")]
    [InlineData("Example.Mod.CustomLocation", "Example.Mod", "UnknownRoom", "")]
    [InlineData("Example.Mod.GreenhouseLocation", "Default", "Greenhouse", "")]
    [InlineData("StardewValley.AnimalHouse", "Default", "SVE_PremiumBarn0001", "FlashShifter.StardewValleyExpandedCP_PremiumBarn")]
    public void FarmCaveClinicAndUnknownOrModdedLocationsFailClosedWithAStableReason(
        string runtimeType,
        string contextId,
        string internalName,
        string parentBuildingType
    )
    {
        var resolution = LoadShipped().Catalog.Resolve(
            Location(runtimeType, contextId, internalName, parentBuildingType)
        );

        Assert.Equal(EnvironmentLightLocationRuleStatus.Unmatched, resolution.Status);
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
    public void LocalizedDisplayNameIsNotAMatcherInput()
    {
        var constructor = Assert.Single(
            typeof(EnvironmentLightLocationSnapshot).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic
            )
        );

        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.Name?.Contains("display", StringComparison.OrdinalIgnoreCase) == true
        );
    }

    [Fact]
    public void MalformedJsonFailsClosedWithoutProducingAUsableCatalog()
    {
        var load = EnvironmentLightLocationRuleCatalog.Load("{not-json");

        Assert.False(load.IsAvailable);
        Assert.Equal("environment-light.location-rules-json-malformed", load.Reason);
        var resolution = load.Catalog.Resolve(
            Location("StardewValley.Locations.FarmHouse", "Default", "FarmHouse", "")
        );
        Assert.Equal(EnvironmentLightLocationRuleStatus.Unavailable, resolution.Status);
        Assert.False(resolution.TwoAmSpecialDeathSafe);
        Assert.Equal(
            TwoAmSpecialDeathLocationReasonIds.RulesUnavailableDefaultUnsafe,
            resolution.TwoAmSpecialDeathReason
        );
    }

    [Fact]
    public void DuplicateRuleIdsInvalidateTheWholeCatalog()
    {
        var load = EnvironmentLightLocationRuleCatalog.Load(
            CatalogJson(RuleJson("duplicate", 20, true), RuleJson("duplicate", 10, false))
        );

        Assert.False(load.IsAvailable);
        Assert.Equal("environment-light.location-rule-id-duplicated", load.Reason);
    }

    [Fact]
    public void HigherPriorityWinsButEqualPriorityConflictIsAmbiguousAndUnsafe()
    {
        var prioritized = EnvironmentLightLocationRuleCatalog.Load(
            CatalogJson(RuleJson("high", 20, true), RuleJson("low", 10, false))
        );
        var winner = prioritized.Catalog.Resolve(
            Location("Example.Location", "Default", "Room", "")
        );
        Assert.Equal("high", winner.RuleId);
        Assert.True(winner.TwoAmSpecialDeathSafe);

        var equal = EnvironmentLightLocationRuleCatalog.Load(
            CatalogJson(RuleJson("a", 20, true), RuleJson("b", 20, false))
        );
        var ambiguous = equal.Catalog.Resolve(
            Location("Example.Location", "Default", "Room", "")
        );
        Assert.Equal(EnvironmentLightLocationRuleStatus.Ambiguous, ambiguous.Status);
        Assert.False(ambiguous.TwoAmSpecialDeathSafe);
        Assert.Equal(
            TwoAmSpecialDeathLocationReasonIds.AmbiguousDefaultUnsafe,
            ambiguous.TwoAmSpecialDeathReason
        );
    }

    [Fact]
    public void VersionOneAndMissingIndependentSemanticFieldsAreRejected()
    {
        var versionOne = EnvironmentLightLocationRuleCatalog.Load(
            "{\"SchemaVersion\":1,\"Rules\":[]}"
        );
        Assert.False(versionOne.IsAvailable);
        Assert.Equal("environment-light.location-rules-version-unsupported", versionOne.Reason);

        const string missingHostile =
            "{\"SchemaVersion\":2,\"Rules\":[{\"Id\":\"bad\",\"Priority\":1,"
            + "\"Match\":{\"RuntimeType\":\"Example.Location\"},"
            + "\"LightProfile\":\"FallbackOnly\",\"TwoAmSpecialDeathSafe\":false,"
            + "\"DarknessAttackSafe\":false,\"JunimoBlessingEligible\":false}]}";
        var missing = EnvironmentLightLocationRuleCatalog.Load(missingHostile);
        Assert.False(missing.IsAvailable);
        Assert.Equal("environment-light.location-rule-invalid", missing.Reason);
    }

    [Fact]
    public void MatchedNonWhitelistRuleHasItsOwnStableUnsafeReason()
    {
        var farm = LoadShipped().Catalog.Resolve(
            Location("StardewValley.Farm", "Default", "Farm", "", outdoors: true)
        );

        Assert.Equal("vanilla.farm", farm.RuleId);
        Assert.False(farm.TwoAmSpecialDeathSafe);
        Assert.Equal(
            TwoAmSpecialDeathLocationReasonIds.UnsafeRuleMatched,
            farm.TwoAmSpecialDeathReason
        );
    }

    private static EnvironmentLightLocationRuleLoadResult LoadShipped()
    {
        return EnvironmentLightLocationRuleCatalog.Load(File.ReadAllText(ShippedRulesPath));
    }

    private static EnvironmentLightLocationSnapshot Location(
        string runtimeType,
        string contextId,
        string internalName,
        string parentBuildingType,
        bool outdoors = false
    )
    {
        return new EnvironmentLightLocationSnapshot(
            runtimeType,
            contextId,
            internalName,
            outdoors,
            isTemporary: false,
            isEventActive: false,
            isFestivalActive: false,
            parentBuildingType: parentBuildingType
        );
    }

    private static string CatalogJson(params string[] rules)
    {
        return "{\"SchemaVersion\":2,\"Rules\":[" + string.Join(",", rules) + "]}";
    }

    private static string RuleJson(string id, int priority, bool twoAmSafe)
    {
        return string.Concat(
            "{\"Id\":\"",
            id,
            "\",\"Priority\":",
            priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",\"Match\":{\"RuntimeType\":\"Example.Location\"},",
            "\"LightProfile\":\"FallbackOnly\",\"TwoAmSpecialDeathSafe\":",
            twoAmSafe ? "true" : "false",
            ",\"DarknessAttackSafe\":false,\"HostileShadowSafe\":false,",
            "\"JunimoBlessingEligible\":false}"
        );
    }
}
