using System.Text.Json;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.DarkHand;

public sealed class DarkHandHarassmentTransactionTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Owner = "123456789";
    private const string OtherOwner = "223456789";
    private const string TargetId = "machine-1";

    private static string ShippedCatalogPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data",
            "dark-hand-machines.json"
        );

    private static string RuntimeServiceSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "MachineInteraction",
            "SmapiDarkHandHarassmentService.cs"
        );

    private static string ModEntrySourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "ShadowProjection",
            "ModEntry.cs"
        );

    [Fact]
    public void VerifiedShippedProductionAllowsDelayAndRejectsMissingTarget()
    {
        var catalog = LoadShipped();
        var capability = MachineInteractionCapabilityGate.Evaluate(
            catalog,
            MachineRuntimeEvidence.VerifiedStardew1615
        );
        var authority = LeaseAuthority();
        var service = new DarkHandHarassmentOperationService(
            catalog,
            capability,
            authority,
            new MachineSnapshotRegistry()
        );

        var issued = service.TryIssue(Request(Owner, 1, 1), Scope(Owner), 10);
        var committed = service.Commit(
            null,
            Scope(Owner),
            10,
            new SequenceRandom(0d),
            new RecordingAdapter(OrdinarySnapshot())
        );

        Assert.Equal(MachineInteractionCapabilityStatus.Available, capability.Status);
        Assert.True(capability.CanCommitDelay);
        Assert.False(capability.CanCommitEject);
        Assert.False(issued.Issued);
        Assert.Equal(DarkHandHarassmentReasonIds.TargetMissing, issued.Reason);
        Assert.Equal("dark-hand.lease-envelope-invalid", committed.Reason);
        Assert.Equal(0, service.ReceiptCount);
        Assert.Equal(0, service.SuccessfulCommitCount);
    }

    [Theory]
    [InlineData(0d, 25d, 125)]
    [InlineData(1d, 125d, 225)]
    public void OrdinaryRunningDelayUsesInclusiveBoundsAndCeiling(
        double delayRoll,
        double expectedPercent,
        int expectedMinutes
    )
    {
        var fixture = Fixture(OrdinarySnapshot(minutes: 100));
        var random = new SequenceRandom(0.499999d, delayRoll);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var lease = fixture.Issue(Owner, nonce: 1, nowTick: 10);

        var result = fixture.Service.Commit(
            lease,
            Scope(Owner),
            11,
            random,
            adapter
        );

        Assert.True(result.Applied);
        Assert.Equal(DarkHandHarassmentAction.Delay, result.Receipt!.Action);
        Assert.Equal(expectedPercent, result.Receipt.DelayPercent);
        Assert.Equal(expectedMinutes, result.Receipt.After!.MinutesUntilReady);
        Assert.Equal(2, random.CallCount);
        Assert.Equal(1, adapter.DelayCommitCount);
        Assert.Equal(0, adapter.EjectCommitCount);
        Assert.False(result.Receipt.ItemLandedExactlyOnce);
    }

    [Fact]
    public void ExactHalfSelectsEjectAndLandingProofPrecedesAtomicClear()
    {
        var fixture = Fixture(OrdinarySnapshot(showNextIndex: true));
        var random = new SequenceRandom(0.5d);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var lease = fixture.Issue(Owner, 1, 10);

        var result = fixture.Service.Commit(
            lease,
            Scope(Owner),
            11,
            random,
            adapter
        );

        Assert.True(result.Applied);
        Assert.Equal(DarkHandHarassmentAction.Eject, result.Receipt!.Action);
        Assert.Equal(new[] { "reserve-landing", "commit-eject" }, adapter.CallOrder);
        Assert.True(result.Receipt.LandingProvenBeforeMutation);
        Assert.True(result.Receipt.ItemLandedExactlyOnce);
        Assert.Null(result.Receipt.After!.HeldOutput);
        Assert.Equal(string.Empty, result.Receipt.After.ActiveRuleId);
        Assert.Equal(0, result.Receipt.After.MinutesUntilReady);
        Assert.False(result.Receipt.After.ReadyForHarvest);
        Assert.False(result.Receipt.After.ShowNextIndex);
        Assert.Equal(result.Receipt.Before.LastInputItem, result.Receipt.After.LastInputItem);
        Assert.Equal(1, random.CallCount);
    }

    [Fact]
    public void ReadyDelayBranchIsInvalidWithoutDelayRollOrAdapterCall()
    {
        var fixture = Fixture(OrdinarySnapshot(ready: true, minutes: 0));
        var random = new SequenceRandom(0.49d, 1d);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var lease = fixture.Issue(Owner, 1, 10);

        var result = fixture.Service.Commit(
            lease,
            Scope(Owner),
            11,
            random,
            adapter
        );

        Assert.Equal(DarkHandHarassmentReasonIds.ReadyDelayInvalid, result.Reason);
        Assert.Equal(DarkHandHarassmentReceiptOutcome.Rejected, result.Receipt!.Outcome);
        Assert.True(result.Receipt.RollbackVerified);
        Assert.Equal(1, random.CallCount);
        Assert.Equal(0, adapter.DelayCommitCount);
        Assert.Equal(0, adapter.EjectCommitCount);
    }

    [Fact]
    public void ReadyEjectBranchCanApplyWithHeldOutputAndLandingProof()
    {
        var fixture = Fixture(OrdinarySnapshot(ready: true, minutes: 0, showNextIndex: true));
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new SequenceRandom(0.5d),
            adapter
        );

        Assert.True(result.Applied);
        Assert.Equal(DarkHandHarassmentReasonIds.EjectApplied, result.Reason);
        Assert.Equal(MachineLifecycleState.Empty, result.Receipt!.After!.State);
    }

    [Theory]
    [InlineData("PermanentInput")]
    [InlineData("NoInputInfiniteOutput")]
    [InlineData("MultiInput")]
    public void NonOrdinaryCategoriesDegradeToDelayOnly(string categoryText)
    {
        var category = Enum.Parse<MachineInteractionCategory>(categoryText);
        var fixture = Fixture(Snapshot(category, minutes: 100));
        var random = new SequenceRandom(0d);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            random,
            adapter
        );

        Assert.True(result.Applied);
        Assert.Equal(DarkHandHarassmentAction.Delay, result.Receipt!.Action);
        Assert.Null(result.Receipt.ActionRoll);
        Assert.Equal(1, random.CallCount);
        Assert.Equal(1, adapter.DelayCommitCount);
        Assert.Equal(0, adapter.ReserveCount);
        Assert.Equal(0, adapter.EjectCommitCount);
    }

    [Fact]
    public void DayBasedNoInputStillChoosesOnlyDelayButFailsClosedWhenMinuteDelayIsUnavailable()
    {
        var snapshot = Snapshot(
            MachineInteractionCategory.NoInputInfiniteOutput,
            minutes: 100,
            schedule: MachineScheduleKind.DayBased
        ) with
        {
            CanDelay = false,
        };
        var fixture = Fixture(snapshot);
        var issued = fixture.Service.TryIssue(
            Request(Owner, 1, snapshot.AuthorityRevision),
            Scope(Owner),
            10
        );

        Assert.False(issued.Issued);
        Assert.Equal(DarkHandHarassmentReasonIds.OperationUnavailable, issued.Reason);
    }

    [Theory]
    [InlineData("dark-hand.harassment.inventory-full")]
    [InlineData("dark-hand.harassment.ground-crowded")]
    public void LandingPreflightFailureNeverClearsMachineOrProducesOutput(string reason)
    {
        var fixture = Fixture(OrdinarySnapshot());
        var adapter = new RecordingAdapter(fixture.Snapshot)
        {
            LandingFailureReason = reason,
        };
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new SequenceRandom(0.5d),
            adapter
        );

        Assert.Equal(reason, result.Reason);
        Assert.Equal(1, adapter.ReserveCount);
        Assert.Equal(0, adapter.EjectCommitCount);
        Assert.False(result.Receipt!.LandingProvenBeforeMutation);
        Assert.False(result.Receipt.WorldMutationApplied);
        Assert.Equal(result.Receipt.Before, result.Receipt.After);
    }

    [Fact]
    public void OutputLandingFailureAfterReservationRollsBackEveryFrozenField()
    {
        var snapshot = OrdinarySnapshot(showNextIndex: true);
        var fixture = Fixture(snapshot);
        var adapter = new RecordingAdapter(snapshot)
        {
            EjectStatus = DarkHandHarassmentAdapterStatus.RolledBack,
        };
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new SequenceRandom(0.5d),
            adapter
        );

        Assert.Equal(DarkHandHarassmentTransactionDisposition.RolledBack, result.Disposition);
        Assert.Equal(DarkHandHarassmentReasonIds.AdapterRolledBack, result.Reason);
        Assert.True(result.Receipt!.LandingProvenBeforeMutation);
        Assert.True(result.Receipt.RollbackVerified);
        Assert.False(result.Receipt.WorldMutationApplied);
        Assert.False(result.Receipt.ItemLandedExactlyOnce);
        Assert.Equal(result.Receipt.Before, result.Receipt.After);
    }

    [Fact]
    public void DelayFailureAlsoRequiresFieldEquivalentRollback()
    {
        var snapshot = OrdinarySnapshot();
        var fixture = Fixture(snapshot);
        var adapter = new RecordingAdapter(snapshot)
        {
            DelayStatus = DarkHandHarassmentAdapterStatus.RolledBack,
        };
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new SequenceRandom(0d, 0d),
            adapter
        );

        Assert.Equal(DarkHandHarassmentTransactionDisposition.RolledBack, result.Disposition);
        Assert.True(result.Receipt!.RollbackVerified);
        Assert.Equal(result.Receipt.Before, result.Receipt.After);
    }

    [Fact]
    public void FalseRollbackClaimWithDriftedFieldIsRejectedAsAdapterContractViolation()
    {
        var snapshot = OrdinarySnapshot();
        var fixture = Fixture(snapshot);
        var adapter = new RecordingAdapter(snapshot)
        {
            DelayStatus = DarkHandHarassmentAdapterStatus.RolledBack,
            DriftRollbackMinutes = true,
        };
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new SequenceRandom(0d, 0d),
            adapter
        );

        Assert.Equal(DarkHandHarassmentReasonIds.AdapterContractInvalid, result.Reason);
        Assert.False(result.Receipt!.RollbackVerified);
        Assert.NotEqual(result.Receipt.Before, result.Receipt.After);
    }

    [Fact]
    public void RevisionDriftBetweenIssueAndCommitRejectsBeforeRngOrAdapter()
    {
        var fixture = Fixture(OrdinarySnapshot(revision: 7));
        var lease = fixture.Issue(Owner, 1, 10);
        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            fixture.Registry.Upsert(OrdinarySnapshot(revision: 8, minutes: 90)).Status
        );
        var random = new SequenceRandom(0d, 0d);
        var adapter = new RecordingAdapter(fixture.Snapshot);

        var result = fixture.Service.Commit(lease, Scope(Owner), 11, random, adapter);

        Assert.Equal("dark-hand.lease-target-revision-changed", result.Reason);
        Assert.Equal(0, random.CallCount);
        Assert.Equal(0, adapter.TotalCommitCount);
    }

    [Fact]
    public void SameRevisionFingerprintDriftQuarantinesTargetUntilHigherRevision()
    {
        var fixture = Fixture(OrdinarySnapshot(revision: 7));
        var lease = fixture.Issue(Owner, 1, 10);
        var conflict = fixture.Snapshot with
        {
            StateFingerprint = new string('F', 64),
            MinutesUntilReady = fixture.Snapshot.MinutesUntilReady + 1,
        };

        Assert.Equal(
            MachineInteractionReasonIds.RegistrySnapshotConflict,
            fixture.Registry.Upsert(conflict).Reason
        );
        Assert.Equal(
            MachineInteractionReasonIds.SnapshotQuarantined,
            fixture.Registry.ValidateCurrent(
                TargetId,
                fixture.Snapshot.AuthorityRevision,
                fixture.Snapshot.StateFingerprint
            )
        );
        Assert.False(fixture.Registry.TryGet(TargetId, out _));

        var result = fixture.Service.Commit(
            lease,
            Scope(Owner),
            11,
            new SequenceRandom(0d),
            new RecordingAdapter(fixture.Snapshot)
        );
        Assert.Equal("dark-hand.lease-target-revision-changed", result.Reason);

        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            fixture.Registry.Upsert(OrdinarySnapshot(revision: 8)).Status
        );
        Assert.True(fixture.Registry.TryGet(TargetId, out _));
    }

    [Fact]
    public void ExactDuplicateLeaseReturnsOriginalReceiptWithoutRerollOrSecondMutation()
    {
        var fixture = Fixture(OrdinarySnapshot());
        var random = new SequenceRandom(0d, 0d, 1d);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var lease = fixture.Issue(Owner, 1, 10);
        var first = fixture.Service.Commit(lease, Scope(Owner), 11, random, adapter);
        var duplicate = fixture.Service.Commit(lease.Clone(), Scope(Owner), 12, random, adapter);

        Assert.True(first.Applied);
        Assert.Equal(DarkHandHarassmentTransactionDisposition.Duplicate, duplicate.Disposition);
        Assert.Same(first.Receipt, duplicate.Receipt);
        Assert.Equal(2, random.CallCount);
        Assert.Equal(1, adapter.TotalCommitCount);
        Assert.Equal(1, fixture.Service.SuccessfulCommitCount);
    }

    [Fact]
    public void ExpiredLeaseIsRejectedBeforeRngAndMutation()
    {
        var fixture = Fixture(OrdinarySnapshot(), lifetimeTicks: 5);
        var lease = fixture.Issue(Owner, 1, 10);
        var random = new SequenceRandom(0d);
        var adapter = new RecordingAdapter(fixture.Snapshot);

        var result = fixture.Service.Commit(lease, Scope(Owner), 15, random, adapter);

        Assert.Equal("dark-hand.lease-expired", result.Reason);
        Assert.Equal(0, random.CallCount);
        Assert.Equal(0, adapter.TotalCommitCount);
    }

    [Fact]
    public void SaveLoadSessionClearInvalidatesOldLeaseAndDropsSnapshotsAndReceipts()
    {
        var fixture = Fixture(OrdinarySnapshot());
        var lease = fixture.Issue(Owner, 1, 10);
        fixture.Service.ClearWindow();
        fixture.Registry.Clear();
        Assert.True(fixture.LeaseAuthority.BeginSession(SessionB, out var reason), reason);

        var result = fixture.Service.Commit(
            lease,
            Scope(Owner),
            11,
            new SequenceRandom(0d),
            new RecordingAdapter(fixture.Snapshot)
        );

        Assert.Equal("dark-hand.lease-session-or-owner-invalid", result.Reason);
        Assert.Equal(0, fixture.Service.ReceiptCount);
        Assert.Equal(0, fixture.Registry.Count);
    }

    [Fact]
    public void CompetingLeasesAtOneRevisionAllowOnlyFirstAtomicCommit()
    {
        var fixture = Fixture(OrdinarySnapshot(revision: 7));
        var firstLease = fixture.Issue(Owner, 1, 10);
        var secondLease = fixture.Issue(OtherOwner, 1, 10);
        var firstAdapter = new RecordingAdapter(fixture.Snapshot);
        var first = fixture.Service.Commit(
            firstLease,
            Scope(Owner),
            11,
            new SequenceRandom(0d, 0d),
            firstAdapter
        );
        var secondRandom = new SequenceRandom(0d, 0d);
        var secondAdapter = new RecordingAdapter(fixture.Snapshot);
        var second = fixture.Service.Commit(
            secondLease,
            Scope(OtherOwner),
            11,
            secondRandom,
            secondAdapter
        );

        Assert.True(first.Applied);
        Assert.Equal("dark-hand.lease-target-revision-changed", second.Reason);
        Assert.Equal(0, secondRandom.CallCount);
        Assert.Equal(0, secondAdapter.TotalCommitCount);
    }

    [Theory]
    [InlineData(false, Owner, Owner, "Harassment", "Farm", 5d, "dark-hand.harassment.host-authority-required")]
    [InlineData(true, "223456789", Owner, "Harassment", "Farm", 5d, "dark-hand.harassment.sender-owner-mismatch")]
    [InlineData(true, Owner, Owner, "Thief", "Farm", 5d, "dark-hand.harassment.mode-required")]
    [InlineData(true, Owner, Owner, "Harassment", "Mine", 5d, "dark-hand.harassment.owner-location-mismatch")]
    [InlineData(true, Owner, Owner, "Harassment", "Farm", 20.001d, "dark-hand.harassment.owner-out-of-range")]
    public void ScopeForgeryAndClientOptimismCannotObtainLease(
        bool isHost,
        string sender,
        string owner,
        string mode,
        string location,
        double distance,
        string expectedReason
    )
    {
        var fixture = Fixture(OrdinarySnapshot());
        var scope = new DarkHandHarassmentOperationScope(
            isHost,
            sender,
            owner,
            mode,
            location,
            distance
        );
        var issued = fixture.Service.TryIssue(
            Request(sender, 1, fixture.Snapshot.AuthorityRevision),
            scope,
            10
        );

        Assert.False(issued.Issued);
        Assert.Equal(expectedReason, issued.Reason);
    }

    [Fact]
    public void InvalidRngSampleBecomesTerminalReceiptAndNeverCallsAdapter()
    {
        var fixture = Fixture(OrdinarySnapshot());
        var random = new SequenceRandom(double.NaN, 0d);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var lease = fixture.Issue(Owner, 1, 10);

        var first = fixture.Service.Commit(lease, Scope(Owner), 11, random, adapter);
        var duplicate = fixture.Service.Commit(lease.Clone(), Scope(Owner), 12, random, adapter);

        Assert.Equal(DarkHandHarassmentReasonIds.RandomInvalid, first.Reason);
        Assert.Equal(DarkHandHarassmentTransactionDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(1, random.CallCount);
        Assert.Equal(0, adapter.TotalCommitCount);
    }

    [Fact]
    public void DelayOverflowRejectsBeforeAdapterAndPreservesFields()
    {
        var snapshot = OrdinarySnapshot(minutes: int.MaxValue);
        var fixture = Fixture(snapshot);
        var adapter = new RecordingAdapter(snapshot);
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new SequenceRandom(0d, 1d),
            adapter
        );

        Assert.Equal(DarkHandHarassmentReasonIds.DelayOverflow, result.Reason);
        Assert.Equal(result.Receipt!.Before, result.Receipt.After);
        Assert.Equal(0, adapter.TotalCommitCount);
    }

    [Fact]
    public void UnknownAndUnsupportedTargetsNeverEnterHarassmentAuthority()
    {
        var catalog = EnabledCatalog(
            "(BC)AnvilFixture",
            MachineInteractionCategory.Unsupported,
            allowDelay: true,
            allowEject: true
        );
        var snapshot = Snapshot(MachineInteractionCategory.Unsupported, qualifiedItemId: "(BC)AnvilFixture") with
        {
            CanDelay = false,
            CanEject = false,
        };
        var fixture = Fixture(snapshot, catalog);

        var issued = fixture.Service.TryIssue(Request(Owner, 1, 1), Scope(Owner), 10);

        Assert.False(issued.Issued);
        Assert.Equal(DarkHandHarassmentReasonIds.OperationUnavailable, issued.Reason);
    }

    [Fact]
    public void ProductionCatalogOwnerDelegatesBoundedMutationToCoordinator()
    {
        var serviceSource = File.ReadAllText(RuntimeServiceSourcePath);
        var modEntrySource = File.ReadAllText(ModEntrySourcePath);

        Assert.Contains("MachineRuntimeEvidence.Current", serviceSource, StringComparison.Ordinal);
        Assert.Contains("RuntimeTargetAuthorityInstalled: runtimeWired", serviceSource, StringComparison.Ordinal);
        Assert.Contains("WorldMutationHookInstalled: runtimeWired", serviceSource, StringComparison.Ordinal);
        Assert.Contains("VerifiedStardew1615", serviceSource, StringComparison.Ordinal);
        Assert.Contains("SmapiDarkHandHarassmentService", modEntrySource, StringComparison.Ordinal);
        Assert.Contains("_darkHandLeaseCoordinator", modEntrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("location.Objects", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MinutesUntilReady =", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("heldObject", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("readyForHarvest", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", serviceSource, StringComparison.Ordinal);
    }

    private static MachineTargetCatalog LoadShipped()
    {
        var load = MachineTargetCatalog.Load(File.ReadAllText(ShippedCatalogPath));
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static HarassmentFixture Fixture(
        MachineInteractionSnapshot snapshot,
        MachineTargetCatalog? catalog = null,
        long lifetimeTicks = 120
    )
    {
        catalog ??= EnabledCatalog(
            snapshot.QualifiedItemId,
            snapshot.Category,
            allowDelay: true,
            allowEject: true
        );
        var capability = MachineInteractionCapabilityGate.Evaluate(
            catalog,
            new MachineRuntimeEvidence(true, true)
        );
        var registry = new MachineSnapshotRegistry();
        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            registry.Upsert(snapshot).Status
        );
        var leaseAuthority = LeaseAuthority(lifetimeTicks);
        return new HarassmentFixture(
            snapshot,
            registry,
            leaseAuthority,
            new DarkHandHarassmentOperationService(
                catalog,
                capability,
                leaseAuthority,
                registry
            )
        );
    }

    private static MachineTargetCatalog EnabledCatalog(
        string qualifiedItemId,
        MachineInteractionCategory category,
        bool allowDelay,
        bool allowEject
    )
    {
        var json = JsonSerializer.Serialize(
            new
            {
                SchemaVersion = MachineTargetCatalog.CurrentSchemaVersion,
                ContractId = MachineTargetCatalog.ContractId,
                Targets = new[]
                {
                    new
                    {
                        Id = "fixture.target",
                        QualifiedItemId = qualifiedItemId,
                        ExpectedCategory = category.ToString(),
                        Enabled = true,
                        Excluded = false,
                        AllowDelay = allowDelay,
                        AllowEject = allowEject,
                        AllowThief = false,
                        LocationAllowlist = new[] { "Farm" },
                        Evidence = new[] { "fixture-only" },
                        Reason = "fixture-only",
                    },
                },
            }
        );
        var load = MachineTargetCatalog.Load(json);
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static DarkHandInteractionLeaseAuthority LeaseAuthority(long lifetimeTicks = 120)
    {
        var authority = new DarkHandInteractionLeaseAuthority(lifetimeTicks);
        Assert.True(authority.BeginSession(SessionA, out var reason), reason);
        return authority;
    }

    private static DarkHandInteractionLeaseRequest Request(
        string owner,
        long nonce,
        long revision,
        string session = SessionA
    ) =>
        new()
        {
            SessionId = session,
            Nonce = nonce,
            OwnerPlayerKey = owner,
            LocationId = "Farm",
            TargetId = TargetId,
            OperationId = DarkHandHarassmentOperationIds.Harassment,
            ObservedTargetRevision = revision,
        };

    private static DarkHandHarassmentOperationScope Scope(string owner) =>
        new(
            IsHostAuthority: true,
            SenderPlayerKey: owner,
            OwnerPlayerKey: owner,
            ModeId: DarkHandHarassmentModeIds.Harassment,
            OwnerLocationId: "Farm",
            OwnerDistanceTiles: 5d
        );

    private static MachineInteractionSnapshot OrdinarySnapshot(
        long revision = 1,
        int minutes = 100,
        bool ready = false,
        bool showNextIndex = false
    ) =>
        Snapshot(
            MachineInteractionCategory.OrdinarySingleInputFinite,
            revision,
            minutes,
            ready,
            showNextIndex
        );

    private static MachineInteractionSnapshot Snapshot(
        MachineInteractionCategory category,
        long revision = 1,
        int minutes = 100,
        bool ready = false,
        bool showNextIndex = false,
        MachineScheduleKind schedule = MachineScheduleKind.FiniteMinutes,
        string qualifiedItemId = "(BC)17"
    )
    {
        var catalog = EnabledCatalog(
            qualifiedItemId,
            category,
            allowDelay: true,
            allowEject: true
        );
        var triggers = category switch
        {
            MachineInteractionCategory.PermanentInput =>
                MachineTriggerKinds.ItemPlacedInMachine | MachineTriggerKinds.OutputCollected,
            MachineInteractionCategory.NoInputInfiniteOutput =>
                MachineTriggerKinds.MachinePutDown,
            _ => MachineTriggerKinds.ItemPlacedInMachine,
        };
        var rule = new MachineRuleFacts(
            "Default",
            triggers,
            category == MachineInteractionCategory.MultiInput ? 2 : 1,
            schedule == MachineScheduleKind.DayBased ? -1 : 240,
            schedule == MachineScheduleKind.DayBased ? 2 : -1,
            RecalculateOnCollect: false,
            HasCustomOutputMethod: false
        );
        var observation = new MachineReadObservation(
            TargetId,
            "Farm",
            qualifiedItemId,
            revision,
            "Default",
            minutes,
            ready,
            showNextIndex,
            new MachineItemFacts("(O)428", 1, 2, false),
            new MachineItemFacts("(O)440", 1, 0, false),
            new MachineDataFacts(
                IsIncubator: false,
                OnlyCompleteOvernight: false,
                HasAdditionalConsumedItems:
                    category == MachineInteractionCategory.MultiInput,
                HasCustomInteractMethod: false,
                HasClearContentsOvernightCondition: false,
                ActiveRule: rule
            )
        );
        var captured = MachineInteractionClassifier.Capture(
            observation,
            catalog,
            new MachineRuntimeEvidence(true, true)
        );
        return captured with
        {
            Schedule = schedule,
            Category = category,
            CanDelay = schedule == MachineScheduleKind.FiniteMinutes
                && category != MachineInteractionCategory.Unsupported
                && !ready,
            CanEject = category == MachineInteractionCategory.OrdinarySingleInputFinite,
            CanLandHeldOutput = true,
        };
    }

    private sealed class HarassmentFixture
    {
        internal HarassmentFixture(
            MachineInteractionSnapshot snapshot,
            MachineSnapshotRegistry registry,
            DarkHandInteractionLeaseAuthority leaseAuthority,
            DarkHandHarassmentOperationService service
        )
        {
            Snapshot = snapshot;
            Registry = registry;
            LeaseAuthority = leaseAuthority;
            Service = service;
        }

        internal MachineInteractionSnapshot Snapshot { get; }
        internal MachineSnapshotRegistry Registry { get; }
        internal DarkHandInteractionLeaseAuthority LeaseAuthority { get; }
        internal DarkHandHarassmentOperationService Service { get; }

        internal DarkHandInteractionLease Issue(string owner, long nonce, long nowTick)
        {
            var issued = Service.TryIssue(
                Request(owner, nonce, Snapshot.AuthorityRevision),
                Scope(owner),
                nowTick
            );
            Assert.True(issued.Issued, issued.Reason);
            return issued.Lease!;
        }
    }

    private sealed class SequenceRandom : IDarkHandHarassmentRandomSource
    {
        private readonly Queue<double> values;

        internal SequenceRandom(params double[] values)
        {
            this.values = new Queue<double>(values);
        }

        internal int CallCount { get; private set; }

        public double NextUnitInterval()
        {
            CallCount++;
            Assert.NotEmpty(values);
            return values.Dequeue();
        }
    }

    private sealed class RecordingAdapter : IDarkHandHarassmentMachineAdapter
    {
        private readonly MachineInteractionSnapshot before;

        internal RecordingAdapter(MachineInteractionSnapshot before)
        {
            this.before = before;
        }

        internal string? LandingFailureReason { get; set; }
        internal DarkHandHarassmentAdapterStatus DelayStatus { get; set; } =
            DarkHandHarassmentAdapterStatus.Applied;
        internal DarkHandHarassmentAdapterStatus EjectStatus { get; set; } =
            DarkHandHarassmentAdapterStatus.Applied;
        internal bool DriftRollbackMinutes { get; set; }
        internal int ReserveCount { get; private set; }
        internal int DelayCommitCount { get; private set; }
        internal int EjectCommitCount { get; private set; }
        internal int TotalCommitCount => DelayCommitCount + EjectCommitCount;
        internal List<string> CallOrder { get; } = new();

        public DarkHandHarassmentLandingResult ReserveLanding(
            DarkHandHarassmentLandingRequest request
        )
        {
            ReserveCount++;
            CallOrder.Add("reserve-landing");
            if (LandingFailureReason is not null)
                return new DarkHandHarassmentLandingResult(false, LandingFailureReason, null);
            return new DarkHandHarassmentLandingResult(
                true,
                "fixture.landing-reserved",
                new DarkHandHarassmentLandingReservation(
                    "fixture-reservation-1",
                    request.TargetId,
                    request.LocationId,
                    request.TargetRevision,
                    request.StateFingerprint,
                    request.Item
                )
            );
        }

        public DarkHandHarassmentAdapterResult CommitDelay(
            DarkHandHarassmentDelayCommitPlan plan
        )
        {
            DelayCommitCount++;
            CallOrder.Add("commit-delay");
            if (DelayStatus == DarkHandHarassmentAdapterStatus.Applied)
            {
                return Applied(
                    before with
                    {
                        AuthorityRevision = before.AuthorityRevision + 1,
                        StateFingerprint = Fingerprint(before.AuthorityRevision + 1, 'D'),
                        MinutesUntilReady = plan.MinutesUntilReadyAfter,
                        State = MachineLifecycleState.Running,
                    },
                    itemLanded: false
                );
            }
            return Failed(DelayStatus);
        }

        public DarkHandHarassmentAdapterResult CommitEject(
            DarkHandHarassmentEjectCommitPlan plan
        )
        {
            EjectCommitCount++;
            CallOrder.Add("commit-eject");
            if (EjectStatus == DarkHandHarassmentAdapterStatus.Applied)
            {
                return Applied(
                    before with
                    {
                        AuthorityRevision = before.AuthorityRevision + 1,
                        StateFingerprint = Fingerprint(before.AuthorityRevision + 1, 'E'),
                        State = MachineLifecycleState.Empty,
                        ActiveRuleId = string.Empty,
                        MinutesUntilReady = 0,
                        ReadyForHarvest = false,
                        ShowNextIndex = false,
                        HeldOutput = null,
                        CanDelay = false,
                        CanEject = false,
                        CanLandHeldOutput = false,
                    },
                    itemLanded: true
                );
            }
            return Failed(EjectStatus);
        }

        private DarkHandHarassmentAdapterResult Failed(
            DarkHandHarassmentAdapterStatus status
        )
        {
            var after = DriftRollbackMinutes
                ? before with { MinutesUntilReady = before.MinutesUntilReady + 1 }
                : before;
            return new DarkHandHarassmentAdapterResult(
                status,
                "fixture-adapter-failure",
                after,
                WorldMutationApplied: false,
                RollbackVerified: status == DarkHandHarassmentAdapterStatus.RolledBack,
                ItemLandedExactlyOnce: false
            );
        }

        private static DarkHandHarassmentAdapterResult Applied(
            MachineInteractionSnapshot after,
            bool itemLanded
        ) =>
            new(
                DarkHandHarassmentAdapterStatus.Applied,
                "fixture-adapter-applied",
                after,
                WorldMutationApplied: true,
                RollbackVerified: false,
                ItemLandedExactlyOnce: itemLanded
            );

        private static string Fingerprint(long revision, char marker)
        {
            var prefix = string.Concat(marker, revision.ToString("X16"));
            return prefix.PadRight(64, marker)[..64];
        }
    }
}
