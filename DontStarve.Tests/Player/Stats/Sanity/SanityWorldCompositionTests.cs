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
    public void Embedded_effect_is_open_gl_mgfx_v9_and_preserves_source_alpha()
    {
        var bytes = SanityWorldEffectBytecode.Bytes;
        Assert.Equal("MGFX", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(SanityWorldEffectBytecode.MgfxVersion, bytes[4]);
        Assert.Equal(SanityWorldEffectBytecode.OpenGlProfile, bytes[5]);
        Assert.Contains("uniform sampler2D ps_s0", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("vFrontColor.a", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
        Assert.Contains("source.a", SanityWorldEffectBytecode.FragmentShader, StringComparison.Ordinal);
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
        double totalSeconds
    )
    {
        Assert.True(
            SanityWorldCompositionPlanner.TryCreate(
                Snapshot(ratio, layers, screenId: 0),
                totalSeconds,
                12345u,
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
