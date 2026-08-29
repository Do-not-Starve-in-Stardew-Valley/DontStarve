#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// A one-shot voice detached from its owner lane after a projection or confirmed death is removed.
/// The detached instance is still ticked by the coordinator, but no longer participates in owner
/// state or cadence.
/// </summary>
internal sealed class ShadowCreatureSfxDetachedVoice : IDisposable
{
    private readonly IShadowCreatureSfxInstance instance;
    private bool disposed;

    internal ShadowCreatureSfxDetachedVoice(IShadowCreatureSfxInstance instance)
    {
        this.instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    internal bool Tick(ShadowCreatureSfxSpatial spatial, float soundVolume)
    {
        if (disposed)
            return false;

        try
        {
            if (instance.State == ShadowCreatureSfxPlaybackState.Stopped)
            {
                Dispose();
                return false;
            }
            if (!spatial.IsInAudibleRadius)
            {
                StopAndDispose();
                return false;
            }
            instance.Volume = Clamp(soundVolume * spatial.DistanceFactor);
            instance.Pan = spatial.Pan;
            return true;
        }
        catch
        {
            StopAndDispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            if (instance.State != ShadowCreatureSfxPlaybackState.Stopped)
                instance.Stop();
        }
        catch { }
        try { instance.Dispose(); } catch { }
    }

    private void StopAndDispose() => Dispose();

    private static float Clamp(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        return Math.Clamp(value, 0f, 1f);
    }
}

/// <summary>
/// One owner voice lane. It owns every SoundEffectInstance it creates and allows event voices for
/// the same shadow to overlap. A voice is stopped only when it naturally ends, leaves the audible
/// radius, or the owner is explicitly cleaned up.
/// </summary>
internal sealed class ShadowCreatureSfxOwnerLane : IDisposable
{
    private const int MaximumCreatedInstanceHistory = 64;
    private readonly Dictionary<ShadowCreatureSfxCue, IShadowCreatureSfxEffect[]> pools;
    private readonly IShadowCreatureSfxRandom random;
    private readonly Action<ShadowCreatureSfxPlaybackStarted>? playbackStarted;
    private readonly Dictionary<ShadowCreatureSfxCue, int> previousIndexes = new();
    private readonly HashSet<string> consumedDeduplicationKeys = new(StringComparer.Ordinal);
    private sealed class ActiveVoice
    {
        internal ActiveVoice(ShadowCreatureSfxCue cue, IShadowCreatureSfxInstance instance)
        {
            Cue = cue;
            Instance = instance;
        }

        internal ShadowCreatureSfxCue Cue { get; }

        internal IShadowCreatureSfxInstance Instance { get; }
    }

    private readonly List<ActiveVoice> activeVoices = new();
    private bool disposed;
    private readonly List<IShadowCreatureSfxInstance> createdInstances = new();
    private int totalCreatedInstanceCount;

    internal ShadowCreatureSfxOwnerLane(
        ShadowCreatureSfxOwnerKey owner,
        ShadowCreatureSpecies species,
        IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>> pools,
        IShadowCreatureSfxRandom random,
        Action<ShadowCreatureSfxPlaybackStarted>? playbackStarted = null
    )
    {
        Owner = owner;
        Species = species;
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.playbackStarted = playbackStarted;
        this.pools = new Dictionary<ShadowCreatureSfxCue, IShadowCreatureSfxEffect[]>();
        foreach (var pair in pools ?? throw new ArgumentNullException(nameof(pools)))
        {
            if (!ShadowCreatureSfxPolicy.IsCueAvailableForSpecies(species, pair.Key))
                throw new ArgumentException("The pool is not valid for the selected species.", nameof(pools));
            if (pair.Value is null || pair.Value.Count == 0)
                continue;
            var copy = new IShadowCreatureSfxEffect[pair.Value.Count];
            for (var index = 0; index < pair.Value.Count; index++)
                copy[index] = pair.Value[index]
                    ?? throw new ArgumentException("A sound pool cannot contain null effects.", nameof(pools));
            this.pools.Add(pair.Key, copy);
        }
    }

    internal ShadowCreatureSfxOwnerKey Owner { get; }

    internal ShadowCreatureSpecies Species { get; }

    internal ShadowCreatureSfxCue? CurrentCue
    {
        get
        {
            ReapStopped();
            return activeVoices.Count == 0 ? null : activeVoices[^1].Cue;
        }
    }

    /// <summary>Reaps naturally finished instances before reporting lane occupancy.</summary>
    internal bool HasActiveInstance
    {
        get
        {
            if (disposed)
                return false;
            ReapStopped();
            return activeVoices.Count > 0;
        }
    }

    /// <summary>Chase may wait on this owner only while an event voice is still active.</summary>
    internal bool HasActiveNonChaseVoice
    {
        get
        {
            if (disposed)
                return false;
            ReapStopped();
            foreach (var voice in activeVoices)
            {
                if (voice.Cue != ShadowCreatureSfxCue.Chase)
                    return true;
            }
            return false;
        }
    }

    internal int PhysicalInstanceCount
    {
        get
        {
            ReapStopped();
            return activeVoices.Count;
        }
    }

    internal IReadOnlyList<IShadowCreatureSfxInstance> CreatedInstances => createdInstances;

    internal int TotalCreatedInstanceCount => totalCreatedInstanceCount;

    /// <summary>
    /// Removes active voices of the requested cue without stopping or disposing them. This is used
    /// only for a confirmed Death voice which must outlive normal owner cleanup.
    /// </summary>
    internal IReadOnlyList<ShadowCreatureSfxDetachedVoice> Detach(ShadowCreatureSfxCue cue)
    {
        if (disposed)
            return Array.Empty<ShadowCreatureSfxDetachedVoice>();

        ReapStopped();
        var detached = new List<ShadowCreatureSfxDetachedVoice>();
        for (var index = activeVoices.Count - 1; index >= 0; index--)
        {
            if (activeVoices[index].Cue != cue)
                continue;
            var instance = activeVoices[index].Instance;
            activeVoices.RemoveAt(index);
            detached.Add(new ShadowCreatureSfxDetachedVoice(instance));
        }
        return detached;
    }

    /// <summary>
    /// Removes every active voice without stopping or disposing it. This is reserved for a harmless
    /// projection which has completed its visual fade; hard cleanup must continue using Dispose.
    /// </summary>
    internal IReadOnlyList<ShadowCreatureSfxDetachedVoice> DetachAll()
    {
        if (disposed)
            return Array.Empty<ShadowCreatureSfxDetachedVoice>();

        ReapStopped();
        if (activeVoices.Count == 0)
            return Array.Empty<ShadowCreatureSfxDetachedVoice>();

        var detached = new List<ShadowCreatureSfxDetachedVoice>(activeVoices.Count);
        foreach (var voice in activeVoices)
            detached.Add(new ShadowCreatureSfxDetachedVoice(voice.Instance));
        activeVoices.Clear();
        return detached;
    }

    internal ShadowCreatureSfxRequestResult Request(
        ShadowCreatureSfxCue cue,
        string deduplicationKey,
        ShadowCreatureSfxSpatial spatial
    )
    {
        if (disposed)
            return Result(cue, ShadowCreatureSfxRequestStatus.Failed, "lane-disposed");
        if (!ShadowCreatureSfxPolicy.IsCueAvailableForSpecies(Species, cue))
            return Result(cue, ShadowCreatureSfxRequestStatus.MissingPool, "cue-not-available-for-species");
        if (string.IsNullOrWhiteSpace(deduplicationKey))
            return Result(cue, ShadowCreatureSfxRequestStatus.Failed, "deduplication-key-missing");
        if (consumedDeduplicationKeys.Contains(deduplicationKey))
            return Result(cue, ShadowCreatureSfxRequestStatus.DroppedDuplicate, "deduplication-key-consumed");

        ReapStopped();
        if (!spatial.IsAudible || spatial.Volume <= 0f)
            return Result(cue, ShadowCreatureSfxRequestStatus.SkippedSilent, "outside-audible-radius-or-muted");
        if (!pools.TryGetValue(cue, out var pool) || pool.Length == 0)
        {
            consumedDeduplicationKeys.Add(deduplicationKey);
            return Result(cue, ShadowCreatureSfxRequestStatus.MissingPool, "cue-pool-missing");
        }

        var index = SelectIndex(cue, pool.Length);
        IShadowCreatureSfxInstance? created = null;
        try
        {
            created = pool[index].CreateInstance();
            if (created is null)
                return Result(cue, ShadowCreatureSfxRequestStatus.Failed, "instance-missing");
            created.IsLooped = false;
            created.Volume = spatial.Volume;
            created.Pan = spatial.Pan;
            created.Play();
            activeVoices.Add(new ActiveVoice(cue, created));
            consumedDeduplicationKeys.Add(deduplicationKey);
            totalCreatedInstanceCount++;
            if (createdInstances.Count == MaximumCreatedInstanceHistory)
                createdInstances.RemoveAt(0);
            createdInstances.Add(created);
        }
        catch (Exception exception)
        {
            if (created is not null)
            {
                try { created.Dispose(); } catch { }
            }
            consumedDeduplicationKeys.Add(deduplicationKey);
            return Result(cue, ShadowCreatureSfxRequestStatus.Failed, exception.GetType().Name);
        }
        playbackStarted?.Invoke(new ShadowCreatureSfxPlaybackStarted(
            Owner,
            Species,
            cue,
            spatial,
            deduplicationKey
        ));
        return Result(cue, ShadowCreatureSfxRequestStatus.Started, "started");
    }

    internal void Tick(ShadowCreatureSfxSpatial spatial, float soundVolume)
    {
        if (disposed)
            return;
        ReapStopped();
        if (activeVoices.Count == 0)
            return;
        if (!spatial.IsInAudibleRadius)
        {
            StopAndDisposeAll();
            return;
        }
        for (var index = activeVoices.Count - 1; index >= 0; index--)
        {
            var voice = activeVoices[index];
            try
            {
                voice.Instance.Volume = Clamp(soundVolume * spatial.DistanceFactor);
                voice.Instance.Pan = spatial.Pan;
            }
            catch
            {
                StopAndDisposeAt(index);
            }
        }
    }

    internal void Stop()
    {
        if (disposed)
            return;
        StopAndDisposeAll();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        StopAndDisposeAll();
        disposed = true;
    }

    private int SelectIndex(ShadowCreatureSfxCue cue, int length)
    {
        var candidate = random.NextIndex(length);
        if (candidate < 0 || candidate >= length)
            candidate = 0;
        if (length > 1 && previousIndexes.TryGetValue(cue, out var previous) && candidate == previous)
            candidate = (candidate + 1) % length;
        previousIndexes[cue] = candidate;
        return candidate;
    }

    private void ReapStopped()
    {
        for (var index = activeVoices.Count - 1; index >= 0; index--)
        {
            try
            {
                if (activeVoices[index].Instance.State != ShadowCreatureSfxPlaybackState.Stopped)
                    continue;
            }
            catch
            {
                // A broken instance cannot be kept alive in the lane.
            }
            StopAndDisposeAt(index);
        }
    }

    private void StopAndDisposeAll()
    {
        for (var index = activeVoices.Count - 1; index >= 0; index--)
            StopAndDisposeAt(index);
    }

    private void StopAndDisposeAt(int index)
    {
        var instance = activeVoices[index].Instance;
        activeVoices.RemoveAt(index);
        try
        {
            if (instance.State != ShadowCreatureSfxPlaybackState.Stopped)
                instance.Stop();
        }
        catch { }
        try { instance.Dispose(); } catch { }
    }

    private static float Clamp(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        return Math.Clamp(value, 0f, 1f);
    }

    private static ShadowCreatureSfxRequestResult Result(
        ShadowCreatureSfxCue cue,
        ShadowCreatureSfxRequestStatus status,
        string reason
    ) => new(status, cue, reason);
}
