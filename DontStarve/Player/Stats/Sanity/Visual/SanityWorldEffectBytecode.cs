#nullable enable

using System.IO;
using System.Text;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// MonoGame 3.8 OpenGL MGFX v9 pixel effect. The SpriteBatch-owned vertex shader supplies
/// vFrontColor/vTexCoord0; RGB carries a normalized distortion phase, independent sanity colour
/// blend, and day/dusk/night profile while alpha carries the final edge-distortion blend. The
/// colour grade is relative to Stardew's already-lit source pixels, so it never replaces every
/// material with a fixed Don't Starve RGB target. The saturation planner is deliberately retained
/// but not applied here.
/// </summary>
internal static class SanityWorldEffectBytecode
{
    internal const byte MgfxVersion = 9;
    internal const byte OpenGlProfile = 0;
    internal const int EffectKey = unchecked((int)0x53414E33);

    internal const string FragmentShader =
        "#ifdef GL_ES\n"
        + "precision mediump float;\n"
        + "precision mediump int;\n"
        + "#endif\n"
        + "uniform sampler2D ps_s0;\n"
        + "varying vec4 vFrontColor;\n"
        + "varying vec4 vTexCoord0;\n"
        + "vec3 ApplyInsanityColourGrade(vec3 worldColor, float sanityPhase)\n"
        + "{\n"
        + "    float luma = dot(worldColor, vec3(0.299, 0.587, 0.114));\n"
        + "    float maximum = max(worldColor.r, max(worldColor.g, worldColor.b));\n"
        + "    float minimum = min(worldColor.r, min(worldColor.g, worldColor.b));\n"
        + "    float chroma = maximum - minimum;\n"
        + "    float coloured = smoothstep(0.05, 0.45, chroma);\n"
        + "    vec3 sourceChroma = worldColor - vec3(luma);\n"
        + "    float redDominance = smoothstep(0.02, 0.42, worldColor.r - max(worldColor.g, worldColor.b));\n"
        + "    float greenDominance = smoothstep(0.02, 0.42, worldColor.g - max(worldColor.r, worldColor.b));\n"
        + "    float blueDominance = smoothstep(0.02, 0.42, worldColor.b - max(worldColor.r, worldColor.g));\n"
        + "    float yellowDominance = smoothstep(0.04, 0.42, min(worldColor.r, worldColor.g) - worldColor.b);\n"
        + "    float redWeight = coloured * redDominance;\n"
        + "    float greenWeight = coloured * greenDominance;\n"
        + "    float blueWeight = coloured * blueDominance;\n"
        + "    float yellowWeight = coloured * yellowDominance * (1.0 - blueWeight);\n"
        + "    float duskWeight = 1.0 - abs((sanityPhase * 2.0) - 1.0);\n"
        + "    float nightWeight = smoothstep(0.75, 0.99, sanityPhase);\n"
        + "    float lightWeight = smoothstep(0.18, 0.88, luma);\n"
        + "    float dayExposure = mix(0.82, 0.73, lightWeight);\n"
        + "    float duskExposure = mix(0.78, 0.69, lightWeight);\n"
        + "    float nightExposure = mix(0.88, 0.80, lightWeight);\n"
        + "    float lumaExposure = mix(dayExposure, duskExposure, duskWeight);\n"
        + "    lumaExposure = mix(lumaExposure, nightExposure, nightWeight);\n"
        + "    float colourRetention = clamp(0.14 + (0.06 * redWeight) + (0.02 * greenWeight) - (0.05 * blueWeight), 0.09, 0.20);\n"
        + "    vec3 dayTint = vec3(0.026, 0.017, 0.005);\n"
        + "    vec3 duskTint = vec3(0.031, 0.010, 0.004);\n"
        + "    vec3 nightTint = vec3(0.006, 0.010, 0.018);\n"
        + "    vec3 phaseTint = mix(dayTint, duskTint, duskWeight);\n"
        + "    phaseTint = mix(phaseTint, nightTint, nightWeight);\n"
        + "    vec3 materialBias = (redWeight * vec3(0.036, -0.010, -0.010))\n"
        + "        + (greenWeight * vec3(0.018, 0.016, -0.014))\n"
        + "        + (yellowWeight * vec3(0.022, 0.010, -0.017))\n"
        + "        + (blueWeight * vec3(0.020, 0.024, -0.004));\n"
        + "    float tintAmount = 0.18 + (0.82 * coloured);\n"
        + "    vec3 relativeColour = vec3(luma * lumaExposure)\n"
        + "        + (sourceChroma * colourRetention * lumaExposure)\n"
        + "        + (phaseTint * tintAmount)\n"
        + "        + materialBias;\n"
        + "    return clamp(relativeColour, vec3(0.0), vec3(1.0));\n"
        + "}\n"
        + "void main()\n"
        + "{\n"
        + "    vec2 uv = vTexCoord0.xy;\n"
        + "    vec4 source = texture2D(ps_s0, uv);\n"
        + "    vec2 center = uv - vec2(0.5, 0.5);\n"
        + "    float radius = length(center);\n"
        + "    float edge = smoothstep(0.315, 0.5, radius);\n"
        + "    vec2 direction = center / max(radius, 0.0001);\n"
        + "    float distortionAngle = vFrontColor.r * 6.28318531;\n"
        + "    vec2 phase = vec2(\n"
        + "        sin(distortionAngle),\n"
        + "        cos((distortionAngle * 0.83) + 0.37)\n"
        + "    );\n"
        + "    float ripple = sin((uv.y * 50.0) + (phase.x * 3.14159265) + phase.y);\n"
        + "    vec2 shiftedUv = clamp(\n"
        + "        uv + ((phase * 0.00625) + (direction * ripple * 0.002)) * edge,\n"
        + "        vec2(0.00625, 0.00625),\n"
        + "        vec2(0.99375, 0.99375)\n"
        + "    );\n"
        + "    vec4 distorted = texture2D(ps_s0, shiftedUv);\n"
        + "    float amount = clamp(vFrontColor.a, 0.0, 0.75) * edge;\n"
        + "    vec3 worldColor = mix(source.rgb, distorted.rgb, amount);\n"
        + "    float insanityBlend = clamp(vFrontColor.g, 0.0, 1.0);\n"
        + "    float sanityPhase = clamp(vFrontColor.b, 0.0, 1.0);\n"
        + "    vec3 insanityColor = ApplyInsanityColourGrade(worldColor, sanityPhase);\n"
        + "    gl_FragColor = vec4(mix(worldColor, insanityColor, insanityBlend), source.a);\n"
        + "}\n";

    internal static readonly byte[] Bytes = Build();

    private static byte[] Build()
    {
        var shader = Encoding.ASCII.GetBytes(FragmentShader);
        using var stream = new MemoryStream(768);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new[] { 'M', 'G', 'F', 'X' });
            writer.Write(MgfxVersion);
            writer.Write(OpenGlProfile);
            writer.Write(EffectKey);

            writer.Write((byte)0); // constant buffers
            writer.Write((byte)1); // shaders
            writer.Write(false); // pixel shader
            writer.Write(shader.Length);
            writer.Write(shader);
            writer.Write((byte)0); // samplers; ps_s0 defaults to texture unit zero
            writer.Write((byte)0); // constant-buffer references
            writer.Write((byte)0); // vertex attributes

            writer.Write((byte)0); // parameters (7-bit encoded zero)
            writer.Write((byte)1); // techniques
            writer.Write("SanityWorld");
            writer.Write((byte)0); // technique annotations
            writer.Write((byte)1); // passes
            writer.Write("WorldOnly");
            writer.Write((byte)0); // pass annotations
            writer.Write(byte.MaxValue); // keep SpriteBatch vertex shader
            writer.Write((byte)0); // pixel shader index
            writer.Write(false); // blend state
            writer.Write(false); // depth/stencil state
            writer.Write(false); // rasterizer state
        }
        return stream.ToArray();
    }
}
