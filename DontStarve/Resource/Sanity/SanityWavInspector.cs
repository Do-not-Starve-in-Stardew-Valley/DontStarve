#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DontStarve.Resource.Sanity;

public sealed record SanityWavInspectionIssue(string Code, string Reason);

public sealed class SanityWavInspectionResult
{
    internal SanityWavInspectionResult(
        IReadOnlyList<SanityWavInspectionIssue> issues,
        string? sha256,
        int formatCode,
        int channels,
        int sampleRateHz,
        int bitsPerSample,
        int blockAlign,
        int byteRate,
        int dataBytes,
        long frames
    )
    {
        Issues = issues;
        Sha256 = sha256;
        FormatCode = formatCode;
        Channels = channels;
        SampleRateHz = sampleRateHz;
        BitsPerSample = bitsPerSample;
        BlockAlign = blockAlign;
        ByteRate = byteRate;
        DataBytes = dataBytes;
        Frames = frames;
    }

    public IReadOnlyList<SanityWavInspectionIssue> Issues { get; }

    public bool Success => Issues.Count == 0;

    public string? Sha256 { get; }

    public int FormatCode { get; }

    public int Channels { get; }

    public int SampleRateHz { get; }

    public int BitsPerSample { get; }

    public int BlockAlign { get; }

    public int ByteRate { get; }

    public int DataBytes { get; }

    public long Frames { get; }

    public double DurationSeconds => SampleRateHz > 0 ? (double)Frames / SampleRateHz : 0d;
}

/// <summary>
/// Strictly inspects RIFF PCM structure without constructing a SoundEffect. Stage 05 uses this
/// pure seam to keep malformed files out; stage 06 remains responsible for real runtime loading.
/// </summary>
public static class SanityWavInspector
{
    public static SanityWavInspectionResult InspectFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("wav.path-empty", "The WAV path must be non-empty.");

        try
        {
            return Inspect(File.ReadAllBytes(path));
        }
        catch (FileNotFoundException)
        {
            return Failure("wav.file-missing", $"WAV file '{path}' does not exist.");
        }
        catch (DirectoryNotFoundException)
        {
            return Failure("wav.file-missing", $"WAV file '{path}' does not exist.");
        }
        catch (IOException exception)
        {
            return Failure("wav.read-failed", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure("wav.read-failed", exception.Message);
        }
    }

    public static SanityWavInspectionResult Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var issues = new List<SanityWavInspectionIssue>();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length < 12 || !MatchesAscii(bytes, 0, "RIFF") || !MatchesAscii(bytes, 8, "WAVE"))
        {
            issues.Add(new("wav.invalid-riff", "The file must begin with a RIFF/WAVE header."));
            return Result(issues, sha256);
        }

        var declaredRiffBytes = (long)ReadUInt32(bytes, 4) + 8L;
        if (declaredRiffBytes != bytes.Length)
        {
            issues.Add(
                new(
                    "wav.riff-length-mismatch",
                    $"RIFF declares {declaredRiffBytes} bytes but the file contains {bytes.Length}."
                )
            );
        }

        var offset = 12;
        var foundFormat = false;
        var foundData = false;
        var formatCode = 0;
        var channels = 0;
        var sampleRate = 0;
        var byteRate = 0;
        var blockAlign = 0;
        var bitsPerSample = 0;
        var dataBytes = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
            {
                issues.Add(new("wav.truncated-chunk-header", "A RIFF chunk header is truncated."));
                break;
            }

            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = (long)ReadUInt32(bytes, offset + 4);
            var chunkStart = offset + 8L;
            var chunkEnd = chunkStart + chunkSize;
            if (chunkEnd > bytes.Length)
            {
                issues.Add(
                    new(
                        "wav.truncated-chunk",
                        $"Chunk '{chunkId}' declares {chunkSize} bytes beyond the physical file."
                    )
                );
                break;
            }

            if (chunkId == "fmt ")
            {
                if (foundFormat)
                    issues.Add(new("wav.duplicate-format", "The WAV contains more than one fmt chunk."));
                else if (chunkSize < 16)
                    issues.Add(new("wav.invalid-format-chunk", "The fmt chunk is shorter than 16 bytes."));
                else
                {
                    foundFormat = true;
                    var start = checked((int)chunkStart);
                    formatCode = ReadUInt16(bytes, start);
                    channels = ReadUInt16(bytes, start + 2);
                    var sampleRateValue = ReadUInt32(bytes, start + 4);
                    var byteRateValue = ReadUInt32(bytes, start + 8);
                    sampleRate = sampleRateValue <= int.MaxValue ? (int)sampleRateValue : 0;
                    byteRate = byteRateValue <= int.MaxValue ? (int)byteRateValue : 0;
                    blockAlign = ReadUInt16(bytes, start + 12);
                    bitsPerSample = ReadUInt16(bytes, start + 14);
                }
            }
            else if (chunkId == "data")
            {
                if (foundData)
                    issues.Add(new("wav.duplicate-data", "The WAV contains more than one data chunk."));
                else
                {
                    foundData = true;
                    dataBytes = checked((int)chunkSize);
                }
            }

            var paddedEnd = chunkEnd + (chunkSize & 1L);
            if (paddedEnd > bytes.Length)
            {
                issues.Add(new("wav.missing-chunk-padding", $"Chunk '{chunkId}' is missing its pad byte."));
                break;
            }
            offset = checked((int)paddedEnd);
        }

        if (!foundFormat)
            issues.Add(new("wav.format-missing", "The WAV does not contain a fmt chunk."));
        if (!foundData)
            issues.Add(new("wav.data-missing", "The WAV does not contain a data chunk."));
        if (foundFormat)
        {
            if (formatCode != 1)
                issues.Add(new("wav.unknown-codec", $"Only PCM format code 1 is supported; found {formatCode}."));
            if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || blockAlign <= 0 || byteRate <= 0)
                issues.Add(new("wav.invalid-format-values", "The fmt chunk contains non-positive values."));
            else
            {
                var expectedBlockAlign = channels * ((bitsPerSample + 7) / 8);
                var expectedByteRate = sampleRate * expectedBlockAlign;
                if (blockAlign != expectedBlockAlign || byteRate != expectedByteRate)
                {
                    issues.Add(
                        new(
                            "wav.incoherent-format",
                            "BlockAlign or ByteRate does not match channels, bit depth, and sample rate."
                        )
                    );
                }
            }
        }
        if (foundData && dataBytes == 0)
            issues.Add(new("wav.empty-audio", "The PCM data chunk is empty."));
        if (foundData && blockAlign > 0 && dataBytes % blockAlign != 0)
            issues.Add(new("wav.partial-frame", "The PCM data chunk ends with a partial sample frame."));

        var frames = foundData && blockAlign > 0 ? dataBytes / blockAlign : 0;
        return new SanityWavInspectionResult(
            issues,
            sha256,
            formatCode,
            channels,
            sampleRate,
            bitsPerSample,
            blockAlign,
            byteRate,
            dataBytes,
            frames
        );
    }

    private static SanityWavInspectionResult Failure(string code, string reason)
    {
        return Result(new[] { new SanityWavInspectionIssue(code, reason) }, null);
    }

    private static SanityWavInspectionResult Result(
        IReadOnlyList<SanityWavInspectionIssue> issues,
        string? sha256 = null
    )
    {
        return new SanityWavInspectionResult(issues, sha256, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static bool MatchesAscii(byte[] bytes, int offset, string expected)
    {
        if (offset < 0 || bytes.Length - offset < expected.Length)
            return false;
        for (var index = 0; index < expected.Length; index++)
        {
            if (bytes[offset + index] != expected[index])
                return false;
        }
        return true;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }
}
