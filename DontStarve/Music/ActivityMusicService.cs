#nullable enable

using System;
using System.Globalization;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Music;

/// <summary>
/// Lifecycle owner for activity music state. Action bridges will call this service in the next
/// stage; keeping the lifecycle here prevents stale activity state from surviving a save, warp or
/// return to title.
/// </summary>
internal static class ActivityMusicService
{
    private static IModHelper? helper;
    private static IMonitor? monitor;
    private static string? modId;
    private static bool initialized;
    private static bool enabled;
    private static bool externalMusicWasActive;
    private static int previousTimeOfDay;

    internal static ActivityMusicStateStore StateStore { get; } = new();

    private static ActivityMusicSuppressionStateStore SuppressionStates { get; } = new();

    internal static void Initialize(IModHelper modHelper, IMonitor modMonitor, string patchOwnerId)
    {
        helper = modHelper;
        monitor = modMonitor;
        modId = patchOwnerId;
        initialized = true;
        ActivityMusicAudioService.Initialize(modHelper, modMonitor);
    }

    internal static void Enable()
    {
        if (!initialized || enabled || helper is null)
            return;

        enabled = true;
        previousTimeOfDay = Game1.timeOfDay;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        helper.Events.Player.Warped += OnWarped;
        previousTimeOfDay = Game1.timeOfDay;
        ActivityMusicActionBridge.Enable(helper, monitor, modId);
        ActivityMusicAudioService.Enable();
    }

    internal static void Disable()
    {
        if (!initialized || helper is null)
            return;

        if (enabled)
        {
            helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
            helper.Events.GameLoop.DayStarted -= OnDayStarted;
            helper.Events.GameLoop.DayEnding -= OnDayEnding;
            helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
            helper.Events.GameLoop.TimeChanged -= OnTimeChanged;
            helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;
            helper.Events.Player.Warped -= OnWarped;
        }

        enabled = false;
        ActivityMusicActionBridge.Disable();
        ActivityMusicAudioService.Disable();
        StateStore.Clear();
        SuppressionStates.Clear();
        externalMusicWasActive = false;
        previousTimeOfDay = 0;
    }

    internal static void ResetForTests()
    {
        StateStore.Clear();
        SuppressionStates.Clear();
        ActivityMusicActionBridge.ResetForTests();
    }

    internal static void RecordWork(Farmer? owner, string reason)
    {
        RecordWork(owner, owner?.currentLocation, reason);
    }

    internal static void RecordWork(Farmer? owner, GameLocation? location, string reason)
    {
        if (location is not null && IsWorkBlockedByDusk(location))
            return;
        if (IsActivitySuppressedByPhase(owner))
            return;
        if (IsActivitySuppressedByInsane(owner))
            return;

        Record(owner, ActivityMusicKind.Work, location, reason);
    }

    internal static void RecordFarming(Farmer? owner, GameLocation? location, string reason)
    {
        if (!IsFarmingLocation(location))
            return;
        if (owner is null || owner.UniqueMultiplayerID == 0)
            return;
        if (IsActivitySuppressedByPhase(owner))
            return;
        if (IsActivitySuppressedByInsane(owner))
            return;
        if (SuppressionStates.IsFarmingBlocked(owner.UniqueMultiplayerID))
            return;

        Record(owner, ActivityMusicKind.Farming, location, reason);
    }

    internal static void RecordCombat(Farmer? owner, string reason)
    {
        Record(owner, ActivityMusicKind.Combat, owner?.currentLocation, reason);
    }

    internal static bool IsLocalCombatOrBossActive()
    {
        if (!HasValidLocalOwner())
            return false;

        return StateStore.TryGet(Game1.player.UniqueMultiplayerID, out var state)
            && state is not null
            && state.Kind is ActivityMusicKind.Combat or ActivityMusicKind.Boss;
    }

    internal static void StartInsaneCooldown(string playerKey, double seconds)
    {
        if (
            !enabled
            || !long.TryParse(
                playerKey,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var ownerId
            )
        )
        {
            return;
        }

        if (!SuppressionStates.StartInsane(ownerId, seconds))
            return;

        StateStore.RemoveWorkAndFarming(ownerId);
        ActivityMusicAudioService.StopWorkAndFarming(ownerId);
    }

    private static void Record(
        Farmer? owner,
        ActivityMusicKind kind,
        GameLocation? location,
        string reason
    )
    {
        _ = reason;
        if (!enabled || owner is null || owner.UniqueMultiplayerID == 0 || location is null)
            return;

        var locationContext = ActivityMusicLocationResolver.Resolve(
            location,
            location.GetSeason()
        );
        var trackKey = ActivityMusicTrackKey.TryCreate(kind, locationContext);
        if (trackKey is null)
            return;

        var state = StateStore.Set(
            owner.UniqueMultiplayerID,
            kind,
            locationContext,
            trackKey,
            ActivityMusicArbitration.GetDurationSeconds(kind)
        );
        ActivityMusicAudioService.OnStateRecorded(owner, state);
    }

    private static bool IsWorkBlockedByDusk(GameLocation location)
    {
        var locationContext = ActivityMusicLocationResolver.Resolve(
            location,
            location.GetSeason()
        );
        var isMineOrSkullCave = locationContext.Family is
            ActivityMusicLocationFamily.Mine
            or ActivityMusicLocationFamily.QuarryMine
            or ActivityMusicLocationFamily.SkullCavern;
        return ActivityMusicPhasePolicy.IsWorkBlocked(
            isMineOrSkullCave,
            Game1.timeOfDay,
            Game1.getStartingToGetDarkTime(location)
        );
    }

    private static bool IsActivitySuppressedByInsane(Farmer? owner)
    {
        return owner is not null
            && owner.UniqueMultiplayerID != 0
            && SuppressionStates.IsInsaneCooldownActive(owner.UniqueMultiplayerID);
    }

    private static bool IsActivitySuppressedByPhase(Farmer? owner)
    {
        return owner is not null
            && owner.UniqueMultiplayerID != 0
            && SuppressionStates.IsFarmingBlocked(owner.UniqueMultiplayerID);
    }

    private static void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        ClearState();
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        previousTimeOfDay = Game1.timeOfDay;
        SuppressionStates.Clear();
        if (HasValidLocalOwner() && Game1.currentLocation is not null && !IsDawnExcludedLocation())
            EnterDawnPhase(Game1.player.UniqueMultiplayerID);
    }

    private static void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        ClearState();
    }

    private static void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ClearState();
    }

    private static void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (!enabled || !HasValidLocalOwner() || Game1.currentLocation is null)
        {
            previousTimeOfDay = e.NewTime;
            return;
        }

        var oldTime = previousTimeOfDay;
        previousTimeOfDay = e.NewTime;
        var location = Game1.currentLocation;
        var ownerId = Game1.player.UniqueMultiplayerID;

        if (
            !IsDawnExcludedLocation()
            && ActivityMusicPhasePolicy.IsDawnCrossing(oldTime, e.NewTime, 600)
        )
        {
            EnterDawnPhase(ownerId);
        }

        var locationContext = ActivityMusicLocationResolver.Resolve(
            location,
            location.GetSeason()
        );
        var isMineOrSkullCave = locationContext.Family is
            ActivityMusicLocationFamily.Mine
            or ActivityMusicLocationFamily.QuarryMine
            or ActivityMusicLocationFamily.SkullCavern;
        if (isMineOrSkullCave)
            return;

        if (
            !ActivityMusicPhasePolicy.IsDuskCrossing(
                oldTime,
                e.NewTime,
                Game1.getStartingToGetDarkTime(location)
            )
        )
        {
            return;
        }

        if (!SuppressionStates.StartDusk(ownerId, ActivityMusicPhasePolicy.DuskSuppressionSeconds))
            return;

        StateStore.RemoveWorkAndFarming(ownerId);
        ActivityMusicAudioService.StopWorkAndFarming(ownerId);
    }

    private static void EnterDawnPhase(long ownerId)
    {
        if (!SuppressionStates.StartDawn(ownerId, ActivityMusicPhasePolicy.DawnSuppressionSeconds))
            return;

        StateStore.RemoveWorkAndFarming(ownerId);
        ActivityMusicAudioService.StopWorkAndFarming(ownerId);
    }

    private static bool IsDawnExcludedLocation()
    {
        return Game1.currentLocation is MineShaft or VolcanoDungeon;
    }

    private static void OnWarped(object? sender, WarpedEventArgs e)
    {
        // A warp is not an activity cancellation. Keep this owner's logical deadline and current
        // instance so a quiet map change follows the normal fade-out path; a new valid action in
        // the destination can replace it with the destination track. Fishing itself is different:
        // its rod lifecycle cannot survive a map change, so only that action bridge is cleared.
        ActivityMusicActionBridge.ForgetFishingOwner(e.Player.UniqueMultiplayerID);
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!HasValidLocalOwner())
        {
            // Save/title transitions normally raise their own events, but this guard also closes
            // the short window where Game1.player is absent or replaced before those callbacks.
            ClearState();
            return;
        }

        var externalMusicActive = ActivityMusicAudioService.IsExternalMusicActive();
        if (externalMusicActive)
        {
            if (!externalMusicWasActive)
            {
                if (ActivityMusicAudioService.IsDawnOrDuskMusicActive())
                {
                    // Dawn/Dusk only own the background and Work/Farming lanes. Combat/Boss
                    // deliberately remains alive and may overlap the independent one-shot.
                    ClearLocalWorkAndFarming();
                }
                else
                {
                    // Events, minigames and jukeboxes remain a full external hand-off.
                    ActivityMusicAudioService.StopAll();
                    StateStore.Clear();
                    ActivityMusicActionBridge.ClearActiveFishing();
                }
            }

            if (ActivityMusicAudioService.IsDawnOrDuskMusicActive())
                ActivityMusicAudioService.UpdateDawnDuskWorkAndFarmingFadeOuts();

            externalMusicWasActive = true;
            return;
        }

        externalMusicWasActive = false;
        ActivityMusicAudioService.Update();
    }

    private static void ClearLocalWorkAndFarming()
    {
        if (!HasValidLocalOwner())
            return;

        var ownerId = Game1.player.UniqueMultiplayerID;
        StateStore.RemoveWorkAndFarming(ownerId);
        ActivityMusicAudioService.StopWorkAndFarming(ownerId);
        ActivityMusicActionBridge.ClearActiveFishing();
    }

    private static void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        ActivityMusicActionBridge.TickActiveFishing();
        SuppressionStates.Advance(1d);
        foreach (var ownerId in StateStore.Advance(1))
            ActivityMusicAudioService.FadeOutOwner(ownerId);
    }

    private static void ClearState()
    {
        ActivityMusicAudioService.StopAll();
        StateStore.Clear();
        SuppressionStates.Clear();
        ActivityMusicActionBridge.ClearActiveFishing();
        externalMusicWasActive = false;
        previousTimeOfDay = Game1.timeOfDay;
    }

    private static bool IsFarmingLocation(GameLocation? location)
    {
        // IslandWest can contain plantable tiles, but it is not a Farm identity. Only Farm
        // (including a Farm-identity mod map) and the explicit greenhouse dimension qualify.
        return location is Farm || location?.IsGreenhouse == true;
    }

    private static bool HasValidLocalOwner()
    {
        return Game1.player is not null
            && Game1.player.IsLocalPlayer
            && Game1.player.UniqueMultiplayerID != 0;
    }
}
