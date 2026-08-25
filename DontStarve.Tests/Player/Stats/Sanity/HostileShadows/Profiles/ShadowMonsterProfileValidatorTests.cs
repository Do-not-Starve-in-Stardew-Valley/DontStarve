using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;

public sealed class ShadowMonsterProfileValidatorTests
{
    [Fact]
    public void ShippedProfilesBuildOneCanonicalCatalogWithFrozenTestValues()
    {
        var result = ShadowMonsterProfileTestFixture.ValidateShipped();

        Assert.True(result.Success, ShadowMonsterProfileTestFixture.FormatIssues(result.Issues));
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(result.Catalog);
        Assert.Equal(4, catalog.Profiles.Count);
        Assert.Equal(4, result.Diagnostics.Count);
        Assert.All(
            result.Diagnostics,
            diagnostic =>
            {
                Assert.Equal("shadow-profile.balance.beta", diagnostic.Code);
                Assert.Equal(ShadowMonsterProfileDiagnosticSeverity.Warning, diagnostic.Severity);
            }
        );

        ShadowMonsterProfile? previousCreeper = null;
        foreach (var profileId in ShadowMonsterDifficultyProfileIds.All)
        {
            Assert.True(catalog.TryGetProfile(profileId, out var difficulty));
            Assert.NotNull(difficulty);
            Assert.True(difficulty!.IsBetaBalance);
            Assert.Equal(2, difficulty.Monsters.Count);

            Assert.True(
                difficulty.TryGetMonster(ShadowMonsterAssetBindingIds.CreeperFear, out var creeper)
            );
            Assert.True(
                difficulty.TryGetMonster(ShadowMonsterAssetBindingIds.Terrorbeak, out var terrorbeak)
            );
            Assert.NotNull(creeper);
            Assert.NotNull(terrorbeak);
            AssertCreeperFear(creeper!);
            AssertTerrorbeak(terrorbeak!);
            AssertCanonicalContract(creeper!);
            AssertCanonicalContract(terrorbeak!);

            if (previousCreeper is not null)
                Assert.NotSame(previousCreeper, creeper);
            previousCreeper = creeper;
        }
    }

    [Theory]
    [InlineData("SchemaVersion")]
    [InlineData("ProfileId")]
    [InlineData("IsBetaBalance")]
    [InlineData("Monsters")]
    public void MissingRequiredRootFieldFailsClosed(string field)
    {
        var documents = ShadowMonsterProfileTestFixture.MutateProfile(
            root => root.Remove(field)
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Key.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MaxHealth")]
    [InlineData("BaseDamage")]
    [InlineData("MovementSpeed")]
    [InlineData("Defense")]
    [InlineData("DetectionRadiusTiles")]
    [InlineData("AttackRangeTiles")]
    [InlineData("AttackIntervalSeconds")]
    [InlineData("NaturalDespawnGameHours")]
    [InlineData("DisplayNameKey")]
    [InlineData("WallTraversalMode")]
    [InlineData("ImmunityTags")]
    [InlineData("DropTable")]
    [InlineData("SanityReward")]
    [InlineData("AssetBindingId")]
    [InlineData("AnimationProfileId")]
    [InlineData("CueSetId")]
    [InlineData("AttackMotionPolicyId")]
    [InlineData("PostAttackPolicyId")]
    public void MissingRequiredMonsterFieldFailsClosed(string field)
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster.Remove(field)
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Key.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SchemaVersion")]
    [InlineData("DropTableId")]
    [InlineData("ItemSemanticId")]
    [InlineData("GuaranteedQuantity")]
    [InlineData("BonusQuantity")]
    [InlineData("BonusChance")]
    public void MissingRequiredDropFieldFailsClosed(string field)
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster["DropTable"]!.AsObject().Remove(field)
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Key.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MaxHealth", 0d)]
    [InlineData("BaseDamage", -1d)]
    [InlineData("MovementSpeed", 0d)]
    [InlineData("Defense", -1d)]
    [InlineData("DetectionRadiusTiles", 0d)]
    [InlineData("AttackRangeTiles", 0d)]
    [InlineData("AttackIntervalSeconds", 0d)]
    [InlineData("NaturalDespawnGameHours", 0d)]
    [InlineData("SanityReward", -1d)]
    public void OutOfRangeRequiredValueFailsClosed(string field, double value)
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster[field] = JsonValue.Create(value)
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Key.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void NonFiniteNumericTokenFailsClosed()
    {
        var documents = ShadowMonsterProfileTestFixture.MutateRawProfile(
            json => json.Replace(
                "\"MovementSpeed\": 2.5",
                "\"MovementSpeed\": 1e999",
                StringComparison.Ordinal
            )
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "shadow-profile.value-non-finite");
    }

    [Fact]
    public void MalformedJsonFailsClosed()
    {
        var documents = ShadowMonsterProfileTestFixture.MutateRawProfile(json => json[..^2]);

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "shadow-profile.json-malformed");
    }

    [Fact]
    public void UnknownFieldsFailClosedAtEverySchemaLayer()
    {
        var schemaResult = ShadowMonsterProfileTestFixture.Validate(
            schemaJson: ShadowMonsterProfileTestFixture.SchemaJson.Replace(
                "\"SchemaVersion\": 1,",
                "\"SchemaVersion\": 1,\n  \"FutureSchemaField\": true,",
                StringComparison.Ordinal
            )
        );
        var rootResult = ShadowMonsterProfileTestFixture.Validate(
            ShadowMonsterProfileTestFixture.MutateProfile(root => root["FutureRootField"] = true)
        );
        var monsterResult = ShadowMonsterProfileTestFixture.Validate(
            ShadowMonsterProfileTestFixture.MutateFirstMonster(
                monster => monster["FutureMonsterField"] = true
            )
        );
        var dropResult = ShadowMonsterProfileTestFixture.Validate(
            ShadowMonsterProfileTestFixture.MutateFirstMonster(
                monster => monster["DropTable"]!.AsObject()["FutureDropField"] = true
            )
        );

        Assert.All(
            new[] { schemaResult, rootResult, monsterResult, dropResult },
            result =>
            {
                Assert.False(result.Success);
                Assert.Contains(result.Issues, issue => issue.Code == "shadow-profile.unknown-field");
            }
        );
    }

    [Fact]
    public void FourFrozenFilesAreRequiredAndDuplicateInputIsRejected()
    {
        var missing = ShadowMonsterProfileTestFixture.Documents
            .Where(document => document.RelativePath != ShadowMonsterProfilePaths.ProfileFiles[ShadowMonsterDifficultyProfileIds.Fusion])
            .ToList();
        var duplicate = ShadowMonsterProfileTestFixture.Documents.ToList();
        duplicate.Add(duplicate[0]);

        var missingResult = ShadowMonsterProfileTestFixture.Validate(missing);
        var duplicateResult = ShadowMonsterProfileTestFixture.Validate(duplicate);

        Assert.False(missingResult.Success);
        Assert.Contains(missingResult.Issues, issue => issue.Code == "shadow-profile.file-missing");
        Assert.False(duplicateResult.Success);
        Assert.Contains(duplicateResult.Issues, issue => issue.Code == "shadow-profile.file-duplicate");
    }

    [Fact]
    public void DuplicateMonsterBindingAndUnknownProfileIdFailClosed()
    {
        var duplicateDocuments = ShadowMonsterProfileTestFixture.MutateProfile(
            root =>
            {
                var monsters = root["Monsters"]!.AsArray();
                monsters.Add(JsonNode.Parse(monsters[0]!.ToJsonString()));
            }
        );
        var unknownDocuments = ShadowMonsterProfileTestFixture.MutateProfile(
            root => root["ProfileId"] = "Unknown"
        );

        var duplicateResult = ShadowMonsterProfileTestFixture.Validate(duplicateDocuments);
        var unknownResult = ShadowMonsterProfileTestFixture.Validate(unknownDocuments);

        Assert.False(duplicateResult.Success);
        Assert.Contains(duplicateResult.Issues, issue => issue.Code == "shadow-profile.asset-binding-id-duplicate");
        Assert.False(unknownResult.Success);
        Assert.Contains(unknownResult.Issues, issue => issue.Code == "shadow-profile.profile-id-unknown");
    }

    [Theory]
    [InlineData("AssetBindingId", "sanity.binding.unknown", "shadow-profile.asset-binding-unknown")]
    [InlineData("AnimationProfileId", "sanity.animation.unknown", "shadow-profile.animation-profile-mismatch")]
    [InlineData("CueSetId", "sanity.cue.unknown", "shadow-profile.cue-set-mismatch")]
    [InlineData("AttackMotionPolicyId", "sanity.attack-motion.unknown", "shadow-profile.attack-motion-policy-mismatch")]
    public void ResourceReferenceMismatchFailsClosed(string field, string value, string code)
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster[field] = value
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == code);
    }

    [Theory]
    [InlineData("DisplayNameKey", "Creeper Fear", "shadow-profile.display-name-key-invalid")]
    [InlineData("WallTraversalMode", "BlockedByTerrain", "shadow-profile.wall-traversal-mode-unknown")]
    [InlineData("PostAttackPolicyId", "sanity.post-attack.unknown", "shadow-profile.post-attack-policy-unknown")]
    public void StableGameplayPolicyContractRejectsUnknownValue(
        string field,
        string value,
        string code
    )
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster[field] = value
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == code);
    }

    [Fact]
    public void AttackRangeCannotExceedDetectionRadius()
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster["AttackRangeTiles"] = 21d
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "shadow-profile.range-order-invalid");
    }

    [Fact]
    public void DropRewardAndImmunityContractsFailClosedOnDrift()
    {
        var dropDocuments = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster["DropTable"]!.AsObject()["BonusChance"] = 0.49d
        );
        var rewardDocuments = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster["SanityReward"] = -1
        );
        var immunityDocuments = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster["ImmunityTags"] = new JsonArray("Knockback", "Poison")
        );

        var dropResult = ShadowMonsterProfileTestFixture.Validate(dropDocuments);
        var rewardResult = ShadowMonsterProfileTestFixture.Validate(rewardDocuments);
        var immunityResult = ShadowMonsterProfileTestFixture.Validate(immunityDocuments);

        Assert.Contains(dropResult.Issues, issue => issue.Code == "shadow-profile.drop-table-contract-invalid");
        Assert.False(rewardResult.Success);
        Assert.Contains(immunityResult.Issues, issue => issue.Code == "shadow-profile.immunity-tag-unknown");
        Assert.Contains(immunityResult.Issues, issue => issue.Code == "shadow-profile.immunity-required-tag-missing");
    }

    [Fact]
    public void DuplicateImmunityTagFailsClosed()
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster => monster["ImmunityTags"] = new JsonArray("Knockback", "Frozen", "Frozen")
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "shadow-profile.immunity-tag-duplicate");
    }

    [Fact]
    public void OptionalFieldsDefaultAndValidVersionOverrideAreCanonical()
    {
        var shipped = ShadowMonsterProfileTestFixture.ValidateShipped();
        var shippedCatalog = Assert.IsType<ShadowMonsterProfileCatalog>(shipped.Catalog);
        Assert.True(shippedCatalog.TryGetProfile(ShadowMonsterDifficultyProfileIds.Compatible, out var shippedDifficulty));
        Assert.True(shippedDifficulty!.TryGetMonster(ShadowMonsterAssetBindingIds.CreeperFear, out var shippedMonster));
        Assert.Equal(0, shippedMonster!.ExperienceValue);
        Assert.Null(shippedMonster.KillCounterId);
        Assert.Empty(shippedMonster.GameVersionOverrides);

        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster =>
            {
                monster["ExperienceValue"] = 12;
                monster["KillCounterId"] = "sanity.kill-counter.creeper-fear";
                monster["GameVersionOverrides"] = new JsonArray(
                    new JsonObject
                    {
                        ["GameVersionId"] = "Stardew1.6",
                        ["MaxHealth"] = 333,
                        ["AttackRangeTiles"] = 1.5d,
                    }
                );
            }
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.True(result.Success, ShadowMonsterProfileTestFixture.FormatIssues(result.Issues));
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(result.Catalog);
        Assert.True(catalog.TryGetProfile(ShadowMonsterDifficultyProfileIds.Compatible, out var difficulty));
        Assert.True(difficulty!.TryGetMonster(ShadowMonsterAssetBindingIds.CreeperFear, out var monster));
        Assert.Equal(12, monster!.ExperienceValue);
        Assert.Equal("sanity.kill-counter.creeper-fear", monster.KillCounterId);
        Assert.True(monster.TryGetVersionOverride("Stardew1.6", out var versionOverride));
        Assert.Equal(333, versionOverride!.MaxHealth);
        Assert.Equal(1.5d, versionOverride.AttackRangeTiles);
    }

    [Theory]
    [InlineData("ExperienceValue")]
    [InlineData("KillCounterId")]
    [InlineData("GameVersionOverrides")]
    public void InvalidOptionalFieldFailsClosed(string field)
    {
        var documents = ShadowMonsterProfileTestFixture.MutateFirstMonster(
            monster =>
            {
                monster[field] = field switch
                {
                    "ExperienceValue" => JsonValue.Create(-1),
                    "KillCounterId" => JsonValue.Create("BAD"),
                    _ => new JsonObject(),
                };
            }
        );

        var result = ShadowMonsterProfileTestFixture.Validate(documents);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Key.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEmptyAndDuplicateVersionOverridesFailClosed()
    {
        var unknown = ValidateOverrideArray(
            new JsonObject { ["GameVersionId"] = "Stardew2.0", ["MaxHealth"] = 1 }
        );
        var empty = ValidateOverrideArray(new JsonObject { ["GameVersionId"] = "Stardew1.6" });
        var duplicate = ValidateOverrideArray(
            new JsonObject { ["GameVersionId"] = "Stardew1.6", ["MaxHealth"] = 1 },
            new JsonObject { ["GameVersionId"] = "Stardew1.6", ["MaxHealth"] = 2 }
        );

        Assert.Contains(unknown.Issues, issue => issue.Code == "shadow-profile.game-version-id-unknown");
        Assert.Contains(empty.Issues, issue => issue.Code == "shadow-profile.game-version-override-empty");
        Assert.Contains(duplicate.Issues, issue => issue.Code == "shadow-profile.game-version-id-duplicate");
    }

    [Fact]
    public void SchemaVersionAndCapabilityDriftFailClosed()
    {
        var schemaVersion = ShadowMonsterProfileTestFixture.Validate(
            schemaJson: ShadowMonsterProfileTestFixture.SchemaJson.Replace(
                "\"SchemaVersion\": 1",
                "\"SchemaVersion\": 2",
                StringComparison.Ordinal
            )
        );
        var capability = ShadowMonsterProfileTestFixture.Validate(
            schemaJson: ShadowMonsterProfileTestFixture.SchemaJson.Replace(
                "\"Stardew17Capability\": \"Unavailable\"",
                "\"Stardew17Capability\": \"Available\"",
                StringComparison.Ordinal
            )
        );

        Assert.Contains(schemaVersion.Issues, issue => issue.Code == "shadow-profile.schema-contract-unsupported");
        Assert.Contains(capability.Issues, issue => issue.Code == "shadow-profile.schema-contract-unsupported");
    }

    [Fact]
    public void StartupLoaderReadsEveryDocumentOnceAndCachesSuccess()
    {
        var source = new CountingProfileSource(ShadowMonsterProfileTestFixture.AllRequiredText);
        var loader = new ShadowMonsterProfileLoader(source);

        var first = loader.LoadAtStartupOnce();
        var second = loader.LoadAtStartupOnce();

        Assert.True(first.Success, ShadowMonsterProfileTestFixture.FormatIssues(first.Issues));
        Assert.Same(first, second);
        Assert.Equal(8, source.TotalReads);
        Assert.All(source.ReadCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void DirectoryLoaderBuildsCatalogFromShippedDeploymentRoot()
    {
        var loader = new ShadowMonsterProfileLoader(
            new DirectoryShadowMonsterProfileFileSource(
                ShadowMonsterProfileTestFixture.ShippedModRoot
            )
        );

        var result = loader.LoadAtStartupOnce();

        Assert.True(result.Success, ShadowMonsterProfileTestFixture.FormatIssues(result.Issues));
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(result.Catalog);
        Assert.Equal(4, catalog.Profiles.Count);
        Assert.Equal(4, result.Diagnostics.Count);
    }

    [Fact]
    public void StartupLoaderCachesFailureWithoutRetryIo()
    {
        var text = new Dictionary<string, string>(ShadowMonsterProfileTestFixture.AllRequiredText, StringComparer.Ordinal);
        text.Remove(ShadowMonsterProfilePaths.Schema);
        var source = new CountingProfileSource(text);
        var loader = new ShadowMonsterProfileLoader(source);

        var first = loader.LoadAtStartupOnce();
        var second = loader.LoadAtStartupOnce();

        Assert.False(first.Success);
        Assert.Same(first, second);
        Assert.Equal(8, source.TotalReads);
        Assert.Contains(first.Issues, issue => issue.Code == "shadow-profile.file-unavailable");
    }

    [Fact]
    public void DirectorySourceRejectsBomInvalidUtf8AndTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "DontStarve-ShadowProfile-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "Asset", "Sanity", "Data", "ShadowMonsters");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var schemaPath = Path.Combine(dataDirectory, "schema.json");
            File.WriteAllBytes(schemaPath, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{}")).ToArray());
            var source = new DirectoryShadowMonsterProfileFileSource(root);

            var bom = source.Read(ShadowMonsterProfilePaths.Schema);
            File.WriteAllBytes(schemaPath, new byte[] { 0xC3, 0x28 });
            var invalidUtf8 = source.Read(ShadowMonsterProfilePaths.Schema);
            var traversal = source.Read("../references/forbidden.json");

            Assert.False(bom.Success);
            Assert.Equal("shadow-profile.utf8-bom-forbidden", bom.Reason);
            Assert.False(invalidUtf8.Success);
            Assert.Equal("shadow-profile.utf8-invalid", invalidUtf8.Reason);
            Assert.False(traversal.Success);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ShadowMonsterProfileValidationResult ValidateOverrideArray(
        params JsonObject[] overrides
    )
    {
        return ShadowMonsterProfileTestFixture.Validate(
            ShadowMonsterProfileTestFixture.MutateFirstMonster(
                monster =>
                    monster["GameVersionOverrides"] = new JsonArray(
                        overrides.Select(item => (JsonNode)item).ToArray()
                    )
            )
        );
    }

    private static void AssertCreeperFear(ShadowMonsterProfile profile)
    {
        Assert.Equal(300, profile.MaxHealth);
        Assert.Equal(20, profile.BaseDamage);
        Assert.Equal(2.5d, profile.MovementSpeed);
        Assert.Equal(1.8d, profile.AttackIntervalSeconds);
        Assert.Equal(15, profile.SanityReward);
        Assert.Equal("sanity.animation.creeper-fear.profile", profile.AnimationProfileId);
        Assert.Equal("sanity.cue.creeper-fear", profile.CueSetId);
    }

    private static void AssertTerrorbeak(ShadowMonsterProfile profile)
    {
        Assert.Equal(400, profile.MaxHealth);
        Assert.Equal(50, profile.BaseDamage);
        Assert.Equal(6d, profile.MovementSpeed);
        Assert.Equal(1.2d, profile.AttackIntervalSeconds);
        Assert.Equal(33, profile.SanityReward);
        Assert.Equal("sanity.animation.terrorbeak.profile", profile.AnimationProfileId);
        Assert.Equal("sanity.cue.terrorbeak", profile.CueSetId);
    }

    private static void AssertCanonicalContract(ShadowMonsterProfile profile)
    {
        Assert.Equal(0, profile.Defense);
        Assert.Equal(20d, profile.DetectionRadiusTiles);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(2d, profile.NaturalDespawnGameHours);
        Assert.Equal("DirectThroughTerrain", profile.WallTraversalMode);
        Assert.Equal(new[] { "Knockback", "Frozen" }, profile.ImmunityTags);
        Assert.Equal("sanity.attack-motion.one-tile-v1", profile.AttackMotionPolicyId);
        Assert.Equal("sanity.post-attack.taunt-or-delay-v1", profile.PostAttackPolicyId);
        Assert.Equal(0, profile.ExperienceValue);
        Assert.Null(profile.KillCounterId);
        Assert.Empty(profile.GameVersionOverrides);
        Assert.Equal("sanity.drop.void-essence-v1", profile.DropTable.DropTableId);
        Assert.Equal("stardew.item.void-essence", profile.DropTable.ItemSemanticId);
        Assert.Equal(1, profile.DropTable.GuaranteedQuantity);
        Assert.Equal(1, profile.DropTable.BonusQuantity);
        Assert.Equal(0.5d, profile.DropTable.BonusChance);
    }

    private sealed class CountingProfileSource : IShadowMonsterProfileFileSource
    {
        private readonly IReadOnlyDictionary<string, string> text;

        internal CountingProfileSource(IReadOnlyDictionary<string, string> text)
        {
            this.text = text;
        }

        internal Dictionary<string, int> ReadCounts { get; } = new(StringComparer.Ordinal);
        internal int TotalReads => ReadCounts.Values.Sum();

        public ShadowMonsterProfileTextReadResult Read(string relativePath)
        {
            ReadCounts.TryGetValue(relativePath, out var count);
            ReadCounts[relativePath] = count + 1;
            return text.TryGetValue(relativePath, out var value)
                ? new ShadowMonsterProfileTextReadResult(true, relativePath, value, "test.read")
                : new ShadowMonsterProfileTextReadResult(false, relativePath, string.Empty, "test.missing");
        }
    }
}

internal static class ShadowMonsterProfileTestFixture
{
    internal static string ShippedModRoot => Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    internal static string SchemaJson => Read(ShadowMonsterProfilePaths.Schema);

    internal static IReadOnlyList<ShadowMonsterProfileDocument> Documents =>
        ShadowMonsterProfilePaths.ProfileFiles.Values
            .Select(path => new ShadowMonsterProfileDocument(path, Read(path)))
            .ToArray();

    internal static IReadOnlyDictionary<string, string> AllRequiredText
    {
        get
        {
            var paths = new[]
            {
                ShadowMonsterProfilePaths.Schema,
                ShadowMonsterProfilePaths.Animations,
                ShadowMonsterProfilePaths.ResourceBindings,
                ShadowMonsterProfilePaths.AudioCues,
            }.Concat(ShadowMonsterProfilePaths.ProfileFiles.Values);
            return paths.ToDictionary(path => path, Read, StringComparer.Ordinal);
        }
    }

    internal static ShadowMonsterProfileValidationResult ValidateShipped()
    {
        return Validate(Documents);
    }

    internal static ShadowMonsterProfileValidationResult Validate(
        IReadOnlyList<ShadowMonsterProfileDocument>? documents = null,
        string? schemaJson = null
    )
    {
        Assert.True(
            ShadowMonsterProfileReferenceCatalog.TryCreate(
                Read(ShadowMonsterProfilePaths.Animations),
                Read(ShadowMonsterProfilePaths.ResourceBindings),
                Read(ShadowMonsterProfilePaths.AudioCues),
                out var references,
                out var referenceIssues
            ),
            FormatIssues(referenceIssues)
        );
        return ShadowMonsterProfileValidator.Validate(
            schemaJson ?? SchemaJson,
            documents ?? Documents,
            references!
        );
    }

    internal static IReadOnlyList<ShadowMonsterProfileDocument> MutateProfile(
        Action<JsonObject> mutation,
        string profileId = ShadowMonsterDifficultyProfileIds.Compatible
    )
    {
        return MutateRawProfile(
            json =>
            {
                var root = JsonNode.Parse(json)!.AsObject();
                mutation(root);
                return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            },
            profileId
        );
    }

    internal static IReadOnlyList<ShadowMonsterProfileDocument> MutateFirstMonster(
        Action<JsonObject> mutation,
        string profileId = ShadowMonsterDifficultyProfileIds.Compatible
    )
    {
        return MutateProfile(
            root => mutation(root["Monsters"]!.AsArray()[0]!.AsObject()),
            profileId
        );
    }

    internal static IReadOnlyList<ShadowMonsterProfileDocument> MutateRawProfile(
        Func<string, string> mutation,
        string profileId = ShadowMonsterDifficultyProfileIds.Compatible
    )
    {
        var target = ShadowMonsterProfilePaths.ProfileFiles[profileId];
        return Documents
            .Select(
                document =>
                    document.RelativePath == target
                        ? new ShadowMonsterProfileDocument(document.RelativePath, mutation(document.Json))
                        : document
            )
            .ToArray();
    }

    internal static string FormatIssues(IReadOnlyList<ShadowMonsterProfileIssue> issues)
    {
        return string.Join(
            Environment.NewLine,
            issues.Select(issue => $"{issue.Code}: {issue.FilePath} {issue.Key} {issue.Reason}")
        );
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(
            Path.Combine(ShippedModRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Encoding.UTF8
        );
    }
}
