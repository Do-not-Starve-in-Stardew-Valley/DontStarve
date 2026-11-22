#nullable enable

using System;
using DontStarve.Player.Stats.Health;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Debug;

/// <summary>
/// One-shot console controls for the current player's Health and vanilla Stamina.
/// Health writes stay behind the Health owner so its fractional ledger remains coherent.
/// </summary>
internal sealed class PlayerResourceDebugCommands
{
    private const string HealthCommand = "ds_health";
    private const string EnergyCommand = "ds_energy";

    private readonly IMonitor monitor;
    private readonly Health health;

    internal PlayerResourceDebugCommands(IMonitor monitor, Health health)
    {
        this.monitor = monitor;
        this.health = health;
    }

    internal void Register(IModHelper helper)
    {
        helper.ConsoleCommands.Add(
            HealthCommand,
            "测试：查看或设置当前主机玩家生命值。用法：ds_health set <数值|百分比%> | ds_health max | ds_health min",
            OnHealthCommand
        );
        helper.ConsoleCommands.Add(
            EnergyCommand,
            "测试：查看或设置当前玩家能量（原版体力）。用法：ds_energy set <数值|百分比%> | ds_energy max | ds_energy min",
            OnEnergyCommand
        );
    }

    private void OnHealthCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            monitor.Log("世界尚未就绪，无法设置生命值。", LogLevel.Warn);
            return;
        }
        if (!Context.IsMainPlayer)
        {
            monitor.Log("ds_health 只能由 host 执行；客户端不会本地修改生命值账本。", LogLevel.Warn);
            return;
        }

        var snapshot = health.GetDebugSnapshot();
        if (args.Length == 0)
        {
            var percent = snapshot.Maximum > 0
                ? snapshot.Current / snapshot.Maximum * 100d
                : 0d;
            monitor.Log(
                $"当前生命值：{snapshot.Current:F2} / {snapshot.Maximum:F0}（{percent:F0}%）",
                LogLevel.Info
            );
            return;
        }

        var action = args[0].ToLowerInvariant();
        double target;
        switch (action)
        {
            case "max":
                target = snapshot.Maximum;
                break;
            case "min":
                target = 0d;
                break;
            case "set":
                if (args.Length < 2)
                {
                    monitor.Log(
                        "用法：ds_health set <数值|百分比%> | ds_health max | ds_health min",
                        LogLevel.Warn
                    );
                    return;
                }
                if (
                    !DebugCommandValueParser.TryResolveTarget(
                        args[1],
                        snapshot.Maximum,
                        out target,
                        out var reason
                    )
                )
                {
                    monitor.Log($"生命值数值无效：{args[1]}（{reason}）", LogLevel.Warn);
                    return;
                }
                break;
            default:
                monitor.Log(
                    "用法：ds_health set <数值|百分比%> | ds_health max | ds_health min",
                    LogLevel.Warn
                );
                return;
        }

        if (
            !health.TrySetDebugValue(
                target,
                out var current,
                out var maximum,
                out var isLethal
            )
        )
        {
            monitor.Log("生命值调试命令未执行：Health 会话尚未就绪或当前设备不是 host。", LogLevel.Warn);
            return;
        }

        var currentPercent = maximum > 0 ? current / maximum * 100d : 0d;
        monitor.Log(
            $"生命值设置成功：{current:F2} / {maximum:F0}（{currentPercent:F0}%）"
                + (isLethal ? "；已归零，后续由原版死亡流程处理。" : "。"),
            LogLevel.Info
        );
    }

    private void OnEnergyCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is not { } player)
        {
            monitor.Log("世界尚未就绪，无法设置能量。", LogLevel.Warn);
            return;
        }

        var maximum = Math.Max(0d, player.MaxStamina);
        var current = player.Stamina;
        if (args.Length == 0)
        {
            var percent = maximum > 0d ? current / maximum * 100d : 0d;
            monitor.Log(
                $"当前能量：{current:F1} / {maximum:F1}（{percent:F0}%）",
                LogLevel.Info
            );
            return;
        }

        var action = args[0].ToLowerInvariant();
        double target;
        switch (action)
        {
            case "max":
                target = maximum;
                break;
            case "min":
                target = 0d;
                break;
            case "set":
                if (args.Length < 2)
                {
                    monitor.Log(
                        "用法：ds_energy set <数值|百分比%> | ds_energy max | ds_energy min",
                        LogLevel.Warn
                    );
                    return;
                }
                if (
                    !DebugCommandValueParser.TryResolveTarget(
                        args[1],
                        maximum,
                        out target,
                        out var reason
                    )
                )
                {
                    monitor.Log($"能量数值无效：{args[1]}（{reason}）", LogLevel.Warn);
                    return;
                }
                break;
            default:
                monitor.Log(
                    "用法：ds_energy set <数值|百分比%> | ds_energy max | ds_energy min",
                    LogLevel.Warn
                );
                return;
        }

        player.Stamina = (float)target;
        current = player.Stamina;
        var currentPercent = maximum > 0d ? current / maximum * 100d : 0d;
        monitor.Log(
            $"能量设置成功：{current:F1} / {maximum:F1}（{currentPercent:F0}%）。",
            LogLevel.Info
        );
    }
}
