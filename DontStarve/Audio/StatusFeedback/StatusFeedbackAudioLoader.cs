using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>
/// 状态反馈 OGG 加载器。复制 <c>DontStarve/Music/MusicAudioLoader.cs</c> 的预解码模式：
/// <c>SoundEffect.FromStream(stream, true)</c> 让 MonoGame 按 Vorbis 解码为完整 PCM，
/// 再 <c>CreateInstance()</c> 得到可直接 Play 的实例，触发时刻零解码开销。
/// 失败只记录日志并返回 null，调用方负责跳过播放；不得抛异常打断结算链路。
/// </summary>
internal static class StatusFeedbackAudioLoader
{
    internal static SoundEffectInstance? LoadInstance(
        IModHelper helper,
        IMonitor monitor,
        StatusFeedbackCueDefinition cue
    )
    {
        var path = Path.Combine(
            helper.DirectoryPath,
            cue.RelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        if (!File.Exists(path))
        {
            monitor.Log($"Status feedback audio not found: {path}", LogLevel.Error);
            return null;
        }

        try
        {
            // 模式来源：MusicAudioLoader.LoadInstance（预解码 + 单实例）。
            using var stream = File.OpenRead(path);
            var soundEffect = SoundEffect.FromStream(stream, true);
            return soundEffect.CreateInstance();
        }
        catch (Exception ex)
        {
            monitor.Log(
                $"Status feedback audio load failed ({cue.Id}): {ex.Message}",
                LogLevel.Error
            );
            return null;
        }
    }
}
