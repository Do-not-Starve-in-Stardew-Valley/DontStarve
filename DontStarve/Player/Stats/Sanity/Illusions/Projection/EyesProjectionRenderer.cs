#nullable enable

using System;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Draws the one loader-owned Eyes blink sheet on the exact owner screen. Placeholder/provisional
/// resources stay visibly outlined and are never presented as final art.
/// </summary>
internal sealed class EyesProjectionRenderer : IHarmlessProjectionWorldRenderer
{
    public void Draw(
        SpriteBatch spriteBatch,
        HarmlessProjectionInstance instance,
        Vector2 screenPixel
    )
    {
        ArgumentNullException.ThrowIfNull(spriteBatch);
        ArgumentNullException.ThrowIfNull(instance);
        var resource = instance.VisualResource;
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
            throw new InvalidOperationException("eyes.visual-resource-unavailable");
        }

        var baseSource = preview.SourceRectangle;
        var source = new Rectangle(
            checked(
                baseSource.X
                    + (instance.CurrentFrameIndex * baseSource.Width)
            ),
            baseSource.Y,
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
            throw new InvalidOperationException("eyes.visual-source-out-of-range");
        }

        var scale = (float)preview.DrawScale;
        var origin = new Vector2(pivot.X, pivot.Y);
        spriteBatch.Draw(
            textureResource.Texture,
            screenPixel,
            source,
            Color.White,
            0f,
            origin,
            scale,
            instance.FacingX < 0
                ? SpriteEffects.FlipHorizontally
                : SpriteEffects.None,
            1f
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
