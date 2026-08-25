#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace DontStarve.Resource.Sanity;

/// <summary>
/// Owns decoded XNA resources by deployment-relative physical path. The explicit limits
/// match the frozen stage-05 contract, so malformed metadata cannot grow the cache unbounded.
/// </summary>
internal sealed class SanityRuntimePhysicalResourceCache
{
    private const int MaxTextureResources = 9;
    private const int MaxSoundResources = 140;

    private readonly string deploymentRoot;
    private readonly ISanityAssetFileAccess fileAccess;
    private readonly ISanityPhysicalResourceFactory resourceFactory;
    private readonly Action<SanityRuntimeResourceDiagnostic> reportDiagnostic;
    private readonly Dictionary<string, ISanityPhysicalResource> resources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> soundFormatIds =
        new(StringComparer.OrdinalIgnoreCase);

    internal SanityRuntimePhysicalResourceCache(
        string deploymentRoot,
        ISanityAssetFileAccess fileAccess,
        ISanityPhysicalResourceFactory resourceFactory,
        Action<SanityRuntimeResourceDiagnostic> reportDiagnostic
    )
    {
        this.deploymentRoot = deploymentRoot;
        this.fileAccess = fileAccess ?? throw new ArgumentNullException(nameof(fileAccess));
        this.resourceFactory = resourceFactory
            ?? throw new ArgumentNullException(nameof(resourceFactory));
        this.reportDiagnostic = reportDiagnostic
            ?? throw new ArgumentNullException(nameof(reportDiagnostic));
    }

    internal int Count => resources.Count;

    internal long Hits { get; private set; }

    internal long Misses { get; private set; }

    internal SanityPhysicalCacheLoadResult Load(
        string slotId,
        string path,
        SanityPhysicalResourceKind kind,
        string? expectedSha256,
        bool required,
        bool isPlaceholder,
        string? expectedFormatId = null
    )
    {
        SanityWavFormatDescriptor? expectedAudioFormat = null;
        if (kind == SanityPhysicalResourceKind.SoundEffect)
        {
            var resolvedFormatId = string.IsNullOrWhiteSpace(expectedFormatId)
                ? SanityWavFormatCatalog.PcmS16Stereo44100V1
                : expectedFormatId;
            if (!SanityWavFormatCatalog.TryGet(resolvedFormatId, out var descriptor))
            {
                return Failed(
                    slotId,
                    path,
                    required,
                    isPlaceholder,
                    "resource.sound.unknown-format-id",
                    $"The audio clip declared unsupported format id '{resolvedFormatId}'."
                );
            }

            expectedAudioFormat = descriptor;
        }

        var key = CreateKey(kind, path);
        if (resources.TryGetValue(key, out var cached))
        {
            if (
                kind == SanityPhysicalResourceKind.SoundEffect
                && (
                    expectedAudioFormat is null
                    || !soundFormatIds.TryGetValue(key, out var cachedFormatId)
                    || !string.Equals(
                        cachedFormatId,
                        expectedAudioFormat.FormatId,
                        StringComparison.Ordinal
                    )
                )
            )
            {
                return Failed(
                    slotId,
                    path,
                    required,
                    isPlaceholder,
                    "resource.sound.cached-format-mismatch",
                    $"The cached sound path is bound to format '{soundFormatIds.GetValueOrDefault(key, "unknown")}', not '{expectedAudioFormat?.FormatId ?? "unknown"}'."
                );
            }

            Hits++;
            return SanityPhysicalCacheLoadResult.Available(
                cached,
                new SanityRuntimeResourceDiagnostic(
                    "sanity.resource.physical",
                    SanityResourceCapabilityStatus.Available,
                    "resource.cache.hit",
                    slotId,
                    path,
                    required,
                    isPlaceholder,
                    "The deployment-relative physical resource was served from the shared bounded cache."
                ),
                createdCacheKey: null
            );
        }

        Misses++;
        if (CountKind(kind) >= GetLimit(kind))
        {
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                "resource.cache.capacity-exceeded",
                $"The bounded {kind} cache reached its frozen stage-06 capacity."
            );
        }
        if (
            !SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                deploymentRoot,
                path,
                out var absolutePath,
                out var pathReason
            )
        )
        {
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                pathReason,
                "The resource path was rejected by the single deployment-root path policy."
            );
        }
        if (!fileAccess.TryReadAllBytes(absolutePath, out var bytes, out var readReason))
        {
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                readReason,
                "The resource could not be read from the deployed Mod root."
            );
        }

        var actualHash = ToSha256(bytes);
        if (
            string.IsNullOrWhiteSpace(expectedSha256)
            || !string.Equals(expectedSha256, actualHash, StringComparison.OrdinalIgnoreCase)
        )
        {
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                "resource.physical.hash-mismatch",
                "The deployed file does not match its manifest/audio metadata SHA-256."
            );
        }

        if (kind == SanityPhysicalResourceKind.Texture)
        {
            if (!SanityAssetFileInspector.IsFormatValid(SanityAssetKind.Png, bytes))
            {
                return Failed(
                    slotId,
                    path,
                    required,
                    isPlaceholder,
                    "resource.texture.invalid-png",
                    "The deployed texture failed the PNG/IHDR format gate."
                );
            }
        }
        else
        {
            var wav = SanityWavInspector.Inspect(bytes);
            if (expectedAudioFormat is null || !expectedAudioFormat.Matches(wav))
            {
                return Failed(
                    slotId,
                    path,
                    required,
                    isPlaceholder,
                    "resource.sound.invalid-runtime-format",
                    $"The deployed WAV failed {expectedAudioFormat?.FormatId ?? "the declared runtime format"}."
                );
            }
        }

        SanityPhysicalResourceCreationResult creation;
        try
        {
            creation = kind == SanityPhysicalResourceKind.Texture
                ? resourceFactory.CreateTexture(path, bytes)
                : resourceFactory.CreateSoundEffect(path, bytes);
        }
        catch (Exception exception)
        {
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                "resource.factory.threw",
                $"The resource factory threw {exception.GetType().Name}: {exception.Message}"
            );
        }

        if (creation.Resource is null)
        {
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                creation.Code,
                creation.Reason
            );
        }
        if (
            creation.Resource.Kind != kind
            || !string.Equals(creation.Resource.Path, path, StringComparison.Ordinal)
        )
        {
            DisposeResource(
                creation.Resource,
                slotId,
                path,
                SanityResourceReleaseReason.WorldCleanup
            );
            return Failed(
                slotId,
                path,
                required,
                isPlaceholder,
                "resource.factory.contract-mismatch",
                "The resource factory returned a mismatched kind or deployment-relative path."
            );
        }

        resources.Add(key, creation.Resource);
        if (kind == SanityPhysicalResourceKind.SoundEffect && expectedAudioFormat is not null)
            soundFormatIds[key] = expectedAudioFormat.FormatId;
        return SanityPhysicalCacheLoadResult.Available(
            creation.Resource,
            new SanityRuntimeResourceDiagnostic(
                "sanity.resource.physical",
                SanityResourceCapabilityStatus.Available,
                "resource.cache.miss-loaded",
                slotId,
                path,
                required,
                isPlaceholder,
                "The physical resource was decoded once and inserted into the shared bounded cache."
            ),
            key
        );
    }

    internal void RollBackCreated(IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!resources.Remove(key, out var resource))
                continue;
            soundFormatIds.Remove(key);
            DisposeResource(
                resource,
                string.Empty,
                resource.Path,
                SanityResourceReleaseReason.WorldCleanup
            );
        }
    }

    internal bool Release(SanityResourceReleaseReason reason)
    {
        if (resources.Count == 0)
            return false;

        foreach (var pair in resources.ToArray())
            DisposeResource(pair.Value, string.Empty, pair.Value.Path, reason);
        resources.Clear();
        soundFormatIds.Clear();
        return true;
    }

    private SanityPhysicalCacheLoadResult Failed(
        string slotId,
        string path,
        bool required,
        bool isPlaceholder,
        string code,
        string reason
    )
    {
        var diagnostic = new SanityRuntimeResourceDiagnostic(
            "sanity.resource.physical",
            required
                ? SanityResourceCapabilityStatus.UnavailableRequired
                : SanityResourceCapabilityStatus.DisabledOptional,
            code,
            slotId,
            path,
            required,
            isPlaceholder,
            reason
        );
        reportDiagnostic(diagnostic);
        return SanityPhysicalCacheLoadResult.Failed(diagnostic);
    }

    private void DisposeResource(
        ISanityPhysicalResource resource,
        string slotId,
        string path,
        SanityResourceReleaseReason reason
    )
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            reportDiagnostic(
                new SanityRuntimeResourceDiagnostic(
                    "sanity.resource.release",
                    SanityResourceCapabilityStatus.InvalidMetadata,
                    "resource.release.dispose-failed",
                    slotId,
                    path,
                    required: false,
                    isPlaceholder: false,
                    $"Release {reason} caught {exception.GetType().Name}: {exception.Message}"
                )
            );
        }
    }

    private int CountKind(SanityPhysicalResourceKind kind)
    {
        return resources.Values.Count(resource => resource.Kind == kind);
    }

    private static int GetLimit(SanityPhysicalResourceKind kind)
    {
        return kind == SanityPhysicalResourceKind.Texture
            ? MaxTextureResources
            : MaxSoundResources;
    }

    private static string CreateKey(SanityPhysicalResourceKind kind, string path)
    {
        return string.Concat(kind.ToString(), ":", path);
    }

    private static string ToSha256(byte[] bytes)
    {
        return BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", string.Empty);
    }
}

internal sealed class SanityPhysicalCacheLoadResult
{
    private SanityPhysicalCacheLoadResult(
        ISanityPhysicalResource? resource,
        SanityRuntimeResourceDiagnostic diagnostic,
        string? createdCacheKey
    )
    {
        Resource = resource;
        Diagnostic = diagnostic;
        CreatedCacheKey = createdCacheKey;
    }

    internal ISanityPhysicalResource? Resource { get; }

    internal SanityRuntimeResourceDiagnostic Diagnostic { get; }

    internal string? CreatedCacheKey { get; }

    internal bool Success => Resource is not null && Diagnostic.IsAvailable;

    internal static SanityPhysicalCacheLoadResult Available(
        ISanityPhysicalResource resource,
        SanityRuntimeResourceDiagnostic diagnostic,
        string? createdCacheKey
    )
    {
        return new SanityPhysicalCacheLoadResult(resource, diagnostic, createdCacheKey);
    }

    internal static SanityPhysicalCacheLoadResult Failed(
        SanityRuntimeResourceDiagnostic diagnostic
    )
    {
        return new SanityPhysicalCacheLoadResult(null, diagnostic, null);
    }
}
