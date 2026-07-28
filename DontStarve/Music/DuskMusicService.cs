using System;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace DontStarve.Music;

/// <summary>
/// 按季节日落线播放 DS 黄昏音乐；跨过触发时间的一次 TimeChanged 才播放。
/// </summary>
internal static class DuskMusicService
{
    private static SoundEffectInstance _duskSound;
    private static SoundEffectInstance _islandDuskSound;
    private static IMonitor _monitor;
    private static bool _initialized;
    private static bool _suppressed;
    private static int _previousTime;

    internal static void Enable(IModHelper helper, IMonitor monitor)
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

    internal static void Disable(IModHelper helper)
    {
        if (!_initialized)
            return;

        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.TimeChanged -= OnTimeChanged;

        _duskSound?.Stop();
        _islandDuskSound?.Stop();
        _duskSound?.Dispose();
        _islandDuskSound?.Dispose();
        _duskSound = null;
        _islandDuskSound = null;
        _initialized = false;
        _suppressed = false;
        _previousTime = 0;
    }

    internal static void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed)
            return;

        _suppressed = suppressed;
        if (!_initialized || !suppressed)
            return;

        // Release does not replay the crossed dusk edge; a later day supplies a new edge.
        _duskSound?.Stop();
        _islandDuskSound?.Stop();
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

            // 用 old < trigger <= new 判断跨越，避免 20:00 之后读档或重复 TimeChanged 多次播放。
            if (
                !_suppressed
                && oldTime < triggerTime
                && newTime >= triggerTime
                && !IsInDungeon()
            )
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
            if (_suppressed)
                return;

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
        // 与夜晚理智损耗使用同一季节边界：秋 19:00，冬 18:00，其余 20:00。
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
