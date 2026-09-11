#nullable enable

using System;
using System.Globalization;
using DontStarve.Player.Stats.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace DontStarve.Player.Stats.Food;

/// <summary>
/// Owns only the world-toolbar input bridge. It delegates all gesture decisions to the pure input
/// state machines and lets Stardew own the actual question dialogue and eating animation.
/// </summary>
internal sealed class SeedEdibilityInteractionService : IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Func<bool> isEnabled;
    private readonly SeedFoodGamepadChordState gamepad = new();
    private readonly SeedFoodTouchGestureState touch = new();
    private SeedFoodInputCandidate? pendingCandidate;
    private bool disposed;
    private bool touchReadFailureLogged;

    internal SeedEdibilityInteractionService(
        IModHelper helper,
        IMonitor monitor,
        Func<bool> isEnabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));

        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Input.ButtonReleased += OnButtonReleased;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

        if (!SeedEdibilityRuntime.VanillaActionButtonGuardInstalled)
        {
            monitor.Log(
                "Seed toolbar interaction is disabled because the vanilla action-button guard is unavailable.",
                LogLevel.Warn
            );
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        helper.Events.Input.ButtonPressed -= OnButtonPressed;
        helper.Events.Input.ButtonReleased -= OnButtonReleased;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        gamepad.Reset();
        touch.Cancel();
        pendingCandidate = null;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (disposed)
            return;

        if (e.Button == SButton.MouseRight)
        {
            if (
                TryGetCandidateAt(
                    Game1.getMousePosition(ui_scale: true),
                    SeedFoodInputDevice.Mouse,
                    out var mouseCandidate
                )
                && RequestConfirmation(mouseCandidate)
            )
            {
                helper.Input.Suppress(SButton.MouseRight);
            }
            return;
        }

        if (
            e.Button != SButton.ControllerA
            && e.Button != SButton.ControllerX
        )
        {
            return;
        }

        if (Game1.options is null || !Game1.options.gamepadControls)
            return;

        var button = e.Button == SButton.ControllerA
            ? SeedFoodGamepadButton.A
            : SeedFoodGamepadButton.X;
        var context = TryGetCandidateAt(
            Game1.getMousePosition(ui_scale: true),
            SeedFoodInputDevice.Gamepad,
            out var candidate
        )
            ? BuildContext(candidate.Slot, allowActiveMenu: false)
            : default;
        var chordCandidate = gamepad.Press(button, context);
        if (!chordCandidate.HasValue)
            return;

        if (!RequestConfirmation(chordCandidate.Value))
            return;

        // Suppress both edges once the chord is recognized. Vanilla already saw whichever edge
        // arrived first, but it cannot start its food path for a toolbar target while this bridge
        // is active because SeedEdibilityRuntime guards the getter for the entire action call.
        helper.Input.Suppress(SButton.ControllerA);
        helper.Input.Suppress(SButton.ControllerX);
    }

    private void OnButtonReleased(object? sender, ButtonReleasedEventArgs e)
    {
        if (disposed)
            return;

        if (e.Button == SButton.ControllerA)
            gamepad.Release(SeedFoodGamepadButton.A);
        else if (e.Button == SButton.ControllerX)
            gamepad.Release(SeedFoodGamepadButton.X);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (!CanReadTouchInput())
        {
            touch.Cancel();
            return;
        }

        TouchCollection touches;
        try
        {
            touches = TouchPanel.GetState();
        }
        catch (Exception exception)
        {
            touch.Cancel();
            if (!touchReadFailureLogged)
            {
                touchReadFailureLogged = true;
                monitor.Log(
                    $"Seed touch input is disabled for this session ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            return;
        }

        if (touches.Count != 1)
        {
            if (touch.IsActive)
                touch.Cancel();
            return;
        }

        var location = touches[0];
        var position = ToUiPoint(location.Position);
        var timestamp = Environment.TickCount64;
        if (location.State == TouchLocationState.Pressed)
        {
            if (
                TryGetCandidateAt(
                    position,
                    SeedFoodInputDevice.Touch,
                    out var candidate
                )
            )
            {
                touch.TryBegin(
                    location.Id,
                    new SeedFoodPoint(position.X, position.Y),
                    timestamp,
                    BuildContext(candidate.Slot, allowActiveMenu: false)
                );
            }
            return;
        }

        if (!touch.IsActive)
            return;

        if (location.State == TouchLocationState.Released)
        {
            var context = TryGetContextAt(position, allowActiveMenu: false);
            var releasedCandidate = touch.Release(
                location.Id,
                new SeedFoodPoint(position.X, position.Y),
                timestamp,
                context
            );
            if (releasedCandidate.HasValue)
                RequestConfirmation(releasedCandidate.Value);
            return;
        }

        if (location.State == TouchLocationState.Moved)
        {
            var context = TryGetContextAt(position, allowActiveMenu: false);
            touch.Observe(
                location.Id,
                new SeedFoodPoint(position.X, position.Y),
                context,
                multiplePointers: false
            );
        }
    }

    private bool RequestConfirmation(SeedFoodInputCandidate candidate)
    {
        if (pendingCandidate.HasValue || !TryGetCurrentCandidate(candidate, allowActiveMenu: false, out var item))
            return false;

        var location = Game1.currentLocation;
        if (location is null)
            return false;

        pendingCandidate = candidate;
        try
        {
            var question = Game1.content.LoadString(
                "Strings\\StringsFromCSFiles:Game1.cs.3160",
                item.DisplayName
            );
            location.createQuestionDialogue(
                question,
                location.createYesNoResponses(),
                OnQuestionAnswered
            );
            return true;
        }
        catch (Exception exception)
        {
            pendingCandidate = null;
            monitor.Log(
                $"Seed confirmation dialogue was not opened ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
            return false;
        }
    }

    private void OnQuestionAnswered(Farmer who, string answer)
    {
        var candidate = pendingCandidate;
        pendingCandidate = null;
        if (
            !candidate.HasValue
            || !string.Equals(answer, "Yes", StringComparison.Ordinal)
            || !TryGetCurrentCandidate(candidate.Value, allowActiveMenu: true, out var item, who)
        )
        {
            return;
        }

        try
        {
            who.CurrentToolIndex = candidate.Value.Slot;
            // eatHeldObject is the native inventory-consumption path. Point it at the same object
            // just revalidated above so its stack reduction and Stardrop branch remain vanilla.
            who.mostRecentlyGrabbedItem = item;
            who.isEating = false;
            who.eatHeldObject();
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Seed consumption failed after confirmation ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }
    }

    private bool TryGetCandidateAt(
        Point position,
        SeedFoodInputDevice device,
        out SeedFoodInputCandidate candidate
    )
    {
        candidate = default;
        if (!TryFindToolbarSlot(position, out var slot))
            return false;

        var context = BuildContext(slot, allowActiveMenu: false);
        if (!context.IsEligible)
            return false;

        candidate = new SeedFoodInputCandidate(
            device,
            context.Slot,
            context.ItemId!,
            context.Stack
        );
        return true;
    }

    private bool TryGetCurrentCandidate(
        SeedFoodInputCandidate candidate,
        bool allowActiveMenu,
        out Item item,
        Farmer? player = null
    )
    {
        item = null!;
        player ??= Game1.player;
        if (player is null || candidate.Slot < 0 || !CanUseWorldState(player, allowActiveMenu))
            return false;

        var context = BuildContext(candidate.Slot, allowActiveMenu);
        if (!candidate.Matches(context))
            return false;

        if (candidate.Slot >= player.Items.Count)
            return false;
        item = player.Items[candidate.Slot]!;
        return item is not null;
    }

    private SeedFoodInputContext TryGetContextAt(Point position, bool allowActiveMenu)
    {
        return TryFindToolbarSlot(position, out var slot)
            ? BuildContext(slot, allowActiveMenu)
            : default;
    }

    private SeedFoodInputContext BuildContext(int slot, bool allowActiveMenu)
    {
        var player = Game1.player;
        if (
            player is null
            || slot < 0
            || slot >= player.Items.Count
            || !CanUseWorldState(player, allowActiveMenu)
        )
        {
            return default;
        }

        var item = player.Items[slot];
        var isTarget = item is not null && SeedEdibilityRuntime.IsTargetSeed(item);
        return new SeedFoodInputContext(
            Enabled:
                isEnabled()
                && SeedEdibilityRuntime.IsOperational
                && SeedEdibilityRuntime.VanillaActionButtonGuardInstalled,
            WorldReady: Context.IsWorldReady,
            HasActiveMenu: !allowActiveMenu && Game1.activeClickableMenu is not null,
            HasActiveEvent: Game1.eventUp || Game1.CurrentEvent is not null,
            HasActiveMinigame: Game1.currentMinigame is not null,
            IsFading: Game1.fadeToBlack || Game1.fadeIn,
            IsEating: player.isEating,
            IsTargetSeed: isTarget,
            Slot: slot,
            ItemId: item?.ItemId,
            Stack: item?.Stack ?? 0
        );
    }

    private bool CanReadTouchInput()
    {
        return isEnabled()
            && SeedEdibilityRuntime.IsOperational
            && SeedEdibilityRuntime.VanillaActionButtonGuardInstalled
            && Context.IsWorldReady;
    }

    private static bool CanUseWorldState(Farmer player, bool allowActiveMenu)
    {
        if (!Context.IsWorldReady || player.isEating || player.UsingTool)
            return false;
        if (!allowActiveMenu && Game1.activeClickableMenu is not null)
            return false;
        if (
            Game1.eventUp
            || Game1.CurrentEvent is not null
            || Game1.currentMinigame is not null
            || Game1.farmEvent is not null
            || Game1.fadeToBlack
            || Game1.fadeIn
            || Game1.killScreen
            || Game1.IsChatting
            || (!allowActiveMenu && Game1.dialogueUp)
        )
        {
            return false;
        }
        return true;
    }

    private static bool TryFindToolbarSlot(Point position, out int slot)
    {
        slot = -1;
        if (Game1.onScreenMenus is null)
            return false;

        foreach (var menu in Game1.onScreenMenus)
        {
            if (menu is not Toolbar toolbar || toolbar.buttons is null)
                continue;

            foreach (var button in toolbar.buttons)
            {
                if (
                    !button.containsPoint(position.X, position.Y)
                    || !int.TryParse(
                        button.name,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out slot
                    )
                )
                {
                    continue;
                }
                return true;
            }
        }

        return false;
    }

    private static Point ToUiPoint(Vector2 screenPosition)
    {
        var scale = Game1.options?.uiScale ?? 1f;
        if (scale <= 0f)
            scale = 1f;
        return new Point(
            (int)(screenPosition.X / scale),
            (int)(screenPosition.Y / scale)
        );
    }
}
