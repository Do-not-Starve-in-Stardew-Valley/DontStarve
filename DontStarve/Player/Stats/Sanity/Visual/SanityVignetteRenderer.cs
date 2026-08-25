#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Loads the supplied Spriter export once and draws its basic/insane timelines as a fixed-screen
/// HUD overlay. Texture ownership remains with SMAPI ModContent, so this class only owns parsed
/// timeline metadata.
/// </summary>
internal sealed class SanityVignetteRenderer
{
    internal const float SourceCanvasWidth = 1366f;
    internal const float SourceCanvasHeight = 768f;
    internal const float OverlayScale = 1.075f;

    private const string AssetRoot = "Asset/Sanity/Overlays/Vignette";
    private readonly IModHelper helper;
    private readonly Dictionary<(int Folder, int File), SpriteAsset> assets = new();
    private readonly Dictionary<SanityVignetteMode, AnimationData> animations = new();
    private bool isLoaded;

    internal SanityVignetteRenderer(IModHelper helper)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
    }

    internal bool IsLoaded => isLoaded;

    internal void Load()
    {
        if (isLoaded)
            return;

        assets.Clear();
        animations.Clear();
        try
        {
            var path = Path.Combine(
                helper.DirectoryPath,
                "Asset",
                "Sanity",
                "Overlays",
                "Vignette",
                "vig.scml"
            );
            var document = XDocument.Load(path);
            var root = document.Root
                ?? throw new InvalidOperationException("Vignette SCML has no root element.");

            LoadAssets(root);
            animations.Add(
                SanityVignetteMode.Basic,
                LoadAnimation(root, "basic")
            );
            animations.Add(
                SanityVignetteMode.Insane,
                LoadAnimation(root, "insane")
            );
            isLoaded = true;
        }
        catch
        {
            assets.Clear();
            animations.Clear();
            throw;
        }
    }

    internal void Draw(
        SpriteBatch spriteBatch,
        Rectangle viewport,
        SanityVignetteMode mode,
        long totalGameMilliseconds
    )
    {
        if (
            !isLoaded
            || mode == SanityVignetteMode.Hidden
            || viewport.Width <= 0
            || viewport.Height <= 0
            || !animations.TryGetValue(mode, out var animation)
        )
        {
            return;
        }

        var animationTime = (int)(
            Math.Max(0L, totalGameMilliseconds) % animation.DurationMilliseconds
        );
        var mainlineKey = animation.GetMainlineKey(animationTime);
        var screenScale = Math.Max(
            viewport.Width / SourceCanvasWidth,
            viewport.Height / SourceCanvasHeight
        ) * OverlayScale;
        var screenCenter = new Vector2(
            viewport.X + (viewport.Width / 2f),
            viewport.Y + (viewport.Height / 2f)
        );

        for (var index = 0; index < mainlineKey.References.Length; index++)
        {
            var reference = mainlineKey.References[index];
            if (
                !animation.TryGetFrame(reference, out var frame)
                || frame is null
                || !assets.TryGetValue((frame.FolderId, frame.FileId), out var asset)
                || frame.Alpha <= 0f
            )
            {
                continue;
            }

            var position = screenCenter
                + new Vector2(frame.Position.X, -frame.Position.Y) * screenScale;
            var textureSize = new Vector2(asset.Texture.Width, asset.Texture.Height);
            var origin = asset.Pivot * textureSize;
            var scale = frame.Scale * screenScale;
            var effects = SpriteEffects.None;

            if (scale.X < 0f)
            {
                scale.X = -scale.X;
                origin.X = textureSize.X - origin.X;
                effects |= SpriteEffects.FlipHorizontally;
            }
            if (scale.Y < 0f)
            {
                scale.Y = -scale.Y;
                origin.Y = textureSize.Y - origin.Y;
                effects |= SpriteEffects.FlipVertically;
            }

            spriteBatch.Draw(
                asset.Texture,
                position,
                sourceRectangle: null,
                color: Color.White * MathHelper.Clamp(frame.Alpha, 0f, 1f),
                rotation: MathHelper.ToRadians(-frame.Angle),
                origin: origin,
                scale: scale,
                effects: effects,
                layerDepth: 0f
            );
        }
    }

    private void LoadAssets(XElement root)
    {
        foreach (var folder in root.Elements("folder"))
        {
            var folderId = GetInt(folder, "id");
            foreach (var file in folder.Elements("file"))
            {
                var fileId = GetInt(file, "id");
                var relativePath = GetRequiredString(file, "name");
                var contentPath = ResolveContentPath(relativePath);
                var texture = helper.ModContent.Load<Texture2D>(contentPath);
                var pivot = new Vector2(
                    GetFloat(file, "pivot_x", 0f),
                    1f - GetFloat(file, "pivot_y", 1f)
                );
                if (!assets.TryAdd((folderId, fileId), new SpriteAsset(texture, pivot)))
                {
                    throw new InvalidOperationException(
                        $"Vignette SCML repeats sprite ({folderId}, {fileId})."
                    );
                }
            }
        }

        if (assets.Count == 0)
            throw new InvalidOperationException("Vignette SCML contains no sprite assets.");
    }

    private AnimationData LoadAnimation(XElement root, string name)
    {
        XElement? animation = null;
        foreach (var candidate in root.Descendants("animation"))
        {
            if (string.Equals(candidate.Attribute("name")?.Value, name, StringComparison.Ordinal))
            {
                animation = candidate;
                break;
            }
        }
        if (animation is null)
            throw new InvalidOperationException($"Vignette SCML has no '{name}' animation.");

        var durationMilliseconds = GetInt(animation, "length");
        if (durationMilliseconds <= 0)
        {
            throw new InvalidOperationException(
                $"Vignette animation '{name}' has an invalid duration."
            );
        }

        var timelines = new Dictionary<int, Timeline>();
        foreach (var timeline in animation.Elements("timeline"))
        {
            var timelineId = GetInt(timeline, "id");
            var frames = new Dictionary<int, ObjectFrame>();
            foreach (var key in timeline.Elements("key"))
            {
                var sprite = key.Element("object");
                if (sprite is null)
                    continue;

                var folderId = GetInt(sprite, "folder");
                var fileId = GetInt(sprite, "file");
                if (!assets.ContainsKey((folderId, fileId)))
                    continue;

                var keyId = GetInt(key, "id");
                if (
                    !frames.TryAdd(
                        keyId,
                        new ObjectFrame(
                            folderId,
                            fileId,
                            new Vector2(
                                GetFloat(sprite, "x", 0f),
                                GetFloat(sprite, "y", 0f)
                            ),
                            new Vector2(
                                GetFloat(sprite, "scale_x", 1f),
                                GetFloat(sprite, "scale_y", 1f)
                            ),
                            GetFloat(sprite, "angle", 0f),
                            GetFloat(sprite, "a", 1f)
                        )
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Vignette animation '{name}' repeats timeline key {timelineId}/{keyId}."
                    );
                }
            }

            if (frames.Count > 0 && !timelines.TryAdd(timelineId, new Timeline(frames)))
            {
                throw new InvalidOperationException(
                    $"Vignette animation '{name}' repeats timeline {timelineId}."
                );
            }
        }

        var mainline = animation.Element("mainline")
            ?? throw new InvalidOperationException(
                $"Vignette animation '{name}' has no mainline."
            );
        var mainlineKeys = new List<MainlineKey>();
        foreach (var key in mainline.Elements("key"))
        {
            var references = new List<ObjectReference>();
            foreach (var reference in key.Elements("object_ref"))
            {
                references.Add(
                    new ObjectReference(
                        GetInt(reference, "timeline"),
                        GetInt(reference, "key"),
                        GetInt(reference, "z_index", 0)
                    )
                );
            }
            references.Sort(
                static (left, right) => left.ZIndex.CompareTo(right.ZIndex)
            );
            mainlineKeys.Add(
                new MainlineKey(
                    GetInt(key, "time", 0),
                    references.ToArray()
                )
            );
        }
        mainlineKeys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        if (mainlineKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Vignette animation '{name}' contains no mainline keys."
            );
        }

        return new AnimationData(
            durationMilliseconds,
            timelines,
            mainlineKeys.ToArray()
        );
    }

    private static string ResolveContentPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Length == 0 || normalized[0] == '/')
            throw new InvalidOperationException("Vignette SCML contains an invalid asset path.");

        var segments = normalized.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            if (
                segments[index].Length == 0
                || string.Equals(segments[index], ".", StringComparison.Ordinal)
                || string.Equals(segments[index], "..", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    "Vignette SCML contains a path outside its asset directory."
                );
            }
        }

        return string.Concat(AssetRoot, "/", normalized);
    }

    private static int GetInt(XElement element, string name, int defaultValue = 0)
    {
        var value = element.Attribute(name)?.Value;
        return value is not null
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static float GetFloat(XElement element, string name, float defaultValue)
    {
        var value = element.Attribute(name)?.Value;
        return value is not null
            && float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
            ? parsed
            : defaultValue;
    }

    private static string GetRequiredString(XElement element, string name)
    {
        return element.Attribute(name)?.Value
            ?? throw new InvalidOperationException(
                $"Vignette SCML is missing required attribute '{name}'."
            );
    }

    private sealed record SpriteAsset(Texture2D Texture, Vector2 Pivot);

    private sealed record ObjectFrame(
        int FolderId,
        int FileId,
        Vector2 Position,
        Vector2 Scale,
        float Angle,
        float Alpha
    );

    private sealed record ObjectReference(int TimelineId, int KeyId, int ZIndex);

    private sealed record MainlineKey(int Time, ObjectReference[] References);

    private sealed class AnimationData
    {
        private readonly Dictionary<int, Timeline> timelines;
        private readonly MainlineKey[] mainlineKeys;

        internal AnimationData(
            int durationMilliseconds,
            Dictionary<int, Timeline> timelines,
            MainlineKey[] mainlineKeys
        )
        {
            DurationMilliseconds = durationMilliseconds;
            this.timelines = timelines;
            this.mainlineKeys = mainlineKeys;
        }

        internal int DurationMilliseconds { get; }

        internal MainlineKey GetMainlineKey(int animationTime)
        {
            var result = mainlineKeys[0];
            for (var index = 1; index < mainlineKeys.Length; index++)
            {
                if (mainlineKeys[index].Time > animationTime)
                    break;
                result = mainlineKeys[index];
            }
            return result;
        }

        internal bool TryGetFrame(
            ObjectReference reference,
            out ObjectFrame? frame
        )
        {
            frame = null;
            return timelines.TryGetValue(reference.TimelineId, out var timeline)
                && timeline.TryGetFrame(reference.KeyId, out frame);
        }
    }

    private sealed class Timeline
    {
        private readonly Dictionary<int, ObjectFrame> framesByKeyId;

        internal Timeline(Dictionary<int, ObjectFrame> framesByKeyId)
        {
            this.framesByKeyId = framesByKeyId;
        }

        internal bool TryGetFrame(int keyId, out ObjectFrame? frame)
        {
            return framesByKeyId.TryGetValue(keyId, out frame);
        }
    }
}
