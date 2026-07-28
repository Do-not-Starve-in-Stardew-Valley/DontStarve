using DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.Forage;

public sealed class ForageReplacementFactFreezeTests
{
    private static string ShippedCatalogPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data",
            "forage-replacements.json"
        );

    [Fact]
    public void ShippedVersionTwoCatalogHasOneDefaultReachableMapping()
    {
        var load = LoadShipped();

        Assert.True(load.IsAvailable, load.Reason);
        Assert.Equal(2, load.Catalog.SchemaVersion);
        Assert.Equal(1, load.Catalog.Revision);
        Assert.Equal(3, load.Catalog.Mappings.Count);
        var enabled = Assert.Single(load.Catalog.Mappings, mapping => mapping.Enabled);
        Assert.Equal("rabbit-foot-to-void-essence", enabled.Id);
        Assert.Equal("(O)446", enabled.SourceQualifiedItemId);
        Assert.Equal("(O)769", enabled.TargetQualifiedItemId);
        Assert.Equal(new[] { "Deluxe Coop" }, enabled.LocationAllowlist);
        Assert.Equal(
            new[] { ForagePickupContextIds.NormalDirectObjectPickup },
            enabled.ContextAllowlist
        );
    }

    [Fact]
    public void MissingBeardTargetBlocksOnlyThatMapping()
    {
        var catalog = LoadShipped().Catalog;

        var bunnyPuff = Assert.IsType<ForageReplacementMapping>(
            catalog.FindBySource("(O)DS_Bunny_Puff")
        );
        Assert.False(bunnyPuff.Enabled);
        Assert.Null(bunnyPuff.TargetQualifiedItemId);
        Assert.True(catalog.FindBySource("(O)446")?.Enabled);
        Assert.Equal("(O)769", catalog.FindBySource("(O)446")?.TargetQualifiedItemId);
    }

    [Fact]
    public void ShippedEvidenceHasNoReferencesOrTestPackageRuntimeDependency()
    {
        var json = File.ReadAllText(ShippedCatalogPath);

        Assert.DoesNotContain("references/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("references\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TestPackage", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    [InlineData("{\"SchemaVersion\":1,\"ContractId\":\"sanity.direct-pickup-replacements.v2\",\"Revision\":1,\"Mappings\":[]}")]
    [InlineData("{\"SchemaVersion\":2,\"ContractId\":\"wrong\",\"Revision\":1,\"Mappings\":[]}")]
    [InlineData("{\"SchemaVersion\":2,\"ContractId\":\"sanity.direct-pickup-replacements.v2\",\"Revision\":0,\"Mappings\":[]}")]
    public void EmptyMalformedOrUnsupportedCatalogFailsClosed(string json)
    {
        var load = ForageReplacementCatalog.Load(json);

        Assert.False(load.IsAvailable);
        Assert.False(load.Catalog.IsAvailable);
    }

    [Fact]
    public void UnknownPropertiesAndDuplicateSourcesInvalidateWholeCatalog()
    {
        var unknownProperty = ForageReplacementCatalog.Load(
            EnabledCatalogJson(",\"Unexpected\":true")
        );
        Assert.False(unknownProperty.IsAvailable);
        Assert.Equal(ForageReplacementReasonIds.MappingInvalid, unknownProperty.Reason);

        var row = EnabledMappingJson();
        var duplicated = ForageReplacementCatalog.Load(
            "{\"SchemaVersion\":2,\"ContractId\":\"sanity.direct-pickup-replacements.v2\","
                + "\"Revision\":1,\"Mappings\":["
                + row
                + ","
                + row.Replace("\"enabled-test\"", "\"other-id\"", StringComparison.Ordinal)
                + "]}"
        );
        Assert.False(duplicated.IsAvailable);
        Assert.Equal(ForageReplacementReasonIds.MappingSourceDuplicated, duplicated.Reason);
    }

    [Theory]
    [InlineData("null", "[\"Farm\"]", "[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"]")]
    [InlineData("\"(O)2\"", "[]", "[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"]")]
    [InlineData("\"(O)2\"", "[\"Farm\"]", "[]")]
    [InlineData("\"(O)2\"", "[\"*\"]", "[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"]")]
    [InlineData("\"(O)2\"", "[\"Farm\"]", "[\"*\"]")]
    public void EnabledRowRequiresTargetAndExactAllowlists(
        string target,
        string locations,
        string contexts
    )
    {
        var load = ForageReplacementCatalog.Load(
            EnabledCatalogJson(string.Empty, target, locations, contexts)
        );

        Assert.False(load.IsAvailable);
        Assert.Equal(ForageReplacementReasonIds.MappingInvalid, load.Reason);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("(BC)1")]
    [InlineData("(O)")]
    [InlineData("(O)bad id")]
    [InlineData("(O)(bad)")]
    public void SourceAndTargetMustUseObjectQualifiedIds(string invalidId)
    {
        Assert.False(ForageReplacementCatalog.IsObjectQualifiedItemId(invalidId));
    }

    [Fact]
    public void ExactDirectPickupFactsPassWithoutProvenanceGuessing()
    {
        var result = ForageReplacementEligibilityGate.Evaluate(
            LoadEnabledCatalog(),
            EligibleFacts()
        );

        Assert.True(result.IsEligible, result.Reason);
        Assert.Equal("enabled-test", result.Mapping?.Id);
    }

    [Theory]
    [InlineData("identity", ForageReplacementReasonIds.ObjectIdentityMismatch)]
    [InlineData("fingerprint", ForageReplacementReasonIds.ObjectFingerprintUnavailable)]
    [InlineData("branch", ForageReplacementReasonIds.DirectPickupBranchRequired)]
    [InlineData("structure", ForageReplacementReasonIds.SpawnedOrErrorObjectRequired)]
    [InlineData("host", ForageReplacementReasonIds.HostAuthorityRequired)]
    [InlineData("picker", ForageReplacementReasonIds.PickerInvalid)]
    [InlineData("target", ForageReplacementReasonIds.TargetUnavailable)]
    public void EveryRequiredHostFactFailsClosed(string missing, string expected)
    {
        var facts = EligibleFacts();
        facts = missing switch
        {
            "identity" => facts with { ObjectIdentityMatchesLocationTile = false },
            "fingerprint" => facts with { ObjectFingerprint = 0 },
            "branch" => facts with { IsDirectPickupBranch = false },
            "structure" => facts with { IsSpawnedObjectOrErrorItem = false },
            "host" => facts with { IsHostAuthoritative = false },
            "picker" => facts with { PickerMatchesRequest = false },
            "target" => facts with { TargetQualifiedItemIdExists = false },
            _ => throw new InvalidOperationException(missing),
        };

        var result = ForageReplacementEligibilityGate.Evaluate(
            LoadEnabledCatalog(),
            facts
        );

        Assert.False(result.IsEligible);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void LocationContextAndSourceAllowlistsAreOrdinal()
    {
        var catalog = LoadEnabledCatalog();

        Assert.Equal(
            ForageReplacementReasonIds.LocationNotAllowlisted,
            ForageReplacementEligibilityGate
                .Evaluate(catalog, EligibleFacts() with { LocationId = "farm" })
                .Reason
        );
        Assert.Equal(
            ForageReplacementReasonIds.ContextNotAllowlisted,
            ForageReplacementEligibilityGate
                .Evaluate(catalog, EligibleFacts() with { ContextId = "other" })
                .Reason
        );
        Assert.Equal(
            ForageReplacementReasonIds.MappingNotAllowlisted,
            ForageReplacementEligibilityGate
                .Evaluate(
                    catalog,
                    EligibleFacts() with { SourceQualifiedItemId = "(O)3" }
                )
                .Reason
        );
    }

    private static ForageReplacementCatalogLoadResult LoadShipped()
    {
        return ForageReplacementCatalog.Load(File.ReadAllText(ShippedCatalogPath));
    }

    private static ForageReplacementCatalog LoadEnabledCatalog()
    {
        var load = ForageReplacementCatalog.Load(EnabledCatalogJson());
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static ForagePickupFactSnapshot EligibleFacts()
    {
        return new ForagePickupFactSnapshot(
            PickerMultiplayerId: 101,
            PickerMatchesRequest: true,
            IsHostAuthoritative: true,
            LocationId: "Farm",
            LocationInstanceId: "Farm",
            TileX: 12,
            TileY: 34,
            ContextId: ForagePickupContextIds.NormalDirectObjectPickup,
            SourceQualifiedItemId: "(O)1",
            ObjectIdentityMatchesLocationTile: true,
            ObjectFingerprint: 7,
            IsDirectPickupBranch: true,
            IsSpawnedObjectOrErrorItem: true,
            TargetQualifiedItemIdExists: true
        );
    }

    private static string EnabledCatalogJson(
        string extraProperty = "",
        string target = "\"(O)2\"",
        string locations = "[\"Farm\"]",
        string contexts = "[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"]"
    )
    {
        return "{\"SchemaVersion\":2,\"ContractId\":\"sanity.direct-pickup-replacements.v2\","
            + "\"Revision\":1,\"Mappings\":["
            + EnabledMappingJson(extraProperty, target, locations, contexts)
            + "]}";
    }

    private static string EnabledMappingJson(
        string extraProperty = "",
        string target = "\"(O)2\"",
        string locations = "[\"Farm\"]",
        string contexts = "[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"]"
    )
    {
        return "{\"Id\":\"enabled-test\",\"SourceQualifiedItemId\":\"(O)1\","
            + "\"TargetQualifiedItemId\":"
            + target
            + ",\"Enabled\":true,\"LocationAllowlist\":"
            + locations
            + ",\"ContextAllowlist\":"
            + contexts
            + ",\"Evidence\":[\"synthetic-test\"],\"Reason\":\"synthetic-enabled\""
            + extraProperty
            + "}";
    }
}
