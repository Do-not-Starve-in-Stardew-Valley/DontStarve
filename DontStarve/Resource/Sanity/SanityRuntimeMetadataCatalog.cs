#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DontStarve.Resource.Sanity;

internal sealed class SanityVisualMetadataCatalog
{
    private readonly Dictionary<string, VisualTemplate> templatesBySlot;

    private SanityVisualMetadataCatalog(Dictionary<string, VisualTemplate> templatesBySlot)
    {
        this.templatesBySlot = templatesBySlot;
    }

    internal static bool TryParse(string json, out SanityVisualMetadataCatalog? catalog, out string reason)
    {
        catalog = null;
        try
        {
            var root = JsonSerializer.Deserialize<AnimationRootDto>(json, SerializerOptions);
            if (root is null)
            {
                reason = "resource.visual-metadata.empty";
                return false;
            }

            var templates = new Dictionary<string, VisualTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in root.AnimationProfiles ?? Array.Empty<AnimationProfileDto>())
            {
                if (
                    string.IsNullOrWhiteSpace(profile.AnimationProfileId)
                    || string.IsNullOrWhiteSpace(profile.TextureSlotId)
                    || profile.FrameWidth <= 0
                    || profile.FrameHeight <= 0
                )
                {
                    reason = "resource.visual-metadata.invalid-profile";
                    return false;
                }

                var actorOrigin = ToPoint(profile.ActorOriginSourcePx) ?? new SanityResourcePoint(0, 0);
                var hurtBox = ToRectangle(profile.Collision?.HurtBoxSourcePx);
                var attackBox = ToRectangle(profile.Collision?.AttackBoxSourcePx);
                foreach (var state in profile.States ?? Array.Empty<AnimationStateDto>())
                {
                    if (
                        string.IsNullOrWhiteSpace(state.AnimationId)
                        || state.FrameCount <= 0
                        || state.Row < 0
                    )
                    {
                        reason = "resource.visual-metadata.invalid-state";
                        return false;
                    }

                    var template = new VisualTemplate(
                        profile.TextureSlotId,
                        SanityVisualPreviewKind.AnimationFrame,
                        profile.FrameWidth,
                        profile.FrameHeight,
                        state.Row,
                        state.FrameCount,
                        ToPoint(state.PivotSourcePx),
                        actorOrigin,
                        hurtBox,
                        attackBox,
                        null,
                        state.DrawScale,
                        profile.OwnerLocalOnly,
                        state.IsProvisional,
                        profile.IsPlaceholder
                    );
                    if (!templates.TryAdd(state.AnimationId, template))
                    {
                        reason = "resource.visual-metadata.duplicate-state";
                        return false;
                    }

                    // A texture slot may back several states. The first metadata order is the stable
                    // default for a direct texture-slot preview; state-slot preview remains exact.
                    templates.TryAdd(profile.TextureSlotId, template);
                }
            }

            foreach (var profile in root.OverlayProfiles ?? Array.Empty<OverlayProfileDto>())
            {
                if (
                    string.IsNullOrWhiteSpace(profile.TextureSlotId)
                    || profile.Width <= 0
                    || profile.Height <= 0
                )
                {
                    reason = "resource.visual-metadata.invalid-overlay";
                    return false;
                }

                SanityResourceRectangle? slice = profile.SliceSourcePx is null
                    ? null
                    : new SanityResourceRectangle(
                        profile.SliceSourcePx.Left,
                        profile.SliceSourcePx.Top,
                        profile.SliceSourcePx.Right,
                        profile.SliceSourcePx.Bottom
                    );
                if (
                    !templates.TryAdd(
                        profile.TextureSlotId,
                        new VisualTemplate(
                            profile.TextureSlotId,
                            SanityVisualPreviewKind.NineSliceOverlay,
                            profile.Width,
                            profile.Height,
                            0,
                            1,
                            null,
                            new SanityResourcePoint(0, 0),
                            null,
                            null,
                            slice,
                            1d,
                            profile.OwnerLocalOnly,
                            profile.IsProvisional,
                            profile.IsPlaceholder
                        )
                    )
                )
                {
                    reason = "resource.visual-metadata.duplicate-texture-slot";
                    return false;
                }
            }

            foreach (var profile in root.StaticSpriteProfiles ?? Array.Empty<StaticProfileDto>())
            {
                if (
                    string.IsNullOrWhiteSpace(profile.TextureSlotId)
                    || profile.Width <= 0
                    || profile.Height <= 0
                )
                {
                    reason = "resource.visual-metadata.invalid-static-sprite";
                    return false;
                }

                if (
                    !templates.TryAdd(
                        profile.TextureSlotId,
                        new VisualTemplate(
                            profile.TextureSlotId,
                            SanityVisualPreviewKind.StaticSprite,
                            profile.Width,
                            profile.Height,
                            0,
                            1,
                            ToPoint(profile.PivotSourcePx),
                            new SanityResourcePoint(0, 0),
                            null,
                            null,
                            null,
                            profile.DrawScale,
                            profile.OwnerLocalOnly,
                            profile.IsProvisional,
                            profile.IsPlaceholder
                        )
                    )
                )
                {
                    reason = "resource.visual-metadata.duplicate-texture-slot";
                    return false;
                }
            }

            catalog = new SanityVisualMetadataCatalog(templates);
            reason = "resource.visual-metadata.available";
            return true;
        }
        catch (JsonException)
        {
            reason = "resource.visual-metadata.invalid-json";
            return false;
        }
        catch (InvalidOperationException)
        {
            reason = "resource.visual-metadata.invalid-shape";
            return false;
        }
        catch (ArgumentException)
        {
            reason = "resource.visual-metadata.invalid-shape";
            return false;
        }
    }

    internal bool TryCreatePreview(
        string requestedSlotId,
        int frameIndex,
        out SanityVisualPreviewDefinition? preview,
        out string reason
    )
    {
        preview = null;
        if (!templatesBySlot.TryGetValue(requestedSlotId, out var template))
        {
            reason = "resource.preview.visual-slot-not-described";
            return false;
        }

        if (frameIndex < 0 || frameIndex >= template.FrameCount)
        {
            reason = "resource.preview.frame-out-of-range";
            return false;
        }

        preview = template.Create(requestedSlotId, frameIndex);
        reason = "resource.preview.visual-available";
        return true;
    }

    private static SanityResourcePoint? ToPoint(PointDto? point)
    {
        return point is null ? null : new SanityResourcePoint(point.X, point.Y);
    }

    private static SanityResourceRectangle? ToRectangle(RectangleDto? rectangle)
    {
        return rectangle is null
            ? null
            : new SanityResourceRectangle(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height
            );
    }

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };

    private sealed class VisualTemplate
    {
        internal VisualTemplate(
            string textureSlotId,
            SanityVisualPreviewKind kind,
            int width,
            int height,
            int row,
            int frameCount,
            SanityResourcePoint? pivot,
            SanityResourcePoint actorOrigin,
            SanityResourceRectangle? hurtBox,
            SanityResourceRectangle? attackBox,
            SanityResourceRectangle? slice,
            double drawScale,
            bool ownerLocalOnly,
            bool isProvisional,
            bool isPlaceholder
        )
        {
            TextureSlotId = textureSlotId;
            Kind = kind;
            Width = width;
            Height = height;
            Row = row;
            FrameCount = frameCount;
            Pivot = pivot;
            ActorOrigin = actorOrigin;
            HurtBox = hurtBox;
            AttackBox = attackBox;
            Slice = slice;
            DrawScale = drawScale;
            OwnerLocalOnly = ownerLocalOnly;
            IsProvisional = isProvisional;
            IsPlaceholder = isPlaceholder;
        }

        internal string TextureSlotId { get; }

        internal SanityVisualPreviewKind Kind { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal int Row { get; }

        internal int FrameCount { get; }

        internal SanityResourcePoint? Pivot { get; }

        internal SanityResourcePoint ActorOrigin { get; }

        internal SanityResourceRectangle? HurtBox { get; }

        internal SanityResourceRectangle? AttackBox { get; }

        internal SanityResourceRectangle? Slice { get; }

        internal double DrawScale { get; }

        internal bool OwnerLocalOnly { get; }

        internal bool IsProvisional { get; }

        internal bool IsPlaceholder { get; }

        internal SanityVisualPreviewDefinition Create(string requestedSlotId, int frameIndex)
        {
            return new SanityVisualPreviewDefinition(
                requestedSlotId,
                TextureSlotId,
                Kind,
                new SanityResourceRectangle(frameIndex * Width, Row * Height, Width, Height),
                Pivot,
                ActorOrigin,
                HurtBox,
                AttackBox,
                Slice,
                frameIndex,
                FrameCount,
                DrawScale,
                OwnerLocalOnly,
                IsProvisional,
                IsPlaceholder
            );
        }
    }

    private sealed class AnimationRootDto
    {
        public AnimationProfileDto[]? AnimationProfiles { get; set; }

        public OverlayProfileDto[]? OverlayProfiles { get; set; }

        public StaticProfileDto[]? StaticSpriteProfiles { get; set; }
    }

    private sealed class AnimationProfileDto
    {
        public string AnimationProfileId { get; set; } = string.Empty;

        public string TextureSlotId { get; set; } = string.Empty;

        public int FrameWidth { get; set; }

        public int FrameHeight { get; set; }

        public bool OwnerLocalOnly { get; set; }

        public bool IsPlaceholder { get; set; }

        public PointDto? ActorOriginSourcePx { get; set; }

        public CollisionDto? Collision { get; set; }

        public AnimationStateDto[]? States { get; set; }
    }

    private sealed class AnimationStateDto
    {
        public string AnimationId { get; set; } = string.Empty;

        public int Row { get; set; }

        public int FrameCount { get; set; }

        public PointDto? PivotSourcePx { get; set; }

        public double DrawScale { get; set; }

        public bool IsProvisional { get; set; }
    }

    private sealed class OverlayProfileDto
    {
        public string TextureSlotId { get; set; } = string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }

        public SliceDto? SliceSourcePx { get; set; }

        public bool OwnerLocalOnly { get; set; }

        public bool IsPlaceholder { get; set; }

        public bool IsProvisional { get; set; }
    }

    private sealed class StaticProfileDto
    {
        public string TextureSlotId { get; set; } = string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }

        public PointDto? PivotSourcePx { get; set; }

        public double DrawScale { get; set; }

        public bool OwnerLocalOnly { get; set; }

        public bool IsPlaceholder { get; set; }

        public bool IsProvisional { get; set; }
    }

    private sealed class CollisionDto
    {
        public RectangleDto? HurtBoxSourcePx { get; set; }

        public RectangleDto? AttackBoxSourcePx { get; set; }
    }

    private sealed class PointDto
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    private sealed class RectangleDto
    {
        public int X { get; set; }

        public int Y { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }

    private sealed class SliceDto
    {
        public int Left { get; set; }

        public int Top { get; set; }

        public int Right { get; set; }

        public int Bottom { get; set; }
    }
}

internal sealed class SanityAudioMetadataCatalog
{
    private readonly Dictionary<string, SanityCueSetDefinition> sets;
    private readonly Dictionary<string, CueLookup> cues;

    private SanityAudioMetadataCatalog(
        Dictionary<string, SanityCueSetDefinition> sets,
        Dictionary<string, CueLookup> cues
    )
    {
        this.sets = sets;
        this.cues = cues;
    }

    internal static bool TryParse(string json, out SanityAudioMetadataCatalog? catalog, out string reason)
    {
        catalog = null;
        try
        {
            var root = JsonSerializer.Deserialize<AudioRootDto>(json, SerializerOptions);
            if (root?.CueSets is null)
            {
                reason = "resource.audio-metadata.cue-sets-missing";
                return false;
            }

            var sets = new Dictionary<string, SanityCueSetDefinition>(StringComparer.OrdinalIgnoreCase);
            var cues = new Dictionary<string, CueLookup>(StringComparer.OrdinalIgnoreCase);
            foreach (var setDto in root.CueSets)
            {
                if (string.IsNullOrWhiteSpace(setDto.CueSetId) || setDto.Cues is null)
                {
                    reason = "resource.audio-metadata.invalid-cue-set";
                    return false;
                }

                var cueDefinitions = new List<SanityCueDefinition>();
                foreach (var cueDto in setDto.Cues)
                {
                    if (string.IsNullOrWhiteSpace(cueDto.CueId) || cueDto.Clips is null)
                    {
                        reason = "resource.audio-metadata.invalid-cue";
                        return false;
                    }

                    var clips = new List<SanityCueClipDefinition>();
                    foreach (var clip in cueDto.Clips)
                    {
                        if (
                            string.IsNullOrWhiteSpace(clip.ClipId)
                            || string.IsNullOrWhiteSpace(clip.Path)
                            || string.IsNullOrWhiteSpace(clip.FormatId)
                            || clip.DurationFrames <= 0
                            || !double.IsFinite(clip.DurationSeconds)
                            || clip.DurationSeconds <= 0d
                        )
                        {
                            reason = "resource.audio-metadata.invalid-clip";
                            return false;
                        }

                        clips.Add(
                            new SanityCueClipDefinition(
                                clip.ClipId,
                                clip.Path,
                                clip.Sha256,
                                clip.FormatId,
                                clip.IsPlaceholder,
                                clip.DurationFrames,
                                clip.DurationSeconds
                            )
                        );
                    }

                    var cue = new SanityCueDefinition(
                        cueDto.CueId,
                        cueDto.PlaybackMode,
                        cueDto.Enabled,
                        cueDto.RequiredForRelease,
                        cueDto.IsPlaceholder,
                        cueDto.ListeningStatus,
                        clips
                    );
                    cueDefinitions.Add(cue);
                    if (!cues.TryAdd(cue.CueId, new CueLookup(setDto.CueSetId, cue)))
                    {
                        reason = "resource.audio-metadata.duplicate-cue";
                        return false;
                    }
                }

                var set = new SanityCueSetDefinition(
                    setDto.CueSetId,
                    setDto.Group,
                    setDto.CachePolicy,
                    setDto.LifecyclePolicy,
                    setDto.MaxConcurrentInstances,
                    cueDefinitions
                );
                if (!sets.TryAdd(set.CueSetId, set))
                {
                    reason = "resource.audio-metadata.duplicate-cue-set";
                    return false;
                }
            }

            catalog = new SanityAudioMetadataCatalog(sets, cues);
            reason = "resource.audio-metadata.available";
            return true;
        }
        catch (JsonException)
        {
            reason = "resource.audio-metadata.invalid-json";
            return false;
        }
        catch (InvalidOperationException)
        {
            reason = "resource.audio-metadata.invalid-shape";
            return false;
        }
        catch (ArgumentException)
        {
            reason = "resource.audio-metadata.invalid-shape";
            return false;
        }
    }

    internal bool TryGetCue(
        string cueId,
        out string cueSetId,
        out SanityCueDefinition? cue
    )
    {
        if (cues.TryGetValue(cueId, out var lookup))
        {
            cueSetId = lookup.CueSetId;
            cue = lookup.Cue;
            return true;
        }

        cueSetId = string.Empty;
        cue = null;
        return false;
    }

    internal bool TryGetCueSet(string cueSetId, out SanityCueSetDefinition? cueSet)
    {
        return sets.TryGetValue(cueSetId, out cueSet);
    }

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };

    private sealed record CueLookup(string CueSetId, SanityCueDefinition Cue);

    private sealed class AudioRootDto
    {
        public CueSetDto[]? CueSets { get; set; }
    }

    private sealed class CueSetDto
    {
        public string CueSetId { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string CachePolicy { get; set; } = string.Empty;

        public string LifecyclePolicy { get; set; } = string.Empty;

        public int MaxConcurrentInstances { get; set; }

        public CueDto[]? Cues { get; set; }
    }

    private sealed class CueDto
    {
        public string CueId { get; set; } = string.Empty;

        public string PlaybackMode { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public bool RequiredForRelease { get; set; }

        public bool IsPlaceholder { get; set; }

        public string ListeningStatus { get; set; } = string.Empty;

        public ClipDto[]? Clips { get; set; }
    }

    private sealed class ClipDto
    {
        public string ClipId { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public string FormatId { get; set; } = string.Empty;

        public bool IsPlaceholder { get; set; }

        public long DurationFrames { get; set; }

        public double DurationSeconds { get; set; }
    }
}
