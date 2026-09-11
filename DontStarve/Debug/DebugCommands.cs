#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Debug;

/// <summary>
/// DIAG-20260804: 测试辅助控制台命令。
/// - ds_sanity set &lt;值&gt; / set &lt;百分比&gt;% / max / min / lock
/// - ds_spawn &lt;名字&gt; / clear（鼠标指针处生成，支持真实影怪与幻觉）
/// - ds_boxes on/off（碰撞箱高亮，默认开启；红=攻击框，绿=受击框，黄=PushBox）
/// - ds_attacklog on/off（攻击判定诊断日志，默认关闭；开启后采样 256 条）
/// 仅用于测试阶段；不影响生产逻辑路径（Sanity 走 Administration 源，影怪走 DebugSpawnAt）。
/// </summary>
internal sealed class DebugCommands
{
    private const string SanityCommand = "ds_sanity";
    private const string SpawnCommand = "ds_spawn";
    private const string BoxesCommand = "ds_boxes";
    private const string AttackDiagnosticsCommand = "ds_attacklog";

    private readonly IMonitor monitor;
    private readonly SmapiHostileShadowHost hostileShadowHost;
    private readonly SmapiHarmlessProjectionHost projectionHost;
    private readonly SanitySystemLifecycleCoordinator sanityLifecycle;

    // DIAG-20260804: 测试用碰撞箱高亮开关，默认开启（用户要求测试阶段默认显示）。
    private static bool boxesVisible = true;

    internal static bool AreBoxesVisible => boxesVisible;

    internal DebugCommands(
        IMonitor monitor,
        SmapiHostileShadowHost hostileShadowHost,
        SmapiHarmlessProjectionHost projectionHost,
        SanitySystemLifecycleCoordinator sanityLifecycle
    )
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.hostileShadowHost = hostileShadowHost
            ?? throw new ArgumentNullException(nameof(hostileShadowHost));
        this.projectionHost = projectionHost
            ?? throw new ArgumentNullException(nameof(projectionHost));
        this.sanityLifecycle = sanityLifecycle
            ?? throw new ArgumentNullException(nameof(sanityLifecycle));
    }

    internal void Register(IModHelper helper)
    {
        helper.ConsoleCommands.Add(
            SanityCommand,
            "测试：设置精神值。用法：ds_sanity set <数值> | ds_sanity set <百分比>% | ds_sanity max | ds_sanity min | ds_sanity lock | ds_sanity unlock",
            OnSanityCommand
        );
        helper.ConsoleCommands.Add(
            SpawnCommand,
            "测试：在鼠标指针处生成怪物/幻觉。用法：ds_spawn <creeper-fear|terrorbeak|mr-skitts|dark-hand|dark-watcher|eyes> | ds_spawn harmless <creeper-fear|terrorbeak>（影怪非危险形态）| ds_spawn bind/unbind（强制脱战/恢复测试）| ds_spawn clear（清除全部 DS 影怪/幻觉）",
            OnSpawnCommand
        );
        helper.ConsoleCommands.Add(
            BoxesCommand,
            "测试：碰撞箱高亮开关。用法：ds_boxes on|off（默认 on；红=攻击框，绿=受击框，黄=PushBox）",
            OnBoxesCommand
        );
        helper.ConsoleCommands.Add(
            AttackDiagnosticsCommand,
            "测试：攻击判定诊断日志。用法：ds_attacklog on|off（默认 off；开启后采样 256 条）",
            OnAttackDiagnosticsCommand
        );
    }

    private void OnSanityCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            monitor.Log("世界尚未就绪，无法设置精神值。", LogLevel.Warn);
            return;
        }

        var player = Game1.player;
        if (args.Length == 0)
        {
            var current = player.GetSanity();
            var maximum = player.GetMaxSanity();
            var ratio = maximum > 0 ? current / maximum : 0d;
            monitor.Log(
                $"当前精神值：{current:F0} / {maximum:F0}（{ratio * 100d:F0}%）"
                + (sanityLifecycle.IsDebugSanityLocked ? "；已锁定（仅命令可改）" : ""),
                LogLevel.Info
            );
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "max":
                player.SetSanity(player.GetMaxSanity());
                monitor.Log("精神值已设为 100%。", LogLevel.Info);
                return;
            case "min":
                player.SetSanity(0d);
                monitor.Log("精神值已设为 0%。", LogLevel.Info);
                return;
            case "lock":
                sanityLifecycle.SetDebugSanityLocked(true);
                monitor.Log("精神值已锁定：除 ds_sanity 命令外，其他来源均不能改变。", LogLevel.Info);
                return;
            case "unlock":
                sanityLifecycle.SetDebugSanityLocked(false);
                monitor.Log("精神值已解锁。", LogLevel.Info);
                return;
            case "set":
                if (args.Length < 2)
                {
                    monitor.Log("用法：ds_sanity set <数值> 或 ds_sanity set <百分比>%", LogLevel.Warn);
                    return;
                }
                var raw = args[1];
                var maximumValue = player.GetMaxSanity();
                if (raw.EndsWith("%", StringComparison.Ordinal))
                {
                    if (
                        !double.TryParse(
                            raw.Substring(0, raw.Length - 1),
                            out var percent
                        )
                        || percent < 0d
                        || percent > 100d
                    )
                    {
                        monitor.Log($"百分比无效：{raw}（需 0~100）", LogLevel.Warn);
                        return;
                    }
                    player.SetSanity(maximumValue * percent / 100d);
                    monitor.Log($"精神值已设为 {percent:F0}%（{maximumValue * percent / 100d:F0}）。", LogLevel.Info);
                    return;
                }
                if (
                    !double.TryParse(raw, out var value)
                    || value < 0d
                )
                {
                    monitor.Log($"数值无效：{raw}（需 ≥0）", LogLevel.Warn);
                    return;
                }
                player.SetSanity(value);
                monitor.Log($"精神值已设为 {value:F0}。", LogLevel.Info);
                return;
            default:
                monitor.Log(
                    "未知子命令。用法：ds_sanity set <数值> | set <百分比>% | max | min | lock | unlock",
                    LogLevel.Warn
                );
                return;
        }
    }

    private void OnSpawnCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            monitor.Log("世界尚未就绪，无法生成。", LogLevel.Warn);
            return;
        }
        if (args.Length == 0)
        {
            monitor.Log(
                "用法：ds_spawn <creeper-fear|terrorbeak|mr-skitts|dark-hand|dark-watcher|eyes> | ds_spawn harmless <creeper-fear|terrorbeak> | ds_spawn bind | ds_spawn unbind | ds_spawn clear",
                LogLevel.Warn
            );
            return;
        }

        var name = args[0].ToLowerInvariant();
        if (name == "clear")
        {
            ClearAll();
            return;
        }

        // 鼠标指针世界坐标（viewport 偏移 + 鼠标屏幕坐标）。
        var mouseX = Game1.getMouseX();
        var mouseY = Game1.getMouseY();
        var worldPixel = new Microsoft.Xna.Framework.Vector2(
            mouseX + Game1.viewport.X,
            mouseY + Game1.viewport.Y
        );

        switch (name)
        {
            case "creeper-fear":
            case "terrorbeak":
                var result = hostileShadowHost.DebugSpawnAt(
                    name == "creeper-fear"
                        ? ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId
                        : ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId,
                    worldPixel.X,
                    worldPixel.Y,
                    out var spawnReason
                );
                monitor.Log(
                    result.Spawned
                        ? $"已在鼠标处生成真实影怪：{name}（entity={result.EntityId}）。"
                        : $"生成失败：{name}（{spawnReason}）",
                    result.Spawned ? LogLevel.Info : LogLevel.Warn
                );
                return;
            case "harmless":
                // DIAG-20260809: 影怪非危险形态（无害投影）召唤，绕过预算/许可，直接生成。
                if (args.Length < 2)
                {
                    monitor.Log(
                        "用法：ds_spawn harmless <creeper-fear|terrorbeak>",
                        LogLevel.Warn
                    );
                    return;
                }
                var shadowName = args[1].ToLowerInvariant();
                var shadowSpeciesId = shadowName switch
                {
                    "creeper-fear" =>
                        ShadowCreatureHarmlessProjectionCatalog.CreeperFearSpeciesId,
                    "terrorbeak" =>
                        ShadowCreatureHarmlessProjectionCatalog.TerrorbeakSpeciesId,
                    _ => null,
                };
                if (shadowSpeciesId is null)
                {
                    monitor.Log(
                        $"未知影怪：{shadowName}。支持：creeper-fear, terrorbeak",
                        LogLevel.Warn
                    );
                    return;
                }
                var shadowReason = projectionHost.DebugSpawnShadowProjectionAt(
                    shadowSpeciesId,
                    worldPixel.X,
                    worldPixel.Y
                );
                monitor.Log(
                    shadowReason == "spawn.debug-spawned"
                        ? $"已在鼠标处生成影怪非危险形态：{shadowName}（无害投影）。"
                        : $"非危险形态生成失败：{shadowName}（{shadowReason}）",
                    shadowReason == "spawn.debug-spawned"
                        ? LogLevel.Info
                        : LogLevel.Warn
                );
                return;
            case "bind":
                // DIAG-20260809: 强制脱战测试——立即对全部在册危险影怪隐藏+绑定投影，
                // 跳过自然脱战的 10 游戏分钟/25% 判定。
                var boundCount = hostileShadowHost.DebugForceBindings();
                monitor.Log(
                    boundCount > 0
                        ? $"已强制 {boundCount} 只影怪脱战（隐藏+绑定投影）。"
                        : "没有可脱战的影怪（需先在册危险影怪）。",
                    LogLevel.Info
                );
                return;
            case "unbind":
                // DIAG-20260809: 强制恢复测试——立即解除全部绑定（恢复危险形态）。
                var restoredCount = hostileShadowHost.DebugForceRestoreBindings();
                monitor.Log(
                    $"已强制恢复 {restoredCount} 只绑定影怪（恢复危险形态）。",
                    LogLevel.Info
                );
                return;
            case "mr-skitts":
            case "dark-hand":
            case "dark-watcher":
            case "eyes":
                var speciesId = name switch
                {
                    "mr-skitts" => "sanity.projection.mr-skitts",
                    "dark-hand" => "sanity.projection.dark-hand",
                    "dark-watcher" => "sanity.projection.dark-watcher",
                    _ => "sanity.projection.eyes",
                };
                var projectionReason = projectionHost.DebugSpawnProjectionAt(
                    speciesId,
                    worldPixel.X,
                    worldPixel.Y
                );
                monitor.Log(
                    projectionReason == "spawn.debug-spawned"
                        ? $"已在鼠标处生成幻觉：{name}。"
                        : $"幻觉生成失败：{name}（{projectionReason}）",
                    projectionReason == "spawn.debug-spawned"
                        ? LogLevel.Info
                        : LogLevel.Warn
                );
                return;
            default:
                monitor.Log(
                    $"未知名字：{name}。支持：creeper-fear, terrorbeak, harmless <creeper-fear|terrorbeak>, bind, unbind, mr-skitts, dark-hand, dark-watcher, eyes",
                    LogLevel.Warn
                );
                return;
        }
    }

    private void ClearAll()
    {
        // DIAG-20260804: 测试期间最小清理。
        // DIAG-20260806: 改用 authority 清理（Removed delta 自动联动物理实体移除），
        // 同时释放占用/转换纪元，避免 ds_spawn clear 后 ds_spawn 因预算残留失效。
        var removed = hostileShadowHost.DebugClearAll();
        // DIAG-20260809: 影怪无害投影无 TTL，ds_spawn clear 一并清掉，避免测试残留。
        var removedShadowProjections =
            projectionHost.DebugClearShadowProjections();
        monitor.Log(
            $"已清理 {removed} 个真实影怪实体（含 authority 状态）、{removedShadowProjections} 个影怪无害投影。普通幻觉投影请在生成后自然过期或重启。",
            LogLevel.Info
        );
    }

    private void OnBoxesCommand(string command, string[] args)
    {
        if (args.Length < 1)
        {
            monitor.Log(
                $"碰撞箱高亮当前：{(boxesVisible ? "开启" : "关闭")}。用法：ds_boxes on|off",
                LogLevel.Info
            );
            return;
        }
        switch (args[0].ToLowerInvariant())
        {
            case "on":
                boxesVisible = true;
                monitor.Log("碰撞箱高亮已开启（红=攻击框，绿=受击框，黄=PushBox）。", LogLevel.Info);
                return;
            case "off":
                boxesVisible = false;
                monitor.Log("碰撞箱高亮已关闭。", LogLevel.Info);
                return;
            default:
                monitor.Log("用法：ds_boxes on|off", LogLevel.Warn);
                return;
        }
    }

    private void OnAttackDiagnosticsCommand(string command, string[] args)
    {
        if (args.Length < 1)
        {
            monitor.Log(
                $"攻击判定诊断日志当前：{(SmapiHostileAttackCombatService.MissDiagnosticsEnabled ? "开启" : "关闭")}。用法：ds_attacklog on|off",
                LogLevel.Info
            );
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "on":
                SmapiHostileAttackCombatService.SetMissDiagnosticsEnabled(true);
                monitor.Log(
                    "攻击判定诊断日志已开启：接下来记录最多 256 条攻击判断；ds_boxes 只控制显示，不参与伤害判断。",
                    LogLevel.Info
                );
                return;
            case "off":
                SmapiHostileAttackCombatService.SetMissDiagnosticsEnabled(false);
                monitor.Log("攻击判定诊断日志已关闭。", LogLevel.Info);
                return;
            default:
                monitor.Log("用法：ds_attacklog on|off", LogLevel.Warn);
                return;
        }
    }
}
