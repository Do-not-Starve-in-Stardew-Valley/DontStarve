using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve;

internal static class TextureLoader
{
    private static Texture2D _sanityFiller;
    private static Texture2D _hungerFiller;
    public static Texture2D SanityContainer { get; private set; } = null!;
    public static Texture2D HungerContainer { get; private set; } = null!;
    private static Color? sanityCache;
    private static Color? hungerCache;
    public const int ContainerScale = 4;
    public const int FillerWidthMultiplier = 6;
    public static int HungerContainerScaledWidth => HungerContainer.Width * ContainerScale;
    public static int HungerContainerScaledHeight => HungerContainer.Height * ContainerScale;
    public static int SanityContainerScaledWidth => SanityContainer.Width * ContainerScale;
    public static int SanityContainerScaledHeight => SanityContainer.Height * ContainerScale;
    public static int HungerFillerScaledWidth =>
        HungerFiller.Width * FillerWidthMultiplier * Game1.pixelZoom;
    public static int SanityFillerScaledWidth =>
        SanityFiller.Width * FillerWidthMultiplier * Game1.pixelZoom;

    /// <summary>
    /// Gets the 1x1 sanity filler texture
    /// </summary>
    internal static Texture2D SanityFiller
    {
        get
        {
            ArgumentNullException.ThrowIfNull(_sanityFiller);
            var color = Brushes.SanityBrush;
            if (!sanityCache.HasValue || sanityCache.Value != color)
            {
                _sanityFiller.SetData(new[] { color });
                sanityCache = color;
            }
            return _sanityFiller;
        }
    }

    /// <summary>
    /// Gets the 1x1 hunger filler texture
    /// </summary>
    internal static Texture2D HungerFiller
    {
        get
        {
            ArgumentNullException.ThrowIfNull(_hungerFiller);
            var color = Brushes.HungerBrush;
            if (!hungerCache.HasValue || hungerCache.Value != color)
            {
                _hungerFiller.SetData(new[] { color });
                hungerCache = color;
            }
            return _hungerFiller;
        }
    }

    /// <summary>
    /// Initialize textures and reset color caches
    /// </summary>
    internal static void Initialize(IModContentHelper modContent)
    {
        sanityCache = null;
        hungerCache = null;

        SanityContainer = modContent.Load<Texture2D>("assets/sanity/container.png");
        HungerContainer = modContent.Load<Texture2D>("assets/hunger/container.png");

        _sanityFiller = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
        _hungerFiller = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
    }
}
