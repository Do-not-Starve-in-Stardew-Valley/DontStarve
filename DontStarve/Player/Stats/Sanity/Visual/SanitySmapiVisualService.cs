#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Events;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// SMAPI owner-local visual coordinator. It consumes the existing tier/effective/resource seams,
/// delegates final world-only composition to the exact renderer adapter, and delegates the
/// reversible non-passout animation token to its dedicated presentation owner.
/// </summary>
internal sealed class SanitySmapiVisualService : IDisposable
{
    private const string DangerBorderProfileId =
        "sanity.overlay.danger-border.profile";
    private const int MaximumLoggedDiagnostics = 32;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SanitySmapiResourceService resources;
    private readonly ISanityEffectiveSanityProvider effectiveSanity;
    private readonly SanityVisualController controller = new();
    private readonly SanityWorldCompositionRuntimeAdapter worldComposition;
    private readonly SanityIdlePresentationRuntime idlePresentation = new();
    private readonly Dictionary<int, OwnerBinding> ownersByScreen = new();
    private readonly HashSet<string> loggedDiagnostics =
        new(StringComparer.Ordinal);
    private XnaSanityTextureResource? dangerBorderTexture;
    private SanityVisualPreviewDefinition? dangerBorderPreview;
    private bool dangerBorderLoadAttempted;
    private bool enabled;
    private bool screenDistortionEnabled;
    private bool disposed;

    internal SanitySmapiVisualService(
        IModHelper helper,
        IMonitor monitor,
        string manifestId,
        SanitySystemLifecycleCoordinator lifecycle,
        SanitySmapiResourceService resources,
        ISanityEffectiveSanityProvider effectiveSanity,
        bool enabled,
        bool screenDistortionEnabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.effectiveSanity = effectiveSanity
            ?? throw new ArgumentNullException(nameof(effectiveSanity));
        this.enabled = enabled;
        this.screenDistortionEnabled = screenDistortionEnabled;
        controller.SetEnabled(enabled);
        worldComposition = new SanityWorldCompositionRuntimeAdapter(
            manifestId,
            monitor,
            enabled
        );

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.TierStateObserved += OnTierStateObserved;
        lifecycle.EventOwnerCoverageChanged += OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        resources.VisualResourcesInvalidating += OnVisualResourcesInvalidating;
        resources.WorldResourcesReleasing += OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.Display.RenderingHud += OnRenderingHud;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Input.MouseWheelScrolled += OnMouseWheelScrolled;

        LogCapabilityMatrix();
    }

    internal SanityVisualController Controller => controller;

    internal void SetEnabled(bool value)
    {
        if (disposed || enabled == value)
            return;

        enabled = value;
        idlePresentation.CancelAll();
        controller.SetEnabled(value);
        worldComposition.SetEnabled(value);
        ownersByScreen.Clear();
        ReleaseBorrowedDangerBorder(resetLoadAttempt: true);
    }

    internal void SetScreenDistortionEnabled(bool value)
    {
        if (disposed || screenDistortionEnabled == value)
            return;

        screenDistortionEnabled = value;
        // Do not leave a stale geometry transform in the final-composition adapter between
        // a GMCM save and the next owner snapshot; the color-only parameters rebuild next tick.
        worldComposition.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.TierStateObserved -= OnTierStateObserved;
        lifecycle.EventOwnerCoverageChanged -= OnEventOwnerCoverageChanged;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        resources.VisualResourcesInvalidating -= OnVisualResourcesInvalidating;
        resources.WorldResourcesReleasing -= OnWorldResourcesReleasing;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.Display.RenderingHud -= OnRenderingHud;
        helper.Events.Input.ButtonPressed -= OnButtonPressed;
        helper.Events.Input.MouseWheelScrolled -= OnMouseWheelScrolled;
        idlePresentation.CancelAll();
        worldComposition.Dispose();
        controller.ClearAll();
        ownersByScreen.Clear();
        ReleaseBorrowedDangerBorder(resetLoadAttempt: false);
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;

        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemDisabled:
                ClearAllOwnerState();
                break;
            case SanityStateEventKind.SystemEnabled:
                ClearAllOwnerState();
                break;
            case SanityStateEventKind.OwnerInvalidated:
                RemoveOwner(stateEvent.PlayerKey);
                break;
            case SanityStateEventKind.TierEntered:
            case SanityStateEventKind.TierExited:
            case SanityStateEventKind.WorldCleanup:
                // TierStateObserved publishes the final nested-tier snapshot.
                // Explicit owner-screen world boundaries below own visual cleanup; the generic
                // state-machine cleanup event must not erase another split-screen owner.
                break;
            default:
                LogOnce(
                    "visual.lifecycle.state-event-unsupported",
                    $"Sanity visual lifecycle ignored unsupported state event {stateEvent.Kind}.",
                    LogLevel.Warn
                );
                break;
        }
    }

    private void OnTierStateObserved(SanityTierOwnerStateSnapshot snapshot)
    {
        if (disposed || !enabled)
            return;

        foreach (var pair in ownersByScreen)
        {
            if (
                pair.Key == Context.ScreenId
                && Context.HasScreenId(pair.Key)
                &&
                string.Equals(
                    pair.Value.PlayerKey,
                    snapshot.PlayerKey,
                    StringComparison.Ordinal
                )
            )
            {
                SubmitSnapshot(pair.Key, snapshot);
            }
        }
    }

    private void OnEventOwnerCoverageChanged(
        SanityEventOwnerCoverageChanged change
    )
    {
        if (disposed)
            return;

        // A coverage edge changes effective Sanity without changing base revision. Clear the
        // exact screen receipt first so the next observation cannot conflict with stale visuals.
        RemoveScreen(change.Key.ScreenId);
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;

        if (boundary == SanityWorldBoundary.Warp)
        {
            RemoveScreen(Context.ScreenId);
            return;
        }

        ClearAllOwnerState();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            ClearAllOwnerState();
    }

    private void OnVisualResourcesInvalidating()
    {
        if (disposed)
            return;

        ClearAllOwnerState();
        ReleaseBorrowedDangerBorder(resetLoadAttempt: true);
    }

    private void OnWorldResourcesReleasing(SanityResourceReleaseReason reason)
    {
        if (disposed)
            return;

        if (reason == SanityResourceReleaseReason.Dispose)
        {
            Dispose();
            return;
        }

        ClearAllOwnerState();
        ReleaseBorrowedDangerBorder(resetLoadAttempt: true);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (e.IsMultipleOf(60))
            CleanupInvalidScreens();
        if (!enabled || !lifecycle.IsEnabled)
        {
            ClearAllOwnerState();
            return;
        }
        if (!TryBindCurrentOwner(out var screenId, out var binding))
        {
            RemoveScreen(Context.ScreenId);
            return;
        }

        var key = new SanityVisualOwnerKey(
            binding.PlayerKey,
            screenId,
            binding.SessionId
        );
        if (
            !lifecycle.TryGetTierState(binding.PlayerKey, out var tierSnapshot)
            || tierSnapshot is null
            || !tierSnapshot.IsAvailable
            || tierSnapshot.Maximum <= 0d
        )
        {
            ClearScreenPresentationState(screenId);
            return;
        }
        if (
            !controller.TryGetSnapshot(key, out var snapshot)
            || snapshot.Revision != tierSnapshot.Revision
            || !snapshot.Ratio.Equals(
                tierSnapshot.Current / tierSnapshot.Maximum
            )
        )
        {
            SubmitSnapshot(screenId, tierSnapshot);
        }

        if (!controller.TryGetSnapshot(key, out snapshot))
            return;

        var playerKey = binding.PlayerKey;
        var effectiveOverride = effectiveSanity.TryGetEffectiveRatio(
            new SanityEffectiveOverlayKey(playerKey, screenId, lifecycle.SessionId),
            out _,
            out _
        );
        controller.UpdateEffectiveSanityOverride(key, effectiveOverride);
        controller.UpdateViewport(
            key,
            Game1.uiViewport.Width,
            Game1.uiViewport.Height
        );

        if (!controller.TryGetSnapshot(key, out snapshot))
            return;

        if (
            SanityWorldCompositionPlanner.TryCreate(
                snapshot,
                Game1.currentGameTime.TotalGameTime.TotalSeconds,
                binding.StableSeed,
                screenDistortionEnabled,
                out var composition
            )
        )
        {
            worldComposition.UpdateScreen(composition);
        }
        else
        {
            worldComposition.RemoveScreen(screenId);
        }

        ObserveCurrentOwnerIdle(key, snapshot);

        if ((snapshot.RenderableLayers & SanityVisualLayerMask.DangerBorder) != 0)
        {
            TryLoadDangerBorder();
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!disposed)
            ClearAllOwnerState();
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        CancelCurrentScreenIdleForRealInput();
    }

    private void OnMouseWheelScrolled(
        object? sender,
        MouseWheelScrolledEventArgs e
    )
    {
        CancelCurrentScreenIdleForRealInput();
    }

    private void CancelCurrentScreenIdleForRealInput()
    {
        if (disposed || !enabled)
            return;

        var screenId = Context.ScreenId;
        if (!ownersByScreen.TryGetValue(screenId, out var binding))
            return;
        var key = new SanityVisualOwnerKey(
            binding.PlayerKey,
            screenId,
            binding.SessionId
        );
        if (idlePresentation.CancelScreen(screenId, key))
            controller.ResetIdle(key, "idle.interrupted.real-input");
    }

    private void OnRenderingHud(object? sender, RenderingHudEventArgs e)
    {
        if (
            disposed
            || !enabled
            || !lifecycle.IsEnabled
            || !Context.IsWorldReady
            || Game1.activeClickableMenu is not null
            || Game1.eventUp
            || dangerBorderTexture is null
            || dangerBorderPreview is null
        )
        {
            return;
        }

        var screenId = Context.ScreenId;
        if (!ownersByScreen.TryGetValue(screenId, out var binding))
            return;
        var key = new SanityVisualOwnerKey(
            binding.PlayerKey,
            screenId,
            binding.SessionId
        );
        if (
            !controller.TryGetSnapshot(key, out var snapshot)
            || (snapshot.RenderableLayers & SanityVisualLayerMask.DangerBorder) == 0
            || snapshot.ViewportWidth != Game1.uiViewport.Width
            || snapshot.ViewportHeight != Game1.uiViewport.Height
        )
        {
            return;
        }

        try
        {
            DrawDangerBorder(
                e.SpriteBatch,
                dangerBorderTexture.Texture,
                dangerBorderPreview,
                snapshot.DangerBorderLayout
            );
        }
        catch (Exception exception)
        {
            RemoveScreen(screenId);
            ReleaseBorrowedDangerBorder(resetLoadAttempt: false);
            LogOnce(
                "visual.danger-border-draw-failed",
                $"Sanity danger border draw failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void DrawDangerBorder(
        SpriteBatch spriteBatch,
        Texture2D texture,
        SanityVisualPreviewDefinition preview,
        SanityNineSliceLayout layout
    )
    {
        var tint = Color.White * SanityVisualController.DangerBorderOpacity;
        for (var index = 0; index < layout.Slices.Count; index++)
            DrawSlice(spriteBatch, texture, preview, layout.Slices[index], tint);
    }

    private static void DrawSlice(
        SpriteBatch spriteBatch,
        Texture2D texture,
        SanityVisualPreviewDefinition preview,
        SanityVisualSlice slice,
        Color tint
    )
    {
        if (
            slice.Source.Width <= 0
            || slice.Source.Height <= 0
            || slice.Destination.Width <= 0
            || slice.Destination.Height <= 0
        )
        {
            return;
        }

        var source = new Rectangle(
            preview.SourceRectangle.X + slice.Source.X,
            preview.SourceRectangle.Y + slice.Source.Y,
            slice.Source.Width,
            slice.Source.Height
        );
        var destination = new Rectangle(
            slice.Destination.X,
            slice.Destination.Y,
            slice.Destination.Width,
            slice.Destination.Height
        );
        spriteBatch.Draw(texture, destination, source, tint);
    }

    private void SubmitSnapshot(
        int screenId,
        SanityTierOwnerStateSnapshot snapshot
    )
    {
        if (
            !snapshot.IsAvailable
            || snapshot.Maximum <= 0d
            || !SanityProtocol.IsValidSessionId(lifecycle.SessionId)
        )
        {
            ClearScreenPresentationState(screenId);
            return;
        }

        var key = new SanityVisualOwnerKey(
            snapshot.PlayerKey,
            screenId,
            lifecycle.SessionId
        );
        var effectiveOverride = effectiveSanity.TryGetEffectiveRatio(
            new SanityEffectiveOverlayKey(snapshot.PlayerKey, screenId, lifecycle.SessionId),
            out _,
            out _
        );
        if (controller.TryGetSnapshot(key, out _))
        {
            controller.UpdateViewport(
                key,
                Game1.uiViewport.Width,
                Game1.uiViewport.Height
            );
            controller.UpdateEffectiveSanityOverride(key, effectiveOverride);
        }

        var mutation = controller.Observe(
            new SanityVisualObservation(
                key,
                snapshot.Revision,
                snapshot.Current,
                snapshot.Maximum,
                snapshot.ActiveTierIds,
                effectiveOverride,
                Game1.uiViewport.Width,
                Game1.uiViewport.Height
            )
        );
        if (
            mutation.Status is SanityVisualMutationStatus.Invalid
                or SanityVisualMutationStatus.CapacityExceeded
        )
        {
            LogOnce(
                mutation.Reason,
                $"Sanity visual observation failed closed (player={snapshot.PlayerKey}, screen={screenId}, reason={mutation.Reason}).",
                LogLevel.Error
            );
        }
    }

    private void ObserveCurrentOwnerIdle(
        SanityVisualOwnerKey key,
        SanityVisualOwnerSnapshot snapshot
    )
    {
        var player = Game1.player;
        if (player is null)
            return;

        var sprite = player.FarmerSprite;
        var ownPresentationActive = idlePresentation.IsOwned(
            key.ScreenId,
            key,
            player
        );
        var isBusy = ownPresentationActive
            ? IsBusyOutsideOwnedPresentation(player)
            : player.IsBusyDoingSomething();
        var interactionWorldReady =
            Context.IsWorldReady
            && !Game1.paused
            && (Game1.game1 is null || Game1.game1.IsActiveNoOverlay);
        var observation = new SanityIdleObservation(
            interactionWorldReady,
            player.IsLocalPlayer,
            player.CanMove,
            isBusy,
            player.isMoving(),
            player.movementDirections.Count > 0,
            player.UsingTool,
            player.temporarilyInvincible,
            sprite.PauseForSingleAnimation,
            sprite.IsPlayingBasicAnimation(player.FacingDirection, player.IsCarrying()),
            Game1.activeClickableMenu is not null,
            Game1.CurrentEvent is not null || Game1.eventUp,
            Game1.isWarping,
            ownPresentationActive
        );
        var elapsed = Game1.currentGameTime.ElapsedGameTime;
        var idle = controller.ObserveIdle(key, elapsed, observation);
        if (!idle.ThresholdReached || !snapshot.IdleEligible)
        {
            if (ownPresentationActive)
                idlePresentation.CancelScreen(key.ScreenId, key);
            return;
        }
        if (
            !ownPresentationActive
            && !idlePresentation.TryStart(key.ScreenId, key, player)
        )
        {
            controller.ResetIdle(key, "idle.presentation-start-rejected");
        }
    }

    private static bool IsBusyOutsideOwnedPresentation(Farmer player)
    {
        // Farmer.IsBusyDoingSomething includes PauseForSingleAnimation, which is expected while
        // our token loops. Keep every other public high-priority predicate explicit.
        return Game1.eventUp
            || Game1.paused
            || (Game1.game1 is not null && !Game1.game1.IsActiveNoOverlay)
            || Game1.fadeToBlack
            || Game1.currentMinigame is not null
            || Game1.activeClickableMenu is not null
            || Game1.isWarping
            || Game1.killScreen
            || player.UsingTool
            || !player.CanMove;
    }

    private bool TryBindCurrentOwner(
        out int screenId,
        out OwnerBinding binding
    )
    {
        screenId = Context.ScreenId;
        binding = null!;
        var player = Game1.player;
        var location = Game1.currentLocation;
        var sessionId = lifecycle.SessionId;
        if (
            !Context.IsWorldReady
            || !Context.HasScreenId(screenId)
            || player is null
            || !player.IsLocalPlayer
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
            || !SanityProtocol.IsValidSessionId(sessionId)
        )
        {
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (!SanityPlayerKey.IsCanonical(playerKey))
            return false;

        if (
            ownersByScreen.TryGetValue(screenId, out var cached)
            && string.Equals(cached.PlayerKey, playerKey, StringComparison.Ordinal)
            && string.Equals(cached.SessionId, sessionId, StringComparison.Ordinal)
            && ReferenceEquals(cached.LocationReference, location)
            && string.Equals(
                cached.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            binding = cached;
            return true;
        }

        RemoveScreen(screenId);
        binding = new OwnerBinding(
            playerKey,
            sessionId,
            location,
            location.NameOrUniqueName,
            SanityWorldCompositionPlanner.CreateStableSeed(
                playerKey,
                screenId,
                sessionId
            )
        );
        ownersByScreen[screenId] = binding;
        return true;
    }

    private void TryLoadDangerBorder()
    {
        if (
            dangerBorderLoadAttempted
            || dangerBorderTexture is not null
            || disposed
        )
        {
            return;
        }

        dangerBorderLoadAttempted = true;
        SanitySlotResourceResult result;
        try
        {
            result = resources.LoadVisualSlot(DangerBorderProfileId, 0);
        }
        catch (Exception exception)
        {
            LogOnce(
                "visual.danger-border-load-threw",
                $"Sanity danger border load failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return;
        }
        if (
            !result.Success
            || result.PhysicalResource is not XnaSanityTextureResource texture
            || result.VisualPreview is not { } preview
            || !IsDangerBorderContract(preview, texture.Texture)
        )
        {
            LogOnce(
                "visual.danger-border-resource-unavailable",
                $"Sanity danger border failed closed (code={result.Diagnostic.Code}, reason={result.Diagnostic.Reason}).",
                LogLevel.Error
            );
            return;
        }

        dangerBorderTexture = texture;
        dangerBorderPreview = preview;
        if (preview.IsPlaceholder)
        {
            LogOnce(
                "visual.danger-border-placeholder-active",
                "Sanity danger border is using the explicit DEV-PLACEHOLDER nine-slice contract; it is not release-eligible.",
                LogLevel.Warn
            );
        }
    }

    private static bool IsDangerBorderContract(
        SanityVisualPreviewDefinition preview,
        Texture2D texture
    )
    {
        return preview.Kind == SanityVisualPreviewKind.NineSliceOverlay
            && preview.OwnerLocalOnly
            && preview.SourceRectangle.Width
                == SanityVisualController.DangerBorderSourceWidth
            && preview.SourceRectangle.Height
                == SanityVisualController.DangerBorderSourceHeight
            && preview.SliceSourcePx is { } slice
            && slice.X == SanityVisualController.DangerBorderSliceLeft
            && slice.Y == SanityVisualController.DangerBorderSliceTop
            && slice.Width == SanityVisualController.DangerBorderSliceRight
            && slice.Height == SanityVisualController.DangerBorderSliceBottom
            && texture.Width >= preview.SourceRectangle.X + preview.SourceRectangle.Width
            && texture.Height >= preview.SourceRectangle.Y + preview.SourceRectangle.Height;
    }

    private void CleanupInvalidScreens()
    {
        controller.ClearInvalidScreens(Context.HasScreenId);
        idlePresentation.CancelInvalidScreens(Context.HasScreenId);
        List<int>? invalid = null;
        foreach (var screenId in ownersByScreen.Keys)
        {
            if (Context.HasScreenId(screenId))
                continue;
            invalid ??= new List<int>();
            invalid.Add(screenId);
        }

        if (invalid is null)
            return;
        foreach (var screenId in invalid)
        {
            worldComposition.RemoveScreen(screenId);
            ownersByScreen.Remove(screenId);
        }
    }

    private void RemoveOwner(string playerKey)
    {
        idlePresentation.CancelOwner(playerKey);
        controller.ClearOwner(playerKey);
        List<int>? screens = null;
        foreach (var pair in ownersByScreen)
        {
            if (!string.Equals(pair.Value.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            screens ??= new List<int>();
            screens.Add(pair.Key);
        }

        if (screens is null)
            return;
        foreach (var screenId in screens)
        {
            worldComposition.RemoveScreen(screenId);
            ownersByScreen.Remove(screenId);
        }
    }

    private void RemoveScreen(int screenId)
    {
        ClearScreenPresentationState(screenId);
        ownersByScreen.Remove(screenId);
    }

    private void ClearScreenPresentationState(int screenId)
    {
        idlePresentation.CancelScreen(screenId);
        worldComposition.RemoveScreen(screenId);
        controller.ClearScreen(screenId);
    }

    private void ClearAllOwnerState()
    {
        idlePresentation.CancelAll();
        worldComposition.Clear();
        controller.ClearAll();
        ownersByScreen.Clear();
    }

    private void ReleaseBorrowedDangerBorder(bool resetLoadAttempt)
    {
        // The stage-03 loader owns and disposes the texture after its release event. This service
        // only drops borrowed references before that point.
        dangerBorderTexture = null;
        dangerBorderPreview = null;
        if (resetLoadAttempt)
            dangerBorderLoadAttempted = false;
    }

    private void LogCapabilityMatrix()
    {
        var renderer = worldComposition.Capability;
        monitor.Log(
            $"Sanity visual capabilities: renderer={renderer.Status}/{worldComposition.EffectiveReason}/{renderer.GameVersion}/{renderer.FrameworkVersion}/{renderer.Backend}; low-saturation={SanityVisualCapabilityCatalog.LowSaturation.Status}/{SanityVisualCapabilityCatalog.LowSaturation.Reason}; shake={SanityVisualCapabilityCatalog.ViewShake.Status}/{SanityVisualCapabilityCatalog.ViewShake.Reason}; danger-border={SanityVisualCapabilityCatalog.DangerBorder.Status}/{SanityVisualCapabilityCatalog.DangerBorder.Reason}; grayscale={SanityVisualCapabilityCatalog.Grayscale.Status}/{SanityVisualCapabilityCatalog.Grayscale.Reason}; idle-presentation={SanityVisualCapabilityCatalog.IdlePresentation.Status}/{SanityVisualCapabilityCatalog.IdlePresentation.Reason}.",
            renderer.IsAvailable ? LogLevel.Debug : LogLevel.Error
        );
    }

    private void LogOnce(string code, string message, LogLevel level)
    {
        if (
            disposed
            || loggedDiagnostics.Count >= MaximumLoggedDiagnostics
            || !loggedDiagnostics.Add(code)
        )
        {
            return;
        }
        monitor.Log(message, level);
    }

    private sealed record OwnerBinding(
        string PlayerKey,
        string SessionId,
        GameLocation LocationReference,
        string LocationNameOrUniqueName,
        uint StableSeed
    );
}
