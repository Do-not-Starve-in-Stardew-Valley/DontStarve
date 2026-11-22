using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityVisualRuntimeStaticTests
{
    [Fact]
    public void Adapter_composes_instance_owned_world_then_ui_without_global_viewport_mutation()
    {
        var service = ReadVisual();
        var runtime = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");

        Assert.DoesNotContain("Events.Display.RenderingHud", service, StringComparison.Ordinal);
        Assert.Contains("Game1.uiViewport.Width", service, StringComparison.Ordinal);
        Assert.Contains("Game1.uiViewport.Height", service, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.ShouldDrawOnBuffer)", runtime, StringComparison.Ordinal);
        Assert.Contains("\"renderScreenBuffer\"", runtime, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.DrawSplitScreenWindow)", runtime, StringComparison.Ordinal);
        Assert.Contains("game.instanceId", runtime, StringComparison.Ordinal);
        Assert.Contains("game.screen", runtime, StringComparison.Ordinal);
        Assert.Contains("game.uiScreen", runtime, StringComparison.Ordinal);
        Assert.Contains("Game1.defaultDeviceViewport", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.viewport =", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("new RenderTarget2D", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderedWorld +=", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_danger_border_has_no_runtime_load_or_draw_path()
    {
        var service = ReadVisual();
        var runtime = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");
        var drawStart = runtime.IndexOf("private void DrawWorldAndUi", StringComparison.Ordinal);
        var drawEnd = runtime.IndexOf("private bool EnsureEffect", drawStart, StringComparison.Ordinal);
        var draw = runtime[drawStart..drawEnd];

        Assert.Contains("worldEffect!", draw, StringComparison.Ordinal);
        Assert.Contains("Game1.spriteBatch.Draw", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("OnRenderingHud", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerBorderSlotId", service, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadDangerBorder", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawDangerBorder", service, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadVisualSlot", service, StringComparison.Ordinal);
        Assert.DoesNotContain("new Texture2D", service, StringComparison.Ordinal);
        Assert.DoesNotContain("new SpriteBatch", service, StringComparison.Ordinal);
        Assert.DoesNotContain("new RenderTarget2D", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("new Effect", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Json", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor.Log", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void World_composition_pauses_saturation_but_keeps_offset_distortion_and_independent_colour_inputs()
    {
        var runtime = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");

        Assert.DoesNotContain("parameters.Saturation", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.OffsetX", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.OffsetY", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.DistortionAmount", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.DistortionPhase", runtime, StringComparison.Ordinal);
        Assert.Contains("parameters.InsanityColourBlend", runtime, StringComparison.Ordinal);
        Assert.Contains("SanityWorldColourPolicy.ResolvePhase", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void World_composition_final_activation_rechecks_live_minigame_and_cleans_stale_state()
    {
        var runtime = ReadWorkspaceSource(
            Path.Combine(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "Visual",
                "SanityWorldCompositionRuntimeAdapter.cs"
            )
        );
        var activationStart = runtime.IndexOf(
            "private bool HasActiveWorldComposition",
            StringComparison.Ordinal
        );
        var activationEnd = runtime.IndexOf(
            "private bool TryDrawSingle",
            activationStart,
            StringComparison.Ordinal
        );

        Assert.True(activationStart >= 0);
        Assert.True(activationEnd > activationStart);
        var activation = runtime[activationStart..activationEnd];
        Assert.Contains("parametersByScreen.TryGetValue", activation, StringComparison.Ordinal);
        Assert.Contains("parameters.IsActive", activation, StringComparison.Ordinal);
        Assert.Contains(
            "SanityMinigameVisualRuntimeClassifier.ResolveCurrent()",
            activation,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SanityMinigameVisualRuntimeClassifier.IsWorldCompositionAllowed",
            activation,
            StringComparison.Ordinal
        );
        Assert.Contains("parametersByScreen.Remove(screenId)", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_classifier_keeps_fishing_whitelist_exactly_bounded()
    {
        var classifier = ReadWorkspaceSource(
            Path.Combine(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "Visual",
                "SanityMinigameVisualRuntimeClassifier.cs"
            )
        );
        var exhibitionStart = classifier.IndexOf(
            "var isExhibitionFishing",
            StringComparison.Ordinal
        );
        var exhibitionEnd = classifier.IndexOf(
            "var isIceFishing",
            exhibitionStart,
            StringComparison.Ordinal
        );

        Assert.True(exhibitionStart >= 0);
        Assert.True(exhibitionEnd > exhibitionStart);
        var exhibition = classifier[exhibitionStart..exhibitionEnd];
        Assert.Contains("var currentMinigame = Game1.currentMinigame", classifier, StringComparison.Ordinal);
        Assert.Contains("currentMinigame is FishingGame", exhibition, StringComparison.Ordinal);
        Assert.Contains("ExhibitionFishingFestivalId = \"fall16\"", classifier, StringComparison.Ordinal);
        Assert.Contains("currentEvent.isSpecificFestival(ExhibitionFishingFestivalId)", exhibition, StringComparison.Ordinal);
        Assert.Contains("var activeMenu = Game1.activeClickableMenu", classifier, StringComparison.Ordinal);
        Assert.Contains("activeMenu is BobberBar", classifier, StringComparison.Ordinal);
        Assert.Contains("IceFishingSequenceId = \"iceFishing\"", classifier, StringComparison.Ordinal);
        Assert.Contains("internal static bool IsWorldCompositionAllowed", classifier, StringComparison.Ordinal);
        Assert.Contains(
            "return context is SanityMinigameVisualContext.None",
            classifier,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "or SanityMinigameVisualContext.Fishing",
            classifier,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "internal static bool ShouldPauseLocalAudio",
            ReadWorkspaceSource(
                Path.Combine(
                    "DontStarve",
                    "Player",
                    "Stats",
                    "Sanity",
                    "Visual",
                    "SanityMinigameVisualClassifier.cs"
                )
            ),
            StringComparison.Ordinal
        );
        Assert.Equal(
            1,
            classifier.Split("isSpecificFestival(", StringSplitOptions.None).Length - 1
        );
        Assert.DoesNotContain("isSpecificFestival(\"spring", classifier, StringComparison.Ordinal);
        Assert.DoesNotContain("isSpecificFestival(\"summer", classifier, StringComparison.Ordinal);
        Assert.DoesNotContain("isSpecificFestival(\"winter", classifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_does_not_request_permanently_retired_danger_border_resource()
    {
        var source = ReadVisual();

        Assert.DoesNotContain("DangerBorderSlotId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("resources.LoadVisualSlot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("XnaSanityTextureResource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dangerBorderTexture", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SanityRuntimeResourceLoader", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Panic_loop_only_resets_the_sprite_when_no_vanilla_action_owns_it()
    {
        var source = ReadWorkspaceSource(
            Path.Combine(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "Visual",
                "SanityIdlePanicLoopService.cs"
            )
        );
        var cancelStart = source.IndexOf(
            "private void CancelAndRecover",
            StringComparison.Ordinal
        );
        var cancelEnd = source.IndexOf("private void HardCancel", cancelStart, StringComparison.Ordinal);

        Assert.True(cancelStart >= 0);
        Assert.True(cancelEnd > cancelStart);
        var cancel = source[cancelStart..cancelEnd];
        Assert.Contains(
            "SanityIdlePanicInterruptionPolicy.ShouldReleasePlayerAnimation(reason)",
            cancel,
            StringComparison.Ordinal
        );
        var guard = cancel.IndexOf(
            "SanityIdlePanicInterruptionPolicy.ShouldReleasePlayerAnimation(reason)",
            StringComparison.Ordinal
        );
        var stop = cancel.IndexOf(
            "player.completelyStopAnimatingOrDoingAction()",
            StringComparison.Ordinal
        );
        Assert.True(stop > guard);
        Assert.DoesNotContain(
            "player.completelyStopAnimatingOrDoingAction()",
            cancel[..guard],
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Panic_loop_does_not_emit_per_tick_gate_diagnostics()
    {
        var source = ReadWorkspaceSource(
            Path.Combine(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "Visual",
                "SanityIdlePanicLoopService.cs"
            )
        );

        Assert.DoesNotContain("LogGate(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked ({code})", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Panic_loop_suspends_on_menu_pause_focus_and_pre_releases_sprite_on_mouse_clicks()
    {
        var source = ReadWorkspaceSource(
            Path.Combine(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "Visual",
                "SanityIdlePanicLoopService.cs"
            )
        );

        // 挂起三信号齐全：菜单/暂停/失焦冻结循环而非取消（用户口径：暂停后恢复）。
        // 2026-11-13 守卫提取：两个判定方法原样移入共享类 CombatInputGuards（战斗自动化
        // 复用同一口径），信号级契约改由 CombatInputGuardsStaticContractTests 锁定；
        // 这里只锁"服务只引用共享类、不留私有副本"。
        Assert.Contains(
            "CombatInputGuards.IsGameInteractionSuspended()",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "private static bool IsGameInteractionSuspended()",
            source,
            StringComparison.Ordinal
        );

        // 原版右键进食入口有 !PauseForSingleAnimation 门：仅鼠标按键在原版输入处理前抢跑
        // 让位；键盘/滚轮不订阅抢跑，菜单挂起-恢复语义与"非动画输入不算操作"口径保留。
        Assert.Contains("SButton.MouseRight", source, StringComparison.Ordinal);
        Assert.Contains("SButton.MouseLeft", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Events.Input.MouseWheelScrolled +=",
            source,
            StringComparison.Ordinal
        );

        // 事件覆盖：eventUp 之外还需识别 CurrentEvent 与 farmEvent，避免任何事件形态中触发。
        // 打断清单整体在共享守卫 DescribePanicInterruption 内（契约同样在守卫契约测试）。
        Assert.Contains(
            "CombatInputGuards.DescribePanicInterruption(player)",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "private static string? DescribePanicInterruption(Farmer player)",
            source,
            StringComparison.Ordinal
        );

        // 受惊表情自我锁豁免：原版 performPlayerEmote 的 noMovementPause 会令 CanMove=false
        // 200ms，若当作打断则受惊段活不过一帧、跳跃永远不可见。（豁免判定随守卫提取移入
        // CombatInputGuards，由其契约测试锁定。）
    }

    [Fact]
    public void Panic_loop_refires_vanilla_passout_instead_of_freezing_for_seven_seconds()
    {
        var source = ReadWorkspaceSource(
            Path.Combine(
                "DontStarve",
                "Player",
                "Stats",
                "Sanity",
                "Visual",
                "SanityIdlePanicLoopService.cs"
            )
        );

        // 2 点/力竭判定必须与原版一致，且只在循环活跃时介入。
        Assert.Contains(
            "Game1.timeOfDay >= 2600 || player.stamina <= -15f",
            source,
            StringComparison.Ordinal
        );
        // 帧五吞触发的补救：清帧五后必须替原版重发 startToPassOut（否则 freezePause=7000
        // 造成约 7 秒冻结）；重发前必须确认精灵不在昏睡态，293 已接管时绝不清理。
        Assert.Contains("player.startToPassOut()", source, StringComparison.Ordinal);
        Assert.Contains(
            "!player.FarmerSprite.isPassingOut()",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "player.FarmerSprite.currentSingleAnimation == VanillaPassOutAnimationToken",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Idle_adapter_uses_owned_negative_token_and_never_passout_side_effects()
    {
        var service = ReadVisual();
        var runtime = ReadRuntime("SanityIdlePresentationRuntime.cs");

        Assert.Contains("player.IsBusyDoingSomething()", service, StringComparison.Ordinal);
        Assert.Contains("player.isMoving()", service, StringComparison.Ordinal);
        Assert.Contains("player.movementDirections.Count", service, StringComparison.Ordinal);
        Assert.Contains("sprite.PauseForSingleAnimation", service, StringComparison.Ordinal);
        Assert.Contains("sprite.IsPlayingBasicAnimation(player.FacingDirection, player.IsCarrying())", service, StringComparison.Ordinal);
        Assert.Contains("Game1.currentGameTime.ElapsedGameTime", service, StringComparison.Ordinal);
        Assert.Contains("Events.Input.ButtonPressed +=", service, StringComparison.Ordinal);
        Assert.Contains("Events.Input.MouseWheelScrolled +=", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Events.Input.CursorMoved +=", service, StringComparison.Ordinal);
        Assert.Contains("sprite.animateOnce(animation)", runtime, StringComparison.Ordinal);
        Assert.Contains("SanityIdlePresentationContract.AnimationToken", runtime, StringComparison.Ordinal);
        Assert.Contains("sprite.loopThisAnimation = true", runtime, StringComparison.Ordinal);
        Assert.Contains("IsOwnedToken", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("animateOnce(293", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("passOutFromTired", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("CanMove =", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain(".Halt(", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_is_owner_screen_session_effective_and_resource_scoped()
    {
        var source = ReadVisual();

        Assert.Contains("lifecycle.EventOwnerCoverageChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("lifecycle.WorldBoundaryStarting +=", source, StringComparison.Ordinal);
        Assert.Contains("lifecycle.SessionClearing +=", source, StringComparison.Ordinal);
        Assert.Contains("resources.WorldResourcesReleasing +=", source, StringComparison.Ordinal);
        Assert.Contains("effectiveSanity.TryGetEffectiveRatio", source, StringComparison.Ordinal);
        Assert.Contains("new SanityEffectiveOverlayKey(playerKey, screenId, lifecycle.SessionId)", source, StringComparison.Ordinal);
        Assert.Contains("RemoveScreen(change.Key.ScreenId)", source, StringComparison.Ordinal);
        Assert.Contains("pair.Key == Context.ScreenId", source, StringComparison.Ordinal);
        Assert.Contains("resources.VisualResourcesInvalidating -= OnVisualResourcesInvalidating", source, StringComparison.Ordinal);
        Assert.Contains("resources.VisualResourcesInvalidating += OnVisualResourcesInvalidating", source, StringComparison.Ordinal);
        Assert.Contains("worldComposition.RemoveScreen(screenId)", source, StringComparison.Ordinal);
        Assert.Contains("idlePresentation.CancelAll()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Mod_entry_wires_visual_config_without_touching_audio_or_music_switches()
    {
        var source = ReadContract("ShadowProjection", "ModEntry.cs");
        var service = ReadVisual();

        Assert.Contains("ConfigKeys.EnableSanityVisualEffects", source, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.EnableLowSanityScreenDistortion", source, StringComparison.Ordinal);
        Assert.Contains("new SanitySmapiVisualService", source, StringComparison.Ordinal);
        Assert.Contains("ModManifest.UniqueID", source, StringComparison.Ordinal);
        Assert.Contains("_sanityVisual?.SetEnabled", source, StringComparison.Ordinal);
        Assert.Contains("_sanityVisual?.SetScreenDistortionEnabled", source, StringComparison.Ordinal);
        Assert.Contains("screenDistortionEnabled", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_is_exact_version_backend_shape_gated_and_exactly_unpatched()
    {
        var source = ReadRuntime("SanityWorldCompositionRuntimeAdapter.cs");

        Assert.Contains("SanityRendererCapabilityGate.Validate", source, StringComparison.Ordinal);
        Assert.Contains("\"MonoGame.OpenGL.GL\"", source, StringComparison.Ordinal);
        Assert.Contains("Harmony.GetPatchInfo", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Postfix", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Prefix", source, StringComparison.Ordinal);
        Assert.Contains("harmony.Unpatch(method, patchType, patchOwnerId)", source, StringComparison.Ordinal);
        Assert.Contains("worldEffect.IsDisposed", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(worldEffect.GraphicsDevice, device)", source, StringComparison.Ordinal);
    }

    private static string ReadVisual()
    {
        return ReadContract("VisualRuntime", "SanitySmapiVisualService.cs");
    }

    private static string ReadRuntime(string fileName)
    {
        if (string.Equals(fileName, "SanityWorldCompositionRuntimeAdapter.cs", StringComparison.Ordinal))
        {
            return ReadWorkspaceSource(
                Path.Combine(
                    "DontStarve",
                    "Player",
                    "Stats",
                    "Sanity",
                    "Visual",
                    fileName
                )
            );
        }

        return ReadContract("VisualRuntime", fileName);
    }

    private static string ReadContract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }

    private static string ReadWorkspaceSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DontStarveInSDV.sln")))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The repository root could not be located for the static source contract."
        );
    }
}
