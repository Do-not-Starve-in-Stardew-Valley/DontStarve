#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Config;

/// <summary>
/// Minimal GMCM surface used by the schema-driven registrar. The SMAPI/GMCM adapter lives in
/// <see cref="ModConfigMenuRegistrar"/>; tests can exercise this layer without loading the game.
/// </summary>
internal interface IConfigMenuRegistrationApi
{
    void Register(Action reset, Action save);

    void AddSection(string sectionId, string text);

    void AddBoolean(
        string fieldId,
        Func<bool> getValue,
        Action<bool> setValue,
        string name,
        string tooltip
    );

    void AddEnum(
        string fieldId,
        Func<string> getValue,
        Action<string> setValue,
        string name,
        string tooltip,
        string[] allowedValues,
        Func<string, string> formatAllowedValue
    );
}

internal interface IConfigMenuTranslationProvider
{
    bool TryGet(string key, out string value);
}

internal enum ConfigMenuRegistrationStatus
{
    Available,
    Degraded,
    Unavailable,
}

internal sealed class ConfigMenuRegistrationResult
{
    internal ConfigMenuRegistrationResult(
        ConfigMenuRegistrationStatus status,
        string reason,
        IReadOnlyCollection<string>? registeredKeys = null,
        IReadOnlyCollection<string>? diagnostics = null
    )
    {
        Status = status;
        Reason = reason;
        RegisteredKeys = new ReadOnlyCollection<string>(
            registeredKeys is null
                ? Array.Empty<string>()
                : new List<string>(registeredKeys)
        );
        Diagnostics = new ReadOnlyCollection<string>(
            diagnostics is null ? Array.Empty<string>() : new List<string>(diagnostics)
        );
    }

    internal ConfigMenuRegistrationStatus Status { get; }

    internal string Reason { get; }

    internal IReadOnlyList<string> RegisteredKeys { get; }

    internal IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// Registers the immutable schema as a GMCM form while keeping edits in a bounded, non-authoritative
/// buffer. Only Save calls the existing atomic <see cref="ConfigurationRuntime.TrySave"/> path.
/// </summary>
internal static class SchemaDrivenConfigMenuRegistrar
{
    internal static ConfigMenuRegistrationResult Register(
        ConfigurationRuntime? runtime,
        IConfigMenuRegistrationApi? menu,
        IConfigMenuTranslationProvider translations,
        Action<string> reportDiagnostic,
        Action<FlatConfigSaveResult> saveCompleted
    )
    {
        if (runtime is null)
        {
            const string reason = "gmcm.config-unavailable";
            reportDiagnostic(reason);
            return new ConfigMenuRegistrationResult(
                ConfigMenuRegistrationStatus.Unavailable,
                reason,
                diagnostics: new[] { reason }
            );
        }

        return Register(
            runtime.Registry,
            runtime.Resolver,
            runtime.TrySave,
            menu,
            translations,
            reportDiagnostic,
            saveCompleted
        );
    }

    internal static ConfigMenuRegistrationResult Register(
        ConfigRegistry registry,
        TypedConfigResolver resolver,
        Func<IReadOnlyDictionary<string, ConfigValue>, FlatConfigSaveResult> save,
        IConfigMenuRegistrationApi? menu,
        IConfigMenuTranslationProvider translations,
        Action<string> reportDiagnostic,
        Action<FlatConfigSaveResult> saveCompleted
    )
    {
        var diagnostics = new List<string>();
        var reportedDiagnostics = new HashSet<string>(StringComparer.Ordinal);

        void Report(string reason)
        {
            if (!reportedDiagnostics.Add(reason))
                return;

            diagnostics.Add(reason);
            reportDiagnostic(reason);
        }

        if (menu is null)
        {
            Report("gmcm.not-installed");
            return new ConfigMenuRegistrationResult(
                ConfigMenuRegistrationStatus.Unavailable,
                "gmcm.not-installed",
                diagnostics: diagnostics
            );
        }

        var editSession = new ConfigMenuEditSession(
            registry,
            resolver,
            save,
            Report,
            saveCompleted
        );
        try
        {
            menu.Register(editSession.Reset, editSession.Save);
        }
        catch (Exception)
        {
            Report("gmcm.api-mismatch");
            return new ConfigMenuRegistrationResult(
                ConfigMenuRegistrationStatus.Unavailable,
                "gmcm.api-mismatch",
                diagnostics: diagnostics
            );
        }

        var registeredKeys = new List<string>();
        string? currentSection = null;
        foreach (var option in registry.Options)
        {
            var section = string.Empty;
            var name = string.Empty;
            var tooltip = string.Empty;
            IReadOnlyDictionary<string, string> enumDisplayValues =
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                );
            try
            {
                if (
                    !TryResolveTranslations(
                        option,
                        translations,
                        out section,
                        out name,
                        out tooltip,
                        out enumDisplayValues
                    )
                )
                {
                    Report($"gmcm.i18n-missing:{option.Key}");
                    continue;
                }
            }
            catch (Exception)
            {
                Report($"gmcm.i18n-missing:{option.Key}");
                continue;
            }

            if (!string.Equals(currentSection, option.SectionI18n, StringComparison.Ordinal))
            {
                currentSection = option.SectionI18n;
                try
                {
                    menu.AddSection(option.SectionI18n, section);
                }
                catch (Exception)
                {
                    Report($"gmcm.section-registration-failed:{option.SectionI18n}");
                }
            }

            try
            {
                if (option.Type == ConfigOptionType.Boolean)
                {
                    menu.AddBoolean(
                        option.Key,
                        () => editSession.GetBoolean(option),
                        value => editSession.SetBoolean(option, value),
                        name,
                        tooltip
                    );
                }
                else if (option.Type == ConfigOptionType.Enum)
                {
                    var allowedValues = CopyAllowedValues(option.AllowedValues);
                    menu.AddEnum(
                        option.Key,
                        () => editSession.GetEnum(option),
                        value => editSession.SetEnum(option, value),
                        name,
                        tooltip,
                        allowedValues,
                        value =>
                            enumDisplayValues.TryGetValue(value, out var display)
                                ? display
                                : value
                    );
                }
                else
                {
                    Report($"gmcm.unsupported-type:{option.Key}");
                    continue;
                }

                registeredKeys.Add(option.Key);
            }
            catch (Exception)
            {
                // One malformed or version-incompatible field must not hide the rest of the page.
                Report($"gmcm.option-registration-failed:{option.Key}");
            }
        }

        if (registeredKeys.Count == 0)
        {
            Report("gmcm.no-options-registered");
            return new ConfigMenuRegistrationResult(
                ConfigMenuRegistrationStatus.Unavailable,
                "gmcm.no-options-registered",
                registeredKeys,
                diagnostics
            );
        }

        return new ConfigMenuRegistrationResult(
            diagnostics.Count == 0
                ? ConfigMenuRegistrationStatus.Available
                : ConfigMenuRegistrationStatus.Degraded,
            diagnostics.Count == 0 ? "gmcm.registered" : "gmcm.registered-degraded",
            registeredKeys,
            diagnostics
        );
    }

    internal static string GetEnumValueI18nKey(
        ConfigOptionDefinition option,
        string rawValue
    )
    {
        const string nameSuffix = ".name";
        var prefix = option.NameI18n.EndsWith(nameSuffix, StringComparison.Ordinal)
            ? option.NameI18n[..^nameSuffix.Length]
            : option.NameI18n;
        return $"{prefix}.value.{ToKebabCase(rawValue)}";
    }

    private static bool TryResolveTranslations(
        ConfigOptionDefinition option,
        IConfigMenuTranslationProvider translations,
        out string section,
        out string name,
        out string tooltip,
        out IReadOnlyDictionary<string, string> enumDisplayValues
    )
    {
        section = string.Empty;
        name = string.Empty;
        tooltip = string.Empty;
        enumDisplayValues = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
        );
        if (
            !translations.TryGet(option.SectionI18n, out section)
            || !translations.TryGet(option.NameI18n, out name)
            || !translations.TryGet(option.TooltipI18n, out tooltip)
        )
        {
            return false;
        }

        if (option.Type != ConfigOptionType.Enum)
            return true;

        var displays = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var allowedValue in option.AllowedValues)
        {
            if (
                !translations.TryGet(
                    GetEnumValueI18nKey(option, allowedValue),
                    out var display
                )
            )
            {
                return false;
            }

            displays.Add(allowedValue, display);
        }

        enumDisplayValues = new ReadOnlyDictionary<string, string>(displays);
        return true;
    }

    private static string[] CopyAllowedValues(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];

        return copy;
    }

    private static string ToKebabCase(string value)
    {
        var characters = new List<char>(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]))
                characters.Add('-');

            characters.Add(char.ToLowerInvariant(character));
        }

        return new string(characters.ToArray());
    }
}

internal sealed class ConfigMenuEditSession
{
    private readonly ConfigRegistry registry;
    private readonly TypedConfigResolver resolver;
    private readonly Func<IReadOnlyDictionary<string, ConfigValue>, FlatConfigSaveResult> save;
    private readonly Action<string> reportDiagnostic;
    private readonly Action<FlatConfigSaveResult> saveCompleted;
    private readonly Dictionary<string, ConfigValue> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirtyKeys = new(StringComparer.Ordinal);

    internal ConfigMenuEditSession(
        ConfigRegistry registry,
        TypedConfigResolver resolver,
        Func<IReadOnlyDictionary<string, ConfigValue>, FlatConfigSaveResult> save,
        Action<string> reportDiagnostic,
        Action<FlatConfigSaveResult> saveCompleted
    )
    {
        this.registry = registry;
        this.resolver = resolver;
        this.save = save;
        this.reportDiagnostic = reportDiagnostic;
        this.saveCompleted = saveCompleted;
        ReloadFromResolver();
    }

    internal bool GetBoolean(ConfigOptionDefinition option)
    {
        return values.TryGetValue(option.Key, out var value)
            && value.Type == ConfigOptionType.Boolean
            ? value.BooleanValue
            : option.DefaultValue.BooleanValue;
    }

    internal string GetEnum(ConfigOptionDefinition option)
    {
        return values.TryGetValue(option.Key, out var value)
            && value.Type == ConfigOptionType.Enum
            && value.EnumValue is not null
            ? value.EnumValue
            : option.DefaultValue.EnumValue ?? string.Empty;
    }

    internal void SetBoolean(ConfigOptionDefinition option, bool value)
    {
        if (option.Type != ConfigOptionType.Boolean)
        {
            reportDiagnostic($"gmcm.invalid-edit:{option.Key}");
            return;
        }

        values[option.Key] = ConfigValue.Boolean(value);
        dirtyKeys.Add(option.Key);
    }

    internal void SetEnum(ConfigOptionDefinition option, string value)
    {
        ConfigValue proposed;
        try
        {
            proposed = ConfigValue.Enum(value);
        }
        catch (ArgumentException)
        {
            reportDiagnostic($"gmcm.invalid-edit:{option.Key}");
            return;
        }

        if (option.Type != ConfigOptionType.Enum || !option.Accepts(proposed))
        {
            reportDiagnostic($"gmcm.invalid-edit:{option.Key}");
            return;
        }

        values[option.Key] = proposed;
        dirtyKeys.Add(option.Key);
    }

    internal void Reset()
    {
        foreach (var option in registry.Options)
        {
            if (
                option.Type != ConfigOptionType.Boolean
                && option.Type != ConfigOptionType.Enum
            )
            {
                continue;
            }

            values[option.Key] = option.DefaultValue;
            dirtyKeys.Add(option.Key);
        }
    }

    internal void Save()
    {
        var updates = new Dictionary<string, ConfigValue>(StringComparer.Ordinal);
        foreach (var key in dirtyKeys)
            updates.Add(key, values[key]);

        FlatConfigSaveResult result;
        try
        {
            result = save(updates);
        }
        catch (Exception)
        {
            result = FlatConfigSaveResult.Error("config.write-exception");
        }

        if (result.Success)
            ReloadFromResolver();
        else
            reportDiagnostic($"gmcm.save-failed:{result.Reason}");

        try
        {
            saveCompleted(result);
        }
        catch (Exception)
        {
            reportDiagnostic("gmcm.save-completion-failed");
        }
    }

    private void ReloadFromResolver()
    {
        values.Clear();
        dirtyKeys.Clear();
        foreach (var option in registry.Options)
        {
            if (option.Type == ConfigOptionType.Boolean)
            {
                var resolved = resolver.GetBoolean(option.Key);
                values[option.Key] = resolved.HasValue
                    ? ConfigValue.Boolean(resolved.Value)
                    : option.DefaultValue;
            }
            else if (option.Type == ConfigOptionType.Enum)
            {
                var resolved = resolver.GetEnum(option.Key);
                values[option.Key] = resolved.HasValue
                    ? ConfigValue.Enum(resolved.Value)
                    : option.DefaultValue;
            }
        }
    }
}
