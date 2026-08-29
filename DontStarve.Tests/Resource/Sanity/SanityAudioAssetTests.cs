using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityAudioAssetTests
{
    private static string ShippedModRoot => Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string MetadataPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Audio", "audio-cues.json");

    private static string ManifestPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-assets.json");

    private static string BindingsPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "resource-bindings.json");

    [Fact]
    public void ShippedAudioContractIsMachineValidWhileListeningRemainsPending()
    {
        var result = SanityAudioContractValidator.ValidateFromFiles(
            MetadataPath,
            ManifestPath,
            ShippedModRoot
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(
            new[] { "sanity.cue.sanity-change.gain", "sanity.cue.sanity-change.loss" },
            result.DisabledCueIds
        );
        Assert.Equal(5, result.PlaceholderCueIds.Count);
        Assert.Equal(23, result.PendingRealMachineCueIds.Count);

        var json = File.ReadAllText(MetadataPath);
        Assert.DoesNotContain("references/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("references\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TestPackage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Loudness", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Volume", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentReleaseGateStillRejectsPlaceholderAssetsCreditsAndDevCues()
    {
        var creditsPath = Path.Combine(
            ShippedModRoot,
            "Asset",
            "Sanity",
            "Data",
            "sanity-credits.json"
        );
        var result = SanityAssetValidator.ValidateFromFiles(
            ManifestPath,
            creditsPath,
            ShippedModRoot,
            SanityAssetValidationGate.Release
        );

        Assert.False(result.Success);
        Assert.Equal(33, result.Issues.Count(issue => issue.Code == "release.asset-placeholder"));
        Assert.Equal(10, result.Issues.Count(issue => issue.Code == "release.dev-credit-group"));
        Assert.Equal(56, result.Issues.Count(issue => issue.Code == "release.credit-placeholder"));
    }

    [Fact]
    public void EveryPhysicalClipHasCanonicalPcmFormatHashAndDuration()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MetadataPath));
        var clips = AllClips(document.RootElement).ToArray();
        Assert.Equal(144, clips.Length);
        Assert.Equal(144, clips.Select(clip => clip.GetProperty("ClipId").GetString()).Distinct().Count());
        Assert.Equal(144, clips.Select(clip => clip.GetProperty("Path").GetString()).Distinct().Count());

        foreach (var clip in clips)
        {
            var relativePath = clip.GetProperty("Path").GetString()!;
            Assert.StartsWith("Asset/Sanity/Audio/", relativePath, StringComparison.Ordinal);
            Assert.EndsWith(".wav", relativePath, StringComparison.Ordinal);
            Assert.All(relativePath, character => Assert.InRange((int)character, 0x20, 0x7E));
            Assert.True(
                SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                    ShippedModRoot,
                    relativePath,
                    out var absolutePath,
                    out var reason
                ),
                reason
            );
            var inspection = SanityWavInspector.InspectFile(absolutePath);
            Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Issues));
            var formatId = clip.GetProperty("FormatId").GetString();
            Assert.True(
                SanityWavFormatCatalog.TryGet(formatId, out var expectedFormat),
                $"Unsupported clip format id '{formatId}'."
            );
            Assert.True(expectedFormat.Matches(inspection));
            Assert.Equal(clip.GetProperty("Sha256").GetString(), inspection.Sha256);
            Assert.Equal(clip.GetProperty("DurationFrames").GetInt64(), inspection.Frames);
            Assert.InRange(
                Math.Abs(clip.GetProperty("DurationSeconds").GetDouble() - inspection.DurationSeconds),
                0d,
                0.0000005d
            );
        }
    }

    [Fact]
    public void RealSourcesPreserveGainAndDocumentUnmodifiedContainers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MetadataPath));
        var clips = AllClips(document.RootElement).ToArray();
        var real = clips.Where(clip => !clip.GetProperty("IsPlaceholder").GetBoolean()).ToArray();
        var placeholders = clips.Where(clip => clip.GetProperty("IsPlaceholder").GetBoolean()).ToArray();
        Assert.Equal(141, real.Length);
        Assert.Equal(3, placeholders.Length);

        foreach (var clip in real)
        {
            var source = clip.GetProperty("SourceEvidence");
            Assert.Equal("AvailableReadOnlyEvidence", source.GetProperty("Status").GetString());
            Assert.Equal(64, source.GetProperty("Sha256").GetString()!.Length);
            Assert.Equal(1, source.GetProperty("Format").GetProperty("FormatCode").GetInt32());
            Assert.Equal(2, source.GetProperty("Format").GetProperty("Channels").GetInt32());
            Assert.Equal(16, source.GetProperty("Format").GetProperty("BitsPerSample").GetInt32());
            var container = source.GetProperty("ContainerEvidence");
            var riffLengthDelta = container.GetProperty("RiffLengthDeltaBytes").GetInt32();
            var canonicalHeaderRebuilt = container.GetProperty("CanonicalHeaderRebuilt").GetBoolean();
            Assert.True(
                (riffLengthDelta == 0 && !canonicalHeaderRebuilt)
                    || (riffLengthDelta == 4 && canonicalHeaderRebuilt),
                $"Unexpected container evidence for {clip.GetProperty("Path").GetString()}."
            );
            Assert.Equal(
                container.GetProperty("DataDeclaredBytes").GetInt32(),
                container.GetProperty("DataDecodedBytes").GetInt32()
            );
            AssertGainAndTailPolicy(clip);
        }
        Assert.All(placeholders, AssertGainAndTailPolicy);
    }

    [Fact]
    public void CueSetsFreezePoolOneShotCacheLifecycleAndBindingIdentities()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MetadataPath));
        var root = document.RootElement;
        Assert.Equal("sanity.wav.pcm-s16-stereo-44100-v1", root.GetProperty("RuntimeFormat").GetProperty("FormatId").GetString());
        var sets = root.GetProperty("CueSets").EnumerateArray().ToArray();
        Assert.Equal(9, sets.Length);
        Assert.All(sets, set =>
        {
            Assert.False(string.IsNullOrWhiteSpace(set.GetProperty("Group").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(set.GetProperty("CachePolicy").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(set.GetProperty("LifecyclePolicy").GetString()));
            Assert.InRange(set.GetProperty("MaxConcurrentInstances").GetInt32(), 0, 1);
        });
        Assert.Equal(
            11,
            FindCue(root, "sanity.cue.ambience.low-sanity").GetProperty("Clips").GetArrayLength()
        );
        Assert.Equal(
            "RandomContinuousOneShotPool",
            FindCue(root, "sanity.cue.whispers.low-sanity").GetProperty("PlaybackMode").GetString()
        );
        Assert.False(FindCue(root, "sanity.cue.thresholds.danger-enter").GetProperty("Loop").GetBoolean());
        Assert.False(FindCue(root, "sanity.cue.sanity-change.gain").GetProperty("Enabled").GetBoolean());
        Assert.Empty(FindCue(root, "sanity.cue.sanity-change.loss").GetProperty("Clips").EnumerateArray());

        using var bindings = JsonDocument.Parse(File.ReadAllText(BindingsPath));
        var cueSetIds = sets.Select(set => set.GetProperty("CueSetId").GetString()).ToHashSet();
        Assert.All(
            bindings.RootElement.GetProperty("Bindings").EnumerateArray(),
            binding => Assert.Contains(binding.GetProperty("CueSetId").GetString(), cueSetIds)
        );
    }

    [Theory]
    [InlineData("bad-riff", "wav.invalid-riff")]
    [InlineData("unknown-codec", "wav.unknown-codec")]
    [InlineData("empty-audio", "wav.empty-audio")]
    public void WavInspectorFailsClosedForMalformedInputs(string mutation, string expectedCode)
    {
        var bytes = BuildWav(formatCode: 1, channels: 2, sampleRate: 44100, bits: 16, dataBytes: 16);
        if (mutation == "bad-riff")
            bytes[0] = (byte)'X';
        else if (mutation == "unknown-codec")
            BitConverter.GetBytes((ushort)3).CopyTo(bytes, 20);
        else if (mutation == "empty-audio")
            bytes = BuildWav(formatCode: 1, channels: 2, sampleRate: 44100, bits: 16, dataBytes: 0);

        var result = SanityWavInspector.Inspect(bytes);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void ValidatorRejectsDuplicateCueIds()
    {
        AssertMetadataRejected(
            root => FindCue(root, "sanity.cue.whispers.low-sanity")["CueId"] = "sanity.cue.ambience.low-sanity",
            "audio.cue.duplicate-id"
        );
    }

    [Fact]
    public void ValidatorRejectsTraversalPaths()
    {
        AssertMetadataRejected(
            root => FirstClip(root)["Path"] = "Asset/Sanity/Audio/../outside.wav",
            "audio.clip.invalid-path"
        );
    }

    [Fact]
    public void ValidatorWarnsStaleFileHashButRejectsStaleDuration()
    {
        AssertMetadataWarning(
            root => FirstClip(root)["Sha256"] = new string('0', 64),
            "audio.clip.hash-mismatch"
        );
        AssertMetadataRejected(
            root => FirstClip(root)["DurationFrames"] = FirstClip(root)["DurationFrames"]!.GetValue<long>() + 1,
            "audio.clip.duration-frames-mismatch"
        );
    }

    [Fact]
    public void ValidatorAllowsMissingClipHashWhenThePathAndWavAreValid()
    {
        AssertMetadataWarning(
            root => FirstClip(root).Remove("Sha256"),
            "audio.clip.hash-missing"
        );
    }

    [Fact]
    public void ValidatorRejectsRuntimeFormatDriftInMetadataAndPhysicalFile()
    {
        AssertMetadataRejected(
            root => root["RuntimeFormat"]!["SampleRateHz"] = 48000,
            "audio.format.sample-rate-mismatch"
        );

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"sanity-audio-{Guid.NewGuid():N}");
        try
        {
            var root = ReadMetadataNode();
            var clip = FirstClip(root);
            const string relativePath = "Asset/Sanity/Audio/Test/mono.wav";
            var absolutePath = Path.Combine(temporaryRoot, "Asset", "Sanity", "Audio", "Test", "mono.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            var bytes = BuildWav(formatCode: 1, channels: 1, sampleRate: 44100, bits: 16, dataBytes: 200);
            File.WriteAllBytes(absolutePath, bytes);
            clip["Path"] = relativePath;
            clip["Sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            clip["DurationFrames"] = 100;
            clip["DurationSeconds"] = Math.Round(100d / 44100d, 6);

            var result = SanityAudioContractValidator.Validate(
                root.ToJsonString(),
                File.ReadAllText(ManifestPath),
                temporaryRoot
            );
            Assert.False(result.Success);
            Assert.Contains(result.Issues, issue => issue.Code == "audio.clip.runtime-format-mismatch");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void ValidatorRejectsManifestCueReferenceDrift()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        var cueSlot = manifest["Slots"]!
            .AsArray()
            .Select(value => value!.AsObject())
            .First(value => value["SlotId"]!.GetValue<string>() == "sanity.cue.ambience.low-sanity");
        cueSlot["Path"] = "Asset/Sanity/Data/audio-cues.json";

        var result = SanityAudioContractValidator.Validate(
            File.ReadAllText(MetadataPath),
            manifest.ToJsonString(),
            ShippedModRoot
        );

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "audio.manifest.path-mismatch");
    }

    [Fact]
    public void ValidatorRejectsStageSixAndGameplayFields()
    {
        AssertMetadataRejected(
            root => root["PlaybackCoordinator"] = "not-stage-05",
            "audio.metadata.forbidden-field"
        );
        AssertMetadataRejected(
            root => root["Damage"] = 20,
            "audio.metadata.forbidden-field"
        );
    }

    private static void AssertGainAndTailPolicy(JsonElement clip)
    {
        var gain = clip.GetProperty("GainEvidence");
        Assert.Equal(0d, gain.GetProperty("GainAppliedDb").GetDouble());
        Assert.False(gain.GetProperty("NormalizationApplied").GetBoolean());
        var tail = clip.GetProperty("TailEvidence");
        Assert.Equal(0, tail.GetProperty("TrimmedFrames").GetInt32());
        Assert.False(tail.GetProperty("ClickRepairApplied").GetBoolean());
        Assert.Equal(
            "PendingRealMachine",
            clip.GetProperty("ListeningEvidence").GetProperty("OverallStatus").GetString()
        );
    }

    private static IEnumerable<JsonElement> AllClips(JsonElement root)
    {
        return root.GetProperty("CueSets")
            .EnumerateArray()
            .SelectMany(set => set.GetProperty("Cues").EnumerateArray())
            .SelectMany(cue => cue.GetProperty("Clips").EnumerateArray());
    }

    private static JsonElement FindCue(JsonElement root, string cueId)
    {
        return Assert.Single(
            root.GetProperty("CueSets")
                .EnumerateArray()
                .SelectMany(set => set.GetProperty("Cues").EnumerateArray())
                .Where(cue => cue.GetProperty("CueId").GetString() == cueId)
        );
    }

    private static JsonObject ReadMetadataNode()
    {
        return JsonNode.Parse(File.ReadAllText(MetadataPath))!.AsObject();
    }

    private static JsonObject FindCue(JsonObject root, string cueId)
    {
        return root["CueSets"]!
            .AsArray()
            .SelectMany(set => set!["Cues"]!.AsArray())
            .Select(cue => cue!.AsObject())
            .First(cue => cue["CueId"]!.GetValue<string>() == cueId);
    }

    private static JsonObject FirstClip(JsonObject root)
    {
        var cueSet = root["CueSets"]!.AsArray()[0]!.AsObject();
        var cue = cueSet["Cues"]!.AsArray()[0]!.AsObject();
        return cue["Clips"]!.AsArray()[0]!.AsObject();
    }

    private static void AssertMetadataRejected(Action<JsonObject> mutate, string expectedCode)
    {
        var root = ReadMetadataNode();
        mutate(root);
        var result = SanityAudioContractValidator.Validate(
            root.ToJsonString(),
            File.ReadAllText(ManifestPath),
            ShippedModRoot
        );
        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    private static void AssertMetadataWarning(Action<JsonObject> mutate, string expectedCode)
    {
        var root = ReadMetadataNode();
        mutate(root);
        var result = SanityAudioContractValidator.Validate(
            root.ToJsonString(),
            File.ReadAllText(ManifestPath),
            ShippedModRoot
        );
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues));
        Assert.Contains(
            result.Issues,
            issue => issue.Code == expectedCode && !issue.IsBlocking
        );
    }

    private static byte[] BuildWav(
        ushort formatCode,
        ushort channels,
        int sampleRate,
        ushort bits,
        int dataBytes
    )
    {
        var bytesPerSample = (bits + 7) / 8;
        var blockAlign = checked((ushort)(channels * bytesPerSample));
        var byteRate = sampleRate * blockAlign;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write(formatCode);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return stream.ToArray();
    }
}
