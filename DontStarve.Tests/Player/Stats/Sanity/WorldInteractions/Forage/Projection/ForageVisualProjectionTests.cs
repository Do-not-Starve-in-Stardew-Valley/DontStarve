using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.Forage.Projection;

public sealed class ForageVisualProjectionTests
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

    private static string RuntimeSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "ForageProjection",
            "SmapiForageVisualProjectionService.cs"
        );

    [Theory]
    [InlineData(39.999d, true)]
    [InlineData(40d, true)]
    [InlineData(40.001d, false)]
    public void ProjectionAndFuturePickerShareTheExactFortyPercentBoundary(
        double current,
        bool expected
    )
    {
        var result = ForageInteractionSanityGate.Evaluate(current, 100d);

        Assert.Equal(expected, result.IsEligible);
        Assert.Equal(current / 100d, result.Ratio);
        var tier = Assert.Single(
            SanityTierCatalog.Rules,
            rule => rule.Id == SanityTierIds.BeardRabbit
        );
        Assert.Equal(ForageInteractionSanityGate.MaximumRatio, tier.EnterRatio);
        Assert.Equal(ForageInteractionSanityGate.MaximumRatio, tier.ExitRatio);
    }

    [Theory]
    [InlineData(double.NaN, 100d)]
    [InlineData(40d, double.PositiveInfinity)]
    [InlineData(0d, 0d)]
    [InlineData(-1d, 100d)]
    [InlineData(101d, 100d)]
    public void InvalidSanitySnapshotFailsClosed(double current, double maximum)
    {
        var result = ForageInteractionSanityGate.Evaluate(current, maximum);

        Assert.False(result.IsEligible);
        Assert.Null(result.Ratio);
        Assert.Equal(
            ForageVisualProjectionReasonIds.SanitySnapshotInvalid,
            result.Reason
        );
    }

    [Fact]
    public void ShippedCatalogEnablesOneGroundObjectProjectionMapping()
    {
        var load = ForageReplacementCatalog.Load(File.ReadAllText(ShippedCatalogPath));
        Assert.True(load.IsAvailable, load.Reason);

        var capability = ForageGroundProjectionCapabilityGate.Evaluate(load.Catalog);

        Assert.Equal(
            ForageGroundProjectionCapabilityStatus.Available,
            capability.Status
        );
        Assert.Equal(1, capability.EnabledMappingCount);
        Assert.True(capability.CanProjectGroundObjects);
        Assert.Equal(
            ForageVisualProjectionReasonIds.GroundMappingsAvailable,
            capability.Reason
        );
    }

    [Fact]
    public void SyntheticEnabledMappingPublishesGroundRendererCapability()
    {
        var load = ForageReplacementCatalog.Load(
            "{\"SchemaVersion\":2,\"ContractId\":\"sanity.direct-pickup-replacements.v2\","
                + "\"Revision\":1,"
                + "\"Mappings\":[{\"Id\":\"enabled\",\"SourceQualifiedItemId\":\"(O)1\","
                + "\"TargetQualifiedItemId\":\"(O)2\",\"Enabled\":true,"
                + "\"LocationAllowlist\":[\"Farm\"],"
                + "\"ContextAllowlist\":[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"],"
                + "\"Evidence\":[\"synthetic\"],\"Reason\":\"synthetic\"}]}"
        );
        Assert.True(load.IsAvailable, load.Reason);

        var capability = ForageGroundProjectionCapabilityGate.Evaluate(load.Catalog);

        Assert.Equal(
            ForageGroundProjectionCapabilityStatus.Available,
            capability.Status
        );
        Assert.Equal(1, capability.EnabledMappingCount);
        Assert.True(capability.CanProjectGroundObjects);
    }

    [Fact]
    public void ShippedRabbitPlaceholderIsDevelopmentUsableButExplicitlyProvisional()
    {
        var capability = ForageRabbitVisualContract.Evaluate(ValidVisual());

        Assert.True(capability.IsAvailable);
        Assert.Equal(
            ForageRabbitVisualCapabilityStatus.AvailableDevelopmentPlaceholder,
            capability.Status
        );
        Assert.True(capability.IsPlaceholder);
        Assert.True(capability.IsProvisional);
        Assert.Equal(
            ForageVisualProjectionReasonIds.RabbitVisualPlaceholderAvailable,
            capability.Reason
        );
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-slot")]
    [InlineData("wrong-size")]
    [InlineData("wrong-pivot")]
    [InlineData("not-owner-local")]
    public void MissingOrDriftedRabbitVisualFailsClosed(string drift)
    {
        var descriptor = ValidVisual();
        descriptor = drift switch
        {
            "missing" => descriptor with { IsAvailable = false },
            "wrong-slot" => descriptor with { TextureSlotId = "sanity.asset.other" },
            "wrong-size" => descriptor with
            {
                SourceRectangle = new SanityResourceRectangle(0, 0, 32, 32),
            },
            "wrong-pivot" => descriptor with
            {
                PivotSourcePx = new SanityResourcePoint(16, 16),
            },
            "not-owner-local" => descriptor with { OwnerLocalOnly = false },
            _ => throw new InvalidOperationException(drift),
        };

        var capability = ForageRabbitVisualContract.Evaluate(descriptor);

        Assert.False(capability.IsAvailable);
        Assert.Equal(ForageRabbitVisualCapabilityStatus.Unavailable, capability.Status);
    }

    [Fact]
    public void TwoOwnersWithDifferentSanityOnlyIndexTheEligibleOwner()
    {
        var cache = new ForageVisualProjectionCache();
        var location = new object();
        var lowOwner = Owner("1", 0, location, "Farm");
        var normalOwner = Owner("2", 1, location, "Farm");
        var lowRabbit = new object();
        var normalRabbit = new object();

        var low = cache.RefreshRabbits(
            lowOwner,
            new[] { lowRabbit },
            ForageInteractionSanityGate.Evaluate(40d, 100d).IsEligible,
            eventBlocked: false,
            visualAvailable: true
        );
        var normal = cache.RefreshRabbits(
            normalOwner,
            new[] { normalRabbit },
            ForageInteractionSanityGate.Evaluate(40.001d, 100d).IsEligible,
            eventBlocked: false,
            visualAvailable: true
        );

        Assert.Equal(ForageVisualProjectionRefreshStatus.Refreshed, low.Status);
        Assert.Equal(ForageVisualProjectionRefreshStatus.TierInactive, normal.Status);
        Assert.True(cache.ContainsRabbit(lowOwner, lowRabbit));
        Assert.False(cache.ContainsRabbit(normalOwner, normalRabbit));
        Assert.Equal(1, cache.RabbitProjectionCount);
    }

    [Fact]
    public void CurrentLocationRabbitsAreIndexedImmediatelyAndObserverIsInvisible()
    {
        var cache = new ForageVisualProjectionCache();
        var location = new object();
        var owner = Owner("1", 0, location, "Farm");
        var rabbit = new object();

        var refresh = cache.RefreshRabbits(
            owner,
            new[] { rabbit },
            tierEligible: true,
            eventBlocked: false,
            visualAvailable: true
        );

        Assert.Equal(1, refresh.RabbitProjectionCount);
        Assert.True(cache.ContainsRabbit(owner, rabbit));
        Assert.False(cache.ContainsRabbit(Owner("2", 1, location, "Farm"), rabbit));
        Assert.False(cache.ContainsRabbit(Owner("1", 0, new object(), "Farm"), rabbit));
    }

    [Fact]
    public void DriftRefreshIsBoundedAndReplacesRemovedRabbitReferences()
    {
        var cache = new ForageVisualProjectionCache();
        var owner = Owner("1", 0, new object(), "Farm");
        var first = new object();
        var candidates = Enumerable
            .Range(0, ForageVisualProjectionCache.MaximumRabbitCandidatesPerOwnerScreen + 8)
            .Select(index => index == 0 ? first : new object())
            .ToArray();

        var initial = cache.RefreshRabbits(owner, candidates, true, false, true);
        Assert.Equal(
            ForageVisualProjectionCache.MaximumRabbitCandidatesPerOwnerScreen,
            initial.InspectedCandidateCount
        );
        Assert.Equal(
            ForageVisualProjectionCache.MaximumRabbitCandidatesPerOwnerScreen,
            initial.RabbitProjectionCount
        );

        var replacement = new object();
        cache.RefreshRabbits(owner, new[] { replacement }, true, false, true);
        Assert.False(cache.ContainsRabbit(owner, first));
        Assert.True(cache.ContainsRabbit(owner, replacement));
    }

    [Fact]
    public void GroundProjectionIsOwnerLocationAndReferenceLocal()
    {
        var cache = new ForageVisualProjectionCache();
        var location = new object();
        var owner = Owner("1", 0, location, "Farm");
        var source = new object();
        var replacement = new object();

        var refresh = cache.RefreshGroundObjects(
            owner,
            new[] { Ground(source, replacement) },
            tierEligible: true,
            eventBlocked: false,
            rendererAvailable: true
        );

        Assert.Equal(1, refresh.GroundObjectProjectionCount);
        Assert.True(
            cache.TryGetGroundReplacement(
                owner,
                source,
                12,
                34,
                1,
                2,
                1,
                out var projected
            )
        );
        Assert.Same(replacement, projected);
        Assert.False(
            cache.TryGetGroundReplacement(
                Owner("2", 1, location, "Farm"),
                source,
                12,
                34,
                1,
                2,
                1,
                out _
            )
        );
        Assert.False(
            cache.TryGetGroundReplacement(
                Owner("1", 0, new object(), "Farm"),
                source,
                12,
                34,
                1,
                2,
                1,
                out _
            )
        );
    }

    [Theory]
    [InlineData("tile-x")]
    [InlineData("tile-y")]
    [InlineData("stack")]
    [InlineData("quality")]
    [InlineData("revision")]
    public void GroundProjectionFailsOpenWhenCachedIdentityDrifts(string drift)
    {
        var cache = new ForageVisualProjectionCache();
        var owner = Owner("1", 0, new object(), "Farm");
        var source = new object();
        cache.RefreshGroundObjects(
            owner,
            new[] { Ground(source, new object()) },
            true,
            false,
            true
        );

        Assert.False(
            cache.TryGetGroundReplacement(
                owner,
                source,
                drift == "tile-x" ? 13 : 12,
                drift == "tile-y" ? 35 : 34,
                drift == "stack" ? 2 : 1,
                drift == "quality" ? 4 : 2,
                drift == "revision" ? 2 : 1,
                out _
            )
        );
    }

    [Fact]
    public void GroundRefreshIsBoundedAndReplacesRemovedSourceReferences()
    {
        var cache = new ForageVisualProjectionCache();
        var owner = Owner("1", 0, new object(), "Farm");
        var first = new object();
        var candidates = Enumerable
            .Range(
                0,
                ForageVisualProjectionCache.MaximumGroundObjectCandidatesPerOwnerScreen
                    + 8
            )
            .Select(
                index =>
                    Ground(
                        index == 0 ? first : new object(),
                        new object()
                    )
            )
            .ToArray();

        var initial = cache.RefreshGroundObjects(owner, candidates, true, false, true);
        Assert.Equal(
            ForageVisualProjectionCache.MaximumGroundObjectCandidatesPerOwnerScreen,
            initial.InspectedCandidateCount
        );
        Assert.Equal(
            ForageVisualProjectionCache.MaximumGroundObjectCandidatesPerOwnerScreen,
            initial.GroundObjectProjectionCount
        );

        var next = new object();
        cache.RefreshGroundObjects(
            owner,
            new[]
            {
                Ground(next, new object()),
            },
            true,
            false,
            true
        );
        Assert.False(
            cache.TryGetGroundReplacement(owner, first, 12, 34, 1, 2, 1, out _)
        );
        Assert.True(
            cache.TryGetGroundReplacement(owner, next, 12, 34, 1, 2, 1, out _)
        );
    }

    [Theory]
    [InlineData("tier")]
    [InlineData("event")]
    [InlineData("renderer")]
    public void GroundTierEventAndRendererExitRestoreOriginalVisibility(string exit)
    {
        var cache = new ForageVisualProjectionCache();
        var owner = Owner("1", 0, new object(), "Farm");
        var source = new object();
        cache.RefreshGroundObjects(
            owner,
            new[]
            {
                Ground(source, new object()),
            },
            true,
            false,
            true
        );

        cache.RefreshGroundObjects(
            owner,
            Array.Empty<ForageGroundProjectionCandidate>(),
            tierEligible: exit != "tier",
            eventBlocked: exit == "event",
            rendererAvailable: exit != "renderer"
        );

        Assert.False(
            cache.TryGetGroundReplacement(owner, source, 12, 34, 1, 2, 1, out _)
        );
    }

    [Theory]
    [InlineData("tier")]
    [InlineData("event")]
    [InlineData("resource")]
    public void TierEventAndResourceExitRestoreOriginalVisibility(string exit)
    {
        var cache = new ForageVisualProjectionCache();
        var owner = Owner("1", 0, new object(), "Farm");
        var rabbit = new object();
        cache.RefreshRabbits(owner, new[] { rabbit }, true, false, true);

        var result = cache.RefreshRabbits(
            owner,
            new[] { rabbit },
            tierEligible: exit != "tier",
            eventBlocked: exit == "event",
            visualAvailable: exit != "resource"
        );

        Assert.False(cache.ContainsRabbit(owner, rabbit));
        Assert.Equal(0, result.RabbitProjectionCount);
    }

    [Fact]
    public void WarpScreenInvalidationTitleAndDisabledCleanupAreIdempotent()
    {
        var cache = new ForageVisualProjectionCache();
        var first = Owner("1", 0, new object(), "Farm");
        var second = Owner("2", 1, new object(), "Town");
        cache.RefreshRabbits(first, new[] { new object() }, true, false, true);
        cache.RefreshRabbits(second, new[] { new object() }, true, false, true);

        Assert.Equal(1, cache.CleanupOwnerScreen("1", 0));
        Assert.Equal(0, cache.CleanupOwnerScreen("1", 0));
        Assert.Equal(1, cache.CleanupInvalidScreens(screen => screen == 0));
        Assert.Equal(0, cache.CleanupAll());
        Assert.Equal(0, cache.RabbitProjectionCount);
        Assert.Equal(0, cache.GroundObjectProjectionCount);
    }

    [Fact]
    public void RuntimeUsesExactRabbitAndObjectDrawOwnerLifecycleAndBoundedScan()
    {
        var source = File.ReadAllText(RuntimeSourcePath);

        Assert.Contains("typeof(StardewCritter)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(StardewCritter.draw)", source, StringComparison.Ordinal);
        Assert.Contains(
            "__instance.GetType() != typeof(Rabbit)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("return !service.TryDrawRabbitProjection", source, StringComparison.Ordinal);
        Assert.Contains("typeof(StardewValley.Object)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(StardewValley.Object.draw)", source, StringComparison.Ordinal);
        Assert.Contains("BeforeObjectDraw", source, StringComparison.Ordinal);
        Assert.Contains("TryDrawGroundObjectProjection", source, StringComparison.Ordinal);
        Assert.Contains("Harmony.GetPatchInfo", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedGameVersion = \"1.6.15\"", source, StringComparison.Ordinal);
        Assert.Contains("DriftValidationIntervalTicks = 60", source, StringComparison.Ordinal);
        Assert.Contains("location.critters.Count", source, StringComparison.Ordinal);
        Assert.Contains("location.Objects.Pairs", source, StringComparison.Ordinal);
        Assert.Contains(
            "MaximumRabbitCandidatesPerOwnerScreen",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("StateEventPublished", source, StringComparison.Ordinal);
        Assert.Contains("EventOwnerCoverageChanged", source, StringComparison.Ordinal);
        Assert.Contains(
            "lifecycle.IsEventCoverageActiveForPlayer(owner.PlayerKey)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("WorldBoundaryStarting", source, StringComparison.Ordinal);
        Assert.Contains("SessionClearing", source, StringComparison.Ordinal);
        Assert.Contains("DayEnding", source, StringComparison.Ordinal);
        Assert.Contains("VisualResourcesInvalidating", source, StringComparison.Ordinal);
        Assert.Contains("WorldResourcesReleasing", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDoesNotMutateWorldScanObjectsOrBroadcastProjection()
    {
        var source = File.ReadAllText(RuntimeSourcePath);

        Assert.DoesNotContain("location.critters.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.locations", source, StringComparison.Ordinal);
        Assert.DoesNotContain("objects.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Objects.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("helper.Multiplayer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadVisualSlot", BeforeDrawPrefix(source), StringComparison.Ordinal);
        Assert.DoesNotContain("ItemRegistry", BeforeObjectDrawPrefix(source), StringComparison.Ordinal);
        Assert.DoesNotContain("location.Objects", BeforeObjectDrawPrefix(source), StringComparison.Ordinal);
    }

    private static ForageGroundProjectionCandidate Ground(
        object source,
        object replacement
    )
    {
        return new ForageGroundProjectionCandidate(
            source,
            replacement,
            tileX: 12,
            tileY: 34,
            sourceStack: 1,
            sourceQuality: 2,
            catalogRevision: 1,
            mappingId: "test"
        );
    }

    private static ForageRabbitVisualDescriptor ValidVisual()
    {
        return new ForageRabbitVisualDescriptor(
            IsAvailable: true,
            RequestedSlotId: ForageRabbitVisualContract.TextureSlotId,
            TextureSlotId: ForageRabbitVisualContract.TextureSlotId,
            PreviewKind: SanityVisualPreviewKind.StaticSprite,
            SourceRectangle: new SanityResourceRectangle(0, 0, 64, 64),
            PivotSourcePx: new SanityResourcePoint(32, 60),
            DrawScale: 1d,
            OwnerLocalOnly: true,
            IsPlaceholder: true,
            IsProvisional: true,
            DiagnosticReason: "resource.preview.placeholder-available"
        );
    }

    private static HarmlessProjectionOwnerContext Owner(
        string playerKey,
        int screenId,
        object location,
        string name
    )
    {
        return new HarmlessProjectionOwnerContext(
            playerKey,
            screenId,
            location,
            name
        );
    }

    private static string BeforeDrawPrefix(string source)
    {
        var start = source.IndexOf(
            "private static bool BeforeCritterDraw",
            StringComparison.Ordinal
        );
        var end = source.IndexOf(
            "private bool TryDrawRabbitProjection",
            start,
            StringComparison.Ordinal
        );
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string BeforeObjectDrawPrefix(string source)
    {
        var start = source.IndexOf(
            "private static bool BeforeObjectDraw",
            StringComparison.Ordinal
        );
        var end = source.IndexOf(
            "private bool TryDrawGroundObjectProjection",
            start,
            StringComparison.Ordinal
        );
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
