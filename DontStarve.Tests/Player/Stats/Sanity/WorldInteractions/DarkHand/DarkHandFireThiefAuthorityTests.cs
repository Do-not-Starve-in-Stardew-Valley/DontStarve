using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.DarkHand;

public sealed class DarkHandFireThiefAuthorityTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";
    private const string Owner = "123456789";
    private const string OtherOwner = "223456789";
    private const string TargetId = "dark-hand-fire-target-1";

    private static string ShippedCatalogPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data",
            "dark-hand-targets.json"
        );

    private static string RuntimeStatusSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkHandFireThief",
            "SmapiDarkHandFireThiefService.cs"
        );

    private static string HostRuntimeSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkHandFireThief",
            "SmapiHostileShadowHost.cs"
        );

    [Fact]
    public void ShippedCatalogEnablesVerifiedFarmTorchWithFullRuntimeEvidence()
    {
        var catalog = LoadShipped();
        var capability = DarkHandFireCapabilityGate.Evaluate(
            catalog,
            DarkHandFireRuntimeEvidence.VerifiedStardew1615
        );

        Assert.Equal(13, catalog.Targets.Count);
        Assert.Equal(1, catalog.EnabledTargetCount);
        Assert.Equal(
            DarkHandFireCapabilityStatus.Available,
            capability.Status
        );
        Assert.Equal(DarkHandFireReasonIds.CapabilityAvailable, capability.Reason);
        Assert.True(capability.CanExecute);
        Assert.True(capability.Evidence.StableTargetRevisionAvailable);
        Assert.True(capability.Evidence.ExplainableLightTargetBindingAvailable);
        Assert.True(capability.Evidence.AtomicExtinguishAdapterAvailable);
        Assert.True(capability.Evidence.ExistingPrivateLeaseAvailable);
    }

    [Fact]
    public void ShippedAllowlistContainsOnlyVerifiedCampfireAndFireplaceIdentities()
    {
        var catalog = LoadShipped();
        var actual = catalog.Targets.Select(target => target.QualifiedItemId).ToHashSet();
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "(BC)146",
            "(BC)278",
            "(F)1792",
            "(F)1794",
            "(F)1796",
            "(F)1798",
            "(F)1800",
            "(F)1866",
            "(F)DesertFireplace",
            "(F)JojaFireplace",
            "(F)JunimoFireplace",
            "(F)RetroFireplace",
            "(F)WizardFireplace",
        };

        Assert.Equal(expected, actual);
        var enabled = Assert.Single(catalog.Targets.Where(target => target.Enabled));
        Assert.Equal("(BC)146", enabled.QualifiedItemId);
        Assert.Equal(new[] { "Farm" }, enabled.LocationAllowlist);
        Assert.All(
            catalog.Targets.Where(target => !ReferenceEquals(target, enabled)),
            target => Assert.False(target.Enabled)
        );
        Assert.All(
            catalog.Targets.Where(target => target.TargetKind == DarkHandFireTargetKind.Campfire),
            target => Assert.Equal(DarkHandFireRuntimeTypeNames.Torch, target.RuntimeTypeFullName)
        );
        Assert.All(
            catalog.Targets.Where(target => target.TargetKind == DarkHandFireTargetKind.Fireplace),
            target => Assert.Equal(
                DarkHandFireRuntimeTypeNames.Furniture,
                target.RuntimeTypeFullName
            )
        );
    }

    [Theory]
    [InlineData("(F)2331")]
    [InlineData("(F)2397")]
    [InlineData("(F)2398")]
    [InlineData("(BC)72")]
    [InlineData("(BC)74")]
    [InlineData("(BC)143")]
    [InlineData("(BC)144")]
    [InlineData("(BC)145")]
    [InlineData("(BC)147")]
    [InlineData("(BC)148")]
    [InlineData("(BC)149")]
    [InlineData("(BC)150")]
    [InlineData("(BC)151")]
    [InlineData("(BC)SomeModdedLight")]
    public void DecorativeBraziersTorchesBonfireAndUnknownSourcesAreNotTargets(
        string qualifiedItemId
    )
    {
        Assert.Null(LoadShipped().FindByQualifiedItemId(qualifiedItemId));
    }

    [Fact]
    public void CatalogParserRejectsUnknownFieldsVersionsWildcardsMismatchesAndDuplicates()
    {
        var shipped = File.ReadAllText(ShippedCatalogPath);

        AssertLoadFailure(
            shipped.Replace(
                "\"Targets\": [",
                "\"Unexpected\": true, \"Targets\": [",
                StringComparison.Ordinal
            ),
            DarkHandFireReasonIds.CatalogRootInvalid
        );
        AssertLoadFailure(
            shipped.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2", StringComparison.Ordinal),
            DarkHandFireReasonIds.CatalogVersionUnsupported
        );
        AssertLoadFailure(
            shipped.Replace("(BC)146", "(BC)*", StringComparison.Ordinal),
            DarkHandFireReasonIds.TargetInvalid
        );
        AssertLoadFailure(
            ReplaceFirst(
                shipped,
                DarkHandFireRuntimeTypeNames.Torch,
                DarkHandFireRuntimeTypeNames.Furniture
            ),
            DarkHandFireReasonIds.TargetInvalid
        );
        AssertLoadFailure(
            ReplaceFirst(shipped, "\"Enabled\": false", "\"Enabled\": true"),
            DarkHandFireReasonIds.TargetInvalid
        );
        AssertLoadFailure(
            shipped.Replace("(BC)278", "(BC)146", StringComparison.Ordinal),
            DarkHandFireReasonIds.TargetQualifiedIdDuplicated
        );
    }

    [Fact]
    public void EnabledCatalogStillRequiresEveryRuntimeCapabilityInOrder()
    {
        var catalog = LoadEnabledCatalog();

        AssertCapability(
            catalog,
            new DarkHandFireRuntimeEvidence(false, false, false, false, false),
            DarkHandFireCapabilityStatus.UnavailableTargetIdentity,
            DarkHandFireReasonIds.TargetIdentityUnavailable
        );
        AssertCapability(
            catalog,
            new DarkHandFireRuntimeEvidence(true, false, false, false, false),
            DarkHandFireCapabilityStatus.UnavailableTargetRevision,
            DarkHandFireReasonIds.TargetRevisionUnavailable
        );
        AssertCapability(
            catalog,
            new DarkHandFireRuntimeEvidence(true, true, false, false, false),
            DarkHandFireCapabilityStatus.UnavailableExplainableLightBinding,
            DarkHandFireReasonIds.ExplainableLightBindingUnavailable
        );
        AssertCapability(
            catalog,
            new DarkHandFireRuntimeEvidence(true, true, true, false, false),
            DarkHandFireCapabilityStatus.UnavailableAtomicAdapter,
            DarkHandFireReasonIds.AtomicAdapterUnavailable
        );
        AssertCapability(
            catalog,
            new DarkHandFireRuntimeEvidence(true, true, true, true, false),
            DarkHandFireCapabilityStatus.UnavailablePrivateLease,
            DarkHandFireReasonIds.PrivateLeaseUnavailable
        );
        AssertCapability(
            catalog,
            AvailableEvidence(),
            DarkHandFireCapabilityStatus.Available,
            DarkHandFireReasonIds.CapabilityAvailable
        );
    }

    [Fact]
    public void RegistryRequiresExactIdentityPositiveRevisionAndMonotonicUpdates()
    {
        var registry = new DarkHandFireTargetRegistry(LoadEnabledCatalog());
        var accepted = registry.Upsert(Snapshot(revision: 7));
        var duplicate = registry.Upsert(Snapshot(revision: 7));
        var stale = registry.Upsert(Snapshot(revision: 6));
        var conflict = registry.Upsert(Snapshot(revision: 7, isOn: false));
        var updated = registry.Upsert(Snapshot(revision: 8, isOn: false));
        var invalid = registry.Upsert(Snapshot(revision: 0));

        Assert.Equal(DarkHandFireRegistryUpdateStatus.Accepted, accepted.Status);
        Assert.Equal(DarkHandFireRegistryUpdateStatus.IgnoredDuplicate, duplicate.Status);
        Assert.Equal(DarkHandFireReasonIds.RegistrySnapshotStale, stale.Reason);
        Assert.Equal(DarkHandFireReasonIds.RegistrySnapshotConflict, conflict.Reason);
        Assert.Equal(DarkHandFireRegistryUpdateStatus.Accepted, updated.Status);
        Assert.Equal(DarkHandFireReasonIds.RegistrySnapshotInvalid, invalid.Reason);
        Assert.True(registry.TryGet(TargetId, out var current));
        Assert.Equal(8, current!.Revision);
        Assert.False(current.IsOn);
    }

    [Fact]
    public void RegistryIsBoundedAndClearRemovesAllWorldReferences()
    {
        var registry = new DarkHandFireTargetRegistry(LoadEnabledCatalog());
        for (var index = 0; index < DarkHandFireTargetRegistry.MaximumTargets; index++)
        {
            Assert.Equal(
                DarkHandFireRegistryUpdateStatus.Accepted,
                registry.Upsert(Snapshot(targetId: $"target-{index}")).Status
            );
        }

        Assert.Equal(
            DarkHandFireReasonIds.RegistryFull,
            registry.Upsert(Snapshot(targetId: "target-overflow")).Reason
        );
        Assert.Equal(DarkHandFireTargetRegistry.MaximumTargets, registry.Count);
        registry.Clear();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void EligibilityRejectsWhitelistModeAuthorityOwnerLocationRangeAndLightFailures()
    {
        var catalog = LoadEnabledCatalog();
        var capability = AvailableCapability(catalog);
        var snapshot = Snapshot();
        var scope = Scope();

        AssertRejected(catalog, capability, snapshot, scope with { IsHostAuthority = false }, DarkHandFireReasonIds.HostAuthorityRequired);
        AssertRejected(catalog, capability, snapshot, scope with { SenderPlayerKey = OtherOwner }, DarkHandFireReasonIds.SenderOwnerMismatch);
        AssertRejected(catalog, capability, snapshot, scope with { ModeId = "Off" }, DarkHandFireReasonIds.FireThiefModeRequired);
        AssertRejected(catalog, capability, snapshot, scope with { ModeId = "Harassment" }, DarkHandFireReasonIds.FireThiefModeRequired);
        AssertRejected(catalog, capability, snapshot, scope with { ModeId = "Thief" }, DarkHandFireReasonIds.FireThiefModeRequired);
        AssertRejected(catalog, capability, snapshot, scope with { OwnerLocationId = "Mine" }, DarkHandFireReasonIds.OwnerLocationMismatch);
        AssertRejected(catalog, capability, snapshot, scope with { OwnerDistanceTiles = 20.001d }, DarkHandFireReasonIds.OwnerOutOfRange);
        AssertRejected(catalog, capability, snapshot, scope with { OwnerDistanceTiles = double.NaN }, DarkHandFireReasonIds.OwnerOutOfRange);
        AssertRejected(catalog, capability, snapshot with { LocationId = "Mine" }, scope, DarkHandFireReasonIds.LocationNotAllowlisted);
        AssertRejected(catalog, capability, snapshot with { HasExplainableLightSource = false }, scope, DarkHandFireReasonIds.ExplainableLightRequired);
        AssertRejected(catalog, capability, snapshot with { AtomicAdapterAvailable = false }, scope, DarkHandFireReasonIds.AtomicAdapterUnavailable);
        AssertRejected(catalog, capability, snapshot with { QualifiedItemId = "(BC)SomeModdedLight" }, scope, DarkHandFireReasonIds.TargetNotAllowlisted);
        AssertRejected(catalog, capability, snapshot with { RuntimeTypeFullName = DarkHandFireRuntimeTypeNames.Furniture }, scope, DarkHandFireReasonIds.TargetScopeMismatch);
        AssertRejected(catalog, capability, snapshot with { OperationId = "dark-hand.fire-thief.forged" }, scope, DarkHandFireReasonIds.TargetScopeMismatch);
        AssertRejected(catalog, capability, snapshot with { IsPresent = false }, scope, DarkHandFireReasonIds.TargetMissing);
        AssertRejected(catalog, capability, snapshot with { IsOn = false }, scope, DarkHandFireReasonIds.FireAlreadyOff);

        var eligible = DarkHandFireEligibilityGate.Evaluate(catalog, capability, snapshot, scope, requireFireOn: true);
        Assert.True(eligible.IsEligible, eligible.Reason);
        Assert.Equal(DarkHandFireReasonIds.Eligible, eligible.Reason);
    }

    [Fact]
    public void ValidHostLeaseCommitsExactlyOnceAndRequestsLightRefreshWithoutDeletingTarget()
    {
        var fixture = AvailableFixture();
        var issued = fixture.Service.TryIssue(Request(1), Scope(), nowTick: 10);
        var committed = fixture.Service.Commit(
            issued.Lease,
            Scope(),
            nowTick: 11,
            fixture.Adapter
        );
        var replay = fixture.Service.Commit(
            issued.Lease,
            Scope(),
            nowTick: 12,
            fixture.Adapter
        );

        Assert.True(issued.Issued, issued.Reason);
        Assert.True(committed.Applied, committed.Reason);
        Assert.True(committed.WorldMutationApplied);
        Assert.True(committed.LightRefreshRequested);
        Assert.Equal(DarkHandFireReasonIds.CommitApplied, committed.Reason);
        Assert.Equal(DarkHandFireCommitStatus.Duplicate, replay.Status);
        Assert.Equal(DarkHandFireReasonIds.ReceiptDuplicate, replay.Reason);
        Assert.Same(committed.Receipt, replay.Receipt);
        Assert.Equal(1, fixture.Adapter.Calls);
        Assert.NotNull(fixture.Adapter.LastPlan);
        Assert.Equal(TargetId, fixture.Adapter.LastPlan!.TargetId);
        Assert.Equal(1, fixture.Service.SuccessfulCommitCount);
    }

    [Fact]
    public void ValidNonceIsConsumedBeforeMutableEligibilityRecheck()
    {
        var fixture = AvailableFixture();
        var rejected = fixture.Service.TryIssue(
            Request(1),
            Scope() with { OwnerDistanceTiles = 21d },
            nowTick: 10
        );
        var replay = fixture.Service.TryIssue(Request(1), Scope(), nowTick: 11);

        Assert.Equal(DarkHandFireReasonIds.OwnerOutOfRange, rejected.Reason);
        Assert.Equal("dark-hand.lease-nonce-replay", replay.Reason);
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public void ExpiredChangedAndOldSessionLeasesNeverCallAdapter()
    {
        var expired = AvailableFixture(lifetimeTicks: 5);
        var expiredLease = expired.Service.TryIssue(Request(1), Scope(), 10).Lease!;
        var expiredResult = expired.Service.Commit(expiredLease, Scope(), 15, expired.Adapter);

        var changed = AvailableFixture();
        var changedLease = changed.Service.TryIssue(Request(1), Scope(), 10).Lease!;
        Assert.Equal(
            DarkHandFireRegistryUpdateStatus.Accepted,
            changed.Registry.Upsert(Snapshot(revision: 8, isOn: false)).Status
        );
        var changedResult = changed.Service.Commit(changedLease, Scope(), 11, changed.Adapter);

        var oldSession = AvailableFixture();
        var oldLease = oldSession.Service.TryIssue(Request(1), Scope(), 10).Lease!;
        Assert.True(oldSession.Authority.BeginSession(SessionB, out var reason), reason);
        var oldResult = oldSession.Service.Commit(oldLease, Scope(), 11, oldSession.Adapter);

        Assert.Equal("dark-hand.lease-expired", expiredResult.Reason);
        Assert.Equal("dark-hand.lease-target-revision-changed", changedResult.Reason);
        Assert.Equal("dark-hand.lease-session-or-owner-invalid", oldResult.Reason);
        Assert.Equal(0, expired.Adapter.Calls);
        Assert.Equal(0, changed.Adapter.Calls);
        Assert.Equal(0, oldSession.Adapter.Calls);
    }

    [Fact]
    public void AlreadyExtinguishedRevisionChangeIsIdempotentAndDoesNotBorrowOldLease()
    {
        var fixture = AvailableFixture();
        var lease = fixture.Service.TryIssue(Request(1), Scope(), 10).Lease!;
        Assert.Equal(
            DarkHandFireRegistryUpdateStatus.Accepted,
            fixture.Registry.Upsert(Snapshot(revision: 8, isOn: false)).Status
        );

        var first = fixture.Service.Commit(lease, Scope(), 11, fixture.Adapter);
        var repeat = fixture.Service.Commit(lease, Scope(), 12, fixture.Adapter);

        Assert.Equal("dark-hand.lease-target-revision-changed", first.Reason);
        Assert.Equal("dark-hand.lease-target-revision-changed", repeat.Reason);
        Assert.Equal(0, fixture.Adapter.Calls);
        Assert.Equal(0, fixture.Service.SuccessfulCommitCount);
    }

    [Fact]
    public void AdapterRejectRollbackAndInvalidPostconditionsNeverReportSuccess()
    {
        AssertAdapterFailure(
            new DarkHandFireAdapterResult(
                DarkHandFireAdapterStatus.Rejected,
                "synthetic-preflight-rejected",
                TargetStillPresent: true,
                IsFireOnAfter: true,
                LightRefreshRequested: false
            ),
            DarkHandFireReasonIds.CommitAdapterRejected
        );
        AssertAdapterFailure(
            new DarkHandFireAdapterResult(
                DarkHandFireAdapterStatus.RolledBack,
                "synthetic-rolled-back",
                TargetStillPresent: true,
                IsFireOnAfter: true,
                LightRefreshRequested: false
            ),
            DarkHandFireReasonIds.CommitAdapterRejected
        );
        AssertAdapterFailure(
            new DarkHandFireAdapterResult(
                DarkHandFireAdapterStatus.Applied,
                "synthetic-invalid-applied",
                TargetStillPresent: false,
                IsFireOnAfter: false,
                LightRefreshRequested: true
            ),
            DarkHandFireReasonIds.CommitAdapterContractInvalid
        );
        AssertAdapterFailure(
            new DarkHandFireAdapterResult(
                DarkHandFireAdapterStatus.Applied,
                "synthetic-no-light-refresh",
                TargetStillPresent: true,
                IsFireOnAfter: false,
                LightRefreshRequested: false
            ),
            DarkHandFireReasonIds.CommitAdapterContractInvalid
        );
    }

    [Fact]
    public void TwoOwnersMayUseSameNonceButForgedSenderCannotBorrowOwnerLane()
    {
        var catalog = LoadEnabledCatalog();
        var capability = AvailableCapability(catalog);
        var registry = new DarkHandFireTargetRegistry(catalog);
        Assert.Equal(DarkHandFireRegistryUpdateStatus.Accepted, registry.Upsert(Snapshot()).Status);
        var authority = StartedAuthority();
        var service = new DarkHandFireThiefOperationService(catalog, capability, authority, registry);

        var first = service.TryIssue(Request(1, owner: Owner), Scope(owner: Owner), 10);
        var second = service.TryIssue(
            Request(1, owner: OtherOwner),
            Scope(owner: OtherOwner),
            11
        );
        var forged = service.TryIssue(
            Request(2, owner: Owner),
            Scope(owner: Owner) with { SenderPlayerKey = OtherOwner },
            12
        );

        Assert.True(first.Issued, first.Reason);
        Assert.True(second.Issued, second.Reason);
        Assert.Equal("dark-hand.lease-request-invalid", forged.Reason);
    }

    [Fact]
    public void ForgedCommitSenderCannotConsumeTheOwnersLease()
    {
        var fixture = AvailableFixture();
        var lease = fixture.Service.TryIssue(Request(1), Scope(), 10).Lease!;

        var forged = fixture.Service.Commit(
            lease,
            Scope() with { SenderPlayerKey = OtherOwner },
            11,
            fixture.Adapter
        );
        var accepted = fixture.Service.Commit(lease, Scope(), 12, fixture.Adapter);

        Assert.Equal("dark-hand.lease-session-or-owner-invalid", forged.Reason);
        Assert.True(accepted.Applied, accepted.Reason);
        Assert.Equal(1, fixture.Adapter.Calls);
    }

    [Fact]
    public void LightSourceEvidenceAloneCannotCreateARegistryTargetOrLease()
    {
        var catalog = LoadEnabledCatalog();
        var capability = AvailableCapability(catalog);
        var registry = new DarkHandFireTargetRegistry(catalog);
        var authority = StartedAuthority();
        var service = new DarkHandFireThiefOperationService(catalog, capability, authority, registry);

        var result = service.TryIssue(Request(1), Scope(), 10);

        Assert.Equal(DarkHandFireReasonIds.TargetNotRegistered, result.Reason);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CatalogOwnerDelegatesMutationAndReportsCoordinatorWiring()
    {
        var source = File.ReadAllText(RuntimeStatusSourcePath);

        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemRegistry.Create", source, StringComparison.Ordinal);
        Assert.DoesNotContain("objects.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("furniture.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("performRemoveAction", source, StringComparison.Ordinal);
        Assert.DoesNotContain("setFireplace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn = false", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeTargetAuthorityInstalled: runtimeWired", source, StringComparison.Ordinal);
        Assert.Contains("WorldMutationHookInstalled: runtimeWired", source, StringComparison.Ordinal);
        Assert.Contains("SuccessfulExtinguishCount: successfulCountProvider()", source, StringComparison.Ordinal);
        Assert.Contains("VerifiedStardew1615", source, StringComparison.Ordinal);
        Assert.Contains("DarkHandFireRuntimeEvidence.Current", source, StringComparison.Ordinal);
        Assert.Contains("StateEventPublished += OnStateEventPublished", source, StringComparison.Ordinal);
        Assert.Contains("WorldBoundaryStarting += OnWorldBoundaryStarting", source, StringComparison.Ordinal);
        Assert.Contains("SessionClearing += OnSessionClearing", source, StringComparison.Ordinal);
        Assert.Contains("SanityStateEventKind.OwnerInvalidated", source, StringComparison.Ordinal);
        Assert.Contains("SanityStateEventKind.SystemDisabled", source, StringComparison.Ordinal);
        Assert.Contains("SanityStateEventKind.WorldCleanup", source, StringComparison.Ordinal);
        Assert.Contains("registry.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedHostKeepsTask07TargetAuthorityUnavailable()
    {
        var source = File.ReadAllText(HostRuntimeSourcePath);

        Assert.Contains(
            "UnavailableDarkHandLeaseTargetAuthority.Instance",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("DarkHandFireTargetRegistry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDarkHandFireExtinguishAdapter", source, StringComparison.Ordinal);
    }

    private static void AssertCapability(
        DarkHandFireTargetCatalog catalog,
        DarkHandFireRuntimeEvidence evidence,
        DarkHandFireCapabilityStatus expectedStatus,
        string expectedReason
    )
    {
        var capability = DarkHandFireCapabilityGate.Evaluate(catalog, evidence);
        Assert.Equal(expectedStatus, capability.Status);
        Assert.Equal(expectedReason, capability.Reason);
        Assert.Equal(1, capability.EnabledTargetCount);
        Assert.Equal(
            expectedStatus == DarkHandFireCapabilityStatus.Available,
            capability.CanExecute
        );
    }

    private static void AssertRejected(
        DarkHandFireTargetCatalog catalog,
        DarkHandFireCapability capability,
        DarkHandFireTargetSnapshot snapshot,
        DarkHandFireOperationScope scope,
        string expectedReason
    )
    {
        var result = DarkHandFireEligibilityGate.Evaluate(
            catalog,
            capability,
            snapshot,
            scope,
            requireFireOn: true
        );
        Assert.False(result.IsEligible);
        Assert.Equal(expectedReason, result.Reason);
    }

    private static void AssertAdapterFailure(
        DarkHandFireAdapterResult adapterResult,
        string expectedReason
    )
    {
        var fixture = AvailableFixture(adapterResult: adapterResult);
        var lease = fixture.Service.TryIssue(Request(1), Scope(), 10).Lease!;
        var result = fixture.Service.Commit(lease, Scope(), 11, fixture.Adapter);

        Assert.False(result.Applied);
        Assert.False(result.WorldMutationApplied);
        Assert.False(result.LightRefreshRequested);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(1, fixture.Adapter.Calls);
        Assert.Equal(0, fixture.Service.SuccessfulCommitCount);
    }

    private static void AssertLoadFailure(string json, string expectedReason)
    {
        var load = DarkHandFireTargetCatalog.Load(json);
        Assert.False(load.IsAvailable);
        Assert.Equal(expectedReason, load.Reason);
        Assert.False(load.Catalog.IsAvailable);
        Assert.Empty(load.Catalog.Targets);
    }

    private static DarkHandFireTargetCatalog LoadShipped()
    {
        var load = DarkHandFireTargetCatalog.Load(File.ReadAllText(ShippedCatalogPath));
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static DarkHandFireTargetCatalog LoadEnabledCatalog()
    {
        var json =
            "{\"SchemaVersion\":1,\"ContractId\":\"sanity.dark-hand-fire-targets.v1\"," +
            "\"Targets\":[{\"Id\":\"synthetic-campfire\",\"QualifiedItemId\":\"(BC)146\"," +
            "\"RuntimeTypeFullName\":\"StardewValley.Torch\",\"TargetKind\":\"Campfire\"," +
            "\"OperationId\":\"dark-hand.fire-thief.extinguish.v1\",\"Enabled\":true," +
            "\"LocationAllowlist\":[\"Farm\"],\"Evidence\":[\"synthetic-contract-test\"]," +
            "\"Reason\":\"synthetic-enabled\"}]}";
        var load = DarkHandFireTargetCatalog.Load(json);
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static DarkHandFireCapability AvailableCapability(
        DarkHandFireTargetCatalog catalog
    )
    {
        var capability = DarkHandFireCapabilityGate.Evaluate(catalog, AvailableEvidence());
        Assert.True(capability.CanExecute, capability.Reason);
        return capability;
    }

    private static DarkHandFireRuntimeEvidence AvailableEvidence() =>
        new(true, true, true, true, true);

    private static DarkHandFireTargetSnapshot Snapshot(
        string targetId = TargetId,
        long revision = 7,
        bool isOn = true
    ) =>
        new(
            "synthetic-campfire",
            targetId,
            "Farm",
            "(BC)146",
            DarkHandFireRuntimeTypeNames.Torch,
            DarkHandFireTargetKind.Campfire,
            DarkHandFireOperationIds.Extinguish,
            revision,
            IsPresent: true,
            IsOn: isOn,
            HasExplainableLightSource: true,
            AtomicAdapterAvailable: true
        );

    private static DarkHandFireOperationScope Scope(string owner = Owner) =>
        new(
            IsHostAuthority: true,
            SenderPlayerKey: owner,
            OwnerPlayerKey: owner,
            ModeId: DarkHandFireModeIds.FireThief,
            OwnerLocationId: "Farm",
            OwnerDistanceTiles: 5d
        );

    private static DarkHandInteractionLeaseRequest Request(
        long nonce,
        string owner = Owner,
        long revision = 7,
        string session = SessionA
    ) =>
        new()
        {
            SessionId = session,
            Nonce = nonce,
            OwnerPlayerKey = owner,
            LocationId = "Farm",
            TargetId = TargetId,
            OperationId = DarkHandFireOperationIds.Extinguish,
            ObservedTargetRevision = revision,
        };

    private static DarkHandInteractionLeaseAuthority StartedAuthority(
        long lifetimeTicks = 120
    )
    {
        var authority = new DarkHandInteractionLeaseAuthority(lifetimeTicks);
        Assert.True(authority.BeginSession(SessionA, out var reason), reason);
        return authority;
    }

    private static AvailableOperationFixture AvailableFixture(
        long lifetimeTicks = 120,
        DarkHandFireAdapterResult? adapterResult = null
    )
    {
        var catalog = LoadEnabledCatalog();
        var registry = new DarkHandFireTargetRegistry(catalog);
        Assert.Equal(
            DarkHandFireRegistryUpdateStatus.Accepted,
            registry.Upsert(Snapshot()).Status
        );
        var authority = StartedAuthority(lifetimeTicks);
        var service = new DarkHandFireThiefOperationService(
            catalog,
            AvailableCapability(catalog),
            authority,
            registry
        );
        return new AvailableOperationFixture(
            service,
            registry,
            authority,
            new RecordingAdapter(
                adapterResult
                    ?? new DarkHandFireAdapterResult(
                        DarkHandFireAdapterStatus.Applied,
                        DarkHandFireReasonIds.CommitApplied,
                        TargetStillPresent: true,
                        IsFireOnAfter: false,
                        LightRefreshRequested: true
                    )
            )
        );
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return value[..index] + newValue + value[(index + oldValue.Length)..];
    }

    private sealed record AvailableOperationFixture(
        DarkHandFireThiefOperationService Service,
        DarkHandFireTargetRegistry Registry,
        DarkHandInteractionLeaseAuthority Authority,
        RecordingAdapter Adapter
    );

    private sealed class RecordingAdapter : IDarkHandFireExtinguishAdapter
    {
        private readonly DarkHandFireAdapterResult result;

        internal RecordingAdapter(DarkHandFireAdapterResult result)
        {
            this.result = result;
        }

        internal int Calls { get; private set; }
        internal DarkHandFireCommitPlan? LastPlan { get; private set; }

        public DarkHandFireAdapterResult Commit(DarkHandFireCommitPlan plan)
        {
            Calls++;
            LastPlan = plan;
            return result;
        }
    }
}
