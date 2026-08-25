#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace DontStarve.Resource.Sanity;

/// <summary>
/// Manifest-driven Sanity resource owner. Metadata is parsed once per content revision;
/// GPU/audio objects are created lazily and shared by deployment-relative physical path.
/// </summary>
internal sealed class SanityRuntimeResourceLoader : IDisposable
{
    internal const string ManifestRelativePath = "Asset/Sanity/Data/sanity-assets.json";
    internal const string CreditsRelativePath = "Asset/Sanity/Data/sanity-credits.json";
    internal const string BindingsRelativePath = "Asset/Sanity/Data/resource-bindings.json";

    private const int MaxRuntimeDiagnostics = 128;
    private readonly object syncRoot = new();
    private readonly string deploymentRoot;
    private readonly ISanityAssetFileAccess fileAccess;
    private readonly SanityRuntimePhysicalResourceCache physicalCache;
    private readonly Dictionary<string, SanityCueSetRuntimeResource> cueSets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SanityRuntimeResourceDiagnostic> runtimeDiagnostics = new();
    private readonly HashSet<string> runtimeDiagnosticKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> runtimeDisabledOptionalSlotIds =
        new(StringComparer.OrdinalIgnoreCase);

    private CatalogState? catalog;
    private SanityRuntimeResourceDiagnostic? catalogFailure;
    private SanityVisualMetadataCatalog? visualMetadata;
    private SanityHostileAttackMetadataCatalog? hostileAttackMetadata;
    private SanityRuntimeResourceDiagnostic? visualMetadataFailure;
    private SanityAudioMetadataCatalog? audioMetadata;
    private SanityRuntimeResourceDiagnostic? audioMetadataFailure;
    private bool enabled = true;
    private bool disposed;
    private long generation;
    private long cacheHits;
    private long cacheMisses;
    private long? lastInvalidationRevision;
    private int manifestParseCount;
    private int visualMetadataParseCount;
    private int audioMetadataParseCount;

    internal SanityRuntimeResourceLoader(
        string deploymentRoot,
        ISanityPhysicalResourceFactory resourceFactory,
        ISanityAssetFileAccess? fileAccess = null
    )
    {
        if (string.IsNullOrWhiteSpace(deploymentRoot))
            throw new ArgumentException("A deployment root is required.", nameof(deploymentRoot));

        this.deploymentRoot = Path.GetFullPath(deploymentRoot);
        this.fileAccess = fileAccess ?? PhysicalSanityAssetFileAccess.Instance;
        physicalCache = new SanityRuntimePhysicalResourceCache(
            this.deploymentRoot,
            this.fileAccess,
            resourceFactory,
            RecordDiagnostic
        );
    }

    internal SanityRuntimeResourceSnapshot Prime()
    {
        lock (syncRoot)
        {
            EnsureCatalog();
            return CreateSnapshot();
        }
    }

    internal SanitySlotResourceResult LoadSlot(string slotId, int frameIndex = 0)
    {
        lock (syncRoot)
        {
            if (!TryBeginRequest(slotId, out var stateFailure))
                return new SanitySlotResourceResult(stateFailure!);

            if (catalog is null)
                return new SanitySlotResourceResult(catalogFailure!);

            if (!catalog.Slots.TryGetValue(slotId, out var requestedSlot))
            {
                return Fail(
                    "sanity.resource.slot",
                    SanityResourceCapabilityStatus.InvalidMetadata,
                    "resource.slot.unknown",
                    slotId,
                    string.Empty,
                    required: false,
                    isPlaceholder: false,
                    "The requested SlotId is not declared by sanity-assets.json."
                );
            }

            var gate = GetSlotGate(requestedSlot);
            if (!gate.IsAvailable)
                return new SanitySlotResourceResult(gate);

            if (slotId.StartsWith("sanity.cue.", StringComparison.OrdinalIgnoreCase))
                return LoadCueSlot(requestedSlot);

            if (
                requestedSlot.Kind == SanityAssetKind.Png
                || slotId.StartsWith("sanity.animation.", StringComparison.OrdinalIgnoreCase)
            )
            {
                return LoadVisualSlot(requestedSlot, frameIndex);
            }

            return Fail(
                "sanity.resource.slot",
                SanityResourceCapabilityStatus.InvalidMetadata,
                "resource.slot.unsupported-kind",
                requestedSlot.SlotId,
                requestedSlot.Path,
                requestedSlot.RequiredForRelease,
                requestedSlot.IsPlaceholder,
                "The runtime loader has no stage-06 resource interpretation for this slot kind."
            );
        }
    }

    internal SanitySlotResourceResult LoadCueSet(string cueSetId)
    {
        lock (syncRoot)
        {
            if (!TryBeginRequest(cueSetId, out var stateFailure))
                return new SanitySlotResourceResult(stateFailure!);
            if (catalog is null)
                return new SanitySlotResourceResult(catalogFailure!);
            if (!EnsureAudioMetadata(out var metadataDiagnostic))
                return new SanitySlotResourceResult(metadataDiagnostic!);
            if (audioMetadata is null || !audioMetadata.TryGetCueSet(cueSetId, out var definition))
            {
                return Fail(
                    "sanity.resource.cue-set",
                    SanityResourceCapabilityStatus.InvalidMetadata,
                    "resource.cue-set.unknown",
                    cueSetId,
                    string.Empty,
                    required: false,
                    isPlaceholder: false,
                    "The requested CueSetId is not declared by audio-cues.json."
                );
            }

            return LoadCueSetCore(definition!, requestedSlot: null, requestedCue: null);
        }
    }

    /// <summary>
    /// Reads validated cue timing without creating a SoundEffect. Countdown consumers use this
    /// seam at startup; the physical warning remains lazy until its audio claim actually fires.
    /// </summary>
    internal SanitySlotResourceResult GetCueMetadata(string cueId)
    {
        lock (syncRoot)
        {
            if (!TryBeginRequest(cueId, out var stateFailure))
                return new SanitySlotResourceResult(stateFailure!);
            if (catalog is null)
                return new SanitySlotResourceResult(catalogFailure!);
            if (!catalog.Slots.TryGetValue(cueId, out var requestedSlot))
            {
                return Fail(
                    "sanity.resource.cue-metadata",
                    SanityResourceCapabilityStatus.InvalidMetadata,
                    "resource.cue-metadata.unknown",
                    cueId,
                    string.Empty,
                    required: false,
                    isPlaceholder: false,
                    "The requested cue is not declared by sanity-assets.json."
                );
            }
            var gate = GetSlotGate(requestedSlot);
            if (!gate.IsAvailable)
                return new SanitySlotResourceResult(gate);
            if (!EnsureAudioMetadata(out var metadataDiagnostic))
                return new SanitySlotResourceResult(metadataDiagnostic!);
            if (
                audioMetadata is null
                || !audioMetadata.TryGetCue(cueId, out _, out var cue)
                || cue is null
            )
            {
                return Fail(
                    "sanity.resource.cue-metadata",
                    SanityResourceCapabilityStatus.InvalidMetadata,
                    "resource.cue-metadata.not-described",
                    requestedSlot.SlotId,
                    requestedSlot.Path,
                    requestedSlot.RequiredForRelease,
                    requestedSlot.IsPlaceholder,
                    "The requested cue is not described by audio-cues.json."
                );
            }

            var isPlaceholder = requestedSlot.IsPlaceholder || cue.IsPlaceholder;
            return new SanitySlotResourceResult(
                Available(
                    "sanity.resource.cue-metadata",
                    requestedSlot,
                    isPlaceholder,
                    isPlaceholder
                        ? "resource.cue-metadata.placeholder-available"
                        : "resource.cue-metadata.available",
                    "Validated cue timing is available without creating a physical audio resource."
                ),
                cue: cue
            );
        }
    }

    /// <summary>
    /// Returns cached common attack semantics from the same strict metadata revision used by the
    /// visual loader. No caller performs a second disk read or owns a second metadata registry.
    /// </summary>
    internal bool TryGetHostileAttackMetadata(
        string assetBindingId,
        out SanityHostileAttackMetadataDefinition? definition,
        out string reason
    )
    {
        lock (syncRoot)
        {
            definition = null;
            if (!TryBeginRequest(assetBindingId, out var stateFailure))
            {
                reason = stateFailure?.Code ?? "resource.hostile-attack-metadata.unavailable";
                return false;
            }
            if (!EnsureVisualMetadata(out var metadataDiagnostic))
            {
                reason = metadataDiagnostic?.Code
                    ?? "resource.hostile-attack-metadata.unavailable";
                return false;
            }
            if (
                hostileAttackMetadata is null
                || !hostileAttackMetadata.TryGet(assetBindingId, out definition)
                || definition is null
            )
            {
                reason = "resource.hostile-attack-metadata.binding-unknown";
                return false;
            }

            reason = "resource.hostile-attack-metadata.available";
            return true;
        }
    }

    internal void SetEnabled(bool value)
    {
        lock (syncRoot)
        {
            if (disposed || enabled == value)
                return;

            enabled = value;
            generation++;
            if (!value)
                ReleasePhysicalResources(SanityResourceReleaseReason.SystemDisabled);
        }
    }

    internal void ReleaseWorldResources(SanityResourceReleaseReason reason)
    {
        lock (syncRoot)
        {
            if (disposed)
                return;
            ReleasePhysicalResources(reason);
        }
    }

    internal void InvalidateContent(long revision)
    {
        lock (syncRoot)
        {
            if (disposed || lastInvalidationRevision == revision)
                return;

            lastInvalidationRevision = revision;
            ReleasePhysicalResources(SanityResourceReleaseReason.ContentInvalidated);
            ClearMetadataCaches();
            generation++;
        }
    }

    internal IReadOnlyList<string> GetKnownAssetPaths()
    {
        lock (syncRoot)
        {
            EnsureCatalog();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ManifestRelativePath,
                CreditsRelativePath,
                BindingsRelativePath,
            };
            if (catalog is not null)
            {
                foreach (var slot in catalog.Slots.Values)
                    paths.Add(slot.Path);
            }
            return new ReadOnlyCollection<string>(
                paths.OrderBy(value => value, StringComparer.Ordinal).ToList()
            );
        }
    }

    internal SanityRuntimeResourceSnapshot Snapshot()
    {
        lock (syncRoot)
            return CreateSnapshot();
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
                return;

            disposed = true;
            enabled = false;
            ReleasePhysicalResources(SanityResourceReleaseReason.Dispose);
            ClearMetadataCaches();
            generation++;
        }
    }

    private SanitySlotResourceResult LoadVisualSlot(
        SanityAssetSlot requestedSlot,
        int frameIndex
    )
    {
        if (!EnsureVisualMetadata(out var metadataDiagnostic))
            return new SanitySlotResourceResult(metadataDiagnostic!);

        var previewReason = "resource.visual-metadata.unavailable";
        SanityVisualPreviewDefinition? preview = null;
        if (
            visualMetadata is null
            || !visualMetadata.TryCreatePreview(
                requestedSlot.SlotId,
                frameIndex,
                out preview,
                out previewReason
            )
        )
        {
            return Fail(
                "sanity.resource.preview",
                requestedSlot.RequiredForRelease
                    ? SanityResourceCapabilityStatus.UnavailableRequired
                    : SanityResourceCapabilityStatus.DisabledOptional,
                previewReason,
                requestedSlot.SlotId,
                requestedSlot.Path,
                requestedSlot.RequiredForRelease,
                requestedSlot.IsPlaceholder,
                "The requested visual slot has no valid stage-03/04 preview descriptor."
            );
        }

        if (catalog is null || !catalog.Slots.TryGetValue(preview!.TextureSlotId, out var textureSlot))
        {
            return Fail(
                "sanity.resource.preview",
                SanityResourceCapabilityStatus.InvalidMetadata,
                "resource.preview.texture-slot-missing",
                requestedSlot.SlotId,
                requestedSlot.Path,
                requestedSlot.RequiredForRelease,
                requestedSlot.IsPlaceholder,
                "The visual metadata names a texture SlotId that is absent from sanity-assets.json."
            );
        }

        var textureGate = GetSlotGate(textureSlot);
        if (!textureGate.IsAvailable)
            return new SanitySlotResourceResult(textureGate, visualPreview: preview);

        var load = physicalCache.Load(
            textureSlot.SlotId,
            textureSlot.Path,
            SanityPhysicalResourceKind.Texture,
            textureSlot.Sha256,
            textureSlot.RequiredForRelease,
            requestedSlot.IsPlaceholder || textureSlot.IsPlaceholder || preview.IsPlaceholder
        );
        if (!load.Success)
            return new SanitySlotResourceResult(load.Diagnostic, visualPreview: preview);

        var isPlaceholder =
            requestedSlot.IsPlaceholder || textureSlot.IsPlaceholder || preview.IsPlaceholder;
        var diagnostic = Available(
            "sanity.resource.preview",
            requestedSlot,
            isPlaceholder,
            isPlaceholder ? "resource.preview.placeholder-available" : "resource.preview.available",
            isPlaceholder
                ? "The single-slot visual preview is available and explicitly marked as placeholder/provisional."
                : "The single-slot visual preview is available from the deployment-root cache."
        );
        return new SanitySlotResourceResult(
            diagnostic,
            load.Resource,
            preview
        );
    }

    private SanitySlotResourceResult LoadCueSlot(SanityAssetSlot requestedSlot)
    {
        if (!EnsureAudioMetadata(out var metadataDiagnostic))
            return new SanitySlotResourceResult(metadataDiagnostic!);
        if (
            audioMetadata is null
            || !audioMetadata.TryGetCue(
                requestedSlot.SlotId,
                out var cueSetId,
                out var cue
            )
        )
        {
            return Fail(
                "sanity.resource.cue",
                SanityResourceCapabilityStatus.InvalidMetadata,
                "resource.cue.not-described",
                requestedSlot.SlotId,
                requestedSlot.Path,
                requestedSlot.RequiredForRelease,
                requestedSlot.IsPlaceholder,
                "The cue SlotId is not described by audio-cues.json."
            );
        }

        if (!cue!.Enabled)
        {
            return Fail(
                "sanity.resource.cue",
                SanityResourceCapabilityStatus.DisabledOptional,
                "resource.cue.disabled-optional",
                requestedSlot.SlotId,
                requestedSlot.Path,
                requestedSlot.RequiredForRelease,
                requestedSlot.IsPlaceholder || cue.IsPlaceholder,
                "The optional cue is explicitly disabled and has no runtime clip cache."
            );
        }

        if (!audioMetadata.TryGetCueSet(cueSetId, out var definition))
        {
            return Fail(
                "sanity.resource.cue",
                SanityResourceCapabilityStatus.InvalidMetadata,
                "resource.cue.cue-set-missing",
                requestedSlot.SlotId,
                requestedSlot.Path,
                requestedSlot.RequiredForRelease,
                requestedSlot.IsPlaceholder || cue.IsPlaceholder,
                "The cue names a CueSetId that is not present in audio-cues.json."
            );
        }

        return LoadCueSetCore(definition!, requestedSlot, cue);
    }

    private SanitySlotResourceResult LoadCueSetCore(
        SanityCueSetDefinition definition,
        SanityAssetSlot? requestedSlot,
        SanityCueDefinition? requestedCue
    )
    {
        if (cueSets.TryGetValue(definition.CueSetId, out var cached))
        {
            cacheHits++;
            return CreateCueSetSuccess(cached, requestedSlot, requestedCue);
        }

        cacheMisses++;
        var resources = new List<ISanityPhysicalResource>();
        var resourcesByCueId = new Dictionary<string, List<ISanityPhysicalResource>>(StringComparer.Ordinal);
        var createdKeys = new List<string>();
        foreach (var cue in definition.Cues)
        {
            var cueResources = new List<ISanityPhysicalResource>();
            resourcesByCueId[cue.CueId] = cueResources;
            if (!cue.Enabled)
                continue;

            foreach (var clip in cue.Clips)
            {
                var load = physicalCache.Load(
                    requestedSlot?.SlotId ?? definition.CueSetId,
                    clip.Path,
                    SanityPhysicalResourceKind.SoundEffect,
                    clip.Sha256,
                    cue.RequiredForRelease,
                    cue.IsPlaceholder || clip.IsPlaceholder,
                    clip.FormatId
                );
                if (!load.Success)
                {
                    physicalCache.RollBackCreated(createdKeys);
                    return new SanitySlotResourceResult(
                        load.Diagnostic,
                        cue: requestedCue
                    );
                }

                resources.Add(load.Resource!);
                cueResources.Add(load.Resource!);
                if (load.CreatedCacheKey is not null)
                    createdKeys.Add(load.CreatedCacheKey);
            }
        }

        var cueResourceMap = new Dictionary<string, IReadOnlyList<ISanityPhysicalResource>>(
            StringComparer.Ordinal
        );
        foreach (var pair in resourcesByCueId)
            cueResourceMap[pair.Key] = pair.Value;

        var runtime = new SanityCueSetRuntimeResource(definition, resources, cueResourceMap);
        cueSets.Add(definition.CueSetId, runtime);
        return CreateCueSetSuccess(runtime, requestedSlot, requestedCue);
    }

    private SanitySlotResourceResult CreateCueSetSuccess(
        SanityCueSetRuntimeResource runtime,
        SanityAssetSlot? requestedSlot,
        SanityCueDefinition? requestedCue
    )
    {
        var placeholder = requestedSlot?.IsPlaceholder == true
            || requestedCue?.IsPlaceholder == true
            || runtime.Definition.Cues.Any(
                cue => cue.IsPlaceholder || cue.Clips.Any(clip => clip.IsPlaceholder)
            );
        var slotId = requestedSlot?.SlotId ?? runtime.Definition.CueSetId;
        var path = requestedSlot?.Path ?? string.Empty;
        var diagnostic = new SanityRuntimeResourceDiagnostic(
            "sanity.resource.cue-set",
            SanityResourceCapabilityStatus.Available,
            placeholder ? "resource.cue-set.placeholder-available" : "resource.cue-set.available",
            slotId,
            path,
            requestedSlot?.RequiredForRelease ?? true,
            placeholder,
            placeholder
                ? "The bounded CueSet cache is available and includes explicit DEV placeholder audio; listening remains PendingRealMachine."
                : "The bounded CueSet cache is available; listening remains PendingRealMachine."
        );
        return new SanitySlotResourceResult(
            diagnostic,
            cueSet: runtime,
            cue: requestedCue
        );
    }

    private bool EnsureCatalog()
    {
        if (catalog is not null)
            return true;
        if (catalogFailure is not null)
            return false;

        manifestParseCount++;
        if (!TryReadStrictUtf8(ManifestRelativePath, out var manifestJson, out var manifestFailure))
        {
            catalogFailure = manifestFailure;
            return false;
        }
        if (!TryReadStrictUtf8(CreditsRelativePath, out var creditsJson, out var creditsFailure))
        {
            catalogFailure = creditsFailure;
            return false;
        }

        var parsed = SanityAssetManifestParser.Parse(manifestJson);
        if (!parsed.Success || parsed.Manifest is null)
        {
            catalogFailure = new SanityRuntimeResourceDiagnostic(
                "sanity.resource.catalog",
                SanityResourceCapabilityStatus.InvalidMetadata,
                parsed.Code,
                string.Empty,
                ManifestRelativePath,
                required: true,
                isPlaceholder: false,
                parsed.Reason
            );
            RecordDiagnostic(catalogFailure);
            return false;
        }

        var validation = SanityAssetValidator.Validate(
            manifestJson,
            creditsJson,
            deploymentRoot,
            SanityAssetValidationGate.Development,
            fileAccess
        );
        var slots = new Dictionary<string, SanityAssetSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in parsed.Manifest.Slots)
            slots[slot.SlotId] = slot;

        catalog = new CatalogState(manifestJson, slots, validation);
        foreach (var issue in validation.Issues)
        {
            RecordDiagnostic(
                new SanityRuntimeResourceDiagnostic(
                    "sanity.resource.catalog",
                    issue.Severity == SanityAssetIssueSeverity.Error
                        ? SanityResourceCapabilityStatus.InvalidMetadata
                        : SanityResourceCapabilityStatus.Available,
                    issue.Code,
                    issue.SlotId,
                    issue.Path,
                    slots.TryGetValue(issue.SlotId, out var issueSlot)
                        && issueSlot.RequiredForRelease,
                    slots.TryGetValue(issue.SlotId, out issueSlot)
                        && issueSlot.IsPlaceholder,
                    issue.Reason
                )
            );
        }
        return true;
    }

    private bool EnsureVisualMetadata(out SanityRuntimeResourceDiagnostic? diagnostic)
    {
        if (visualMetadata is not null && hostileAttackMetadata is not null)
        {
            diagnostic = null;
            return true;
        }
        if (visualMetadataFailure is not null)
        {
            diagnostic = visualMetadataFailure;
            return false;
        }
        if (catalog is null)
        {
            diagnostic = catalogFailure;
            return false;
        }

        visualMetadataParseCount++;
        var animationSlot = catalog.Slots.Values.FirstOrDefault(
            slot => slot.SlotId.StartsWith("sanity.animation.", StringComparison.OrdinalIgnoreCase)
        );
        if (animationSlot is null)
        {
            visualMetadataFailure = MetadataFailure(
                "resource.visual-metadata.manifest-path-missing",
                string.Empty,
                "sanity-assets.json does not declare an animation metadata path."
            );
            diagnostic = visualMetadataFailure;
            return false;
        }

        if (!TryReadStrictUtf8(animationSlot.Path, out var animationsJson, out var animationFailure))
        {
            visualMetadataFailure = animationFailure;
            diagnostic = visualMetadataFailure;
            return false;
        }
        if (!TryReadStrictUtf8(BindingsRelativePath, out var bindingsJson, out var bindingFailure))
        {
            visualMetadataFailure = bindingFailure;
            diagnostic = visualMetadataFailure;
            return false;
        }

        var contract = SanityHostileVisualContractValidator.Validate(
            animationsJson,
            bindingsJson
        );
        if (!contract.Success)
        {
            var issue = contract.Issues[0];
            visualMetadataFailure = MetadataFailure(issue.Code, animationSlot.Path, issue.Reason);
            diagnostic = visualMetadataFailure;
            return false;
        }
        if (
            !SanityVisualMetadataCatalog.TryParse(
                animationsJson,
                out var parsedVisualMetadata,
                out var reason
            )
        )
        {
            visualMetadataFailure = MetadataFailure(reason, animationSlot.Path, reason);
            diagnostic = visualMetadataFailure;
            return false;
        }
        if (
            !SanityHostileAttackMetadataCatalog.TryParse(
                animationsJson,
                bindingsJson,
                out var parsedAttackMetadata,
                out reason
            )
        )
        {
            visualMetadataFailure = MetadataFailure(reason, animationSlot.Path, reason);
            diagnostic = visualMetadataFailure;
            return false;
        }

        visualMetadata = parsedVisualMetadata;
        hostileAttackMetadata = parsedAttackMetadata;

        diagnostic = null;
        return true;
    }

    private bool EnsureAudioMetadata(out SanityRuntimeResourceDiagnostic? diagnostic)
    {
        if (audioMetadata is not null)
        {
            diagnostic = null;
            return true;
        }
        if (audioMetadataFailure is not null)
        {
            diagnostic = audioMetadataFailure;
            return false;
        }
        if (catalog is null)
        {
            diagnostic = catalogFailure;
            return false;
        }

        audioMetadataParseCount++;
        var cueSlot = catalog.Slots.Values.FirstOrDefault(
            slot => slot.SlotId.StartsWith("sanity.cue.", StringComparison.OrdinalIgnoreCase)
        );
        if (cueSlot is null)
        {
            audioMetadataFailure = MetadataFailure(
                "resource.audio-metadata.manifest-path-missing",
                string.Empty,
                "sanity-assets.json does not declare an audio metadata path."
            );
            diagnostic = audioMetadataFailure;
            return false;
        }
        if (!TryReadStrictUtf8(cueSlot.Path, out var audioJson, out var readFailure))
        {
            audioMetadataFailure = readFailure;
            diagnostic = audioMetadataFailure;
            return false;
        }

        var contract = SanityAudioContractValidator.Validate(
            audioJson,
            catalog.ManifestJson,
            deploymentRoot
        );
        if (!contract.Success)
        {
            var issue = contract.Issues[0];
            audioMetadataFailure = MetadataFailure(issue.Code, cueSlot.Path, issue.Reason);
            diagnostic = audioMetadataFailure;
            return false;
        }
        if (!SanityAudioMetadataCatalog.TryParse(audioJson, out audioMetadata, out var reason))
        {
            audioMetadataFailure = MetadataFailure(reason, cueSlot.Path, reason);
            diagnostic = audioMetadataFailure;
            return false;
        }

        diagnostic = null;
        return true;
    }

    private SanityRuntimeResourceDiagnostic GetSlotGate(SanityAssetSlot slot)
    {
        if (catalog is null)
            return catalogFailure!;

        var error = catalog.Validation.Issues.FirstOrDefault(
            issue => issue.Severity == SanityAssetIssueSeverity.Error
                && (
                    string.Equals(issue.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(issue.Path, slot.Path, StringComparison.Ordinal)
                )
        );
        if (error is not null)
        {
            return new SanityRuntimeResourceDiagnostic(
                "sanity.resource.slot",
                slot.RequiredForRelease
                    ? SanityResourceCapabilityStatus.UnavailableRequired
                    : SanityResourceCapabilityStatus.DisabledOptional,
                error.Code,
                slot.SlotId,
                slot.Path,
                slot.RequiredForRelease,
                slot.IsPlaceholder,
                error.Reason
            );
        }

        if (
            catalog.Validation.DisabledOptionalSlotIds.Contains(
                slot.SlotId,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            return new SanityRuntimeResourceDiagnostic(
                "sanity.resource.slot",
                SanityResourceCapabilityStatus.DisabledOptional,
                "asset.optional-missing",
                slot.SlotId,
                slot.Path,
                slot.RequiredForRelease,
                slot.IsPlaceholder,
                "The optional slot is missing and only that slot is disabled."
            );
        }

        return Available(
            "sanity.resource.slot",
            slot,
            slot.IsPlaceholder,
            slot.IsPlaceholder ? "resource.slot.placeholder-available" : "resource.slot.available",
            slot.IsPlaceholder
                ? "The slot is available for development and remains an explicit placeholder."
                : "The slot passed the Development manifest/path/credit gate."
        );
    }

    private bool TryBeginRequest(
        string requestId,
        out SanityRuntimeResourceDiagnostic? diagnostic
    )
    {
        if (disposed)
        {
            diagnostic = new SanityRuntimeResourceDiagnostic(
                "sanity.resource.loader",
                SanityResourceCapabilityStatus.LoaderDisposed,
                "resource.loader.disposed",
                requestId,
                string.Empty,
                required: false,
                isPlaceholder: false,
                "The loader was disposed and cannot create new runtime resources."
            );
            RecordDiagnostic(diagnostic);
            return false;
        }
        if (!enabled)
        {
            diagnostic = new SanityRuntimeResourceDiagnostic(
                "sanity.resource.loader",
                SanityResourceCapabilityStatus.LoaderDisabled,
                "resource.loader.disabled",
                requestId,
                string.Empty,
                required: false,
                isPlaceholder: false,
                "The Sanity system is Disabled; no runtime resource is created."
            );
            RecordDiagnostic(diagnostic);
            return false;
        }
        if (!EnsureCatalog())
        {
            diagnostic = catalogFailure;
            return false;
        }

        var globalError = catalog!.Validation.Issues.FirstOrDefault(
            issue => issue.Severity == SanityAssetIssueSeverity.Error
                && string.IsNullOrWhiteSpace(issue.SlotId)
        );
        if (globalError is not null)
        {
            diagnostic = MetadataFailure(globalError.Code, globalError.Path, globalError.Reason);
            return false;
        }

        diagnostic = null;
        return true;
    }

    private bool TryReadStrictUtf8(
        string relativePath,
        out string text,
        out SanityRuntimeResourceDiagnostic? diagnostic
    )
    {
        text = string.Empty;
        diagnostic = null;
        if (
            !SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                deploymentRoot,
                relativePath,
                out var absolutePath,
                out var pathReason
            )
        )
        {
            diagnostic = MetadataFailure(pathReason, relativePath, "Metadata path resolution failed closed.");
            return false;
        }
        if (!fileAccess.TryReadAllBytes(absolutePath, out var bytes, out var readReason))
        {
            diagnostic = MetadataFailure(readReason, relativePath, "Metadata could not be read from the deployed Mod root.");
            return false;
        }
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            diagnostic = MetadataFailure(
                "resource.metadata.utf8-bom-forbidden",
                relativePath,
                "Runtime metadata must be strict UTF-8 without BOM."
            );
            return false;
        }

        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            diagnostic = MetadataFailure(
                "resource.metadata.invalid-utf8",
                relativePath,
                "Runtime metadata is not strict UTF-8."
            );
            return false;
        }
    }

    private SanitySlotResourceResult Fail(
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
        var diagnostic = new SanityRuntimeResourceDiagnostic(
            capability,
            status,
            code,
            slotId,
            path,
            required,
            isPlaceholder,
            reason
        );
        RecordDiagnostic(diagnostic);
        return new SanitySlotResourceResult(diagnostic);
    }

    private SanityRuntimeResourceDiagnostic MetadataFailure(
        string code,
        string path,
        string reason
    )
    {
        var diagnostic = new SanityRuntimeResourceDiagnostic(
            "sanity.resource.metadata",
            SanityResourceCapabilityStatus.InvalidMetadata,
            code,
            string.Empty,
            path,
            required: true,
            isPlaceholder: false,
            reason
        );
        RecordDiagnostic(diagnostic);
        return diagnostic;
    }

    private static SanityRuntimeResourceDiagnostic Available(
        string capability,
        SanityAssetSlot slot,
        bool isPlaceholder,
        string code,
        string reason
    )
    {
        return new SanityRuntimeResourceDiagnostic(
            capability,
            SanityResourceCapabilityStatus.Available,
            code,
            slot.SlotId,
            slot.Path,
            slot.RequiredForRelease,
            isPlaceholder,
            reason
        );
    }

    private void RecordDiagnostic(SanityRuntimeResourceDiagnostic diagnostic)
    {
        if (
            diagnostic.Status == SanityResourceCapabilityStatus.DisabledOptional
            && !string.IsNullOrWhiteSpace(diagnostic.SlotId)
        )
        {
            runtimeDisabledOptionalSlotIds.Add(diagnostic.SlotId);
        }

        var key = string.Concat(
            generation.ToString(),
            "|",
            diagnostic.Code,
            "|",
            diagnostic.SlotId,
            "|",
            diagnostic.Path
        );
        if (
            runtimeDiagnostics.Count >= MaxRuntimeDiagnostics
            || !runtimeDiagnosticKeys.Add(key)
        )
        {
            return;
        }
        runtimeDiagnostics.Add(diagnostic);
    }

    private void ReleasePhysicalResources(SanityResourceReleaseReason reason)
    {
        if (physicalCache.Count == 0 && cueSets.Count == 0)
            return;

        physicalCache.Release(reason);
        cueSets.Clear();
        generation++;
    }

    private void ClearMetadataCaches()
    {
        catalog = null;
        catalogFailure = null;
        visualMetadata = null;
        hostileAttackMetadata = null;
        visualMetadataFailure = null;
        audioMetadata = null;
        audioMetadataFailure = null;
        runtimeDisabledOptionalSlotIds.Clear();
    }

    private SanityRuntimeResourceSnapshot CreateSnapshot()
    {
        var placeholders = catalog?.Slots.Values
            .Where(slot => slot.IsPlaceholder)
            .Select(slot => slot.SlotId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        var disabled = new HashSet<string>(runtimeDisabledOptionalSlotIds, StringComparer.OrdinalIgnoreCase);
        if (catalog is not null)
        {
            foreach (var slotId in catalog.Validation.DisabledOptionalSlotIds)
                disabled.Add(slotId);
        }
        return new SanityRuntimeResourceSnapshot(
            enabled,
            disposed,
            generation,
            cacheHits + physicalCache.Hits,
            cacheMisses + physicalCache.Misses,
            manifestParseCount,
            visualMetadataParseCount,
            audioMetadataParseCount,
            physicalCache.Count,
            cueSets.Count,
            placeholders,
            disabled.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            runtimeDiagnostics
        );
    }

    private sealed class CatalogState
    {
        internal CatalogState(
            string manifestJson,
            Dictionary<string, SanityAssetSlot> slots,
            SanityAssetValidationResult validation
        )
        {
            ManifestJson = manifestJson;
            Slots = slots;
            Validation = validation;
        }

        internal string ManifestJson { get; }

        internal Dictionary<string, SanityAssetSlot> Slots { get; }

        internal SanityAssetValidationResult Validation { get; }
    }

}
