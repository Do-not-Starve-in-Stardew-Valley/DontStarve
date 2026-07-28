using DontStarve.Player.Stats.Sanity.Events;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityEventOverrideCatalogTests
{
    [Fact]
    public void Version_one_catalog_resolves_location_then_global_event_id()
    {
        var result = SanityEventOverrideCatalog.Load(
            "{\"SchemaVersion\":1,\"Overrides\":["
                + "{\"LocationName\":\"Town\",\"EventId\":\"100\",\"Classification\":\"Friendship\"},"
                + "{\"LocationName\":\"*\",\"EventId\":\"200\",\"Classification\":\"NonFriendship\"}]}"
        );

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(2, result.Catalog.Count);
        Assert.True(result.Catalog.TryResolve("Town", "100", out var friendship));
        Assert.Equal(SanityEventClassification.Friendship, friendship);
        Assert.True(result.Catalog.TryResolve("Forest", "200", out var ordinary));
        Assert.Equal(SanityEventClassification.NonFriendship, ordinary);
        Assert.False(result.Catalog.TryResolve("Forest", "100", out _));
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":2,\"Overrides\":[]}", "event-override-schema-version-is-unsupported")]
    [InlineData("{\"SchemaVersion\":1,\"Overrides\":{}}", "event-override-array-is-missing")]
    [InlineData("not-json", "event-override-json-is-malformed")]
    public void Bad_or_unknown_documents_disable_all_overrides(
        string json,
        string expectedReason
    )
    {
        var result = SanityEventOverrideCatalog.Load(json);

        Assert.False(result.IsAvailable);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(0, result.Catalog.Count);
    }

    [Fact]
    public void Duplicate_or_unknown_classification_disables_partial_catalog()
    {
        var duplicate = SanityEventOverrideCatalog.Load(
            "{\"SchemaVersion\":1,\"Overrides\":["
                + "{\"LocationName\":\"Town\",\"EventId\":\"100\",\"Classification\":\"Friendship\"},"
                + "{\"LocationName\":\"Town\",\"EventId\":\"100\",\"Classification\":\"NonFriendship\"}]}"
        );
        var unknown = SanityEventOverrideCatalog.Load(
            "{\"SchemaVersion\":1,\"Overrides\":["
                + "{\"LocationName\":\"Town\",\"EventId\":\"100\",\"Classification\":\"Maybe\"}]}"
        );

        Assert.False(duplicate.IsAvailable);
        Assert.Equal("event-override-key-is-duplicated", duplicate.Reason);
        Assert.Equal(0, duplicate.Catalog.Count);
        Assert.False(unknown.IsAvailable);
        Assert.Equal("event-override-classification-is-invalid", unknown.Reason);
    }

    [Fact]
    public void Shipped_override_file_is_versioned_and_empty_by_default()
    {
        var json = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "ShippedMod",
                "Asset",
                "Sanity",
                "Data",
                "event-overrides.json"
            )
        );

        var result = SanityEventOverrideCatalog.Load(json);
        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(0, result.Catalog.Count);
    }
}
