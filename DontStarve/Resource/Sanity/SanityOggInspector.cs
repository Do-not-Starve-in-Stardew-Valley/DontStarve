#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DontStarve.Resource.Sanity;

public sealed record SanityOggInspectionIssue(string Code, string Reason);

public sealed class SanityOggInspectionResult
{
    internal SanityOggInspectionResult(
        IReadOnlyList<SanityOggInspectionIssue> issues,
        string? sha256,
        int channels,
        int sampleRateHz,
        long frames
    )
    {
        Issues = issues;
        Sha256 = sha256;
        Channels = channels;
        SampleRateHz = sampleRateHz;
        Frames = frames;
    }

    public IReadOnlyList<SanityOggInspectionIssue> Issues { get; }

    public bool Success => Issues.Count == 0;

    public string? Sha256 { get; }

    public int Channels { get; }

    public int SampleRateHz { get; }

    public long Frames { get; }

    public double DurationSeconds => SampleRateHz > 0 ? (double)Frames / SampleRateHz : 0d;
}

/// <summary>
/// Checks the Ogg container and Vorbis identification packet without decoding audio. The
/// runtime still lets MonoGame create the SoundEffect; this seam keeps missing, malformed,
/// wrong-rate, and empty clips out before that call.
/// </summary>
public static class SanityOggInspector
{
    private const int PageHeaderBytes = 27;
    private const int VorbisIdentificationPacketBytes = 30;

    public static SanityOggInspectionResult InspectFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("ogg.path-empty", "The OGG path must be non-empty.");

        try
        {
            return Inspect(File.ReadAllBytes(path));
        }
        catch (FileNotFoundException)
        {
            return Failure("ogg.file-missing", $"OGG file '{path}' does not exist.");
        }
        catch (DirectoryNotFoundException)
        {
            return Failure("ogg.file-missing", $"OGG file '{path}' does not exist.");
        }
        catch (IOException exception)
        {
            return Failure("ogg.read-failed", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure("ogg.read-failed", exception.Message);
        }
    }

    public static SanityOggInspectionResult Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var issues = new List<SanityOggInspectionIssue>();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length < PageHeaderBytes || !MatchesAscii(bytes, 0, "OggS"))
        {
            issues.Add(new("ogg.invalid-capture", "The file must begin with an Ogg page capture pattern."));
            return Result(issues, sha256);
        }

        var offset = 0;
        uint? streamSerial = null;
        var sequenceNumbers = new HashSet<uint>();
        var packet = new List<byte>();
        byte[]? identificationPacket = null;
        long lastGranulePosition = -1;
        var pageCount = 0;

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < PageHeaderBytes || !MatchesAscii(bytes, offset, "OggS"))
            {
                issues.Add(new("ogg.truncated-page-header", "An Ogg page header is missing or truncated."));
                break;
            }

            if (bytes[offset + 4] != 0)
                issues.Add(new("ogg.unsupported-version", "Only Ogg bitstream version 0 is supported."));

            var granulePosition = BinaryPrimitives.ReadInt64LittleEndian(
                bytes.AsSpan(offset + 6, sizeof(long))
            );
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 14, sizeof(uint)));
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 18, sizeof(uint)));
            var segmentCount = bytes[offset + 26];
            var lacingOffset = offset + PageHeaderBytes;
            if (bytes.Length - lacingOffset < segmentCount)
            {
                issues.Add(new("ogg.truncated-segment-table", "The Ogg page segment table is truncated."));
                break;
            }

            var bodyBytes = 0;
            for (var index = 0; index < segmentCount; index++)
                bodyBytes += bytes[lacingOffset + index];

            var bodyOffset = lacingOffset + segmentCount;
            if (bytes.Length - bodyOffset < bodyBytes)
            {
                issues.Add(new("ogg.truncated-page-body", "The Ogg page body is truncated."));
                break;
            }

            if (streamSerial is null)
                streamSerial = serial;
            if (serial == streamSerial.Value)
            {
                pageCount++;
                if (!sequenceNumbers.Add(sequence))
                    issues.Add(new("ogg.duplicate-page", "The Vorbis stream contains a duplicate page sequence number."));
                if (granulePosition >= 0)
                    lastGranulePosition = Math.Max(lastGranulePosition, granulePosition);

                var bodyCursor = bodyOffset;
                for (var index = 0; index < segmentCount; index++)
                {
                    var segmentBytes = bytes[lacingOffset + index];
                    if (segmentBytes > 0)
                        packet.AddRange(bytes.AsSpan(bodyCursor, segmentBytes).ToArray());
                    bodyCursor += segmentBytes;
                    if (segmentBytes < byte.MaxValue)
                    {
                        if (identificationPacket is null)
                            identificationPacket = packet.ToArray();
                        packet.Clear();
                    }
                }
            }

            offset = bodyOffset + bodyBytes;
        }

        if (pageCount == 0)
            issues.Add(new("ogg.page-missing", "The file does not contain a readable Ogg page for its first stream."));
        if (identificationPacket is null)
        {
            issues.Add(new("ogg.identification-missing", "The first Vorbis identification packet is missing."));
        }
        else
        {
            ValidateIdentificationPacket(identificationPacket, issues, out var channels, out var sampleRateHz);
            if (channels > 0 && sampleRateHz > 0 && lastGranulePosition <= 0)
                issues.Add(new("ogg.empty-audio", "The Vorbis stream contains no positive PCM granule position."));
            return new SanityOggInspectionResult(
                issues,
                sha256,
                channels,
                sampleRateHz,
                lastGranulePosition > 0 ? lastGranulePosition : 0
            );
        }

        return Result(issues, sha256);
    }

    private static void ValidateIdentificationPacket(
        byte[] packet,
        ICollection<SanityOggInspectionIssue> issues,
        out int channels,
        out int sampleRateHz
    )
    {
        channels = 0;
        sampleRateHz = 0;
        if (packet.Length < VorbisIdentificationPacketBytes
            || packet[0] != 1
            || !MatchesAscii(packet, 1, "vorbis"))
        {
            issues.Add(new("ogg.invalid-identification", "The first packet is not a complete Vorbis identification packet."));
            return;
        }

        var vorbisVersion = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7, sizeof(uint)));
        if (vorbisVersion != 0)
            issues.Add(new("ogg.unsupported-vorbis-version", $"Only Vorbis version 0 is supported; found {vorbisVersion}."));

        channels = packet[11];
        sampleRateHz = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, sizeof(uint))) switch
        {
            <= int.MaxValue and var value => (int)value,
            _ => 0,
        };
        if (channels <= 0 || sampleRateHz <= 0)
            issues.Add(new("ogg.invalid-format-values", "The Vorbis identification packet contains non-positive channels or sample rate."));
    }

    private static SanityOggInspectionResult Failure(string code, string reason)
    {
        return Result(new[] { new SanityOggInspectionIssue(code, reason) });
    }

    private static SanityOggInspectionResult Result(
        IReadOnlyList<SanityOggInspectionIssue> issues,
        string? sha256 = null
    )
    {
        return new SanityOggInspectionResult(issues, sha256, 0, 0, 0);
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
}
