#nullable enable

using System;
using DontStarve.Resource.Sanity;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// Draws only the loader-owned DarkHand sheet and private action feedback on the current owner
/// screen. Placeholder/provisional metadata keeps its magenta development outline.
/// </summary>
internal sealed class DarkHandProjectionRenderer : IHarmlessProjectionWorldRenderer
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
            throw new InvalidOperationException("dark-hand.visual-resource-unavailable");
        }

        var baseSource = preview.SourceRectangle;
        var source = new Rectangle(
            checked(baseSource.X + (instance.CurrentFrameIndex * baseSource.Width)),
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
            throw new InvalidOperationException("dark-hand.visual-source-out-of-range");
        }

        var scale = (float)preview.DrawScale;
        var origin = new Vector2(pivot.X, pivot.Y);
        var layerDepth = Math.Clamp(
            ((float)instance.SpawnWorldPixel.Y + 64f) / 10000f,
            0f,
            1f
        );
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
            layerDepth
        );

        if (!string.IsNullOrWhiteSpace(instance.BehaviorLabel))
        {
            var labelSize = Game1.smallFont.MeasureString(instance.BehaviorLabel);
            spriteBatch.DrawString(
                Game1.smallFont,
                instance.BehaviorLabel,
                new Vector2(
                    screenPixel.X - (labelSize.X / 2f),
                    screenPixel.Y - (pivot.Y * scale) - labelSize.Y - 4f
                ),
                Color.LightGray
            );
        }

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

    private static void DrawPlaceholderOutline(SpriteBatch spriteBatch, Rectangle bounds)
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
            new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height),
            color
        );
    }
}
