using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;

namespace DontStarve.Music;

internal static class MusicAudioLoader
{
    // OGG 由本 mod 自带资源目录加载；第二个参数告知 MonoGame 按 Vorbis 解码。
    // 失败只记录日志并返回 null，调用方负责跳过播放。
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
            var soundEffect = SoundEffect.FromStream(stream, true);
            return soundEffect.CreateInstance();
        }
        catch (Exception ex)
        {
            monitor.Log($"{fileName} load failed: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
}
