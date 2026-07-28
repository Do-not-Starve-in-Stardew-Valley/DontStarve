using System.Text.Json;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.DarkHand;

public sealed class DarkHandThiefTransactionTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Owner = "123456789";
    private const string OtherOwner = "223456789";
    private const string TargetId = "machine-1";

    private static string ShippedCatalogPath => Path.Combine(
        AppContext.BaseDirectory,
        "ShippedMod",
        "Asset",
        "Sanity",
        "Data",
        "dark-hand-machines.json"
    );

    private static string RuntimeServiceSourcePath => Path.Combine(
        AppContext.BaseDirectory,
        "Contracts",
        "MachineInteraction",
        "SmapiDarkHandThiefService.cs"
    );

    private static string ModEntrySourcePath => Path.Combine(
        AppContext.BaseDirectory,
        "Contracts",
        "ShadowProjection",
        "ModEntry.cs"
    );

    private static string DefaultI18nPath => Path.Combine(AppContext.BaseDirectory, "I18n", "default.json");
    private static string ChineseI18nPath => Path.Combine(AppContext.BaseDirectory, "I18n", "zh.json");

    [Fact]
    public void ShippedCatalogAuthorizesOnlyLoomBehindExplicitThiefMode()
    {
        var catalog = LoadShipped();
        var capability = DarkHandThiefCapabilityGate.Evaluate(
            catalog,
            MachineRuntimeEvidence.VerifiedStardew1615
        );

        Assert.Equal(2, catalog.SchemaVersion);
        Assert.Equal(8, catalog.Targets.Count);
        Assert.Equal(1, catalog.EnabledTargetCount);
        var loom = Assert.Single(catalog.Targets.Where(target => target.AllowThief));
        Assert.Equal("(BC)17", loom.QualifiedItemId);
        Assert.Equal(new[] { "Farm" }, loom.LocationAllowlist);
        Assert.Equal(DarkHandThiefCapabilityStatus.Available, capability.Status);
        Assert.Equal(DarkHandThiefReasonIds.Eligible, capability.Reason);
        Assert.True(capability.CanCommitDelete);
    }

    [Fact]
    public void VerifiedShippedProductionRejectsMissingTargetWithoutDeleting()
    {
        var catalog = LoadShipped();
        var capability = DarkHandThiefCapabilityGate.Evaluate(
            catalog,
            MachineRuntimeEvidence.VerifiedStardew1615
        );
        var authority = LeaseAuthority();
        var service = new DarkHandThiefOperationService(catalog, capability, authority, new MachineSnapshotRegistry());
        var adapter = new RecordingAdapter(Snapshot());

        var issued = service.TryIssue(Request(Owner, 1, 1), Scope(Owner), 10);
        var committed = service.Commit(null, Scope(Owner), 10, adapter);

        Assert.False(issued.Issued);
        Assert.Equal(DarkHandThiefReasonIds.TargetMissing, issued.Reason);
        Assert.Equal("dark-hand.lease-envelope-invalid", committed.Reason);
        Assert.Equal(0, adapter.CommitCount);
        Assert.Equal(0, service.SuccessfulCommitCount);
    }

    [Fact]
    public void CapabilityRequiresSeparateOptInRevisionAndAtomicDeletionAdapter()
    {
        var noOptIn = DarkHandThiefCapabilityGate.Evaluate(
            EnabledCatalog(MachineInteractionCategory.OrdinarySingleInputFinite, allowThief: false),
            new MachineRuntimeEvidence(true, true, true)
        );
        var noRevision = DarkHandThiefCapabilityGate.Evaluate(
            EnabledCatalog(MachineInteractionCategory.OrdinarySingleInputFinite),
            new MachineRuntimeEvidence(false, true, true)
        );
        var noAdapter = DarkHandThiefCapabilityGate.Evaluate(
            EnabledCatalog(MachineInteractionCategory.OrdinarySingleInputFinite),
            new MachineRuntimeEvidence(true, true, false)
        );
        var available = DarkHandThiefCapabilityGate.Evaluate(
            EnabledCatalog(MachineInteractionCategory.OrdinarySingleInputFinite),
            new MachineRuntimeEvidence(true, false, true)
        );

        Assert.Equal(DarkHandThiefCapabilityStatus.ReadOnlyNoAuthorizedTargets, noOptIn.Status);
        Assert.Equal(DarkHandThiefCapabilityStatus.ReadOnlyAuthorityRevisionUnavailable, noRevision.Status);
        Assert.Equal(DarkHandThiefCapabilityStatus.ReadOnlyAtomicAdapterUnavailable, noAdapter.Status);
        Assert.Equal(DarkHandThiefCapabilityStatus.Available, available.Status);
        Assert.True(available.CanCommitDelete);
    }

    [Fact]
    public void StrictSchemaRejectsThiefPermissionOnDisabledAndMultiInputRows()
    {
        var shipped = File.ReadAllText(ShippedCatalogPath);
        var disabledOptIn = ReplaceFirst(shipped, "\"AllowThief\": false", "\"AllowThief\": true");
        var multiInputOptIn = CatalogJson(MachineInteractionCategory.MultiInput, allowThief: true);

        Assert.False(MachineTargetCatalog.Load(disabledOptIn).IsAvailable);
        Assert.False(MachineTargetCatalog.Load(multiInputOptIn).IsAvailable);
    }

    [Fact]
    public void GmcmTooltipExplicitlyWarnsThatDeletionIsPermanentAndUnrecoverable()
    {
        var english = ReadI18n(DefaultI18nPath)["config.dark-hand-mode.tooltip"];
        var chinese = ReadI18n(ChineseI18nPath)["config.dark-hand-mode.tooltip"];

        Assert.Contains("permanently stop", english, StringComparison.Ordinal);
        Assert.Contains("cannot be recovered", english, StringComparison.Ordinal);
        Assert.Contains("永久停止", chinese, StringComparison.Ordinal);
        Assert.Contains("无法恢复", chinese, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningOrdinaryContentsAreDeletedWhileMachineAndExternalStateStayIntact()
    {
        var fixture = Fixture(Snapshot());
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var result = fixture.Service.Commit(fixture.Issue(Owner, 1, 10), Scope(Owner), 11, adapter);

        Assert.True(result.Applied);
        Assert.Equal(DarkHandThiefDeletedContentKind.RunningContents, result.Receipt!.ContentKind);
        Assert.NotNull(result.Receipt.DeletedInput);
        Assert.NotNull(result.Receipt.DeletedOutput);
        Assert.True(result.Receipt.ContentPermanentlyDeleted);
        Assert.True(result.Receipt.MachineObjectPreserved);
        Assert.True(result.Receipt.ExternalStatePreserved);
        Assert.Equal(MachineLifecycleState.Empty, result.Receipt.After!.State);
        Assert.Null(result.Receipt.After.HeldOutput);
        Assert.Null(result.Receipt.After.LastInputItem);
        Assert.Equal(1, adapter.CommitCount);
    }

    [Fact]
    public void ReadyOutputUsesTheSameAtomicStopAndDeleteReceipt()
    {
        var fixture = Fixture(Snapshot(ready: true, minutes: 0));
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new RecordingAdapter(fixture.Snapshot)
        );

        Assert.True(result.Applied);
        Assert.Equal(DarkHandThiefDeletedContentKind.ReadyOutput, result.Receipt!.ContentKind);
        Assert.False(result.Receipt.After!.ReadyForHarvest);
        Assert.False(result.Receipt.After.ShowNextIndex);
        Assert.Equal(string.Empty, result.Receipt.After.ActiveRuleId);
    }

    [Theory]
    [InlineData("PermanentInput", false)]
    [InlineData("NoInputInfiniteOutput", true)]
    public void AllowedNonSpecialCategoriesCanDeleteOnlyProvenOrdinaryContent(
        string categoryText,
        bool ready
    )
    {
        var category = Enum.Parse<MachineInteractionCategory>(categoryText);
        var fixture = Fixture(Snapshot(category, ready: ready, minutes: ready ? 0 : 100));
        var result = fixture.Service.Commit(
            fixture.Issue(Owner, 1, 10),
            Scope(Owner),
            11,
            new RecordingAdapter(fixture.Snapshot)
        );

        Assert.True(result.Applied);
        Assert.Equal(1, fixture.Service.SuccessfulCommitCount);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("FireThief")]
    [InlineData("Harassment")]
    [InlineData("thief")]
    public void OnlyExactThiefModeCanObtainADeletionLease(string mode)
    {
        var fixture = Fixture(Snapshot());
        var issued = fixture.Service.TryIssue(Request(Owner, 1, 1), Scope(Owner) with { ModeId = mode }, 10);

        Assert.False(issued.Issued);
        Assert.Equal(DarkHandThiefReasonIds.ModeRequired, issued.Reason);
    }

    [Theory]
    [InlineData("MachineObject")]
    [InlineData("Chest")]
    [InlineData("PlayerInventoryItem")]
    [InlineData("QuestOrProtectedItem")]
    [InlineData("Map")]
    [InlineData("SaveData")]
    public void DestructiveScopeCanNeverExpandBeyondMachineContents(string scopeText)
    {
        var fixture = Fixture(Snapshot());
        var scope = Enum.Parse<DarkHandThiefDeletionScope>(scopeText);
        var issued = fixture.Service.TryIssue(
            Request(Owner, 1, 1),
            Scope(Owner) with { DeletionScope = scope },
            10
        );

        Assert.False(issued.Issued);
        Assert.Equal(DarkHandThiefReasonIds.DeletionScopeForbidden, issued.Reason);
    }

    [Theory]
    [InlineData("Unknown", false)]
    [InlineData("ProtectedOrQuest", false)]
    [InlineData("Ordinary", true)]
    public void ContentProtectionMustBeExplicitAndRecipesRemainProtected(
        string protectionText,
        bool ordinary
    )
    {
        var protection = Enum.Parse<MachineContentProtection>(protectionText);
        var snapshot = Snapshot(protection: protection, itemIsRecipe: !ordinary);
        var fixture = Fixture(snapshot);
        var issued = fixture.Service.TryIssue(Request(Owner, 1, 1), Scope(Owner), 10);

        if (ordinary)
            Assert.True(issued.Issued, issued.Reason);
        else
        {
            Assert.False(issued.Issued);
            Assert.Equal(DarkHandThiefReasonIds.ContentProtectionUnproven, issued.Reason);
        }
    }

    [Fact]
    public void NoInputRunningMachineWithoutCurrentItemHasNothingToDelete()
    {
        var snapshot = Snapshot(
            MachineInteractionCategory.NoInputInfiniteOutput,
            heldOutput: null,
            includeInput: false
        );
        var fixture = Fixture(snapshot);
        var issued = fixture.Service.TryIssue(Request(Owner, 1, 1), Scope(Owner), 10);

        Assert.False(issued.Issued);
        Assert.Equal(DarkHandThiefReasonIds.ContentProtectionUnproven, issued.Reason);
    }

    [Fact]
    public void MultiInputAndAdditionalConsumedItemTopologyCannotBeOptedIntoThief()
    {
        var load = MachineTargetCatalog.Load(CatalogJson(MachineInteractionCategory.MultiInput, allowThief: true));
        Assert.False(load.IsAvailable);

        var shippedFurnace = LoadShipped().Find("(BC)13");
        Assert.NotNull(shippedFurnace);
        Assert.Equal(MachineInteractionCategory.MultiInput, shippedFurnace!.ExpectedCategory);
        Assert.False(shippedFurnace.AllowThief);
    }

    [Fact]
    public void IronAnvilIncubatorUnknownAndModdedTargetsStayExcluded()
    {
        var shipped = LoadShipped();
        Assert.True(shipped.Find("(BC)Anvil")!.Excluded);
        Assert.True(shipped.Find("(BC)101")!.Excluded);
        Assert.Null(shipped.Find("(BC)SomeModdedMachine"));
        Assert.Null(shipped.Find("Chest"));
    }

    [Fact]
    public void RevisionDriftRejectsBeforeAdapterCall()
    {
        var fixture = Fixture(Snapshot(revision: 7));
        var lease = fixture.Issue(Owner, 1, 10);
        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            fixture.Registry.Upsert(Snapshot(revision: 8, minutes: 90)).Status
        );
        var adapter = new RecordingAdapter(fixture.Snapshot);

        var result = fixture.Service.Commit(lease, Scope(Owner), 11, adapter);

        Assert.Equal("dark-hand.lease-target-revision-changed", result.Reason);
        Assert.Equal(0, adapter.CommitCount);
    }

    [Fact]
    public void SameRevisionStateDriftQuarantinesTargetUntilHigherRevision()
    {
        var fixture = Fixture(Snapshot(revision: 7));
        var lease = fixture.Issue(Owner, 1, 10);
        var conflict = fixture.Snapshot with
        {
            StateFingerprint = new string('F', 64),
            MinutesUntilReady = 99,
        };

        Assert.Equal(MachineInteractionReasonIds.RegistrySnapshotConflict, fixture.Registry.Upsert(conflict).Reason);
        Assert.False(fixture.Registry.TryGet(TargetId, out _));
        Assert.Equal(
            "dark-hand.lease-target-revision-changed",
            fixture.Service.Commit(lease, Scope(Owner), 11, new RecordingAdapter(fixture.Snapshot)).Reason
        );
        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            fixture.Registry.Upsert(Snapshot(revision: 8)).Status
        );
    }

    [Fact]
    public void ExactDuplicateLeaseReplaysTerminalReceiptWithoutSecondDeletion()
    {
        var fixture = Fixture(Snapshot());
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var lease = fixture.Issue(Owner, 1, 10);
        var first = fixture.Service.Commit(lease, Scope(Owner), 11, adapter);
        var duplicate = fixture.Service.Commit(lease.Clone(), Scope(Owner), 12, adapter);

        Assert.True(first.Applied);
        Assert.Equal(DarkHandThiefTransactionDisposition.Duplicate, duplicate.Disposition);
        Assert.Same(first.Receipt, duplicate.Receipt);
        Assert.Equal(1, adapter.CommitCount);
        Assert.Equal(1, fixture.Service.SuccessfulCommitCount);
    }

    [Fact]
    public void ExpiredLeaseCannotDeleteAnything()
    {
        var fixture = Fixture(Snapshot(), lifetimeTicks: 5);
        var adapter = new RecordingAdapter(fixture.Snapshot);
        var result = fixture.Service.Commit(fixture.Issue(Owner, 1, 10), Scope(Owner), 15, adapter);

        Assert.Equal("dark-hand.lease-expired", result.Reason);
        Assert.Equal(0, adapter.CommitCount);
    }

    [Fact]
    public void CompetingOwnersAtOneRevisionAllowOnlyTheFirstDeletion()
    {
        var fixture = Fixture(Snapshot(revision: 7));
        var firstLease = fixture.Issue(Owner, 1, 10);
        var secondLease = fixture.Issue(OtherOwner, 1, 10);
        var first = fixture.Service.Commit(firstLease, Scope(Owner), 11, new RecordingAdapter(fixture.Snapshot));
        var secondAdapter = new RecordingAdapter(fixture.Snapshot);
        var second = fixture.Service.Commit(secondLease, Scope(OtherOwner), 11, secondAdapter);

        Assert.True(first.Applied);
        Assert.Equal("dark-hand.lease-target-revision-changed", second.Reason);
        Assert.Equal(0, secondAdapter.CommitCount);
    }

    [Fact]
    public void SaveLoadSessionClearInvalidatesLeaseSnapshotAndReceiptWindow()
    {
        var fixture = Fixture(Snapshot());
        var lease = fixture.Issue(Owner, 1, 10);
        fixture.Service.ClearWindow();
        fixture.Registry.Clear();
        Assert.True(fixture.LeaseAuthority.BeginSession(SessionB, out var reason), reason);

        var result = fixture.Service.Commit(lease, Scope(Owner), 11, new RecordingAdapter(fixture.Snapshot));

        Assert.Equal("dark-hand.lease-session-or-owner-invalid", result.Reason);
        Assert.Equal(0, fixture.Service.ReceiptCount);
        Assert.Equal(0, fixture.Registry.Count);
    }

    [Fact]
    public void HarassmentOperationLeaseCannotBeReusedForThief()
    {
        var fixture = Fixture(Snapshot());
        var request = Request(Owner, 1, 1);
        request.OperationId = DarkHandHarassmentOperationIds.Harassment;

        var issued = fixture.Service.TryIssue(request, Scope(Owner), 10);

        Assert.False(issued.Issued);
        Assert.Equal("dark-hand.lease-target-revision-or-scope-changed", issued.Reason);
    }

    [Fact]
    public void AdapterRollbackMustRestoreEveryFrozenField()
    {
        var fixture = Fixture(Snapshot(showNextIndex: true));
        var adapter = new RecordingAdapter(fixture.Snapshot)
        {
            Status = DarkHandThiefAdapterStatus.RolledBack,
        };
        var result = fixture.Service.Commit(fixture.Issue(Owner, 1, 10), Scope(Owner), 11, adapter);

        Assert.Equal(DarkHandThiefTransactionDisposition.RolledBack, result.Disposition);
        Assert.Equal(DarkHandThiefReasonIds.AdapterRolledBack, result.Reason);
        Assert.True(result.Receipt!.RollbackVerified);
        Assert.Equal(result.Receipt.Before, result.Receipt.After);
        Assert.False(result.Receipt.ContentPermanentlyDeleted);
    }

    [Fact]
    public void FalseRollbackWithFieldDriftIsAContractViolation()
    {
        var fixture = Fixture(Snapshot());
        var adapter = new RecordingAdapter(fixture.Snapshot)
        {
            Status = DarkHandThiefAdapterStatus.RolledBack,
            DriftRollback = true,
        };
        var result = fixture.Service.Commit(fixture.Issue(Owner, 1, 10), Scope(Owner), 11, adapter);

        Assert.Equal(DarkHandThiefReasonIds.AdapterContractInvalid, result.Reason);
        Assert.False(result.Receipt!.RollbackVerified);
        Assert.NotEqual(result.Receipt.Before, result.Receipt.After);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("external")]
    [InlineData("content")]
    [InlineData("after")]
    public void AppliedAdapterMustProveExactDeletionAndPreserveEverythingElse(string brokenProof)
    {
        var fixture = Fixture(Snapshot());
        var adapter = new RecordingAdapter(fixture.Snapshot)
        {
            PreserveMachine = brokenProof != "machine",
            PreserveExternal = brokenProof != "external",
            DeleteContent = brokenProof != "content",
            LeaveAfterContent = brokenProof == "after",
        };
        var result = fixture.Service.Commit(fixture.Issue(Owner, 1, 10), Scope(Owner), 11, adapter);

        Assert.Equal(DarkHandThiefReasonIds.AdapterContractInvalid, result.Reason);
        Assert.Equal(DarkHandThiefReceiptOutcome.Rejected, result.Receipt!.Outcome);
        Assert.Equal(0, fixture.Service.SuccessfulCommitCount);
    }

    [Theory]
    [InlineData(false, Owner, Owner, "Farm", 5d, "dark-hand.thief.host-authority-required")]
    [InlineData(true, OtherOwner, Owner, "Farm", 5d, "dark-hand.thief.sender-owner-mismatch")]
    [InlineData(true, Owner, Owner, "Mine", 5d, "dark-hand.thief.owner-location-mismatch")]
    [InlineData(true, Owner, Owner, "Farm", 20.001d, "dark-hand.thief.owner-out-of-range")]
    public void ClientOwnerLocationAndRangeForgeryCannotObtainLease(
        bool host,
        string sender,
        string owner,
        string location,
        double distance,
        string expectedReason
    )
    {
        var fixture = Fixture(Snapshot());
        var scope = Scope(owner) with
        {
            IsHostAuthority = host,
            SenderPlayerKey = sender,
            OwnerLocationId = location,
            OwnerDistanceTiles = distance,
        };

        var issued = fixture.Service.TryIssue(Request(sender, 1, 1), scope, 10);

        Assert.False(issued.Issued);
        Assert.Equal(expectedReason, issued.Reason);
    }

    [Fact]
    public void ProductionCapabilityOwnerReusesCatalogAndReportsCoordinatorWiring()
    {
        var service = File.ReadAllText(RuntimeServiceSourcePath);
        var modEntry = File.ReadAllText(ModEntrySourcePath);

        Assert.Contains("RuntimeTargetAuthorityInstalled: runtimeWired", service, StringComparison.Ordinal);
        Assert.Contains("WorldMutationHookInstalled: runtimeWired", service, StringComparison.Ordinal);
        Assert.Contains("SmapiDarkHandThiefService", modEntry, StringComparison.Ordinal);
        Assert.Contains("_darkHandHarassment.Catalog", modEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", service, StringComparison.Ordinal);
        Assert.DoesNotContain("location.Objects", service, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", service, StringComparison.Ordinal);
        Assert.DoesNotContain("heldObject", service, StringComparison.Ordinal);
        Assert.DoesNotContain("MinutesUntilReady =", service, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ReadI18n(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static MachineTargetCatalog LoadShipped()
    {
        var result = MachineTargetCatalog.Load(File.ReadAllText(ShippedCatalogPath));
        Assert.True(result.IsAvailable, result.Reason);
        return result.Catalog;
    }

    private static MachineTargetCatalog EnabledCatalog(
        MachineInteractionCategory category,
        bool allowThief = true,
        string qualifiedItemId = "(BC)17"
    )
    {
        var result = MachineTargetCatalog.Load(CatalogJson(category, allowThief, qualifiedItemId));
        Assert.True(result.IsAvailable, result.Reason);
        return result.Catalog;
    }

    private static string CatalogJson(
        MachineInteractionCategory category,
        bool allowThief,
        string qualifiedItemId = "(BC)17"
    ) => JsonSerializer.Serialize(
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
                    AllowDelay = false,
                    AllowEject = false,
                    AllowThief = allowThief,
                    LocationAllowlist = new[] { "Farm" },
                    Evidence = new[] { "fixture-only" },
                    Reason = "fixture-only",
                },
            },
        }
    );

    private static ThiefFixture Fixture(
        MachineInteractionSnapshot snapshot,
        long lifetimeTicks = 120
    )
    {
        var catalog = EnabledCatalog(snapshot.Category, qualifiedItemId: snapshot.QualifiedItemId);
        var capability = DarkHandThiefCapabilityGate.Evaluate(
            catalog,
            new MachineRuntimeEvidence(true, false, true)
        );
        var registry = new MachineSnapshotRegistry();
        Assert.Equal(MachineSnapshotRegistryUpdateStatus.Accepted, registry.Upsert(snapshot).Status);
        var authority = LeaseAuthority(lifetimeTicks);
        return new ThiefFixture(
            snapshot,
            registry,
            authority,
            new DarkHandThiefOperationService(catalog, capability, authority, registry)
        );
    }

    private static MachineInteractionSnapshot Snapshot(
        MachineInteractionCategory category = MachineInteractionCategory.OrdinarySingleInputFinite,
        long revision = 1,
        int minutes = 100,
        bool ready = false,
        bool showNextIndex = false,
        MachineContentProtection protection = MachineContentProtection.Ordinary,
        bool itemIsRecipe = false,
        MachineItemFacts? heldOutput = null,
        bool includeInput = true
    )
    {
        var catalog = EnabledCatalog(category);
        var triggers = category switch
        {
            MachineInteractionCategory.PermanentInput =>
                MachineTriggerKinds.ItemPlacedInMachine | MachineTriggerKinds.OutputCollected,
            MachineInteractionCategory.NoInputInfiniteOutput => MachineTriggerKinds.MachinePutDown,
            _ => MachineTriggerKinds.ItemPlacedInMachine,
        };
        var input = includeInput
            ? new MachineItemFacts("(O)440", 1, 0, itemIsRecipe, protection)
            : null;
        var output = heldOutput ?? new MachineItemFacts("(O)428", 1, 2, itemIsRecipe, protection);
        if (heldOutput is null && category == MachineInteractionCategory.NoInputInfiniteOutput && !includeInput && !ready)
            output = null;
        var observation = new MachineReadObservation(
            TargetId,
            "Farm",
            "(BC)17",
            revision,
            "Default",
            minutes,
            ready,
            showNextIndex,
            output,
            input,
            new MachineDataFacts(
                IsIncubator: false,
                OnlyCompleteOvernight: false,
                HasAdditionalConsumedItems: false,
                HasCustomInteractMethod: false,
                HasClearContentsOvernightCondition: false,
                ActiveRule: new MachineRuleFacts(
                    "Default",
                    triggers,
                    1,
                    240,
                    -1,
                    RecalculateOnCollect: false,
                    HasCustomOutputMethod: false
                )
            )
        );
        return MachineInteractionClassifier.Capture(
            observation,
            catalog,
            new MachineRuntimeEvidence(true, false, true)
        ) with
        {
            Category = category,
        };
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
    ) => new()
    {
        SessionId = session,
        Nonce = nonce,
        OwnerPlayerKey = owner,
        LocationId = "Farm",
        TargetId = TargetId,
        OperationId = DarkHandThiefOperationIds.Thief,
        ObservedTargetRevision = revision,
    };

    private static DarkHandThiefOperationScope Scope(string owner) => new(
        IsHostAuthority: true,
        SenderPlayerKey: owner,
        OwnerPlayerKey: owner,
        ModeId: DarkHandThiefModeIds.Thief,
        OwnerLocationId: "Farm",
        OwnerDistanceTiles: 5d,
        DeletionScope: DarkHandThiefDeletionScope.MachineContents
    );

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
    }

    private sealed class ThiefFixture
    {
        internal ThiefFixture(
            MachineInteractionSnapshot snapshot,
            MachineSnapshotRegistry registry,
            DarkHandInteractionLeaseAuthority leaseAuthority,
            DarkHandThiefOperationService service
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
        internal DarkHandThiefOperationService Service { get; }

        internal DarkHandInteractionLease Issue(string owner, long nonce, long nowTick)
        {
            var issued = Service.TryIssue(Request(owner, nonce, Snapshot.AuthorityRevision), Scope(owner), nowTick);
            Assert.True(issued.Issued, issued.Reason);
            return issued.Lease!;
        }
    }

    private sealed class RecordingAdapter : IDarkHandThiefMachineAdapter
    {
        private readonly MachineInteractionSnapshot before;

        internal RecordingAdapter(MachineInteractionSnapshot before)
        {
            this.before = before;
        }

        internal DarkHandThiefAdapterStatus Status { get; set; } = DarkHandThiefAdapterStatus.Applied;
        internal bool DriftRollback { get; set; }
        internal bool PreserveMachine { get; set; } = true;
        internal bool PreserveExternal { get; set; } = true;
        internal bool DeleteContent { get; set; } = true;
        internal bool LeaveAfterContent { get; set; }
        internal int CommitCount { get; private set; }

        public DarkHandThiefAdapterResult CommitDelete(DarkHandThiefCommitPlan plan)
        {
            CommitCount++;
            if (Status != DarkHandThiefAdapterStatus.Applied)
            {
                var after = DriftRollback
                    ? before with { MinutesUntilReady = before.MinutesUntilReady + 1 }
                    : before;
                return new DarkHandThiefAdapterResult(
                    Status,
                    "fixture.failure",
                    after,
                    WorldMutationApplied: false,
                    RollbackVerified: Status == DarkHandThiefAdapterStatus.RolledBack,
                    ContentPermanentlyDeleted: false,
                    MachineObjectPreserved: true,
                    ExternalStatePreserved: true
                );
            }

            var afterApplied = before with
            {
                AuthorityRevision = before.AuthorityRevision + 1,
                StateFingerprint = Fingerprint(before.AuthorityRevision + 1),
                State = MachineLifecycleState.Empty,
                Category = MachineInteractionCategory.Unsupported,
                Schedule = MachineScheduleKind.None,
                ActiveRuleId = string.Empty,
                MinutesUntilReady = 0,
                ReadyForHarvest = false,
                ShowNextIndex = false,
                HeldOutput = LeaveAfterContent ? before.HeldOutput : null,
                LastInputItem = null,
                CanDelay = false,
                CanEject = false,
                CanLandHeldOutput = false,
                CanDeleteContent = false,
            };
            return new DarkHandThiefAdapterResult(
                DarkHandThiefAdapterStatus.Applied,
                "fixture.applied",
                afterApplied,
                WorldMutationApplied: true,
                RollbackVerified: false,
                ContentPermanentlyDeleted: DeleteContent,
                MachineObjectPreserved: PreserveMachine,
                ExternalStatePreserved: PreserveExternal
            );
        }

        private static string Fingerprint(long revision)
        {
            var prefix = string.Concat("T", revision.ToString("X16"));
            return prefix.PadRight(64, 'T')[..64];
        }
    }
}
