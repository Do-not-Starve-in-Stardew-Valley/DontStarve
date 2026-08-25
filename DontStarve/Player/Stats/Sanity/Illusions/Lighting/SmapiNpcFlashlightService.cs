#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Maintains local native-lightmap flashlights for non-monster NPCs in the current fully-dark
/// natural-darkness scene. It owns only its two LightSource entries per NPC and never touches map
/// tiles or Stardew's unrelated light entries.
/// </summary>
internal sealed class SmapiNpcFlashlightService : IDisposable
{
    private const int RefreshCadenceTicks = 3;
    private const int CleanupCadenceTicks = 60;
    private const int MaximumTrackedScreens =
        EnvironmentLightProductionContract.MaximumScreens;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Func<int, NaturalDarknessSceneState> sceneStateProvider;
    private readonly string lightIdPrefix;
    private readonly Dictionary<int, ScreenFlashlightState> screensById = new();
    private readonly List<int> invalidScreenIds = new();
    private bool enabled;
    private bool disposed;
    private bool providerFailureLogged;
    private bool runtimeFailureLogged;
    private long nextLightSequence;

    internal SmapiNpcFlashlightService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        bool enabled,
        Func<int, NaturalDarknessSceneState> sceneStateProvider
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod id is required.", nameof(modId));
        this.sceneStateProvider = sceneStateProvider
            ?? throw new ArgumentNullException(nameof(sceneStateProvider));
        lightIdPrefix = string.Concat(modId, ".NpcFlashlight.");
        this.enabled = enabled;

        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    internal void SetEnabled(bool value)
    {
        if (disposed || enabled == value)
            return;

        enabled = value;
        if (!enabled)
            ClearAllLights(releaseConeTexture: true);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.Player.Warped -= OnWarped;
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        ClearAllLights(releaseConeTexture: true);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _ = sender;
        if (disposed)
            return;

        var screenId = Context.ScreenId;
        if (screenId < 0 || !Context.HasScreenId(screenId))
            return;

        if (
            !enabled
            || !Context.IsWorldReady
            || !IsCurrentSceneFullyDark(screenId)
        )
        {
            ClearScreenLights(screenId);
        }
        else if (e.IsMultipleOf(RefreshCadenceTicks))
        {
            try
            {
                RefreshScreenLights(screenId, Game1.currentLocation);
            }
            catch (Exception exception)
            {
                ClearScreenLights(screenId);
                if (!runtimeFailureLogged)
                {
                    runtimeFailureLogged = true;
                    monitor.Log(
                        $"NPC flashlight refresh failed closed ({exception.GetType().Name}: {exception.Message}).",
                        LogLevel.Warn
                    );
                }
            }
        }

        if (e.IsMultipleOf(CleanupCadenceTicks))
            CleanupInvalidScreens();
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        _ = sender;
        if (e.IsLocalPlayer)
            ClearScreenLights(Context.ScreenId);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _ = sender;
        _ = e;
        ClearAllLights(releaseConeTexture: false);
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        _ = sender;
        _ = e;
        ClearAllLights(releaseConeTexture: false);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _ = sender;
        _ = e;
        ClearAllLights(releaseConeTexture: true);
    }

    private bool IsCurrentSceneFullyDark(int screenId)
    {
        try
        {
            var sceneState = sceneStateProvider(screenId);
            return NpcFlashlightPolicy.ShouldEmit(
                new NpcFlashlightSceneInput(
                    sceneState.Phase,
                    sceneState.LightmapWhiteBlend
                )
            );
        }
        catch (Exception exception)
        {
            if (!providerFailureLogged)
            {
                providerFailureLogged = true;
                monitor.Log(
                    $"NPC flashlight darkness-state provider failed closed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            return false;
        }
    }

    private void RefreshScreenLights(int screenId, GameLocation? location)
    {
        if (location is null)
        {
            ClearScreenLights(screenId);
            return;
        }

        var screen = GetOrCreateScreen(screenId);
        if (screen is null)
            return;

        if (screen.LightsByNpc.Count > 0 || location.characters.Count > 0)
            NpcFlashlightConeLightSource.EnsureCachedTexture(monitor);

        var refreshRevision = screen.BeginRefresh();
        foreach (var npc in location.characters)
        {
            if (
                !NpcFlashlightPolicy.IsEligibleNpc(
                    npc.Name,
                    npc is Monster
                )
            )
            {
                continue;
            }

            var standingPixel = npc.StandingPixel;
            var position = new Vector2(standingPixel.X, standingPixel.Y);
            if (!screen.LightsByNpc.TryGetValue(npc, out var lights))
            {
                lights = TryCreateLights(screenId, location, position, npc.FacingDirection);
                if (lights is null)
                    continue;

                screen.LightsByNpc.Add(npc, lights);
            }

            if (!EnsureRegistered(lights))
                continue;

            lights.Body.position.Value = position;
            lights.Cone.Update(position, npc.FacingDirection);
            lights.LastSeenRefreshRevision = refreshRevision;
        }

        screen.RemoveUnseenLights(refreshRevision);
    }

    private ScreenFlashlightState? GetOrCreateScreen(int screenId)
    {
        if (screensById.TryGetValue(screenId, out var existing))
            return existing;

        if (screensById.Count >= MaximumTrackedScreens)
            return null;

        var screen = new ScreenFlashlightState(RemoveLights);
        screensById.Add(screenId, screen);
        return screen;
    }

    private NpcFlashlightLights? TryCreateLights(
        int screenId,
        GameLocation location,
        Vector2 position,
        int facingDirection
    )
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var idRoot = CreateLightIdRoot(screenId);
            var bodyId = string.Concat(idRoot, ".body");
            var coneId = string.Concat(idRoot, ".cone");
            if (
                Game1.currentLightSources.ContainsKey(bodyId)
                || Game1.currentLightSources.ContainsKey(coneId)
            )
            {
                continue;
            }

            var body = new LightSource(
                bodyId,
                LightSource.lantern,
                position,
                NpcFlashlightPolicy.BodyLightRadiusTiles,
                Color.Black,
                LightSource.LightContext.None,
                0L,
                location.NameOrUniqueName
            );
            var cone = new NpcFlashlightConeLightSource(
                coneId,
                position,
                facingDirection,
                location.NameOrUniqueName
            );
            var lights = new NpcFlashlightLights(bodyId, coneId, body, cone);
            if (EnsureRegistered(lights))
                return lights;

            RemoveLights(lights);
        }

        monitor.Log(
            "NPC flashlight ids collided repeatedly; the affected NPC flashlight was skipped.",
            LogLevel.Warn
        );
        return null;
    }

    private string CreateLightIdRoot(int screenId)
    {
        nextLightSequence++;
        return string.Concat(
            lightIdPrefix,
            screenId.ToString(CultureInfo.InvariantCulture),
            ".",
            nextLightSequence.ToString(CultureInfo.InvariantCulture)
        );
    }

    private static bool EnsureRegistered(NpcFlashlightLights lights)
    {
        return EnsureRegistered(lights.BodyId, lights.Body)
            && EnsureRegistered(lights.ConeId, lights.Cone);
    }

    private static bool EnsureRegistered(string id, LightSource source)
    {
        if (Game1.currentLightSources.TryGetValue(id, out var existing))
            return ReferenceEquals(existing, source);

        Game1.currentLightSources.Add(id, source);
        return true;
    }

    private void ClearScreenLights(int screenId)
    {
        if (!screensById.Remove(screenId, out var screen))
            return;

        screen.RemoveAllLights();
    }

    private void ClearAllLights(bool releaseConeTexture)
    {
        foreach (var screen in screensById.Values)
            screen.RemoveAllLights();

        screensById.Clear();
        if (releaseConeTexture)
            NpcFlashlightConeLightSource.ReleaseCachedTexture();
    }

    private static void RemoveLights(NpcFlashlightLights lights)
    {
        RemoveOwnedLight(lights.BodyId, lights.Body);
        RemoveOwnedLight(lights.ConeId, lights.Cone);
    }

    private static void RemoveOwnedLight(string id, LightSource source)
    {
        if (
            Game1.currentLightSources.TryGetValue(id, out var existing)
            && ReferenceEquals(existing, source)
        )
        {
            Game1.currentLightSources.Remove(id);
        }
    }

    private void CleanupInvalidScreens()
    {
        invalidScreenIds.Clear();
        foreach (var screenId in screensById.Keys)
        {
            if (!Context.HasScreenId(screenId))
                invalidScreenIds.Add(screenId);
        }

        foreach (var screenId in invalidScreenIds)
            ClearScreenLights(screenId);
    }

    private sealed class ScreenFlashlightState
    {
        private readonly Action<NpcFlashlightLights> removeLights;
        private readonly List<NPC> staleNpcs = new();
        private int refreshRevision;

        internal ScreenFlashlightState(Action<NpcFlashlightLights> removeLights)
        {
            this.removeLights = removeLights;
        }

        internal Dictionary<NPC, NpcFlashlightLights> LightsByNpc { get; } =
            new(ReferenceEqualityComparer.Instance);

        internal int BeginRefresh()
        {
            if (refreshRevision == int.MaxValue)
            {
                refreshRevision = 0;
                foreach (var lights in LightsByNpc.Values)
                    lights.LastSeenRefreshRevision = 0;
            }

            refreshRevision++;
            return refreshRevision;
        }

        internal void RemoveUnseenLights(int currentRefreshRevision)
        {
            staleNpcs.Clear();
            foreach (var pair in LightsByNpc)
            {
                if (pair.Value.LastSeenRefreshRevision != currentRefreshRevision)
                    staleNpcs.Add(pair.Key);
            }

            foreach (var npc in staleNpcs)
            {
                if (LightsByNpc.Remove(npc, out var lights))
                    removeLights(lights);
            }
        }

        internal void RemoveAllLights()
        {
            foreach (var lights in LightsByNpc.Values)
                removeLights(lights);

            LightsByNpc.Clear();
            staleNpcs.Clear();
        }
    }

    private sealed class NpcFlashlightLights
    {
        internal NpcFlashlightLights(
            string bodyId,
            string coneId,
            LightSource body,
            NpcFlashlightConeLightSource cone
        )
        {
            BodyId = bodyId;
            ConeId = coneId;
            Body = body;
            Cone = cone;
        }

        internal string BodyId { get; }

        internal string ConeId { get; }

        internal LightSource Body { get; }

        internal NpcFlashlightConeLightSource Cone { get; }

        internal int LastSeenRefreshRevision { get; set; }
    }
}
