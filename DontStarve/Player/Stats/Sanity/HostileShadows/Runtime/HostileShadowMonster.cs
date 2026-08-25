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
    internal const string DisplayDamageModDataKey =
        "Yurin.DontStarve/HostileShadow/DisplayDamage";
    internal const string WanderActiveModDataKey =
        "Yurin.DontStarve/HostileShadow/WanderActive";
    internal const string MovementFrameModDataKey =
        "Yurin.DontStarve/HostileShadow/MovementFrame";
    internal const string KnockbackImmunityModDataKey =
        "Yurin.DontStarve/HostileShadow/Immunity/Knockback";
    internal const string FrozenImmunityModDataKey =
        "Yurin.DontStarve/HostileShadow/Immunity/Frozen";
    // DIAG-20260809: 绑定隐藏态标记（1=隐藏）。危险实体被无害投影外观取代时置位：
    // 不渲染/无敌/行为禁用；低理智恢复时清除。
    internal const string BindingHiddenModDataKey =
        "Yurin.DontStarve/HostileShadow/BindingHidden";
    // DIAG-20260810: 脱战恐吓标记（1=恐吓中）。危险影怪脱战后先播恐吓动画
    // （期间无敌、停止行为），恐吓结束才进入绑定隐藏态——恐吓是脱战的视觉缓冲。
    internal const string RetreatingModDataKey =
        "Yurin.DontStarve/HostileShadow/Retreating";
    private const string EnabledModDataValue = "1";

    public HostileShadowMonster()
    {
        // DIAG-20260807: "Hostile Shadow" 是游戏中实际存在的怪物名（主策划之后会给它加靠近
        // 掉 san 能力），不能占用。这里用通用占位名，真实物种名在 TryMaterialize 按
        // AssetBindingId 覆盖（"Creeper Fear"/"Terrorbeak"），供 Lookup/Data.Monsters 查询。
        Name = "DS Shadow";
        DamageToFarmer = 0;
        Health = 1;
        MaxHealth = 1;
        // Both Creeper Fear and Terrorbeak use this shared physical entity. Let vanilla
        // ignore only this NPC during Farmer collision checks; map, object, and terrain
        // collision must remain owned by GameLocation.
        farmerPassesThrough = true;
        // 影怪本身就是影子（饥荒原版设定），不再绘制脚下阴影；
        // 同时避免原版 DrawShadow 访问空 Sprite 导致 DrawWorld NRE 崩溃。
        HideShadow = true;
        // DIAG-20260806: 补一个有效 Sprite 占位（原版绿史莱姆贴图 + 48x64 帧），
        // 供 Lookup Anything 等第三方读取角色区域/头像时不再因 Sprite 为 null 而 NRE；
        // 实际外观仍走自定义渲染器，此 Sprite 仅作占位，HideShadow 已挡住影子绘制。
        // 注意：AnimatedSprite 无 Texture2D 重载，只能用资产名字符串（构造时加载），
        // 故选用原版必存在的 Characters\\GreenSlime，避免资产缺失导致生成失败。
        Sprite = new AnimatedSprite("Characters\\GreenSlime", 0, 48, 64);
    }

    public override void update(GameTime time, GameLocation location)
    {
        // Vanilla Monster.update owns contact attacks, pathing, sounds, and removal. The common
        // host runtime drives attacks, so only preserve shared location and explicit profile-tagged
        // control immunities here. Frozen effects write stunTime directly in Stardew 1.6.
        currentLocation = location;
        if (
            HostileShadowGameplayPausePolicy.IsBehaviorFrozen(
                Game1.activeClickableMenu is not null,
                Game1.IsMultiplayer,
                Game1.paused,
                Game1.game1.IsActive
            )
        )
        {
            return;
        }
        SuppressTaggedControlEffects();
        // DIAG-20260807: Lookup 显示欺骗的恢复端——LookupAnythingDisplayFake 通过
        // Monster.DamageToFarmer getter 拦截显示配置攻击力（不写字段，仅 Lookup 枚举
        // 瞬间生效）；此处保留兜底：万一其他路径写入了 DamageToFarmer，一律恢复 0，
        // 确保原版接触伤害始终禁用。同时每 tick 关闭 getter 拦截窗口（Lookup 窗口只在
        // CharacterSubject 构造 Postfix 到下一次 update 之间打开）。
        if (DamageToFarmer != 0)
            DamageToFarmer = 0;
        Debug.LookupAnythingDisplayFake.EndLookupWindow();
        // DIAG-20260806: 模拟原版 Character.update 的无敌递减。原版在 base.update 里做这件事，
        // 但我们不调用 base.update（避免原版寻路/接触攻击），所以手动递减，让受击无敌帧
        // （HandleIncomingHit 设置的 1000ms）与其他怪物一样正常倒计时。
        // 注意：不能每 tick 清零——那样会禁用无敌帧，导致玩家挥剑每帧多段伤害（实测多段帧伤）。
        if (invincibleCountdown > 0)
        {
            invincibleCountdown = Math.Max(
                0,
                invincibleCountdown - time.ElapsedGameTime.Milliseconds
            );
        }
    }

    // DIAG-20260807: 原版接触伤害的唯一触发点是 Monster.MovePosition →
    // GameLocation.isCollidingPosition(..., damagesFarmer: DamageToFarmer)：怪物移动撞到玩家时
    // 按 DamageToFarmer 扣血。本类 update 不调 base.update/MovePosition（位置由权威状态机直接
    // 写入），此处再兜底一道空实现——即使未来有路径误调 MovePosition，也不会因 DamageToFarmer>0
    // 触发原版接触伤害（玩家站在受击框内不应受伤；主策划裁定语义）。
    public override void MovePosition(
        GameTime time,
        xTile.Dimensions.Rectangle viewport,
        GameLocation currentLocation
    )
    {
        // 空实现：移动/碰撞/接触伤害全部由自有攻击框（红框）系统负责。
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

    public override void shedChunks(int number, float scale)
    {
        // DIAG-20260806: 影怪 Sprite 为 null（外观由专用渲染器负责）。
        // 原版 shedChunks 无条件访问 Sprite.Texture，玩家挥剑命中时会在
        // GameLocation.damageMonster 路径触发 NRE 并卡死挥剑动画；
        // 影怪是影子，不掉碎块，直接空实现最安全。
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

    private readonly record struct AttackBoxLayout(
        HostileAttackPoint ActorOriginSourcePx,
        HostileAttackPoint PivotSourcePx,
        HostileAttackRectangle AttackBoxSourcePx,
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

    // DIAG-20260806: 非移动/攻击状态（Idle/Spawn/Taunt）的动画时序。
    // 运行时状态机只推进 Chase/Attack 的帧号，这些状态需要渲染器本地按时间推进。
    private readonly record struct AnimationTiming(
        int FrameCount,
        int FrameDurationMs,
        bool Loop
    );

    private readonly SanitySmapiResourceService resources;
    // Frame identity is already three stable values; a value key avoids rebuilding a formatted
    // string for every visible monster on every draw.
    private readonly Dictionary<
        (string BindingId, string StateId, int FrameIndex),
        SanitySlotResourceResult
    > frames = new();
    private readonly Dictionary<string, HurtBoxLayout> hurtBoxes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttackBoxLayout> attackBoxes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, MovementLayout> movementLayouts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, MovementLayout> attackLayouts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationTiming> idleTimings =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationTiming> spawnTimings =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationTiming> tauntTimings =
        new(StringComparer.Ordinal);
    // 每个实体在 Idle/Spawn/Taunt 下的帧时钟：记录 (stateId, 状态起始游戏时间 ms, 当前帧)。
    private readonly Dictionary<long, (string StateId, double StartedMs, int Frame)> frameClocks =
        new();
    // DIAG-20260806: 每个实体上次绘制的视觉状态。一次性动画（Taunt/受击传送）重新进入时
    // 必须重置帧时钟——否则缓存时钟的 StateId 相同不重置，elapsed 累计导致直接跳到最后一帧。
    private readonly Dictionary<long, string> lastDrawnStates = new();
    // DIAG-20260806: HitTeleport/Dying 的受击响应视觉（复用死亡行动画）时序，供渲染器本地推进。
    private readonly Dictionary<string, AnimationTiming> hitResponseTimings =
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
            // DIAG-20260806: Idle/Spawn/Taunt 也加载全部帧——渲染器按 FrameDurationMs
            // 本地推进这些状态的帧（运行时只推进 Chase/Attack），只加载第 0 帧会导致
            // 帧推进到 1+ 时查不到资源而整帧隐身。
            (HostileShadowStateIds.Idle, metadata.Idle, true),
            (HostileShadowStateIds.Chase, metadata.Chase, false),
            (HostileShadowStateIds.Spawn, metadata.Spawn, true),
            (HostileShadowStateIds.Taunt, metadata.Taunt, true),
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

        // DIAG-20260806: 记录 Idle/Spawn/Taunt 的动画时序供渲染器本地推进。
        idleTimings[assetBindingId] = new AnimationTiming(
            metadata.Idle.FrameCount,
            metadata.Idle.FrameDurationMilliseconds,
            true
        );
        spawnTimings[assetBindingId] = new AnimationTiming(
            metadata.Spawn.FrameCount,
            metadata.Spawn.FrameDurationMilliseconds,
            false
        );
        tauntTimings[assetBindingId] = new AnimationTiming(
            metadata.Taunt.FrameCount,
            metadata.Taunt.FrameDurationMilliseconds,
            false
        );

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
        // DIAG-20260804: 测试碰撞箱高亮用。攻击框与受击框共用 ActorOrigin，
        // 但 pivot/drawScale 采用攻击状态的值（攻击框随攻击动画定位）。
        attackBoxes[assetBindingId] = new AttackBoxLayout(
            new HostileAttackPoint(
                metadata.Collision.ActorOriginSourcePx.X,
                metadata.Collision.ActorOriginSourcePx.Y
            ),
            new HostileAttackPoint(
                metadata.Attack.PivotSourcePx.X,
                metadata.Attack.PivotSourcePx.Y
            ),
            new HostileAttackRectangle(
                metadata.Collision.AttackBoxSourcePx.X,
                metadata.Collision.AttackBoxSourcePx.Y,
                metadata.Collision.AttackBoxSourcePx.Width,
                metadata.Collision.AttackBoxSourcePx.Height
            ),
            metadata.Attack.DrawScale
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

            if (
                !metadata.Attack.TryGetDirectionRow(
                    HostileShadowFacingIds.Down,
                    out var attackDownRow
                )
                || !metadata.Attack.TryGetDirectionRow(
                    HostileShadowFacingIds.Right,
                    out var attackRightRow
                )
                || !metadata.Attack.TryGetDirectionRow(
                    HostileShadowFacingIds.Up,
                    out var attackUpRow
                )
                || !metadata.Attack.TryGetDirectionRow(
                    HostileShadowFacingIds.Left,
                    out var attackLeftRow
                )
            )
            {
                reason = "hostile-shadow.attack-direction-metadata-invalid";
                return false;
            }
            attackLayouts[assetBindingId] = new MovementLayout(
                metadata.Attack.FrameCount,
                attackDownRow,
                attackRightRow,
                attackUpRow,
                attackLeftRow
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
            // DIAG-20260806: 受击传送（HitTeleport/Dying）视觉是复用死亡行动画的一次性动画，
            // 记录其时序供渲染器本地推进（此前缺失导致帧号恒为 0，只播第一帧）。
            hitResponseTimings[assetBindingId] = new AnimationTiming(
                hitResponseVisual.FrameCount,
                hitResponseVisual.FrameDurationMilliseconds,
                false
            );
        }
        reason = "hostile-shadow.shared-visual-ready";
        return true;
    }

    internal void Clear()
    {
        frames.Clear();
        hurtBoxes.Clear();
        attackBoxes.Clear();
        movementLayouts.Clear();
        attackLayouts.Clear();
        idleTimings.Clear();
        spawnTimings.Clear();
        tauntTimings.Clear();
        // DIAG-20260806: 实体帧时钟/绘制状态/受击时序随资源一起清理，避免长期运行内存泄漏。
        frameClocks.Clear();
        lastDrawnStates.Clear();
        hitResponseTimings.Clear();
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
            monster.modData.TryGetValue(
                HostileShadowMonster.BindingHiddenModDataKey,
                out var bindingHiddenValue
            )
            && string.Equals(bindingHiddenValue, "1", StringComparison.Ordinal)
        )
        {
            // DIAG-20260809: 绑定隐藏态——不渲染（由无害投影外观取代）。
            return;
        }
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
        // DIAG-20260806: 记录实体本次绘制的视觉状态，用于检测一次性动画（Taunt/受击传送）
        // 的重新进入——状态重入必须重置帧时钟，否则缓存时钟不重置会直接跳到最后一帧。
        long? trackedEntityId = null;
        if (
            monster.modData.TryGetValue(
                HostileShadowMonster.EntityIdModDataKey,
                out var entityIdText
            )
            && long.TryParse(
                entityIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedEntityId
            )
        )
        {
            trackedEntityId = parsedEntityId;
        }
        var frameIndex = 0;
        var frameLayout = default(MovementLayout);
        // DIAG-20260809: 游荡视觉——Idle 且 WanderActive=1 时按移动布局/移动帧绘制（动画帧由
        // WorldRuntime 以半速推进 MovementFrame）；索敌/停止后标记清除，自动回到 Idle 帧。
        var isWanderVisual = string.Equals(
                visualState,
                HostileShadowStateIds.Idle,
                StringComparison.Ordinal
            )
            && monster.modData.TryGetValue(
                HostileShadowMonster.WanderActiveModDataKey,
                out var wanderValue
            )
            && string.Equals(wanderValue, "1", StringComparison.Ordinal)
            && movementLayouts.TryGetValue(bindingId, out frameLayout);
        var usesMovementLayout = isWanderVisual
            || (
                string.Equals(
                    visualState,
                    HostileShadowStateIds.Chase,
                    StringComparison.Ordinal
                )
                && movementLayouts.TryGetValue(bindingId, out frameLayout)
            );
        var attackFrameLayout = default(MovementLayout);
        var usesAttackLayout =
            string.Equals(
                visualState,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && attackLayouts.TryGetValue(bindingId, out attackFrameLayout);
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
            usesAttackLayout
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
        else
        {
            // DIAG-20260806: Idle/Spawn/Taunt 由渲染器按时间推进帧（运行时只推进 Chase/Attack）。
            var timing = visualState switch
            {
                HostileShadowStateIds.Idle => idleTimings.TryGetValue(bindingId, out var t0) ? t0 : default,
                HostileShadowStateIds.Spawn => spawnTimings.TryGetValue(bindingId, out var t1) ? t1 : default,
                HostileShadowStateIds.Taunt => tauntTimings.TryGetValue(bindingId, out var t2) ? t2 : default,
                // DIAG-20260806: 受击传送/濒死视觉此前没有时序，帧号恒为 0（只播第一帧）。
                HitResponseVisualStateId => hitResponseTimings.TryGetValue(bindingId, out var t3) ? t3 : default,
                _ => default,
            };
            if (timing.FrameCount <= 0 || timing.FrameDurationMs <= 0)
            {
                frameIndex = 0;
            }
            else if (trackedEntityId is { } entityId)
            {
                var nowMs = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
                if (
                    !frameClocks.TryGetValue(entityId, out var clock)
                    || !string.Equals(clock.StateId, visualState, StringComparison.Ordinal)
                    || !lastDrawnStates.TryGetValue(entityId, out var lastDrawn)
                    || !string.Equals(lastDrawn, visualState, StringComparison.Ordinal)
                )
                {
                    // DIAG-20260806: 状态重入（如 Taunt→Chase→Taunt）或首次绘制时重置帧时钟；
                    // 否则缓存的同状态时钟不重置，elapsed 累计导致一次性动画直接跳到最后一帧。
                    // DIAG-20260812: 重置后的时钟必须写回字典——否则每次绘制都重新进入
                    // 重置分支、帧号恒为 0，一次性动画永远只播第一帧（恐吓卡首帧根因）。
                    clock = (visualState, nowMs, 0);
                    frameClocks[entityId] = clock;
                }
                else if (
                    HostileShadowGameplayPausePolicy.IsBehaviorFrozen(
                        Game1.activeClickableMenu is not null,
                        Game1.IsMultiplayer,
                        Game1.paused,
                        Game1.game1.IsActive
                    )
                )
                {
                    // DIAG-20260812: 暂停（菜单/对话）或失焦（切窗）时动画帧冻结在
                    // 当前帧——顺延时钟起点并直接沿用缓存帧号。此前失焦未检查
                    // IsActive 会继续播到末帧；顺延起点后再从 elapsed 推导又因
                    // elapsed=0 回退到第一帧。
                    frameClocks[entityId] = (visualState, nowMs, clock.Frame);
                    frameIndex = clock.Frame;
                }
                else
                {
                    var elapsed = (long)(nowMs - clock.StartedMs);
                    if (elapsed < 0)
                        elapsed = 0;
                    var advanced = (int)(elapsed / timing.FrameDurationMs);
                    if (timing.Loop)
                    {
                        frameIndex = advanced % timing.FrameCount;
                    }
                    else
                    {
                        frameIndex = Math.Min(advanced, timing.FrameCount - 1);
                    }
                    frameClocks[entityId] = (visualState, clock.StartedMs, frameIndex);
                }
            }
        }
        // DIAG-20260806: 恐怖尖喙 taunt 分左右方向（新图 Row 11=右恐吓、Row 12=左恐吓）。
        // 按怪物与最近玩家的相对位置选择方向行，替换默认 taunt 行。
        // 注意：taunt 默认行（metadata.Taunt.Row=11）在 DirectionRows 未命中时兜底使用。
        // 帧资源仍按 taunt 动画的 FrameCount 全部加载，仅绘制时替换 sourceY 行。
        var tauntRowOverride = -1;
        if (
            string.Equals(
                visualState,
                HostileShadowStateIds.Taunt,
                StringComparison.Ordinal
            )
        )
        {
            var facing = ResolveTauntFacing(monster);
            if (facing is not null)
            {
                // DIAG-20260806: 玩家在怪物右→Row 11（右恐吓）、左→Row 12（左恐吓），
                // 与 animations.json DirectionRows（Right→11、Left→12）一致。
                // 2026-08-07 18:45 主策划纠正：此映射在加入静息动画前实测正确，
                // 禁止反向补丁；若实机仍反需定位静息/镜像改动是否连带，不再改此映射。
                tauntRowOverride = facing == HostileAttackFacing.Right ? 11 : 12;
            }
        }
        var visualResourceState = visualState;
        if (
            !frames.TryGetValue(
                (bindingId, visualResourceState, frameIndex),
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
        var spriteFacingId = string.Empty;
        if (
            monster.modData.TryGetValue(
                HostileShadowMonster.MovementFacingModDataKey,
                out var serializedFacingId
            )
        )
        {
            spriteFacingId = serializedFacingId;
        }
        if (usesMovementLayout)
        {
            if (!frameLayout.TryResolveRow(spriteFacingId, out var directionRow))
            {
                return;
            }
            sourceY = directionRow * source.Height;
        }
        else if (usesAttackLayout)
        {
            if (!attackFrameLayout.TryResolveRow(spriteFacingId, out var directionRow))
                return;
            sourceY = directionRow * source.Height;
        }
        else if (tauntRowOverride >= 0)
        {
            sourceY = tauntRowOverride * source.Height;
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
        // DIAG-20260807/20260819-05: Creeper Fear 从旧的 +64px 位置上移一个地图图格，
        // 回到实体 Position 的绘制锚点；Terrorbeak 保持 -64px。实体几何仍使用 screen。
        // ② 恐怖尖喙贴图整体半透明 50%（当前资源显示规则，仅 terrorbeak）；
        // ③ 移动和恐吓方向使用资源清单提供的方向行；静息资源只有默认朝向，
        // 由 ResolveSpriteEffects 只对静止左向做一次水平翻转，移动/攻击不重复翻转。
        var spriteScreen = new Vector2(
            screen.X,
            screen.Y
                + (
                    string.Equals(
                        bindingId,
                        ShadowMonsterAssetBindingIds.CreeperFear,
                        StringComparison.Ordinal
                    )
                        ? 0f
                        : -64f
                )
        );
        var drawColor = string.Equals(
            bindingId,
            ShadowMonsterAssetBindingIds.CreeperFear,
            StringComparison.Ordinal
        )
            || string.Equals(
                bindingId,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                StringComparison.Ordinal
            )
            ? Color.White * 0.5f
            : Color.White;
        var spriteEffects = ResolveSpriteEffects(
            visualState,
            spriteFacingId,
            usesMovementLayout || usesAttackLayout
        );
        spriteBatch.Draw(
            texture.Texture,
            spriteScreen,
            new Rectangle(source.X, sourceY, source.Width, source.Height),
            drawColor,
            0f,
            // DIAG-20260807: 贴图左上角对齐 monster.Position（原版惯例），不再用 pivot
            // 对齐——pivot(24,48) 对齐会把贴图整体画到 Position 上方，导致贴图偏到判定框
            // 左上角。origin=0 后贴图 (ActorOrigin) 源像素点正好落在判定框中心
            // （ActorOrigin=(24,40)：贴图(24,40)处=Position+(96,160)=框中心）。
            // 注：绘制点用 spriteScreen（Y-64 上移），判定框仍基于 screen（不变）。
            Vector2.Zero,
            (float)preview.DrawScale,
            spriteEffects,
            layerDepth
        );
        // DIAG-20260804: 测试碰撞箱高亮。红=攻击框（仅攻击态按朝向旋转），绿=受击框。
        // 开关由 ds_boxes 命令控制，默认开启；仅本机绘制，不影响任何逻辑。
        DrawDebugCollisionBoxes(
            spriteBatch,
            monster,
            bindingId,
            visualState,
            screen,
            pivot,
            (float)preview.DrawScale
        );
        // DIAG-20260806: 本帧绘制完成后再记录“上次绘制状态”（先检查后更新——若在开头就更新，
        // 状态重入检测永远失效）。Chase/Attack/帧推进所有分支都汇到这里统一绘制，故只更新一次。
        if (trackedEntityId is { } drawnEntityId)
        {
            lastDrawnStates[drawnEntityId] = visualState;
        }
    }

    internal static SpriteEffects ResolveSpriteEffects(
        string visualState,
        string facingId,
        bool usesMovementLayout
    )
    {
        return !usesMovementLayout
                && string.Equals(
                    visualState,
                    HostileShadowStateIds.Idle,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    facingId,
                    HostileShadowFacingIds.Left,
                    StringComparison.Ordinal
                )
            ? SpriteEffects.FlipHorizontally
            : SpriteEffects.None;
    }

    private void DrawDebugCollisionBoxes(
        SpriteBatch spriteBatch,
        HostileShadowMonster monster,
        string bindingId,
        string visualState,
        Vector2 screen,
        SanityResourcePoint pivot,
        float drawScale
    )
    {
        if (!Debug.DebugCommands.AreBoxesVisible)
            return;

        // 受击框（绿色）：始终显示，使用受击 pivot/drawScale。
        if (
            hurtBoxes.TryGetValue(bindingId, out var hurtLayout)
            && hurtLayout.HurtBoxSourcePx.IsValid
        )
        {
            var hurtScreenX = screen.X
                + (hurtLayout.ActorOriginSourcePx.X - hurtLayout.PivotSourcePx.X)
                    * (float)hurtLayout.DrawScale
                + hurtLayout.HurtBoxSourcePx.X * (float)hurtLayout.DrawScale;
            var hurtScreenY = screen.Y
                + (hurtLayout.ActorOriginSourcePx.Y - hurtLayout.PivotSourcePx.Y)
                    * (float)hurtLayout.DrawScale
                + hurtLayout.HurtBoxSourcePx.Y * (float)hurtLayout.DrawScale;
        // DIAG-20260806: 用户要求碰撞箱更醒目——亮绿（受击框）、亮红（攻击框），边框加粗。
        DrawBoxOutline(
            spriteBatch,
            new Rectangle(
                (int)hurtScreenX,
                (int)hurtScreenY,
                (int)(hurtLayout.HurtBoxSourcePx.Width * hurtLayout.DrawScale),
                (int)(hurtLayout.HurtBoxSourcePx.Height * hurtLayout.DrawScale)
            ),
            new Color(100, 255, 100)
        );

        // DIAG-20260807: 怪物中心标记点（受击框几何中心 = 贴图 ActorOrigin 处）。
        // 放在受击框块内（任何状态都绘制），与碰撞箱同开关（ds_boxes，默认开）；
        // 在碰撞箱之后绘制（最上层），不被贴图遮挡。亮黄色十字。
        var fill = Game1.staminaRect;
        if (fill is not null)
        {
            var centerX = hurtScreenX
                + hurtLayout.HurtBoxSourcePx.Width
                    * (float)hurtLayout.DrawScale
                    / 2f;
            var centerY = hurtScreenY
                + hurtLayout.HurtBoxSourcePx.Height
                    * (float)hurtLayout.DrawScale
                    / 2f;
            var cx = (int)centerX;
            var cy = (int)centerY;
            // DIAG-20260807: 十字加大一倍（mark 12、条宽 5px），并显式 layerDepth=1f
            // 置顶——默认 layerDepth=0 会被后画的贴图（layerDepth>0）盖住（用户反馈被贴图遮挡）。
            var mark = 12;
            var markColor = new Color(255, 255, 100);
            spriteBatch.Draw(
                fill,
                new Rectangle(cx - mark, cy - 2, mark * 2, 5),
                null,
                markColor,
                0f,
                Vector2.Zero,
                SpriteEffects.None,
                1f
            );
            spriteBatch.Draw(
                fill,
                new Rectangle(cx - 2, cy - mark, 5, mark * 2),
                null,
                markColor,
                0f,
                Vector2.Zero,
                SpriteEffects.None,
                1f
            );
        }
        }

        // 攻击框（红色）：仅在攻击状态绘制，用攻击框自己的 pivot/drawScale 旋转包围盒，
        // 与判定侧 TryCreateWorldAttackBox 保持一致（DIAG-20260807 回退）。
        if (
            !string.Equals(
                visualState,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            || !attackBoxes.TryGetValue(bindingId, out var attackLayout)
            || !attackLayout.AttackBoxSourcePx.IsValid
        )
        {
            return;
        }
        var facing = HostileAttackFacing.Down;
        if (
            monster.modData.TryGetValue(
                HostileShadowMonster.MovementFacingModDataKey,
                out var facingId
            )
        )
        {
            facing = facingId switch
            {
                HostileShadowFacingIds.Right => HostileAttackFacing.Right,
                HostileShadowFacingIds.Up => HostileAttackFacing.Up,
                HostileShadowFacingIds.Left => HostileAttackFacing.Left,
                _ => HostileAttackFacing.Down,
            };
        }
        var box = attackLayout.AttackBoxSourcePx;
        var topLeft = RotateBoxPoint(box.X, box.Y, facing);
        var topRight = RotateBoxPoint(box.X + box.Width, box.Y, facing);
        var bottomLeft = RotateBoxPoint(box.X, box.Y + box.Height, facing);
        var bottomRight = RotateBoxPoint(
            box.X + box.Width,
            box.Y + box.Height,
            facing
        );
        var minX = Math.Min(
            Math.Min(topLeft.X, topRight.X),
            Math.Min(bottomLeft.X, bottomRight.X)
        );
        var minY = Math.Min(
            Math.Min(topLeft.Y, topRight.Y),
            Math.Min(bottomLeft.Y, bottomRight.Y)
        );
        var maxX = Math.Max(
            Math.Max(topLeft.X, topRight.X),
            Math.Max(bottomLeft.X, bottomRight.X)
        );
        var maxY = Math.Max(
            Math.Max(topLeft.Y, topRight.Y),
            Math.Max(bottomLeft.Y, bottomRight.Y)
        );
        var attackWidth = (maxX - minX) * (float)attackLayout.DrawScale;
        var attackHeight = (maxY - minY) * (float)attackLayout.DrawScale;

        // DIAG-20260807 主策划裁定：攻击框只动偏移、大小不变（旋转后包围盒尺寸），
        // 几何中心对齐受击框（绿框）中心，与判定侧 TryCreateWorldAttackBox 一致；
        // 攻击框仅在攻击状态绘制。受击框不可用时兑底为原 pivot 平移（保持旧行为）。
        int boxX;
        int boxY;
        if (
            hurtBoxes.TryGetValue(bindingId, out var hurtLayout2)
            && hurtLayout2.HurtBoxSourcePx.IsValid
        )
        {
            var hurtScreenX2 = screen.X
                + (
                    hurtLayout2.ActorOriginSourcePx.X
                    - hurtLayout2.PivotSourcePx.X
                ) * (float)hurtLayout2.DrawScale
                + hurtLayout2.HurtBoxSourcePx.X * (float)hurtLayout2.DrawScale;
            var hurtScreenY2 = screen.Y
                + (
                    hurtLayout2.ActorOriginSourcePx.Y
                    - hurtLayout2.PivotSourcePx.Y
                ) * (float)hurtLayout2.DrawScale
                + hurtLayout2.HurtBoxSourcePx.Y * (float)hurtLayout2.DrawScale;
            var hurtCenterX = hurtScreenX2
                + hurtLayout2.HurtBoxSourcePx.Width
                    * (float)hurtLayout2.DrawScale
                    / 2f;
            var hurtCenterY = hurtScreenY2
                + hurtLayout2.HurtBoxSourcePx.Height
                    * (float)hurtLayout2.DrawScale
                    / 2f;
            boxX = (int)(hurtCenterX - attackWidth / 2f);
            boxY = (int)(hurtCenterY - attackHeight / 2f);
        }
        else
        {
            var actorX = screen.X
                + (
                    attackLayout.ActorOriginSourcePx.X
                    - attackLayout.PivotSourcePx.X
                ) * (float)attackLayout.DrawScale;
            var actorY = screen.Y
                + (
                    attackLayout.ActorOriginSourcePx.Y
                    - attackLayout.PivotSourcePx.Y
                ) * (float)attackLayout.DrawScale;
            boxX = (int)(actorX + minX * (float)attackLayout.DrawScale);
            boxY = (int)(actorY + minY * (float)attackLayout.DrawScale);
        }
        DrawBoxOutline(
            spriteBatch,
            new Rectangle(boxX, boxY, (int)attackWidth, (int)attackHeight),
            new Color(255, 80, 80)
        );
    }

    /// <summary>
    /// DIAG-20260806: 判断恐怖尖喙恐吓时应面向的左右方向（基于最近玩家的相对位置）。
    /// 返回 null 时保持默认行。
    /// </summary>
    private static HostileAttackFacing? ResolveTauntFacing(
        HostileShadowMonster monster
    )
    {
        if (Game1.player is null)
            return null;
        var playerX = Game1.player.Position.X;
        var playerY = Game1.player.Position.Y;
        var monsterX = monster.Position.X;
        var monsterY = monster.Position.Y;
        var deltaX = playerX - monsterX;
        var deltaY = playerY - monsterY;
        // DIAG-20260807: 恐吓方向只看左右。旧判定要求 |dx|>|dy| 才分左右，玩家斜向
        // 站位（垂直差更大）时返回 null 走默认行——实测玩家在左也朝右恐吓（19:02 反馈）。
        // 放宽：仅当水平差几乎为 0（玩家近乎正上/正下）才用默认行，否则按 dx 符号分左右。
        if (Math.Abs(deltaX) < 0.01d)
            return null;
        return deltaX >= 0d
            ? HostileAttackFacing.Right
            : HostileAttackFacing.Left;
    }

    private static HostileAttackPoint RotateBoxPoint(
        double x,
        double y,
        HostileAttackFacing facing
    )
    {
        // 与 HostileAttackGeometry.Rotate 相同规则（Down 不动，顺时针旋转）。
        return facing switch
        {
            HostileAttackFacing.Right => new HostileAttackPoint(y, -x),
            HostileAttackFacing.Up => new HostileAttackPoint(-x, -y),
            HostileAttackFacing.Left => new HostileAttackPoint(-y, x),
            _ => new HostileAttackPoint(x, y),
        };
    }

    private static void DrawBoxOutline(
        SpriteBatch spriteBatch,
        Rectangle rect,
        Color color
    )
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        // 复用原版静态 1x1 白色纹理，避免每帧新建 Texture2D 造成资源泄漏。
        var fill = Game1.staminaRect;
        if (fill is null)
            return;
        var border = 5;
        // 上
        spriteBatch.Draw(
            fill,
            new Rectangle(rect.X, rect.Y, rect.Width, border),
            color
        );
        // 下
        spriteBatch.Draw(
            fill,
            new Rectangle(rect.X, rect.Y + rect.Height - border, rect.Width, border),
            color
        );
        // 左
        spriteBatch.Draw(
            fill,
            new Rectangle(rect.X, rect.Y, border, rect.Height),
            color
        );
        // 右
        spriteBatch.Draw(
            fill,
            new Rectangle(rect.X + rect.Width - border, rect.Y, border, rect.Height),
            color
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
