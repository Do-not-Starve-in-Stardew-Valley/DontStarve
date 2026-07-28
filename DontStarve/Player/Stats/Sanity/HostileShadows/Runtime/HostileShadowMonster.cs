#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// A real location-owned network character. It adds no custom NetFields: stable identity and draw
/// routing use Character.modData, which is already an inherited net field. Vanilla contact attack,
/// death, and reward behavior remain disabled; the stage-06 host bridge owns incoming hit damage
/// and true-death classification without calling Monster.takeDamage's vanilla kill branch.
/// </summary>
public sealed class HostileShadowMonster : Monster
{
    internal const string EntityIdModDataKey =
        "Yurin.DontStarve/HostileShadow/EntityId";
    internal const string AssetBindingModDataKey =
        "Yurin.DontStarve/HostileShadow/AssetBindingId";
    internal const string StateModDataKey =
        "Yurin.DontStarve/HostileShadow/StateId";
    internal const string AttackInstanceModDataKey =
        "Yurin.DontStarve/HostileShadow/AttackInstanceId";
    internal const string AttackInstanceRevisionModDataKey =
        "Yurin.DontStarve/HostileShadow/AttackInstanceRevision";
    internal const string AttackFrameModDataKey =
        "Yurin.DontStarve/HostileShadow/AttackFrame";
    internal const string MovementFacingModDataKey =
        "Yurin.DontStarve/HostileShadow/MovementFacing";
    internal const string MovementFrameModDataKey =
        "Yurin.DontStarve/HostileShadow/MovementFrame";
    internal const string KnockbackImmunityModDataKey =
        "Yurin.DontStarve/HostileShadow/Immunity/Knockback";
    internal const string FrozenImmunityModDataKey =
        "Yurin.DontStarve/HostileShadow/Immunity/Frozen";
    private const string EnabledModDataValue = "1";

    public HostileShadowMonster()
    {
        Name = "Hostile Shadow";
        DamageToFarmer = 0;
        Health = 1;
        MaxHealth = 1;
    }

    public override void update(GameTime time, GameLocation location)
    {
        // Vanilla Monster.update owns contact attacks, pathing, sounds, and removal. The common
        // host runtime drives attacks, so only preserve shared location and explicit profile-tagged
        // control immunities here. Frozen effects write stunTime directly in Stardew 1.6.
        currentLocation = location;
        SuppressTaggedControlEffects();
    }

    public override void setTrajectory(Vector2 trajectory)
    {
        if (HasImmunity(KnockbackImmunityModDataKey))
        {
            xVelocity = 0f;
            yVelocity = 0f;
            return;
        }

        base.setTrajectory(trajectory);
    }

    public override int takeDamage(
        int damage,
        int xTrajectory,
        int yTrajectory,
        bool isBomb,
        double addedPrecision,
        Farmer who
    )
    {
        return HostileShadowMonsterHitBridge.HandleHit(this, damage, who);
    }

    public override bool ShouldMonsterBeRemoved()
    {
        return false;
    }

    public override Rectangle GetBoundingBox()
    {
        return HostileShadowMonsterVisualBridge.GetBoundingBox(this);
    }

    public override void draw(SpriteBatch b)
    {
        HostileShadowMonsterVisualBridge.Draw(b, this);
    }

    internal void ApplyCombatImmunity(
        HostileShadowCombatImmunityPolicy policy
    )
    {
        if (policy is null)
            throw new ArgumentNullException(nameof(policy));

        if (policy.BlocksKnockback)
        {
            modData[KnockbackImmunityModDataKey] = EnabledModDataValue;
            // GameLocation.damageMonster treats -1 as vanilla knockback immunity.
            Slipperiness = -1;
        }
        if (policy.BlocksFrozen)
            modData[FrozenImmunityModDataKey] = EnabledModDataValue;
        SuppressTaggedControlEffects();
    }

    private void SuppressTaggedControlEffects()
    {
        if (HasImmunity(KnockbackImmunityModDataKey))
        {
            if (Slipperiness != -1)
                Slipperiness = -1;
            if (xVelocity != 0f)
                xVelocity = 0f;
            if (yVelocity != 0f)
                yVelocity = 0f;
        }
        if (
            HasImmunity(FrozenImmunityModDataKey)
            && HostileShadowCombatImmunityPolicy.IsFrozenStun(stunTime.Value)
        )
        {
            stunTime.Value = 0;
        }
    }

    private bool HasImmunity(string key)
    {
        return modData.TryGetValue(key, out var value)
            && string.Equals(value, EnabledModDataValue, StringComparison.Ordinal);
    }
}

internal static class HostileShadowMonsterHitBridge
{
    private static Func<HostileShadowMonster, int, Farmer?, int>? handler;

    internal static void Configure(
        Func<HostileShadowMonster, int, Farmer?, int> value
    )
    {
        handler = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal static void Clear(
        Func<HostileShadowMonster, int, Farmer?, int> value
    )
    {
        if (ReferenceEquals(handler, value))
            handler = null;
    }

    internal static int HandleHit(
        HostileShadowMonster monster,
        int damage,
        Farmer? attacker
    )
    {
        return handler?.Invoke(monster, damage, attacker) ?? 0;
    }
}

internal static class HostileShadowMonsterVisualBridge
{
    private static HostileShadowMonsterRenderer? renderer;

    internal static void Configure(HostileShadowMonsterRenderer value)
    {
        renderer = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal static void Clear(HostileShadowMonsterRenderer value)
    {
        if (ReferenceEquals(renderer, value))
            renderer = null;
    }

    internal static void Draw(SpriteBatch spriteBatch, HostileShadowMonster monster)
    {
        renderer?.Draw(spriteBatch, monster);
    }

    internal static Rectangle GetBoundingBox(HostileShadowMonster monster)
    {
        return renderer?.GetBoundingBox(monster)
            ?? new Rectangle((int)monster.Position.X, (int)monster.Position.Y, 0, 0);
    }
}

/// <summary>
/// Borrows loader-owned shared visual frames outside the draw path. Attack frames remain owned by
/// the common combat state; Chase consumes host-synchronized four-way facing and metadata cadence.
/// </summary>
internal sealed class HostileShadowMonsterRenderer
{
    private const string HitResponseVisualStateId =
        "hostile-shadow.visual.hit-response";

    private readonly record struct HurtBoxLayout(
        HostileAttackPoint ActorOriginSourcePx,
        HostileAttackPoint PivotSourcePx,
        HostileAttackRectangle HurtBoxSourcePx,
        double DrawScale
    );

    private readonly record struct MovementLayout(
        int FrameCount,
        int DownRow,
        int RightRow,
        int UpRow,
        int LeftRow
    )
    {
        internal bool TryResolveRow(string facingId, out int row)
        {
            row = facingId switch
            {
                HostileShadowFacingIds.Down => DownRow,
                HostileShadowFacingIds.Right => RightRow,
                HostileShadowFacingIds.Up => UpRow,
                HostileShadowFacingIds.Left => LeftRow,
                _ => -1,
            };
            return row >= 0;
        }
    }

    private readonly SanitySmapiResourceService resources;
    // Frame identity is already three stable values; a value key avoids rebuilding a formatted
    // string for every visible monster on every draw.
    private readonly Dictionary<
        (string BindingId, string StateId, int FrameIndex),
        SanitySlotResourceResult
    > frames = new();
    private readonly Dictionary<string, HurtBoxLayout> hurtBoxes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, MovementLayout> movementLayouts =
        new(StringComparer.Ordinal);

    internal HostileShadowMonsterRenderer(
        SanitySmapiResourceService resources
    )
    {
        this.resources = resources
            ?? throw new ArgumentNullException(nameof(resources));
    }

    internal bool TryPrepareBinding(string assetBindingId, out string reason)
    {
        if (
            !resources.TryGetHostileAttackMetadata(
                assetBindingId,
                out var metadata,
                out reason
            )
            || metadata is null
        )
        {
            return false;
        }

        var states = new[]
        {
            (HostileShadowStateIds.Idle, metadata.Idle, false),
            (HostileShadowStateIds.Chase, metadata.Chase, false),
            (HostileShadowStateIds.Spawn, metadata.Spawn, false),
            (HostileShadowStateIds.Taunt, metadata.Taunt, false),
            (HostileShadowStateIds.Attack, metadata.Attack, true),
        };
        foreach (var (stateId, state, allFrames) in states)
        {
            var framesToLoad = allFrames
                || (
                    HostileShadowMovementPresentationBindings.Supports(
                        assetBindingId
                    )
                    && string.Equals(
                        stateId,
                        HostileShadowStateIds.Chase,
                        StringComparison.Ordinal
                    )
                )
                ? state.FrameCount
                : 1;
            for (var frameIndex = 0; frameIndex < framesToLoad; frameIndex++)
            {
                if (
                    !TryGetOrLoad(
                        assetBindingId,
                        stateId,
                        state.AnimationId,
                        frameIndex,
                        out reason
                    )
                )
                {
                    return false;
                }
            }
        }

        hurtBoxes[assetBindingId] = new HurtBoxLayout(
            new HostileAttackPoint(
                metadata.Collision.ActorOriginSourcePx.X,
                metadata.Collision.ActorOriginSourcePx.Y
            ),
            new HostileAttackPoint(
                metadata.Idle.PivotSourcePx.X,
                metadata.Idle.PivotSourcePx.Y
            ),
            new HostileAttackRectangle(
                metadata.Collision.HurtBoxSourcePx.X,
                metadata.Collision.HurtBoxSourcePx.Y,
                metadata.Collision.HurtBoxSourcePx.Width,
                metadata.Collision.HurtBoxSourcePx.Height
            ),
            metadata.Idle.DrawScale
        );
        if (HostileShadowMovementPresentationBindings.Supports(assetBindingId))
        {
            if (
                !metadata.Chase.TryGetDirectionRow(
                    HostileShadowFacingIds.Down,
                    out var downRow
                )
                || !metadata.Chase.TryGetDirectionRow(
                    HostileShadowFacingIds.Right,
                    out var rightRow
                )
                || !metadata.Chase.TryGetDirectionRow(
                    HostileShadowFacingIds.Up,
                    out var upRow
                )
                || !metadata.Chase.TryGetDirectionRow(
                    HostileShadowFacingIds.Left,
                    out var leftRow
                )
            )
            {
                reason = "hostile-shadow.movement-direction-metadata-invalid";
                return false;
            }
            movementLayouts[assetBindingId] = new MovementLayout(
                metadata.Chase.FrameCount,
                downRow,
                rightRow,
                upRow,
                leftRow
            );
        }

        // Death-row frames are an optional presentation lease. A missing or invalid visual never
        // removes the physical entity or changes HitTeleport/Dying business state.
        if (metadata.HitResponseVisual is { } hitResponseVisual)
        {
            for (
                var frameIndex = 0;
                frameIndex < hitResponseVisual.FrameCount;
                frameIndex++
            )
            {
                if (
                    !TryGetOrLoad(
                        assetBindingId,
                        HitResponseVisualStateId,
                        hitResponseVisual.AnimationId,
                        frameIndex,
                        out _
                    )
                )
                {
                    break;
                }
            }
        }
        reason = "hostile-shadow.shared-visual-ready";
        return true;
    }

    internal void Clear()
    {
        frames.Clear();
        hurtBoxes.Clear();
        movementLayouts.Clear();
    }

    internal Rectangle GetBoundingBox(HostileShadowMonster monster)
    {
        if (
            !monster.modData.TryGetValue(
                HostileShadowMonster.AssetBindingModDataKey,
                out var bindingId
            )
            || !hurtBoxes.TryGetValue(bindingId, out var layout)
            || !layout.HurtBoxSourcePx.IsValid
            || !double.IsFinite(layout.DrawScale)
            || layout.DrawScale <= 0d
        )
        {
            return new Rectangle(
                (int)monster.Position.X,
                (int)monster.Position.Y,
                0,
                0
            );
        }

        var actorX = monster.Position.X
            + (layout.ActorOriginSourcePx.X - layout.PivotSourcePx.X)
                * layout.DrawScale;
        var actorY = monster.Position.Y
            + (layout.ActorOriginSourcePx.Y - layout.PivotSourcePx.Y)
                * layout.DrawScale;
        var left = actorX + layout.HurtBoxSourcePx.X * layout.DrawScale;
        var top = actorY + layout.HurtBoxSourcePx.Y * layout.DrawScale;
        var right = left + layout.HurtBoxSourcePx.Width * layout.DrawScale;
        var bottom = top + layout.HurtBoxSourcePx.Height * layout.DrawScale;
        if (
            !double.IsFinite(left)
            || !double.IsFinite(top)
            || !double.IsFinite(right)
            || !double.IsFinite(bottom)
            || left < int.MinValue
            || top < int.MinValue
            || right > int.MaxValue
            || bottom > int.MaxValue
        )
        {
            return new Rectangle(
                (int)monster.Position.X,
                (int)monster.Position.Y,
                0,
                0
            );
        }
        var x = (int)Math.Floor(left);
        var y = (int)Math.Floor(top);
        var width = (int)Math.Ceiling(right) - x;
        var height = (int)Math.Ceiling(bottom) - y;
        return width > 0 && height > 0
            ? new Rectangle(x, y, width, height)
            : new Rectangle((int)monster.Position.X, (int)monster.Position.Y, 0, 0);
    }

    internal void Draw(SpriteBatch spriteBatch, HostileShadowMonster monster)
    {
        ArgumentNullException.ThrowIfNull(spriteBatch);
        ArgumentNullException.ThrowIfNull(monster);
        if (
            !monster.modData.TryGetValue(
                HostileShadowMonster.AssetBindingModDataKey,
                out var bindingId
            )
            || !monster.modData.TryGetValue(
                HostileShadowMonster.StateModDataKey,
                out var stateId
            )
        )
        {
            return;
        }

        var visualState = stateId switch
        {
            HostileShadowStateIds.Spawn => HostileShadowStateIds.Spawn,
            HostileShadowStateIds.Taunt => HostileShadowStateIds.Taunt,
            HostileShadowStateIds.Chase => HostileShadowStateIds.Chase,
            HostileShadowStateIds.Attack => HostileShadowStateIds.Attack,
            HostileShadowStateIds.HitTeleport => HitResponseVisualStateId,
            HostileShadowStateIds.Dying => HitResponseVisualStateId,
            HostileShadowStateIds.Despawn => string.Empty,
            _ => HostileShadowStateIds.Idle,
        };
        if (visualState.Length == 0)
            return;
        var frameIndex = 0;
        var frameLayout = default(MovementLayout);
        var usesMovementLayout = string.Equals(
                visualState,
                HostileShadowStateIds.Chase,
                StringComparison.Ordinal
            )
            && movementLayouts.TryGetValue(bindingId, out frameLayout);
        if (usesMovementLayout)
        {
            if (
                !monster.modData.TryGetValue(
                    HostileShadowMonster.MovementFrameModDataKey,
                    out var serializedMovementFrame
                )
                || !int.TryParse(
                    serializedMovementFrame,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out frameIndex
                )
                || frameIndex < 0
                || frameIndex >= frameLayout.FrameCount
            )
            {
                return;
            }
        }
        else if (
            string.Equals(
                visualState,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && monster.modData.TryGetValue(
                HostileShadowMonster.AttackFrameModDataKey,
                out var serializedFrame
            )
            && int.TryParse(
                serializedFrame,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var oneBasedFrame
            )
            && oneBasedFrame > 0
        )
        {
            frameIndex = oneBasedFrame - 1;
        }
        if (
            !frames.TryGetValue(
                (bindingId, visualState, frameIndex),
                out var resource
            )
            || resource.PhysicalResource is not XnaSanityTextureResource texture
            || resource.VisualPreview is not { } preview
            || preview.PivotSourcePx is not { } pivot
        )
        {
            return;
        }

        var source = preview.SourceRectangle;
        var sourceY = source.Y;
        if (usesMovementLayout)
        {
            if (
                !monster.modData.TryGetValue(
                    HostileShadowMonster.MovementFacingModDataKey,
                    out var facingId
                )
                || !frameLayout.TryResolveRow(facingId, out var directionRow)
            )
            {
                return;
            }
            sourceY = directionRow * source.Height;
        }
        if (
            source.Width <= 0
            || source.Height <= 0
            || source.X < 0
            || sourceY < 0
            || source.X + source.Width > texture.Texture.Width
            || sourceY + source.Height > texture.Texture.Height
        )
        {
            return;
        }

        var screen = Game1.GlobalToLocal(Game1.viewport, monster.Position);
        var layerDepth = Math.Clamp(
            ((float)monster.StandingPixel.Y + 64f) / 10000f,
            0f,
            1f
        );
        spriteBatch.Draw(
            texture.Texture,
            screen,
            new Rectangle(source.X, sourceY, source.Width, source.Height),
            Color.White,
            0f,
            new Vector2(pivot.X, pivot.Y),
            (float)preview.DrawScale,
            SpriteEffects.None,
            layerDepth
        );
    }

    private bool TryGetOrLoad(
        string bindingId,
        string stateId,
        string slotId,
        int frameIndex,
        out string reason
    )
    {
        var key = (bindingId, stateId, frameIndex);
        if (frames.ContainsKey(key))
        {
            reason = "hostile-shadow.shared-visual-cached";
            return true;
        }

        SanitySlotResourceResult resource;
        try
        {
            resource = resources.LoadVisualSlot(slotId, frameIndex);
        }
        catch (Exception exception)
        {
            reason = string.Concat(
                "hostile-shadow.shared-visual-load-threw-",
                exception.GetType().Name
            );
            return false;
        }
        var preview = resource.VisualPreview;
        if (
            !resource.Success
            || resource.PhysicalResource is not XnaSanityTextureResource
            || preview is null
            || preview.Kind != SanityVisualPreviewKind.AnimationFrame
            || preview.OwnerLocalOnly
            || preview.PivotSourcePx is null
            || preview.FrameCount <= 0
        )
        {
            reason = resource.Success
                ? "hostile-shadow.shared-visual-contract-invalid"
                : string.Concat(
                    "hostile-shadow.shared-visual-unavailable-",
                    resource.Diagnostic.Code
                );
            return false;
        }

        frames.Add(key, resource);
        reason = "hostile-shadow.shared-visual-loaded";
        return true;
    }

}
