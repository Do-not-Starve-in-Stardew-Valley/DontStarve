#nullable enable

using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum SanityTwoAmDarknessEventCommand
{
    Warning,
    Hit,
    Completed,
}

/// <summary>
/// A generated vanilla Event keeps the original event/message renderer: a full-screen fade and
/// `message` commands create the narrow no-speaker window. Only the three mod-owned synchronization
/// commands are overridden; all rendering and dialogue dismissal remains Stardew-owned.
/// </summary>
internal sealed class SanityTwoAmDarknessEvent : Event
{
    internal const string EventId = "sanity-two-am-special-death-v2";
    private const string WarningCommand = "dontStarveDarknessWarning";
    private const string HitCommand = "dontStarveDarknessHit";
    private const string CompletedCommand = "dontStarveDarknessCompleted";
    // 任务5-B（2026-11-13）：脚本头三个字段（music/镜头/角色位置）由原版 InitializeEvent 消费，
    // 角色位置字段会对 this.farmer 执行 Halt+StopAnimation，把昏迷前定格的第 5 帧重置回站立帧，
    // 导致事件开始到 fade 全黑之间出现短暂"正常站立"画面。这里在第一个可执行命令位（头字段
    // 之后、fade 之前）插入自属命令，在 fade 启动前重新定格昏迷姿态；全黑后姿态无所谓。
    private const string CollapsePoseCommand = "dontStarveDarknessCollapsePose";
    private const int CollapsePoseFrame = 5;
    private const int CollapsePosePauseMilliseconds = 3000;

    private readonly Farmer eventFarmer;
    private readonly Action<SanityTwoAmDarknessEventCommand> commandHandler;

    internal SanityTwoAmDarknessEvent(
        Farmer farmer,
        string enteredPrompt,
        string warningPrompt,
        string resolvedPrompt,
        Action<SanityTwoAmDarknessEventCommand> commandHandler
    )
        : base(
            BuildScript(farmer, enteredPrompt, warningPrompt, resolvedPrompt),
            fromAssetName: null,
            eventID: EventId,
            farmerActor: farmer
        )
    {
        this.eventFarmer = farmer ?? throw new ArgumentNullException(nameof(farmer));
        this.commandHandler = commandHandler
            ?? throw new ArgumentNullException(nameof(commandHandler));
        skippable = false;
    }

    public override void tryEventCommand(
        GameLocation location,
        GameTime time,
        string[] args
    )
    {
        var command = args.Length == 0 ? string.Empty : args[0];
        if (string.Equals(command, CollapsePoseCommand, StringComparison.OrdinalIgnoreCase))
        {
            // fade 全黑前不得恢复站立：InitializeEvent 的角色位置字段已把姿态重置，这里立刻
            // 重新定格昏迷第 5 帧（与 BeginLocalPresentation / 原版 passOutFromTired 同帧）。
            eventFarmer.FarmerSprite.setCurrentSingleFrame(
                CollapsePoseFrame,
                CollapsePosePauseMilliseconds
            );
            eventFarmer.FarmerSprite.PauseForSingleAnimation = true;
            CurrentCommand++;
            return;
        }
        if (string.Equals(command, WarningCommand, StringComparison.OrdinalIgnoreCase))
        {
            commandHandler(SanityTwoAmDarknessEventCommand.Warning);
            CurrentCommand++;
            return;
        }
        if (string.Equals(command, HitCommand, StringComparison.OrdinalIgnoreCase))
        {
            commandHandler(SanityTwoAmDarknessEventCommand.Hit);
            CurrentCommand++;
            return;
        }
        if (string.Equals(command, CompletedCommand, StringComparison.OrdinalIgnoreCase))
        {
            commandHandler(SanityTwoAmDarknessEventCommand.Completed);
            CurrentCommand++;
            return;
        }
        base.tryEventCommand(location, time, args);
    }

    private static string BuildScript(
        Farmer farmer,
        string enteredPrompt,
        string warningPrompt,
        string resolvedPrompt
    )
    {
        if (farmer is null)
            throw new ArgumentNullException(nameof(farmer));

        var tile = farmer.TilePoint;
        var direction = Math.Clamp(farmer.FacingDirection, 0, 3);
        // 脚本字段顺序契约：0 music、1 镜头位置、2 角色位置（farmer x y dir）由原版
        // InitializeEvent 消费；自 3 号位起才是逐帧命令。CollapsePoseCommand 必须位于
        // fade 之前，见上方案件注释。
        return string.Join(
            "/",
            "none",
            "-1000 -1000",
            $"farmer {tile.X} {tile.Y} {direction}",
            CollapsePoseCommand,
            "fade",
            $"message \"{Escape(enteredPrompt)}\"",
            WarningCommand,
            $"message \"{Escape(warningPrompt)}\"",
            HitCommand,
            $"message \"{Escape(resolvedPrompt)}\"",
            CompletedCommand,
            "end"
        );
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("/", "\\/", StringComparison.Ordinal);
    }
}
