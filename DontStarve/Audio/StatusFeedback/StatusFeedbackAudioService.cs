using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>
/// 状态反馈音效播放服务。启动即预解码全部六个 <see cref="SoundEffectInstance"/>
/// （用户要求：游戏开始时加载好，防止触发时解压卡顿）。
/// </summary>
/// <remarks>
/// 播放语义：每个音效固定单实例；如果该实例仍在播放，重复触发直接丢弃，不停止当前
/// 音效。三系方向混合（如吃食物饥饿+生命-理智-）时各音效独立播放、互不覆盖，无任何
/// 仲裁。只有卸载、退出或明确的生命周期清理才会停止并释放实例。
/// </remarks>
internal sealed class StatusFeedbackAudioService : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Dictionary<string, SoundEffectInstance> instances = new(
        StringComparer.Ordinal
    );
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private bool loaded;

    internal StatusFeedbackAudioService(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    internal bool IsLoaded => loaded;

    /// <summary>启动/GameLaunched 前调用一次：预解码全部音效。重复调用是 no-op。</summary>
    internal void LoadAll()
    {
        if (loaded)
            return;

        foreach (var cue in StatusFeedbackCueCatalog.Cues)
        {
            var instance = StatusFeedbackAudioLoader.LoadInstance(helper, monitor, cue);
            if (instance is null)
            {
                // 单条资源失败只禁用该音效（Play 时短路），不影响其它五条；失败原因已由加载器记录。
                loggedFailures.Add(cue.Id);
                continue;
            }
            instances.Add(cue.Id, instance);
        }

        loaded = true;
        monitor.Log(
            $"Status feedback audio preloaded (loaded={instances.Count}/{StatusFeedbackCueCatalog.ExpectedCueCount}).",
            instances.Count == StatusFeedbackCueCatalog.ExpectedCueCount
                ? LogLevel.Debug
                : LogLevel.Warn
        );
    }

    /// <summary>释放全部实例（GMCM 关闭开关或 mod 卸载）。可再次 LoadAll 重建。</summary>
    internal void UnloadAll()
    {
        if (!loaded)
            return;

        foreach (var instance in instances.Values)
        {
            try
            {
                instance.Stop();
                instance.Dispose();
            }
            catch (Exception ex)
            {
                // 实例释放失败不应打断关闭流程；MonoGame 在设备丢失等场景可能抛错。
                monitor.Log($"Status feedback audio dispose failed: {ex.Message}", LogLevel.Trace);
            }
        }
        instances.Clear();
        loggedFailures.Clear();
        loaded = false;
    }

    /// <summary>
    /// 播放一条音效。内部失败只记录日志（每条资源一次）并返回 false，不抛异常、
    /// 不静默吞错误；调用点位于结算热路径附近，必须保持零分配快速短路。
    /// </summary>
    internal bool Play(string cueId)
    {
        if (!loaded)
            return false;
        if (loggedFailures.Contains(cueId))
            return false;
        if (!instances.TryGetValue(cueId, out var instance))
        {
            // 未知 id 属于接线错误而不是资源失败；每次都记录，方便定位（播放频率低）。
            monitor.Log($"Status feedback cue id is unknown: {cueId}", LogLevel.Error);
            return false;
        }

        try
        {
            // 正常操作不能截断已经开始的音效；明确的卸载/退出路径才会 Stop。
            if (instance.State != SoundState.Stopped)
                return false;
            instance.Play();
            return true;
        }
        catch (Exception ex)
        {
            if (loggedFailures.Add($"play:{cueId}"))
            {
                monitor.Log(
                    $"Status feedback play failed ({cueId}): {ex.Message}",
                    LogLevel.Error
                );
            }
            return false;
        }
    }

    public void Dispose()
    {
        UnloadAll();
    }
}
