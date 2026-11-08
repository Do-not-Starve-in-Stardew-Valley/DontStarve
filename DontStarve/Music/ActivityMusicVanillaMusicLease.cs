#nullable enable

using System;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Music;

/// <summary>
/// Temporarily pauses the vanilla ICue while local activity audio owns the foreground. The cue is
/// resumed only when the exact same object is still paused; a replaced/stopped cue is abandoned so
/// vanilla can select the current track instead of reviving stale music.
/// </summary>
internal sealed class ActivityMusicVanillaMusicLease
{
    private readonly IMonitor monitor;
    private ICue? pausedCue;
    private string? pausedCueName;
    private float originalVolume;
    private float currentVolume;
    private bool ownsPause;
    private bool releasing;
    private bool pausedByLease;

    internal ActivityMusicVanillaMusicLease(IMonitor activityMonitor)
    {
        monitor = activityMonitor ?? throw new ArgumentNullException(nameof(activityMonitor));
    }

    internal bool TryAcquire()
    {
        if (ownsPause)
        {
            Validate();
            if (ownsPause && releasing)
            {
                releasing = false;
                if (pausedByLease && pausedCue is not null)
                {
                    pausedCue.Resume();
                    pausedByLease = false;
                }
            }
            return ownsPause;
        }

        var cue = Game1.currentSong;
        if (cue is null || !cue.IsPlaying || cue.IsPaused || cue.IsStopped)
            return true;

        try
        {
            pausedCue = cue;
            pausedCueName = cue.Name;
            originalVolume = Math.Clamp(cue.Volume, 0f, 1f);
            currentVolume = originalVolume;
            ownsPause = true;
            releasing = false;
            pausedByLease = false;
            return true;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Could not pause vanilla music for activity ownership ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Trace
            );
            Clear();
            return false;
        }
    }

    internal void Update(double elapsedSeconds)
    {
        if (!ownsPause || pausedCue is null)
            return;

        Validate();
        if (!ownsPause || pausedCue is null || elapsedSeconds <= 0d)
            return;

        if (pausedCue.IsPaused && !pausedByLease)
            return;

        var target = releasing ? originalVolume : 0f;
        var duration = ActivityMusicFadePolicy.WorkFarmingFadeInSeconds;
        currentVolume = ActivityMusicFadePolicy.Advance(
            currentVolume,
            target,
            elapsedSeconds,
            duration
        );
        pausedCue.Volume = currentVolume;

        if (!releasing && currentVolume <= ActivityMusicFadePolicy.SilentThreshold)
        {
            pausedCue.Pause();
            pausedByLease = true;
            return;
        }

        if (releasing && Math.Abs(currentVolume - originalVolume) <= ActivityMusicFadePolicy.SilentThreshold)
        {
            pausedCue.Volume = originalVolume;
            Clear();
        }
    }

    internal void Validate()
    {
        if (!ownsPause)
            return;

        if (!ReferenceEquals(Game1.currentSong, pausedCue)
            || pausedCue is null
            || pausedCue.IsStopped
            || !string.Equals(pausedCue.Name, pausedCueName, StringComparison.Ordinal))
        {
            Clear();
        }
    }

    internal void Release(bool resumeIfStillOwned)
    {
        if (!ownsPause || pausedCue is null)
        {
            Clear();
            return;
        }

        Validate();
        if (!ownsPause || pausedCue is null)
            return;

        if (!resumeIfStillOwned)
        {
            if (ReferenceEquals(Game1.currentSong, pausedCue) && !pausedCue.IsStopped)
            {
                if (!pausedByLease)
                    pausedCue.Pause();
                pausedByLease = true;
            }

            Clear();
            RequestVanillaReselect();
            return;
        }

        releasing = true;
        if (pausedByLease)
        {
            pausedCue.Resume();
            pausedByLease = false;
        }
    }

    internal void ReleaseImmediately()
    {
        if (!ownsPause || pausedCue is null)
        {
            Clear();
            return;
        }

        Validate();
        if (!ownsPause || pausedCue is null)
            return;

        if (ReferenceEquals(Game1.currentSong, pausedCue) && !pausedCue.IsStopped)
        {
            try
            {
                if (pausedByLease)
                    pausedCue.Resume();
                pausedCue.Volume = originalVolume;
            }
            catch (Exception exception)
            {
                monitor.Log(
                    $"Could not immediately restore vanilla music ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Trace
                );
            }
        }
        else
        {
            RequestVanillaReselect();
        }

        Clear();
    }

    private static void RequestVanillaReselect()
    {
        if (Game1.game1 is not null && Game1.game1.IsMainInstance && Game1.hasLoadedGame)
            Game1.requestedMusicDirty = true;
    }

    private void Clear()
    {
        pausedCue = null;
        pausedCueName = null;
        originalVolume = 0f;
        currentVolume = 0f;
        ownsPause = false;
        releasing = false;
        pausedByLease = false;
    }
}
