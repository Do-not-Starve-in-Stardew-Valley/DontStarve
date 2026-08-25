using System.Text;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityWorldCompositionTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Frozen_threshold_layers_produce_world_only_progressive_parameters()
    {
        var low = Plan(
            ratio: 0.75d,
            SanityVisualLayerMask.LowSaturation,
            totalSeconds: 1d
        );
        Assert.Equal(SanityVisualLayerMask.LowSaturation, low.WorldLayers);
        Assert.InRange(
            low.Saturation,
            SanityWorldCompositionPlanner.MinimumProgressiveSaturation,
            0.9f
        );
        Assert.InRange(
            Math.Abs(low.OffsetX),
            0f,
            SanityWorldCompositionPlanner.MaximumDriftPixels
        );

        var shake = Plan(
            ratio: 0.6d,
            SanityVisualLayerMask.LowSaturation
                | SanityVisualLayerMask.ViewShake
                | SanityVisualLayerMask.DangerBorder,
            totalSeconds: 2d
        );
        Assert.Equal(
            SanityVisualLayerMask.None,
            shake.WorldLayers & SanityVisualLayerMask.DangerBorder
        );
        Assert.True(
            (shake.WorldLayers & SanityVisualLayerMask.ViewShake) != 0
        );
        Assert.InRange(
            Math.Abs(shake.OffsetX),
            0f,
            SanityWorldCompositionPlanner.MaximumDriftPixels
                + SanityWorldCompositionPlanner.MaximumShakePixels
        );

        var grayscale = Plan(
            ratio: 0.1d,
            SanityVisualLayerMask.All,
            totalSeconds: 3d
        );
        Assert.Equal(0f, grayscale.Saturation);
        Assert.True(
            grayscale.OverscanPixels
                <= SanityWorldCompositionPlanner.MaximumOverscanPixels
        );
    }

    [Fact]
    public void Low_sanity_colour_and_motion_use_the_full_bounded_quadratic_curve()
    {
        var atSeventyFive = Plan(
            ratio: 0.75d,
            SanityVisualLayerMask.LowSaturation,
            totalSeconds: 1d
        );
        var atFifteen = Plan(
            ratio: 0.15d,
            SanityVisualLayerMask.LowSaturation,
            totalSeconds: 1d
        );
        var atZero = Plan(
            ratio: 0d,
            SanityVisualLayerMask.LowSaturation,
            totalSeconds: 1d
        );

        Assert.InRange(atSeventyFive.DistortionAmount, 0.0468f, 0.0470f);
        Assert.InRange(atFifteen.DistortionAmount, 0.5418f, 0.5420f);
        Assert.InRange(atSeventyFive.InsanityColourBlend, 0.0624f, 0.0626f);
        Assert.InRange(atFifteen.InsanityColourBlend, 0.7224f, 0.7226f);
        Assert.Equal(
            SanityWorldCompositionPlanner.MaximumDistortionAmount,
            atZero.DistortionAmount
        );
        Assert.Equal(1f, atZero.InsanityColourBlend);
        Assert.NotEqual(0f, atFifteen.DistortionPhase);
    }

    [Fact]
    public void Disabling_screen_distortion_preserves_low_sanity_colour_without_motion()
    {
        var parameters = Plan(
            ratio: 0.15d,
            SanityVisualLayerMask.LowSaturation | SanityVisualLayerMask.ViewShake,
            totalSeconds: 1d,
            screenDistortionEnabled: false
        );

        Assert.InRange(parameters.InsanityColourBlend, 0.7224f, 0.7226f);
        Assert.Equal(0f, parameters.DistortionAmount);
        Assert.Equal(0f, parameters.DistortionPhase);
        Assert.Equal(0f, parameters.OffsetX);
        Assert.Equal(0f, parameters.OffsetY);
        Assert.Equal(1, parameters.OverscanPixels);
    }

    [Fact]
    public void Motion_frequencies_are_half_speed_and_independent_of_sanity_strength()
    {
        Assert.Equal(0.11d, SanityWorldCompositionPlanner.DriftCyclesPerSecond);
        Assert.Equal(1.55d, SanityWorldCompositionPlanner.ShakeXCyclesPerSecond);
        Assert.Equal(1.35d, SanityWorldCompositionPlanner.ShakeYCyclesPerSecond);
        Assert.Equal(25d, SanityWorldCompositionPlanner.DistortionRadiansPerSecond);
    }

    [Fact]
    public void Same_owner_seed_and_time_are_deterministic_and_other_screen_isolated()
    {
        var firstSeed = SanityWorldCompositionPlanner.CreateStableSeed(
            "1",
            0,
            Session
        );
        var sameSeed = SanityWorldCompositionPlanner.CreateStableSeed(
            "1",
            0,
            Session
        );
        var otherScreenSeed = SanityWorldCompositionPlanner.CreateStableSeed(
            "1",
            1,
            Session
        );
        Assert.Equal(firstSeed, sameSeed);
        Assert.NotEqual(firstSeed, otherScreenSeed);

        var snapshot = Snapshot(
            ratio: 0.4d,
            SanityVisualLayerMask.LowSaturation
                | SanityVisualLayerMask.ViewShake,
            screenId: 0
        );
        Assert.True(
            SanityWorldCompositionPlanner.TryCreate(
                snapshot,
                12.5d,
                firstSeed,
                out var first
            )
        );
        Assert.True(
            SanityWorldCompositionPlanner.TryCreate(
                snapshot,
                12.5d,
                sameSeed,
                out var duplicate
            )
        );
        Assert.Equal(first, duplicate);
    }

    [Fact]
    public void No_world_layer_and_invalid_time_short_circuit()
    {
        Assert.False(
            SanityWorldCompositionPlanner.TryCreate(
                Snapshot(
                    ratio: 0.15d,
                    SanityVisualLayerMask.DangerBorder,
                    screenId: 0
                ),
                1d,
                1u,
                out _
            )
        );
        Assert.False(
            SanityWorldCompositionPlanner.TryCreate(
                Snapshot(
                    ratio: 0.1d,
                    SanityVisualLayerMask.All,
                    screenId: 0
                ),
                double.NaN,
                1u,
                out _
            )
        );
    }

    [Fact]
    public void Renderer_gate_requires_exact_version_backend_and_three_method_shapes()
    {
        var zero = Shape(0);
        var one = Shape(1);
        Assert.Null(
            SanityRendererCapabilityGate.Validate(
                SanityRendererCapabilityGate.ExpectedGameVersion,
                SanityRendererCapabilityGate.ExpectedFrameworkVersion,
                openGlBackend: true,
                zero,
                one,
                zero
            )
        );
        Assert.Equal(
            "visual.renderer.game-version-mismatch",
            SanityRendererCapabilityGate.Validate(
                "1.6.16",
                SanityRendererCapabilityGate.ExpectedFrameworkVersion,
                true,
                zero,
                one,
                zero
            )
        );
        Assert.Equal(
            "visual.renderer.backend-mismatch",
            SanityRendererCapabilityGate.Validate(
                SanityRendererCapabilityGate.ExpectedGameVersion,
                SanityRendererCapabilityGate.ExpectedFrameworkVersion,
                false,
                zero,
                one,
                zero
            )
        );
        Assert.Equal(
            "visual.renderer.single-composition-signature-mismatch",
            SanityRendererCapabilityGate.Validate(
                SanityRendererCapabilityGate.ExpectedGameVersion,
                SanityRendererCapabilityGate.ExpectedFrameworkVersion,
                true,
                zero,
                zero,
                zero
            )
        );
    }

    [Fact]
    public void Embedded_effect_uses_localized_edge_distortion_and_relative_material_colour_grade()
    {
        var bytes = SanityWorldEffectBytecode.Bytes;
        Assert.Equal("MGFX", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(SanityWorldEffectBytecode.MgfxVersion, bytes[4]);
        Assert.Equal(SanityWorldEffectBytecode.OpenGlProfile, bytes[5]);
        Assert.Contains("uniform sampler2D ps_s0", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vFrontColor.a", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("source.a", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("smoothstep(0.315, 0.5, radius)", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vec2 shiftedUv", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("mix(source.rgb, distorted.rgb, amount)", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float insanityBlend", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vec3 ApplyInsanityColourGrade", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float chroma = maximum - minimum", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vec3 sourceChroma = worldColor - vec3(luma)", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float redDominance", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float greenDominance", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float blueDominance", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float yellowDominance", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("float lumaExposure", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vec3 phaseTint", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vec3 materialBias", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vec3 relativeColour", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("return clamp(relativeColour, vec3(0.0), vec3(1.0))", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("ApplyInsanityColourGrade(worldColor, sanityPhase)", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("mix(worldColor, insanityColor, insanityBlend)", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.DoesNotContain("dayChromaNeutral", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.DoesNotContain("RedTarget", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.DoesNotContain("saturation", SanityWorldEffectBytecode.FragmentShader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discard", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);

        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        Assert.Equal("MGFX", new string(reader.ReadChars(4)));
        Assert.Equal(SanityWorldEffectBytecode.MgfxVersion, reader.ReadByte());
        Assert.Equal(SanityWorldEffectBytecode.OpenGlProfile, reader.ReadByte());
        Assert.Equal(SanityWorldEffectBytecode.EffectKey, reader.ReadInt32());
        Assert.Equal(0, reader.ReadByte()); // constant buffers
        Assert.Equal(1, reader.ReadByte()); // shaders
        Assert.False(reader.ReadBoolean()); // pixel shader
        var shaderLength = reader.ReadInt32();
        Assert.Equal(
            SanityWorldEffectBytecode.FragmentShader,
            Encoding.ASCII.GetString(reader.ReadBytes(shaderLength))
        );
        Assert.Equal(0, reader.ReadByte()); // samplers
        Assert.Equal(0, reader.ReadByte()); // buffer refs
        Assert.Equal(0, reader.ReadByte()); // attributes
        Assert.Equal(0, reader.ReadByte()); // parameters
        Assert.Equal(1, reader.ReadByte()); // techniques
        Assert.Equal("SanityWorld", reader.ReadString());
        Assert.Equal(0, reader.ReadByte()); // annotations
        Assert.Equal(1, reader.ReadByte()); // passes
        Assert.Equal("WorldOnly", reader.ReadString());
        Assert.Equal(0, reader.ReadByte()); // annotations
        Assert.Equal(byte.MaxValue, reader.ReadByte()); // preserve SpriteBatch VS
        Assert.Equal(0, reader.ReadByte()); // pixel shader index
        Assert.False(reader.ReadBoolean());
        Assert.False(reader.ReadBoolean());
        Assert.False(reader.ReadBoolean());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    private static SanityWorldCompositionParameters Plan(
        double ratio,
        SanityVisualLayerMask layers,
        double totalSeconds,
        bool screenDistortionEnabled = true
    )
    {
        Assert.True(
            SanityWorldCompositionPlanner.TryCreate(
                Snapshot(ratio, layers, screenId: 0),
                totalSeconds,
                12345u,
                screenDistortionEnabled,
                out var parameters
            )
        );
        return parameters;
    }

    private static SanityVisualOwnerSnapshot Snapshot(
        double ratio,
        SanityVisualLayerMask layers,
        int screenId
    )
    {
        Assert.True(
            SanityNineSliceLayout.TryCreate(
                64,
                64,
                16,
                16,
                16,
                16,
                1920,
                1080,
                out var border,
                out _
            )
        );
        return new SanityVisualOwnerSnapshot(
            Key: new SanityVisualOwnerKey("1", screenId, Session),
            Revision: 1,
            Ratio: ratio,
            RequestedLayers: layers,
            RenderableLayers: layers,
            EffectiveSanityOverrideActive: false,
            ViewportWidth: 1920,
            ViewportHeight: 1080,
            DangerBorderLayout: border,
            IdleEligible: ratio < 0.5d,
            Idle: new SanityIdleSnapshot(TimeSpan.Zero, false, "idle.test")
        );
    }

    private static SanityRendererMethodShape Shape(int parameterCount)
    {
        return new SanityRendererMethodShape(
            Exists: true,
            IsStatic: false,
            IsVirtual: true,
            ReturnsExpectedType: true,
            parameterCount,
            ParametersMatch: true
        );
    }
}
