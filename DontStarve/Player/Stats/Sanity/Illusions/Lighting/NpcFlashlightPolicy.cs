#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Pure geometry and activation rules for the local NPC flashlight presentation. This is kept
/// independent from Stardew types so the visible contract can be exercised without a game client.
/// </summary>
internal static class NpcFlashlightPolicy
{
    internal const string KrobusName = "Krobus";
    internal const float BodyLightRadiusTiles = 1f;
    internal const float ConeRangeTiles = 5f;
    internal const int TileSizePixels = 64;
    internal const int ConeRangePixels = 320;
    internal const float ConeAngleDegrees = 45f;
    internal const float FullyDarkWhiteBlendThreshold = 0.999f;

    /// <summary>
    /// NPC lights begin only after the native base-light override has actually reached black.
    /// Mine dusk deliberately remains excluded because it retains 70 percent of its base light.
    /// </summary>
    internal static bool ShouldEmit(NpcFlashlightSceneInput input)
    {
        return input.Phase switch
        {
            NaturalDarknessPhase.FullDark => true,
            NaturalDarknessPhase.NightThreeSeconds
                or NaturalDarknessPhase.MineNight =>
                float.IsFinite(input.LightmapWhiteBlend)
                && input.LightmapWhiteBlend >= FullyDarkWhiteBlendThreshold,
            _ => false,
        };
    }

    internal static bool IsEligibleNpc(string? name, bool isMonster)
    {
        return !isMonster
            && !string.Equals(name, KrobusName, StringComparison.Ordinal);
    }

    /// <summary>Maps Stardew's standard 0=up, 1=right, 2=down, 3=left directions to radians.</summary>
    internal static float GetConeRotationRadians(int facingDirection)
    {
        return facingDirection switch
        {
            0 => -MathF.PI / 2f,
            1 => 0f,
            2 => MathF.PI / 2f,
            3 => MathF.PI,
            // A transient invalid direction should not leave the cone uninitialized. Stardew's
            // normal idle-facing default is down, which is the least surprising fallback.
            _ => MathF.PI / 2f,
        };
    }

    internal static float GetConeHalfWidthPixels(float forwardPixels)
    {
        if (!float.IsFinite(forwardPixels) || forwardPixels <= 0f)
            return 0f;

        return forwardPixels
            * MathF.Tan(ConeAngleDegrees * MathF.PI / 360f);
    }

    internal static int GetConeTextureHeightPixels()
    {
        return (int)MathF.Ceiling(GetConeHalfWidthPixels(ConeRangePixels) * 2f)
            + 2;
    }
}

/// <summary>Read-only scene facts supplied by the natural-darkness service.</summary>
internal readonly record struct NpcFlashlightSceneInput(
    NaturalDarknessPhase Phase,
    float LightmapWhiteBlend
);
