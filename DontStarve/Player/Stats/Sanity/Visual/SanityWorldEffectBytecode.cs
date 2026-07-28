#nullable enable

using System.IO;
using System.Text;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// MonoGame 3.8 OpenGL MGFX v9 pixel effect. The SpriteBatch-owned vertex shader supplies
/// vFrontColor/vTexCoord0; alpha in the cached vertex color is the only saturation parameter.
/// The bytecode is built once and contains no runtime-reflected parameter table.
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
        + "void main()\n"
        + "{\n"
        + "    vec4 source = texture2D(ps_s0, vTexCoord0.xy);\n"
        + "    float luminance = dot(source.rgb, vec3(0.299, 0.587, 0.114));\n"
        + "    float saturation = clamp(vFrontColor.a, 0.0, 1.0);\n"
        + "    gl_FragColor = vec4(mix(vec3(luminance), source.rgb, saturation), source.a);\n"
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
