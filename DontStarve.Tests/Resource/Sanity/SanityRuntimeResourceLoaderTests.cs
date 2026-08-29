using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityRuntimeResourceLoaderTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    [Fact]
    public void ShippedCatalogPrimesOnceAndKeepsPlaceholderDiagnosticsExplicit()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var first = loader.Prime();
        var second = loader.Prime();

        Assert.Equal(1, first.ManifestParseCount);
        Assert.Equal(1, second.ManifestParseCount);
        Assert.Equal(33, first.PlaceholderSlotIds.Count);
        Assert.Empty(first.DisabledOptionalSlotIds);
        Assert.Equal(0, factory.TotalCreates);
        Assert.Contains(first.Diagnostics, value => value.Code == "asset.placeholder-pending");
    }

    [Fact]
    public void WrongDeploymentRootFailsClosedWithoutTryingSourcePrefixedFallbacks()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(
            Path.GetFullPath(Path.Combine(ShippedModRoot, "..", "..", "..", "..")),
            factory
        );

        var result = loader.LoadSlot("sanity.asset.mr-skitts.sprite");

        Assert.False(result.Success);
        Assert.Equal(SanityResourceCapabilityStatus.InvalidMetadata, result.Diagnostic.Status);
        Assert.Equal("asset.read-failed", result.Diagnostic.Code);
        Assert.Equal(0, factory.TotalCreates);
    }

    [Fact]
    public void RequiredMissingIsUnavailableWhileOptionalMissingDisablesOnlyThatSlot()
    {
        var files = new MemoryFiles();
        files.AddText(
            SanityRuntimeResourceLoader.ManifestRelativePath,
            Manifest(
                Slot("sanity.asset.required.sprite", "Asset/Sanity/Sprites/required.png", required: true),
                Slot("sanity.asset.optional.sprite", "Asset/Sanity/Sprites/optional.png", required: false)
            )
        );
        files.AddText(SanityRuntimeResourceLoader.CreditsRelativePath, Credits());
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(files.DeploymentRoot, factory, files);

        var required = loader.LoadSlot("sanity.asset.required.sprite");
        var optional = loader.LoadSlot("sanity.asset.optional.sprite");

        Assert.Equal(SanityResourceCapabilityStatus.UnavailableRequired, required.Diagnostic.Status);
        Assert.Equal("asset.required-missing", required.Diagnostic.Code);
        Assert.Equal(SanityResourceCapabilityStatus.DisabledOptional, optional.Diagnostic.Status);
        Assert.Equal("asset.optional-missing", optional.Diagnostic.Code);
        Assert.Equal(0, factory.TotalCreates);
    }

    [Fact]
    public void BadManifestAndUnsafePathsReturnStableMetadataFailures()
    {
        var badJsonFiles = new MemoryFiles();
        badJsonFiles.AddText(SanityRuntimeResourceLoader.ManifestRelativePath, "{");
        badJsonFiles.AddText(SanityRuntimeResourceLoader.CreditsRelativePath, Credits());
        using var badJson = new SanityRuntimeResourceLoader(
            badJsonFiles.DeploymentRoot,
            new FakeFactory(),
            badJsonFiles
        );

        var malformed = badJson.LoadSlot("sanity.asset.any.sprite");
        Assert.Equal(SanityResourceCapabilityStatus.InvalidMetadata, malformed.Diagnostic.Status);
        Assert.Equal("manifest.invalid-json", malformed.Diagnostic.Code);

        var unsafeFiles = new MemoryFiles();
        unsafeFiles.AddText(
            SanityRuntimeResourceLoader.ManifestRelativePath,
            Manifest(Slot("sanity.asset.unsafe.sprite", "DontStarve/Asset/Sanity/Sprites/unsafe.png", true))
        );
        unsafeFiles.AddText(SanityRuntimeResourceLoader.CreditsRelativePath, Credits());
        using var unsafeLoader = new SanityRuntimeResourceLoader(
            unsafeFiles.DeploymentRoot,
            new FakeFactory(),
            unsafeFiles
        );

        var unsafeResult = unsafeLoader.LoadSlot("sanity.asset.unsafe.sprite");
        Assert.False(unsafeResult.Success);
        Assert.Contains(
            unsafeResult.Diagnostic.Code,
            new[] { "path.source-prefix", "manifest.invalid-path" }
        );
    }

    [Fact]
    public void AnimationSlotsShareOnePhysicalTextureAndParseMetadataOnce()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var idle = loader.LoadSlot("sanity.animation.mr-skitts.idle", frameIndex: 0);
        var disappear = loader.LoadSlot("sanity.animation.mr-skitts.disappear", frameIndex: 1);
        var snapshot = loader.Snapshot();

        Assert.True(idle.Success, idle.Diagnostic.Reason);
        Assert.True(disappear.Success, disappear.Diagnostic.Reason);
        Assert.Same(idle.PhysicalResource, disappear.PhysicalResource);
        Assert.Equal(1, factory.TextureCreates);
        Assert.Equal(1, snapshot.VisualMetadataParseCount);
        Assert.Equal(1, snapshot.PhysicalResourceCount);
        Assert.True(snapshot.CacheHits >= 1);
        Assert.Equal(1, disappear.VisualPreview!.FrameIndex);
    }

    [Fact]
    public void HostilePreviewExposesFramePivotAndCollisionWithoutGameplayState()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var result = loader.LoadSlot("sanity.animation.creeper-fear.attack", frameIndex: 2);

        Assert.True(result.Success, result.Diagnostic.Reason);
        var preview = Assert.IsType<SanityVisualPreviewDefinition>(result.VisualPreview);
        Assert.Equal(SanityVisualPreviewKind.AnimationFrame, preview.Kind);
        Assert.Equal(new SanityResourceRectangle(128, 384, 64, 96), preview.SourceRectangle);
        Assert.Equal(new SanityResourcePoint(32, 48), preview.PivotSourcePx);
        Assert.Equal(new SanityResourceRectangle(4, 48, 56, 48), preview.HurtBoxSourcePx);
        Assert.Equal(new SanityResourceRectangle(0, 64, 64, 64), preview.AttackBoxSourcePx);
        Assert.True(preview.IsProvisional);

        var outOfRange = loader.LoadSlot("sanity.animation.creeper-fear.attack", frameIndex: 4);
        Assert.False(outOfRange.Success);
        Assert.Equal("resource.preview.frame-out-of-range", outOfRange.Diagnostic.Code);
    }

    [Fact]
    public void PlaceholderIsVisibleInBothLoadAndOwnerLocalPreviewResults()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);
        var controller = new SanityResourcePreviewController(loader);

        var selection = controller.Select("owner-a", "sanity.animation.eyes.blink", 0);

        Assert.True(selection.Result.Success, selection.Result.Diagnostic.Reason);
        Assert.True(selection.Result.Diagnostic.IsPlaceholder);
        Assert.Contains("placeholder", selection.Result.Diagnostic.Code, StringComparison.Ordinal);
        Assert.True(controller.TryGet("owner-a", out var current));
        Assert.Same(selection, current);
    }

    [Fact]
    public void OptionalCreeperDespawnDoesNotMasqueradeAsImplementedThroughSharedJson()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var result = loader.LoadSlot("sanity.animation.creeper-fear.despawn");

        Assert.False(result.Success);
        Assert.Equal(SanityResourceCapabilityStatus.DisabledOptional, result.Diagnostic.Status);
        Assert.Equal("resource.preview.visual-slot-not-described", result.Diagnostic.Code);
        Assert.Equal(0, factory.TotalCreates);
        Assert.Contains("sanity.animation.creeper-fear.despawn", loader.Snapshot().DisabledOptionalSlotIds);
    }

    [Fact]
    public void PreviewSelectionsDoNotCrossOwnersWhilePhysicalCacheIsShared()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);
        var controller = new SanityResourcePreviewController(loader);

        var first = controller.Select("owner-a", "sanity.animation.mr-skitts.idle", 0);
        var second = controller.Select("owner-b", "sanity.animation.mr-skitts.disappear", 1);

        Assert.True(controller.TryGet("owner-a", out var firstCurrent));
        Assert.True(controller.TryGet("owner-b", out var secondCurrent));
        Assert.Equal(first.SlotId, firstCurrent!.SlotId);
        Assert.Equal(second.SlotId, secondCurrent!.SlotId);
        Assert.NotEqual(firstCurrent.SlotId, secondCurrent.SlotId);
        Assert.Same(first.Result.PhysicalResource, second.Result.PhysicalResource);
        Assert.True(controller.ClearOwner("owner-a"));
        Assert.False(controller.TryGet("owner-a", out _));
        Assert.True(controller.TryGet("owner-b", out _));
    }

    [Fact]
    public void ReturnedToTitleStyleReleaseIsIdempotentAndNextRequestReloads()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        var firstResource = Assert.Single(factory.Resources);
        loader.ReleaseWorldResources(SanityResourceReleaseReason.ReturnedToTitle);
        loader.ReleaseWorldResources(SanityResourceReleaseReason.ReturnedToTitle);

        Assert.Equal(1, firstResource.DisposeCount);
        Assert.Equal(0, loader.Snapshot().PhysicalResourceCount);
        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        Assert.Equal(2, factory.TextureCreates);
    }

    [Fact]
    public void RepeatedContentRevisionInvalidatesOnceAndReparsesOnDemand()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        var first = Assert.Single(factory.Resources);
        loader.InvalidateContent(7);
        loader.InvalidateContent(7);

        Assert.Equal(1, first.DisposeCount);
        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        var snapshot = loader.Snapshot();
        Assert.Equal(2, snapshot.ManifestParseCount);
        Assert.Equal(2, snapshot.VisualMetadataParseCount);
        Assert.Equal(2, factory.TextureCreates);
    }

    [Fact]
    public void DisabledAndDisposePathsReleaseAndFailClosed()
    {
        var factory = new FakeFactory();
        var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        var first = Assert.Single(factory.Resources);
        loader.SetEnabled(false);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(
            SanityResourceCapabilityStatus.LoaderDisabled,
            loader.LoadSlot("sanity.animation.mr-skitts.idle").Diagnostic.Status
        );

        loader.SetEnabled(true);
        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        var second = factory.Resources.Last();
        loader.Dispose();
        loader.Dispose();
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(
            SanityResourceCapabilityStatus.LoaderDisposed,
            loader.LoadSlot("sanity.animation.mr-skitts.idle").Diagnostic.Status
        );
    }

    [Fact]
    public void CuePreviewLoadsOneBoundedSetOnceAndLeavesListeningPending()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var attack = loader.LoadSlot("sanity.cue.creeper-fear.attack");
        var hurt = loader.LoadSlot("sanity.cue.creeper-fear.hurt-sharp");
        var bySet = loader.LoadCueSet("sanity.cue.creeper-fear");
        var optional = loader.LoadSlot("sanity.cue.sanity-change.gain");
        var snapshot = loader.Snapshot();

        Assert.True(attack.Success, attack.Diagnostic.Reason);
        Assert.True(hurt.Success, hurt.Diagnostic.Reason);
        Assert.True(bySet.Success, bySet.Diagnostic.Reason);
        Assert.Same(attack.CueSet, hurt.CueSet);
        Assert.Same(attack.CueSet, bySet.CueSet);
        Assert.Equal("sanity.cue.creeper-fear", attack.CueSet!.Definition.CueSetId);
        Assert.Equal(50, attack.CueSet.PhysicalResources.Count);
        Assert.Equal(50, factory.SoundCreates);
        Assert.Equal("PendingRealMachine", attack.Cue!.ListeningStatus);
        Assert.False(attack.Diagnostic.IsPlaceholder);
        Assert.Equal(SanityResourceCapabilityStatus.DisabledOptional, optional.Diagnostic.Status);
        Assert.Equal("resource.cue.disabled-optional", optional.Diagnostic.Code);
        Assert.Contains("sanity.cue.sanity-change.gain", snapshot.DisabledOptionalSlotIds);
        Assert.Equal(1, snapshot.AudioMetadataParseCount);
        Assert.Equal(1, snapshot.CueSetCount);
    }

    [Fact]
    public void Gameplay_audio_sets_share_the_bounded_loader_cache_and_are_not_placeholders()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var ambience = loader.LoadCueSet("sanity.cue.ambience");
        var ambienceAgain = loader.LoadCueSet("sanity.cue.ambience");
        var whispers = loader.LoadCueSet("sanity.cue.whispers");
        var danger = loader.LoadCueSet("sanity.cue.thresholds");

        Assert.True(ambience.Success, ambience.Diagnostic.Reason);
        Assert.True(whispers.Success, whispers.Diagnostic.Reason);
        Assert.True(danger.Success, danger.Diagnostic.Reason);
        Assert.False(ambience.Diagnostic.IsPlaceholder);
        Assert.False(whispers.Diagnostic.IsPlaceholder);
        Assert.False(danger.Diagnostic.IsPlaceholder);
        Assert.Same(ambience.CueSet, ambienceAgain.CueSet);
        Assert.Equal(11, ambience.CueSet!.PhysicalResources.Count);
        Assert.Equal(11, whispers.CueSet!.PhysicalResources.Count);
        Assert.Single(danger.CueSet!.PhysicalResources);
        Assert.All(
            new[] { ambience.CueSet, whispers.CueSet, danger.CueSet },
            cueSet => Assert.Equal(1, cueSet!.Definition.MaxConcurrentInstances)
        );
        Assert.Equal(23, factory.SoundCreates);
        Assert.Equal(23, loader.Snapshot().PhysicalResourceCount);
        Assert.Equal(3, loader.Snapshot().CueSetCount);
    }

    [Fact]
    public void Unknown_gameplay_audio_set_fails_closed_without_creating_a_sound()
    {
        var factory = new FakeFactory();
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var result = loader.LoadCueSet("sanity.cue.missing");

        Assert.False(result.Success);
        Assert.Equal(SanityResourceCapabilityStatus.InvalidMetadata, result.Diagnostic.Status);
        Assert.Equal("resource.cue-set.unknown", result.Diagnostic.Code);
        Assert.Equal(0, factory.SoundCreates);
    }

    [Fact]
    public void FactoryFailureKeepsRequiredSlotUnavailableWithStableReason()
    {
        var factory = new FakeFactory { FailTextureCreation = true };
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        var result = loader.LoadSlot("sanity.animation.mr-skitts.idle");

        Assert.False(result.Success);
        Assert.Equal(SanityResourceCapabilityStatus.UnavailableRequired, result.Diagnostic.Status);
        Assert.Equal("test.texture-create-failed", result.Diagnostic.Code);
        Assert.Equal(0, loader.Snapshot().PhysicalResourceCount);
    }

    [Fact]
    public void PhysicalTextureCacheHasAnExplicitFrozenUpperBound()
    {
        var files = new MemoryFiles();
        var factory = new FakeFactory();
        var diagnostics = new List<SanityRuntimeResourceDiagnostic>();
        var cache = new SanityRuntimePhysicalResourceCache(
            files.DeploymentRoot,
            files,
            factory,
            diagnostics.Add
        );
        var bytes = ValidPng();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        for (var index = 0; index < 10; index++)
        {
            var path = $"Asset/Sanity/Sprites/bounded-{index}.png";
            files.Add(path, bytes);
            var result = cache.Load(
                $"sanity.asset.bounded-{index}.sprite",
                path,
                SanityPhysicalResourceKind.Texture,
                hash,
                required: true,
                isPlaceholder: false
            );
            if (index < 9)
                Assert.True(result.Success, result.Diagnostic.Reason);
            else
                Assert.Equal("resource.cache.capacity-exceeded", result.Diagnostic.Code);
        }

        Assert.Equal(9, cache.Count);
        Assert.Equal(9, factory.TextureCreates);
        Assert.Contains(diagnostics, value => value.Code == "resource.cache.capacity-exceeded");
        Assert.True(cache.Release(SanityResourceReleaseReason.WorldCleanup));
    }

    [Fact]
    public void PhysicalTextureCacheLoadsByPathWhenHashIsStaleAndReportsWarning()
    {
        var files = new MemoryFiles();
        var factory = new FakeFactory();
        var diagnostics = new List<SanityRuntimeResourceDiagnostic>();
        var cache = new SanityRuntimePhysicalResourceCache(
            files.DeploymentRoot,
            files,
            factory,
            diagnostics.Add
        );
        const string path = "Asset/Sanity/Sprites/stale-hash.png";
        files.Add(path, ValidPng());

        var result = cache.Load(
            "sanity.asset.stale-hash.sprite",
            path,
            SanityPhysicalResourceKind.Texture,
            new string('0', 64),
            required: true,
            isPlaceholder: false
        );

        Assert.True(result.Success, result.Diagnostic.Reason);
        Assert.Equal(1, factory.TextureCreates);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "resource.physical.hash-mismatch"
                && diagnostic.IsWarning
                && diagnostic.Status == SanityResourceCapabilityStatus.Available
        );
        Assert.True(cache.Release(SanityResourceReleaseReason.WorldCleanup));
    }

    [Fact]
    public void DisposeFailureIsRecordedAndNeverEscapesOrRetriesTheSameResource()
    {
        var factory = new FakeFactory { ThrowOnDispose = true };
        using var loader = new SanityRuntimeResourceLoader(ShippedModRoot, factory);

        Assert.True(loader.LoadSlot("sanity.animation.mr-skitts.idle").Success);
        var resource = Assert.Single(factory.Resources);
        loader.ReleaseWorldResources(SanityResourceReleaseReason.ReturnedToTitle);
        loader.ReleaseWorldResources(SanityResourceReleaseReason.ReturnedToTitle);

        Assert.Equal(1, resource.DisposeCount);
        Assert.Contains(
            loader.Snapshot().Diagnostics,
            diagnostic => diagnostic.Code == "resource.release.dispose-failed"
        );
    }

    private static string Manifest(params SlotSpec[] slots)
    {
        return JsonSerializer.Serialize(new { SchemaVersion = 1, Slots = slots });
    }

    private static byte[] ValidPng()
    {
        return new byte[]
        {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        };
    }

    private static string Credits()
    {
        return JsonSerializer.Serialize(
            new
            {
                SchemaVersion = 1,
                Groups = new[]
                {
                    new
                    {
                        CreditGroup = "ART-01",
                        DisplayName = "Test asset",
                        AttributionText = "Test asset",
                        SourceEvidenceId = "test:asset",
                        PermissionScope = "Project-original; public mod distribution permitted.",
                        IsPlaceholder = false,
                    },
                },
            }
        );
    }

    private static SlotSpec Slot(string id, string path, bool required)
    {
        return new SlotSpec
        {
            SlotId = id,
            Path = path,
            Kind = "Png",
            Sha256 = null,
            IsPlaceholder = false,
            ContractVersion = 1,
            RequiredForRelease = required,
            CreditGroup = "ART-01",
        };
    }

    private sealed class SlotSpec
    {
        public string SlotId { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string? Sha256 { get; init; }

        public bool IsPlaceholder { get; init; }

        public int ContractVersion { get; init; }

        public bool RequiredForRelease { get; init; }

        public string CreditGroup { get; init; } = string.Empty;
    }

    private sealed class MemoryFiles : ISanityAssetFileAccess
    {
        private readonly Dictionary<string, byte[]> files = new(PathComparer);

        internal MemoryFiles()
        {
            DeploymentRoot = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "dontstarve-sanity-runtime-virtual-mod")
            );
        }

        internal string DeploymentRoot { get; }

        internal void AddText(string relativePath, string text)
        {
            Add(relativePath, Encoding.UTF8.GetBytes(text));
        }

        internal void Add(string relativePath, byte[] bytes)
        {
            Assert.True(
                SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                    DeploymentRoot,
                    relativePath,
                    out var absolutePath,
                    out var reason
                ),
                reason
            );
            files[absolutePath] = bytes;
        }

        public bool FileExists(string absolutePath)
        {
            return files.ContainsKey(Path.GetFullPath(absolutePath));
        }

        public bool TryReadAllBytes(string absolutePath, out byte[] bytes, out string reason)
        {
            if (files.TryGetValue(Path.GetFullPath(absolutePath), out var stored))
            {
                bytes = stored.ToArray();
                reason = "asset.read";
                return true;
            }

            bytes = Array.Empty<byte>();
            reason = "asset.read-failed";
            return false;
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
    }

    private sealed class FakeFactory : ISanityPhysicalResourceFactory
    {
        internal bool FailTextureCreation { get; init; }

        internal bool ThrowOnDispose { get; init; }

        internal int TextureCreates { get; private set; }

        internal int SoundCreates { get; private set; }

        internal int TotalCreates => TextureCreates + SoundCreates;

        internal List<FakeResource> Resources { get; } = new();

        public SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes)
        {
            if (FailTextureCreation)
            {
                return SanityPhysicalResourceCreationResult.Failed(
                    "test.texture-create-failed",
                    "The fake texture factory was configured to fail."
                );
            }

            TextureCreates++;
            var resource = new FakeResource(
                SanityPhysicalResourceKind.Texture,
                path,
                ThrowOnDispose
            );
            Resources.Add(resource);
            return SanityPhysicalResourceCreationResult.Created(resource);
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes)
        {
            SoundCreates++;
            var resource = new FakeResource(
                SanityPhysicalResourceKind.SoundEffect,
                path,
                ThrowOnDispose
            );
            Resources.Add(resource);
            return SanityPhysicalResourceCreationResult.Created(resource);
        }
    }

    private sealed class FakeResource : ISanityPhysicalResource
    {
        private readonly bool throwOnDispose;

        internal FakeResource(
            SanityPhysicalResourceKind kind,
            string path,
            bool throwOnDispose
        )
        {
            Kind = kind;
            Path = path;
            this.throwOnDispose = throwOnDispose;
        }

        public SanityPhysicalResourceKind Kind { get; }

        public string Path { get; }

        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
                throw new InvalidOperationException("fake-dispose-failure");
        }
    }
}
