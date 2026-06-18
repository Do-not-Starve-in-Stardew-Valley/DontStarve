using System;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Music;

internal static class DuskMusicService
{
    private static SoundEffectInstance _duskSound;
    private static SoundEffectInstance _islandDuskSound;
    private static IMonitor _monitor;
    private static bool _initialized;
    private static int _previousTime;

    internal static void Initialize(IModHelper helper, IMonitor monitor)
    {
        if (_initialized)
            return;

        _initialized = true;
        _monitor = monitor;
        _previousTime = Game1.timeOfDay;
        _duskSound = MusicAudioLoader.LoadInstance(helper, monitor, "dusk.wav");
        _islandDuskSound = MusicAudioLoader.LoadInstance(helper, monitor, "sw_dusk.wav");

        if (_duskSound == null || _islandDuskSound == null)
            return;

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
    }

    private static void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        _previousTime = Game1.timeOfDay;
    }

    private static void OnTimeChanged(object sender, TimeChangedEventArgs e)
    {
        try
        {
            var oldTime = _previousTime;
            var newTime = e.NewTime;
            var triggerTime = GetSeasonTriggerTime();

            if (oldTime < triggerTime && newTime >= triggerTime && !IsInDungeon())
                PlayDuskMusic(IsIslandArea());

            _previousTime = newTime;
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to handle dusk music time change: {ex.Message}", LogLevel.Error);
        }
    }

    private static void PlayDuskMusic(bool island)
    {
        try
        {
            var targetSound = island ? _islandDuskSound : _duskSound;
            if (targetSound == null)
                return;

            if (targetSound.State == SoundState.Playing)
                targetSound.Stop();

            targetSound.Volume = Game1.options.musicVolumeLevel;
            targetSound.Play();
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to play dusk music: {ex.Message}", LogLevel.Error);
        }
    }

    private static int GetSeasonTriggerTime()
    {
        return Game1.currentSeason switch
        {
            "fall" => 1900,
            "winter" => 1800,
            _ => 2000,
        };
    }

    private static bool IsInDungeon()
    {
        return Game1.currentLocation is MineShaft || Game1.currentLocation is VolcanoDungeon;
    }

    private static bool IsIslandArea()
    {
        return Game1.currentLocation is IslandLocation
            || Game1.currentLocation?.Name == "IslandFarmHouse";
    }
}
