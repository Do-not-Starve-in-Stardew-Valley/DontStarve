using System.IO.Compression;
using System.Text;

namespace DontStarve.Tests.Resource.Sanity;

internal sealed class PngRgbaImage
{
    private static readonly byte[] Signature =
    {
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
    };

    private PngRgbaImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal byte[] Pixels { get; }

    internal static PngRgbaImage Decode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < Signature.Length || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException($"'{path}' is not a PNG file.");

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
                throw new InvalidDataException($"'{path}' has a truncated PNG chunk.");

            var length = ReadBigEndianInt32(bytes, offset);
            offset += 4;
            if (length < 0 || bytes.Length - offset < length + 8)
                throw new InvalidDataException($"'{path}' has an invalid PNG chunk length.");

            var type = Encoding.ASCII.GetString(bytes, offset, 4);
            offset += 4;
            var dataOffset = offset;
            offset += length;
            offset += 4; // CRC is covered by the manifest SHA; the decoder only validates the pixel contract.

            switch (type)
            {
                case "IHDR":
                    if (sawHeader || length != 13)
                        throw new InvalidDataException($"'{path}' has an invalid IHDR chunk.");
                    width = ReadBigEndianInt32(bytes, dataOffset);
                    height = ReadBigEndianInt32(bytes, dataOffset + 4);
                    if (width <= 0 || height <= 0)
                        throw new InvalidDataException($"'{path}' has invalid PNG dimensions.");
                    if (
                        bytes[dataOffset + 8] != 8
                        || bytes[dataOffset + 9] != 6
                        || bytes[dataOffset + 10] != 0
                        || bytes[dataOffset + 11] != 0
                        || bytes[dataOffset + 12] != 0
                    )
                    {
                        throw new InvalidDataException(
                            $"'{path}' must be non-interlaced RGBA8 PNG for deterministic alpha validation."
                        );
                    }
                    sawHeader = true;
                    break;

                case "IDAT":
                    if (!sawHeader)
                        throw new InvalidDataException($"'{path}' has IDAT before IHDR.");
                    compressed.Write(bytes, dataOffset, length);
                    break;

                case "IEND":
                    sawEnd = true;
                    break;
            }

            if (sawEnd)
                break;
        }

        if (!sawHeader || !sawEnd || compressed.Length == 0)
            throw new InvalidDataException($"'{path}' is missing required PNG chunks.");

        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
            zlib.CopyTo(decompressed);

        var scanlineLength = checked(width * 4);
        var expectedLength = checked((scanlineLength + 1) * height);
        var filtered = decompressed.ToArray();
        if (filtered.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"'{path}' decompressed to {filtered.Length} bytes; expected {expectedLength}."
            );
        }

        var pixels = new byte[checked(scanlineLength * height)];
        var sourceOffset = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[sourceOffset++];
            if (filter > 4)
                throw new InvalidDataException($"'{path}' uses unsupported PNG filter {filter}.");

            var rowOffset = y * scanlineLength;
            for (var x = 0; x < scanlineLength; x++)
            {
                var raw = filtered[sourceOffset++];
                var left = x >= 4 ? pixels[rowOffset + x - 4] : (byte)0;
                var above = y > 0 ? pixels[rowOffset - scanlineLength + x] : (byte)0;
                var upperLeft = y > 0 && x >= 4
                    ? pixels[rowOffset - scanlineLength + x - 4]
                    : (byte)0;
                pixels[rowOffset + x] = filter switch
                {
                    0 => raw,
                    1 => unchecked((byte)(raw + left)),
                    2 => unchecked((byte)(raw + above)),
                    3 => unchecked((byte)(raw + ((left + above) / 2))),
                    4 => unchecked((byte)(raw + Paeth(left, above, upperLeft))),
                    _ => throw new InvalidDataException($"'{path}' uses unsupported PNG filter {filter}."),
                };
            }
        }

        return new PngRgbaImage(width, height, pixels);
    }

    internal int CountNonTransparentPixels(int x, int y, int width, int height)
    {
        ValidateRegion(x, y, width, height);
        var count = 0;
        for (var currentY = y; currentY < y + height; currentY++)
        {
            for (var currentX = x; currentX < x + width; currentX++)
            {
                if (Pixels[((currentY * Width + currentX) * 4) + 3] != 0)
                    count++;
            }
        }
        return count;
    }

    internal bool ContainsColor(byte red, byte green, byte blue, byte alpha)
    {
        for (var offset = 0; offset < Pixels.Length; offset += 4)
        {
            if (
                Pixels[offset] == red
                && Pixels[offset + 1] == green
                && Pixels[offset + 2] == blue
                && Pixels[offset + 3] == alpha
            )
            {
                return true;
            }
        }
        return false;
    }

    internal bool RegionsAreHorizontalMirrors(
        int firstX,
        int firstY,
        int secondX,
        int secondY,
        int width,
        int height
    )
    {
        ValidateRegion(firstX, firstY, width, height);
        ValidateRegion(secondX, secondY, width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var firstOffset = (((firstY + y) * Width + firstX + x) * 4);
                var secondOffset = (((secondY + y) * Width + secondX + width - 1 - x) * 4);
                if (!Pixels.AsSpan(firstOffset, 4).SequenceEqual(Pixels.AsSpan(secondOffset, 4)))
                    return false;
            }
        }
        return true;
    }

    private void ValidateRegion(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width), "PNG inspection region is out of bounds.");
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static byte Paeth(byte left, byte above, byte upperLeft)
    {
        var estimate = left + above - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var aboveDistance = Math.Abs(estimate - above);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
            return left;
        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }
}
