using System;
using HarmonyLib;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Music;

internal static class DawnMusicService
{
    private static IMonitor _monitor;
    private static Harmony _harmony;
    private static SoundEffectInstance _dawnSound;
    private static SoundEffectInstance _islandDawnSound;
    private static bool _hasMorningSongPatched;
    private static bool _initialized;
    private static bool _isDawnTime;
    private static bool _isIslandArea;
    private static bool _isInDungeon;

    internal static void Initialize(IModHelper helper, IMonitor monitor, string manifestId)
    {
        if (_initialized)
            return;

        _initialized = true;
        _monitor = monitor;
        _harmony = new Harmony(manifestId);
        _dawnSound = MusicAudioLoader.LoadInstance(helper, monitor, "dawn.wav");
        _islandDawnSound = MusicAudioLoader.LoadInstance(helper, monitor, "sw_dawn.wav");

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.Player.Warped += OnWarped;
    }

    private static void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        _isDawnTime = false;
        UpdateLocationState(Game1.currentLocation);
    }

    private static void OnWarped(object sender, WarpedEventArgs e)
    {
        UpdateLocationState(e.NewLocation);
        if (_isInDungeon && _isDawnTime)
            StopDawnMusic();
    }

    private static void OnTimeChanged(object sender, TimeChangedEventArgs e)
    {
        UpdateLocationState(Game1.currentLocation);

        switch (e.NewTime)
        {
            case 600 when !_isInDungeon:
                StartDawnMusic();
                break;
            case 620 when !_isIslandArea:
            case 630 when _isIslandArea:
                StopDawnMusic();
                break;
        }
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (_isDawnTime && !_isIslandArea && Game1.currentSong?.IsPlaying == true)
            Game1.currentSong.Stop(AudioStopOptions.Immediate);
    }

    private static void StartDawnMusic()
    {
        try
        {
            UpdateLocationState(Game1.currentLocation);
            if (_isInDungeon)
                return;

            var targetSound = _isIslandArea ? _islandDawnSound : _dawnSound;
            if (targetSound == null)
                return;

            _isDawnTime = true;
            Game1.currentSong?.Stop(AudioStopOptions.Immediate);

            if (targetSound.State == SoundState.Playing)
                targetSound.Stop();

            targetSound.Play();

            if (!_isIslandArea)
                PatchMorningSong(true);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to start dawn music: {ex.Message}", LogLevel.Error);
        }
    }

    private static void StopDawnMusic(bool forceRestore = false)
    {
        if (!_isDawnTime && !forceRestore)
            return;

        _isDawnTime = false;
        _dawnSound?.Stop();
        _islandDawnSound?.Stop();

        try
        {
            if (!_isIslandArea || forceRestore)
            {
                PatchMorningSong(false);
                RestoreMusicSystem();
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to stop dawn music: {ex.Message}", LogLevel.Error);
        }
    }

    private static void PatchMorningSong(bool enable)
    {
        var originalMethod = AccessTools.Method(typeof(Game1), "playMorningSong");
        if (originalMethod == null || _harmony == null)
            return;

        if (enable && !_hasMorningSongPatched)
        {
            _harmony.Patch(
                originalMethod,
                new HarmonyMethod(typeof(DawnMusicService), nameof(BlockMorningSong)));
            _hasMorningSongPatched = true;
        }
        else if (!enable && _hasMorningSongPatched)
        {
            _harmony.Unpatch(originalMethod, HarmonyPatchType.Prefix, _harmony.Id);
            _hasMorningSongPatched = false;
        }
    }

    private static bool BlockMorningSong()
    {
        return !_isDawnTime && !_isIslandArea;
    }

    private static void RestoreMusicSystem()
    {
        try
        {
            Game1.updateMusic();
            if (Game1.currentLocation == null)
                return;

            var methodName = Constants.ApiVersion.IsNewerThan("3.14.0")
                ? "forceUpdateLocalAmbient"
                : "updateLocalAmbient";

            object[] parameters = Constants.ApiVersion.IsNewerThan("3.14.0")
                ? null
                : new object[] { Game1.currentLocation.currentEvent == null };

            AccessTools.Method(typeof(GameLocation), methodName)
                ?.Invoke(Game1.currentLocation, parameters);

            if (!_isIslandArea)
                Game1.playMorningSong();
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to restore music system: {ex.Message}", LogLevel.Error);
        }
    }

    private static void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        StopDawnMusic(forceRestore: true);
    }

    private static void UpdateLocationState(GameLocation location)
    {
        _isIslandArea = IsIslandArea(location);
        _isInDungeon = location is MineShaft || location is VolcanoDungeon;
    }

    private static bool IsIslandArea(GameLocation location)
    {
        return location is IslandLocation || location?.Name?.Contains("Island") == true;
    }
}
