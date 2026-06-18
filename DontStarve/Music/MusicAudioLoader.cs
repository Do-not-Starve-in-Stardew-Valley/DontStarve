using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;

namespace DontStarve.Music;

internal static class MusicAudioLoader
{
    internal static SoundEffectInstance LoadInstance(
        IModHelper helper,
        IMonitor monitor,
        string fileName)
    {
        var path = Path.Combine(helper.DirectoryPath, "Asset", "Music", fileName);
        if (!File.Exists(path))
        {
            monitor.Log($"Music file not found: {path}", LogLevel.Error);
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var soundEffect = SoundEffect.FromStream(stream);
            return soundEffect.CreateInstance();
        }
        catch (Exception ex)
        {
            monitor.Log($"{fileName} load failed: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
}
