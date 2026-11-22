#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using Microsoft.Xna.Framework;
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
    private readonly Func<int, NaturalDarknessDrawContext> drawContextProvider;
    private readonly Func<int, NaturalDarknessSceneState> sceneStateProvider;
    private readonly Dictionary<int, ScreenTransition> transitionsByScreen = new();
    private readonly Dictionary<int, ScreenScene> scenesByScreen = new();
    private readonly Dictionary<int, ScreenAmbienceColor> ambienceColorsByScreen = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool enabled;
    private bool darknessEnabled;
    private bool patchInstalled;
    private bool wallSconcePatchInstalled;
    private bool disposed;

    internal SmapiNaturalDarknessLightmapService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        bool enabled,
        bool darknessEnabled,
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
        this.darknessEnabled = darknessEnabled;
        harmony = new Harmony(string.Concat(modId, ".Sanity4.NaturalDarkness"));
        drawContextProvider = GetDrawContextForScreen;
        sceneStateProvider = GetSceneStateForScreen;
        patchInstalled = NaturalDarknessLightmapPatch.TryInstall(
            harmony,
            monitor,
            drawContextProvider
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
        ambienceColorsByScreen.Clear();
    }

    /// <summary>
    /// 黑暗开关独立切换（氛围可单独开启）。模式切换时清空绝对色状态，避免旧模式的
    /// 混合值被当作新模式的起点。
    /// </summary>
    internal void SetDarknessEnabled(bool value)
    {
        if (disposed || darknessEnabled == value)
            return;

        darknessEnabled = value;
        transitionsByScreen.Clear();
        ambienceColorsByScreen.Clear();
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
                drawContextProvider
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
            {
                transitionsByScreen.Remove(screenId);
                ambienceColorsByScreen.Remove(screenId);
            }
        }
        else
        {
            SynchronizeScreenTransition(screenId, plan, Stopwatch.GetTimestamp());
            if (IsAmbienceActive)
                AdvanceAmbienceColor(screenId, plan, Stopwatch.GetTimestamp());
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
        if (IsAmbienceActive)
        {
            ambienceColorsByScreen[screenId] = new ScreenAmbienceColor(
                ToXnaColor(plan.TargetColor),
                timestamp
            );
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _ = sender;
        _ = e;
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
        ambienceColorsByScreen.Clear();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        _ = sender;
        _ = e;
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
        ambienceColorsByScreen.Clear();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _ = sender;
        _ = e;
        transitionsByScreen.Clear();
        scenesByScreen.Clear();
        ambienceColorsByScreen.Clear();
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
        var startingProgress = plan.ProgressCeiling <= currentProgress
            ? plan.ProgressCeiling
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
                    + $"target=({plan.TargetColor.R.ToString("0.###", CultureInfo.InvariantCulture)},"
                    + $"{plan.TargetColor.G.ToString("0.###", CultureInfo.InvariantCulture)},"
                    + $"{plan.TargetColor.B.ToString("0.###", CultureInfo.InvariantCulture)}), "
                    + $"ceiling={plan.ProgressCeiling.ToString("0.###", CultureInfo.InvariantCulture)}, "
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

    private bool TryGetActiveScreenTransition(
        int screenId,
        out ScreenTransition transition
    )
    {
        transition = default;
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
            || !transitionsByScreen.TryGetValue(screenId, out transition)
            || !scenesByScreen.TryGetValue(screenId, out var scene)
            || !ReferenceEquals(scene.Location, Game1.currentLocation)
        )
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 光照图绘制上下文。氛围激活时走绝对色（服务端已推进的当前色），否则保持旧相对模型
    /// （Lerp 原生底色向白，进度受计划上限钳制）逐字节同旧行为。
    /// </summary>
    private NaturalDarknessDrawContext GetDrawContextForScreen(int screenId)
    {
        if (!TryGetActiveScreenTransition(screenId, out var transition))
            return NaturalDarknessDrawContext.None;

        var timestamp = Stopwatch.GetTimestamp();
        if (IsAmbienceActive)
        {
            var color = ambienceColorsByScreen.TryGetValue(screenId, out var state)
                ? state.Current
                : ToXnaColor(transition.Plan.TargetColor);
            return NaturalDarknessDrawContext.FromAbsolute(color);
        }

        var progress = NaturalDarknessTransitionPolicy.GetLightmapWhiteBlend(
            GetTransitionProgress(transition, timestamp)
        );
        return NaturalDarknessDrawContext.Relative(progress);
    }

    internal NaturalDarknessSceneState GetSceneStateForScreen(int screenId)
    {
        if (!TryGetActiveScreenTransition(screenId, out var transition))
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

    private bool IsAmbienceActive => enabled && !darknessEnabled;

    private void AdvanceAmbienceColor(
        int screenId,
        NaturalDarknessScenePlan plan,
        long timestamp
    )
    {
        var target = ToXnaColor(plan.TargetColor);
        if (!ambienceColorsByScreen.TryGetValue(screenId, out var state))
        {
            // 首个绝对色计划：时长 0 直达目标；否则从「无暗」出发按计划时长淡入，
            // 等价旧模型从原生亮场渐暗的观感起点。
            var initial = plan.TransitionDurationSeconds <= 0d
                ? target
                : Color.Black;
            ambienceColorsByScreen[screenId] = new ScreenAmbienceColor(
                initial,
                timestamp
            );
            return;
        }

        var elapsed = GetElapsedSeconds(state.Timestamp, timestamp);
        var fraction = plan.TransitionDurationSeconds > 0d
            ? (float)Math.Clamp(
                elapsed / plan.TransitionDurationSeconds,
                0d,
                1d
            )
            : 1f;
        ambienceColorsByScreen[screenId] = new ScreenAmbienceColor(
            Color.Lerp(state.Current, target, fraction),
            timestamp
        );
    }

    private static Color ToXnaColor(NaturalDarknessTargetColor target)
    {
        return new Color(
            (byte)Math.Clamp((int)Math.Round(target.R * 255d), 0, 255),
            (byte)Math.Clamp((int)Math.Round(target.G * 255d), 0, 255),
            (byte)Math.Clamp((int)Math.Round(target.B * 255d), 0, 255)
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

            var isAndroidPlatform = Constants.TargetPlatform == GamePlatform.Android;
            var expectedGameVersion = StardewRuntimeVersionPolicy.ExpectedGameVersion(
                isAndroidPlatform
            );
            if (
                !StardewRuntimeVersionPolicy.IsSupportedGameVersion(
                    Game1.version,
                    isAndroidPlatform
                )
            )
            {
                LogOnce(
                    "natural-darkness.game-version-unsupported",
                    $"Natural darkness is disabled because Stardew version '{Game1.version}' is not the verified {expectedGameVersion} lightmap contract.",
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

        var input = new NaturalDarknessLocationInput(
            locationRule.Status == EnvironmentLightLocationRuleStatus.Matched,
            locationRule.NaturalDarknessProfile,
            location.IsOutdoors,
            darknessAttackLocationAuthorization.IsJunimoBlessingProtecting(
                locationRule
            ),
            isMineShaft,
            location is VolcanoDungeon,
            Game1.season == Season.Spring,
            mineLevel,
            isSkullCavern,
            isDangerousSkullCavern,
            Game1.timeOfDay,
            Game1.getStartingToGetDarkTime(location),
            Game1.getTrulyDarkTime(location)
        );
        // 氛围开=新矩阵（含黑暗耦合）；氛围关=旧黑暗矩阵逐字节不变。
        return IsAmbienceActive
            ? DontStarveLightAmbiencePolicy.Resolve(input, darknessEnabled)
            : NaturalDarknessLocationPolicy.Resolve(input);
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
            transition.Plan.ProgressCeiling,
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

    private readonly record struct ScreenAmbienceColor(Color Current, long Timestamp);
}
