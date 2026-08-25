#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// One owner voice lane. It owns SoundEffectInstance objects, borrows effects, and never queues a
/// lower-priority request behind a currently playing voice.
/// </summary>
internal sealed class ShadowCreatureSfxOwnerLane : IDisposable
{
    private const int MaximumCreatedInstanceHistory = 64;
    private readonly Dictionary<ShadowCreatureSfxCue, IShadowCreatureSfxEffect[]> pools;
    private readonly IShadowCreatureSfxRandom random;
    private readonly Dictionary<ShadowCreatureSfxCue, int> previousIndexes = new();
    private readonly HashSet<string> consumedDeduplicationKeys = new(StringComparer.Ordinal);
    private IShadowCreatureSfxInstance? current;
    private ShadowCreatureSfxCue? currentCue;
    private int currentPriority;
    private bool disposed;
    private readonly List<IShadowCreatureSfxInstance> createdInstances = new();
    private int totalCreatedInstanceCount;

    internal ShadowCreatureSfxOwnerLane(
        ShadowCreatureSfxOwnerKey owner,
        ShadowCreatureSpecies species,
        IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>> pools,
        IShadowCreatureSfxRandom random
    )
    {
        Owner = owner;
        Species = species;
        this.random = random ?? throw new ArgumentNullException(nameof(random));
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

    internal ShadowCreatureSfxCue? CurrentCue => currentCue;

    /// <summary>Reaps a naturally finished instance before reporting lane occupancy.</summary>
    internal bool HasActiveInstance
    {
        get
        {
            if (disposed)
                return false;
            ReapStopped();
            return current is not null;
        }
    }

    internal int PhysicalInstanceCount => current is null ? 0 : 1;

    internal IReadOnlyList<IShadowCreatureSfxInstance> CreatedInstances => createdInstances;

    internal int TotalCreatedInstanceCount => totalCreatedInstanceCount;

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
        var priority = ShadowCreatureSfxPolicy.Priority(cue);
        if (current is not null && priority <= currentPriority)
        {
            consumedDeduplicationKeys.Add(deduplicationKey);
            return Result(cue, ShadowCreatureSfxRequestStatus.DroppedPriority, "current-voice-has-equal-or-higher-priority");
        }
        if (!spatial.IsAudible || spatial.Volume <= 0f)
            return Result(cue, ShadowCreatureSfxRequestStatus.SkippedSilent, "outside-audible-radius-or-muted");
        if (!pools.TryGetValue(cue, out var pool) || pool.Length == 0)
        {
            consumedDeduplicationKeys.Add(deduplicationKey);
            return Result(cue, ShadowCreatureSfxRequestStatus.MissingPool, "cue-pool-missing");
        }

        if (current is not null)
            StopAndDisposeCurrent();

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
            current = created;
            currentCue = cue;
            currentPriority = priority;
            consumedDeduplicationKeys.Add(deduplicationKey);
            totalCreatedInstanceCount++;
            if (createdInstances.Count == MaximumCreatedInstanceHistory)
                createdInstances.RemoveAt(0);
            createdInstances.Add(created);
            return Result(cue, ShadowCreatureSfxRequestStatus.Started, "started");
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
    }

    internal void Tick(ShadowCreatureSfxSpatial spatial, float soundVolume)
    {
        if (disposed)
            return;
        ReapStopped();
        if (current is null)
            return;
        if (!spatial.IsInAudibleRadius)
        {
            StopAndDisposeCurrent();
            return;
        }
        try
        {
            current.Volume = Clamp(soundVolume * spatial.DistanceFactor);
            current.Pan = spatial.Pan;
        }
        catch
        {
            StopAndDisposeCurrent();
        }
    }

    internal void Stop()
    {
        if (disposed)
            return;
        StopAndDisposeCurrent();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        StopAndDisposeCurrent();
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
        if (current is null || current.State != ShadowCreatureSfxPlaybackState.Stopped)
            return;
        StopAndDisposeCurrent();
    }

    private void StopAndDisposeCurrent()
    {
        var instance = current;
        current = null;
        currentCue = null;
        currentPriority = 0;
        if (instance is null)
            return;
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
