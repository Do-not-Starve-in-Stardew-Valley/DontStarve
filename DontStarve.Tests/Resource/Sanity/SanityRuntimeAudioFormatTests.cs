using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityRuntimeAudioFormatTests
{
    [Theory]
    [InlineData(44100, "sanity.ogg.vorbis-stereo-44100-v1")]
    [InlineData(48000, "sanity.ogg.vorbis-stereo-48000-v1")]
    public void CatalogMatchesOnlyItsExactPcmDescriptor(
        int sampleRate,
        string formatId
    )
    {
        var inspection = SanityOggInspector.Inspect(BuildOgg(sampleRate));

        Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Issues));
        Assert.Equal(2, inspection.Channels);
        Assert.Equal(sampleRate, inspection.SampleRateHz);
        Assert.True(SanityOggFormatCatalog.TryGet(formatId, out var descriptor));
        Assert.True(descriptor.Matches(inspection));
    }

    [Fact]
    public void CatalogRejectsAValidOggWithTheWrongDeclaredRate()
    {
        var inspection = SanityOggInspector.Inspect(BuildOgg(44100));

        Assert.True(SanityOggFormatCatalog.TryGet(
            SanityOggFormatCatalog.VorbisStereo48000V1,
            out var descriptor
        ));
        Assert.False(descriptor.Matches(inspection));
    }

    [Fact]
    public void RuntimeCacheRejectsUnknownFormatsAndAcceptsNative48000Vorbis()
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
        var path = "Asset/Sanity/Audio/Creatures/test.ogg";
        var bytes = BuildOgg(48000);
        files.Add(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var unknown = cache.Load(
            "sanity.cue.test",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: "sanity.ogg.unsupported-v1"
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
            expectedFormatId: SanityOggFormatCatalog.VorbisStereo48000V1
        );
        Assert.True(accepted.Success, accepted.Diagnostic.Reason);
        Assert.Equal(1, factory.SoundCreates);
    }

    [Fact]
    public void RuntimeCacheDoesNotExposeAudioFactoryExceptionMessages()
    {
        const string path = "Asset/Sanity/Audio/Creatures/test.ogg";
        const string privatePath = @"C:\Users\player\Mods\DontStarveCS\Asset\secret.ogg";
        var files = new MemoryFiles();
        var factory = new FakeFactory { SoundCreationFailureMessage = privatePath };
        var diagnostics = new List<SanityRuntimeResourceDiagnostic>();
        var cache = new SanityRuntimePhysicalResourceCache(
            files.Root,
            files,
            factory,
            diagnostics.Add
        );
        var bytes = BuildOgg(48000);
        files.Add(path, bytes);

        var result = cache.Load(
            "sanity.cue.test",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            Convert.ToHexString(SHA256.HashData(bytes)),
            required: true,
            isPlaceholder: false,
            expectedFormatId: SanityOggFormatCatalog.VorbisStereo48000V1
        );

        Assert.False(result.Success);
        Assert.Equal(path, result.Diagnostic.Path);
        Assert.Equal("resource.factory.threw", result.Diagnostic.Code);
        Assert.Contains("InvalidDataException", result.Diagnostic.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePath, result.Diagnostic.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePath, Assert.Single(diagnostics).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void XnaSoundDecodeFailureDoesNotUseTheRawExceptionMessage()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "AudioVisualFact",
                "SanitySmapiResourceService.cs"
            )
        );
        var start = source.IndexOf(
            "public SanityPhysicalResourceCreationResult CreateSoundEffect(",
            StringComparison.Ordinal
        );
        var end = source.IndexOf("internal sealed class XnaSanitySoundResource", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var soundFactory = source[start..end];

        Assert.Contains("exception.GetType().Name", soundFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", soundFactory, StringComparison.Ordinal);
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
        var path = "Asset/Sanity/Audio/Creatures/shared.ogg";
        var bytes = BuildOgg(44100);
        files.Add(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var first = cache.Load(
            "sanity.cue.first",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: SanityOggFormatCatalog.VorbisStereo44100V1
        );
        var conflicting = cache.Load(
            "sanity.cue.second",
            path,
            SanityPhysicalResourceKind.SoundEffect,
            hash,
            required: true,
            isPlaceholder: false,
            expectedFormatId: SanityOggFormatCatalog.VorbisStereo48000V1
        );

        Assert.True(first.Success, first.Diagnostic.Reason);
        Assert.False(conflicting.Success);
        Assert.Equal("resource.sound.cached-format-mismatch", conflicting.Diagnostic.Code);
        Assert.Equal(1, factory.SoundCreates);
    }

    [Fact]
    public void CueResourceMapPreservesExactPoolOrderAndLegacyFlatList()
    {
        var first = new FakeResource("first.ogg");
        var second = new FakeResource("second.ogg");
        var third = new FakeResource("third.ogg");
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
            SanityOggFormatCatalog.VorbisStereo44100V1,
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

    private static byte[] BuildOgg(int sampleRate, int channels = 2, long frames = 4)
    {
        var bytes = new List<byte>();
        var identification = new byte[30];
        identification[0] = 1;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(identification, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(7), 0);
        identification[11] = checked((byte)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), checked((uint)sampleRate));
        identification[28] = 0x88;
        identification[29] = 1;
        AppendPage(bytes, headerType: 2, granulePosition: 0, sequence: 0, identification);

        var comment = new byte[15];
        comment[0] = 3;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(comment, 1);
        AppendPage(bytes, headerType: 0, granulePosition: 0, sequence: 1, comment);
        AppendPage(bytes, headerType: 4, granulePosition: frames, sequence: 2, new byte[] { 0 });
        return bytes.ToArray();
    }

    private static void AppendPage(
        List<byte> output,
        byte headerType,
        long granulePosition,
        uint sequence,
        byte[] packet
    )
    {
        output.AddRange(Encoding.ASCII.GetBytes("OggS"));
        output.Add(0);
        output.Add(headerType);
        var numeric = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(numeric, granulePosition);
        output.AddRange(numeric);
        numeric = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(numeric, 7);
        output.AddRange(numeric);
        BinaryPrimitives.WriteUInt32LittleEndian(numeric, sequence);
        output.AddRange(numeric);
        output.AddRange(new byte[4]);
        output.Add(1);
        output.Add(checked((byte)packet.Length));
        output.AddRange(packet);
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

        internal string? SoundCreationFailureMessage { get; init; }

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
            if (SoundCreationFailureMessage is not null)
                throw new InvalidDataException(SoundCreationFailureMessage);
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
