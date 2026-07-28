#nullable enable

using System;
using System.Collections.Generic;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Physical owner of the low-Sanity idle loop. Cancellation touches FarmerSprite only while the
/// exact player, sprite, owner key, and private animation token still match; a vanilla/other-mod
/// replacement therefore always wins.
/// </summary>
internal sealed class SanityIdlePresentationRuntime
{
    private readonly Dictionary<int, OwnedPresentation> ownedByScreen = new();

    internal int Count => ownedByScreen.Count;

    internal bool IsOwned(
        int screenId,
        SanityVisualOwnerKey ownerKey,
        Farmer player
    )
    {
        if (!ownedByScreen.TryGetValue(screenId, out var owned))
            return false;
        if (
            ReferenceEquals(owned.Player, player)
            && ReferenceEquals(owned.Sprite, player.FarmerSprite)
            && owned.OwnerKey.Equals(ownerKey)
            && player.FarmerSprite.PauseForSingleAnimation
            && SanityIdlePresentationContract.IsOwnedToken(
                player.FarmerSprite.currentSingleAnimation
            )
            && player.FarmerSprite.loopThisAnimation
            && player.FarmerSprite.CurrentAnimationFrame.frame
                == owned.BaseFrame
        )
        {
            return true;
        }

        // Ownership was replaced. Forget our receipt without mutating the replacement.
        ownedByScreen.Remove(screenId);
        return false;
    }

    internal bool TryStart(
        int screenId,
        SanityVisualOwnerKey ownerKey,
        Farmer player
    )
    {
        if (IsOwned(screenId, ownerKey, player))
            return true;
        if (
            ownedByScreen.Count >= SanityIdlePresentationContract.MaximumOwners
            || player is null
            || !player.IsLocalPlayer
            || !player.CanMove
            || player.UsingTool
            || player.isMoving()
            || player.movementDirections.Count > 0
        )
        {
            return false;
        }

        var sprite = player.FarmerSprite;
        if (
            sprite.PauseForSingleAnimation
            || !sprite.IsPlayingBasicAnimation(
                player.FacingDirection,
                player.IsCarrying()
            )
        )
        {
            return false;
        }

        var baseFrame = sprite.CurrentAnimationFrame;
        if (!SanityIdlePresentationContract.IsSafeBasicFrame(baseFrame.frame))
            return false;

        var steps = SanityIdlePresentationContract.Steps;
        var animation = new FarmerSprite.AnimationFrame[steps.Count];
        for (var index = 0; index < steps.Count; index++)
        {
            animation[index] = new FarmerSprite.AnimationFrame(
                baseFrame.frame,
                steps[index].DurationMilliseconds,
                steps[index].PositionOffset,
                baseFrame.armOffset,
                baseFrame.flip,
                null,
                null,
                baseFrame.xOffset
            );
        }

        sprite.animateOnce(animation);
        sprite.currentSingleAnimation =
            SanityIdlePresentationContract.AnimationToken;
        sprite.loopThisAnimation = true;
        ownedByScreen[screenId] = new OwnedPresentation(
            ownerKey,
            player,
            sprite,
            baseFrame.frame,
            baseFrame.armOffset == 12,
            baseFrame.flip
        );
        return true;
    }

    internal bool CancelScreen(
        int screenId,
        SanityVisualOwnerKey? expectedOwner = null
    )
    {
        if (!ownedByScreen.TryGetValue(screenId, out var owned))
            return false;
        if (expectedOwner is { } owner && !owned.OwnerKey.Equals(owner))
            return false;

        ownedByScreen.Remove(screenId);
        var sprite = owned.Sprite;
        if (
            !ReferenceEquals(owned.Player.FarmerSprite, sprite)
            || !sprite.PauseForSingleAnimation
            || !SanityIdlePresentationContract.IsOwnedToken(
                sprite.currentSingleAnimation
            )
            || !sprite.loopThisAnimation
            || sprite.CurrentAnimationFrame.frame != owned.BaseFrame
        )
        {
            return false;
        }

        sprite.loopThisAnimation = false;
        sprite.PauseForSingleAnimation = false;
        sprite.currentSingleAnimation = -1;
        sprite.setCurrentSingleFrame(
            owned.BaseFrame,
            short.MaxValue,
            owned.SecondaryArm,
            owned.Flip
        );
        return true;
    }

    internal int CancelOwner(string playerKey)
    {
        List<int>? screens = null;
        foreach (var pair in ownedByScreen)
        {
            if (
                !string.Equals(
                    pair.Value.OwnerKey.PlayerKey,
                    playerKey,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            screens ??= new List<int>();
            screens.Add(pair.Key);
        }
        return CancelScreens(screens);
    }

    internal int CancelInvalidScreens(Func<int, bool> isScreenValid)
    {
        if (isScreenValid is null)
            throw new ArgumentNullException(nameof(isScreenValid));

        List<int>? screens = null;
        foreach (var screenId in ownedByScreen.Keys)
        {
            if (isScreenValid(screenId))
                continue;
            screens ??= new List<int>();
            screens.Add(screenId);
        }
        return CancelScreens(screens);
    }

    internal int CancelAll()
    {
        if (ownedByScreen.Count == 0)
            return 0;

        var screens = new int[ownedByScreen.Count];
        ownedByScreen.Keys.CopyTo(screens, 0);
        return CancelScreens(screens);
    }

    private int CancelScreens(IReadOnlyList<int>? screens)
    {
        if (screens is null)
            return 0;

        var cancelled = 0;
        for (var index = 0; index < screens.Count; index++)
        {
            if (CancelScreen(screens[index]))
                cancelled++;
        }
        return cancelled;
    }

    private sealed record OwnedPresentation(
        SanityVisualOwnerKey OwnerKey,
        Farmer Player,
        FarmerSprite Sprite,
        int BaseFrame,
        bool SecondaryArm,
        bool Flip
    );
}
