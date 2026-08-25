#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity.Darkness;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Illusions.Lighting;

/// <summary>
/// Opt-in, owner-local diagnostic matrix. It is off by default and never changes the world or
/// player state; its only update work is one current-owner sample every 15 ticks while visible.
/// </summary>
internal sealed class SmapiEnvironmentLightDebugOverlay : IDisposable
{
    private const string Command = "ds_sanity_light";
    private const int MaximumLoggedReasons = 32;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ITimeAPI timeApi;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly EnvironmentLightService service;
    private readonly SmapiDarknessAttackService darknessAttack;
    private readonly SmapiDarknessAttackResolutionService darknessResolution;
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool visible;
    private bool disposed;

    internal SmapiEnvironmentLightDebugOverlay(
        IModHelper helper,
        IMonitor monitor,
        ITimeAPI timeApi,
        SanitySystemLifecycleCoordinator lifecycle,
        EnvironmentLightService service,
        SmapiDarknessAttackService darknessAttack,
        SmapiDarknessAttackResolutionService darknessResolution
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.timeApi = timeApi ?? throw new ArgumentNullException(nameof(timeApi));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.darknessAttack = darknessAttack
            ?? throw new ArgumentNullException(nameof(darknessAttack));
        this.darknessResolution = darknessResolution
            ?? throw new ArgumentNullException(nameof(darknessResolution));

        helper.ConsoleCommands.Add(
            Command,
            "Harmless light diagnostic: ds_sanity_light on|off|status|refresh|clear.",
            OnCommand
        );
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Display.RenderingHud += OnRenderingHud;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.EventCoverageChanged += OnEventCoverageChanged;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        service.DiagnosticCaptured += OnDiagnosticCaptured;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        visible = false;
        service.Clear();
        loggedReasons.Clear();
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.Display.RenderingHud -= OnRenderingHud;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.EventCoverageChanged -= OnEventCoverageChanged;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        service.DiagnosticCaptured -= OnDiagnosticCaptured;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private void OnCommand(string command, string[] arguments)
    {
        if (disposed)
            return;

        var action = arguments.Length == 0
            ? "status"
            : arguments[0].Trim().ToLowerInvariant();
        switch (action)
        {
            case "on":
                visible = true;
                service.Clear();
                loggedReasons.Clear();
                RefreshAndLog();
                monitor.Log(
                    "Environment-light harmless diagnostic overlay enabled.",
                    LogLevel.Info
                );
                break;
            case "off":
                visible = false;
                monitor.Log(
                    "Environment-light harmless diagnostic overlay disabled.",
                    LogLevel.Info
                );
                break;
            case "refresh":
                InvalidateCurrent(EnvironmentLightInvalidationReason.LocationInvalid);
                RefreshAndLog();
                break;
            case "clear":
                Clear();
                monitor.Log(
                    "Environment-light diagnostic cache cleared.",
                    LogLevel.Debug
                );
                break;
            case "status":
                RefreshAndLog();
                break;
            default:
                monitor.Log(
                    $"{Command}: expected on, off, status, refresh, or clear.",
                    LogLevel.Info
                );
                break;
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (
            disposed
            || !visible
            || !e.IsMultipleOf((uint)EnvironmentLightCache.SampleCadenceTicks)
        )
        {
            return;
        }
        RefreshAndLog();
    }

    private void OnRenderingHud(object? sender, RenderingHudEventArgs e)
    {
        if (
            disposed
            || !visible
            || !TryGetCurrent(out _, out var playerKey, out var screenId, out _)
            || !service.TryGetDiagnostic(playerKey, screenId, out var diagnostic)
        )
        {
            return;
        }

        darknessAttack.TryGetSnapshot(playerKey, screenId, out var attackSnapshot);
        darknessResolution.TryGetSnapshot(
            playerKey,
            screenId,
            out var resolutionSnapshot
        );
        DrawDiagnostic(
            e.SpriteBatch,
            diagnostic,
            attackSnapshot,
            resolutionSnapshot
        );
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;

        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemDisabled:
                Clear();
                break;
            case SanityStateEventKind.OwnerInvalidated:
                service.Invalidate(
                    stateEvent.PlayerKey,
                    Context.ScreenId,
                    EnvironmentLightInvalidationReason.OwnerInvalidated
                );
                loggedReasons.Clear();
                break;
        }
    }

    private void OnEventCoverageChanged(bool active)
    {
        if (!disposed && active)
            Clear();
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (disposed)
            return;

        if (boundary == SanityWorldBoundary.Warp)
        {
            InvalidateCurrent(EnvironmentLightInvalidationReason.OwnerWarped);
            return;
        }
        if (boundary == SanityWorldBoundary.DayStarted)
            Clear();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            Clear();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!disposed)
            Clear();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void OnDiagnosticCaptured(EnvironmentLightDiagnostic diagnostic)
    {
        LogReasonOnce(
            diagnostic.Reason,
            string.Concat(
                "Environment-light diagnostic ",
                $"(location={diagnostic.LocationNameOrUniqueName}, ",
                $"rule={diagnostic.LocationRuleId}@{diagnostic.LocationRuleContractVersion}, ",
                $"visibility={FormatNullable(diagnostic.FinalVisibilityScore)}, ",
                $"evaluator={diagnostic.EvaluatorRevision}, ",
                $"level={diagnostic.Level}, evidence={diagnostic.EvidenceStatus}, ",
                $"pitchBlackAuthorized={diagnostic.PitchBlackAuthorized}, ",
                $"reason={diagnostic.Reason})."
            ),
            diagnostic.EvidenceStatus == EnvironmentLightEvidenceStatus.Fallback
                ? LogLevel.Warn
                : LogLevel.Debug
        );
    }

    private void RefreshAndLog()
    {
        if (!TryGetCurrent(out var owner, out var playerKey, out var screenId, out var location))
        {
            LogReasonOnce(
                "environment-light.debug-world-unavailable",
                "Environment-light diagnostic is unavailable until a local world is loaded."
            );
            return;
        }

        service.Evaluate(owner, screenId, location, timeApi.Time);
        if (!service.TryGetDiagnostic(playerKey, screenId, out var diagnostic))
            return;

        LogReasonOnce(
            diagnostic.Reason,
            string.Concat(
                "Environment-light diagnostic ",
                $"(location={diagnostic.LocationNameOrUniqueName}, ",
                $"rule={diagnostic.LocationRuleId}@{diagnostic.LocationRuleContractVersion}, ",
                $"visibility={FormatNullable(diagnostic.FinalVisibilityScore)}, ",
                $"evaluator={diagnostic.EvaluatorRevision}, ",
                $"level={diagnostic.Level}, evidence={diagnostic.EvidenceStatus}, ",
                $"pitchBlackAuthorized={diagnostic.PitchBlackAuthorized}, ",
                $"reason={diagnostic.Reason})."
            ),
            diagnostic.EvidenceStatus == EnvironmentLightEvidenceStatus.Fallback
                ? LogLevel.Warn
                : LogLevel.Debug
        );
    }

    private void InvalidateCurrent(EnvironmentLightInvalidationReason reason)
    {
        if (TryGetCurrent(out _, out var playerKey, out var screenId, out _))
            service.Invalidate(playerKey, screenId, reason);
        else
            service.Clear();
        loggedReasons.Clear();
    }

    private void Clear()
    {
        service.Clear();
        loggedReasons.Clear();
    }

    private void LogReasonOnce(
        string reason,
        string message,
        LogLevel level = LogLevel.Debug
    )
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || loggedReasons.Contains(reason)
            || loggedReasons.Count >= MaximumLoggedReasons
        )
        {
            return;
        }
        loggedReasons.Add(reason);
        monitor.Log(message, level);
    }

    private static bool TryGetCurrent(
        out Farmer owner,
        out string playerKey,
        out int screenId,
        out GameLocation location
    )
    {
        owner = Game1.player;
        location = Game1.currentLocation;
        playerKey = string.Empty;
        screenId = Context.ScreenId;
        if (
            !Context.IsWorldReady
            || screenId < 0
            || !Context.HasScreenId(screenId)
            || owner is null
            || !owner.IsLocalPlayer
            || location is null
            || string.IsNullOrWhiteSpace(location.NameOrUniqueName)
        )
        {
            return false;
        }

        playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            owner.UniqueMultiplayerID
        );
        return SanityPlayerKey.IsCanonical(playerKey);
    }

    private static void DrawDiagnostic(
        SpriteBatch spriteBatch,
        EnvironmentLightDiagnostic diagnostic,
        DarknessAttackStateSnapshot? attack,
        DarknessAttackResolutionReceipt? resolution
    )
    {
        const int panelX = 24;
        const int panelY = 24;
        const int maximumWidth = 1080;
        const int maximumPanelHeight = 480;
        var panelWidth = Math.Min(maximumWidth, Game1.uiViewport.Width - panelX * 2);
        var panelHeight = Math.Min(
            maximumPanelHeight,
            Game1.uiViewport.Height - panelY * 2
        );
        if (panelWidth <= 0 || panelHeight <= 0)
            return;

        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(panelX, panelY, panelWidth, panelHeight),
            Color.Black * 0.82f
        );

        var x = panelX + 14;
        var y = panelY + 12;
        DrawLine(spriteBatch, "Sanity harmless environment-light diagnostic", x, ref y, Color.White);
        DrawLine(
            spriteBatch,
            $"owner/screen: {diagnostic.PlayerKey}/{diagnostic.ScreenId}; location: {diagnostic.LocationNameOrUniqueName}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"location type/context: {diagnostic.LocationRuntimeTypeFullName} / {diagnostic.LocationContextId}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"location flags outdoors/temp/event/festival: {diagnostic.IsLocationOutdoors}/{diagnostic.IsLocationTemporary}/{diagnostic.IsEventActive}/{diagnostic.IsFestivalActive}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            FormattableString.Invariant(
                $"standing px: {diagnostic.StandingPixel.X:0},{diagnostic.StandingPixel.Y:0}; base: {diagnostic.BaseSource} [{diagnostic.BaseRawColor.ToDiagnosticString()}]"
            ),
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            FormattableString.Invariant(
                $"raw LightLevel: {diagnostic.LocationLightLevelCapabilityStatus}/{diagnostic.RawLocationLightLevel:0.###}; isDarkOut: {diagnostic.IsDarkOut}"
            ),
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"location rule: {diagnostic.LocationRuleStatus}/{diagnostic.LocationRuleId}@{diagnostic.LocationRuleContractVersion}; profile={diagnostic.LocationLightProfile}",
            x,
            ref y,
            diagnostic.LocationRuleStatus == EnvironmentLightLocationRuleStatus.Matched
                ? Color.LightGray
                : Color.Yellow
        );
        DrawLine(
            spriteBatch,
            $"rule semantics 2am/hostile/junimo: {diagnostic.TwoAmSpecialDeathSafe}/{diagnostic.HostileShadowSafe}/{diagnostic.JunimoBlessingEligible}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"2am reason={diagnostic.TwoAmSpecialDeathReason}; rule reason={diagnostic.LocationRuleReason}; parent building={diagnostic.LocationParentBuildingType}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"lights current/shared/unique: {diagnostic.CurrentLightSourceCount}/{diagnostic.SharedLightSourceCount}/{diagnostic.UniqueCandidateCount}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            FormatNearest(diagnostic),
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"mine-dark: {diagnostic.MineDarkAreaCapabilityStatus}/{FormatNullable(diagnostic.IsMineDarkArea)}; night-vision: {diagnostic.NightVisionCapabilityStatus}/{FormatNullable(diagnostic.IsNightVisionActive)}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            FormattableString.Invariant(
                $"final visibility: {diagnostic.FinalVisibilityCapabilityStatus}/{FormatNullable(diagnostic.FinalVisibilityScore)}; darkness={FormatNullable(diagnostic.FinalVisibilityDarknessColor)}; lighting={diagnostic.StandardLightingDrawn}; rain={diagnostic.RainOverlayApplied}; quality/zoom/unscaled={diagnostic.LightingQuality}/{FormatNullable(diagnostic.ZoomLevel)}/{diagnostic.UseUnscaledLighting}"
            ),
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"renderer/evaluator: {diagnostic.RendererRevision}/{diagnostic.EvaluatorRevision}; sample tick={diagnostic.FinalVisibilityCapturedAtTick}; final reason={diagnostic.FinalVisibilityReason}",
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"result: {diagnostic.Level} / {diagnostic.EvidenceStatus}; PitchBlackAuthorized: {diagnostic.PitchBlackAuthorized}",
            x,
            ref y,
            diagnostic.EvidenceStatus == EnvironmentLightEvidenceStatus.Confirmed
                ? Color.LightGreen
                : Color.Yellow
        );
        DrawLine(spriteBatch, $"reason: {diagnostic.Reason}", x, ref y, Color.Yellow);
        DrawLine(
            spriteBatch,
            $"minute/tick/revision: {diagnostic.CapturedAtMinute}/{diagnostic.CapturedAtTick}/{diagnostic.Revision}",
            x,
            ref y,
            Color.Gray
        );
        DrawLine(
            spriteBatch,
            resolution is null
                ? "settlement: none"
                : $"settlement: {resolution.Status}; request={resolution.RequestId}; mode={resolution.Mode}; roll/base={resolution.RngRoll}/{resolution.BaseDamage}; op={resolution.Operation}; receipt={resolution.ReceiptId}; sanity-rev={resolution.SanityReceipt?.AfterRevision.ToString() ?? "none"}",
            x,
            ref y,
            resolution?.IsSettled == true ? Color.LightGreen : Color.Gray
        );
        if (attack is null)
        {
            DrawLine(
                spriteBatch,
                "darkness attack: Inactive / no authorized host cycle",
                x,
                ref y,
                Color.Gray
            );
            return;
        }
        DrawLine(
            spriteBatch,
            FormattableString.Invariant(
                $"darkness attack: {attack.State}; remaining={attack.RemainingSeconds:0.000}s; warning={attack.WarningClaimActive}; request={attack.RequestId}"
            ),
            x,
            ref y,
            attack.State == DarknessAttackOwnerState.ExpiredAwaitingReceipt
                ? Color.OrangeRed
                : Color.LightGray
        );
        DrawLine(
            spriteBatch,
            FormattableString.Invariant(
                $"attack light/reason: {attack.LightLevel}/{attack.EvidenceStatus}; {attack.LightReason}; cancel={attack.CancelReason}"
            ),
            x,
            ref y,
            Color.LightGray
        );
        DrawLine(
            spriteBatch,
            $"attack sample/rng/lead/contract: {attack.SampledSeconds}s / {attack.RngBranch} / {attack.WarningLeadSeconds:0.###}s / {attack.ContractVersion}",
            x,
            ref y,
            Color.Gray
        );
    }

    private static string FormatNearest(EnvironmentLightDiagnostic diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.NearestCandidateId))
            return "nearest light: none";

        var distance = diagnostic.NearestCandidateDistanceWorldPixels.HasValue
            ? diagnostic.NearestCandidateDistanceWorldPixels.Value.ToString(
                "0.0",
                CultureInfo.InvariantCulture
            )
            : "unknown";
        var radius = diagnostic.NearestCandidateRawRadius.HasValue
            ? diagnostic.NearestCandidateRawRadius.Value.ToString(
                "0.###",
                CultureInfo.InvariantCulture
            )
            : "unknown";
        var tint = diagnostic.NearestCandidateRawTint?.ToDiagnosticString()
            ?? "unknown";
        var position = diagnostic.NearestCandidatePosition.HasValue
            ? FormattableString.Invariant(
                $"{diagnostic.NearestCandidatePosition.Value.X:0},{diagnostic.NearestCandidatePosition.Value.Y:0}"
            )
            : "unknown";
        var playerId = diagnostic.NearestCandidateAttachedPlayerId?.ToString(
            CultureInfo.InvariantCulture
        ) ?? "unknown";
        return string.Concat(
            $"nearest light: {diagnostic.NearestCandidateId}; pos={position}; distance={distance}; ",
            $"raw radius={radius}; tint={tint}; origin/context={diagnostic.NearestCandidateOrigin}/{diagnostic.NearestCandidateLightContext}; ",
            $"player={playerId}; onlyLocation={diagnostic.NearestCandidateOnlyLocation}; ",
            $"draw={diagnostic.NearestCandidateDrawEligibilityCapabilityStatus}/{FormatNullable(diagnostic.IsNearestCandidateDrawEligible)}"
        );
    }

    private static string FormatNullable(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "unknown";
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.000", CultureInfo.InvariantCulture)
            : "unknown";
    }

    private static string FormatNullable(EnvironmentLightColor? value)
    {
        return value.HasValue
            ? FormattableString.Invariant(
                $"{value.Value.R},{value.Value.G},{value.Value.B},{value.Value.A}"
            )
            : "unknown";
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
        y += 27;
    }
}
