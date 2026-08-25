#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Selects a location-specific native-light plan and advances it on a real-time clock. The patch
/// changes only Stardew's lightmap input for the duration of DrawLighting; it never creates a
/// second lightmap or mutates Stardew's local-light collections. A separate draw-only patch may
/// suppress the explicitly identified native MineShaft wall sconces under the same scene state.
/// </summary>
internal sealed class SmapiNaturalDarknessLightmapService : IDisposable
{
    private const int MaximumTrackedScreens =
        EnvironmentLightProductionContract.MaximumScreens;
    private const int CleanupCadenceTicks = 60;
    private const int MaximumLoggedReasons = 8;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Harmony harmony;
    private readonly EnvironmentLightLocationRuleCatalog locationRules;
    private readonly DarknessAttackLocationAuthorizationPolicy
        darknessAttackLocationAuthorization;
    private readonly Func<int, double> progressProvider;
    private readonly Func<int, NaturalDarknessSceneState> sceneStateProvider;
    private readonly Dictionary<int, ScreenTransition> transitionsByScreen = new();
    private readonly Dictionary<int, ScreenScene> scenesByScreen = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool enabled;
    private bool patchInstalled;
    private bool wallSconcePatchInstalled;
    private bool disposed;

    internal SmapiNaturalDarknessLightmapService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        bool enabled,
        EnvironmentLightLocationRuleCatalog locationRules,
        DarknessAttackLocationAuthorizationPolicy darknessAttackLocationAuthorization
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.locationRules = locationRules
            ?? throw new ArgumentNullException(nameof(locationRules));
        this.darknessAttackLocationAuthorization = darknessAttackLocationAuthorization
            ?? throw new ArgumentNullException(nameof(darknessAttackLocationAuthorization));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A Harmony owner id is required.", nameof(modId));

        this.enabled = enabled;
        harmony = new Harmony(string.Concat(modId, ".Sanity4.NaturalDarkness"));
        progressProvider = GetProgressForScreen;
        sceneStateProvider = GetSceneStateForScreen;
        patchInstalled = NaturalDarknessLightmapPatch.TryInstall(
            harmony,
            monitor,
            progressProvider
        );
        if (patchInstalled)
        {
            wallSconcePatchInstalled = MineWallSconceRenderPatch.TryInstall(
                harmony,
                monitor,
                sceneStateProvider
            );
        }

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
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
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
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
        if (wallSconcePatchInstalled)
        {
            MineWallSconceRenderPatch.Uninstall(
                harmony,
                monitor,
                sceneStateProvider
            );
            wallSconcePatchInstalled = false;
        }
        if (patchInstalled)
        {
            NaturalDarknessLightmapPatch.Uninstall(
                harmony,
                monitor,
                progressProvider
            );
            patchInstalled = false;
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _ = sender;
        if (disposed || !patchInstalled)
            return;

        var screenId = Context.ScreenId;
        if (screenId < 0 || !Context.HasScreenId(screenId))
            return;

        var plan = enabled
            ? ResolveScenePlan(screenId)
            : NaturalDarknessScenePlan.None;
        if (!plan.IsActive)
        {
            // Warped can arrive while Stardew still reports isWarping. Keep a completed
            // destination state until native warping ends, but never draw it during the warp.
            if (!Game1.isWarping)
                transitionsByScreen.Remove(screenId);
        }
        else
        {
            SynchronizeScreenTransition(screenId, plan, Stopwatch.GetTimestamp());
        }

        if (e.IsMultipleOf(CleanupCadenceTicks))
            CleanupInvalidScreens();
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        _ = sender;
        if (!e.IsLocalPlayer)
            return;

        var screenId = Context.ScreenId;
        if (screenId < 0 || !Context.HasScreenId(screenId))
            return;

        var plan = enabled && patchInstalled
            ? ResolveScenePlan(screenId, e.NewLocation, ignoreWarping: true)
            : NaturalDarknessScenePlan.None;
        LogSceneDiagnostic("warp", screenId, e.NewLocation, plan);
        if (!plan.IsActive)
        {
            transitionsByScreen.Remove(screenId);
            return;
        }

        if (
            !transitionsByScreen.ContainsKey(screenId)
            && transitionsByScreen.Count >= MaximumTrackedScreens
        )
        {
            return;
        }

        // Reaching a map after its applicable threshold must show that map's current state
        // immediately. This applies to all-dark interiors, true night, and mine dusk alike.
        var timestamp = Stopwatch.GetTimestamp();
        var progress = NaturalDarknessTransitionPolicy.GetWarpArrivalProgress(plan);
        transitionsByScreen[screenId] = new ScreenTransition(
            plan,
            progress,
            timestamp
        );
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _ = sender;
        _ = e;
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        _ = sender;
        _ = e;
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _ = sender;
        _ = e;
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
    }

    private void SynchronizeScreenTransition(
        int screenId,
        NaturalDarknessScenePlan plan,
        long timestamp
    )
    {
        if (!transitionsByScreen.TryGetValue(screenId, out var previous))
        {
            if (transitionsByScreen.Count >= MaximumTrackedScreens)
                return;

            transitionsByScreen[screenId] = new ScreenTransition(plan, 0d, timestamp);
            LogCurrentLocationSceneDiagnostic("transition-start", screenId, plan);
            return;
        }

        if (previous.Plan == plan)
            return;

        var currentProgress = GetTransitionProgress(previous, timestamp);
        var startingProgress = plan.TargetWhiteBlend <= currentProgress
            ? plan.TargetWhiteBlend
            : currentProgress;
        transitionsByScreen[screenId] = new ScreenTransition(
            plan,
            startingProgress,
            timestamp
        );
        LogCurrentLocationSceneDiagnostic("transition-change", screenId, plan);
    }

    private void LogCurrentLocationSceneDiagnostic(
        string trigger,
        int screenId,
        NaturalDarknessScenePlan plan
    )
    {
        if (Game1.currentLocation is not null)
            LogSceneDiagnostic(trigger, screenId, Game1.currentLocation, plan);
    }

    private void LogSceneDiagnostic(
        string trigger,
        int screenId,
        GameLocation location,
        NaturalDarknessScenePlan plan
    )
    {
        try
        {
            var rule = SmapiEnvironmentLightSnapshotProvider.ResolveLocationRule(
                location,
                locationRules
            );
            var mine = location as MineShaft;
            var isSkullCavern = mine is not null && mine.getMineArea() == 121;
            var eventActive =
                Game1.eventUp
                || Game1.CurrentEvent is not null
                || location.currentEvent is not null;
            var festivalActive = Game1.CurrentEvent?.isFestival == true;
            var mineLevel = mine is null
                ? "n/a"
                : mine.mineLevel.ToString(CultureInfo.InvariantCulture);
            var difficulty = mine is null
                ? "n/a"
                : mine.GetAdditionalDifficulty().ToString(CultureInfo.InvariantCulture);

            // This is emitted only for a local warp or a plan transition, never from the per-tick
            // steady state. It makes a map/type/time mismatch visible without adding render-path logs.
            monitor.Log(
                $"Natural darkness scene diagnostic (trigger={trigger}, screen={screenId}, "
                    + $"location={location.NameOrUniqueName}, type={location.GetType().FullName ?? "unknown"}, "
                    + $"time={Game1.timeOfDay}, start-dark={Game1.getStartingToGetDarkTime(location)}, "
                    + $"truly-dark={Game1.getTrulyDarkTime(location)}, rule={rule.RuleId}@{rule.ContractVersion}, "
                    + $"profile={rule.NaturalDarknessProfile}, mine-level={mineLevel}, skull-cavern={isSkullCavern}, "
                    + $"difficulty={difficulty}, event={eventActive}, festival={festivalActive}, "
                    + $"minigame={Game1.currentMinigame is not null}, enabled={enabled}, "
                    + $"patch-installed={patchInstalled}, plan={plan.Phase}, "
                    + $"target={plan.TargetWhiteBlend.ToString("0.###", CultureInfo.InvariantCulture)}, "
                    + $"duration-seconds={plan.TransitionDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}).",
                LogLevel.Debug
            );
        }
        catch (Exception exception)
        {
            LogOnce(
                "natural-darkness.scene-diagnostic-failed",
                $"Natural darkness scene diagnostic failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }
    }

    private double GetProgressForScreen(int screenId)
    {
        var sceneState = GetSceneStateForScreen(screenId);
        if (!sceneState.IsActive)
            return 0d;

        return sceneState.LightmapWhiteBlend;
    }

    internal NaturalDarknessSceneState GetSceneStateForScreen(int screenId)
    {
        if (
            disposed
            || !patchInstalled
            || !enabled
            || screenId < 0
            || !Context.HasScreenId(screenId)
            || !Context.IsWorldReady
            || Game1.isWarping
            || Game1.eventUp
            || Game1.CurrentEvent is not null
            || Game1.currentMinigame is not null
            || !transitionsByScreen.TryGetValue(screenId, out var transition)
            || !scenesByScreen.TryGetValue(screenId, out var scene)
            || !ReferenceEquals(scene.Location, Game1.currentLocation)
        )
        {
            return NaturalDarknessSceneState.None;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var transitionProgress = GetTransitionProgress(transition, timestamp);
        return new NaturalDarknessSceneState(
            transition.Plan.Phase,
            GetElapsedSeconds(transition.StartTimestamp, timestamp),
            NaturalDarknessTransitionPolicy.GetLightmapWhiteBlend(
                transitionProgress
            )
        );
    }

    private NaturalDarknessScenePlan ResolveScenePlan(
        int screenId,
        GameLocation? sceneLocation = null,
        bool ignoreWarping = false
    )
    {
        try
        {
            if (!Context.IsWorldReady)
                return NaturalDarknessScenePlan.None;

            if (
                !string.Equals(
                    Game1.version,
                    EnvironmentLightProductionContract.SupportedGameVersion,
                    StringComparison.Ordinal
                )
            )
            {
                LogOnce(
                    "natural-darkness.game-version-unsupported",
                    $"Natural darkness is disabled because Stardew version '{Game1.version}' is not the verified {EnvironmentLightProductionContract.SupportedGameVersion} lightmap contract.",
                    LogLevel.Warn
                );
                return NaturalDarknessScenePlan.None;
            }

            var location = sceneLocation ?? Game1.currentLocation;
            if (location is null)
                return NaturalDarknessScenePlan.None;

            var eventActive =
                Game1.eventUp
                || Game1.CurrentEvent is not null
                || location.currentEvent is not null;
            var festivalActive = Game1.CurrentEvent?.isFestival == true;
            if (
                eventActive
                || festivalActive
                || (!ignoreWarping && Game1.isWarping)
                || Game1.currentMinigame is not null
            )
            {
                return NaturalDarknessScenePlan.None;
            }

            var scene = GetOrCreateScene(screenId, location);
            if (scene is null)
                return NaturalDarknessScenePlan.None;

            return BuildScenePlan(location, scene.LocationRule);
        }
        catch (Exception exception)
        {
            LogOnce(
                "natural-darkness.scene-check-failed",
                $"Natural darkness scene check failed closed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
            return NaturalDarknessScenePlan.None;
        }
    }

    private ScreenScene? GetOrCreateScene(int screenId, GameLocation location)
    {
        if (
            scenesByScreen.TryGetValue(screenId, out var existing)
            && ReferenceEquals(existing.Location, location)
        )
        {
            return existing;
        }

        if (
            !scenesByScreen.ContainsKey(screenId)
            && scenesByScreen.Count >= MaximumTrackedScreens
        )
        {
            return null;
        }

        var locationRule = SmapiEnvironmentLightSnapshotProvider.ResolveLocationRule(
            location,
            locationRules
        );
        var scene = new ScreenScene(location, locationRule);
        scenesByScreen[screenId] = scene;
        return scene;
    }

    private NaturalDarknessScenePlan BuildScenePlan(
        GameLocation location,
        EnvironmentLightLocationRuleResolution locationRule
    )
    {
        var isMineShaft = location is MineShaft;
        var mineLevel = 0;
        var isSkullCavern = false;
        var isDangerousSkullCavern = false;
        if (location is MineShaft mine)
        {
            mineLevel = mine.mineLevel;
            isSkullCavern = mine.getMineArea() == 121;
            isDangerousSkullCavern =
                isSkullCavern && mine.GetAdditionalDifficulty() > 0;
        }

        return NaturalDarknessLocationPolicy.Resolve(
            new NaturalDarknessLocationInput(
                locationRule.Status == EnvironmentLightLocationRuleStatus.Matched,
                locationRule.NaturalDarknessProfile,
                location.IsOutdoors,
                darknessAttackLocationAuthorization.IsJunimoBlessingProtecting(
                    locationRule
                ),
                isMineShaft,
                mineLevel,
                isSkullCavern,
                isDangerousSkullCavern,
                Game1.timeOfDay,
                Game1.getStartingToGetDarkTime(location),
                Game1.getTrulyDarkTime(location)
            )
        );
    }

    private void CleanupInvalidScreens()
    {
        List<int>? removals = null;
        foreach (var screenId in scenesByScreen.Keys)
        {
            if (Context.HasScreenId(screenId))
                continue;

            removals ??= new List<int>();
            removals.Add(screenId);
        }
        if (removals is null)
            return;

        foreach (var screenId in removals)
        {
            scenesByScreen.Remove(screenId);
            transitionsByScreen.Remove(screenId);
        }
    }

    private void LogOnce(string reason, string message, LogLevel level)
    {
        if (
            loggedReasons.Count >= MaximumLoggedReasons
            && !loggedReasons.Contains(reason)
        )
        {
            return;
        }

        if (loggedReasons.Add(reason))
            monitor.Log(message, level);
    }

    private static double GetTransitionProgress(
        ScreenTransition transition,
        long timestamp
    )
    {
        return NaturalDarknessTransitionPolicy.Advance(
            transition.StartingProgress,
            transition.Plan.TargetWhiteBlend,
            GetElapsedSeconds(transition.StartTimestamp, timestamp),
            transition.Plan.TransitionDurationSeconds
        );
    }

    private static double GetElapsedSeconds(long previousTimestamp, long timestamp)
    {
        if (
            previousTimestamp <= 0L
            || timestamp <= previousTimestamp
            || Stopwatch.Frequency <= 0L
        )
        {
            return 0d;
        }

        return (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
    }

    private sealed record ScreenScene(
        GameLocation Location,
        EnvironmentLightLocationRuleResolution LocationRule
    );

    private readonly record struct ScreenTransition(
        NaturalDarknessScenePlan Plan,
        double StartingProgress,
        long StartTimestamp
    );
}
