#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Resource.Sanity;

internal enum SanityResourceCapabilityStatus
{
    Available,
    DisabledOptional,
    UnavailableRequired,
    InvalidMetadata,
    LoaderDisabled,
    LoaderDisposed,
}

internal enum SanityPhysicalResourceKind
{
    Texture,
    SoundEffect,
}

internal enum SanityResourceReleaseReason
{
    WorldCleanup,
    ReturnedToTitle,
    SystemDisabled,
    ContentInvalidated,
    Dispose,
}

internal enum SanityVisualPreviewKind
{
    Texture,
    AnimationFrame,
    StaticSprite,
    NineSliceOverlay,
}

internal sealed class SanityRuntimeResourceDiagnostic
{
    internal SanityRuntimeResourceDiagnostic(
        string capability,
        SanityResourceCapabilityStatus status,
        string code,
        string slotId,
        string path,
        bool required,
        bool isPlaceholder,
        string reason
    )
    {
        Capability = capability;
        Status = status;
        Code = code;
        SlotId = slotId;
        Path = path;
        Required = required;
        IsPlaceholder = isPlaceholder;
        Reason = reason;
    }

    internal string Capability { get; }

    internal SanityResourceCapabilityStatus Status { get; }

    internal string Code { get; }

    internal string SlotId { get; }

    internal string Path { get; }

    internal bool Required { get; }

    internal bool IsPlaceholder { get; }

    internal string Reason { get; }

    internal bool IsAvailable => Status == SanityResourceCapabilityStatus.Available;
}

internal readonly record struct SanityResourcePoint(int X, int Y);

internal readonly record struct SanityResourceRectangle(int X, int Y, int Width, int Height);

internal sealed class SanityVisualPreviewDefinition
{
    internal SanityVisualPreviewDefinition(
        string requestedSlotId,
        string textureSlotId,
        SanityVisualPreviewKind kind,
        SanityResourceRectangle sourceRectangle,
        SanityResourcePoint? pivotSourcePx,
        SanityResourcePoint actorOriginSourcePx,
        SanityResourceRectangle? hurtBoxSourcePx,
        SanityResourceRectangle? attackBoxSourcePx,
        SanityResourceRectangle? sliceSourcePx,
        int frameIndex,
        int frameCount,
        double drawScale,
        bool ownerLocalOnly,
        bool isProvisional,
        bool isPlaceholder
    )
    {
        RequestedSlotId = requestedSlotId;
        TextureSlotId = textureSlotId;
        Kind = kind;
        SourceRectangle = sourceRectangle;
        PivotSourcePx = pivotSourcePx;
        ActorOriginSourcePx = actorOriginSourcePx;
        HurtBoxSourcePx = hurtBoxSourcePx;
        AttackBoxSourcePx = attackBoxSourcePx;
        SliceSourcePx = sliceSourcePx;
        FrameIndex = frameIndex;
        FrameCount = frameCount;
        DrawScale = drawScale;
        OwnerLocalOnly = ownerLocalOnly;
        IsProvisional = isProvisional;
        IsPlaceholder = isPlaceholder;
    }

    internal string RequestedSlotId { get; }

    internal string TextureSlotId { get; }

    internal SanityVisualPreviewKind Kind { get; }

    internal SanityResourceRectangle SourceRectangle { get; }

    internal SanityResourcePoint? PivotSourcePx { get; }

    internal SanityResourcePoint ActorOriginSourcePx { get; }

    internal SanityResourceRectangle? HurtBoxSourcePx { get; }

    internal SanityResourceRectangle? AttackBoxSourcePx { get; }

    internal SanityResourceRectangle? SliceSourcePx { get; }

    internal int FrameIndex { get; }

    internal int FrameCount { get; }

    internal double DrawScale { get; }

    internal bool OwnerLocalOnly { get; }

    internal bool IsProvisional { get; }

    internal bool IsPlaceholder { get; }
}

internal sealed class SanityCueClipDefinition
{
    internal SanityCueClipDefinition(
        string clipId,
        string path,
        string sha256,
        string formatId,
        bool isPlaceholder,
        long durationFrames,
        double durationSeconds
    )
    {
        ClipId = clipId;
        Path = path;
        Sha256 = sha256;
        FormatId = formatId;
        IsPlaceholder = isPlaceholder;
        DurationFrames = durationFrames;
        DurationSeconds = durationSeconds;
    }

    internal string ClipId { get; }

    internal string Path { get; }

    internal string Sha256 { get; }

    internal string FormatId { get; }

    internal bool IsPlaceholder { get; }

    internal long DurationFrames { get; }

    internal double DurationSeconds { get; }
}

internal sealed class SanityCueDefinition
{
    internal SanityCueDefinition(
        string cueId,
        string playbackMode,
        bool enabled,
        bool requiredForRelease,
        bool isPlaceholder,
        string listeningStatus,
        IReadOnlyList<SanityCueClipDefinition> clips
    )
    {
        CueId = cueId;
        PlaybackMode = playbackMode;
        Enabled = enabled;
        RequiredForRelease = requiredForRelease;
        IsPlaceholder = isPlaceholder;
        ListeningStatus = listeningStatus;
        Clips = Copy(clips);
    }

    internal string CueId { get; }

    internal string PlaybackMode { get; }

    internal bool Enabled { get; }

    internal bool RequiredForRelease { get; }

    internal bool IsPlaceholder { get; }

    internal string ListeningStatus { get; }

    internal IReadOnlyList<SanityCueClipDefinition> Clips { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        var copy = new T[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];
        return Array.AsReadOnly(copy);
    }
}

internal sealed class SanityCueSetDefinition
{
    internal SanityCueSetDefinition(
        string cueSetId,
        string group,
        string cachePolicy,
        string lifecyclePolicy,
        int maxConcurrentInstances,
        IReadOnlyList<SanityCueDefinition> cues
    )
    {
        CueSetId = cueSetId;
        Group = group;
        CachePolicy = cachePolicy;
        LifecyclePolicy = lifecyclePolicy;
        MaxConcurrentInstances = maxConcurrentInstances;
        Cues = Copy(cues);
    }

    internal string CueSetId { get; }

    internal string Group { get; }

    internal string CachePolicy { get; }

    internal string LifecyclePolicy { get; }

    internal int MaxConcurrentInstances { get; }

    internal IReadOnlyList<SanityCueDefinition> Cues { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        var copy = new T[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];
        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// GPU/audio wrappers are owned by the loader cache. Callers may inspect them but must never dispose them.
/// </summary>
internal interface ISanityPhysicalResource : IDisposable
{
    SanityPhysicalResourceKind Kind { get; }

    string Path { get; }
}

internal interface ISanityPhysicalResourceFactory
{
    SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes);

    SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes);
}

internal sealed class SanityPhysicalResourceCreationResult
{
    private SanityPhysicalResourceCreationResult(
        ISanityPhysicalResource? resource,
        string code,
        string reason
    )
    {
        Resource = resource;
        Code = code;
        Reason = reason;
    }

    internal ISanityPhysicalResource? Resource { get; }

    internal string Code { get; }

    internal string Reason { get; }

    internal bool Success => Resource is not null;

    internal static SanityPhysicalResourceCreationResult Created(
        ISanityPhysicalResource resource
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new SanityPhysicalResourceCreationResult(
            resource,
            "resource.factory.created",
            "The physical resource was created on the owning runtime thread."
        );
    }

    internal static SanityPhysicalResourceCreationResult Failed(string code, string reason)
    {
        return new SanityPhysicalResourceCreationResult(null, code, reason);
    }
}

internal sealed class SanityCueSetRuntimeResource
{
    internal SanityCueSetRuntimeResource(
        SanityCueSetDefinition definition,
        IReadOnlyList<ISanityPhysicalResource> physicalResources
    )
    {
        Definition = definition;
        PhysicalResources = Copy(physicalResources);
    }

    internal SanityCueSetDefinition Definition { get; }

    internal IReadOnlyList<ISanityPhysicalResource> PhysicalResources { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        var copy = new T[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];
        return Array.AsReadOnly(copy);
    }
}

internal sealed class SanitySlotResourceResult
{
    internal SanitySlotResourceResult(
        SanityRuntimeResourceDiagnostic diagnostic,
        ISanityPhysicalResource? physicalResource = null,
        SanityVisualPreviewDefinition? visualPreview = null,
        SanityCueSetRuntimeResource? cueSet = null,
        SanityCueDefinition? cue = null
    )
    {
        Diagnostic = diagnostic;
        PhysicalResource = physicalResource;
        VisualPreview = visualPreview;
        CueSet = cueSet;
        Cue = cue;
    }

    internal SanityRuntimeResourceDiagnostic Diagnostic { get; }

    internal ISanityPhysicalResource? PhysicalResource { get; }

    internal SanityVisualPreviewDefinition? VisualPreview { get; }

    internal SanityCueSetRuntimeResource? CueSet { get; }

    internal SanityCueDefinition? Cue { get; }

    internal bool Success => Diagnostic.IsAvailable;
}

internal sealed class SanityRuntimeResourceSnapshot
{
    internal SanityRuntimeResourceSnapshot(
        bool enabled,
        bool disposed,
        long generation,
        long cacheHits,
        long cacheMisses,
        int manifestParseCount,
        int visualMetadataParseCount,
        int audioMetadataParseCount,
        int physicalResourceCount,
        int cueSetCount,
        IReadOnlyCollection<string> placeholderSlotIds,
        IReadOnlyCollection<string> disabledOptionalSlotIds,
        IReadOnlyCollection<SanityRuntimeResourceDiagnostic> diagnostics
    )
    {
        Enabled = enabled;
        Disposed = disposed;
        Generation = generation;
        CacheHits = cacheHits;
        CacheMisses = cacheMisses;
        ManifestParseCount = manifestParseCount;
        VisualMetadataParseCount = visualMetadataParseCount;
        AudioMetadataParseCount = audioMetadataParseCount;
        PhysicalResourceCount = physicalResourceCount;
        CueSetCount = cueSetCount;
        PlaceholderSlotIds = Copy(placeholderSlotIds);
        DisabledOptionalSlotIds = Copy(disabledOptionalSlotIds);
        Diagnostics = Copy(diagnostics);
    }

    internal bool Enabled { get; }

    internal bool Disposed { get; }

    internal long Generation { get; }

    internal long CacheHits { get; }

    internal long CacheMisses { get; }

    internal int ManifestParseCount { get; }

    internal int VisualMetadataParseCount { get; }

    internal int AudioMetadataParseCount { get; }

    internal int PhysicalResourceCount { get; }

    internal int CueSetCount { get; }

    internal IReadOnlyList<string> PlaceholderSlotIds { get; }

    internal IReadOnlyList<string> DisabledOptionalSlotIds { get; }

    internal IReadOnlyList<SanityRuntimeResourceDiagnostic> Diagnostics { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyCollection<T> values)
    {
        return new ReadOnlyCollection<T>(new List<T>(values));
    }
}
