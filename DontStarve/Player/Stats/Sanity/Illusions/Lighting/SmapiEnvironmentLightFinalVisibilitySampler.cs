#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Samples the already-rendered Stardew lightmap at the current owner's feet. The readback is a
/// fixed 2x2 texel region, is cadence-bound, and reuses one buffer; no full lightmap or new render
/// target is created. RenderedWorld is used because World_RenderLightmap fires before SpriteBatch
/// is flushed and while the lightmap is still bound.
/// </summary>
internal sealed class SmapiEnvironmentLightFinalVisibilitySampler
    : IEnvironmentLightFinalVisibilityProvider
{
    private const int MaximumLoggedReasons = 16;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Dictionary<SampleKey, SampleEntry> samples = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private readonly Color[] readback = new Color[
        EnvironmentLightProductionContract.ReadbackWidth
            * EnvironmentLightProductionContract.ReadbackHeight
    ];
    private long rendererRevision;
    private long storedSequence;
    private long lastCleanupTick = long.MinValue;

    internal SmapiEnvironmentLightFinalVisibilitySampler(
        IModHelper helper,
        IMonitor monitor
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Display.WindowResized += OnWindowResized;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    public event Action<string, int>? SampleUpdated;

    public EnvironmentLightFinalVisibilitySnapshot GetLatest(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        long currentTick
    )
    {
        var key = new SampleKey(
            playerKey,
            screenId,
            locationNameOrUniqueName,
            locationInstanceId
        );
        if (!samples.TryGetValue(key, out var entry))
        {
            return Unavailable(
                key,
                currentTick,
                EnvironmentLightReasonIds.FinalVisibilityUnavailable
            );
        }
        if (
            currentTick < entry.Sample.CapturedAtTick
            || currentTick - entry.Sample.CapturedAtTick
                > EnvironmentLightProductionContract.MaximumSampleAgeTicks
        )
        {
            return Unavailable(
                key,
                currentTick,
                EnvironmentLightReasonIds.FinalVisibilityStale
            );
        }
        return entry.Sample;
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        _ = sender;
        _ = e;
        if (
            !Context.IsWorldReady
            || !string.Equals(
                Game1.version,
                EnvironmentLightProductionContract.SupportedGameVersion,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        var owner = Game1.player;
        var location = Game1.currentLocation;
        var screenId = Context.ScreenId;
        if (
            owner is null
            || location is null
            || !owner.IsLocalPlayer
            || screenId < 0
            || !Context.HasScreenId(screenId)
        )
        {
            return;
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
            return;
        }

        var currentTick = Math.Max(0L, Game1.ticks);
        CleanupInvalidScreens(currentTick);
        var key = new SampleKey(
            playerKey,
            screenId,
            locationName,
            EnvironmentLightLocationIdentity.Get(location)
        );
        var standing = owner.StandingPixel;
        var lightCount = Game1.currentLightSources?.Count ?? -1;
        var baseSignature = ComputeBaseSignature(location);
        if (
            samples.TryGetValue(key, out var previous)
            && !ShouldSample(
                previous,
                currentTick,
                standing,
                lightCount,
                baseSignature
            )
        )
        {
            return;
        }

        EnvironmentLightFinalVisibilitySnapshot sample;
        try
        {
            sample = Capture(
                key,
                location,
                standing,
                currentTick,
                NextRendererRevision()
            );
        }
        catch (Exception exception)
        {
            var reason =
                $"environment-light.final-visibility-readback-failed:{exception.GetType().Name}";
            sample = Unavailable(
                key,
                currentTick,
                reason,
                EnvironmentLightCapabilityStatus.Invalid
            );
            LogOnce(
                reason,
                $"Environment-light owner-foot readback failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }

        Store(
            key,
            new SampleEntry(
                sample,
                standing,
                lightCount,
                baseSignature,
                ++storedSequence
            )
        );
        SampleUpdated?.Invoke(playerKey, screenId);
    }

    private EnvironmentLightFinalVisibilitySnapshot Capture(
        SampleKey key,
        GameLocation location,
        Point standing,
        long currentTick,
        long revision
    )
    {
        var quality = Game1.options?.lightingQuality ?? 0;
        var zoom = Game1.options?.zoomLevel ?? 0f;
        var useUnscaled = Game1.game1?.useUnscaledLighting == true;
        if (quality < 2 || quality % 2 != 0 || !float.IsFinite(zoom) || zoom <= 0f)
        {
            return Unavailable(
                key,
                currentTick,
                "environment-light.final-visibility-renderer-settings-invalid",
                EnvironmentLightCapabilityStatus.Invalid
            );
        }

        var raining = location.IsOutdoors && location.IsRainingHere();
        if (!Game1.drawLighting)
        {
            const double fullyVisible = 1d;
            return EnvironmentLightFinalVisibilitySnapshot.Confirmed(
                key.PlayerKey,
                key.ScreenId,
                key.LocationNameOrUniqueName,
                key.LocationInstanceId,
                fullyVisible,
                0d,
                0d,
                0d,
                standardLightingDrawn: false,
                rainOverlayApplied: false,
                quality,
                zoom,
                useUnscaled,
                currentTick,
                revision,
                "environment-light.final-visibility-no-lighting-overlay"
            );
        }

        var lightmap = Game1.lightmap;
        if (
            lightmap is null
            || lightmap.IsDisposed
            || lightmap.IsContentLost
            || lightmap.Format != SurfaceFormat.Color
            || lightmap.Width < EnvironmentLightProductionContract.ReadbackWidth
            || lightmap.Height < EnvironmentLightProductionContract.ReadbackHeight
        )
        {
            return Unavailable(
                key,
                currentTick,
                "environment-light.final-visibility-lightmap-unavailable"
            );
        }

        var local = Game1.GlobalToLocal(
            Game1.viewport,
            new Vector2(standing.X, standing.Y)
        );
        var outputScale = quality / 2d;
        if (useUnscaled)
            outputScale /= zoom;
        if (
            !double.IsFinite(outputScale)
            || outputScale <= 0d
            || !float.IsFinite(local.X)
            || !float.IsFinite(local.Y)
        )
        {
            return Unavailable(
                key,
                currentTick,
                "environment-light.final-visibility-coordinate-invalid",
                EnvironmentLightCapabilityStatus.Invalid
            );
        }

        // SpriteBatch's scaled LinearClamp draw maps a destination pixel center to source texel
        // space with a half-texel offset. Read the four neighbors and reproduce that interpolation.
        var sourceX = (local.X / outputScale) - 0.5d;
        var sourceY = (local.Y / outputScale) - 0.5d;
        var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, lightmap.Width - 2);
        var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, lightmap.Height - 2);
        var fractionX = Math.Clamp(sourceX - x0, 0d, 1d);
        var fractionY = Math.Clamp(sourceY - y0, 0d, 1d);
        var region = new Rectangle(
            x0,
            y0,
            EnvironmentLightProductionContract.ReadbackWidth,
            EnvironmentLightProductionContract.ReadbackHeight
        );
        lightmap.GetData(0, region, readback, 0, readback.Length);

        var sampled = EnvironmentLightVisibilityMath.Interpolate(
            ToSnapshotColor(readback[0]),
            ToSnapshotColor(readback[1]),
            ToSnapshotColor(readback[2]),
            ToSnapshotColor(readback[3]),
            fractionX,
            fractionY
        );
        var red = sampled.R / 255d;
        var green = sampled.G / 255d;
        var blue = sampled.B / 255d;
        var score = EnvironmentLightVisibilityMath.ComputeVisibilityScore(
            red,
            green,
            blue,
            raining
        );
        return EnvironmentLightFinalVisibilitySnapshot.Confirmed(
            key.PlayerKey,
            key.ScreenId,
            key.LocationNameOrUniqueName,
            key.LocationInstanceId,
            score,
            red,
            green,
            blue,
            standardLightingDrawn: true,
            rainOverlayApplied: raining,
            quality,
            zoom,
            useUnscaled,
            currentTick,
            revision,
            "environment-light.final-visibility-owner-foot-lightmap"
        );
    }

    private static bool ShouldSample(
        SampleEntry previous,
        long currentTick,
        Point standing,
        int lightCount,
        int baseSignature
    )
    {
        if (currentTick < previous.Sample.CapturedAtTick)
            return true;
        if (
            lightCount != previous.LightCount
            || baseSignature != previous.BaseSignature
        )
        {
            return true;
        }
        var dangerous =
            previous.Sample.IsConfirmed
            && previous.Sample.VisibilityScore
                <= EnvironmentLightThresholds.Default.PitchBlackExit;
        var cadence = dangerous
            ? EnvironmentLightProductionContract.DangerousSampleCadenceTicks
            : EnvironmentLightProductionContract.NormalSampleCadenceTicks;
        if (currentTick - previous.Sample.CapturedAtTick >= cadence)
            return true;

        // While danger is active, owner movement gets the short cadence so entering a torch or
        // machine-light radius releases the claim on the next bounded sample.
        if (!dangerous)
            return false;
        var moved =
            Math.Abs(standing.X - previous.StandingPixel.X)
                + Math.Abs(standing.Y - previous.StandingPixel.Y);
        return moved >= 16
            && currentTick - previous.Sample.CapturedAtTick >= 2;
    }

    private static int ComputeBaseSignature(GameLocation location)
    {
        var hash = new HashCode();
        hash.Add(Game1.drawLighting);
        hash.Add(Game1.ambientLight.PackedValue);
        hash.Add(Game1.outdoorLight.PackedValue);
        hash.Add(location.LightLevel);
        hash.Add(location.IsOutdoors);
        hash.Add(location.IsRainingHere());
        hash.Add(Game1.options?.lightingQuality ?? 0);
        hash.Add(Game1.options?.zoomLevel ?? 0f);
        hash.Add(Game1.game1?.useUnscaledLighting == true);
        return hash.ToHashCode();
    }

    private void Store(SampleKey key, SampleEntry entry)
    {
        if (
            !samples.ContainsKey(key)
            && samples.Count >= EnvironmentLightProductionContract.MaximumScreens
        )
        {
            RemoveOldest();
        }
        samples[key] = entry;
    }

    private void CleanupInvalidScreens(long currentTick)
    {
        if (
            lastCleanupTick != long.MinValue
            && currentTick >= lastCleanupTick
            && currentTick - lastCleanupTick < 60
        )
        {
            return;
        }
        lastCleanupTick = currentTick;
        List<SampleKey>? removals = null;
        foreach (var key in samples.Keys)
        {
            if (Context.HasScreenId(key.ScreenId))
                continue;
            removals ??= new List<SampleKey>();
            removals.Add(key);
        }
        if (removals is null)
            return;
        foreach (var key in removals)
        {
            samples.Remove(key);
            SampleUpdated?.Invoke(key.PlayerKey, key.ScreenId);
        }
    }

    private void RemoveOldest()
    {
        var found = false;
        var oldest = default(SampleKey);
        var sequence = long.MaxValue;
        foreach (var pair in samples)
        {
            if (pair.Value.StoredSequence >= sequence)
                continue;
            found = true;
            oldest = pair.Key;
            sequence = pair.Value.StoredSequence;
        }
        if (found)
        {
            samples.Remove(oldest);
            SampleUpdated?.Invoke(oldest.PlayerKey, oldest.ScreenId);
        }
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        _ = sender;
        _ = e;
        Clear();
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        _ = sender;
        _ = e;
        RemoveScreen(Context.ScreenId);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _ = sender;
        _ = e;
        Clear();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _ = sender;
        _ = e;
        Clear();
    }

    private void RemoveScreen(int screenId)
    {
        List<SampleKey>? removals = null;
        foreach (var key in samples.Keys)
        {
            if (key.ScreenId != screenId)
                continue;
            removals ??= new List<SampleKey>();
            removals.Add(key);
        }
        if (removals is null)
            return;
        foreach (var key in removals)
        {
            samples.Remove(key);
            SampleUpdated?.Invoke(key.PlayerKey, key.ScreenId);
        }
    }

    private void Clear()
    {
        SampleKey[]? invalidated = null;
        if (samples.Count > 0)
        {
            invalidated = new SampleKey[samples.Count];
            samples.Keys.CopyTo(invalidated, 0);
        }
        samples.Clear();
        loggedReasons.Clear();
        lastCleanupTick = long.MinValue;
        if (invalidated is null)
            return;
        foreach (var key in invalidated)
            SampleUpdated?.Invoke(key.PlayerKey, key.ScreenId);
    }

    private long NextRendererRevision()
    {
        rendererRevision =
            rendererRevision == long.MaxValue ? 1L : rendererRevision + 1L;
        return rendererRevision;
    }

    private void LogOnce(string reason, string message)
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || loggedReasons.Count >= MaximumLoggedReasons
            || !loggedReasons.Add(reason)
        )
        {
            return;
        }
        monitor.Log(message, LogLevel.Warn);
    }

    private static EnvironmentLightColor ToSnapshotColor(Color color)
    {
        return new EnvironmentLightColor(color.R, color.G, color.B, color.A);
    }

    private static EnvironmentLightFinalVisibilitySnapshot Unavailable(
        SampleKey key,
        long currentTick,
        string reason,
        EnvironmentLightCapabilityStatus status =
            EnvironmentLightCapabilityStatus.Unavailable
    )
    {
        return EnvironmentLightFinalVisibilitySnapshot.Unavailable(
            key.PlayerKey,
            key.ScreenId,
            key.LocationNameOrUniqueName,
            key.LocationInstanceId,
            Math.Max(0L, currentTick),
            reason,
            status
        );
    }

    private readonly record struct SampleKey(
        string PlayerKey,
        int ScreenId,
        string LocationNameOrUniqueName,
        long LocationInstanceId
    );

    private sealed record SampleEntry(
        EnvironmentLightFinalVisibilitySnapshot Sample,
        Point StandingPixel,
        int LightCount,
        int BaseSignature,
        long StoredSequence
    );
}
