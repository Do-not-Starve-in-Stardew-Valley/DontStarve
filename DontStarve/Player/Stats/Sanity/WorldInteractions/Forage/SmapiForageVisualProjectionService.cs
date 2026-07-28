#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewCritter = StardewValley.BellsAndWhistles.Critter;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;

/// <summary>
/// Owner-local forage visual adapter. It preserves the inherited Rabbit projection and adds the
/// exact Stardew 1.6.15 ground-object draw seam. Vanilla drawing is suppressed only after the
/// private replacement draw succeeds; no critter, object, asset, pickup, or multiplayer state is
/// mutated.
/// </summary>
internal sealed class SmapiForageVisualProjectionService : IDisposable
{
    private sealed class RuntimeOwnerContext
    {
        internal RuntimeOwnerContext(
            long multiplayerId,
            HarmlessProjectionOwnerContext owner
        )
        {
            MultiplayerId = multiplayerId;
            Owner = owner;
        }

        internal long MultiplayerId { get; }
        internal HarmlessProjectionOwnerContext Owner { get; }
    }

    internal const int DriftValidationIntervalTicks = 60;
    internal const string ExpectedGameVersion = "1.6.15";
    private const int MaximumLoggedFailures = 64;

    private static SmapiForageVisualProjectionService? activeInstance;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SanitySmapiResourceService resources;
    private readonly ForageVisualProjectionCache cache = new();
    private readonly Dictionary<int, RuntimeOwnerContext> ownersByScreen = new();
    private readonly HashSet<ForageProjectionOwnerScreenKey> lifecycleEventBlocks = new();
    private readonly HashSet<ForageProjectionOwnerScreenKey> engineEventBlocks = new();
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private readonly ForageReplacementCatalog replacementCatalog;
    private readonly ForageGroundProjectionCapability groundCapability;

    private Harmony? harmony;
    private MethodInfo? patchedDrawMethod;
    private MethodInfo? patchedObjectDrawMethod;
    private SanitySlotResourceResult? rabbitResource;
    private ForageRabbitVisualCapability? rabbitVisualCapability;
    private bool patchInstalled;
    private bool groundPatchInstalled;
    private bool dayEnding;
    private bool disposed;

    internal SmapiForageVisualProjectionService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        SanitySystemLifecycleCoordinator lifecycle,
        SanitySmapiResourceService resources
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod ID is required.", nameof(modId));

        replacementCatalog = LoadReplacementCatalog(helper.DirectoryPath);
        groundCapability = ForageGroundProjectionCapabilityGate.Evaluate(replacementCatalog);
        LogGroundCapability();
        InstallRabbitDrawPatch(modId);
        InstallGroundObjectDrawPatch(modId);

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged += OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        resources.VisualResourcesInvalidating += OnVisualResourcesInvalidating;
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal int RabbitProjectionCount => cache.RabbitProjectionCount;

    internal int GroundObjectProjectionCount => cache.GroundObjectProjectionCount;

    internal ForageGroundProjectionCapability GroundCapability => groundCapability;

    /// <summary>
    /// The pickup capability consumes the same startup catalog instance. Reading the JSON a
    /// second time would create a drifting authority between local projection and picker status.
    /// </summary>
    internal ForageReplacementCatalog ReplacementCatalog => replacementCatalog;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
        if (harmony is not null && patchedDrawMethod is not null)
        {
            try
            {
                harmony.Unpatch(
                    patchedDrawMethod,
                    HarmonyPatchType.Prefix,
                    harmony.Id
                );
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    "patch-uninstall",
                    $"Forage rabbit projection patch cleanup failed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }
        if (harmony is not null && patchedObjectDrawMethod is not null)
        {
            try
            {
                harmony.Unpatch(
                    patchedObjectDrawMethod,
                    HarmonyPatchType.Prefix,
                    harmony.Id
                );
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    "object-patch-uninstall",
                    $"Forage object projection patch cleanup failed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }
        patchInstalled = false;
        groundPatchInstalled = false;
        ClearProjectionState(clearVisual: true);
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.EventOwnerCoverageChanged -= OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        resources.VisualResourcesInvalidating -= OnVisualResourcesInvalidating;
        resources.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.Content.AssetsInvalidated -= OnAssetsInvalidated;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private static bool BeforeCritterDraw(StardewCritter __instance, SpriteBatch b)
    {
        if (
            __instance.GetType() != typeof(Rabbit)
            || activeInstance is not { } service
        )
            return true;

        var rabbit = (Rabbit)__instance;

        try
        {
            // Returning false is allowed only after a complete replacement draw. Any unavailable
            // owner/resource/cache fact falls through to Stardew's original rabbit rendering.
            return !service.TryDrawRabbitProjection(rabbit, b);
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                "rabbit-draw",
                $"Forage rabbit projection draw failed open to the original sprite ({exception.GetType().Name}: {exception.Message})."
            );
            return true;
        }
    }

    private static bool BeforeObjectDraw(
        StardewValley.Object __instance,
        SpriteBatch spriteBatch,
        int x,
        int y,
        float alpha
    )
    {
        if (activeInstance is not { } service)
            return true;

        try
        {
            // The source is suppressed only after its private cached replacement completed the
            // same vanilla object draw. Any owner/cache/resource drift falls through unchanged.
            return !service.TryDrawGroundObjectProjection(
                __instance,
                spriteBatch,
                x,
                y,
                alpha
            );
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                "ground-object-draw",
                $"Forage ground projection failed open to the original object ({exception.GetType().Name}: {exception.Message})."
            );
            return true;
        }
    }

    private bool TryDrawGroundObjectProjection(
        StardewValley.Object source,
        SpriteBatch spriteBatch,
        int x,
        int y,
        float alpha
    )
    {
        if (
            disposed
            || !groundPatchInstalled
            || dayEnding
            || IsEngineEventActive()
            || !TryGetCachedCurrentOwner(out var runtimeOwner)
            || IsEventBlocked(runtimeOwner.Owner)
            || !cache.TryGetGroundReplacement(
                runtimeOwner.Owner,
                source,
                x,
                y,
                source.Stack,
                source.Quality,
                replacementCatalog.Revision,
                out var replacementReference
            )
            || replacementReference is not StardewValley.Object replacement
        )
        {
            return false;
        }

        replacement.draw(spriteBatch, x, y, alpha);
        return true;
    }

    private bool TryDrawRabbitProjection(Rabbit rabbit, SpriteBatch spriteBatch)
    {
        if (
            disposed
            || !patchInstalled
            || dayEnding
            || IsEngineEventActive()
            || !TryGetCachedCurrentOwner(out var runtimeOwner)
            || IsEventBlocked(runtimeOwner.Owner)
            || !cache.ContainsRabbit(runtimeOwner.Owner, rabbit)
            || rabbitResource?.VisualPreview is not { } preview
            || rabbitResource.PhysicalResource is not XnaSanityTextureResource texture
            || rabbitVisualCapability is not { IsAvailable: true }
        )
        {
            return false;
        }

        var source = preview.SourceRectangle;
        var pivot = preview.PivotSourcePx!.Value;
        var worldPixel = rabbit.position
            + new Vector2(0f, rabbit.yJumpOffset + rabbit.yOffset);
        var screenPixel = Game1.GlobalToLocal(Game1.viewport, worldPixel);
        var effects = rabbit.flip
            ? SpriteEffects.FlipHorizontally
            : SpriteEffects.None;
        var layerDepth = rabbit.position.Y / 10000f
            + rabbit.position.X / 1000000f;

        spriteBatch.Draw(
            texture.Texture,
            screenPixel,
            new Rectangle(source.X, source.Y, source.Width, source.Height),
            Color.White,
            0f,
            new Vector2(pivot.X, pivot.Y),
            (float)preview.DrawScale,
            effects,
            layerDepth
        );
        spriteBatch.Draw(
            Game1.shadowTexture,
            Game1.GlobalToLocal(
                Game1.viewport,
                rabbit.position + new Vector2(0f, -4f)
            ),
            Game1.shadowTexture.Bounds,
            Color.White
                * (1f
                    - Math.Min(
                        1f,
                        Math.Abs((rabbit.yJumpOffset + rabbit.yOffset) / 64f)
                    )),
            0f,
            new Vector2(
                Game1.shadowTexture.Bounds.Center.X,
                Game1.shadowTexture.Bounds.Center.Y
            ),
            3f + Math.Max(-3f, (rabbit.yJumpOffset + rabbit.yOffset) / 64f),
            SpriteEffects.None,
            (rabbit.position.Y - 1f) / 10000f
        );
        return true;
    }

    private void InstallRabbitDrawPatch(string modId)
    {
        if (activeInstance is not null && !ReferenceEquals(activeInstance, this))
        {
            LogFailureOnce(
                "patch-instance-conflict",
                "Forage rabbit projection patch was disabled because another service instance is still active."
            );
            return;
        }

        try
        {
            patchedDrawMethod = AccessTools.DeclaredMethod(
                typeof(StardewCritter),
                nameof(StardewCritter.draw),
                new[] { typeof(SpriteBatch) }
            );
            var prefix = AccessTools.DeclaredMethod(
                typeof(SmapiForageVisualProjectionService),
                nameof(BeforeCritterDraw)
            );
            if (patchedDrawMethod is null || prefix is null)
            {
                LogFailureOnce(
                    "patch-target-unavailable",
                    "Forage rabbit projection draw capability is unavailable (reason=forage.projection.rabbit-draw-target-unavailable)."
                );
                return;
            }

            harmony = new Harmony(string.Concat(modId, ".Sanity4.ForageProjection"));
            harmony.Patch(patchedDrawMethod, prefix: new HarmonyMethod(prefix));
            activeInstance = this;
            patchInstalled = true;
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                "patch-install",
                $"Forage rabbit projection draw capability is unavailable (reason=forage.projection.rabbit-draw-patch-failed, {exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void InstallGroundObjectDrawPatch(string modId)
    {
        if (!groundCapability.CanProjectGroundObjects)
            return;
        if (!string.Equals(Game1.version, ExpectedGameVersion, StringComparison.Ordinal))
        {
            LogFailureOnce(
                "object-patch-version",
                $"Forage ground projection is unavailable (expected-game-version={ExpectedGameVersion}, actual={Game1.version})."
            );
            return;
        }
        if (activeInstance is not null && !ReferenceEquals(activeInstance, this))
        {
            LogFailureOnce(
                "object-patch-instance-conflict",
                "Forage ground projection was disabled because another service instance is active."
            );
            return;
        }

        try
        {
            patchedObjectDrawMethod = AccessTools.DeclaredMethod(
                typeof(StardewValley.Object),
                nameof(StardewValley.Object.draw),
                new[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }
            );
            var prefixMethod = AccessTools.DeclaredMethod(
                typeof(SmapiForageVisualProjectionService),
                nameof(BeforeObjectDraw)
            );
            if (
                patchedObjectDrawMethod is null
                || prefixMethod is null
                || patchedObjectDrawMethod.IsStatic
                || patchedObjectDrawMethod.ReturnType != typeof(void)
                || patchedObjectDrawMethod.GetParameters().Length != 4
            )
            {
                LogFailureOnce(
                    "object-patch-target",
                    "Forage ground projection is unavailable (reason=forage.projection.object-draw-signature-drift)."
                );
                return;
            }

            harmony ??= new Harmony(
                string.Concat(modId, ".Sanity4.ForageProjection")
            );
            harmony.Patch(
                patchedObjectDrawMethod,
                prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First }
            );
            if (
                !IsOwnedPatchInstalled(
                    patchedObjectDrawMethod,
                    harmony.Id,
                    HarmonyPatchType.Prefix
                )
            )
            {
                harmony.Unpatch(
                    patchedObjectDrawMethod,
                    HarmonyPatchType.Prefix,
                    harmony.Id
                );
                LogFailureOnce(
                    "object-patch-readback",
                    "Forage ground projection is unavailable (reason=forage.projection.object-draw-patch-readback-failed)."
                );
                return;
            }

            activeInstance = this;
            groundPatchInstalled = true;
        }
        catch (Exception exception)
        {
            if (harmony is not null && patchedObjectDrawMethod is not null)
            {
                try
                {
                    harmony.Unpatch(
                        patchedObjectDrawMethod,
                        HarmonyPatchType.Prefix,
                        harmony.Id
                    );
                }
                catch (Exception cleanupException)
                {
                    LogFailureOnce(
                        "object-patch-cleanup",
                        $"Forage object projection partial patch cleanup failed ({cleanupException.GetType().Name}: {cleanupException.Message})."
                    );
                }
            }
            LogFailureOnce(
                "object-patch-install",
                $"Forage ground projection patch failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;

        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.TierEntered
                when string.Equals(
                    stateEvent.TierId,
                    SanityTierIds.BeardRabbit,
                    StringComparison.Ordinal
                ):
                RefreshCurrentOwnerIfMatches(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.TierExited
                when string.Equals(
                    stateEvent.TierId,
                    SanityTierIds.BeardRabbit,
                    StringComparison.Ordinal
                ):
                cache.CleanupOwner(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.SystemDisabled:
                ClearProjectionState(clearVisual: false);
                break;
            case SanityStateEventKind.OwnerInvalidated:
                cache.CleanupOwner(stateEvent.PlayerKey);
                RemoveOwnerContexts(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.WorldCleanup:
                ClearProjectionState(clearVisual: false);
                break;
        }
    }

    private void OnEventOwnerCoverageChanged(
        SanityEventOwnerCoverageChanged change
    )
    {
        if (disposed)
            return;

        var key = new ForageProjectionOwnerScreenKey(
            change.Key.PlayerKey,
            change.Key.ScreenId
        );
        if (change.Active)
        {
            lifecycleEventBlocks.Add(key);
            cache.CleanupOwnerScreen(key.PlayerKey, key.ScreenId);
            return;
        }

        lifecycleEventBlocks.Remove(key);
        RefreshCurrentOwnerIfMatches(key.PlayerKey, key.ScreenId);
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;

        if (boundary == SanityWorldBoundary.DayStarted)
        {
            dayEnding = false;
            ClearProjectionState(clearVisual: false);
            return;
        }

        if (boundary == SanityWorldBoundary.Warp)
        {
            var screenId = Context.ScreenId;
            cache.CleanupScreen(screenId);
            ownersByScreen.Remove(screenId);
            RemoveEventBlocksForScreen(screenId);
        }
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (disposed)
            return;
        ClearProjectionState(clearVisual: false);
    }

    private void OnVisualResourcesInvalidating()
    {
        if (disposed)
            return;
        ClearProjectionState(clearVisual: true);
        loggedFailures.Clear();
    }

    private void OnWorldResourcesReleasing(SanityResourceReleaseReason reason)
    {
        if (disposed)
            return;
        ClearProjectionState(clearVisual: true);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (e.IsMultipleOf(DriftValidationIntervalTicks))
            CleanupInvalidScreens();
        if (!TryCaptureCurrentOwner(out var runtimeOwner, out var contextChanged))
            return;

        var key = new ForageProjectionOwnerScreenKey(
            runtimeOwner.Owner.PlayerKey,
            runtimeOwner.Owner.ScreenId
        );
        var engineEventActive = IsEngineEventActive();
        var engineEventChanged = engineEventActive
            ? engineEventBlocks.Add(key)
            : engineEventBlocks.Remove(key);
        if (engineEventActive)
        {
            cache.CleanupOwnerScreen(key.PlayerKey, key.ScreenId);
            return;
        }

        if (
            contextChanged
            || engineEventChanged
            || e.IsMultipleOf(DriftValidationIntervalTicks)
        )
        {
            RefreshOwner(runtimeOwner);
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (disposed)
            return;
        dayEnding = true;
        ClearProjectionState(clearVisual: false);
    }

    private void OnAssetsInvalidated(
        object? sender,
        AssetsInvalidatedEventArgs e
    )
    {
        if (!disposed)
            ClearProjectionState(clearVisual: true);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void RefreshCurrentOwnerIfMatches(
        string playerKey,
        int? screenId = null
    )
    {
        if (
            !TryCaptureCurrentOwner(out var runtimeOwner, out _)
            || !string.Equals(
                runtimeOwner.Owner.PlayerKey,
                playerKey,
                StringComparison.Ordinal
            )
            || (screenId.HasValue && runtimeOwner.Owner.ScreenId != screenId.Value)
        )
        {
            return;
        }
        RefreshOwner(runtimeOwner);
    }

    private void RefreshOwner(RuntimeOwnerContext runtimeOwner)
    {
        var owner = runtimeOwner.Owner;
        var tierEligible = TryGetEligibleTier(owner.PlayerKey);
        var blocked = dayEnding || IsEngineEventActive() || IsEventBlocked(owner);
        var rabbitVisualAvailable =
            tierEligible && !blocked && EnsureRabbitVisual();
        if (!tierEligible || blocked)
        {
            cache.RefreshRabbits(
                owner,
                Array.Empty<object>(),
                tierEligible,
                blocked,
                rabbitVisualAvailable
            );
            cache.RefreshGroundObjects(
                owner,
                Array.Empty<ForageGroundProjectionCandidate>(),
                tierEligible,
                blocked,
                rendererAvailable: false
            );
            return;
        }

        var location = (GameLocation)owner.LocationReference;
        var rabbits = new List<object>(
            Math.Min(
                location.critters?.Count ?? 0,
                ForageVisualProjectionCache.MaximumRabbitCandidatesPerOwnerScreen
            )
        );
        if (location.critters is not null)
        {
            var count = Math.Min(
                location.critters.Count,
                ForageVisualProjectionCache.MaximumRabbitCandidatesPerOwnerScreen
            );
            for (var index = 0; index < count; index++)
            {
                if (location.critters[index] is Rabbit rabbit)
                    rabbits.Add(rabbit);
            }
        }

        cache.RefreshRabbits(
            owner,
            rabbits,
            tierEligible: true,
            eventBlocked: false,
            visualAvailable: rabbitVisualAvailable
        );

        var groundCandidates = new List<ForageGroundProjectionCandidate>(
            ForageVisualProjectionCache.MaximumGroundObjectCandidatesPerOwnerScreen
        );
        if (groundCapability.CanProjectGroundObjects && groundPatchInstalled)
        {
            var inspected = 0;
            foreach (var pair in location.Objects.Pairs)
            {
                if (
                    inspected
                    >= ForageVisualProjectionCache.MaximumGroundObjectCandidatesPerOwnerScreen
                )
                {
                    break;
                }
                inspected++;

                var source = pair.Value;
                var mapping = replacementCatalog.FindBySource(
                    source.QualifiedItemId
                );
                if (
                    mapping is not
                    {
                        Enabled: true,
                        TargetQualifiedItemId: not null,
                    }
                    || !mapping.AllowsLocation(location.Name)
                    || !mapping.AllowsContext(
                        ForagePickupContextIds.NormalDirectObjectPickup
                    )
                    || source.Stack != 1
                    || (
                        !source.isSpawnedObject.Value
                        && !ItemRegistry
                            .GetDataOrErrorItem(source.QualifiedItemId)
                            .IsErrorItem
                    )
                    || !ItemRegistry.Exists(mapping.TargetQualifiedItemId)
                    || ItemRegistry.Create(
                        mapping.TargetQualifiedItemId,
                        1,
                        source.Quality,
                        allowNull: true
                    ) is not StardewValley.Object replacement
                )
                {
                    continue;
                }

                groundCandidates.Add(
                    new ForageGroundProjectionCandidate(
                        source,
                        replacement,
                        (int)pair.Key.X,
                        (int)pair.Key.Y,
                        source.Stack,
                        source.Quality,
                        replacementCatalog.Revision,
                        mapping.Id
                    )
                );
            }
        }

        cache.RefreshGroundObjects(
            owner,
            groundCandidates,
            tierEligible: true,
            eventBlocked: false,
            rendererAvailable:
                groundCapability.CanProjectGroundObjects && groundPatchInstalled
        );
    }

    private bool TryGetEligibleTier(string playerKey)
    {
        if (
            !lifecycle.IsEnabled
            || !lifecycle.TryGetTierState(playerKey, out var tierState)
            || tierState is null
            || !tierState.IsAvailable
        )
        {
            return false;
        }

        var hasBeardRabbitTier = false;
        foreach (var tierId in tierState.ActiveTierIds)
        {
            if (string.Equals(tierId, SanityTierIds.BeardRabbit, StringComparison.Ordinal))
            {
                hasBeardRabbitTier = true;
                break;
            }
        }
        if (!hasBeardRabbitTier)
            return false;

        var eligibility = ForageInteractionSanityGate.Evaluate(
            tierState.Current,
            tierState.Maximum
        );
        if (!eligibility.IsEligible)
        {
            LogFailureOnce(
                string.Concat("tier-boundary|", eligibility.Reason),
                $"Forage projection tier snapshot failed closed (player={playerKey}, reason={eligibility.Reason})."
            );
        }
        return eligibility.IsEligible;
    }

    private bool EnsureRabbitVisual()
    {
        if (rabbitVisualCapability.HasValue)
            return rabbitVisualCapability.Value.IsAvailable;

        var result = resources.LoadVisualSlot(
            ForageRabbitVisualContract.TextureSlotId,
            frameIndex: 0
        );
        var preview = result.VisualPreview;
        var descriptor = new ForageRabbitVisualDescriptor(
            result.Success && result.PhysicalResource is XnaSanityTextureResource,
            preview?.RequestedSlotId ?? result.Diagnostic.SlotId,
            preview?.TextureSlotId ?? string.Empty,
            preview?.Kind ?? default,
            preview?.SourceRectangle ?? default,
            preview?.PivotSourcePx,
            preview?.DrawScale ?? 0d,
            preview?.OwnerLocalOnly == true,
            result.Diagnostic.IsPlaceholder || preview?.IsPlaceholder == true,
            preview?.IsProvisional == true,
            result.Diagnostic.Code
        );
        var capability = ForageRabbitVisualContract.Evaluate(descriptor);
        rabbitVisualCapability = capability;
        if (!capability.IsAvailable)
        {
            rabbitResource = null;
            LogFailureOnce(
                string.Concat("rabbit-resource|", capability.Reason),
                $"Forage rabbit projection resource failed closed (slot={ForageRabbitVisualContract.TextureSlotId}, reason={capability.Reason})."
            );
            return false;
        }

        rabbitResource = result;
        if (capability.IsPlaceholder || capability.IsProvisional)
        {
            LogFailureOnce(
                "rabbit-resource-placeholder",
                $"Forage rabbit projection is using a Development placeholder (slot={ForageRabbitVisualContract.TextureSlotId}, reason={capability.Reason}); ReleaseAssetEligible remains false."
            );
        }
        return true;
    }

    private bool TryCaptureCurrentOwner(
        out RuntimeOwnerContext runtimeOwner,
        out bool contextChanged
    )
    {
        runtimeOwner = null!;
        contextChanged = false;
        var player = Game1.player;
        var location = Game1.currentLocation;
        var screenId = Context.ScreenId;
        if (
            !Context.IsWorldReady
            || !Context.HasScreenId(screenId)
            || player is null
            || !player.IsLocalPlayer
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
        )
        {
            return false;
        }

        if (
            ownersByScreen.TryGetValue(screenId, out var cached)
            && cached.MultiplayerId == player.UniqueMultiplayerID
            && ReferenceEquals(cached.Owner.LocationReference, location)
            && string.Equals(
                cached.Owner.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            runtimeOwner = cached;
            return true;
        }

        cache.CleanupScreen(screenId);
        RemoveEventBlocksForScreen(screenId);
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return false;

        runtimeOwner = new RuntimeOwnerContext(
            player.UniqueMultiplayerID,
            new HarmlessProjectionOwnerContext(
                playerKey,
                screenId,
                location,
                location.NameOrUniqueName
            )
        );
        ownersByScreen[screenId] = runtimeOwner;
        contextChanged = true;
        return true;
    }

    private bool TryGetCachedCurrentOwner(out RuntimeOwnerContext runtimeOwner)
    {
        runtimeOwner = null!;
        var player = Game1.player;
        var location = Game1.currentLocation;
        var screenId = Context.ScreenId;
        if (
            !Context.IsWorldReady
            || !Context.HasScreenId(screenId)
            || player is null
            || !player.IsLocalPlayer
            || location is null
            || !ownersByScreen.TryGetValue(screenId, out var cachedOwner)
            || cachedOwner.MultiplayerId != player.UniqueMultiplayerID
            || !ReferenceEquals(cachedOwner.Owner.LocationReference, location)
            || !string.Equals(
                cachedOwner.Owner.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        runtimeOwner = cachedOwner;
        return true;
    }

    private bool IsEventBlocked(HarmlessProjectionOwnerContext owner)
    {
        var key = new ForageProjectionOwnerScreenKey(owner.PlayerKey, owner.ScreenId);
        return lifecycle.IsEventCoverageActiveForPlayer(owner.PlayerKey)
            || lifecycleEventBlocks.Contains(key)
            || engineEventBlocks.Contains(key);
    }

    private static bool IsEngineEventActive()
    {
        return Game1.eventUp || Game1.CurrentEvent is not null;
    }

    private void CleanupInvalidScreens()
    {
        cache.CleanupInvalidScreens(Context.HasScreenId);
        var screens = new List<int>();
        foreach (var screenId in ownersByScreen.Keys)
        {
            if (!Context.HasScreenId(screenId))
                screens.Add(screenId);
        }
        foreach (var screenId in screens)
        {
            ownersByScreen.Remove(screenId);
            RemoveEventBlocksForScreen(screenId);
        }
    }

    private void RemoveOwnerContexts(string playerKey)
    {
        var screens = new List<int>();
        foreach (var pair in ownersByScreen)
        {
            if (
                string.Equals(
                    pair.Value.Owner.PlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
            )
            {
                screens.Add(pair.Key);
            }
        }
        foreach (var screenId in screens)
        {
            ownersByScreen.Remove(screenId);
            RemoveEventBlocksForScreen(screenId);
        }
    }

    private void RemoveEventBlocksForScreen(int screenId)
    {
        lifecycleEventBlocks.RemoveWhere(key => key.ScreenId == screenId);
        engineEventBlocks.RemoveWhere(key => key.ScreenId == screenId);
    }

    private void ClearProjectionState(bool clearVisual)
    {
        cache.CleanupAll();
        ownersByScreen.Clear();
        lifecycleEventBlocks.Clear();
        engineEventBlocks.Clear();
        if (clearVisual)
        {
            rabbitResource = null;
            rabbitVisualCapability = null;
        }
    }

    private ForageReplacementCatalog LoadReplacementCatalog(string modDirectory)
    {
        var path = Path.Combine(
            modDirectory,
            ForageReplacementCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        try
        {
            var json = File.ReadAllText(path, new UTF8Encoding(false, true));
            var load = ForageReplacementCatalog.Load(json);
            if (load.IsAvailable)
                return load.Catalog;

            LogFailureOnce(
                string.Concat("catalog|", load.Reason),
                $"Forage projection catalog failed closed (path={ForageReplacementCatalog.RelativePath}, reason={load.Reason})."
            );
            return load.Catalog;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or DecoderFallbackException
                or ArgumentException
        )
        {
            LogFailureOnce(
                "catalog-file-unavailable",
                $"Forage projection catalog failed closed (path={ForageReplacementCatalog.RelativePath}, reason=forage.projection.catalog-file-unavailable, {exception.GetType().Name}: {exception.Message})."
            );
            return ForageReplacementCatalog.Unavailable(
                ForageVisualProjectionReasonIds.CatalogUnavailable
            );
        }
    }

    private void LogGroundCapability()
    {
        var message =
            $"Forage ground-object visual projection capability: status={groundCapability.Status}, enabled-mappings={groundCapability.EnabledMappingCount}, reason={groundCapability.Reason}.";
        monitor.Log(
            message,
            groundCapability.Status
                is ForageGroundProjectionCapabilityStatus.DisabledNoEnabledMappings
                    or ForageGroundProjectionCapabilityStatus.Available
                ? LogLevel.Debug
                : LogLevel.Error
        );
    }

    private static bool IsOwnedPatchInstalled(
        MethodInfo method,
        string ownerId,
        HarmonyPatchType patchType
    )
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;
        var patches = patchType == HarmonyPatchType.Prefix
            ? patchInfo.Prefixes
            : patchInfo.Transpilers;
        foreach (var patch in patches)
        {
            if (string.Equals(patch.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void LogFailureOnce(string key, string message)
    {
        if (loggedFailures.Count >= MaximumLoggedFailures || !loggedFailures.Add(key))
            return;
        monitor.Log(message, LogLevel.Warn);
    }
}
