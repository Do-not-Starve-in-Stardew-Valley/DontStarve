#nullable enable

using System;
using System.Collections.Generic;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Owner-local read-only environment-light facade. It owns cadence and cache state but exposes no
/// damage, countdown, Sanity mutation, or world mutation operation.
/// </summary>
internal sealed class EnvironmentLightService
{
    private readonly IEnvironmentLightSnapshotProvider snapshotProvider;
    private readonly EnvironmentLightClassifier classifier;
    private readonly EnvironmentLightCache cache = new();
    private readonly Func<long> tickProvider;
    private readonly HashSet<OwnerScreenKey> invalidatedOwners = new();

    internal event Action<EnvironmentLightDiagnostic>? DiagnosticCaptured;

    internal EnvironmentLightService(
        IEnvironmentLightSnapshotProvider snapshotProvider,
        EnvironmentLightClassifier classifier,
        Func<long> tickProvider
    )
    {
        this.snapshotProvider = snapshotProvider
            ?? throw new ArgumentNullException(nameof(snapshotProvider));
        this.classifier = classifier
            ?? throw new ArgumentNullException(nameof(classifier));
        this.tickProvider = tickProvider
            ?? throw new ArgumentNullException(nameof(tickProvider));
        if (snapshotProvider is IEnvironmentLightSnapshotInvalidationSource source)
            source.SnapshotInvalidated += OnSnapshotInvalidated;
    }

    internal EnvironmentLightResult Evaluate(
        Farmer owner,
        int screenId,
        GameLocation location,
        long gameMinute
    )
    {
        var currentTick = GetCurrentTick();
        if (!TryCreateKey(owner, screenId, location, out var key))
        {
            return EnvironmentLightResult.Fallback(
                EnvironmentLightReasonIds.SnapshotInvalid,
                gameMinute
            );
        }
        var ownerScreen = new OwnerScreenKey(key.PlayerKey, key.ScreenId);
        var samplerInvalidated = invalidatedOwners.Remove(ownerScreen);
        cache.TryGetLatest(key, out var previous);
        if (
            !samplerInvalidated
            && cache.TryGetFresh(key, currentTick, out var cached)
        )
        {
            return cached.Result;
        }

        EnvironmentLightSnapshotCaptureResult capture;
        try
        {
            capture = snapshotProvider.Capture(
                owner,
                screenId,
                location,
                gameMinute
            );
        }
        catch (Exception)
        {
            capture = EnvironmentLightSnapshotCaptureResult.Failed(
                EnvironmentLightCapabilityStatus.Invalid,
                "environment-light.snapshot-provider-failed"
            );
        }

        if (!capture.Success || capture.Snapshot is null)
        {
            var reason = string.IsNullOrWhiteSpace(capture.Reason)
                ? EnvironmentLightReasonIds.SnapshotUnavailable
                : capture.Reason;
            var fallback = EnvironmentLightResult.Fallback(reason, gameMinute);
            var diagnostic = EnvironmentLightDiagnostic.CaptureFailure(
                key.PlayerKey,
                key.ScreenId,
                key.LocationNameOrUniqueName,
                key.LocationInstanceId,
                gameMinute,
                currentTick,
                reason
            );
            cache.Store(key, 0, currentTick, fallback, diagnostic);
            DiagnosticCaptured?.Invoke(diagnostic);
            return fallback;
        }

        var snapshot = capture.Snapshot;
        if (!Matches(key, snapshot))
        {
            var fallback = EnvironmentLightResult.Fallback(
                EnvironmentLightReasonIds.SnapshotInvalid,
                gameMinute
            );
            var diagnostic = EnvironmentLightDiagnostic.CaptureFailure(
                key.PlayerKey,
                key.ScreenId,
                key.LocationNameOrUniqueName,
                key.LocationInstanceId,
                gameMinute,
                currentTick,
                fallback.Reason
            );
            cache.Store(key, 0, currentTick, fallback, diagnostic);
            DiagnosticCaptured?.Invoke(diagnostic);
            return fallback;
        }

        EnvironmentLightResult result;
        try
        {
            result = classifier.Classify(snapshot, previous?.Result.Level);
        }
        catch (Exception)
        {
            result = EnvironmentLightResult.Fallback(
                "environment-light.classifier-failed",
                snapshot.CapturedAtMinute
            );
        }
        var capturedDiagnostic = EnvironmentLightDiagnostic.From(
            snapshot,
            result
        );
        cache.Store(
            key,
            snapshot.Revision,
            currentTick,
            result,
            capturedDiagnostic
        );
        DiagnosticCaptured?.Invoke(capturedDiagnostic);
        return result;
    }

    internal bool TryGetDiagnostic(
        string playerKey,
        int screenId,
        out EnvironmentLightDiagnostic diagnostic
    )
    {
        return cache.TryGetDiagnostic(playerKey, screenId, out diagnostic);
    }

    internal void Invalidate(
        string playerKey,
        int screenId,
        EnvironmentLightInvalidationReason reason
    )
    {
        _ = reason;
        invalidatedOwners.Remove(new OwnerScreenKey(playerKey, screenId));
        cache.Invalidate(playerKey, screenId);
    }

    internal void Clear()
    {
        cache.Clear();
        invalidatedOwners.Clear();
    }

    private long GetCurrentTick()
    {
        try
        {
            return tickProvider();
        }
        catch (Exception)
        {
            // A failed clock forces the cache stale on every call without manufacturing evidence.
            return long.MinValue;
        }
    }

    private void OnSnapshotInvalidated(string playerKey, int screenId)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey) || screenId < 0)
            return;
        var key = new OwnerScreenKey(playerKey, screenId);
        if (
            invalidatedOwners.Count >= EnvironmentLightCache.MaximumEntries
            && !invalidatedOwners.Contains(key)
        )
        {
            return;
        }
        invalidatedOwners.Add(key);
    }

    private static bool TryCreateKey(
        Farmer? owner,
        int screenId,
        GameLocation? location,
        out EnvironmentLightCacheKey key
    )
    {
        if (owner is null || location is null || screenId < 0)
        {
            key = default;
            return false;
        }
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            owner.UniqueMultiplayerID
        );
        var locationName = location.NameOrUniqueName;
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || string.IsNullOrWhiteSpace(locationName)
        )
        {
            key = default;
            return false;
        }

        key = new EnvironmentLightCacheKey(
            playerKey,
            screenId,
            locationName,
            EnvironmentLightLocationIdentity.Get(location)
        );
        return true;
    }

    private static bool Matches(
        EnvironmentLightCacheKey key,
        EnvironmentLightSnapshot snapshot
    )
    {
        return string.Equals(
                key.PlayerKey,
                snapshot.PlayerKey,
                StringComparison.Ordinal
            )
            && key.ScreenId == snapshot.ScreenId
            && string.Equals(
                key.LocationNameOrUniqueName,
                snapshot.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
            && key.LocationInstanceId == snapshot.LocationInstanceId;
    }

    private readonly record struct OwnerScreenKey(string PlayerKey, int ScreenId);
}
