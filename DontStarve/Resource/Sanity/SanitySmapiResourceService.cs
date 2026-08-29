#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Resource.Sanity;

/// <summary>
/// Thin SMAPI/XNA adapter for the pure loader. The console preview is diagnostic-only:
/// it never advances Sanity/gameplay state and intentionally does not play stage-05 cues.
/// </summary>
internal sealed class SanitySmapiResourceService
    : IHarmlessProjectionResourceProvider,
        IDisposable
{
    private const string PreviewCommand = "ds_sanity_preview";

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly int owningThreadId;
    private readonly SanityResourceDisposalGate disposalGate;
    private readonly SanityRuntimeResourceLoader loader;
    private readonly SanityResourcePreviewController previews;
    private readonly HashSet<string> loggedRenderFailures = new(StringComparer.Ordinal);
    private long contentRevision;
    private bool disposed;

    internal event Action? VisualResourcesInvalidating;

    internal event Action<SanityResourceReleaseReason>? WorldResourcesReleasing;

    internal SanitySmapiResourceService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        bool enabled
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.modId = string.IsNullOrWhiteSpace(modId)
            ? throw new ArgumentException("A mod ID is required.", nameof(modId))
            : modId;
        owningThreadId = Environment.CurrentManagedThreadId;
        disposalGate = new SanityResourceDisposalGate(owningThreadId);

        loader = new SanityRuntimeResourceLoader(
            helper.DirectoryPath,
            new XnaSanityPhysicalResourceFactory(owningThreadId)
        );
        loader.DiagnosticRecorded += OnLoaderDiagnosticRecorded;
        previews = new SanityResourcePreviewController(loader);

        var snapshot = loader.Prime();
        loader.SetEnabled(enabled);
        LogPrimeSnapshot(snapshot);

        helper.ConsoleCommands.Add(
            PreviewCommand,
            "Sanity resource preview: ds_sanity_preview <SlotId> [frame], ds_sanity_preview clear, or ds_sanity_preview status. Cues are loaded but not played.",
            OnPreviewCommand
        );
        helper.Events.Display.RenderingHud += OnRenderingHud;
        helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        // 当前 SMAPI API 没有 GameExiting 事件。ProcessExit 只记录进程回收分流，
        // 不在 SMAPI monitor 已关闭、线程也不确定的阶段触碰 helper/XNA 资源。
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal void SetEnabled(bool enabled)
    {
        if (disposed)
            return;

        if (!enabled)
        {
            WorldResourcesReleasing?.Invoke(
                SanityResourceReleaseReason.SystemDisabled
            );
            previews.ClearAll();
        }
        loader.SetEnabled(enabled);
    }

    /// <summary>
    /// Projection consumers borrow the existing loader-owned resource. This facade deliberately
    /// performs no manifest parsing, cache ownership transfer, or caller-side disposal.
    /// </summary>
    SanitySlotResourceResult IHarmlessProjectionResourceProvider.LoadVisualSlot(
        string slotId,
        int frameIndex
    )
    {
        return LoadVisualSlot(slotId, frameIndex);
    }

    /// <summary>
    /// Owner-local visual consumers borrow the same loader-owned texture seam as projections.
    /// Callers must drop references on WorldResourcesReleasing and must never dispose textures.
    /// </summary>
    internal SanitySlotResourceResult LoadVisualSlot(
        string slotId,
        int frameIndex
    )
    {
        return loader.LoadSlot(slotId, frameIndex);
    }

    /// <summary>
    /// Gameplay audio borrows the existing loader-owned cue set. The caller may create and own
    /// instances from its XNA effects, but it must never dispose the returned physical resources.
    /// </summary>
    internal SanitySlotResourceResult LoadAudioCueSet(string cueSetId)
    {
        return loader.LoadCueSet(cueSetId);
    }

    /// <summary>Returns validated timing only; it does not create or transfer audio ownership.</summary>
    internal SanitySlotResourceResult GetAudioCueMetadata(string cueId)
    {
        return loader.GetCueMetadata(cueId);
    }

    internal bool TryGetHostileAttackMetadata(
        string assetBindingId,
        out SanityHostileAttackMetadataDefinition? definition,
        out string reason
    )
    {
        return loader.TryGetHostileAttackMetadata(
            assetBindingId,
            out definition,
            out reason
        );
    }

    public void Dispose()
    {
        var request = disposalGate.TryBegin(
            SanityResourceDisposeOrigin.Explicit,
            Environment.CurrentManagedThreadId
        );
        if (
            request.Decision
            == SanityResourceDisposeDecision.WrongThreadRejected
        )
        {
            if (request.ShouldWriteDiagnostic)
            {
                monitor.Log(
                    "Sanity runtime resource dispose was rejected outside the captured SMAPI/XNA owning thread (code=resource.dispose.wrong-thread). Process teardown will reclaim the remaining native resources.",
                    LogLevel.Error
                );
            }
            return;
        }
        if (
            request.Decision
            != SanityResourceDisposeDecision.BeginOwnedThreadDispose
        )
        {
            return;
        }

        try
        {
            WorldResourcesReleasing?.Invoke(SanityResourceReleaseReason.Dispose);
            disposed = true;
            previews.ClearAll();
            helper.Events.Display.RenderingHud -= OnRenderingHud;
            helper.Events.Content.AssetsInvalidated -= OnAssetsInvalidated;
            helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            loader.DiagnosticRecorded -= OnLoaderDiagnosticRecorded;
            loader.Dispose();
            disposalGate.Complete();
        }
        catch
        {
            disposed = false;
            disposalGate.Abort();
            throw;
        }
    }

    private void OnPreviewCommand(string command, string[] arguments)
    {
        if (disposed)
            return;

        if (arguments.Length == 0)
        {
            monitor.Log(
                $"{PreviewCommand}: expected <SlotId> [frame], clear, or status.",
                LogLevel.Info
            );
            return;
        }

        if (string.Equals(arguments[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            LogSnapshot(loader.Snapshot());
            return;
        }

        if (!TryGetOwnerKey(out var ownerKey))
        {
            monitor.Log(
                "Sanity resource preview is unavailable (capability=sanity.resource.preview, status=InvalidMetadata, code=resource.preview.world-not-ready, reason=a loaded local player is required).",
                LogLevel.Warn
            );
            return;
        }

        if (string.Equals(arguments[0], "clear", StringComparison.OrdinalIgnoreCase))
        {
            previews.ClearOwner(ownerKey);
            monitor.Log($"Sanity resource preview cleared (owner={ownerKey}).", LogLevel.Debug);
            return;
        }

        var frameIndex = 0;
        if (
            arguments.Length > 1
            && !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out frameIndex
            )
        )
        {
            monitor.Log(
                "Sanity resource preview rejected frame index (code=resource.preview.frame-invalid).",
                LogLevel.Warn
            );
            return;
        }

        var selection = previews.Select(ownerKey, arguments[0], frameIndex);
        LogDiagnostic(selection.Result.Diagnostic);
    }

    private void OnRenderingHud(object? sender, RenderingHudEventArgs e)
    {
        if (disposed || !TryGetOwnerKey(out var ownerKey))
            return;
        if (!previews.TryGet(ownerKey, out var selection) || selection is null)
            return;

        DrawPreviewPanel(e.SpriteBatch, selection);
    }

    private void DrawPreviewPanel(
        SpriteBatch spriteBatch,
        SanityResourcePreviewSelection selection
    )
    {
        const int panelX = 24;
        const int panelY = 24;
        const int panelWidth = 560;
        const int panelHeight = 292;
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(panelX, panelY, panelWidth, panelHeight),
            Color.Black * 0.82f
        );

        var diagnostic = selection.Result.Diagnostic;
        var textX = panelX + 224;
        var textY = panelY + 16;
        DrawLine(spriteBatch, $"Sanity resource preview", textX, ref textY, Color.White);
        DrawLine(spriteBatch, $"slot: {selection.SlotId}", textX, ref textY, Color.White);
        DrawLine(
            spriteBatch,
            $"status: {diagnostic.Status} / {diagnostic.Code}",
            textX,
            ref textY,
            diagnostic.IsAvailable ? Color.LightGreen : Color.OrangeRed
        );
        DrawLine(
            spriteBatch,
            $"placeholder: {diagnostic.IsPlaceholder}",
            textX,
            ref textY,
            diagnostic.IsPlaceholder ? Color.Magenta : Color.LightGray
        );

        if (selection.Result.Cue is not null && selection.Result.CueSet is not null)
        {
            DrawLine(
                spriteBatch,
                $"cue-set: {selection.Result.CueSet.Definition.CueSetId}",
                textX,
                ref textY,
                Color.LightGray
            );
            DrawLine(
                spriteBatch,
                $"cue: {selection.Result.Cue.CueId}",
                textX,
                ref textY,
                Color.LightGray
            );
            DrawLine(
                spriteBatch,
                $"clips: {selection.Result.Cue.Clips.Count}; listening: {selection.Result.Cue.ListeningStatus}",
                textX,
                ref textY,
                Color.Yellow
            );
            DrawLine(
                spriteBatch,
                "preview policy: load-only; no playback/coordinator",
                textX,
                ref textY,
                Color.Yellow
            );
            return;
        }

        if (
            selection.Result.VisualPreview is null
            || selection.Result.PhysicalResource is not XnaSanityTextureResource textureResource
        )
        {
            DrawLine(
                spriteBatch,
                diagnostic.Reason,
                textX,
                ref textY,
                Color.LightGray
            );
            return;
        }

        var preview = selection.Result.VisualPreview;
        var source = preview.SourceRectangle;
        if (
            source.X < 0
            || source.Y < 0
            || source.Width <= 0
            || source.Height <= 0
            || source.X + source.Width > textureResource.Texture.Width
            || source.Y + source.Height > textureResource.Texture.Height
        )
        {
            LogRenderFailureOnce(
                selection.SlotId,
                "resource.preview.source-out-of-range",
                "The preview source rectangle exceeds the decoded texture."
            );
            return;
        }

        var scale = Math.Min(192f / source.Width, 240f / source.Height);
        scale = Math.Max(1f, Math.Min(scale, 4f));
        var destination = new Rectangle(
            panelX + 16,
            panelY + 32,
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale))
        );
        spriteBatch.Draw(
            textureResource.Texture,
            destination,
            new Rectangle(source.X, source.Y, source.Width, source.Height),
            Color.White
        );

        if (preview.PivotSourcePx is { } pivot)
        {
            var pivotX = destination.X + (int)Math.Round(pivot.X * scale);
            var pivotY = destination.Y + (int)Math.Round(pivot.Y * scale);
            spriteBatch.Draw(
                Game1.staminaRect,
                new Rectangle(pivotX - 5, pivotY - 1, 11, 3),
                Color.Cyan
            );
            spriteBatch.Draw(
                Game1.staminaRect,
                new Rectangle(pivotX - 1, pivotY - 5, 3, 11),
                Color.Cyan
            );
        }

        DrawSourceBox(spriteBatch, destination, scale, preview.HurtBoxSourcePx, Color.LimeGreen);
        DrawSourceBox(spriteBatch, destination, scale, preview.AttackBoxSourcePx, Color.OrangeRed);
        DrawLine(
            spriteBatch,
            $"kind: {preview.Kind}; frame: {preview.FrameIndex + 1}/{preview.FrameCount}",
            textX,
            ref textY,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"pivot/boxes: cyan / green / red; provisional: {preview.IsProvisional}",
            textX,
            ref textY,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"owner-local: {preview.OwnerLocalOnly}; shared cache: yes",
            textX,
            ref textY,
            Color.LightGray
        );
    }

    private static void DrawSourceBox(
        SpriteBatch spriteBatch,
        Rectangle destination,
        float scale,
        SanityResourceRectangle? sourceBox,
        Color color
    )
    {
        if (sourceBox is not { } box || box.Width <= 0 || box.Height <= 0)
            return;

        var rectangle = new Rectangle(
            destination.X + (int)Math.Round(box.X * scale),
            destination.Y + (int)Math.Round(box.Y * scale),
            Math.Max(1, (int)Math.Round(box.Width * scale)),
            Math.Max(1, (int)Math.Round(box.Height * scale))
        );
        DrawRectangleOutline(spriteBatch, rectangle, color);
    }

    private static void DrawRectangleOutline(
        SpriteBatch spriteBatch,
        Rectangle rectangle,
        Color color
    )
    {
        const int thickness = 2;
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, thickness),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(
                rectangle.X,
                rectangle.Bottom - thickness,
                rectangle.Width,
                thickness
            ),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(rectangle.X, rectangle.Y, thickness, rectangle.Height),
            color
        );
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(
                rectangle.Right - thickness,
                rectangle.Y,
                thickness,
                rectangle.Height
            ),
            color
        );
    }

    private static void DrawLine(
        SpriteBatch spriteBatch,
        string text,
        int x,
        ref int y,
        Color color
    )
    {
        spriteBatch.DrawString(Game1.smallFont, text, new Vector2(x, y), color);
        y += 30;
    }

    private void OnAssetsInvalidated(object? sender, AssetsInvalidatedEventArgs e)
    {
        if (disposed)
            return;

        var paths = loader.GetKnownAssetPaths();
        var matched = e.NamesWithoutLocale.Any(name =>
            paths.Any(path =>
                name.IsEquivalentTo(path, true)
                || name.IsEquivalentTo($"Mods/{modId}/{path}", true)
            )
        );
        if (!matched)
            return;

        // Borrowers must stop and dispose SoundEffectInstance objects before the loader releases
        // their SoundEffect owners. Event invocation is synchronous on the SMAPI/XNA thread.
        WorldResourcesReleasing?.Invoke(
            SanityResourceReleaseReason.ContentInvalidated
        );
        VisualResourcesInvalidating?.Invoke();
        previews.ClearAll();
        loggedRenderFailures.Clear();
        loader.InvalidateContent(++contentRevision);
        monitor.Log(
            $"Sanity runtime resources invalidated (revision={contentRevision}, code=resource.content-invalidated).",
            LogLevel.Debug
        );
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        WorldResourcesReleasing?.Invoke(
            SanityResourceReleaseReason.ReturnedToTitle
        );
        previews.ClearAll();
        loggedRenderFailures.Clear();
        loader.ReleaseWorldResources(SanityResourceReleaseReason.ReturnedToTitle);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        // SMAPI closes its content coordinator and console/monitor before AppDomain ProcessExit.
        // This callback may also run off the captured XNA thread, so it must not call Dispose(),
        // helper events, monitor diagnostics, or native XNA disposal. Explicit lifecycle paths already
        // release world resources; final native handles are reclaimed with the process.
        _ = disposalGate.TryBegin(
            SanityResourceDisposeOrigin.ProcessExit,
            Environment.CurrentManagedThreadId
        );
    }

    private bool TryGetOwnerKey(out string ownerKey)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            ownerKey = string.Empty;
            return false;
        }

        ownerKey = Game1.player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private void LogPrimeSnapshot(SanityRuntimeResourceSnapshot snapshot)
    {
        var errors = snapshot.Diagnostics.Count(diagnostic =>
            diagnostic.Status is SanityResourceCapabilityStatus.InvalidMetadata
                or SanityResourceCapabilityStatus.UnavailableRequired
        );
        monitor.Log(
            $"Sanity resource catalog initialized (manifest-parses={snapshot.ManifestParseCount}, placeholders={snapshot.PlaceholderSlotIds.Count}, optional-disabled={snapshot.DisabledOptionalSlotIds.Count}, errors={errors}, deployment-root-only=true).",
            errors == 0 ? LogLevel.Debug : LogLevel.Error
        );
    }

    private void LogSnapshot(SanityRuntimeResourceSnapshot snapshot)
    {
        monitor.Log(
            $"Sanity resource status: enabled={snapshot.Enabled}, disposed={snapshot.Disposed}, generation={snapshot.Generation}, physical={snapshot.PhysicalResourceCount}, cue-sets={snapshot.CueSetCount}, hits={snapshot.CacheHits}, misses={snapshot.CacheMisses}, manifest-parses={snapshot.ManifestParseCount}, visual-parses={snapshot.VisualMetadataParseCount}, audio-parses={snapshot.AudioMetadataParseCount}, placeholders={snapshot.PlaceholderSlotIds.Count}, optional-disabled={snapshot.DisabledOptionalSlotIds.Count}.",
            LogLevel.Info
        );
    }

    private void LogDiagnostic(SanityRuntimeResourceDiagnostic diagnostic)
    {
        var level = diagnostic.Status switch
        {
            _ when diagnostic.IsWarning => LogLevel.Warn,
            SanityResourceCapabilityStatus.Available when diagnostic.IsPlaceholder => LogLevel.Warn,
            SanityResourceCapabilityStatus.Available => LogLevel.Info,
            SanityResourceCapabilityStatus.DisabledOptional => LogLevel.Warn,
            _ => LogLevel.Error,
        };
        monitor.Log(
            $"Sanity resource result (capability={diagnostic.Capability}, status={diagnostic.Status}, code={diagnostic.Code}, slot={diagnostic.SlotId}, path={diagnostic.Path}, required={diagnostic.Required}, placeholder={diagnostic.IsPlaceholder}, reason={diagnostic.Reason}).",
            level
        );
    }

    private void OnLoaderDiagnosticRecorded(SanityRuntimeResourceDiagnostic diagnostic)
    {
        if (diagnostic.IsWarning && diagnostic.Code.Contains("hash-", StringComparison.OrdinalIgnoreCase))
            LogDiagnostic(diagnostic);
    }

    private void LogRenderFailureOnce(string slotId, string code, string reason)
    {
        var key = string.Concat(slotId, "|", code);
        if (!loggedRenderFailures.Add(key))
            return;

        monitor.Log(
            $"Sanity preview draw failed closed (slot={slotId}, code={code}, reason={reason}).",
            LogLevel.Error
        );
    }
}

internal sealed class XnaSanityPhysicalResourceFactory : ISanityPhysicalResourceFactory
{
    private readonly int owningThreadId;

    internal XnaSanityPhysicalResourceFactory(int owningThreadId)
    {
        this.owningThreadId = owningThreadId;
    }

    public SanityPhysicalResourceCreationResult CreateTexture(string path, byte[] bytes)
    {
        if (Environment.CurrentManagedThreadId != owningThreadId)
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "resource.factory.wrong-thread",
                "Texture2D creation is allowed only on the SMAPI/XNA owning thread."
            );
        }
        if (Game1.graphics?.GraphicsDevice is null)
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "resource.factory.graphics-unavailable",
                "The XNA GraphicsDevice is not available at this lifecycle point."
            );
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
            return SanityPhysicalResourceCreationResult.Created(
                new XnaSanityTextureResource(path, texture)
            );
        }
        catch (Exception exception)
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "resource.factory.texture-decode-failed",
                $"Texture2D.FromStream failed with {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    public SanityPhysicalResourceCreationResult CreateSoundEffect(string path, byte[] bytes)
    {
        if (Environment.CurrentManagedThreadId != owningThreadId)
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "resource.factory.wrong-thread",
                "SoundEffect creation is allowed only on the SMAPI/XNA owning thread."
            );
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var soundEffect = SoundEffect.FromStream(stream);
            return SanityPhysicalResourceCreationResult.Created(
                new XnaSanitySoundResource(path, soundEffect)
            );
        }
        catch (Exception exception)
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "resource.factory.sound-decode-failed",
                $"SoundEffect.FromStream failed with {exception.GetType().Name}: {exception.Message}"
            );
        }
    }
}

internal sealed class XnaSanityTextureResource : ISanityPhysicalResource
{
    private bool disposed;

    internal XnaSanityTextureResource(string path, Texture2D texture)
    {
        Path = path;
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));
    }

    public SanityPhysicalResourceKind Kind => SanityPhysicalResourceKind.Texture;

    public string Path { get; }

    internal Texture2D Texture { get; }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Texture.Dispose();
    }
}

internal sealed class XnaSanitySoundResource : ISanityPhysicalResource
{
    private bool disposed;

    internal XnaSanitySoundResource(string path, SoundEffect soundEffect)
    {
        Path = path;
        SoundEffect = soundEffect ?? throw new ArgumentNullException(nameof(soundEffect));
    }

    public SanityPhysicalResourceKind Kind => SanityPhysicalResourceKind.SoundEffect;

    public string Path { get; }

    internal SoundEffect SoundEffect { get; }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        SoundEffect.Dispose();
    }
}
