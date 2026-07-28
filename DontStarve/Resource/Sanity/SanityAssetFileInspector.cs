#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DontStarve.Resource.Sanity;

internal static class SanityAssetFileInspector
{
    internal static SanityAssetFileInspection Inspect(
        string absolutePath,
        ISanityAssetFileAccess fileAccess
    )
    {
        if (!fileAccess.TryReadAllBytes(absolutePath, out var bytes, out var reason))
            return SanityAssetFileInspection.Failed(reason);

        return SanityAssetFileInspection.Available(bytes, ToSha256(bytes));
    }

    internal static bool ExtensionMatchesKind(string path, SanityAssetKind kind)
    {
        var expected = kind switch
        {
            SanityAssetKind.Png => ".png",
            SanityAssetKind.Json => ".json",
            SanityAssetKind.Wav => ".wav",
            _ => string.Empty,
        };
        return string.Equals(Path.GetExtension(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFormatValid(SanityAssetKind kind, byte[] bytes)
    {
        return kind switch
        {
            SanityAssetKind.Png => IsPngHeader(bytes),
            SanityAssetKind.Json => IsStrictJson(bytes),
            SanityAssetKind.Wav => IsWaveHeader(bytes),
            _ => false,
        };
    }

    internal static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var character in value)
        {
            var isHex = (character >= '0' && character <= '9')
                || (character >= 'A' && character <= 'F')
                || (character >= 'a' && character <= 'f');
            if (!isHex)
                return false;
        }

        return true;
    }

    private static bool IsPngHeader(byte[] bytes)
    {
        // 阶段 02 不引入图像依赖；这里只冻结 PNG/IHDR/正尺寸格式门，完整解码留给资源加载阶段。
        return bytes.Length >= 24
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4e
            && bytes[3] == 0x47
            && bytes[4] == 0x0d
            && bytes[5] == 0x0a
            && bytes[6] == 0x1a
            && bytes[7] == 0x0a
            && bytes[12] == (byte)'I'
            && bytes[13] == (byte)'H'
            && bytes[14] == (byte)'D'
            && bytes[15] == (byte)'R'
            && ReadBigEndianInt32(bytes, 16) > 0
            && ReadBigEndianInt32(bytes, 20) > 0;
    }

    private static bool IsWaveHeader(byte[] bytes)
    {
        return bytes.Length >= 12
            && bytes[0] == (byte)'R'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W'
            && bytes[9] == (byte)'A'
            && bytes[10] == (byte)'V'
            && bytes[11] == (byte)'E';
    }

    private static bool IsStrictJson(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static string ToSha256(byte[] bytes)
    {
        return BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", string.Empty);
    }
}

internal sealed class SanityAssetFileInspection
{
    private SanityAssetFileInspection(
        bool readSucceeded,
        byte[] bytes,
        string sha256,
        string reason
    )
    {
        ReadSucceeded = readSucceeded;
        Bytes = bytes;
        Sha256 = sha256;
        Reason = reason;
    }

    internal bool ReadSucceeded { get; }

    internal byte[] Bytes { get; }

    internal string Sha256 { get; }

    internal string Reason { get; }

    internal static SanityAssetFileInspection Available(byte[] bytes, string sha256)
    {
        return new SanityAssetFileInspection(true, bytes, sha256, "asset.read");
    }

    internal static SanityAssetFileInspection Failed(string reason)
    {
        return new SanityAssetFileInspection(false, Array.Empty<byte>(), string.Empty, reason);
    }
}
