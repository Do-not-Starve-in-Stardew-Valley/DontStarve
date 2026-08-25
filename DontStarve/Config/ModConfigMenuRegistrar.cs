#nullable enable

using System;
using System.Collections.Generic;
using Common.ConfigurationServices;
using StardewModdingAPI;

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

        public void AddSection(string sectionId, Func<string> getText)
        {
            api.AddSectionTitle(manifest, getText);
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
