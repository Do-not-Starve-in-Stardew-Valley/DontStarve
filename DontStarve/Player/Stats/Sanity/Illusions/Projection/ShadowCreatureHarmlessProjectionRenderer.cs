#nullable enable

using System;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal interface IShadowCreatureHarmlessProjectionWorldRenderer
{
    void Draw(
        SpriteBatch spriteBatch,
        ShadowCreatureHarmlessProjectionInstance instance,
        Vector2 screenPixel
    );
}

/// <summary>
/// Draws an idle appearance only on the exact local owner screen. The shipped profile remains
/// visibly provisional; this presenter does not consume any gameplay-oriented profile fields.
/// </summary>
internal sealed class ShadowCreatureHarmlessProjectionRenderer
    : IShadowCreatureHarmlessProjectionWorldRenderer
{
    public void Draw(
        SpriteBatch spriteBatch,
        ShadowCreatureHarmlessProjectionInstance instance,
        Vector2 screenPixel
    )
    {
        ArgumentNullException.ThrowIfNull(spriteBatch);
        ArgumentNullException.ThrowIfNull(instance);
        var resource = instance.CurrentVisualResource;
        var preview = resource?.VisualPreview;
        if (
            resource?.PhysicalResource is not XnaSanityTextureResource textureResource
            || preview is null
            || preview.Kind != SanityVisualPreviewKind.AnimationFrame
            || preview.PivotSourcePx is not { } pivot
            || instance.CurrentFrameIndex < 0
            || instance.CurrentFrameIndex >= preview.FrameCount
        )
        {
            throw new InvalidOperationException(
                "shadow-projection.visual-resource-unavailable"
            );
        }

        var baseSource = preview.SourceRectangle;
        var sourceY = baseSource.Y;
        if (
            instance.AnimationState
                == ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Moving
        )
        {
            // DIAG-20260809: 四方向行走：按朝向选行（Down=0/Right=1/Up=2/Left=3，
            // 与 animations.json DirectionRows 一致；Left 行为 Baked 预镜像，直接绘制）。
            var directionRow = instance.FacingId switch
            {
                ShadowCreatureHarmlessProjectionCatalog.FacingRight => 1,
                ShadowCreatureHarmlessProjectionCatalog.FacingUp => 2,
                ShadowCreatureHarmlessProjectionCatalog.FacingLeft => 3,
                _ => 0,
            };
            sourceY = checked(baseSource.Y + (directionRow * baseSource.Height));
        }
        var source = new Rectangle(
            checked(baseSource.X + (instance.CurrentFrameIndex * baseSource.Width)),
            sourceY,
            baseSource.Width,
            baseSource.Height
        );
        if (
            source.X < 0
            || source.Y < 0
            || source.Width <= 0
            || source.Height <= 0
            || source.Right > textureResource.Texture.Width
            || source.Bottom > textureResource.Texture.Height
        )
        {
            throw new InvalidOperationException(
                "shadow-projection.visual-source-out-of-range"
            );
        }

        var scale = (float)preview.DrawScale;
        var origin = new Vector2(pivot.X, pivot.Y);
        var layerDepth = Math.Clamp(
            ((float)instance.WorldPixel.Y + 64f) / 10000f,
            0f,
            1f
        );
        // DIAG-20260809: 无害形态默认 25% 透明度（与危险 50% 区分）；淡出时随实例 Alpha 递减。
        var drawColor = Color.White * instance.Alpha;
        // DIAG-20260812: 静息贴图只有默认朝右——面朝左时水平镜像（危险影怪转化时
        // 朝向不跳变；行走四方向行已是 Baked 预镜像，不重复翻转）。
        var effects = SpriteEffects.None;
        if (
            instance.AnimationState
                != ShadowCreatureHarmlessProjectionInstance.ShadowCreatureProjectionAnimationState.Moving
            && string.Equals(
                instance.FacingId,
                ShadowCreatureHarmlessProjectionCatalog.FacingLeft,
                StringComparison.Ordinal
            )
        )
        {
            effects = SpriteEffects.FlipHorizontally;
        }
        spriteBatch.Draw(
            textureResource.Texture,
            screenPixel,
            source,
            drawColor,
            0f,
            origin,
            scale,
            effects,
            layerDepth
        );

        if (!preview.IsPlaceholder && !preview.IsProvisional)
            return;

        var bounds = new Rectangle(
            (int)Math.Round(screenPixel.X - (pivot.X * scale)),
            (int)Math.Round(screenPixel.Y - (pivot.Y * scale)),
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale))
        );
        DrawPlaceholderOutline(spriteBatch, bounds);
    }

    private static void DrawPlaceholderOutline(
        SpriteBatch spriteBatch,
        Rectangle bounds
    )
    {
        const int thickness = 2;
        var color = Color.Magenta * 0.8f;
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(
                bounds.X,
                bounds.Bottom - thickness,
                bounds.Width,
                thickness
            ),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(
                bounds.Right - thickness,
                bounds.Y,
                thickness,
                bounds.Height
            ),
            color
        );
    }
}
