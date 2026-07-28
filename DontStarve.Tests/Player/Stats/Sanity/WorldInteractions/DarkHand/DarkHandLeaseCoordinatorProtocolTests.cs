using System.Reflection;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.DarkHand;

public sealed class DarkHandLeaseCoordinatorProtocolTests
{
    private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Owner = "123456789";
    private const string OtherOwner = "223456789";
    private const string TargetA = "target-a";
    private const string TargetB = "target-b";
    private const string OperationA = "dark-hand.operation-a";
    private const string OperationB = "dark-hand.operation-b";
    private const string ModeA = "ModeA";
    private const string ModeB = "ModeB";

    [Fact]
    public void Animation_commit_message_contains_only_lease_id_and_nonce()
    {
        var properties = typeof(DarkHandInteractionLeaseCommitRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "LeaseId", "Nonce" }, properties);
        Assert.DoesNotContain("AnimationFrame", properties);
        Assert.DoesNotContain("ScreenCoordinate", properties);
        Assert.DoesNotContain("ConfigFingerprint", properties);
    }

    [Theory]
    [InlineData(false, Owner, Owner, "Farm", 5d, true, 7, ModeA, DarkHandLeaseCoordinatorReasonIds.HostAuthorityRequired)]
    [InlineData(true, OtherOwner, Owner, "Farm", 5d, true, 7, ModeA, DarkHandLeaseCoordinatorReasonIds.SenderOwnerMismatch)]
    [InlineData(true, Owner, Owner, "Mine", 5d, true, 7, ModeA, DarkHandLeaseCoordinatorReasonIds.OwnerLocationMismatch)]
    [InlineData(true, Owner, Owner, "Farm", 20.001d, true, 7, ModeA, DarkHandLeaseCoordinatorReasonIds.OwnerOutOfRange)]
    [InlineData(true, Owner, Owner, "Farm", 5d, false, 7, ModeA, DarkHandLeaseCoordinatorReasonIds.SanityIneligible)]
    [InlineData(true, Owner, Owner, "Farm", 5d, true, 0, ModeA, DarkHandLeaseCoordinatorReasonIds.SanityAuthorityInvalid)]
    [InlineData(true, Owner, Owner, "Farm", 5d, true, 7, ModeB, DarkHandLeaseCoordinatorReasonIds.OperationModeMismatch)]
    public void Candidate_revalidates_sender_location_range_sanity_and_mode(
        bool isHost,
        string sender,
        string owner,
        string location,
        double range,
        bool sanityEligible,
        long sanityRevision,
        string mode,
        string expectedReason
    )
    {
        var fixture = Fixture();
        var context = Context(owner, mode) with
        {
            IsHostAuthority = isHost,
            SenderPlayerKey = sender,
            OwnerLocationId = location,
            OwnerDistanceTiles = range,
            HostSanityEligible = sanityEligible,
            HostSanityRevision = sanityRevision,
        };

        var result = fixture.Coordinator.TryIssue(Request(owner, 1), context, 10);

        Assert.False(result.Issued);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(0, fixture.BindingA.IssueCount);
        Assert.Equal(0, fixture.BindingA.MutationCount);
    }

    [Fact]
    public void Unknown_operation_and_wrong_mode_never_reach_an_operation_binding()
    {
        var fixture = Fixture();
        var unknown = fixture.Coordinator.TryIssue(
            Request(Owner, 1, operationId: "dark-hand.unknown"),
            Context(Owner, ModeA),
            10
        );
        var mismatch = fixture.Coordinator.TryIssue(
            Request(Owner, 2, operationId: OperationB),
            Context(Owner, ModeA),
            11
        );

        Assert.Equal(DarkHandLeaseCoordinatorReasonIds.OperationBindingUnavailable, unknown.Reason);
        Assert.Equal(DarkHandLeaseCoordinatorReasonIds.OperationModeMismatch, mismatch.Reason);
        Assert.Equal(0, fixture.BindingA.IssueCount);
        Assert.Equal(0, fixture.BindingB.IssueCount);
    }

    [Fact]
    public void Nonce_replay_out_of_order_revision_drift_and_expiry_fail_closed()
    {
        var fixture = Fixture(lifetimeTicks: 5);
        var issued = fixture.Coordinator.TryIssue(Request(Owner, 3), Context(Owner, ModeA), 10);
        var replay = fixture.Coordinator.TryIssue(Request(Owner, 3), Context(Owner, ModeA), 11);
        var outOfOrder = fixture.Coordinator.TryIssue(Request(Owner, 2), Context(Owner, ModeA), 12);
        var expired = fixture.Coordinator.Commit(
            Commit(issued.Lease!),
            Context(Owner, ModeA),
            15
        );

        Assert.True(issued.Issued, issued.Reason);
        Assert.Equal("dark-hand.lease-nonce-replay", replay.Reason);
        Assert.Equal("dark-hand.lease-nonce-out-of-order", outOfOrder.Reason);
        Assert.Equal("dark-hand.lease-expired", expired.Reason);
        Assert.Equal(0, fixture.BindingA.MutationCount);

        var changed = Fixture();
        var changedLease = changed.Coordinator.TryIssue(
            Request(Owner, 1),
            Context(Owner, ModeA),
            20
        ).Lease!;
        changed.BindingA.SetRevision(TargetA, 8);
        var changedResult = changed.Coordinator.Commit(
            Commit(changedLease),
            Context(Owner, ModeA),
            21
        );
        Assert.Equal("dark-hand.lease-target-revision-changed", changedResult.Reason);
        Assert.Equal(0, changed.BindingA.MutationCount);
    }

    [Fact]
    public void Two_owners_competing_for_one_target_get_one_private_lease_but_two_targets_are_independent()
    {
        var fixture = Fixture();
        var first = fixture.Coordinator.TryIssue(Request(Owner, 1), Context(Owner, ModeA), 10);
        var competing = fixture.Coordinator.TryIssue(
            Request(OtherOwner, 1),
            Context(OtherOwner, ModeA),
            11
        );
        var independent = fixture.Coordinator.TryIssue(
            Request(OtherOwner, 2, targetId: TargetB),
            Context(OtherOwner, ModeA),
            12
        );

        Assert.True(first.Issued, first.Reason);
        Assert.Equal("dark-hand.lease-target-owned-by-another", competing.Reason);
        Assert.True(independent.Issued, independent.Reason);

        var secondTarget = fixture.Coordinator.Commit(
            Commit(independent.Lease!),
            Context(OtherOwner, ModeA),
            13
        );
        var firstTarget = fixture.Coordinator.Commit(
            Commit(first.Lease!),
            Context(Owner, ModeA),
            14
        );
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, secondTarget.Disposition);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, firstTarget.Disposition);
        Assert.Equal(2, fixture.BindingA.MutationCount);
    }

    [Fact]
    public void Config_or_sanity_change_at_commit_retires_the_lease_without_mutation()
    {
        var modeFixture = Fixture();
        var modeLease = modeFixture.Coordinator.TryIssue(
            Request(Owner, 1),
            Context(Owner, ModeA),
            10
        ).Lease!;
        var changedMode = modeFixture.Coordinator.Commit(
            Commit(modeLease),
            Context(Owner, ModeB),
            11
        );
        var modeRetry = modeFixture.Coordinator.Commit(
            Commit(modeLease),
            Context(Owner, ModeA),
            12
        );

        Assert.Equal(DarkHandLeaseCoordinatorReasonIds.OperationModeMismatch, changedMode.Reason);
        Assert.Equal("dark-hand.lease-replay", modeRetry.Reason);
        Assert.Equal(0, modeFixture.BindingA.MutationCount);

        var sanityFixture = Fixture();
        var sanityLease = sanityFixture.Coordinator.TryIssue(
            Request(Owner, 1),
            Context(Owner, ModeA),
            10
        ).Lease!;
        var sanityChanged = sanityFixture.Coordinator.Commit(
            Commit(sanityLease),
            Context(Owner, ModeA) with { HostSanityEligible = false, HostSanityRevision = 8 },
            11
        );
        Assert.Equal(DarkHandLeaseCoordinatorReasonIds.SanityIneligible, sanityChanged.Reason);
        Assert.Equal(0, sanityFixture.BindingA.MutationCount);
    }

    [Fact]
    public void Exact_commit_duplicate_replays_the_operation_receipt_without_adapter_replay()
    {
        var fixture = Fixture();
        var lease = fixture.Coordinator.TryIssue(
            Request(Owner, 1),
            Context(Owner, ModeA),
            10
        ).Lease!;

        var first = fixture.Coordinator.Commit(Commit(lease), Context(Owner, ModeA), 11);
        var duplicate = fixture.Coordinator.Commit(Commit(lease), Context(Owner, ModeB), 12);

        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, first.Disposition);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Duplicate, duplicate.Disposition);
        Assert.Same(first.OperationReceipt, duplicate.OperationReceipt);
        Assert.True(first.WorldMutationApplied);
        Assert.False(duplicate.WorldMutationApplied);
        Assert.Equal(1, fixture.BindingA.MutationCount);
    }

    [Fact]
    public void Lost_and_out_of_order_messages_never_create_optimistic_client_mutation()
    {
        var fixture = Fixture();
        var commitBeforeLease = fixture.Coordinator.Commit(
            new DarkHandInteractionLeaseCommitRequest
            {
                LeaseId = "dark-hand.lease.missing",
                Nonce = 1,
            },
            Context(Owner, ModeA),
            10
        );
        var issuedButLost = fixture.Coordinator.TryIssue(
            Request(Owner, 1),
            Context(Owner, ModeA),
            11
        );

        Assert.Equal("dark-hand.lease-commit-unknown", commitBeforeLease.Reason);
        Assert.True(issuedButLost.Issued, issuedButLost.Reason);
        Assert.Equal(0, fixture.BindingA.MutationCount);
        Assert.False(commitBeforeLease.WorldMutationApplied);
    }

    [Fact]
    public void Forged_commit_owner_or_nonce_cannot_consume_the_real_owners_lease()
    {
        var fixture = Fixture();
        var lease = fixture.Coordinator.TryIssue(
            Request(Owner, 1),
            Context(Owner, ModeA),
            10
        ).Lease!;

        var forgedOwner = fixture.Coordinator.Commit(
            Commit(lease),
            Context(OtherOwner, ModeA),
            11
        );
        var forgedNonce = fixture.Coordinator.Commit(
            new DarkHandInteractionLeaseCommitRequest
            {
                LeaseId = lease.LeaseId,
                Nonce = lease.Nonce + 1,
            },
            Context(Owner, ModeA),
            12
        );
        var accepted = fixture.Coordinator.Commit(
            Commit(lease),
            Context(Owner, ModeA),
            13
        );

        Assert.Equal("dark-hand.lease-commit-owner-nonce-or-session-invalid", forgedOwner.Reason);
        Assert.Equal("dark-hand.lease-commit-owner-nonce-or-session-invalid", forgedNonce.Reason);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, accepted.Disposition);
        Assert.Equal(1, fixture.BindingA.MutationCount);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("warp")]
    [InlineData("day-ending")]
    [InlineData("disabled")]
    [InlineData("returned-to-title")]
    [InlineData("save-load")]
    public void Lifecycle_boundaries_clear_private_lease_and_operation_windows(string boundary)
    {
        var authority = new DarkHandInteractionLeaseAuthority();
        var lifecycle = new HostileShadowSessionLifecycleCoordinator(authority);
        Assert.True(lifecycle.BeginSession(SessionA, enabled: true, out var reason), reason);
        Assert.True(
            lifecycle.TrySubscribe(1, Owner, "Farm", ShadowSnapshotTrigger.Warp, out reason),
            reason
        );
        var binding = new RecordingBinding(authority, OperationA, ModeA);
        var coordinator = new DarkHandLeaseCoordinator(authority, new[] { binding });
        var lease = coordinator.TryIssue(Request(Owner, 1), Context(Owner, ModeA), 10).Lease!;

        switch (boundary)
        {
            case "disconnect":
                lifecycle.Disconnect(1, Owner);
                break;
            case "warp":
                Assert.True(
                    lifecycle.TrySubscribe(1, Owner, "Mine", ShadowSnapshotTrigger.Warp, out reason),
                    reason
                );
                break;
            case "day-ending":
                lifecycle.DayEnding();
                break;
            case "disabled":
                lifecycle.SetEnabled(false);
                break;
            case "returned-to-title":
                lifecycle.ClearSession();
                break;
            case "save-load":
                Assert.True(lifecycle.BeginSession(SessionB, enabled: true, out reason), reason);
                break;
        }
        coordinator.ClearOperationWindows();

        var result = coordinator.Commit(Commit(lease), Context(Owner, ModeA), 11);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Rejected, result.Disposition);
        Assert.Equal(0, binding.MutationCount);
        Assert.Equal(0, binding.ReceiptCount);
    }

    [Fact]
    public void Concrete_bindings_keep_exact_operation_mode_and_receipt_ownership()
    {
        var authority = StartedAuthority();
        var fireCatalog = DarkHandFireTargetCatalog.Unavailable("fixture-unavailable");
        var fireOperation = new DarkHandFireThiefOperationService(
            fireCatalog,
            DarkHandFireCapabilityGate.Evaluate(fireCatalog, DarkHandFireRuntimeEvidence.Current),
            authority,
            new DarkHandFireTargetRegistry(fireCatalog)
        );
        var fire = new DarkHandFireThiefLeaseOperationBinding(fireOperation, new FireAdapter());

        var machineCatalog = MachineTargetCatalog.Unavailable("fixture-unavailable");
        var harassmentOperation = new DarkHandHarassmentOperationService(
            machineCatalog,
            MachineInteractionCapabilityGate.Evaluate(machineCatalog, MachineRuntimeEvidence.Current),
            authority,
            new MachineSnapshotRegistry()
        );
        var harassment = new DarkHandHarassmentLeaseOperationBinding(
            harassmentOperation,
            new RandomSource(),
            new HarassmentAdapter()
        );
        var thiefOperation = new DarkHandThiefOperationService(
            machineCatalog,
            DarkHandThiefCapabilityGate.Evaluate(machineCatalog, MachineRuntimeEvidence.Current),
            authority,
            new MachineSnapshotRegistry()
        );
        var thief = new DarkHandThiefLeaseOperationBinding(thiefOperation, new ThiefAdapter());

        Assert.Equal(DarkHandFireOperationIds.Extinguish, fire.OperationId);
        Assert.Equal(DarkHandFireModeIds.FireThief, fire.ModeId);
        Assert.Equal(DarkHandHarassmentOperationIds.Harassment, harassment.OperationId);
        Assert.Equal(DarkHandHarassmentModeIds.Harassment, harassment.ModeId);
        Assert.Equal(DarkHandThiefOperationIds.Thief, thief.OperationId);
        Assert.Equal(DarkHandThiefModeIds.Thief, thief.ModeId);
        Assert.NotEqual(fire.OperationId, harassment.OperationId);
        Assert.NotEqual(harassment.OperationId, thief.OperationId);
    }

    [Fact]
    public void Fire_binding_replays_its_schema_v1_receipt_without_a_second_adapter_call()
    {
        var authority = StartedAuthority();
        var catalogLoad = DarkHandFireTargetCatalog.Load(
            "{\"SchemaVersion\":1,\"ContractId\":\"sanity.dark-hand-fire-targets.v1\"," +
            "\"Targets\":[{\"Id\":\"fixture-fire\",\"QualifiedItemId\":\"(BC)146\"," +
            "\"RuntimeTypeFullName\":\"StardewValley.Torch\",\"TargetKind\":\"Campfire\"," +
            "\"OperationId\":\"dark-hand.fire-thief.extinguish.v1\",\"Enabled\":true," +
            "\"LocationAllowlist\":[\"Farm\"],\"Evidence\":[\"fixture\"],\"Reason\":\"fixture\"}]}"
        );
        Assert.True(catalogLoad.IsAvailable, catalogLoad.Reason);
        var registry = new DarkHandFireTargetRegistry(catalogLoad.Catalog);
        Assert.Equal(
            DarkHandFireRegistryUpdateStatus.Accepted,
            registry.Upsert(
                new DarkHandFireTargetSnapshot(
                    "fixture-fire",
                    TargetA,
                    "Farm",
                    "(BC)146",
                    DarkHandFireRuntimeTypeNames.Torch,
                    DarkHandFireTargetKind.Campfire,
                    DarkHandFireOperationIds.Extinguish,
                    7,
                    IsPresent: true,
                    IsOn: true,
                    HasExplainableLightSource: true,
                    AtomicAdapterAvailable: true
                )
            ).Status
        );
        var operation = new DarkHandFireThiefOperationService(
            catalogLoad.Catalog,
            DarkHandFireCapabilityGate.Evaluate(
                catalogLoad.Catalog,
                new DarkHandFireRuntimeEvidence(true, true, true, true, true)
            ),
            authority,
            registry
        );
        var adapter = new AppliedFireAdapter();
        var coordinator = new DarkHandLeaseCoordinator(
            authority,
            new[] { new DarkHandFireThiefLeaseOperationBinding(operation, adapter) }
        );
        var context = Context(Owner, DarkHandFireModeIds.FireThief);
        var request = Request(
            Owner,
            1,
            operationId: DarkHandFireOperationIds.Extinguish
        );

        var lease = coordinator.TryIssue(request, context, 10).Lease!;
        var applied = coordinator.Commit(Commit(lease), context, 11);
        var duplicate = coordinator.Commit(Commit(lease), context, 12);

        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, applied.Disposition);
        Assert.IsType<DarkHandFireReceipt>(applied.OperationReceipt);
        Assert.Equal(DarkHandFireReceipt.CurrentSchemaVersion, ((DarkHandFireReceipt)applied.OperationReceipt!).SchemaVersion);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Duplicate, duplicate.Disposition);
        Assert.Same(applied.OperationReceipt, duplicate.OperationReceipt);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public void Harassment_binding_replays_its_original_receipt_rng_and_adapter_once()
    {
        var authority = StartedAuthority();
        var catalog = EnabledMachineCatalog();
        var snapshot = MachineSnapshot(catalog);
        var registry = new MachineSnapshotRegistry();
        Assert.Equal(MachineSnapshotRegistryUpdateStatus.Accepted, registry.Upsert(snapshot).Status);
        var operation = new DarkHandHarassmentOperationService(
            catalog,
            MachineInteractionCapabilityGate.Evaluate(
                catalog,
                new MachineRuntimeEvidence(true, true, true)
            ),
            authority,
            registry
        );
        var random = new SequenceRandomSource(0.1d, 0.5d);
        var adapter = new AppliedHarassmentAdapter(snapshot);
        var coordinator = new DarkHandLeaseCoordinator(
            authority,
            new[] { new DarkHandHarassmentLeaseOperationBinding(operation, random, adapter) }
        );
        var context = Context(Owner, DarkHandHarassmentModeIds.Harassment);
        var request = Request(
            Owner,
            1,
            targetId: snapshot.TargetId,
            operationId: DarkHandHarassmentOperationIds.Harassment,
            revision: snapshot.AuthorityRevision
        );

        var lease = coordinator.TryIssue(request, context, 10).Lease!;
        var applied = coordinator.Commit(Commit(lease), context, 11);
        var duplicate = coordinator.Commit(Commit(lease), context, 12);

        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, applied.Disposition);
        Assert.IsType<DarkHandHarassmentReceipt>(applied.OperationReceipt);
        Assert.Equal(DarkHandHarassmentReceipt.CurrentSchemaVersion, ((DarkHandHarassmentReceipt)applied.OperationReceipt!).SchemaVersion);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Duplicate, duplicate.Disposition);
        Assert.Same(applied.OperationReceipt, duplicate.OperationReceipt);
        Assert.Equal(2, random.CallCount);
        Assert.Equal(1, adapter.CommitCount);
    }

    [Fact]
    public void Thief_binding_replays_its_original_receipt_and_keeps_machine_scope_isolated()
    {
        var authority = StartedAuthority();
        var catalog = EnabledMachineCatalog();
        var snapshot = MachineSnapshot(catalog);
        Assert.True(snapshot.CanDeleteContent);
        var registry = new MachineSnapshotRegistry();
        Assert.Equal(MachineSnapshotRegistryUpdateStatus.Accepted, registry.Upsert(snapshot).Status);
        var operation = new DarkHandThiefOperationService(
            catalog,
            DarkHandThiefCapabilityGate.Evaluate(
                catalog,
                new MachineRuntimeEvidence(true, true, true)
            ),
            authority,
            registry
        );
        var adapter = new AppliedThiefAdapter(snapshot);
        var coordinator = new DarkHandLeaseCoordinator(
            authority,
            new[] { new DarkHandThiefLeaseOperationBinding(operation, adapter) }
        );
        var context = Context(Owner, DarkHandThiefModeIds.Thief);
        var request = Request(
            Owner,
            1,
            targetId: snapshot.TargetId,
            operationId: DarkHandThiefOperationIds.Thief,
            revision: snapshot.AuthorityRevision
        );

        var lease = coordinator.TryIssue(request, context, 10).Lease!;
        var applied = coordinator.Commit(Commit(lease), context, 11);
        var duplicate = coordinator.Commit(Commit(lease), context, 12);

        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Applied, applied.Disposition);
        var receipt = Assert.IsType<DarkHandThiefReceipt>(applied.OperationReceipt);
        Assert.Equal(DarkHandThiefReceipt.CurrentSchemaVersion, receipt.SchemaVersion);
        Assert.True(receipt.MachineObjectPreserved);
        Assert.True(receipt.ExternalStatePreserved);
        Assert.Equal(DarkHandThiefDeletedContentKind.RunningContents, receipt.ContentKind);
        Assert.Equal(DarkHandLeaseBoundCommitDisposition.Duplicate, duplicate.Disposition);
        Assert.Same(applied.OperationReceipt, duplicate.OperationReceipt);
        Assert.Equal(1, adapter.CommitCount);
    }

    [Fact]
    public void Coordinator_state_is_bounded_and_has_no_snapshot_or_fingerprint_contract()
    {
        Assert.Equal(3, DarkHandLeaseCoordinator.MaximumBindings);
        Assert.Equal(256, DarkHandInteractionLeaseAuthority.MaximumOwners);
        Assert.Equal(256, DarkHandInteractionLeaseAuthority.MaximumLeaseReceipts);
        Assert.Equal(256, DarkHandInteractionLeaseInbox.MaximumReceipts);

        var coordinatorProperties = typeof(DarkHandLeaseCoordinator)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(coordinatorProperties, name => name.Contains("Snapshot", StringComparison.Ordinal));
        Assert.DoesNotContain(coordinatorProperties, name => name.Contains("Fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_installs_bounded_transport_observer_and_transaction_hooks_without_reflection()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkHandLeaseCoordinator",
            "SmapiDarkHandLeaseCoordinatorService.cs"
        );
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("PrivateOwnerLeaseTransportInstalled: transportBound", source, StringComparison.Ordinal);
        Assert.Contains("OwnerLocalCommitTransportInstalled: transportBound", source, StringComparison.Ordinal);
        Assert.Contains("ObserverLeaseBroadcastInstalled: transportBound", source, StringComparison.Ordinal);
        Assert.Contains("WorldMutationHookInstalled: available", source, StringComparison.Ordinal);
        Assert.Contains("MaximumIssuedBindings = 256", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCooldowns = 256", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModMessageReceived", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetField(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_adapters_are_version_gated_capped_and_never_remove_world_objects()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkHandLeaseCoordinator",
            "SmapiDarkHandWorldTransactionAdapters.cs"
        );
        var source = File.ReadAllText(path);

        Assert.Contains("VerifiedFileVersion = \"1.6.15.24356\"", source, StringComparison.Ordinal);
        Assert.Contains("MaximumObjectsPerScan = 256", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTrackedTargets = 256", source, StringComparison.Ordinal);
        Assert.Contains("torch.checkForAction(owner, justCheckingForActivity: false)", source, StringComparison.Ordinal);
        Assert.Contains("machine.MinutesUntilReady = plan.MinutesUntilReadyAfter", source, StringComparison.Ordinal);
        Assert.Contains("machine.heldObject.Value = null", source, StringComparison.Ordinal);
        Assert.Contains("machine.lastInputItem.Value = null", source, StringComparison.Ordinal);
        Assert.Contains("dark-hand.harassment-eject-disabled-no-atomic-landing", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Objects.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("objects.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Debris(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Inventory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Chest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor.Log", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_transport_has_owner_private_lease_commit_and_result_lanes()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkHandLeaseCoordinator",
            "SmapiHostileShadowMultiplayerCoordinator.cs"
        );
        var source = File.ReadAllText(path);

        Assert.Contains("DarkHandCommitRequestMessageType", source, StringComparison.Ordinal);
        Assert.Contains("DarkHandCommitResultMessageType", source, StringComparison.Ordinal);
        Assert.Contains("new[] { e.FromPlayerID }", source, StringComparison.Ordinal);
        Assert.Contains("new[] { host.UniqueMultiplayerID }", source, StringComparison.Ordinal);
        Assert.Contains("MaximumLocalDarkHandOwners = 16", source, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, DarkHandInteractionLeaseInbox>", source, StringComparison.Ordinal);
        Assert.Contains("BindDarkHandInteractionHandler", source, StringComparison.Ordinal);
        Assert.Contains("HandleOwnerContextInvalidated", source, StringComparison.Ordinal);
        Assert.Contains("request.Trigger == ShadowSnapshotTrigger.Warp", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_commit_revalidates_sanity_mode_config_rule_revision_distance_and_cooldown()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarkHandLeaseCoordinator",
            "SmapiDarkHandLeaseCoordinatorService.cs"
        );
        var source = File.ReadAllText(path);

        Assert.Contains("TryGetTierState", source, StringComparison.Ordinal);
        Assert.Contains("IsEventCoverageActiveForPlayer", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveCurrentMode", source, StringComparison.Ordinal);
        Assert.Contains("ConfigFingerprint", source, StringComparison.Ordinal);
        Assert.Contains("RuleRevision", source, StringComparison.Ordinal);
        Assert.Contains("RefreshForCommit", source, StringComparison.Ordinal);
        Assert.Contains("distanceTiles", source, StringComparison.Ordinal);
        Assert.Contains("IsCoolingDown", source, StringComparison.Ordinal);
        Assert.Contains("RetireOutstanding", source, StringComparison.Ordinal);
        Assert.Contains("terminalResults", source, StringComparison.Ordinal);
        Assert.Contains("public void HandleOwnerContextInvalidated", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.ClearOperationWindows()", source, StringComparison.Ordinal);
    }

    private static CoordinatorFixture Fixture(long lifetimeTicks = 120)
    {
        var authority = StartedAuthority(lifetimeTicks);
        var bindingA = new RecordingBinding(authority, OperationA, ModeA);
        var bindingB = new RecordingBinding(authority, OperationB, ModeB);
        return new CoordinatorFixture(
            new DarkHandLeaseCoordinator(authority, new[] { bindingA, bindingB }),
            bindingA,
            bindingB
        );
    }

    private static DarkHandInteractionLeaseAuthority StartedAuthority(long lifetimeTicks = 120)
    {
        var authority = new DarkHandInteractionLeaseAuthority(lifetimeTicks);
        Assert.True(authority.BeginSession(SessionA, out var reason), reason);
        return authority;
    }

    private static MachineTargetCatalog EnabledMachineCatalog()
    {
        var load = MachineTargetCatalog.Load(
            "{\"SchemaVersion\":2,\"ContractId\":\"sanity.dark-hand-machines.v2\"," +
            "\"Targets\":[{\"Id\":\"fixture-machine\",\"QualifiedItemId\":\"(BC)17\"," +
            "\"ExpectedCategory\":\"OrdinarySingleInputFinite\",\"Enabled\":true," +
            "\"Excluded\":false,\"AllowDelay\":true,\"AllowEject\":true,\"AllowThief\":true," +
            "\"LocationAllowlist\":[\"Farm\"],\"Evidence\":[\"fixture\"],\"Reason\":\"fixture\"}]}"
        );
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static MachineInteractionSnapshot MachineSnapshot(MachineTargetCatalog catalog)
    {
        var observation = new MachineReadObservation(
            "machine-target",
            "Farm",
            "(BC)17",
            7,
            "Default",
            100,
            ReadyForHarvest: false,
            ShowNextIndex: false,
            HeldOutput: new MachineItemFacts(
                "(O)428",
                1,
                0,
                IsRecipe: false,
                MachineContentProtection.Ordinary
            ),
            LastInputItem: new MachineItemFacts(
                "(O)440",
                1,
                0,
                IsRecipe: false,
                MachineContentProtection.Ordinary
            ),
            Data: new MachineDataFacts(
                IsIncubator: false,
                OnlyCompleteOvernight: false,
                HasAdditionalConsumedItems: false,
                HasCustomInteractMethod: false,
                HasClearContentsOvernightCondition: false,
                ActiveRule: new MachineRuleFacts(
                    "Default",
                    MachineTriggerKinds.ItemPlacedInMachine,
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
            new MachineRuntimeEvidence(true, true, true)
        );
    }

    private static DarkHandInteractionLeaseRequest Request(
        string owner,
        long nonce,
        string targetId = TargetA,
        string operationId = OperationA,
        long revision = 7
    ) =>
        new()
        {
            SessionId = SessionA,
            Nonce = nonce,
            OwnerPlayerKey = owner,
            LocationId = "Farm",
            TargetId = targetId,
            OperationId = operationId,
            ObservedTargetRevision = revision,
        };

    private static DarkHandInteractionLeaseCommitRequest Commit(
        DarkHandInteractionLease lease
    ) =>
        new() { LeaseId = lease.LeaseId, Nonce = lease.Nonce };

    private static DarkHandLeaseOwnerContext Context(string owner, string mode) =>
        new(
            IsHostAuthority: true,
            SenderPlayerKey: owner,
            OwnerPlayerKey: owner,
            OwnerLocationId: "Farm",
            OwnerDistanceTiles: 5d,
            HostSanityEligible: true,
            HostSanityRevision: 7,
            ModeId: mode
        );

    private sealed record CoordinatorFixture(
        DarkHandLeaseCoordinator Coordinator,
        RecordingBinding BindingA,
        RecordingBinding BindingB
    );

    private sealed record SyntheticReceipt(string LeaseId, string OperationId, int Sequence);

    private sealed class RecordingBinding : IDarkHandLeaseOperationBinding
    {
        private readonly DarkHandInteractionLeaseAuthority authority;
        private readonly Dictionary<string, long> revisions = new(StringComparer.Ordinal)
        {
            [TargetA] = 7,
            [TargetB] = 7,
        };
        private readonly Dictionary<string, SyntheticReceipt> receipts = new(StringComparer.Ordinal);

        internal RecordingBinding(
            DarkHandInteractionLeaseAuthority authority,
            string operationId,
            string modeId
        )
        {
            this.authority = authority;
            OperationId = operationId;
            ModeId = modeId;
        }

        public string OperationId { get; }
        public string ModeId { get; }
        internal int IssueCount { get; private set; }
        internal int MutationCount { get; private set; }
        internal int ReceiptCount => receipts.Count;

        internal void SetRevision(string targetId, long revision) => revisions[targetId] = revision;

        public DarkHandLeaseIssueResult TryIssue(
            DarkHandInteractionLeaseRequest request,
            DarkHandLeaseOwnerContext context,
            long nowTick
        )
        {
            IssueCount++;
            return authority.TryIssue(
                request,
                context.SenderPlayerKey,
                nowTick,
                new TargetAuthority(revisions, OperationId),
                requireExclusiveTargetOwner: true
            );
        }

        public DarkHandLeaseBoundCommitResult Commit(
            DarkHandInteractionLease lease,
            DarkHandLeaseOwnerContext context,
            long nowTick
        )
        {
            if (receipts.TryGetValue(lease.LeaseId, out var receipt))
            {
                return new DarkHandLeaseBoundCommitResult(
                    DarkHandLeaseBoundCommitDisposition.Duplicate,
                    "fixture.duplicate",
                    OperationId,
                    WorldMutationApplied: false,
                    receipt
                );
            }
            var current = revisions.TryGetValue(lease.TargetId, out var revision)
                ? new DarkHandLeaseTargetSnapshot(
                    lease.TargetId,
                    lease.LocationId,
                    OperationId,
                    revision
                )
                : null;
            var validation = authority.ValidateAndConsume(
                lease,
                context.SenderPlayerKey,
                nowTick,
                current
            );
            if (!validation.Accepted)
            {
                return new DarkHandLeaseBoundCommitResult(
                    DarkHandLeaseBoundCommitDisposition.Rejected,
                    validation.Reason,
                    OperationId,
                    WorldMutationApplied: false,
                    OperationReceipt: null
                );
            }

            MutationCount++;
            receipt = new SyntheticReceipt(lease.LeaseId, OperationId, MutationCount);
            receipts.Add(lease.LeaseId, receipt);
            return new DarkHandLeaseBoundCommitResult(
                DarkHandLeaseBoundCommitDisposition.Applied,
                "fixture.applied",
                OperationId,
                WorldMutationApplied: true,
                receipt
            );
        }

        public void ClearWindow()
        {
            receipts.Clear();
            MutationCount = 0;
        }
    }

    private sealed class TargetAuthority : IDarkHandLeaseTargetAuthority
    {
        private readonly IReadOnlyDictionary<string, long> revisions;
        private readonly string operationId;

        internal TargetAuthority(
            IReadOnlyDictionary<string, long> revisions,
            string operationId
        )
        {
            this.revisions = revisions;
            this.operationId = operationId;
        }

        public bool TryResolve(
            string targetId,
            out DarkHandLeaseTargetSnapshot? target,
            out string reason
        )
        {
            if (!revisions.TryGetValue(targetId, out var revision))
            {
                target = null;
                reason = "fixture.target-missing";
                return false;
            }
            target = new DarkHandLeaseTargetSnapshot(targetId, "Farm", operationId, revision);
            reason = "fixture.target-resolved";
            return true;
        }
    }

    private sealed class FireAdapter : IDarkHandFireExtinguishAdapter
    {
        public DarkHandFireAdapterResult Commit(DarkHandFireCommitPlan plan) =>
            new(
                DarkHandFireAdapterStatus.Rejected,
                "fixture.unavailable",
                TargetStillPresent: true,
                IsFireOnAfter: true,
                LightRefreshRequested: false
            );
    }

    private sealed class AppliedFireAdapter : IDarkHandFireExtinguishAdapter
    {
        internal int CallCount { get; private set; }

        public DarkHandFireAdapterResult Commit(DarkHandFireCommitPlan plan)
        {
            CallCount++;
            return new DarkHandFireAdapterResult(
                DarkHandFireAdapterStatus.Applied,
                "fixture.applied",
                TargetStillPresent: true,
                IsFireOnAfter: false,
                LightRefreshRequested: true
            );
        }
    }

    private sealed class RandomSource : IDarkHandHarassmentRandomSource
    {
        public double NextUnitInterval() => 0d;
    }

    private sealed class SequenceRandomSource : IDarkHandHarassmentRandomSource
    {
        private readonly Queue<double> values;

        internal SequenceRandomSource(params double[] values)
        {
            this.values = new Queue<double>(values);
        }

        internal int CallCount { get; private set; }

        public double NextUnitInterval()
        {
            CallCount++;
            return values.Dequeue();
        }
    }

    private sealed class HarassmentAdapter : IDarkHandHarassmentMachineAdapter
    {
        public DarkHandHarassmentLandingResult ReserveLanding(
            DarkHandHarassmentLandingRequest request
        ) => new(false, "fixture.unavailable", null);

        public DarkHandHarassmentAdapterResult CommitDelay(
            DarkHandHarassmentDelayCommitPlan plan
        ) => throw new InvalidOperationException("fixture must remain unavailable");

        public DarkHandHarassmentAdapterResult CommitEject(
            DarkHandHarassmentEjectCommitPlan plan
        ) => throw new InvalidOperationException("fixture must remain unavailable");
    }

    private sealed class AppliedHarassmentAdapter : IDarkHandHarassmentMachineAdapter
    {
        private readonly MachineInteractionSnapshot before;

        internal AppliedHarassmentAdapter(MachineInteractionSnapshot before)
        {
            this.before = before;
        }

        internal int CommitCount { get; private set; }

        public DarkHandHarassmentLandingResult ReserveLanding(
            DarkHandHarassmentLandingRequest request
        ) => new(false, "fixture.delay-only", null);

        public DarkHandHarassmentAdapterResult CommitDelay(
            DarkHandHarassmentDelayCommitPlan plan
        )
        {
            CommitCount++;
            return new DarkHandHarassmentAdapterResult(
                DarkHandHarassmentAdapterStatus.Applied,
                "fixture.applied",
                before with
                {
                    AuthorityRevision = before.AuthorityRevision + 1,
                    StateFingerprint = new string('H', 64),
                    MinutesUntilReady = plan.MinutesUntilReadyAfter,
                },
                WorldMutationApplied: true,
                RollbackVerified: false,
                ItemLandedExactlyOnce: false
            );
        }

        public DarkHandHarassmentAdapterResult CommitEject(
            DarkHandHarassmentEjectCommitPlan plan
        ) => throw new InvalidOperationException("fixture selected delay");
    }

    private sealed class ThiefAdapter : IDarkHandThiefMachineAdapter
    {
        public DarkHandThiefAdapterResult CommitDelete(DarkHandThiefCommitPlan plan) =>
            throw new InvalidOperationException("fixture must remain unavailable");
    }

    private sealed class AppliedThiefAdapter : IDarkHandThiefMachineAdapter
    {
        private readonly MachineInteractionSnapshot before;

        internal AppliedThiefAdapter(MachineInteractionSnapshot before)
        {
            this.before = before;
        }

        internal int CommitCount { get; private set; }

        public DarkHandThiefAdapterResult CommitDelete(DarkHandThiefCommitPlan plan)
        {
            CommitCount++;
            return new DarkHandThiefAdapterResult(
                DarkHandThiefAdapterStatus.Applied,
                "fixture.applied",
                before with
                {
                    AuthorityRevision = before.AuthorityRevision + 1,
                    StateFingerprint = new string('T', 64),
                    State = MachineLifecycleState.Empty,
                    Category = MachineInteractionCategory.Unsupported,
                    Schedule = MachineScheduleKind.None,
                    ActiveRuleId = string.Empty,
                    MinutesUntilReady = 0,
                    ReadyForHarvest = false,
                    ShowNextIndex = false,
                    HeldOutput = null,
                    LastInputItem = null,
                    CanDelay = false,
                    CanEject = false,
                    CanLandHeldOutput = false,
                    CanDeleteContent = false,
                },
                WorldMutationApplied: true,
                RollbackVerified: false,
                ContentPermanentlyDeleted: true,
                MachineObjectPreserved: true,
                ExternalStatePreserved: true
            );
        }
    }
}
