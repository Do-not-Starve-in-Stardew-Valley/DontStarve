using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.Forage;

public sealed class ForagePickupAuthorityTransactionTests
{
    private const string Session = "11111111111111111111111111111111";

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
            "ForagePickup",
            "SmapiForagePickupReplacementService.cs"
        );

    [Fact]
    public void ShippedCatalogAndCurrentEvidencePublishExecutableCapability()
    {
        var capability = ForagePickupReplacementCapabilityGate.Evaluate(
            LoadShipped(),
            ForagePickupRuntimeEvidence.Current
        );

        Assert.Equal(
            ForagePickupReplacementCapabilityStatus.Available,
            capability.Status
        );
        Assert.Equal(1, capability.EnabledMappingCount);
        Assert.True(capability.CanExecute);
    }

    [Fact]
    public void EnabledCatalogRequiresEveryRuntimeCapabilityInOrder()
    {
        var catalog = LoadEnabledCatalog();

        AssertCapability(
            catalog,
            new ForagePickupRuntimeEvidence(false, false, false, false),
            ForagePickupReplacementCapabilityStatus.UnavailableNormalPickupHook
        );
        AssertCapability(
            catalog,
            new ForagePickupRuntimeEvidence(true, false, false, false),
            ForagePickupReplacementCapabilityStatus.UnavailableObjectFingerprint
        );
        AssertCapability(
            catalog,
            new ForagePickupRuntimeEvidence(true, true, false, false),
            ForagePickupReplacementCapabilityStatus.UnavailableAtomicCommitAdapter
        );
        AssertCapability(
            catalog,
            new ForagePickupRuntimeEvidence(true, true, true, false),
            ForagePickupReplacementCapabilityStatus.UnavailableMultiplayerTransport
        );
        AssertCapability(
            catalog,
            AvailableEvidence(),
            ForagePickupReplacementCapabilityStatus.Available
        );
    }

    [Theory]
    [InlineData(39.999d, true)]
    [InlineData(40d, true)]
    [InlineData(40.001d, false)]
    public void HostCurrentSanityAtomicallySelectsReplacementOrOriginal(
        double current,
        bool replacement
    )
    {
        var fixture = AvailableFixture();
        var result = fixture.Service.Handle(
            fixture.Request,
            Observation(fixture.Request, sanityCurrent: current),
            fixture.Adapter
        );

        Assert.True(result.Applied);
        Assert.Equal(replacement, result.ReplacementApplied);
        Assert.Equal(
            replacement
                ? ForagePickupTransactionDisposition.ReplacementApplied
                : ForagePickupTransactionDisposition.OriginalApplied,
            result.Disposition
        );
        Assert.Equal(
            replacement
                ? ForagePickupDeliveryKind.Replacement
                : ForagePickupDeliveryKind.Original,
            fixture.Adapter.LastPlan?.DeliveryKind
        );
        Assert.Equal(replacement ? "(O)2" : "(O)1", fixture.Adapter.LastPlan?.DeliveryQualifiedItemId);
    }

    [Fact]
    public void ClientObservedSanityRevisionIsNotHostAuthority()
    {
        var fixture = AvailableFixture();
        var observation = Observation(fixture.Request);
        var snapshot = observation.SanitySnapshot!;
        snapshot.Revision = 999;

        var result = fixture.Service.Handle(
            fixture.Request,
            observation with { SanitySnapshot = snapshot },
            fixture.Adapter
        );

        Assert.True(result.ReplacementApplied);
        Assert.Equal(1, fixture.Adapter.Calls);
    }

    [Theory]
    [InlineData("identity", ForageReplacementReasonIds.ObjectIdentityMismatch)]
    [InlineData("fingerprint", ForagePickupTransactionReasonIds.ObservationMismatch)]
    [InlineData("branch", ForageReplacementReasonIds.DirectPickupBranchRequired)]
    [InlineData("structure", ForageReplacementReasonIds.SpawnedOrErrorObjectRequired)]
    public void DirectBranchDriftPreservesSourceWithoutCommit(
        string missing,
        string expectedReason
    )
    {
        var fixture = AvailableFixture();
        var observation = Observation(fixture.Request);
        var facts = observation.PickupFacts;
        facts = missing switch
        {
            "identity" => facts with { ObjectIdentityMatchesLocationTile = false },
            "fingerprint" => facts with { ObjectFingerprint = 0 },
            "branch" => facts with { IsDirectPickupBranch = false },
            "structure" => facts with { IsSpawnedObjectOrErrorItem = false },
            _ => throw new InvalidOperationException(missing),
        };

        var result = fixture.Service.Handle(
            fixture.Request,
            observation with { PickupFacts = facts },
            fixture.Adapter
        );

        Assert.Equal(
            ForagePickupTransactionDisposition.OriginalFlowPreserved,
            result.Disposition
        );
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public void TwoPickersMayUseSameNonceWithoutSharingReceipt()
    {
        var service = StartedAvailableService();
        var adapter = new RecordingCommitAdapter();
        var first = Request(pickerId: 101, nonce: 1);
        var second = Request(pickerId: 202, nonce: 1);

        var firstResult = service.Handle(first, Observation(first), adapter);
        var secondResult = service.Handle(second, Observation(second), adapter);

        Assert.True(firstResult.Applied);
        Assert.True(secondResult.Applied);
        Assert.NotEqual(firstResult.Receipt?.ReceiptId, secondResult.Receipt?.ReceiptId);
        Assert.Equal(2, service.OwnerCount);
        Assert.Equal(2, service.ReceiptCount);
    }

    [Fact]
    public void DuplicateAndConflictingNonceNeverCommitTwice()
    {
        var fixture = AvailableFixture();
        var first = fixture.Service.Handle(
            fixture.Request,
            Observation(fixture.Request),
            fixture.Adapter
        );
        var duplicate = fixture.Service.Handle(
            fixture.Request,
            Observation(fixture.Request),
            fixture.Adapter
        );
        var conflict = Request(nonce: fixture.Request.Nonce, tileX: 13);
        var conflicting = fixture.Service.Handle(
            conflict,
            Observation(conflict),
            fixture.Adapter
        );

        Assert.True(first.Applied);
        Assert.Equal(
            ForagePickupTransactionDisposition.IgnoredDuplicate,
            duplicate.Disposition
        );
        Assert.Equal(
            ForagePickupTransactionDisposition.Rejected,
            conflicting.Disposition
        );
        Assert.Equal(ForagePickupTransactionReasonIds.NonceConflict, conflicting.Reason);
        Assert.Equal(1, fixture.Adapter.Calls);
    }

    [Fact]
    public void OlderNonceIsRejectedAfterHigherNonce()
    {
        var service = StartedAvailableService();
        var adapter = new RecordingCommitAdapter();
        var newer = Request(nonce: 2);
        var older = Request(nonce: 1);

        Assert.True(service.Handle(newer, Observation(newer), adapter).Applied);
        var stale = service.Handle(older, Observation(older), adapter);

        Assert.Equal(ForagePickupTransactionDisposition.Rejected, stale.Disposition);
        Assert.Equal(ForagePickupTransactionReasonIds.NonceOutOfOrder, stale.Reason);
        Assert.Equal(1, adapter.Calls);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("invalid")]
    public void HostSanitySnapshotMustBeValidAndBelongToPicker(string mismatch)
    {
        var fixture = AvailableFixture();
        var snapshot = new SanityPlayerSnapshot
        {
            PlayerKey = mismatch == "owner" ? "202" : fixture.Request.PickerPlayerKey,
            Current = mismatch == "invalid" ? double.NaN : 40d,
            Maximum = 100d,
            Revision = 7,
        };

        var result = fixture.Service.Handle(
            fixture.Request,
            Observation(fixture.Request) with { SanitySnapshot = snapshot },
            fixture.Adapter
        );

        Assert.Equal(
            mismatch == "invalid"
                ? ForagePickupTransactionReasonIds.SanitySnapshotInvalid
                : ForagePickupTransactionReasonIds.SanitySnapshotMismatch,
            result.Reason
        );
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Theory]
    [InlineData(true, false, 40d)]
    [InlineData(false, true, 41d)]
    public void CapacityIsCheckedForHostSelectedDeliveryOnly(
        bool originalCapacity,
        bool replacementCapacity,
        double sanity
    )
    {
        var fixture = AvailableFixture();
        var observation = Observation(fixture.Request, sanityCurrent: sanity) with
        {
            InventoryCanAcceptOriginal = originalCapacity,
            InventoryCanAcceptReplacement = replacementCapacity,
        };

        var result = fixture.Service.Handle(
            fixture.Request,
            observation,
            fixture.Adapter
        );

        Assert.Equal(
            ForagePickupTransactionDisposition.OriginalFlowPreserved,
            result.Disposition
        );
        Assert.Equal(
            ForagePickupTransactionReasonIds.InventoryCapacityUnavailable,
            result.Reason
        );
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public void CommitPlanUsesHostHarvestQualityAndSelectedStack()
    {
        var fixture = AvailableFixture();
        var observation = Observation(fixture.Request) with
        {
            HarvestQuality = 4,
            ReplacementDeliveryStack = 2,
        };

        var result = fixture.Service.Handle(
            fixture.Request,
            observation,
            fixture.Adapter
        );

        Assert.True(result.ReplacementApplied);
        Assert.Equal(2, fixture.Adapter.LastPlan?.Stack);
        Assert.Equal(4, fixture.Adapter.LastPlan?.Quality);
    }

    [Theory]
    [InlineData((int)ForagePickupAtomicCommitStatus.RejectedBeforeMutation)]
    [InlineData((int)ForagePickupAtomicCommitStatus.RolledBack)]
    public void RejectedOrRolledBackCommitHasNoSuccessReceipt(int status)
    {
        var fixture = AvailableFixture((ForagePickupAtomicCommitStatus)status);

        var result = fixture.Service.Handle(
            fixture.Request,
            Observation(fixture.Request),
            fixture.Adapter
        );

        Assert.Equal(
            ForagePickupTransactionDisposition.OriginalFlowPreserved,
            result.Disposition
        );
        Assert.Equal(1, fixture.Adapter.Calls);
        Assert.Equal(0, fixture.Service.SuccessfulTransactionCount);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("instance")]
    [InlineData("tile")]
    [InlineData("source")]
    [InlineData("fingerprint")]
    [InlineData("stack")]
    [InlineData("quality")]
    [InlineData("catalog")]
    [InlineData("mapping")]
    public void RequestAndHostObservationIdentityAreRevalidated(string mismatch)
    {
        var fixture = AvailableFixture();
        var request = fixture.Request;
        var observation = Observation(request);
        var facts = observation.PickupFacts;
        switch (mismatch)
        {
            case "location":
                observation = observation with
                {
                    PickupFacts = facts with { LocationId = "Forest" },
                };
                break;
            case "tile":
                observation = observation with
                {
                    PickupFacts = facts with { TileX = 99 },
                };
                break;
            case "instance":
                observation = observation with
                {
                    PickupFacts = facts with
                    {
                        LocationInstanceId = "OtherFarm",
                    },
                };
                break;
            case "source":
                observation = observation with
                {
                    PickupFacts = facts with { SourceQualifiedItemId = "(O)3" },
                };
                break;
            case "fingerprint":
                observation = observation with
                {
                    PickupFacts = facts with { ObjectFingerprint = 8 },
                };
                break;
            case "stack":
                observation = observation with { ObservedSourceStack = 2 };
                break;
            case "quality":
                observation = observation with { ObservedSourceOriginalQuality = 4 };
                break;
            case "catalog":
                request.CatalogRevision = 2;
                break;
            case "mapping":
                request.MappingId = "other";
                break;
            default:
                throw new InvalidOperationException(mismatch);
        }

        var result = fixture.Service.Handle(request, observation, fixture.Adapter);

        Assert.Equal(
            ForagePickupTransactionReasonIds.ObservationMismatch,
            result.Reason
        );
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public void SessionClearRetiresNonceReceiptAndSuccessWindows()
    {
        var fixture = AvailableFixture();
        Assert.True(
            fixture.Service.Handle(
                fixture.Request,
                Observation(fixture.Request),
                fixture.Adapter
            ).Applied
        );

        fixture.Service.ClearSession();
        var result = fixture.Service.Handle(
            fixture.Request,
            Observation(fixture.Request),
            fixture.Adapter
        );

        Assert.Equal(ForagePickupTransactionReasonIds.SessionInactive, result.Reason);
        Assert.Equal(0, fixture.Service.OwnerCount);
        Assert.Equal(0, fixture.Service.ReceiptCount);
        Assert.Equal(0, fixture.Service.SuccessfulTransactionCount);
    }

    [Fact]
    public void ReceiptWindowEvictsBoundedlyButHighWaterNonceStillRejectsReplay()
    {
        var service = StartedAvailableService();
        var adapter = new RecordingCommitAdapter();
        for (
            var nonce = 1;
            nonce <= ForagePickupTransactionService.MaximumReceipts + 1;
            nonce++
        )
        {
            var request = Request(nonce: nonce);
            Assert.True(service.Handle(request, Observation(request), adapter).Applied);
        }

        var replay = Request(nonce: 1);
        var result = service.Handle(replay, Observation(replay), adapter);

        Assert.Equal(ForagePickupTransactionService.MaximumReceipts, service.ReceiptCount);
        Assert.Equal(ForagePickupTransactionDisposition.Rejected, result.Disposition);
        Assert.Equal(ForagePickupTransactionReasonIds.NonceOutOfOrder, result.Reason);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("nonce")]
    [InlineData("catalog")]
    [InlineData("mapping")]
    [InlineData("instance")]
    [InlineData("picker")]
    [InlineData("context")]
    [InlineData("fingerprint")]
    [InlineData("stack")]
    [InlineData("quality")]
    public void MalformedTransportRequestFailsProtocolValidation(string invalid)
    {
        var request = Request();
        switch (invalid)
        {
            case "session":
                request.SessionId = "wrong";
                break;
            case "nonce":
                request.Nonce = 0;
                break;
            case "catalog":
                request.CatalogRevision = 0;
                break;
            case "mapping":
                request.MappingId = string.Empty;
                break;
            case "picker":
                request.PickerMultiplayerId = 0;
                break;
            case "instance":
                request.LocationInstanceId = string.Empty;
                break;
            case "context":
                request.ContextId = "debris";
                break;
            case "fingerprint":
                request.ObjectFingerprint = 0;
                break;
            case "stack":
                request.SourceStack = 2;
                break;
            case "quality":
                request.SourceOriginalQuality = 3;
                break;
            default:
                throw new InvalidOperationException(invalid);
        }

        Assert.False(
            ForagePickupTransactionProtocol.IsValidRequest(request, Session)
        );
    }

    [Fact]
    public void RuntimeOwnsExactVersionedSeamTransportAndAtomicRollback()
    {
        var source = File.ReadAllText(RuntimeSourcePath);

        Assert.Contains("ExpectedGameVersion = \"1.6.15\"", source, StringComparison.Ordinal);
        Assert.Contains("nameof(GameLocation.checkAction)", source, StringComparison.Ordinal);
        Assert.Contains("TranspileCheckAction", source, StringComparison.Ordinal);
        Assert.Contains("BeforeDirectObjectPickup", source, StringComparison.Ordinal);
        Assert.Contains("nameof(StardewObject.isSpawnedObject)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(Item.quality)", source, StringComparison.Ordinal);
        Assert.Contains("Harmony.GetPatchInfo", source, StringComparison.Ordinal);
        Assert.Contains("RequestMessageType", source, StringComparison.Ordinal);
        Assert.Contains("e.FromPlayerID", source, StringComparison.Ordinal);
        Assert.Contains("Game1.GetPlayer", source, StringComparison.Ordinal);
        Assert.Contains(
            "Utility.tileWithinRadiusOfPlayer(",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("source.questItem.Value", source, StringComparison.Ordinal);
        Assert.Contains("lifecycle.TryGetTierState(", source, StringComparison.Ordinal);
        Assert.Contains(
            "lifecycle.IsEventCoverageActiveForPlayer(",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("location.Objects.Remove(tile)", source, StringComparison.Ordinal);
        Assert.Contains("RestoreInventory", source, StringComparison.Ordinal);
        Assert.Contains("MaximumPendingRequests = 32", source, StringComparison.Ordinal);
        Assert.Contains("PendingRequestTtlTicks = 180", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShopMenu", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Debris(", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("typeof(Debris)")]
    [InlineData("typeof(ShopMenu)")]
    [InlineData("typeof(ItemGrabMenu)")]
    [InlineData("typeof(Furniture)")]
    [InlineData("typeof(Crop)")]
    [InlineData("performToolAction")]
    [InlineData("checkForAction")]
    [InlineData("MachineData")]
    [InlineData("Utility.addItemToInventory")]
    public void RuntimeDoesNotPatchExcludedPickupOrContainerRoutes(string marker)
    {
        Assert.DoesNotContain(
            marker,
            File.ReadAllText(RuntimeSourcePath),
            StringComparison.Ordinal
        );
    }

    private static void AssertCapability(
        ForageReplacementCatalog catalog,
        ForagePickupRuntimeEvidence evidence,
        ForagePickupReplacementCapabilityStatus expected
    )
    {
        var capability = ForagePickupReplacementCapabilityGate.Evaluate(
            catalog,
            evidence
        );
        Assert.Equal(expected, capability.Status);
        Assert.Equal(1, capability.EnabledMappingCount);
        Assert.Equal(
            expected == ForagePickupReplacementCapabilityStatus.Available,
            capability.CanExecute
        );
    }

    private static (
        ForagePickupTransactionService Service,
        RecordingCommitAdapter Adapter,
        ForagePickupTransactionRequest Request
    ) AvailableFixture(
        ForagePickupAtomicCommitStatus status = ForagePickupAtomicCommitStatus.Applied
    )
    {
        return (
            StartedAvailableService(),
            new RecordingCommitAdapter(status),
            Request()
        );
    }

    private static ForagePickupTransactionService StartedAvailableService()
    {
        var catalog = LoadEnabledCatalog();
        var capability = ForagePickupReplacementCapabilityGate.Evaluate(
            catalog,
            AvailableEvidence()
        );
        var service = new ForagePickupTransactionService(catalog, capability);
        Assert.True(service.BeginSession(Session, out var reason), reason);
        return service;
    }

    private static ForagePickupTransactionRequest Request(
        long pickerId = 101,
        long nonce = 1,
        int tileX = 12
    )
    {
        return new ForagePickupTransactionRequest
        {
            SessionId = Session,
            Nonce = nonce,
            CatalogRevision = 1,
            MappingId = "enabled-test",
            PickerPlayerKey = pickerId.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            PickerMultiplayerId = pickerId,
            LocationId = "Farm",
            LocationInstanceId = "Farm",
            TileX = tileX,
            TileY = 34,
            ContextId = ForagePickupContextIds.NormalDirectObjectPickup,
            SourceQualifiedItemId = "(O)1",
            ObjectFingerprint = 7,
            SourceStack = 1,
            SourceOriginalQuality = 2,
        };
    }

    private static ForagePickupAuthorityObservation Observation(
        ForagePickupTransactionRequest request,
        double sanityCurrent = 40d
    )
    {
        return new ForagePickupAuthorityObservation(
            new ForagePickupFactSnapshot(
                request.PickerMultiplayerId,
                PickerMatchesRequest: true,
                IsHostAuthoritative: true,
                request.LocationId,
                request.LocationInstanceId,
                request.TileX,
                request.TileY,
                request.ContextId,
                request.SourceQualifiedItemId,
                ObjectIdentityMatchesLocationTile: true,
                request.ObjectFingerprint,
                IsDirectPickupBranch: true,
                IsSpawnedObjectOrErrorItem: true,
                TargetQualifiedItemIdExists: true
            ),
            new SanityPlayerSnapshot
            {
                PlayerKey = request.PickerPlayerKey,
                Current = sanityCurrent,
                Maximum = 100d,
                Revision = 7,
            },
            request.SourceStack,
            request.SourceOriginalQuality,
            HarvestQuality: 2,
            OriginalDeliveryStack: 1,
            ReplacementDeliveryStack: 1,
            InventoryCanAcceptOriginal: true,
            InventoryCanAcceptReplacement: true
        );
    }

    private static ForagePickupRuntimeEvidence AvailableEvidence()
    {
        return new ForagePickupRuntimeEvidence(true, true, true, true);
    }

    private static ForageReplacementCatalog LoadShipped()
    {
        var load = ForageReplacementCatalog.Load(File.ReadAllText(ShippedCatalogPath));
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private static ForageReplacementCatalog LoadEnabledCatalog()
    {
        var json =
            "{\"SchemaVersion\":2,\"ContractId\":\"sanity.direct-pickup-replacements.v2\"," +
            "\"Revision\":1,\"Mappings\":[{\"Id\":\"enabled-test\",\"SourceQualifiedItemId\":\"(O)1\"," +
            "\"TargetQualifiedItemId\":\"(O)2\",\"Enabled\":true," +
            "\"LocationAllowlist\":[\"Farm\"]," +
            "\"ContextAllowlist\":[\"stardew.game-location-check-action.normal-direct-object-pickup.v2\"]," +
            "\"Evidence\":[\"synthetic-transaction-test\"],\"Reason\":\"synthetic-enabled\"}]}";
        var load = ForageReplacementCatalog.Load(json);
        Assert.True(load.IsAvailable, load.Reason);
        return load.Catalog;
    }

    private sealed class RecordingCommitAdapter : IForagePickupAtomicCommitAdapter
    {
        private readonly ForagePickupAtomicCommitStatus status;

        internal RecordingCommitAdapter(
            ForagePickupAtomicCommitStatus status = ForagePickupAtomicCommitStatus.Applied
        )
        {
            this.status = status;
        }

        internal int Calls { get; private set; }
        internal ForagePickupCommitPlan? LastPlan { get; private set; }

        public ForagePickupAtomicCommitResult TryCommit(ForagePickupCommitPlan plan)
        {
            Calls++;
            LastPlan = plan;
            return status switch
            {
                ForagePickupAtomicCommitStatus.Applied =>
                    ForagePickupAtomicCommitResult.Applied(),
                ForagePickupAtomicCommitStatus.RejectedBeforeMutation =>
                    ForagePickupAtomicCommitResult.Rejected(),
                ForagePickupAtomicCommitStatus.RolledBack =>
                    ForagePickupAtomicCommitResult.RolledBack(),
                _ => new ForagePickupAtomicCommitResult(status, "invalid-test-status"),
            };
        }
    }
}
