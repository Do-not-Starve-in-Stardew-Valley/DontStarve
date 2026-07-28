#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Frozen production contract for the Stardew 1.6.15 final-lightmap evaluator. The evaluator reads
/// only the owner-foot texels from the already-rendered lightmap; it never reads back the full
/// render target.
/// </summary>
internal static class EnvironmentLightProductionContract
{
    internal const string SupportedGameVersion = "1.6.15";
    internal const string EvaluatorRevision =
        "stardew-1.6.15-owner-foot-lightmap-readback-v1";
    internal const string RuleRevision =
        "environment-light-final-visibility-v1:0.2200:0.2800:0.4800:0.5500";
    internal const int NormalSampleCadenceTicks = 15;
    internal const int DangerousSampleCadenceTicks = 4;
    internal const int MaximumSampleAgeTicks = 20;
    internal const int MaximumScreens = 16;
    internal const int ReadbackWidth = 2;
    internal const int ReadbackHeight = 2;
}

/// <summary>
/// Centralized and clamped final-visibility thresholds. Pitch-black authorization uses the strict
/// enter threshold even while the display classification is inside its exit hysteresis band.
/// </summary>
internal sealed record EnvironmentLightThresholds
{
    private EnvironmentLightThresholds(
        double pitchBlackEnter,
        double pitchBlackExit,
        double litExit,
        double litEnter
    )
    {
        PitchBlackEnter = pitchBlackEnter;
        PitchBlackExit = pitchBlackExit;
        LitExit = litExit;
        LitEnter = litEnter;
    }

    internal static EnvironmentLightThresholds Default { get; } =
        CreateClamped(0.22d, 0.28d, 0.48d, 0.55d);

    internal double PitchBlackEnter { get; }

    internal double PitchBlackExit { get; }

    internal double LitExit { get; }

    internal double LitEnter { get; }

    internal string RuleRevision => EnvironmentLightProductionContract.RuleRevision;

    internal static EnvironmentLightThresholds CreateClamped(
        double pitchBlackEnter,
        double pitchBlackExit,
        double litExit,
        double litEnter
    )
    {
        const double gap = 0.01d;
        var blackEnter = ClampFinite(pitchBlackEnter, 0.05d, 0.45d, 0.22d);
        var blackExit = ClampFinite(
            pitchBlackExit,
            blackEnter + gap,
            0.55d,
            0.28d
        );
        var visibleExit = ClampFinite(
            litExit,
            blackExit + gap,
            0.90d,
            0.48d
        );
        var visibleEnter = ClampFinite(
            litEnter,
            visibleExit + gap,
            0.95d,
            0.55d
        );
        return new EnvironmentLightThresholds(
            blackEnter,
            blackExit,
            visibleExit,
            visibleEnter
        );
    }

    private static double ClampFinite(
        double value,
        double minimum,
        double maximum,
        double fallback
    )
    {
        var candidate = double.IsFinite(value) ? value : fallback;
        return Math.Clamp(candidate, minimum, maximum);
    }
}

/// <summary>
/// Immutable owner/screen/location sample from the standard Stardew lightmap. No RenderTarget2D,
/// Farmer, GameLocation, viewport, or graphics-device reference crosses this boundary.
/// </summary>
internal sealed record EnvironmentLightFinalVisibilitySnapshot
{
    private EnvironmentLightFinalVisibilitySnapshot(
        EnvironmentLightCapabilityStatus capabilityStatus,
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        double visibilityScore,
        double darknessRed,
        double darknessGreen,
        double darknessBlue,
        bool standardLightingDrawn,
        bool rainOverlayApplied,
        int lightingQuality,
        double zoomLevel,
        bool useUnscaledLighting,
        long capturedAtTick,
        long rendererRevision,
        string evaluatorRevision,
        string reason
    )
    {
        CapabilityStatus = capabilityStatus;
        PlayerKey = playerKey ?? string.Empty;
        ScreenId = screenId;
        LocationNameOrUniqueName = locationNameOrUniqueName ?? string.Empty;
        LocationInstanceId = locationInstanceId;
        VisibilityScore = visibilityScore;
        DarknessRed = darknessRed;
        DarknessGreen = darknessGreen;
        DarknessBlue = darknessBlue;
        StandardLightingDrawn = standardLightingDrawn;
        RainOverlayApplied = rainOverlayApplied;
        LightingQuality = lightingQuality;
        ZoomLevel = zoomLevel;
        UseUnscaledLighting = useUnscaledLighting;
        CapturedAtTick = capturedAtTick;
        RendererRevision = rendererRevision;
        EvaluatorRevision = evaluatorRevision ?? string.Empty;
        Reason = reason ?? string.Empty;
    }

    internal EnvironmentLightCapabilityStatus CapabilityStatus { get; }

    internal string PlayerKey { get; }

    internal int ScreenId { get; }

    internal string LocationNameOrUniqueName { get; }

    internal long LocationInstanceId { get; }

    internal double VisibilityScore { get; }

    internal double DarknessRed { get; }

    internal double DarknessGreen { get; }

    internal double DarknessBlue { get; }

    internal bool StandardLightingDrawn { get; }

    internal bool RainOverlayApplied { get; }

    internal int LightingQuality { get; }

    internal double ZoomLevel { get; }

    internal bool UseUnscaledLighting { get; }

    internal long CapturedAtTick { get; }

    internal long RendererRevision { get; }

    internal string EvaluatorRevision { get; }

    internal string Reason { get; }

    internal bool IsConfirmed =>
        CapabilityStatus == EnvironmentLightCapabilityStatus.Available
        && SanityPlayerKey.IsCanonical(PlayerKey)
        && ScreenId >= 0
        && !string.IsNullOrWhiteSpace(LocationNameOrUniqueName)
        && double.IsFinite(VisibilityScore)
        && VisibilityScore is >= 0d and <= 1d
        && double.IsFinite(DarknessRed)
        && DarknessRed is >= 0d and <= 1d
        && double.IsFinite(DarknessGreen)
        && DarknessGreen is >= 0d and <= 1d
        && double.IsFinite(DarknessBlue)
        && DarknessBlue is >= 0d and <= 1d
        && LightingQuality >= 2
        && double.IsFinite(ZoomLevel)
        && ZoomLevel > 0d
        && CapturedAtTick >= 0
        && RendererRevision > 0
        && string.Equals(
            EvaluatorRevision,
            EnvironmentLightProductionContract.EvaluatorRevision,
            StringComparison.Ordinal
        )
        && !string.IsNullOrWhiteSpace(Reason);

    internal static EnvironmentLightFinalVisibilitySnapshot Confirmed(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        double visibilityScore,
        double darknessRed,
        double darknessGreen,
        double darknessBlue,
        bool standardLightingDrawn,
        bool rainOverlayApplied,
        int lightingQuality,
        double zoomLevel,
        bool useUnscaledLighting,
        long capturedAtTick,
        long rendererRevision,
        string reason
    )
    {
        return new EnvironmentLightFinalVisibilitySnapshot(
            EnvironmentLightCapabilityStatus.Available,
            playerKey,
            screenId,
            locationNameOrUniqueName,
            locationInstanceId,
            visibilityScore,
            darknessRed,
            darknessGreen,
            darknessBlue,
            standardLightingDrawn,
            rainOverlayApplied,
            lightingQuality,
            zoomLevel,
            useUnscaledLighting,
            capturedAtTick,
            rendererRevision,
            EnvironmentLightProductionContract.EvaluatorRevision,
            reason
        );
    }

    internal static EnvironmentLightFinalVisibilitySnapshot Unavailable(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        long capturedAtTick,
        string reason,
        EnvironmentLightCapabilityStatus capabilityStatus =
            EnvironmentLightCapabilityStatus.Unavailable
    )
    {
        if (capabilityStatus == EnvironmentLightCapabilityStatus.Available)
            capabilityStatus = EnvironmentLightCapabilityStatus.Invalid;
        return new EnvironmentLightFinalVisibilitySnapshot(
            capabilityStatus,
            playerKey,
            screenId,
            locationNameOrUniqueName,
            locationInstanceId,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            standardLightingDrawn: false,
            rainOverlayApplied: false,
            lightingQuality: 0,
            zoomLevel: double.NaN,
            useUnscaledLighting: false,
            capturedAtTick,
            rendererRevision: 0,
            EnvironmentLightProductionContract.EvaluatorRevision,
            string.IsNullOrWhiteSpace(reason)
                ? EnvironmentLightReasonIds.FinalVisibilityUnavailable
                : reason
        );
    }
}

internal interface IEnvironmentLightFinalVisibilityProvider
{
    event Action<string, int>? SampleUpdated;

    EnvironmentLightFinalVisibilitySnapshot GetLatest(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        long currentTick
    );
}

internal sealed class UnavailableEnvironmentLightFinalVisibilityProvider
    : IEnvironmentLightFinalVisibilityProvider
{
    public event Action<string, int>? SampleUpdated
    {
        add { }
        remove { }
    }

    public EnvironmentLightFinalVisibilitySnapshot GetLatest(
        string playerKey,
        int screenId,
        string locationNameOrUniqueName,
        long locationInstanceId,
        long currentTick
    )
    {
        return EnvironmentLightFinalVisibilitySnapshot.Unavailable(
            playerKey,
            screenId,
            locationNameOrUniqueName,
            locationInstanceId,
            Math.Max(0L, currentTick),
            EnvironmentLightReasonIds.FinalVisibilityUnavailable
        );
    }
}

internal static class EnvironmentLightVisibilityMath
{
    private const double RedLuminance = 0.2126d;
    private const double GreenLuminance = 0.7152d;
    private const double BlueLuminance = 0.0722d;
    private const double RainTintScale = 0.45d;
    private const double OrangeRedGreen = 69d / 255d;

    internal static double ComputeVisibilityScore(
        double darknessRed,
        double darknessGreen,
        double darknessBlue,
        bool rainOverlayApplied
    )
    {
        var red = Clamp01(darknessRed);
        var green = Clamp01(darknessGreen);
        var blue = Clamp01(darknessBlue);
        var rainRed = rainOverlayApplied ? RainTintScale : 0d;
        var rainGreen = rainOverlayApplied
            ? OrangeRedGreen * RainTintScale
            : 0d;

        // Stardew composites its lightmap with ReverseSubtract, SourceColor, One. Against a white
        // reference pixel the retained channel is therefore 1 - darkness^2. The rain overlay is
        // drawn in the same batch and contributes one more squared subtraction.
        var retainedRed = Clamp01(1d - (red * red) - (rainRed * rainRed));
        var retainedGreen = Clamp01(
            1d - (green * green) - (rainGreen * rainGreen)
        );
        var retainedBlue = Clamp01(1d - (blue * blue));
        return Clamp01(
            (retainedRed * RedLuminance)
                + (retainedGreen * GreenLuminance)
                + (retainedBlue * BlueLuminance)
        );
    }

    internal static EnvironmentLightLevel Classify(
        double visibilityScore,
        EnvironmentLightThresholds thresholds,
        EnvironmentLightLevel? previousLevel = null
    )
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (!double.IsFinite(visibilityScore))
            return EnvironmentLightLevel.Dim;
        var score = Clamp01(visibilityScore);

        if (
            previousLevel == EnvironmentLightLevel.PitchBlack
            && score < thresholds.PitchBlackExit
        )
        {
            return EnvironmentLightLevel.PitchBlack;
        }
        if (
            previousLevel == EnvironmentLightLevel.Lit
            && score > thresholds.LitExit
        )
        {
            return EnvironmentLightLevel.Lit;
        }
        if (score <= thresholds.PitchBlackEnter)
            return EnvironmentLightLevel.PitchBlack;
        if (score >= thresholds.LitEnter)
            return EnvironmentLightLevel.Lit;
        return EnvironmentLightLevel.Dim;
    }

    internal static bool CanAuthorizePitchBlack(
        double visibilityScore,
        EnvironmentLightThresholds thresholds
    )
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        return double.IsFinite(visibilityScore)
            && visibilityScore <= thresholds.PitchBlackEnter;
    }

    internal static EnvironmentLightColor Interpolate(
        EnvironmentLightColor topLeft,
        EnvironmentLightColor topRight,
        EnvironmentLightColor bottomLeft,
        EnvironmentLightColor bottomRight,
        double fractionX,
        double fractionY
    )
    {
        var x = Clamp01(fractionX);
        var y = Clamp01(fractionY);
        return new EnvironmentLightColor(
            ToByte(Bilinear(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R, x, y)),
            ToByte(Bilinear(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G, x, y)),
            ToByte(Bilinear(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B, x, y)),
            ToByte(Bilinear(topLeft.A, topRight.A, bottomLeft.A, bottomRight.A, x, y))
        );
    }

    private static double Bilinear(
        byte topLeft,
        byte topRight,
        byte bottomLeft,
        byte bottomRight,
        double x,
        double y
    )
    {
        var top = topLeft + ((topRight - topLeft) * x);
        var bottom = bottomLeft + ((bottomRight - bottomLeft) * x);
        return top + ((bottom - top) * y);
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
            return 0d;
        return Math.Clamp(value, 0d, 1d);
    }
}
