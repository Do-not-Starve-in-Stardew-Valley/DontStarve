#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Music;

/// <summary>
/// Owns the physical activity-music instances. Logical activity state remains in the state store;
/// this service only creates instances for local Farmers and never touches the vanilla music channel.
/// </summary>
internal static class ActivityMusicAudioService
{
    private sealed class Playback
    {
        internal Playback(
            long ownerId,
            long generation,
            ActivityMusicKind kind,
            string trackKey,
            SoundEffectInstance instance
        )
        {
            OwnerId = ownerId;
            Generation = generation;
            Kind = kind;
            TrackKey = trackKey;
            Instance = instance;
            CurrentVolume = 0f;
        }

        internal long OwnerId { get; }

        internal long Generation { get; set; }

        internal ActivityMusicKind Kind { get; set; }

        internal string TrackKey { get; set; }

        internal SoundEffectInstance Instance { get; }

        internal float CurrentVolume { get; set; }

        internal bool IsFadingOut { get; set; }
    }

    private static readonly Dictionary<long, Playback> playbacks = new();
    private static readonly List<Playback> retiringPlaybacks = new();
    private static ActivityMusicVanillaMusicLease? vanillaMusicLease;
    private static IMonitor? monitor;
    private static ActivityMusicResourceRuntime? resources;
    private static bool initialized;
    private static bool enabled;
    private static long lastUpdateTimestamp;

    internal static void Initialize(IModHelper helper, IMonitor activityMonitor)
    {
        monitor = activityMonitor ?? throw new ArgumentNullException(nameof(activityMonitor));
        resources = new ActivityMusicResourceRuntime(helper, activityMonitor);
        var preload = resources.PreloadAll();
        monitor.Log(
            $"Activity music resources preloaded (loaded={preload.Loaded}, failed={preload.Failed}).",
            preload.Failed == 0 ? LogLevel.Debug : LogLevel.Warn
        );
        vanillaMusicLease = new ActivityMusicVanillaMusicLease(activityMonitor);
        initialized = true;
    }

    internal static void Enable()
    {
        if (!initialized || resources is null)
            return;

        enabled = true;
        lastUpdateTimestamp = Stopwatch.GetTimestamp();
    }

    internal static void Disable()
    {
        if (!initialized)
            return;

        StopAll(resumeVanillaMusic: true);
        resources?.Dispose();
        resources = null;
        vanillaMusicLease = null;
        enabled = false;
        lastUpdateTimestamp = 0;
        initialized = false;
    }

    internal static void OnStateRecorded(Farmer owner, ActivityMusicState state)
    {
        if (!enabled || resources is null || owner is null || !owner.IsLocalPlayer)
            return;

        if (
            IsExternalMusicActive()
            && !(IsDawnOrDuskMusicActive()
                && state.Kind is ActivityMusicKind.Combat or ActivityMusicKind.Boss)
        )
        {
            // The lifecycle coordinator clears the logical state on its next tick. Do not create
            // an independent instance in the same frame as an event, jukebox, or higher cue.
            StopOwner(owner.UniqueMultiplayerID);
            return;
        }

        if (playbacks.TryGetValue(owner.UniqueMultiplayerID, out var existing))
        {
            if (existing.Generation == state.Generation
                && existing.Instance.State is SoundState.Playing or SoundState.Paused)
            {
                existing.IsFadingOut = false;
                return;
            }

            if (playbacks.Remove(owner.UniqueMultiplayerID, out var replaced))
            {
                if (replaced.Instance.State != SoundState.Stopped
                    && IsSameTrack(replaced.TrackKey, state.TrackKey))
                {
                    RevivePlayback(replaced, state);
                    return;
                }

                if (replaced.Instance.State == SoundState.Stopped)
                    DisposeInstance(replaced.Instance);
                else if (ActivityMusicFadePolicy.IsImmediateCombatPreemption(
                    state.Kind,
                    replaced.Kind
                ))
                    StopAndDispose(replaced.Instance);
                else
                    BeginFadeOut(replaced);
            }
        }

        if (TryReviveRetiringPlayback(owner.UniqueMultiplayerID, state))
            return;

        if (!resources.TryCreateInstance(state.TrackKey, out var instance) || instance is null)
            return;

        try
        {
            // Raw OGG playback has no FMOD intensity parameter. Work/Farming starts silent and
            // ramps in; Combat mirrors DST's direct PlaySound path and starts at target volume.
            instance.Volume = ActivityMusicFadePolicy.GetFadeInSeconds(state.Kind) <= 0f
                ? GetTargetVolume()
                : 0f;
            if (vanillaMusicLease is not null && !vanillaMusicLease.TryAcquire())
            {
                DisposeInstance(instance);
                return;
            }
            instance.Play();
            playbacks[owner.UniqueMultiplayerID] = new Playback(
                owner.UniqueMultiplayerID,
                state.Generation,
                state.Kind,
                state.TrackKey,
                instance
            );
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"Activity music playback failed for owner {owner.UniqueMultiplayerID}: {ex.Message}",
                LogLevel.Warn
            );
            DisposeInstance(instance);
        }
    }

    internal static void Update()
    {
        if (!enabled)
            return;

        vanillaMusicLease?.Validate();
        if (playbacks.Count > 0)
            vanillaMusicLease?.TryAcquire();

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = GetElapsedSeconds(now);

        vanillaMusicLease?.Update(elapsedSeconds);

        // Snapshot the values because a naturally completed instance may be replaced while this
        // pass is running. The old instance list is also advanced here for short crossfades.
        foreach (var playback in new List<Playback>(playbacks.Values))
        {
            if (!playbacks.TryGetValue(playback.OwnerId, out var current)
                || !ReferenceEquals(current, playback))
            {
                continue;
            }

            if (playback.Instance.State == SoundState.Stopped)
            {
                RestartIfStillActive(playback);
                continue;
            }

            AdvancePlayback(playback, elapsedSeconds);
        }

        AdvanceRetiringPlaybacks(elapsedSeconds, dawnDuskOnly: false);

        ReleaseVanillaMusicIfIdle(resumeIfStillOwned: true);
    }

    internal static void UpdateDawnDuskWorkAndFarmingFadeOuts()
    {
        if (!enabled)
            return;

        var elapsedSeconds = GetElapsedSeconds(Stopwatch.GetTimestamp());
        AdvanceRetiringPlaybacks(elapsedSeconds, dawnDuskOnly: true);
        ReleaseVanillaMusicIfIdle(resumeIfStillOwned: true);
    }

    private static void AdvanceRetiringPlaybacks(double elapsedSeconds, bool dawnDuskOnly)
    {
        foreach (var playback in new List<Playback>(retiringPlaybacks))
        {
            if (
                dawnDuskOnly
                && !ActivityMusicFadePolicy.ShouldAdvanceDuringDawnDusk(playback.Kind)
            )
            {
                continue;
            }

            if (playback.Instance.State == SoundState.Stopped)
            {
                RetireCompletedPlayback(playback);
                continue;
            }

            AdvancePlayback(playback, elapsedSeconds);
            if (playback.IsFadingOut && playback.CurrentVolume <= ActivityMusicFadePolicy.SilentThreshold)
            {
                retiringPlaybacks.Remove(playback);
                StopAndDispose(playback.Instance);
            }
        }
    }

    internal static bool IsExternalMusicActive()
    {
        try
        {
            if (Game1.eventUp || Game1.CurrentEvent is not null || Game1.currentMinigame is not null)
                return true;

            if (Game1.currentLocation?.IsMiniJukeboxPlaying() == true)
                return true;

            return IsDawnOrDuskMusicActive();
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"Activity music external-music boundary could not be read; yielding safely: {ex.Message}",
                LogLevel.Warn
            );
            return true;
        }
    }

    internal static bool IsDawnOrDuskMusicActive()
    {
        return DawnMusicService.IsPlaying || DuskMusicService.IsPlaying;
    }

    internal static void StopOwner(long ownerId)
    {
        if (playbacks.Remove(ownerId, out var playback))
            StopAndDispose(playback.Instance);

        for (var index = retiringPlaybacks.Count - 1; index >= 0; index--)
        {
            if (retiringPlaybacks[index].OwnerId != ownerId)
                continue;

            var retiring = retiringPlaybacks[index];
            retiringPlaybacks.RemoveAt(index);
            StopAndDispose(retiring.Instance);
        }

        ReleaseVanillaMusicIfIdle(resumeIfStillOwned: false);
    }

    internal static void StopWorkAndFarming(long ownerId)
    {
        if (
            playbacks.TryGetValue(ownerId, out var playback)
            && playback.Kind is ActivityMusicKind.Work or ActivityMusicKind.Farming
        )
        {
            playbacks.Remove(ownerId);
            BeginFadeOut(playback);
        }

        // Existing retiring Work/Farming instances already follow the same fade path. Keep them
        // alive until Update advances the tail, so a dusk/Insane edge never creates a hard cut.
        ReleaseVanillaMusicIfIdle(resumeIfStillOwned: true);
    }

    internal static void FadeOutOwner(long ownerId)
    {
        if (!playbacks.Remove(ownerId, out var playback))
            return;

        BeginFadeOut(playback);
        if (playbacks.Count == 0)
            vanillaMusicLease?.Release(resumeIfStillOwned: true);
    }

    internal static void StopAll(bool resumeVanillaMusic = false)
    {
        foreach (var playback in playbacks.Values)
            StopAndDispose(playback.Instance);
        playbacks.Clear();

        foreach (var playback in retiringPlaybacks)
            StopAndDispose(playback.Instance);
        retiringPlaybacks.Clear();
        if (resumeVanillaMusic)
            vanillaMusicLease?.ReleaseImmediately();
        else
            vanillaMusicLease?.Release(resumeIfStillOwned: false);
    }

    private static void RestartIfStillActive(Playback completed)
    {
        if (!ActivityMusicService.StateStore.TryGet(completed.OwnerId, out var state)
            || state is null
            || state.Generation != completed.Generation)
        {
            StopOwner(completed.OwnerId);
            return;
        }

        if (resources is null)
        {
            StopOwner(completed.OwnerId);
            return;
        }

        if (!resources.TryCreateInstance(state.TrackKey, out var replacement)
            || replacement is null)
        {
            // The logical activity state remains intact so a later action can retry, but this
            // failed physical instance must not be retained as a hot-path retry loop.
            StopOwner(completed.OwnerId);
            return;
        }

        var replacementInstance = replacement;

        try
        {
            replacementInstance.Volume = GetTargetVolume();
            replacementInstance.Play();

            var replacementPlayback = new Playback(
                completed.OwnerId,
                completed.Generation,
                completed.Kind,
                completed.TrackKey,
                replacementInstance
            )
            {
                CurrentVolume = GetTargetVolume()
            };
            playbacks[completed.OwnerId] = replacementPlayback;
            DisposeInstance(completed.Instance);
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"Activity music replay failed for owner {completed.OwnerId}: {ex.Message}",
                LogLevel.Warn
            );
            DisposeInstance(replacementInstance);
            StopOwner(completed.OwnerId);
        }
    }

    private static void AdvancePlayback(Playback playback, double elapsedSeconds)
    {
        try
        {
            var target = playback.IsFadingOut ? 0f : GetTargetVolume();
            var duration = playback.IsFadingOut
                ? ActivityMusicFadePolicy.GetFadeOutSeconds(playback.Kind)
                : ActivityMusicFadePolicy.GetFadeInSeconds(playback.Kind);
            playback.CurrentVolume = ActivityMusicFadePolicy.Advance(
                playback.CurrentVolume,
                target,
                elapsedSeconds,
                duration
            );
            playback.Instance.Volume = playback.CurrentVolume;
        }
        catch (Exception ex)
        {
            monitor?.Log(
                $"Activity music fade update failed for owner {playback.OwnerId}: {ex.Message}",
                LogLevel.Trace
            );
        }
    }

    private static void BeginFadeOut(Playback playback)
    {
        playback.IsFadingOut = true;
        if (ActivityMusicFadePolicy.GetFadeOutSeconds(playback.Kind) <= 0f)
        {
            StopAndDispose(playback.Instance);
            return;
        }

        if (playback.Instance.State == SoundState.Stopped)
        {
            StopAndDispose(playback.Instance);
            return;
        }

        retiringPlaybacks.Add(playback);
    }

    private static bool TryReviveRetiringPlayback(long ownerId, ActivityMusicState state)
    {
        for (var index = retiringPlaybacks.Count - 1; index >= 0; index--)
        {
            var playback = retiringPlaybacks[index];
            if (playback.OwnerId != ownerId || !IsSameTrack(playback.TrackKey, state.TrackKey))
                continue;

            retiringPlaybacks.RemoveAt(index);
            if (playback.Instance.State == SoundState.Stopped)
            {
                DisposeInstance(playback.Instance);
                continue;
            }

            RevivePlayback(playback, state);
            return true;
        }

        return false;
    }

    private static void RevivePlayback(Playback playback, ActivityMusicState state)
    {
        playback.Generation = state.Generation;
        playback.Kind = state.Kind;
        playback.TrackKey = state.TrackKey;
        playback.IsFadingOut = false;
        playbacks[state.OwnerId] = playback;
    }

    private static bool IsSameTrack(string firstTrackKey, string secondTrackKey)
    {
        if (string.Equals(firstTrackKey, secondTrackKey, StringComparison.Ordinal))
            return true;

        return ActivityMusicResourceCatalog.TryResolve(firstTrackKey, out var first, out _)
            && first is not null
            && ActivityMusicResourceCatalog.TryResolve(secondTrackKey, out var second, out _)
            && second is not null
            && string.Equals(first.RelativePath, second.RelativePath, StringComparison.Ordinal);
    }

    private static void RetireCompletedPlayback(Playback playback)
    {
        retiringPlaybacks.Remove(playback);
        DisposeInstance(playback.Instance);
    }

    private static void ReleaseVanillaMusicIfIdle(bool resumeIfStillOwned)
    {
        if (playbacks.Count == 0 && retiringPlaybacks.Count == 0)
            vanillaMusicLease?.Release(resumeIfStillOwned);
    }

    private static float GetTargetVolume()
    {
        return Math.Clamp(Game1.options.musicVolumeLevel, 0f, 1f);
    }

    private static double GetElapsedSeconds(long now)
    {
        if (lastUpdateTimestamp == 0)
        {
            lastUpdateTimestamp = now;
            return 0d;
        }

        var elapsed = (now - lastUpdateTimestamp) / (double)Stopwatch.Frequency;
        lastUpdateTimestamp = now;
        return double.IsFinite(elapsed) && elapsed > 0d
            ? Math.Min(elapsed, 0.1d)
            : 0d;
    }

    private static void StopAndDispose(SoundEffectInstance instance)
    {
        try
        {
            instance.Stop();
        }
        catch (Exception ex)
        {
            monitor?.Log($"Activity music stop failed: {ex.Message}", LogLevel.Warn);
        }

        DisposeInstance(instance);
    }

    private static void DisposeInstance(SoundEffectInstance instance)
    {
        try
        {
            instance.Dispose();
        }
        catch (Exception ex)
        {
            monitor?.Log($"Activity music instance dispose failed: {ex.Message}", LogLevel.Warn);
        }
    }
}
