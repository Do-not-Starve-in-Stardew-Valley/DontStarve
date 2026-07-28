#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using TileLocation = xTile.Dimensions.Location;
using StardewObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;

internal sealed class ForagePickupResultMessage
{
    public int ProtocolVersion { get; set; } =
        ForagePickupTransactionProtocol.ProtocolVersion;
    public string SessionId { get; set; } = string.Empty;
    public long Nonce { get; set; }
    public long PickerMultiplayerId { get; set; }
    public ForagePickupTransactionDisposition Disposition { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Stardew 1.6.15 direct-object pickup adapter. Its transpiler enters after vanilla captures the
/// original quality and before quality, inventory, stats, or location mutation. Farmhands send a
/// bounded request; only the host revalidates the picker, tile object, fingerprint, mapping, and
/// current Sanity before committing either the original item or its replacement.
/// </summary>
internal sealed class SmapiForagePickupReplacementService : IDisposable
{
    private sealed class PendingClientPickup
    {
        internal PendingClientPickup(
            ForagePickupTransactionRequest request,
            StardewObject sourceReference,
            long createdTick
        )
        {
            Request = request;
            SourceReference = sourceReference;
            CreatedTick = createdTick;
        }

        internal ForagePickupTransactionRequest Request { get; }
        internal StardewObject SourceReference { get; }
        internal long CreatedTick { get; }
    }

    private sealed class InventorySnapshot
    {
        internal InventorySnapshot(Item?[] items, int[] stacks)
        {
            Items = items;
            Stacks = stacks;
        }

        internal Item?[] Items { get; }
        internal int[] Stacks { get; }
    }

    private sealed class HostCommitAdapter : IForagePickupAtomicCommitAdapter
    {
        private readonly SmapiForagePickupReplacementService owner;
        private readonly GameLocation location;
        private readonly Farmer picker;
        private readonly StardewObject source;
        private readonly Vector2 tile;
        private readonly long expectedFingerprint;

        internal HostCommitAdapter(
            SmapiForagePickupReplacementService owner,
            GameLocation location,
            Farmer picker,
            StardewObject source,
            Vector2 tile,
            long expectedFingerprint
        )
        {
            this.owner = owner;
            this.location = location;
            this.picker = picker;
            this.source = source;
            this.tile = tile;
            this.expectedFingerprint = expectedFingerprint;
        }

        public ForagePickupAtomicCommitResult TryCommit(
            ForagePickupCommitPlan plan
        )
        {
            if (
                plan.ObjectFingerprint != expectedFingerprint
                || plan.PickerMultiplayerId != picker.UniqueMultiplayerID
                || plan.TileX != (int)tile.X
                || plan.TileY != (int)tile.Y
                || !string.Equals(
                    plan.LocationInstanceId,
                    location.NameOrUniqueName,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    plan.SourceQualifiedItemId,
                    source.QualifiedItemId,
                    StringComparison.Ordinal
                )
                || !location.Objects.TryGetValue(tile, out var current)
                || !ReferenceEquals(current, source)
                || owner.ComputeObjectFingerprint(source) != expectedFingerprint
            )
            {
                return ForagePickupAtomicCommitResult.Rejected(
                    "host-object-drift-before-commit"
                );
            }

            var delivery = owner.CreateDeliveryItem(
                source,
                plan.DeliveryQualifiedItemId,
                plan.DeliveryKind,
                plan.Stack,
                plan.Quality
            );
            if (
                delivery is null
                || delivery.Stack != plan.Stack
                || !picker.couldInventoryAcceptThisItem(delivery)
            )
            {
                return ForagePickupAtomicCommitResult.Rejected(
                    "host-delivery-preflight-rejected"
                );
            }

            var inventory = CaptureInventory(picker);
            try
            {
                var leftover = picker.addItemToInventory(delivery);
                if (leftover is not null)
                {
                    RestoreInventory(picker, inventory);
                    return ForagePickupAtomicCommitResult.RolledBack(
                        "host-inventory-add-returned-leftover"
                    );
                }
                if (
                    !location.Objects.TryGetValue(tile, out var beforeRemove)
                    || !ReferenceEquals(beforeRemove, source)
                    || owner.ComputeObjectFingerprint(source)
                        != expectedFingerprint
                    || !location.Objects.Remove(tile)
                )
                {
                    RestoreInventory(picker, inventory);
                    return ForagePickupAtomicCommitResult.RolledBack(
                        "host-source-drifted-before-remove"
                    );
                }
            }
            catch (Exception exception)
            {
                RestoreInventory(picker, inventory);
                owner.LogFailureOnce(
                    string.Concat(
                        "commit-add|",
                        exception.GetType().FullName
                    ),
                    $"Forage pickup inventory commit rolled back ({exception.GetType().Name}: {exception.Message})."
                );
                return ForagePickupAtomicCommitResult.RolledBack(
                    "host-inventory-add-threw"
                );
            }

            owner.CompleteVanillaPickupConsequences(
                location,
                picker,
                source,
                plan.Quality,
                plan.Stack > 1
            );
            return ForagePickupAtomicCommitResult.Applied(
                plan.DeliveryKind == ForagePickupDeliveryKind.Replacement
                    ? "replacement-granted-source-removed"
                    : "original-granted-source-removed"
            );
        }

        private static InventorySnapshot CaptureInventory(Farmer picker)
        {
            var items = new Item?[picker.Items.Count];
            var stacks = new int[picker.Items.Count];
            for (var index = 0; index < picker.Items.Count; index++)
            {
                var item = picker.Items[index];
                items[index] = item;
                stacks[index] = item?.Stack ?? 0;
            }
            return new InventorySnapshot(items, stacks);
        }

        private static void RestoreInventory(
            Farmer picker,
            InventorySnapshot snapshot
        )
        {
            var count = Math.Min(picker.Items.Count, snapshot.Items.Length);
            for (var index = 0; index < count; index++)
            {
                picker.Items[index] = snapshot.Items[index];
                if (snapshot.Items[index] is { } item)
                    item.Stack = snapshot.Stacks[index];
            }
        }
    }

    internal const string ExpectedGameVersion = "1.6.15";
    internal const string RequestMessageType = "Sanity.ForagePickupRequest.v2";
    internal const string ResultMessageType = "Sanity.ForagePickupResult.v2";
    internal const int MaximumPendingRequests = 32;
    internal const int PendingRequestTtlTicks = 180;
    internal const int MaximumFingerprintModDataEntries = 32;
    private const int MaximumLoggedFailures = 64;

    private static SmapiForagePickupReplacementService? activeInstance;
    private static bool transpilerMarkerMatched;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly ForageReplacementCatalog catalog;
    private readonly ForagePickupTransactionService transactions;
    private readonly Dictionary<long, PendingClientPickup> pendingClientRequests =
        new();
    private readonly List<long> expiredPendingNonces = new(MaximumPendingRequests);
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);

    private Harmony? harmony;
    private MethodInfo? patchedCheckActionMethod;
    private long nextNonce;
    private bool hookInstalled;
    private bool disposed;

    internal SmapiForagePickupReplacementService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        ForageReplacementCatalog catalog
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod ID is required.", nameof(modId));
        this.modId = modId;

        InstallPickupPatch();
        Capability = ForagePickupReplacementCapabilityGate.Evaluate(
            catalog,
            new ForagePickupRuntimeEvidence(
                NormalPickupHookAvailable: hookInstalled,
                StableObjectFingerprintAvailable: true,
                AtomicCommitAdapterAvailable: true,
                MultiplayerTransportAvailable: true
            )
        );
        transactions = new ForagePickupTransactionService(catalog, Capability);

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        monitor.Log(
            string.Concat(
                "Forage direct-pickup replacement capability: status=",
                Capability.Status.ToString(),
                ", reason=",
                Capability.Reason,
                ", enabledMappings=",
                Capability.EnabledMappingCount.ToString(CultureInfo.InvariantCulture),
                ", objectFingerprint=",
                Capability.Evidence.StableObjectFingerprintAvailable.ToString(),
                ", atomicCommitAdapter=",
                Capability.Evidence.AtomicCommitAdapterAvailable.ToString(),
                ", multiplayerTransport=",
                Capability.Evidence.MultiplayerTransportAvailable.ToString(),
                ", runtimeHookInstalled=",
                hookInstalled.ToString(),
                "."
            ),
            Capability.CanExecute ? LogLevel.Debug : LogLevel.Warn
        );
    }

    internal ForagePickupReplacementCapability Capability { get; }

    internal ForagePickupRuntimeDiagnostic Diagnostic =>
        new(
            Capability.Status,
            Capability.Reason,
            Capability.EnabledMappingCount,
            hookInstalled,
            transactions.SuccessfulTransactionCount
        );

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
        if (harmony is not null && patchedCheckActionMethod is not null)
        {
            try
            {
                harmony.Unpatch(
                    patchedCheckActionMethod,
                    HarmonyPatchType.Transpiler,
                    harmony.Id
                );
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    "patch-uninstall",
                    $"Forage pickup transpiler cleanup failed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }
        hookInstalled = false;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        pendingClientRequests.Clear();
        transactions.ClearSession();
    }

    private static bool BeforeDirectObjectPickup(
        GameLocation location,
        TileLocation tileLocation,
        Farmer picker,
        StardewObject source,
        int originalQuality
    )
    {
        var service = activeInstance;
        if (service is null || service.disposed)
            return false;

        try
        {
            return service.TryHandleDirectObjectPickup(
                location,
                tileLocation,
                picker,
                source,
                originalQuality
            );
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("pickup-hook|", exception.GetType().FullName),
                $"Forage pickup hook failed open to vanilla before mutation ({exception.GetType().Name}: {exception.Message})."
            );
            return false;
        }
    }

    private static IEnumerable<CodeInstruction> TranspileCheckAction(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator
    )
    {
        var list = new List<CodeInstruction>(instructions);
        var spawnedField = AccessTools.Field(
            typeof(StardewObject),
            nameof(StardewObject.isSpawnedObject)
        );
        var qualityField = AccessTools.Field(typeof(Item), nameof(Item.quality));
        var callback = AccessTools.DeclaredMethod(
            typeof(SmapiForagePickupReplacementService),
            nameof(BeforeDirectObjectPickup)
        );
        if (spawnedField is null || qualityField is null || callback is null)
            return list;

        var spawnedIndex = -1;
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index].LoadsField(spawnedField))
            {
                spawnedIndex = index;
                break;
            }
        }
        if (spawnedIndex < 0)
            return list;

        for (
            var index = spawnedIndex + 1;
            index + 3 < list.Count && index < spawnedIndex + 96;
            index++
        )
        {
            if (
                !list[index].LoadsField(qualityField)
                || index == 0
                || !IsLoadLocal(list[index - 1])
                || list[index + 1].operand is not MethodInfo getter
                || !string.Equals(getter.Name, "get_Value", StringComparison.Ordinal)
                || !TryCreateLoadForStore(list[index + 2], out var oldQualityLoad)
            )
            {
                continue;
            }

            var continuation = generator.DefineLabel();
            list[index + 3].labels.Add(continuation);
            var sourceLoad = new CodeInstruction(
                list[index - 1].opcode,
                list[index - 1].operand
            );
            list.InsertRange(
                index + 3,
                new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_3),
                    sourceLoad,
                    oldQualityLoad,
                    new CodeInstruction(OpCodes.Call, callback),
                    new CodeInstruction(OpCodes.Brfalse, continuation),
                    new CodeInstruction(OpCodes.Ldc_I4_1),
                    new CodeInstruction(OpCodes.Ret),
                }
            );
            transpilerMarkerMatched = true;
            return list;
        }

        return list;
    }

    private bool TryHandleDirectObjectPickup(
        GameLocation location,
        TileLocation tileLocation,
        Farmer picker,
        StardewObject source,
        int originalQuality
    )
    {
        if (
            !hookInstalled
            || !Capability.CanExecute
            || !Context.IsWorldReady
            || !lifecycle.IsEnabled
            || picker is null
            || source is null
            || !TryResolveMapping(location, source, out var mapping)
            || source.Stack != 1
            || (
                !source.isSpawnedObject.Value
                && !ItemRegistry.GetDataOrErrorItem(source.QualifiedItemId).IsErrorItem
            )
            || source.questItem.Value
        )
        {
            return false;
        }

        var sessionId = lifecycle.SessionId;
        if (!EnsureTransactionSession(sessionId))
            return false;

        var fingerprint = ComputeObjectFingerprint(source);
        if (fingerprint <= 0)
            return false;
        var request = CreateRequest(
            sessionId,
            mapping,
            location,
            tileLocation,
            picker,
            source,
            fingerprint,
            originalQuality
        );

        if (
            Context.IsMainPlayer
            && lifecycle.AuthorityRole == SanityAuthorityRole.Host
        )
        {
            HandleHostRequest(request, picker, senderPlayerId: picker.UniqueMultiplayerID);
            // Once a valid mapping enters the host transaction, every terminal result owns this
            // action. A capacity/drift failure leaves the source in place instead of falling
            // through to vanilla and silently changing the selected delivery.
            return true;
        }

        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Client
            || !picker.IsLocalPlayer
            || Game1.MasterPlayer is not { } host
        )
        {
            return false;
        }

        foreach (var pending in pendingClientRequests.Values)
        {
            if (
                ReferenceEquals(pending.SourceReference, source)
                && pending.Request.ObjectFingerprint == fingerprint
                && pending.Request.TileX == tileLocation.X
                && pending.Request.TileY == tileLocation.Y
            )
            {
                return true;
            }
        }
        if (pendingClientRequests.Count >= MaximumPendingRequests)
        {
            LogFailureOnce(
                "client-pending-window-full",
                "Forage pickup client pending window is full; the mapped source was preserved."
            );
            return true;
        }

        pendingClientRequests[request.Nonce] = new PendingClientPickup(
            request,
            source,
            Game1.ticks
        );
        try
        {
            helper.Multiplayer.SendMessage(
                request,
                RequestMessageType,
                new[] { modId },
                new[] { host.UniqueMultiplayerID }
            );
        }
        catch (Exception exception)
        {
            pendingClientRequests.Remove(request.Nonce);
            LogFailureOnce(
                string.Concat("client-send|", exception.GetType().FullName),
                $"Forage pickup request could not be sent; the mapped source was preserved ({exception.GetType().Name}: {exception.Message})."
            );
        }
        return true;
    }

    private ForagePickupTransactionRequest CreateRequest(
        string sessionId,
        ForageReplacementMapping mapping,
        GameLocation location,
        TileLocation tileLocation,
        Farmer picker,
        StardewObject source,
        long fingerprint,
        int originalQuality
    )
    {
        if (nextNonce == long.MaxValue)
            nextNonce = 0;
        nextNonce++;
        return new ForagePickupTransactionRequest
        {
            SessionId = sessionId,
            Nonce = nextNonce,
            CatalogRevision = catalog.Revision,
            MappingId = mapping.Id,
            PickerPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(
                picker.UniqueMultiplayerID
            ),
            PickerMultiplayerId = picker.UniqueMultiplayerID,
            LocationId = location.Name,
            LocationInstanceId = location.NameOrUniqueName,
            TileX = tileLocation.X,
            TileY = tileLocation.Y,
            ContextId = ForagePickupContextIds.NormalDirectObjectPickup,
            SourceQualifiedItemId = source.QualifiedItemId,
            ObjectFingerprint = fingerprint,
            SourceStack = source.Stack,
            SourceOriginalQuality = originalQuality,
        };
    }

    private void HandleHostRequest(
        ForagePickupTransactionRequest request,
        Farmer picker,
        long senderPlayerId
    )
    {
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !Context.IsMainPlayer
            || picker.UniqueMultiplayerID != senderPlayerId
            || !ForagePickupTransactionProtocol.IsValidRequest(
                request,
                lifecycle.SessionId
            )
            || !EnsureTransactionSession(lifecycle.SessionId)
        )
        {
            return;
        }

        var location = picker.currentLocation;
        var tile = new Vector2(request.TileX, request.TileY);
        if (
            location is null
            || !string.Equals(
                location.NameOrUniqueName,
                request.LocationInstanceId,
                StringComparison.Ordinal
            )
            // The three vanilla action-button callers all require the requested tile to be
            // within one tile of the picker before entering checkAction. Recheck the same public
            // 1.6.15 rule on the host so a forged farmhand request cannot pick across the map.
            || !Utility.tileWithinRadiusOfPlayer(
                request.TileX,
                request.TileY,
                1,
                picker
            )
            || !location.Objects.TryGetValue(tile, out var source)
            || source.questItem.Value
            || !string.Equals(
                source.QualifiedItemId,
                request.SourceQualifiedItemId,
                StringComparison.Ordinal
            )
        )
        {
            SendHostResult(
                request,
                new ForagePickupTransactionResult(
                    ForagePickupTransactionDisposition.OriginalFlowPreserved,
                    ForagePickupTransactionReasonIds.ObservationMismatch,
                    null
                ),
                senderPlayerId
            );
            return;
        }

        var mapping = catalog.FindBySource(source.QualifiedItemId);
        var targetExists =
            mapping is
            {
                Enabled: true,
                TargetQualifiedItemId: not null,
            }
            && string.Equals(mapping.Id, request.MappingId, StringComparison.Ordinal)
            && mapping.AllowsLocation(location.Name)
            && mapping.AllowsContext(
                ForagePickupContextIds.NormalDirectObjectPickup
            )
            && ItemRegistry.Exists(mapping.TargetQualifiedItemId);
        var fingerprint = ComputeObjectFingerprint(source);
        SanityPlayerSnapshot? sanitySnapshot = null;
        if (
            lifecycle.TryGetTierState(
                request.PickerPlayerKey,
                out var tierSnapshot
            )
            && tierSnapshot is { IsAvailable: true }
        )
        {
            sanitySnapshot = new SanityPlayerSnapshot
            {
                PlayerKey = tierSnapshot.PlayerKey,
                Current = lifecycle.IsEventCoverageActiveForPlayer(
                    request.PickerPlayerKey
                )
                    ? tierSnapshot.Maximum
                    : tierSnapshot.Current,
                Maximum = tierSnapshot.Maximum,
                Revision = tierSnapshot.Revision,
            };
        }

        var harvestQuality = ResolveHarvestQuality(
            location,
            picker,
            source,
            tile,
            out var duplicateRollPassed
        );
        var originalPreview = CreateBestCapacityPreview(
            picker,
            source,
            request.SourceQualifiedItemId,
            ForagePickupDeliveryKind.Original,
            harvestQuality,
            duplicateRollPassed
        );
        var replacementPreview = targetExists
            ? CreateBestCapacityPreview(
                picker,
                source,
                mapping!.TargetQualifiedItemId!,
                ForagePickupDeliveryKind.Replacement,
                harvestQuality,
                duplicateRollPassed
            )
            : null;

        var facts = new ForagePickupFactSnapshot(
            request.PickerMultiplayerId,
            PickerMatchesRequest: picker.UniqueMultiplayerID == senderPlayerId,
            IsHostAuthoritative: true,
            location.Name,
            location.NameOrUniqueName,
            request.TileX,
            request.TileY,
            ForagePickupContextIds.NormalDirectObjectPickup,
            source.QualifiedItemId,
            ObjectIdentityMatchesLocationTile: ReferenceEquals(
                location.Objects[tile],
                source
            ),
            fingerprint,
            IsDirectPickupBranch: true,
            IsSpawnedObjectOrErrorItem:
                source.isSpawnedObject.Value
                || ItemRegistry.GetDataOrErrorItem(source.QualifiedItemId).IsErrorItem,
            TargetQualifiedItemIdExists: targetExists
        );
        var observation = new ForagePickupAuthorityObservation(
            facts,
            sanitySnapshot,
            source.Stack,
            source.Quality,
            harvestQuality,
            originalPreview?.Stack ?? 1,
            replacementPreview?.Stack ?? 1,
            InventoryCanAcceptOriginal:
                originalPreview is not null
                && picker.couldInventoryAcceptThisItem(originalPreview),
            InventoryCanAcceptReplacement:
                replacementPreview is not null
                && picker.couldInventoryAcceptThisItem(replacementPreview)
        );
        var result = transactions.Handle(
            request,
            observation,
            new HostCommitAdapter(
                this,
                location,
                picker,
                source,
                tile,
                fingerprint
            )
        );
        SendHostResult(request, result, senderPlayerId);
    }

    private Item? CreateBestCapacityPreview(
        Farmer picker,
        StardewObject source,
        string qualifiedItemId,
        ForagePickupDeliveryKind kind,
        int harvestQuality,
        bool duplicateRollPassed
    )
    {
        if (duplicateRollPassed)
        {
            var doubled = CreateDeliveryItem(
                source,
                qualifiedItemId,
                kind,
                2,
                harvestQuality
            );
            if (
                doubled is not null
                && picker.couldInventoryAcceptThisItem(doubled)
            )
            {
                return doubled;
            }
        }
        return CreateDeliveryItem(
            source,
            qualifiedItemId,
            kind,
            1,
            harvestQuality
        );
    }

    private Item? CreateDeliveryItem(
        StardewObject source,
        string qualifiedItemId,
        ForagePickupDeliveryKind kind,
        int stack,
        int quality
    )
    {
        Item? item = kind == ForagePickupDeliveryKind.Original
            ? source.getOne()
            : ItemRegistry.Create(
                qualifiedItemId,
                stack,
                quality,
                allowNull: true
            );
        if (item is null || !string.Equals(item.QualifiedItemId, qualifiedItemId, StringComparison.Ordinal))
            return null;

        item.Stack = stack;
        item.Quality = quality;
        if (kind == ForagePickupDeliveryKind.Replacement)
        {
            var copied = 0;
            foreach (var pair in source.modData.Pairs)
            {
                if (copied >= MaximumFingerprintModDataEntries)
                    return null;
                item.modData[pair.Key] = pair.Value;
                copied++;
            }
        }
        return item;
    }

    private static int ResolveHarvestQuality(
        GameLocation location,
        Farmer picker,
        StardewObject source,
        Vector2 tile,
        out bool duplicateRollPassed
    )
    {
        var random = Utility.CreateDaySaveRandom(tile.X, tile.Y * 777f);
        var quality = source.isForage()
            ? location.GetHarvestSpawnedObjectQuality(
                picker,
                source.isForage(),
                tile,
                random
            )
            : source.Quality;
        duplicateRollPassed =
            picker.professions.Contains(13)
            && random.NextDouble() < 0.2d
            && !source.questItem.Value
            && !location.isFarmBuildingInterior();
        return quality;
    }

    private void CompleteVanillaPickupConsequences(
        GameLocation location,
        Farmer picker,
        StardewObject source,
        int harvestQuality,
        bool duplicateGranted
    )
    {
        try
        {
            source.Quality = harvestQuality;
            if (picker.IsLocalPlayer)
            {
                location.localSound("pickUpItem");
                DelayedAction.playSoundAfterDelay("coin", 300);
            }
            picker.animateOnce(279 + picker.FacingDirection);
            if (!location.isFarmBuildingInterior())
            {
                if (source.isForage())
                    location.OnHarvestedForage(picker, source);
            }
            else
            {
                picker.gainExperience(0, 5);
            }
            picker.stats.ItemsForaged++;
            if (duplicateGranted)
                picker.gainExperience(2, 7);
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                string.Concat("pickup-consequence|", exception.GetType().FullName),
                $"Forage item commit succeeded but a vanilla ancillary consequence failed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private bool TryResolveMapping(
        GameLocation location,
        StardewObject source,
        out ForageReplacementMapping mapping
    )
    {
        mapping = catalog.FindBySource(source.QualifiedItemId)!;
        return catalog.IsAvailable
            && mapping is
            {
                Enabled: true,
                TargetQualifiedItemId: not null,
            }
            && mapping.AllowsLocation(location.Name)
            && mapping.AllowsContext(
                ForagePickupContextIds.NormalDirectObjectPickup
            )
            && ItemRegistry.Exists(mapping.TargetQualifiedItemId)
            && ItemRegistry.Create(
                mapping.TargetQualifiedItemId,
                1,
                source.Quality,
                allowNull: true
            ) is StardewObject;
    }

    private long ComputeObjectFingerprint(StardewObject source)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        AddFingerprint(ref hash, source.QualifiedItemId, prime);
        AddFingerprint(ref hash, source.Stack, prime);
        AddFingerprint(ref hash, source.Quality, prime);
        AddFingerprint(ref hash, source.isSpawnedObject.Value ? 1 : 0, prime);
        AddFingerprint(ref hash, source.questItem.Value ? 1 : 0, prime);
        AddFingerprint(ref hash, source.questId.Value ?? string.Empty, prime);

        var metadata = new List<KeyValuePair<string, string>>(
            MaximumFingerprintModDataEntries
        );
        foreach (var pair in source.modData.Pairs)
        {
            if (metadata.Count >= MaximumFingerprintModDataEntries)
                return 0;
            metadata.Add(pair);
        }
        metadata.Sort(
            static (left, right) =>
            {
                var key = string.CompareOrdinal(left.Key, right.Key);
                return key != 0
                    ? key
                    : string.CompareOrdinal(left.Value, right.Value);
            }
        );
        foreach (var pair in metadata)
        {
            AddFingerprint(ref hash, pair.Key, prime);
            AddFingerprint(ref hash, pair.Value, prime);
        }

        var positive = (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
        return positive == 0 ? 1 : positive;
    }

    private static void AddFingerprint(ref ulong hash, string value, ulong prime)
    {
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        hash ^= 0xFF;
        hash *= prime;
    }

    private static void AddFingerprint(ref ulong hash, int value, ulong prime)
    {
        unchecked
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= prime;
            }
        }
    }

    private void OnModMessageReceived(
        object? sender,
        ModMessageReceivedEventArgs e
    )
    {
        if (!string.Equals(e.FromModID, modId, StringComparison.Ordinal))
            return;

        try
        {
            if (
                e.Type == RequestMessageType
                && Context.IsMainPlayer
                && lifecycle.AuthorityRole == SanityAuthorityRole.Host
            )
            {
                var request = e.ReadAs<ForagePickupTransactionRequest>();
                if (
                    request is null
                    || request.PickerMultiplayerId != e.FromPlayerID
                    || Game1.GetPlayer(e.FromPlayerID, onlyOnline: true)
                        is not { } picker
                )
                {
                    return;
                }
                HandleHostRequest(request, picker, e.FromPlayerID);
                return;
            }

            if (
                e.Type == ResultMessageType
                && lifecycle.AuthorityRole == SanityAuthorityRole.Client
                && Game1.MasterPlayer is { } host
                && e.FromPlayerID == host.UniqueMultiplayerID
            )
            {
                var result = e.ReadAs<ForagePickupResultMessage>();
                if (
                    result is null
                    || result.ProtocolVersion
                        != ForagePickupTransactionProtocol.ProtocolVersion
                    || !string.Equals(
                        result.SessionId,
                        lifecycle.SessionId,
                        StringComparison.Ordinal
                    )
                    || Game1.player is not { } localPlayer
                    || result.PickerMultiplayerId
                        != localPlayer.UniqueMultiplayerID
                )
                {
                    return;
                }
                pendingClientRequests.Remove(result.Nonce);
            }
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                string.Concat("message-decode|", exception.GetType().FullName),
                $"Forage pickup multiplayer message failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void SendHostResult(
        ForagePickupTransactionRequest request,
        ForagePickupTransactionResult result,
        long pickerPlayerId
    )
    {
        if (Game1.player?.UniqueMultiplayerID == pickerPlayerId)
            return;
        try
        {
            helper.Multiplayer.SendMessage(
                new ForagePickupResultMessage
                {
                    SessionId = request.SessionId,
                    Nonce = request.Nonce,
                    PickerMultiplayerId = request.PickerMultiplayerId,
                    Disposition = result.Disposition,
                    Reason = result.Reason,
                },
                ResultMessageType,
                new[] { modId },
                new[] { pickerPlayerId }
            );
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                string.Concat("result-send|", exception.GetType().FullName),
                $"Forage pickup result could not be returned to the picker ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void OnPeerDisconnected(
        object? sender,
        PeerDisconnectedEventArgs e
    )
    {
        transactions.ClearOwner(
            SanityPlayerKey.FromUniqueMultiplayerId(e.Peer.PlayerID)
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (pendingClientRequests.Count == 0)
            return;
        expiredPendingNonces.Clear();
        foreach (var pair in pendingClientRequests)
        {
            if (Game1.ticks - pair.Value.CreatedTick >= PendingRequestTtlTicks)
                expiredPendingNonces.Add(pair.Key);
        }
        foreach (var nonce in expiredPendingNonces)
            pendingClientRequests.Remove(nonce);
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;
        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.OwnerInvalidated:
                transactions.ClearOwner(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.SystemDisabled:
            case SanityStateEventKind.WorldCleanup:
                ClearSession();
                break;
        }
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;
        // Warp/day boundaries retire only client wait state. The session nonce high-water marks
        // remain authoritative so a delayed pre-boundary request cannot become replayable.
        pendingClientRequests.Clear();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            ClearSession();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void ClearSession()
    {
        pendingClientRequests.Clear();
        transactions.ClearSession();
        nextNonce = 0;
    }

    private bool EnsureTransactionSession(string sessionId)
    {
        if (!SanityProtocol.IsValidSessionId(sessionId))
            return false;
        if (string.Equals(transactions.SessionId, sessionId, StringComparison.Ordinal))
            return true;
        return transactions.BeginSession(sessionId, out _);
    }

    private void InstallPickupPatch()
    {
        if (!string.Equals(Game1.version, ExpectedGameVersion, StringComparison.Ordinal))
        {
            LogFailureOnce(
                "patch-version",
                $"Forage pickup is unavailable (expected-game-version={ExpectedGameVersion}, actual={Game1.version})."
            );
            return;
        }
        if (activeInstance is not null && !ReferenceEquals(activeInstance, this))
        {
            LogFailureOnce(
                "patch-instance-conflict",
                "Forage pickup is unavailable because another service instance is active."
            );
            return;
        }

        patchedCheckActionMethod = AccessTools.DeclaredMethod(
            typeof(GameLocation),
            nameof(GameLocation.checkAction),
            new[] { typeof(TileLocation), typeof(Rectangle), typeof(Farmer) }
        );
        var transpilerMethod = AccessTools.DeclaredMethod(
            typeof(SmapiForagePickupReplacementService),
            nameof(TranspileCheckAction)
        );
        if (
            patchedCheckActionMethod is null
            || transpilerMethod is null
            || patchedCheckActionMethod.IsStatic
            || patchedCheckActionMethod.ReturnType != typeof(bool)
            || patchedCheckActionMethod.GetParameters().Length != 3
        )
        {
            LogFailureOnce(
                "patch-signature",
                "Forage pickup is unavailable (reason=forage.pickup.check-action-signature-drift)."
            );
            return;
        }

        try
        {
            transpilerMarkerMatched = false;
            harmony = new Harmony(string.Concat(modId, ".Sanity4.ForagePickup"));
            harmony.Patch(
                patchedCheckActionMethod,
                transpiler: new HarmonyMethod(transpilerMethod)
                {
                    priority = Priority.First,
                }
            );
            if (
                !transpilerMarkerMatched
                || !IsOwnedTranspilerInstalled(
                    patchedCheckActionMethod,
                    harmony.Id
                )
            )
            {
                harmony.Unpatch(
                    patchedCheckActionMethod,
                    HarmonyPatchType.Transpiler,
                    harmony.Id
                );
                LogFailureOnce(
                    "patch-marker-readback",
                    "Forage pickup is unavailable (reason=forage.pickup.check-action-il-marker-or-owner-readback-failed)."
                );
                return;
            }

            activeInstance = this;
            hookInstalled = true;
        }
        catch (Exception exception)
        {
            if (harmony is not null && patchedCheckActionMethod is not null)
            {
                try
                {
                    harmony.Unpatch(
                        patchedCheckActionMethod,
                        HarmonyPatchType.Transpiler,
                        harmony.Id
                    );
                }
                catch (Exception cleanupException)
                {
                    LogFailureOnce(
                        "patch-cleanup",
                        $"Forage pickup partial patch cleanup failed ({cleanupException.GetType().Name}: {cleanupException.Message})."
                    );
                }
            }
            LogFailureOnce(
                "patch-install",
                $"Forage pickup transpiler failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private static bool IsOwnedTranspilerInstalled(
        MethodInfo method,
        string ownerId
    )
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;
        foreach (var transpiler in patchInfo.Transpilers)
        {
            if (string.Equals(transpiler.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsLoadLocal(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldloc
            || instruction.opcode == OpCodes.Ldloc_S
            || instruction.opcode == OpCodes.Ldloc_0
            || instruction.opcode == OpCodes.Ldloc_1
            || instruction.opcode == OpCodes.Ldloc_2
            || instruction.opcode == OpCodes.Ldloc_3;
    }

    private static bool TryCreateLoadForStore(
        CodeInstruction store,
        out CodeInstruction load
    )
    {
        if (store.opcode == OpCodes.Stloc_0)
            load = new CodeInstruction(OpCodes.Ldloc_0);
        else if (store.opcode == OpCodes.Stloc_1)
            load = new CodeInstruction(OpCodes.Ldloc_1);
        else if (store.opcode == OpCodes.Stloc_2)
            load = new CodeInstruction(OpCodes.Ldloc_2);
        else if (store.opcode == OpCodes.Stloc_3)
            load = new CodeInstruction(OpCodes.Ldloc_3);
        else if (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S)
            load = new CodeInstruction(
                store.opcode == OpCodes.Stloc ? OpCodes.Ldloc : OpCodes.Ldloc_S,
                store.operand
            );
        else
        {
            load = null!;
            return false;
        }
        return true;
    }

    private void LogFailureOnce(string key, string message)
    {
        if (loggedFailures.Count >= MaximumLoggedFailures || !loggedFailures.Add(key))
            return;
        monitor.Log(message, LogLevel.Warn);
    }
}
