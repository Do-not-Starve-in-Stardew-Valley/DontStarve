using System;
using Common.ConfigurationServices;
using StardewModdingAPI;

namespace DontStarve.Config;

internal static class ModConfigMenuRegistrar
{
    internal static void Register(
        IModHelper helper,
        IMonitor monitor,
        IManifest manifest,
        ModConfig config,
        Action saveConfig,
        Action applyConfig)
    {
        var configMenu = IntegrationHelper.GetGenericModConfigMenu(helper.ModRegistry, monitor);
        if (configMenu == null)
            return;

        try
        {
            // GMCM 是可选集成：存在时即时应用音乐开关，不存在时 config.json 仍然生效。
            configMenu.Register(
                manifest,
                reset: () =>
                {
                    config.EnableDawnDuskMusic = true;
                    applyConfig();
                },
                save: saveConfig);

            configMenu.AddSectionTitle(
                manifest,
                text: () => helper.Translation.Get("config.music.section").ToString());

            configMenu.AddBoolOption(
                manifest,
                getValue: () => config.EnableDawnDuskMusic,
                setValue: value =>
                {
                    config.EnableDawnDuskMusic = value;
                    applyConfig();
                },
                name: () => helper.Translation.Get("config.enable-dawn-dusk-music.name").ToString(),
                tooltip: () => helper.Translation.Get("config.enable-dawn-dusk-music.tooltip").ToString(),
                fieldId: "EnableDawnDuskMusic");
        }
        catch (Exception ex)
        {
            monitor.Log($"Failed to register Generic Mod Config Menu options: {ex.Message}", LogLevel.Warn);
        }
    }
}
