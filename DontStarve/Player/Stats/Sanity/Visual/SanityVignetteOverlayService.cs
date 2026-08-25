#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Events;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Owner-local HUD vignette coordinator. It intentionally stays separate from the world-only
/// low-Sanity compositor so disabling that filter can still leave the configured basic vignette.
/// </summary>
internal sealed class SanityVignetteOverlayService : IDisposable
{
    private const int MaximumOwners = SanityVisualController.MaximumOwners;
    private const int MaximumLoggedDiagnostics = 16;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly ISanityEffectiveSanityProvider effectiveSanity;
    private readonly SanityVignetteRenderer renderer;
    private readonly Dictionary<int, OwnerState> ownersByScreen = new();
    private readonly HashSet<string> loggedDiagnostics =
        new(StringComparer.Ordinal);
    private bool lowSanityFilterEnabled;
    private bool vignetteEnabled;
    private bool rendererLoadAttempted;
    private bool disposed;

    internal SanityVignetteOverlayService(
        IModHelper helper,
        IMonitor monitor,
        SanitySystemLifecycleCoordinator lifecycle,
        ISanityEffectiveSanityProvider effectiveSanity,
        bool lowSanityFilterEnabled,
        bool vignetteEnabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.effectiveSanity = effectiveSanity
            ?? throw new ArgumentNullException(nameof(effectiveSanity));
        this.lowSanityFilterEnabled = lowSanityFilterEnabled;
        this.vignetteEnabled = vignetteEnabled;
        renderer = new SanityVignetteRenderer(helper);

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Display.RenderedHud += OnRenderedHud;
    }

    internal void SetLowSanityFilterEnabled(bool value)
    {
        if (disposed || lowSanityFilterEnabled == value)
            return;

        lowSanityFilterEnabled = value;
        ownersByScreen.Clear();
    }

    internal void SetVignetteEnabled(bool value)
    {
        if (disposed || vignetteEnabled == value)
            return;

        vignetteEnabled = value;
        ownersByScreen.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        helper.Events.Display.RenderedHud -= OnRenderedHud;
        ownersByScreen.Clear();
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;

        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemDisabled:
            case SanityStateEventKind.SystemEnabled:
                ownersByScreen.Clear();
                break;
            case SanityStateEventKind.OwnerInvalidated:
                RemoveOwner(stateEvent.PlayerKey);
                break;
        }
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;

        if (boundary == SanityWorldBoundary.Warp)
        {
            ownersByScreen.Remove(Context.ScreenId);
            return;
        }

        ownersByScreen.Clear();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            ownersByScreen.Clear();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (e.IsMultipleOf(60))
            CleanupInvalidScreens();

        if (
            !lifecycle.IsEnabled
            || (!lowSanityFilterEnabled && !vignetteEnabled)
        )
        {
            ownersByScreen.Clear();
            return;
        }

        if (!TryRefreshCurrentOwner(out var screenId))
            ownersByScreen.Remove(screenId);

        // Texture loading must happen outside the HUD draw callback. This also covers a
        // vignette toggle turned on after SaveLoaded without putting I/O on the render path.
        if (ownersByScreen.Count > 0)
            _ = TryLoadRenderer();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!disposed && (lowSanityFilterEnabled || vignetteEnabled))
            _ = TryLoadRenderer();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!disposed)
            ownersByScreen.Clear();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        if (!disposed)
            ownersByScreen.Clear();
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (
            disposed
            || !lifecycle.IsEnabled
            || !Context.IsWorldReady
            || Game1.eventUp
        )
        {
            return;
        }

        var screenId = Context.ScreenId;
        if (
            !ownersByScreen.TryGetValue(screenId, out var state)
            || !Context.HasScreenId(screenId)
            || !string.Equals(state.SessionId, lifecycle.SessionId, StringComparison.Ordinal)
        )
        {
            return;
        }

        var mode = SanityVignettePolicy.Resolve(
            lowSanityFilterEnabled && !state.EffectiveSanityOverrideActive,
            vignetteEnabled,
            state.SanityRatio
        );
        if (mode == SanityVignetteMode.Hidden || !renderer.IsLoaded)
            return;

        try
        {
            renderer.Draw(
                e.SpriteBatch,
                e.SpriteBatch.GraphicsDevice.Viewport.Bounds,
                mode,
                (long)Game1.currentGameTime.TotalGameTime.TotalMilliseconds
            );
        }
        catch (Exception exception)
        {
            ownersByScreen.Remove(screenId);
            LogOnce(
                "vignette.draw-failed",
                $"Sanity vignette draw failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private bool TryRefreshCurrentOwner(out int screenId)
    {
        screenId = Context.ScreenId;
        var player = Game1.player;
        var sessionId = lifecycle.SessionId;
        if (
            !Context.IsWorldReady
            || !Context.HasScreenId(screenId)
            || player is null
            || !player.IsLocalPlayer
            || !SanityProtocol.IsValidSessionId(sessionId)
        )
        {
            return false;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !lifecycle.TryGetTierState(playerKey, out var snapshot)
            || snapshot is null
            || !snapshot.IsAvailable
            || snapshot.Maximum <= 0d
        )
        {
            return false;
        }

        var ratio = snapshot.Current / snapshot.Maximum;
        if (!double.IsFinite(ratio) || ratio < 0d || ratio > 1d)
            return false;

        var effectiveSanityOverrideActive = effectiveSanity.TryGetEffectiveRatio(
            new SanityEffectiveOverlayKey(playerKey, screenId, sessionId),
            out _,
            out _
        );
        if (
            SanityVignettePolicy.Resolve(
                lowSanityFilterEnabled && !effectiveSanityOverrideActive,
                vignetteEnabled,
                ratio
            ) == SanityVignetteMode.Hidden
        )
        {
            ownersByScreen.Remove(screenId);
            return true;
        }

        if (
            !ownersByScreen.ContainsKey(screenId)
            && ownersByScreen.Count >= MaximumOwners
        )
        {
            return false;
        }

        ownersByScreen[screenId] = new OwnerState(
            playerKey,
            sessionId,
            ratio,
            effectiveSanityOverrideActive
        );
        return true;
    }

    private bool TryLoadRenderer()
    {
        if (renderer.IsLoaded)
            return true;
        if (rendererLoadAttempted)
            return false;

        rendererLoadAttempted = true;
        try
        {
            renderer.Load();
            return true;
        }
        catch (Exception exception)
        {
            LogOnce(
                "vignette.resource-load-failed",
                $"Sanity vignette resources failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return false;
        }
    }

    private void CleanupInvalidScreens()
    {
        List<int>? removals = null;
        foreach (var screenId in ownersByScreen.Keys)
        {
            if (Context.HasScreenId(screenId))
                continue;
            removals ??= new List<int>();
            removals.Add(screenId);
        }

        if (removals is null)
            return;
        for (var index = 0; index < removals.Count; index++)
            ownersByScreen.Remove(removals[index]);
    }

    private void RemoveOwner(string playerKey)
    {
        List<int>? removals = null;
        foreach (var pair in ownersByScreen)
        {
            if (!string.Equals(pair.Value.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<int>();
            removals.Add(pair.Key);
        }

        if (removals is null)
            return;
        for (var index = 0; index < removals.Count; index++)
            ownersByScreen.Remove(removals[index]);
    }

    private void LogOnce(string code, string message, LogLevel level)
    {
        if (
            loggedDiagnostics.Count >= MaximumLoggedDiagnostics
            || !loggedDiagnostics.Add(code)
        )
        {
            return;
        }
        monitor.Log(message, level);
    }

    private sealed record OwnerState(
        string PlayerKey,
        string SessionId,
        double SanityRatio,
        bool EffectiveSanityOverrideActive
    );
}
