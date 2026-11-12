#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>状态反馈音效归属的结算系统；与 hunger / sanity / health 三系一一对应。</summary>
internal enum StatusFeedbackSystem
{
    Health = 0,
    Hunger = 1,
    Sanity = 2,
}

/// <summary>离散增减的方向；升播 up、降播 down，与饥荒原版 HUD 音效语义一致。</summary>
internal enum StatusFeedbackDirection
{
    Up = 0,
    Down = 1,
}

/// <summary>
/// 变更来源的播音分类。对齐 DST 语义：只有 not-overtime 的离散增减才播音，
/// 持续（overtime）增减一律静音。
/// </summary>
/// <remarks>
/// 该枚举同时是主机→客机增量快照上的 wire 字段（FeedbackHint）。数值即协议：
/// Unknown=0 表示"没有可播音事实"（完整快照、旧消息缺字段），接收端必须静音；
/// 因此给新增枚举值赋号时不能改动既有值。
/// </remarks>
internal enum StatusFeedbackTriggerKind
{
    Unknown = 0,
    Discrete = 1,
    Continuous = 2,
}

/// <summary>单条音效资源的代码侧清单条目；与 cues.json 契约测试互为对照。</summary>
internal sealed class StatusFeedbackCueDefinition
{
    internal StatusFeedbackCueDefinition(
        string id,
        string relativePath,
        StatusFeedbackSystem system,
        StatusFeedbackDirection direction,
        double durationSeconds
    )
    {
        Id = id;
        RelativePath = relativePath;
        System = system;
        Direction = direction;
        DurationSeconds = durationSeconds;
    }

    internal string Id { get; }

    /// <summary>相对 mod 根目录的资源路径，使用 '/' 分隔；加载时转为本机分隔符。</summary>
    internal string RelativePath { get; }

    internal StatusFeedbackSystem System { get; }

    internal StatusFeedbackDirection Direction { get; }

    internal double DurationSeconds { get; }
}

/// <summary>
/// 六音效的唯一事实清单（health/hunger/sanity × up/down）。
/// cues.json 与此目录共同构成资源契约，契约测试断言两者一致。
/// </summary>
internal static class StatusFeedbackCueCatalog
{
    internal const string RelativeDirectory = "Asset/Audio/StatusFeedback";
    internal const string CuesFileName = "cues.json";
    internal const int CuesFormatVersion = 1;
    internal const int ExpectedCueCount = 6;

    internal static IReadOnlyList<StatusFeedbackCueDefinition> Cues { get; } = new[]
    {
        new StatusFeedbackCueDefinition(
            "health-down",
            $"{RelativeDirectory}/health-down.ogg",
            StatusFeedbackSystem.Health,
            StatusFeedbackDirection.Down,
            0.711
        ),
        new StatusFeedbackCueDefinition(
            "health-up",
            $"{RelativeDirectory}/health-up.ogg",
            StatusFeedbackSystem.Health,
            StatusFeedbackDirection.Up,
            0.739
        ),
        new StatusFeedbackCueDefinition(
            "hunger-down",
            $"{RelativeDirectory}/hunger-down.ogg",
            StatusFeedbackSystem.Hunger,
            StatusFeedbackDirection.Down,
            0.583
        ),
        new StatusFeedbackCueDefinition(
            "hunger-up",
            $"{RelativeDirectory}/hunger-up.ogg",
            StatusFeedbackSystem.Hunger,
            StatusFeedbackDirection.Up,
            0.488
        ),
        new StatusFeedbackCueDefinition(
            "sanity-down",
            $"{RelativeDirectory}/sanity-down.ogg",
            StatusFeedbackSystem.Sanity,
            StatusFeedbackDirection.Down,
            0.646
        ),
        new StatusFeedbackCueDefinition(
            "sanity-up",
            $"{RelativeDirectory}/sanity-up.ogg",
            StatusFeedbackSystem.Sanity,
            StatusFeedbackDirection.Up,
            0.540
        ),
    };

    internal static bool TryGetCue(
        string id,
        out StatusFeedbackCueDefinition? cue
    )
    {
        cue = null;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        foreach (var candidate in Cues)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                cue = candidate;
                return true;
            }
        }
        return false;
    }
}

/// <summary>cues.json 解析结果；解析失败时 IsAvailable=false 并携带 reason。</summary>
internal sealed class StatusFeedbackCuesDocument
{
    private StatusFeedbackCuesDocument(IReadOnlyList<StatusFeedbackCueDefinition> cues)
    {
        Cues = cues;
    }

    internal IReadOnlyList<StatusFeedbackCueDefinition> Cues { get; }

    internal bool IsAvailable => Cues.Count == StatusFeedbackCueCatalog.ExpectedCueCount;

    internal static StatusFeedbackCuesDocument Unavailable()
    {
        return new StatusFeedbackCuesDocument(Array.Empty<StatusFeedbackCueDefinition>());
    }

    /// <summary>
    /// 纯解析规则：校验 FormatVersion、条目数量与每条目字段；条目还必须与代码侧
    /// StatusFeedbackCueCatalog 完全一致（id/路径/系统/方向），时长必须大于 0。
    /// </summary>
    internal static StatusFeedbackCuesDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Unavailable();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("FormatVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var formatVersion)
                || formatVersion != StatusFeedbackCueCatalog.CuesFormatVersion
                || !root.TryGetProperty("Cues", out var cuesElement)
                || cuesElement.ValueKind != JsonValueKind.Array
            )
            {
                return Unavailable();
            }

            var cues = new List<StatusFeedbackCueDefinition>();
            foreach (var entry in cuesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    return Unavailable();
                if (!TryReadString(entry, "id", out var id))
                    return Unavailable();
                if (!TryReadString(entry, "path", out var path))
                    return Unavailable();
                if (!TryReadString(entry, "system", out var systemText))
                    return Unavailable();
                if (!TryReadString(entry, "direction", out var directionText))
                    return Unavailable();
                if (!TryReadDuration(entry, out var durationSeconds))
                    return Unavailable();
                if (
                    !TryParseSystem(systemText, out var system)
                    || !TryParseDirection(directionText, out var direction)
                    || durationSeconds <= 0
                )
                {
                    return Unavailable();
                }

                // 条目必须与代码侧目录完全一致；目录缺失的 id 视为契约破坏。
                if (
                    !StatusFeedbackCueCatalog.TryGetCue(id, out var catalogCue)
                    || catalogCue is null
                    || !string.Equals(catalogCue.RelativePath, path, StringComparison.Ordinal)
                    || catalogCue.System != system
                    || catalogCue.Direction != direction
                )
                {
                    return Unavailable();
                }

                cues.Add(catalogCue);
            }

            if (cues.Count != StatusFeedbackCueCatalog.ExpectedCueCount)
                return Unavailable();

            return new StatusFeedbackCuesDocument(cues);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return Unavailable();
        }
    }

    private static bool TryReadString(JsonElement entry, string propertyName, out string value)
    {
        value = string.Empty;
        if (
            !entry.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadDuration(JsonElement entry, out double durationSeconds)
    {
        durationSeconds = 0;
        if (
            !entry.TryGetProperty("durationSeconds", out var element)
            || element.ValueKind != JsonValueKind.Number
        )
        {
            return false;
        }

        durationSeconds = element.GetDouble();
        return double.IsFinite(durationSeconds);
    }

    private static bool TryParseSystem(string text, out StatusFeedbackSystem system)
    {
        switch (text.ToLowerInvariant())
        {
            case "health":
                system = StatusFeedbackSystem.Health;
                return true;
            case "hunger":
                system = StatusFeedbackSystem.Hunger;
                return true;
            case "sanity":
                system = StatusFeedbackSystem.Sanity;
                return true;
            default:
                system = StatusFeedbackSystem.Health;
                return false;
        }
    }

    private static bool TryParseDirection(string text, out StatusFeedbackDirection direction)
    {
        switch (text.ToLowerInvariant())
        {
            case "up":
                direction = StatusFeedbackDirection.Up;
                return true;
            case "down":
                direction = StatusFeedbackDirection.Down;
                return true;
            default:
                direction = StatusFeedbackDirection.Up;
                return false;
        }
    }
}

/// <summary>本地玩家一次可播音的离散增减事实；方向由结算前后值判定。</summary>
internal readonly record struct StatusFeedbackPlayback(
    StatusFeedbackSystem System,
    StatusFeedbackDirection Direction,
    string Reason
);

/// <summary>
/// 跨系统状态反馈的纯判定规则：给定一次结算的方向事实，返回应播音效（或 null）。
/// 方向混合（如吃食物饥饿+生命-理智-）时每个结算各自产出各自的播音，互不仲裁，
/// 与 DST eater.lua 的三次独立 HUD 音效语义一致。
/// </summary>
internal static class StatusFeedbackPlaybackResolver
{
    internal static StatusFeedbackPlayback? Resolve(
        StatusFeedbackSystem system,
        double previousValue,
        double currentValue,
        string reason
    )
    {
        if (
            !double.IsFinite(previousValue)
            || !double.IsFinite(currentValue)
            || previousValue.Equals(currentValue)
        )
        {
            return null;
        }

        var direction = currentValue > previousValue
            ? StatusFeedbackDirection.Up
            : StatusFeedbackDirection.Down;
        return new StatusFeedbackPlayback(system, direction, reason);
    }
}
