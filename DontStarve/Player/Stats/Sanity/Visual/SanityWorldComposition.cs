#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Visual;

internal enum SanityRendererCapabilityStatus
{
    Available,
    Unavailable,
}

internal readonly record struct SanityRendererMethodShape(
    bool Exists,
    bool IsStatic,
    bool IsVirtual,
    bool ReturnsExpectedType,
    int ParameterCount,
    bool ParametersMatch
);

internal readonly record struct SanityRendererCapability(
    SanityRendererCapabilityStatus Status,
    string Reason,
    string GameVersion,
    string FrameworkVersion,
    string Backend,
    string PatchOwnerId
)
{
    internal bool IsAvailable =>
        Status == SanityRendererCapabilityStatus.Available;
}

/// <summary>
/// Exact Stardew/MonoGame contract for the instance-owned final-composition adapter. The runtime
/// performs reflection once during installation and feeds only the resulting shapes into this
/// deterministic gate.
/// </summary>
internal static class SanityRendererCapabilityGate
{
    internal const string ExpectedGameVersion = "1.6.15";
    internal const string ExpectedFrameworkVersion = "3.8.0.1641";
    internal const string ExpectedBackend = "OpenGL";
    internal const string ExpectedShouldDrawSignature =
        "Game1.ShouldDrawOnBuffer():System.Boolean";
    internal const string ExpectedSingleCompositionSignature =
        "Game1.renderScreenBuffer(RenderTarget2D):System.Void";
    internal const string ExpectedSplitCompositionSignature =
        "Game1.DrawSplitScreenWindow():System.Void";

    internal static string? Validate(
        string? gameVersion,
        string? frameworkVersion,
        bool openGlBackend,
        SanityRendererMethodShape shouldDraw,
        SanityRendererMethodShape singleComposition,
        SanityRendererMethodShape splitComposition
    )
    {
        if (!string.Equals(gameVersion, ExpectedGameVersion, StringComparison.Ordinal))
            return "visual.renderer.game-version-mismatch";
        if (
            !string.Equals(
                frameworkVersion,
                ExpectedFrameworkVersion,
                StringComparison.Ordinal
            )
        )
        {
            return "visual.renderer.framework-version-mismatch";
        }
        if (!openGlBackend)
            return "visual.renderer.backend-mismatch";
        if (!IsExpected(shouldDraw, expectedParameterCount: 0))
            return "visual.renderer.should-draw-signature-mismatch";
        if (!IsExpected(singleComposition, expectedParameterCount: 1))
            return "visual.renderer.single-composition-signature-mismatch";
        if (!IsExpected(splitComposition, expectedParameterCount: 0))
            return "visual.renderer.split-composition-signature-mismatch";
        return null;
    }

    private static bool IsExpected(
        SanityRendererMethodShape shape,
        int expectedParameterCount
    )
    {
        return shape.Exists
            && !shape.IsStatic
            && shape.IsVirtual
            && shape.ReturnsExpectedType
            && shape.ParameterCount == expectedParameterCount
            && shape.ParametersMatch;
    }
}

internal readonly record struct SanityWorldCompositionParameters(
    int ScreenId,
    long Revision,
    SanityVisualLayerMask WorldLayers,
    float Saturation,
    float InsanityColourBlend,
    float DistortionAmount,
    float DistortionPhase,
    float OffsetX,
    float OffsetY,
    int OverscanPixels
)
{
    internal bool IsActive => WorldLayers != SanityVisualLayerMask.None;
}

/// <summary>
/// Pure, allocation-free parameter planner. It never owns a camera or render target: the runtime
/// applies these bounded values only while composing the matching Game1.instanceId world buffer.
/// </summary>
internal static class SanityWorldCompositionPlanner
{
    internal const int ConfigurationRevision = 2;
    internal const float MinimumProgressiveSaturation = 0.5f;
    internal const float MaximumDriftPixels = 1.25f;
    internal const float MaximumShakePixels = 2f;
    internal const int MaximumOverscanPixels = 5;
    internal const float MaximumDistortionAmount = 0.75f;

    private const double Tau = Math.PI * 2d;
    internal const double DriftCyclesPerSecond = 0.11d;
    internal const double ShakeXCyclesPerSecond = 1.55d;
    internal const double ShakeYCyclesPerSecond = 1.35d;
    internal const double DistortionRadiansPerSecond = 25d;
    // The legacy LowSaturation layer ID remains the compatibility trigger for its existing
    // drift curve. Saturation is still planned for a future opt-in pass, but rendering now uses
    // this layer only for the localized edge-distortion curve below.
    private const SanityVisualLayerMask WorldMask =
        SanityVisualLayerMask.LowSaturation
        | SanityVisualLayerMask.ViewShake
        | SanityVisualLayerMask.Grayscale;

    internal static bool TryCreate(
        SanityVisualOwnerSnapshot snapshot,
        double totalSeconds,
        uint stableSeed,
        out SanityWorldCompositionParameters parameters
    )
    {
        return TryCreate(
            snapshot,
            totalSeconds,
            stableSeed,
            screenDistortionEnabled: true,
            out parameters
        );
    }

    internal static bool TryCreate(
        SanityVisualOwnerSnapshot snapshot,
        double totalSeconds,
        uint stableSeed,
        bool screenDistortionEnabled,
        out SanityWorldCompositionParameters parameters
    )
    {
        parameters = default;
        if (!double.IsFinite(totalSeconds) || totalSeconds < 0d)
            return false;

        var worldLayers = snapshot.RenderableLayers & WorldMask;
        if (worldLayers == SanityVisualLayerMask.None)
            return false;

        var ratio = Math.Clamp(snapshot.Ratio, 0d, 1d);
        var phase = SeedPhase(stableSeed);
        var lowSanProgress = Math.Clamp((0.75d - ratio) / 0.65d, 0d, 1d);
        var saturation = 1f;
        var insanityColourBlend = 0f;
        var distortionAmount = 0f;
        var distortionPhase = 0f;
        var driftX = 0f;
        var driftY = 0f;
        if ((worldLayers & SanityVisualLayerMask.LowSaturation) != 0)
        {
            saturation = (float)Math.Clamp(
                0.9d - (0.4d * lowSanProgress),
                MinimumProgressiveSaturation,
                0.9d
            );
            var remainingSanity = 1d - ratio;
            var insanityBlend = Math.Clamp(
                remainingSanity * remainingSanity,
                0d,
                1d
            );
            insanityColourBlend = (float)insanityBlend;
            if (screenDistortionEnabled)
            {
                var driftAmplitude =
                    0.35d + ((MaximumDriftPixels - 0.35d) * lowSanProgress);
                driftX = (float)(
                    Math.Sin((totalSeconds * Tau * DriftCyclesPerSecond) + phase)
                    * driftAmplitude
                );
                driftY = (float)(
                    Math.Cos(
                        (totalSeconds * Tau * DriftCyclesPerSecond * 0.73d)
                        + (phase * 1.31d)
                    )
                    * driftAmplitude
                    * 0.65d
                );
                distortionAmount = (float)Math.Clamp(
                    MaximumDistortionAmount * insanityBlend,
                    0d,
                    MaximumDistortionAmount
                );
                var distortionRadians =
                    (totalSeconds * DistortionRadiansPerSecond) + phase;
                distortionPhase = (float)(
                    (distortionRadians % Tau) / Tau
                );
            }
        }

        var shakeX = 0f;
        var shakeY = 0f;
        if (
            screenDistortionEnabled
            && (worldLayers & SanityVisualLayerMask.ViewShake) != 0
        )
        {
            var shakeProgress = Math.Clamp((0.6d - ratio) / 0.45d, 0d, 1d);
            var shakeAmplitude =
                0.75d + ((MaximumShakePixels - 0.75d) * shakeProgress);
            shakeX = (float)Math.Round(
                Math.Sin(
                    (totalSeconds * Tau * ShakeXCyclesPerSecond)
                    + (phase * 2.17d)
                )
                * shakeAmplitude,
                MidpointRounding.AwayFromZero
            );
            shakeY = (float)Math.Round(
                Math.Cos(
                    (totalSeconds * Tau * ShakeYCyclesPerSecond)
                    + (phase * 1.79d)
                )
                * shakeAmplitude,
                MidpointRounding.AwayFromZero
            );
        }

        if ((worldLayers & SanityVisualLayerMask.Grayscale) != 0)
            saturation = 0f;

        var offsetX = Math.Clamp(
            driftX + shakeX,
            -(MaximumDriftPixels + MaximumShakePixels),
            MaximumDriftPixels + MaximumShakePixels
        );
        var offsetY = Math.Clamp(
            driftY + shakeY,
            -(MaximumDriftPixels + MaximumShakePixels),
            MaximumDriftPixels + MaximumShakePixels
        );
        var overscan = Math.Clamp(
            (int)Math.Ceiling(Math.Max(Math.Abs(offsetX), Math.Abs(offsetY))) + 1,
            1,
            MaximumOverscanPixels
        );

        parameters = new SanityWorldCompositionParameters(
            snapshot.Key.ScreenId,
            snapshot.Revision,
            worldLayers,
            Math.Clamp(saturation, 0f, 1f),
            Math.Clamp(insanityColourBlend, 0f, 1f),
            Math.Clamp(distortionAmount, 0f, MaximumDistortionAmount),
            Math.Clamp(distortionPhase, 0f, 1f),
            offsetX,
            offsetY,
            overscan
        );
        return true;
    }

    internal static uint CreateStableSeed(
        string playerKey,
        int screenId,
        string sessionId
    )
    {
        var hash = 2166136261u;
        AddText(ref hash, playerKey);
        AddInt32(ref hash, screenId);
        AddText(ref hash, sessionId);
        return hash == 0u ? 1u : hash;
    }

    private static double SeedPhase(uint seed)
    {
        return (seed / (double)uint.MaxValue) * Tau;
    }

    private static void AddText(ref uint hash, string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            hash ^= value[index];
            hash *= 16777619u;
        }
    }

    private static void AddInt32(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (byte)value;
            hash *= 16777619u;
            hash ^= (byte)(value >> 8);
            hash *= 16777619u;
            hash ^= (byte)(value >> 16);
            hash *= 16777619u;
            hash ^= (byte)(value >> 24);
            hash *= 16777619u;
        }
    }
}
