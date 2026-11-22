#nullable enable

using System;
using System.Collections.Generic;
using Common.ConfigurationServices;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Config;

internal static class ModConfigMenuRegistrar
{
    private const string GenericModConfigMenuId = "spacechase0.GenericModConfigMenu";
    private const string MinimumGenericModConfigMenuVersion = "1.16.0";

    internal static void Register(
        IModHelper helper,
        IMonitor monitor,
        IManifest manifest,
        ConfigurationRuntime? runtime,
        Action<FlatConfigSaveResult> saveCompleted
    )
    {
        var reportedReasons = new HashSet<string>(StringComparer.Ordinal);

        void ReportOnce(string reason)
        {
            if (!reportedReasons.Add(reason))
                return;

            var level = string.Equals(
                reason,
                "gmcm.not-installed",
                StringComparison.Ordinal
            )
                ? LogLevel.Debug
                : LogLevel.Warn;
            monitor.Log($"[DontStarve][GMCM] {reason}", level);
        }

        if (runtime is null)
        {
            ReportOnce("gmcm.config-unavailable");
            return;
        }

        var installedManifest = helper.ModRegistry.Get(GenericModConfigMenuId)?.Manifest;
        if (installedManifest is null)
        {
            ReportOnce("gmcm.not-installed");
            return;
        }

        // Stage 03 only mirrors the locally verified 1.16.0 surface; older APIs fail closed.
        if (installedManifest.Version.IsOlderThan(MinimumGenericModConfigMenuVersion))
        {
            ReportOnce("gmcm.version-too-old");
            return;
        }

        var configMenu = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(
            GenericModConfigMenuId
        );
        if (configMenu is null)
        {
            ReportOnce("gmcm.api-mismatch");
            return;
        }

        SchemaDrivenConfigMenuRegistrar.Register(
            runtime,
            new SmapiConfigMenuRegistrationApi(configMenu, manifest),
            new SmapiConfigMenuTranslationProvider(helper.Translation),
            ReportOnce,
            saveCompleted
        );
    }

    private sealed class SmapiConfigMenuRegistrationApi : IConfigMenuRegistrationApi
    {
        private readonly IGenericModConfigMenuApi api;
        private readonly IManifest manifest;

        internal SmapiConfigMenuRegistrationApi(
            IGenericModConfigMenuApi api,
            IManifest manifest
        )
        {
            this.api = api;
            this.manifest = manifest;
        }

        public void Register(Action reset, Action save)
        {
            api.Register(manifest, reset, save);
        }

        public void AddPage(string pageId, Func<string> getTitle)
        {
            api.AddPage(manifest, pageId, getTitle);
        }

        public void AddPageLink(string pageId, Func<string> getText)
        {
            api.AddPageLink(manifest, pageId, getText);
        }

        public void AddBoolean(
            string fieldId,
            Func<bool> getValue,
            Action<bool> setValue,
            Func<string> getName,
            Func<string> getTooltip
        )
        {
            api.AddBoolOption(
                manifest,
                getValue,
                setValue,
                getName,
                getTooltip,
                fieldId
            );
        }

        public void AddEnum(
            string fieldId,
            Func<string> getValue,
            Action<string> setValue,
            Func<string> getName,
            Func<string> getTooltip,
            string[] allowedValues,
            Func<string, string> formatAllowedValue
        )
        {
            api.AddTextOption(
                manifest,
                getValue,
                setValue,
                getName,
                getTooltip,
                allowedValues,
                formatAllowedValue,
                fieldId
            );
        }

        public void AddKeybind(
            string fieldId,
            Func<string> getValue,
            Action<string> setValue,
            Func<string> getName,
            Func<string> getTooltip
        )
        {
            // Keybind 在纯逻辑层以 SButton 枚举名字符串流转；只有这里做 string ↔ SButton
            // 转换。写回永远走 SButton.ToString()，所以 config.json 里只会出现规范枚举名。
            api.AddKeybind(
                manifest,
                () => ParseButtonName(getValue()),
                button => setValue(button.ToString()),
                getName,
                getTooltip,
                fieldId
            );
        }

        public void AddNumber(
            string fieldId,
            Func<int> getValue,
            Action<int> setValue,
            Func<string> getName,
            Func<string> getTooltip,
            int min,
            int max,
            int interval,
            Func<int, string> formatValue
        )
        {
            // GMCM 1.16 的 AddNumberOption(int)：min/max 齐备时绘制滑轮，interval 为步长。
            api.AddNumberOption(
                manifest,
                getValue,
                setValue,
                getName,
                getTooltip,
                min,
                max,
                interval,
                formatValue,
                fieldId
            );
        }

        private static SButton ParseButtonName(string raw)
        {
            // 契约：Enum.TryParse(ignoreCase:true) 解析失败一律回落 None，绝不抛异常；
            // Enum.IsDefined 按名称拦截 "42" 这类 TryParse 可按数值成功、却不是枚举名的输入。
            // GMCM 写回的总是规范枚举名，因此手改 config.json 的小写/垃圾值同样 fail-safe 回
            // 落 None（重新绑定即可恢复），不会崩溃或产生错误按键。
            if (
                !string.IsNullOrWhiteSpace(raw)
                && Enum.TryParse(raw, ignoreCase: true, out SButton button)
                && Enum.IsDefined(typeof(SButton), raw)
            )
            {
                return button;
            }

            return SButton.None;
        }
    }

    private sealed class SmapiConfigMenuTranslationProvider
        : IConfigMenuTranslationProvider
    {
        private readonly ITranslationHelper translations;

        internal SmapiConfigMenuTranslationProvider(ITranslationHelper translations)
        {
            this.translations = translations;
        }

        public bool TryGet(string key, out string value)
        {
            var translation = translations.Get(key);
            if (!translation.HasValue())
            {
                value = string.Empty;
                return false;
            }

            value = translation.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }
    }
}
