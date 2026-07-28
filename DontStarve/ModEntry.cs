using System;
using System.IO;
using DontStarve.Buff;
using DontStarve.Config;
using DontStarve.Display;
using DontStarve.Display.UIElements;
using DontStarve.Music;
using DontStarve.Player;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Audio;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Events;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Player.Stats.Sanity.PassOut;
using DontStarve.Player.Stats.Sanity.Visual;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;
using DontStarve.Recipe;
using DontStarve.Resource;
using DontStarve.Resource.Sanity;
using DontStarve.Time;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve;

internal class ModEntry : Mod
{
    // 全局只维护这一份内部分钟时间服务；Hunger、Sanity、Buff、Display 都从这里接收同一个时间源。
    private readonly TimeApi _timeApi = new();
    private ModConfig _config = new();
    private ConfigurationRuntime _configurationRuntime;
    private SanitySystemLifecycleCoordinator _sanityLifecycle;
    private SanitySmapiResourceService _sanityResources;
    private SanitySmapiAudioService _sanityAudio;
    private SanitySmapiEventService _sanityEvents;
    private SanitySmapiVisualService _sanityVisual;
    private SmapiHarmlessProjectionHost _harmlessProjectionHost;
    private SmapiForageVisualProjectionService _forageVisualProjection;
    private SmapiForagePickupReplacementService _foragePickupReplacement;
    private SmapiVanillaSanityTooltipService _vanillaSanityTooltip;
    private SmapiDarkHandFireThiefService _darkHandFireThief;
    private SmapiDarkHandHarassmentService _darkHandHarassment;
    private SmapiDarkHandThiefService _darkHandThief;
    private SmapiDarkHandLeaseCoordinatorService _darkHandLeaseCoordinator;
    private SmapiHostileShadowHost _hostileShadowHost;
    private SmapiEnvironmentLightFinalVisibilitySampler _environmentLightSampler;
    private EnvironmentLightService _environmentLightService;
    private SmapiEnvironmentLightMultiplayerCoordinator
        _environmentLightMultiplayer;
    private SmapiDarknessAttackService _darknessAttack;
    private SmapiDarknessAttackResolutionService _darknessAttackResolution;
    private PassOutReasonLedger _passOutReasonLedger;
    private SmapiPassOutReasonService _passOutReasonService;
    private SmapiSanityTwoAmSpecialDeathService _sanityTwoAmPassOut;
    private SmapiEnvironmentLightDebugOverlay _environmentLightDebugOverlay;
    private bool _sanitySystemEnabled = true;
    private bool _sanityVisualEffectsEnabled = true;

    // schema 或配置根读坏时保持 fail-closed：允许本次用安全默认值运行，但不覆盖原文件。
    private bool _canWriteConfig;

    /// <summary>
    /// SMAPI 入口。初始化顺序有依赖关系，尤其是 TimeApi 必须先于所有时间消费者注册。
    /// </summary>
    public override void Entry(IModHelper helper)
    {
        _config = ReadModConfig(helper);

        // 后续模块不要再从外部 mod 获取 MinuteTimeHelper；本项目的时间契约由内部 TimeApi 提供。
        _timeApi.Initialize(helper);
        TextureLoader.Initialize(helper);
        MusicManager.Initialize(helper, Monitor, ModManifest.UniqueID, _config.EnableDawnDuskMusic);
        // SMAPI 只允许在所有 mod 初始化完成后获取其它 mod API，GMCM 注册必须延后到 GameLaunched。
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        RecipeCategoryDisplayService.Initialize(helper, Monitor, ModManifest.UniqueID);
        BuffManager.Initialize(helper, _timeApi);
        _passOutReasonLedger = new PassOutReasonLedger();
        _sanityLifecycle = StatManager.Initialize(
            helper,
            _timeApi,
            Monitor,
            ModManifest.UniqueID,
            _canWriteConfig ? _configurationRuntime.Resolver : null,
            _passOutReasonLedger
        );
        DisplayManager.Initialize(helper, _timeApi, _sanityLifecycle);
        // EatFood/Wearing have already loaded their unique data tables through StatManager.
        // The menu adapter consumes those exact values and never mutates state while drawing.
        _vanillaSanityTooltip = new SmapiVanillaSanityTooltipService(
            helper,
            Monitor,
            ModManifest.UniqueID
        );
        // Sanity.Init 已先完成 registry/tier/budget/cleanup 注册；这里才应用真实总开关。
        _sanityLifecycle.ApplyConfiguredState(_sanitySystemEnabled);
        _sanityEvents = new SanitySmapiEventService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanitySystemEnabled
        );
        // 任务族 03 阶段 06 只建立 manifest-driven 资源与诊断预览；具体玩法仍由下方
        // owner-local projection species 显式注册，资源 facade 本身不创建实体。
        _sanityResources = new SanitySmapiResourceService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanitySystemEnabled
        );
        // Physical XNA audio is process-shared. This single coordinator consumes owner/screen
        // tier claims and borrows effects from the already-created stage-03 resource owner.
        _sanityAudio = new SanitySmapiAudioService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanityResources
        );
        // 视觉服务只消费既有 tier/effective/resource seam。版本受控 adapter 复用各
        // Game1.instanceId 的 screen/uiScreen 最终合成，idle 只持有可撤销负 token。
        _sanityVisual = new SanitySmapiVisualService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanityResources,
            _sanityEvents.EffectiveSanityProvider,
            _sanityVisualEffectsEnabled
        );
        // Location 规则只在启动时读取一次；加载失败时 catalog 自身 fail closed，光照服务
        // 仍可提供有 reason 的 Dim/Fallback 诊断，而不会猜测地点或授权 PitchBlack。
        var environmentLightLocationRules = LoadEnvironmentLightLocationRules(helper);
        _environmentLightSampler = new SmapiEnvironmentLightFinalVisibilitySampler(
            helper,
            Monitor
        );
        // 阶段 03 的无伤害光照服务先于需要消费其只读结果的物种创建；分类和 15-tick
        // cache 仍由该服务唯一拥有，DarkHand/Watcher 不建立第二套扫描或危险光照规则。
        _environmentLightService = new EnvironmentLightService(
            new SmapiEnvironmentLightSnapshotProvider(
                new KnownInactiveEnvironmentNightVisionProvider(),
                environmentLightLocationRules,
                _environmentLightSampler
            ),
            new EnvironmentLightClassifier(),
            () => Game1.ticks
        );
        IDarknessDamageModeResolver darknessDamageModeResolver = _canWriteConfig
            ? new TypedDarknessDamageModeResolver(_configurationRuntime.Resolver)
            : new UnavailableDarknessDamageModeResolver();
        IJunimoBlessingResolver junimoBlessingResolver = _canWriteConfig
            ? new TypedJunimoBlessingResolver(_configurationRuntime.Resolver)
            : new UnavailableJunimoBlessingResolver();
        _environmentLightMultiplayer =
            new SmapiEnvironmentLightMultiplayerCoordinator(
                helper,
                Monitor,
                ModManifest.UniqueID,
                ModManifest.Version.ToString(),
                _timeApi,
                _sanityLifecycle,
                _environmentLightService,
                environmentLightLocationRules,
                _sanityAudio,
                () =>
                {
                    if (
                        !_canWriteConfig
                        || !_configurationRuntime.Fingerprint.IsAvailable
                    )
                    {
                        return EnvironmentLightConfigIdentity.Unavailable;
                    }
                    return new EnvironmentLightConfigIdentity(
                        _configurationRuntime.Registry.SchemaVersion,
                        _configurationRuntime.Fingerprint.FullHash
                    );
                }
            );
        // Countdown and settlement share the same typed mode resolver. A runtime mode change is
        // therefore observed before the old request can reach a different damage operation.
        _darknessAttack = new SmapiDarknessAttackService(
            helper,
            Monitor,
            _timeApi,
            _sanityLifecycle,
            _environmentLightService,
            _sanityResources,
            _sanityAudio,
            darknessDamageModeResolver,
            _environmentLightMultiplayer,
            _environmentLightMultiplayer
        );
        _darknessAttackResolution = new SmapiDarknessAttackResolutionService(
            helper,
            Monitor,
            _sanityLifecycle,
            _darknessAttack,
            darknessDamageModeResolver,
            _sanityResources
        );
        // Ordinary committed sleep and confirmed kill-screen recovery have their own narrow
        // engine adapters. They only capture a reason or settle 50% of the current Sanity max;
        // the 2:00 prefix remains the sole owner of the special darkness flow.
        _passOutReasonService = new SmapiPassOutReasonService(
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _passOutReasonLedger
        );
        // The 2:00 adapter consumes the same typed mode, location result, lifecycle authority,
        // event overlay, audio coordinator and 10-03 Reduce receipt seam. It owns no duplicate
        // config, location, health-floor, Sanity, session or physical-audio authority.
        _sanityTwoAmPassOut = new SmapiSanityTwoAmSpecialDeathService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _passOutReasonService,
            darknessDamageModeResolver,
            junimoBlessingResolver,
            _environmentLightService,
            environmentLightLocationRules,
            _darknessAttackResolution,
            _sanityEvents,
            _sanityAudio
        );
        // 共享敌对态先注册为 owner-local 转换的唯一主机 sink。真实 Monster 只有在
        // NetCollection roundtrip、同版本 peer、共享视觉与 location 落点均成立时才生成。
        _hostileShadowHost = new SmapiHostileShadowHost(
            helper,
            Monitor,
            ModManifest.UniqueID,
            ModManifest.Version.ToString(),
            _timeApi,
            _sanityLifecycle,
            () =>
            {
                if (!_canWriteConfig || !_configurationRuntime.Fingerprint.IsAvailable)
                    return HostileShadowConfigFingerprintSnapshot.Unavailable;
                return new HostileShadowConfigFingerprintSnapshot(
                    _configurationRuntime.Registry.SchemaVersion,
                    _configurationRuntime.Fingerprint.FullHash
                );
            },
            _canWriteConfig ? _configurationRuntime.Resolver : null,
            _sanitySystemEnabled,
            _sanityResources
        );
        IDarkHandModeResolver darkHandModeResolver = _canWriteConfig
            ? new TypedConfigDarkHandModeResolver(_configurationRuntime.Resolver)
            : new UnavailableDarkHandModeResolver();
        // Fire and machine catalogs load once before the projection species is registered. The
        // transaction coordinator then binds the existing hostile-shadow lease authority and
        // exposes the only production owner-local target observer.
        _darkHandFireThief = new SmapiDarkHandFireThiefService(
            helper,
            Monitor,
            _sanityLifecycle
        );
        _darkHandHarassment = new SmapiDarkHandHarassmentService(
            helper,
            Monitor,
            _sanityLifecycle
        );
        _darkHandThief = new SmapiDarkHandThiefService(
            Monitor,
            _sanityLifecycle,
            _darkHandHarassment.Catalog,
            _darkHandHarassment.Evidence
        );
        _darkHandLeaseCoordinator = new SmapiDarkHandLeaseCoordinatorService(
            Monitor,
            _sanityLifecycle,
            _hostileShadowHost,
            darkHandModeResolver,
            () =>
            {
                if (
                    !_canWriteConfig
                    || !_configurationRuntime.Fingerprint.IsAvailable
                )
                {
                    return DarkHandRuntimeConfigIdentity.Unavailable;
                }
                return new DarkHandRuntimeConfigIdentity(
                    _configurationRuntime.Registry.SchemaVersion,
                    _configurationRuntime.Fingerprint.FullHash
                );
            },
            _darkHandFireThief,
            _darkHandHarassment,
            _darkHandThief
        );
        // 通用 host 仍只持有 mod-private owner-local 投影；具体物种必须显式注册。
        _harmlessProjectionHost = new SmapiHarmlessProjectionHost(
            helper,
            Monitor,
            _timeApi,
            _sanityLifecycle,
            _sanityResources,
            _hostileShadowHost
        );
        var mrSkittsBehavior = new MrSkittsProjectionBehavior();
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                MrSkittsProjectionContract.CreatePolicy(),
                new MrSkittsProjectionRenderer(),
                mrSkittsBehavior,
                out var mrSkittsRegistrationReason
            )
        )
        {
            // 注册失败时 species 保持 disabled；不能回退旧 Critter 或共享世界集合。
            Monitor.Log(
                $"Mr.Skitts owner-local projection registration failed closed ({mrSkittsRegistrationReason}).",
                LogLevel.Error
            );
        }
        var darkHandBehavior = new DarkHandProjectionBehavior(
            darkHandModeResolver,
            new SmapiDarkHandEnvironmentLightProbe(_environmentLightService, _timeApi),
            _darkHandLeaseCoordinator
        );
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                DarkHandProjectionContract.CreatePolicy(),
                new DarkHandProjectionRenderer(),
                darkHandBehavior,
                out var darkHandRegistrationReason
            )
        )
        {
            // 失败时只禁用 owner-local DarkHand；不得回退旧 Critter 或触碰共享对象。
            Monitor.Log(
                $"DarkHand owner-local projection registration failed closed ({darkHandRegistrationReason}).",
                LogLevel.Error
            );
        }
        var darkWatcherBehavior = new DarkWatcherProjectionBehavior(
            new SmapiDarkWatcherEnvironmentLightProbe(
                _environmentLightService,
                _timeApi
            )
        );
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                DarkWatcherProjectionContract.CreatePolicy(),
                new DarkWatcherProjectionRenderer(),
                darkWatcherBehavior,
                out var darkWatcherRegistrationReason
            )
        )
        {
            // 失败时只禁用 owner-local Watcher；旧 Critter 不会成为隐式回退路径。
            Monitor.Log(
                $"DarkWatcher owner-local projection registration failed closed ({darkWatcherRegistrationReason}).",
                LogLevel.Error
            );
        }
        var eyesBehavior = new EyesProjectionBehavior(
            new SmapiEyesEnvironmentLightProbe(
                _environmentLightService,
                _timeApi
            )
        );
        if (
            !_harmlessProjectionHost.RegisterSpecies(
                EyesProjectionContract.CreatePolicy(),
                new EyesProjectionRenderer(),
                eyesBehavior,
                out var eyesRegistrationReason
            )
        )
        {
            // 失败时只禁用 owner-local Eyes；资源或光照不可用不能触发旧 Critter 回退。
            Monitor.Log(
                $"Eyes owner-local projection registration failed closed ({eyesRegistrationReason}).",
                LogLevel.Error
            );
        }
        // The two 50% shadow appearances use the dedicated owner-shared permit lane. Keeping this
        // registration separate proves the ordinary four species cannot consume that permit.
        var shadowProjectionRenderer = new ShadowCreatureHarmlessProjectionRenderer();
        foreach (var policy in ShadowCreatureHarmlessProjectionCatalog.Policies)
        {
            if (
                _harmlessProjectionHost.RegisterShadowCreatureSpecies(
                    policy,
                    shadowProjectionRenderer,
                    out var shadowRegistrationReason
                )
            )
            {
                continue;
            }

            Monitor.Log(
                $"Shadow harmless projection registration failed closed (species={policy.SpeciesId}, reason={shadowRegistrationReason}).",
                LogLevel.Error
            );
        }
        // The forage presentation remains private to the current owner screen. Its shared startup
        // catalog now drives both the exact ground-object draw projection and host pickup authority.
        _forageVisualProjection = new SmapiForageVisualProjectionService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _sanityResources
        );
        // The same startup catalog drives projection and host authority. The exact checkAction
        // adapter still fails closed independently if its version/signature/IL owner gate drifts.
        _foragePickupReplacement = new SmapiForagePickupReplacementService(
            helper,
            Monitor,
            ModManifest.UniqueID,
            _sanityLifecycle,
            _forageVisualProjection.ReplacementCatalog
        );
        _environmentLightDebugOverlay = new SmapiEnvironmentLightDebugOverlay(
            helper,
            Monitor,
            _timeApi,
            _sanityLifecycle,
            _environmentLightService,
            _darknessAttack,
            _darknessAttackResolution
        );
    }

    private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
    {
        var runtime = _canWriteConfig ? _configurationRuntime : null;
        ModConfigMenuRegistrar.Register(
            Helper,
            Monitor,
            ModManifest,
            runtime,
            saveCompleted: OnConfigMenuSaveCompleted
        );
        ModContentPatcherTokenRegistrar.Register(
            Helper,
            Monitor,
            ModManifest,
            runtime
        );
    }

    private ModConfig ReadModConfig(IModHelper helper)
    {
        try
        {
            var load = ConfigurationRuntime.Load(
                Path.Combine(
                    helper.DirectoryPath,
                    "Asset",
                    "Config",
                    "config-options.json"
                ),
                Path.Combine(helper.DirectoryPath, "config.json")
            );
            foreach (var diagnostic in load.Schema.Diagnostics)
            {
                Monitor.Log(
                    $"Config schema option was skipped ({diagnostic}).",
                    LogLevel.Warn
                );
            }

            if (!load.IsAvailable || load.Runtime is null)
            {
                _canWriteConfig = false;
                Monitor.Log(
                    $"Configuration is fail-closed ({load.Reason}); using safe defaults without writing config.json.",
                    LogLevel.Error
                );
                return new ModConfig();
            }

            _configurationRuntime = load.Runtime;
            _canWriteConfig = true;
            var config = new ModConfig();
            var music = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableDawnDuskMusic
            );
            if (music.HasValue)
            {
                config.LoadEnableDawnDuskMusic(music.Value);
            }
            else
            {
                Monitor.Log(
                    $"EnableDawnDuskMusic is unavailable ({music.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var sanitySystem = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableSanitySystem
            );
            if (sanitySystem.HasValue)
            {
                _sanitySystemEnabled = sanitySystem.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableSanitySystem is unavailable ({sanitySystem.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            var sanityVisualEffects = _configurationRuntime.Resolver.GetBoolean(
                ConfigKeys.EnableSanityVisualEffects
            );
            if (sanityVisualEffects.HasValue)
            {
                _sanityVisualEffectsEnabled = sanityVisualEffects.Value;
            }
            else
            {
                Monitor.Log(
                    $"EnableSanityVisualEffects is unavailable ({sanityVisualEffects.Reason}); using its safe runtime default without overwriting the raw value.",
                    LogLevel.Warn
                );
            }

            LogFingerprint();
            return config;
        }
        catch (Exception ex)
        {
            _canWriteConfig = false;
            Monitor.Log(
                $"Failed to initialize configuration; using safe defaults without overwriting config.json. {ex.Message}",
                LogLevel.Error
            );
            return new ModConfig();
        }
    }

    private EnvironmentLightLocationRuleCatalog LoadEnvironmentLightLocationRules(
        IModHelper helper
    )
    {
        var path = Path.Combine(
            helper.DirectoryPath,
            EnvironmentLightLocationRuleCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        try
        {
            var load = EnvironmentLightLocationRuleCatalog.Load(
                File.ReadAllText(path)
            );
            if (!load.IsAvailable)
            {
                Monitor.Log(
                    $"Environment-light location rules are unavailable ({load.Reason}); classification will remain Dim/Fallback.",
                    LogLevel.Error
                );
            }
            else
            {
                Monitor.Log(
                    $"Environment-light location rules loaded (version={load.Catalog.ContractVersion}, rules={load.Catalog.Count}).",
                    LogLevel.Debug
                );
            }
            return load.Catalog;
        }
        catch (Exception ex)
        {
            const string reason = "environment-light.location-rules-file-read-failed";
            Monitor.Log(
                $"Environment-light location rules are unavailable ({reason}); classification will remain Dim/Fallback. {ex.Message}",
                LogLevel.Error
            );
            return EnvironmentLightLocationRuleCatalog.Unavailable(reason);
        }
    }

    private void OnConfigMenuSaveCompleted(FlatConfigSaveResult save)
    {
        if (!save.Success)
            return;

        if (
            string.Equals(
                save.Reason,
                "config.write-succeeded-temp-cleanup-failed",
                StringComparison.Ordinal
            )
        )
        {
            Monitor.Log(
                "config.json was saved, but its bounded temporary file could not be removed (config.write-succeeded-temp-cleanup-failed).",
                LogLevel.Warn
            );
        }

        ApplySavedConfiguration();
        LogFingerprint();
    }

    private void ApplySavedConfiguration()
    {
        var savedMusic = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableDawnDuskMusic
        );
        if (savedMusic.HasValue)
        {
            _config.LoadEnableDawnDuskMusic(savedMusic.Value);
            MusicManager.SetEnabled(savedMusic.Value);
        }
        else
        {
            Monitor.Log(
                $"EnableDawnDuskMusic remains unavailable after GMCM save ({savedMusic.Reason}); the active music state was left unchanged.",
                LogLevel.Warn
            );
        }

        var savedSanitySystem = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableSanitySystem
        );
        if (savedSanitySystem.HasValue)
        {
            _sanitySystemEnabled = savedSanitySystem.Value;
            // Hostile entities must observe the explicit Disabled boundary before tier exits can
            // clean them under a less specific danger-exit reason.
            _hostileShadowHost?.SetEnabled(savedSanitySystem.Value);
            if (!savedSanitySystem.Value)
                _passOutReasonLedger?.Clear();
            _sanityLifecycle.ApplyConfiguredState(savedSanitySystem.Value);
            if (savedSanitySystem.Value)
                _sanityEvents?.SetEnabled(true);
            else
                _sanityEvents?.SetEnabled(false);
            _sanityResources?.SetEnabled(savedSanitySystem.Value);
        }
        else
        {
            Monitor.Log(
                $"EnableSanitySystem remains unavailable after GMCM save ({savedSanitySystem.Reason}); the active Sanity system state was left unchanged.",
                LogLevel.Warn
            );
        }

        var savedSanityVisualEffects = _configurationRuntime.Resolver.GetBoolean(
            ConfigKeys.EnableSanityVisualEffects
        );
        if (savedSanityVisualEffects.HasValue)
        {
            _sanityVisualEffectsEnabled = savedSanityVisualEffects.Value;
            _sanityVisual?.SetEnabled(savedSanityVisualEffects.Value);
        }
        else
        {
            Monitor.Log(
                $"EnableSanityVisualEffects remains unavailable after GMCM save ({savedSanityVisualEffects.Reason}); the active visual state was left unchanged.",
                LogLevel.Warn
            );
        }
    }

    private void LogFingerprint()
    {
        var fingerprint = _configurationRuntime.Fingerprint;
        if (!fingerprint.IsAvailable)
        {
            Monitor.Log(
                $"Gameplay config fingerprint is {fingerprint.PublicIdentifier}.",
                LogLevel.Warn
            );
            return;
        }

        Monitor.Log(
            $"Gameplay config fingerprint: {fingerprint.PublicIdentifier}.",
            LogLevel.Debug
        );
        // 完整值仅进入诊断日志；它不会携带具体配置，也不参与同步、覆盖或拒绝联机。
        Monitor.Log(
            $"Gameplay config fingerprint full SHA-256: {fingerprint.FullHash}.",
            LogLevel.Trace
        );
    }
}
