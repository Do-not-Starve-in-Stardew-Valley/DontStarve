#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// A local LightSource whose draw shape is a narrow forward cone instead of one of Stardew's
/// radial light textures. It still draws during Game1.DrawLighting, so its result participates in
/// the native lightmap and can illuminate the player inside the beam.
/// </summary>
internal sealed class NpcFlashlightConeLightSource : LightSource
{
    private static Texture2D? coneTexture;
    private static GraphicsDevice? coneTextureGraphicsDevice;
    private static bool textureBuildFailureLogged;

    private float rotationRadians;

    internal NpcFlashlightConeLightSource(
        string id,
        Vector2 position,
        int facingDirection,
        string onlyLocation
    )
        : base(
            id,
            LightSource.lantern,
            position,
            NpcFlashlightPolicy.ConeRangeTiles,
            Color.Black,
            LightContext.None,
            0L,
            onlyLocation
        )
    {
        rotationRadians = NpcFlashlightPolicy.GetConeRotationRadians(
            facingDirection
        );
    }

    internal void Update(Vector2 position, int facingDirection)
    {
        this.position.Value = position;
        rotationRadians = NpcFlashlightPolicy.GetConeRotationRadians(
            facingDirection
        );
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        GameLocation location,
        float lightMultiplier
    )
    {
        _ = location;
        if (!IsOnScreen())
            return;

        var texture = GetCachedConeTexture(spriteBatch.GraphicsDevice);
        if (texture is null)
            return;

        var lightingQuality = Game1.options.lightingQuality;
        var lightmapPixelScale = lightingQuality / 2;
        if (lightmapPixelScale <= 0)
            return;

        spriteBatch.Draw(
            texture,
            Game1.GlobalToLocal(Game1.viewport, position.Value)
                / lightmapPixelScale,
            texture.Bounds,
            color.Value * lightMultiplier,
            rotationRadians,
            new Vector2(0f, texture.Height * 0.5f),
            1f / lightmapPixelScale,
            SpriteEffects.None,
            0.9f
        );
    }

    internal static void ReleaseCachedTexture()
    {
        var texture = coneTexture;
        coneTexture = null;
        coneTextureGraphicsDevice = null;
        textureBuildFailureLogged = false;
        if (texture is not null && !texture.IsDisposed)
            texture.Dispose();
    }

    /// <summary>
    /// Builds the shared texture from the update path before Game1 begins the lightmap SpriteBatch.
    /// The draw override then only consumes the cached texture.
    /// </summary>
    internal static void EnsureCachedTexture(IMonitor monitor)
    {
        if (Game1.graphics?.GraphicsDevice is not GraphicsDevice graphicsDevice)
            return;

        _ = GetOrCreateConeTexture(graphicsDevice, monitor);
    }

    private static Texture2D? GetCachedConeTexture(GraphicsDevice graphicsDevice)
    {
        return coneTexture is not null
            && !coneTexture.IsDisposed
            && ReferenceEquals(coneTextureGraphicsDevice, graphicsDevice)
            ? coneTexture
            : null;
    }

    private static Texture2D? GetOrCreateConeTexture(
        GraphicsDevice graphicsDevice,
        IMonitor monitor
    )
    {
        var existing = GetCachedConeTexture(graphicsDevice);
        if (existing is not null)
            return existing;

        try
        {
            if (coneTexture is not null && !coneTexture.IsDisposed)
                coneTexture.Dispose();

            var texture = BuildConeTexture(graphicsDevice);
            coneTexture = texture;
            coneTextureGraphicsDevice = graphicsDevice;
            textureBuildFailureLogged = false;
            return texture;
        }
        catch (Exception exception)
        {
            if (!textureBuildFailureLogged)
            {
                textureBuildFailureLogged = true;
                monitor.Log(
                    $"NPC flashlight cone texture creation failed closed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Warn
                );
            }
            return null;
        }
    }

    private static Texture2D BuildConeTexture(GraphicsDevice graphicsDevice)
    {
        var width = NpcFlashlightPolicy.ConeRangePixels;
        var height = NpcFlashlightPolicy.GetConeTextureHeightPixels();
        var pixels = new Color[width * height];
        var centerY = (height - 1) * 0.5f;
        var finalX = width - 1f;

        for (var x = 1; x < width; x++)
        {
            var forwardPixels = (float)x;
            var halfWidth = NpcFlashlightPolicy.GetConeHalfWidthPixels(
                forwardPixels
            );
            if (halfWidth <= 0f)
                continue;

            var forwardRatio = forwardPixels / finalX;
            // A soft tip and edge make this feel like a light beam rather than a solid polygon,
            // while the outside boundary still remains the requested 45 degree fan.
            var forwardFade = 1f - forwardRatio * forwardRatio;
            for (var y = 0; y < height; y++)
            {
                var lateralRatio = MathF.Abs(y - centerY) / halfWidth;
                if (lateralRatio >= 1f)
                    continue;

                var edgeFade = Math.Clamp((1f - lateralRatio) * 6f, 0f, 1f);
                var alpha = (byte)Math.Clamp(
                    (int)MathF.Round(255f * forwardFade * edgeFade),
                    0,
                    255
                );
                pixels[y * width + x] = new Color(
                    byte.MaxValue,
                    byte.MaxValue,
                    byte.MaxValue,
                    alpha
                );
            }
        }

        var texture = new Texture2D(graphicsDevice, width, height);
        texture.SetData(pixels);
        return texture;
    }
}
