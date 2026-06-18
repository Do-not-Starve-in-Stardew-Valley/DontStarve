using System;
using DontStarve.Buff;
using DontStarve.Config;
using DontStarve.Display;
using DontStarve.Music;
using DontStarve.Player;
using DontStarve.Recipe;
using DontStarve.Resource;
using DontStarve.Time;
using StardewModdingAPI;

namespace DontStarve;

internal class ModEntry : Mod
{
    // 全局只维护这一份内部分钟时间服务；Hunger、Sanity、Buff、Display 都从这里接收同一个时间源。
    private readonly TimeApi _timeApi = new();
    private ModConfig _config = new();

    // 配置文件读坏时保持 fail-closed：允许本次用默认值运行，但不覆盖玩家原来的坏文件。
    private bool _canWriteConfig = true;

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
        ModConfigMenuRegistrar.Register(
            helper,
            Monitor,
            ModManifest,
            _config,
            saveConfig: () => SaveModConfig(helper),
            applyConfig: () => MusicManager.SetEnabled(_config.EnableDawnDuskMusic));
        RecipeCategoryDisplayService.Initialize(helper, Monitor, ModManifest.UniqueID);
        BuffManager.Initialize(helper, _timeApi);
        StatManager.Initialize(helper, _timeApi);
        DisplayManager.Initialize(helper, _timeApi);
    }

    private ModConfig ReadModConfig(IModHelper helper)
    {
        try
        {
            _canWriteConfig = true;
            return helper.ReadConfig<ModConfig>();
        }
        catch (Exception ex)
        {
            _canWriteConfig = false;
            Monitor.Log($"Failed to read config.json; using safe defaults without overwriting the existing file. {ex.Message}", LogLevel.Warn);
            return new ModConfig();
        }
    }

    private void SaveModConfig(IModHelper helper)
    {
        if (!_canWriteConfig)
        {
            Monitor.Log("Skipped writing config.json because it failed to load earlier. Fix or remove the file to recreate defaults.", LogLevel.Warn);
            return;
        }

        helper.WriteConfig(_config);
    }
}
