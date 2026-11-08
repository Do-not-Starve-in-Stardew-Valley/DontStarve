#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;

namespace DontStarve.Music;

/// <summary>
/// Caches shared read-only SoundEffect objects. It never caches or returns a shared
/// SoundEffectInstance: each caller owns the instance it creates.
/// </summary>
internal sealed class ActivityMusicResourceRuntime : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Dictionary<string, SoundEffect> effects = new(StringComparer.Ordinal);
    private readonly HashSet<string> diagnostics = new(StringComparer.Ordinal);
    private bool disposed;

    internal ActivityMusicResourceRuntime(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    /// <summary>
    /// Decodes every registered physical activity track while the mod is starting. Playback still
    /// creates owner-local instances later, but the expensive file read/Vorbis decode never occurs
    /// on the first in-game action. Multiple logical entries sharing one file are decoded once.
    /// </summary>
    internal (int Loaded, int Failed) PreloadAll()
    {
        if (disposed)
            return (0, 0);

        var loaded = 0;
        var failed = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ActivityMusicResourceCatalog.PhysicalEntries)
        {
            if (!paths.Add(entry.RelativePath))
                continue;

            if (TryLoadEffect(entry, out _))
                loaded++;
            else
                failed++;
        }

        return (loaded, failed);
    }

    internal bool TryCreateInstance(
        string trackKey,
        out SoundEffectInstance? instance
    )
    {
        instance = null;
        if (disposed)
            return false;

        if (!ActivityMusicResourceCatalog.TryResolve(trackKey, out var entry, out _)
            || entry is null)
        {
            LogOnce(
                $"missing-registration:{trackKey}",
                $"Activity music track is not registered and will remain silent: {trackKey}"
            );
            return false;
        }

        if (!ActivityMusicResourceCatalog.IsSafeRelativePath(entry.RelativePath))
        {
            LogOnce(
                $"unsafe-path:{entry.TrackKey}",
                $"Activity music registration has an unsafe path and will remain silent: {entry.TrackKey} -> {entry.RelativePath}"
            );
            return false;
        }

        if (!TryLoadEffect(entry, out var soundEffect) || soundEffect is null)
            return false;

        try
        {
            instance = soundEffect.CreateInstance();
            return true;
        }
        catch (Exception ex)
        {
            LogOnce(
                $"instance-failed:{entry.TrackKey}:{ex.GetType().Name}",
                $"Activity music instance could not be created; no replacement will be used: {entry.RelativePath} ({ex.Message})"
            );
            return false;
        }
    }

    private bool TryLoadEffect(
        ActivityMusicResourceEntry entry,
        out SoundEffect? soundEffect
    )
    {
        soundEffect = null;
        if (effects.TryGetValue(entry.RelativePath, out soundEffect))
            return true;

        var path = Path.Combine(
            helper.DirectoryPath,
            entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        if (!File.Exists(path))
        {
            LogOnce(
                $"missing-file:{entry.TrackKey}:{entry.RelativePath}",
                $"Activity music file is missing; no replacement will be used: {entry.RelativePath}"
            );
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            soundEffect = SoundEffect.FromStream(stream, true);
            effects.Add(entry.RelativePath, soundEffect);
            return true;
        }
        catch (Exception ex)
        {
            LogOnce(
                $"decode-failed:{entry.TrackKey}:{ex.GetType().Name}",
                $"Activity music file could not be decoded; no replacement will be used: {entry.RelativePath} ({ex.Message})"
            );
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var effect in effects.Values)
        {
            try
            {
                effect.Dispose();
            }
            catch (Exception ex)
            {
                monitor.Log(
                    $"Failed to dispose activity music resource: {ex.Message}",
                    LogLevel.Warn
                );
            }
        }

        effects.Clear();
        diagnostics.Clear();
    }

    private void LogOnce(string key, string message)
    {
        if (diagnostics.Add(key))
            monitor.Log(message, LogLevel.Warn);
    }
}
