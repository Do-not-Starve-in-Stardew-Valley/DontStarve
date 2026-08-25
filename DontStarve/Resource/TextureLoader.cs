using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Resource;

internal static class TextureLoader
{
    // filler 是 1x1 纹理，每次颜色变化时 SetData 后拉伸绘制；不要在 HUD 绘制中反复 new Texture2D。
    private static Texture2D _sanityFiller;
    private static Texture2D _hungerFiller;
    public static Texture2D SanityContainer { get; private set; } = null!;
    public static Texture2D HungerContainer { get; private set; } = null!;
    public static Texture2D SanityBrain { get; private set; } = null!;
    public static Texture2D HungerIcon { get; private set; } = null!;
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
    /// 获取理智条 1x1 填充纹理；颜色随当前理智值变化但按颜色缓存。
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
    /// 获取饥饿条 1x1 填充纹理；颜色随当前饥饿值变化但按颜色缓存。
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
    /// 初始化 HUD 贴图并重置颜色缓存。
    /// </summary>
    internal static void Initialize(IModHelper helper)
    {
        sanityCache = null;
        hungerCache = null;

        SanityContainer = helper.ModContent.Load<Texture2D>("Asset/Sanity/container.png");
        HungerContainer = helper.ModContent.Load<Texture2D>("Asset/Hunger/container.png");
        SanityBrain = helper.ModContent.Load<Texture2D>("Asset/Sanity/sanity.png");
        HungerIcon = helper.ModContent.Load<Texture2D>("Asset/Hunger/hunger.png");

        _sanityFiller = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
        _hungerFiller = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
    }
}
