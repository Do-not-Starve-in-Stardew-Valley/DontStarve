#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Resource.Sanity;

internal sealed class SanityResourcePreviewSelection
{
    internal SanityResourcePreviewSelection(
        string ownerKey,
        string slotId,
        int frameIndex,
        long loaderGeneration,
        SanitySlotResourceResult result
    )
    {
        OwnerKey = ownerKey;
        SlotId = slotId;
        FrameIndex = frameIndex;
        LoaderGeneration = loaderGeneration;
        Result = result;
    }

    internal string OwnerKey { get; }

    internal string SlotId { get; }

    internal int FrameIndex { get; }

    internal long LoaderGeneration { get; }

    internal SanitySlotResourceResult Result { get; }
}

/// <summary>
/// Selection is owner-local even though the loader's immutable physical resources are shared.
/// It never mutates Sanity, tiers, entities, playback state, or save data.
/// </summary>
internal sealed class SanityResourcePreviewController
{
    private readonly SanityRuntimeResourceLoader loader;
    private readonly Dictionary<string, SanityResourcePreviewSelection> selections =
        new(StringComparer.Ordinal);

    internal SanityResourcePreviewController(SanityRuntimeResourceLoader loader)
    {
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    internal SanityResourcePreviewSelection Select(
        string ownerKey,
        string slotId,
        int frameIndex = 0
    )
    {
        if (string.IsNullOrWhiteSpace(ownerKey))
            throw new ArgumentException("A preview owner key is required.", nameof(ownerKey));
        if (string.IsNullOrWhiteSpace(slotId))
            throw new ArgumentException("A preview SlotId is required.", nameof(slotId));

        var result = loader.LoadSlot(slotId, frameIndex);
        var selection = new SanityResourcePreviewSelection(
            ownerKey,
            slotId,
            frameIndex,
            loader.Snapshot().Generation,
            result
        );
        // Failure/Disabled results are retained too so the same structured reason can be previewed.
        selections[ownerKey] = selection;
        return selection;
    }

    internal bool TryGet(
        string ownerKey,
        out SanityResourcePreviewSelection? selection
    )
    {
        if (!selections.TryGetValue(ownerKey, out selection))
            return false;

        if (selection.LoaderGeneration != loader.Snapshot().Generation)
        {
            selections.Remove(ownerKey);
            selection = null;
            return false;
        }
        return true;
    }

    internal bool ClearOwner(string ownerKey)
    {
        return selections.Remove(ownerKey);
    }

    internal void ClearAll()
    {
        selections.Clear();
    }
}
