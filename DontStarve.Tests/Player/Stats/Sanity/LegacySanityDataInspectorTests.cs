using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class LegacySanityDataInspectorTests
{
    [Theory]
    [InlineData("zero.json", 0d)]
    [InlineData("half.json", 75d)]
    [InlineData("max.json", 150d)]
    [InlineData("negative.json", -1d)]
    [InlineData("over-max.json", 151d)]
    public void Inspect_preserves_finite_legacy_values(string fixtureName, double expected)
    {
        var result = InspectFixture(fixtureName);

        Assert.Equal(LegacySanityInspectionStatus.FiniteSanityValue, result.Status);
        Assert.Equal(expected, result.Value);
        Assert.Equal("legacy-sanity-value-is-finite", result.Reason);
    }

    [Theory]
    [InlineData("missing-key.json")]
    [InlineData("wrong-case-key.json")]
    public void Inspect_requires_the_exact_Sanity_key(string fixtureName)
    {
        var result = InspectFixture(fixtureName);

        Assert.Equal(LegacySanityInspectionStatus.MissingSanityKey, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("legacy-sanity-key-is-missing", result.Reason);
    }

    [Fact]
    public void Inspect_classifies_null_without_inventing_a_value()
    {
        var result = InspectFixture("null.json");

        Assert.Equal(LegacySanityInspectionStatus.NullSanityValue, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("legacy-sanity-value-is-null", result.Reason);
    }

    [Theory]
    [InlineData("nan-string.json")]
    [InlineData("positive-infinity-string.json")]
    [InlineData("negative-infinity-string.json")]
    [InlineData("non-finite-overflow.json")]
    public void Inspect_rejects_non_finite_numeric_boundaries(string fixtureName)
    {
        var result = InspectFixture(fixtureName);

        Assert.Equal(LegacySanityInspectionStatus.NonFiniteSanityValue, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("legacy-sanity-value-is-non-finite", result.Reason);
    }

    [Fact]
    public void Inspect_rejects_a_bad_field_type()
    {
        var result = InspectFixture("bad-field.json");

        Assert.Equal(LegacySanityInspectionStatus.InvalidSanityType, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("legacy-sanity-value-must-be-a-number", result.Reason);
    }

    [Fact]
    public void Inspect_rejects_a_non_object_root()
    {
        var result = InspectFixture("invalid-root.json");

        Assert.Equal(LegacySanityInspectionStatus.InvalidRoot, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("legacy-root-must-be-an-object", result.Reason);
    }

    [Theory]
    [InlineData("bad-json.json")]
    [InlineData("nan-token.json")]
    public void Inspect_rejects_malformed_json(string fixtureName)
    {
        var result = InspectFixture(fixtureName);

        Assert.Equal(LegacySanityInspectionStatus.MalformedJson, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("legacy-json-is-malformed", result.Reason);
    }

    [Fact]
    public void Inspecting_the_same_fixture_twice_is_idempotent()
    {
        var json = ReadFixture("half.json");

        var first = LegacySanityDataInspector.Inspect(json);
        var second = LegacySanityDataInspector.Inspect(json);

        Assert.Equal(first, second);
    }

    private static LegacySanityInspection InspectFixture(string fixtureName)
    {
        return LegacySanityDataInspector.Inspect(ReadFixture(fixtureName));
    }

    private static string ReadFixture(string fixtureName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "LegacySanity",
            fixtureName
        );
        return File.ReadAllText(path);
    }
}
