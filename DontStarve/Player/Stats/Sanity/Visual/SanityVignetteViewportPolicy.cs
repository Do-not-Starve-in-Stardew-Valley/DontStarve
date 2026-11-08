#nullable enable

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Defines the coordinate domain used by the HUD vignette. The renderer receives the current
/// screen's logical UI size, not the physical graphics-device viewport used for presentation.
/// </summary>
internal static class SanityVignetteViewportPolicy
{
    internal static (float X, float Y) GetCenter(
        int x,
        int y,
        int width,
        int height
    )
    {
        return (
            x + (width / 2f),
            y + (height / 2f)
        );
    }
}
