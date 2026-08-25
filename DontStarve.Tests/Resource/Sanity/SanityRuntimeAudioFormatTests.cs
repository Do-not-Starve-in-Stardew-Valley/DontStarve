using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityRuntimeAudioFormatTests
{
    [Theory]
    [InlineData(44100, "sanity.wav.pcm-s16-stereo-44100-v1", 176400)]
    [InlineData(48000, "sanity.wav.pcm-s16-stereo-48000-v1", 192000)]
    public void CatalogMatchesOnlyItsExactPcmDescriptor(
        int sampleRate,
        string formatId,
        int byteRate
    )
    {
        var inspection = SanityWavInspector.Inspect(BuildWav(sampleRate));

        Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Issues));
        Assert.Equal(byteRate, inspection.ByteRate);
        Assert.True(SanityWavFormatCatalog.TryGet(formatId, out var descriptor));
        Assert.True(descriptor.Matches(inspection));
    }

    [Fact]
    public void CatalogRejectsAValidWavWithTheWrongDeclaredRate()
    {
        var inspection = SanityWavInspector.Inspect(BuildWav(44100));

        Assert.True(SanityWavFormatCatalog.TryGet(
            SanityWavFormatCatalog.PcmS16Stereo48000V1,
            out var descriptor
        ));
        Assert.False(descriptor.Matches(inspection));
    }

    [Fact]
    public void RuntimeCacheRejectsUnknownFormatsAndAcceptsNative48000Pcm()
    {
        var files = new MemoryFiles();
        var factory = new FakeFactory();
        var diagnostics = new List<SanityRuntimeResourceDiagnostic>();
        var cache = new SanityRuntimePhysicalResourceCache(
            files.Root,
            files,
            factory,
            diagnostics.Add
        );
        var path = "Asset/Sanity/Audio/Creatures/test.wav";
        var bytes = BuildWav(48000);
        files.Add(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var unknown = cache.Load(
            "sanity.cue.test",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: "sanity.wav.unsupported-v1"
        );
        Assert.False(unknown.Success);
        Assert.Equal("resource.sound.unknown-format-id", unknown.Diagnostic.Code);

        var accepted = cache.Load(
            "sanity.cue.test",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: SanityWavFormatCatalog.PcmS16Stereo48000V1
        );
        Assert.True(accepted.Success, accepted.Diagnostic.Reason);
        Assert.Equal(1, factory.SoundCreates);
    }

    [Fact]
    public void RuntimeCacheRejectsAConflictingFormatForAnAlreadyCachedSoundPath()
    {
        var files = new MemoryFiles();
        var factory = new FakeFactory();
        var diagnostics = new List<SanityRuntimeResourceDiagnostic>();
        var cache = new SanityRuntimePhysicalResourceCache(
            files.Root,
            files,
            factory,
            diagnostics.Add
        );
        var path = "Asset/Sanity/Audio/Creatures/shared.wav";
        var bytes = BuildWav(44100);
        files.Add(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var first = cache.Load(
            "sanity.cue.first",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: SanityWavFormatCatalog.PcmS16Stereo44100V1
        );
        var conflicting = cache.Load(
            "sanity.cue.second",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: SanityWavFormatCatalog.PcmS16Stereo48000V1
        );

        Assert.True(first.Success, first.Diagnostic.Reason);
        Assert.False(conflicting.Success);
        Assert.Equal("resource.sound.cached-format-mismatch", conflicting.Diagnostic.Code);
        Assert.Equal(1, factory.SoundCreates);
    }

    [Fact]
    public void CueResourceMapPreservesExactPoolOrderAndLegacyFlatList()
    {
        var first = new FakeResource("first.wav");
        var second = new FakeResource("second.wav");
        var third = new FakeResource("third.wav");
        var firstCue = Cue(
            "sanity.cue.test.first",
            new[] { Clip("first-1", first.Path), Clip("first-2", second.Path) }
        );
        var secondCue = Cue(
            "sanity.cue.test.second",
            new[] { Clip("second-1", third.Path) }
        );
        var definition = new SanityCueSetDefinition(
            "sanity.cue.test",
            "test",
            "LazyPerCueSetBounded",
            "ReleaseOnWorldTitleDispose",
            1,
            new[] { firstCue, secondCue }
        );
        var flat = new ISanityPhysicalResource[] { first, second, third };
        var runtime = new SanityCueSetRuntimeResource(definition, flat);

        Assert.Equal(flat, runtime.PhysicalResources);
        Assert.True(runtime.TryGetCueResources(firstCue.CueId, out var firstPool));
        Assert.Equal(new ISanityPhysicalResource[] { first, second }, firstPool);
        Assert.True(runtime.TryGetCueResources(secondCue.CueId, out var secondPool));
        Assert.Equal(new ISanityPhysicalResource[] { third }, secondPool);
        Assert.False(runtime.TryGetCueResources("sanity.cue.test.missing", out var missing));
        Assert.Empty(missing);
    }

    private static SanityCueClipDefinition Clip(string id, string path)
    {
        return new SanityCueClipDefinition(
            id,
            $"Asset/Sanity/Audio/Creatures/{path}",
            new string('A', 64),
            SanityWavFormatCatalog.PcmS16Stereo44100V1,
            isPlaceholder: false,
            durationFrames: 1,
            durationSeconds: 1d / 44100d
        );
    }

    private static SanityCueDefinition Cue(
        string cueId,
        IReadOnlyList<SanityCueClipDefinition> clips
    )
    {
        return new SanityCueDefinition(
            cueId,
            "OneShot",
            enabled: true,
            requiredForRelease: true,
            isPlaceholder: false,
            listeningStatus: "PendingRealMachine",
            clips
        );
    }

    private static byte[] BuildWav(int sampleRate)
    {
        const int channels = 2;
        const int bits = 16;
        const int blockAlign = channels * (bits / 8);
        const int dataBytes = blockAlign * 4;
        var bytes = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), bits);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), dataBytes);
        return bytes;
    }

    private sealed class MemoryFiles : ISanityAssetFileAccess
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);

        internal string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            "dontstarve-runtime-audio-format-tests"
        );

        internal void Add(string relativePath, byte[] bytes)
        {
            Assert.True(
                SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                    Root,
                    relativePath,
                    out var absolutePath,
                    out var reason
                ),
                reason
            );
            files[absolutePath] = bytes;
        }

        public bool FileExists(string absolutePath) => files.ContainsKey(absolutePath);

        public bool TryReadAllBytes(string absolutePath, out byte[] bytes, out string reason)
        {
            if (files.TryGetValue(absolutePath, out var found))
            {
                bytes = found;
                reason = "asset.read";
                return true;
            }

            bytes = Array.Empty<byte>();
            reason = "asset.file-missing";
            return false;
        }
    }

    private sealed class FakeFactory : ISanityPhysicalResourceFactory
    {
        internal int SoundCreates { get; private set; }

        public SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes)
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "test.texture.unused",
                "Texture creation is not used by this test."
            );
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes)
        {
            SoundCreates++;
            return SanityPhysicalResourceCreationResult.Created(new FakeResource(path));
        }
    }

    private sealed class FakeResource : ISanityPhysicalResource
    {
        internal FakeResource(string path)
        {
            Path = path;
        }

        public SanityPhysicalResourceKind Kind => SanityPhysicalResourceKind.SoundEffect;

        public string Path { get; }

        public void Dispose()
        {
        }
    }
}
