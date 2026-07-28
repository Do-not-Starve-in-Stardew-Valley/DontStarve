using System.Text.Json;
using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

internal static class ConfigTestData
{
    internal static string ShippedSchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "Config", "config-options.json");

    internal static ConfigRegistry LoadShippedRegistry()
    {
        var result = ConfigSchemaLoader.LoadFromFile(ShippedSchemaPath);
        Assert.True(result.IsAvailable, result.Reason);
        Assert.NotNull(result.Registry);
        return result.Registry!;
    }

    internal static string Schema(params string[] options)
    {
        return "{\n"
            + "  \"SchemaVersion\": 1,\n"
            + "  \"Options\": [\n"
            + string.Join(",\n", options)
            + "\n  ]\n"
            + "}";
    }

    internal static string BooleanOption(
        string key,
        bool defaultValue,
        bool exposeToContentPatcher,
        bool affectsWorldState,
        int order,
        string? extraProperty = null
    )
    {
        var extra = string.IsNullOrEmpty(extraProperty) ? string.Empty : $",\n{extraProperty}";
        return "{\n"
            + $"  \"Key\": {JsonSerializer.Serialize(key)},\n"
            + "  \"Type\": \"Boolean\",\n"
            + $"  \"Default\": {defaultValue.ToString().ToLowerInvariant()},\n"
            + $"  \"ExposeToContentPatcher\": {exposeToContentPatcher.ToString().ToLowerInvariant()},\n"
            + $"  \"AffectsWorldState\": {affectsWorldState.ToString().ToLowerInvariant()},\n"
            + "  \"SectionI18n\": \"config.test.section\",\n"
            + "  \"NameI18n\": \"config.test.name\",\n"
            + "  \"TooltipI18n\": \"config.test.tooltip\",\n"
            + $"  \"Order\": {order}{extra}\n"
            + "}";
    }

    internal static string EnumOption(
        string key,
        string defaultValue,
        IReadOnlyList<string> allowedValues,
        bool exposeToContentPatcher,
        bool affectsWorldState,
        int order,
        string? extraProperty = null
    )
    {
        var allowed = string.Join(", ", allowedValues.Select(value => JsonSerializer.Serialize(value)));
        var extra = string.IsNullOrEmpty(extraProperty) ? string.Empty : $",\n{extraProperty}";
        return "{\n"
            + $"  \"Key\": {JsonSerializer.Serialize(key)},\n"
            + "  \"Type\": \"Enum\",\n"
            + $"  \"Default\": {JsonSerializer.Serialize(defaultValue)},\n"
            + $"  \"AllowedValues\": [{allowed}],\n"
            + $"  \"ExposeToContentPatcher\": {exposeToContentPatcher.ToString().ToLowerInvariant()},\n"
            + $"  \"AffectsWorldState\": {affectsWorldState.ToString().ToLowerInvariant()},\n"
            + "  \"SectionI18n\": \"config.test.section\",\n"
            + "  \"NameI18n\": \"config.test.name\",\n"
            + "  \"TooltipI18n\": \"config.test.tooltip\",\n"
            + $"  \"Order\": {order}{extra}\n"
            + "}";
    }
}

internal sealed class MemoryFlatConfigFileAccess : IFlatConfigFileAccess
{
    internal MemoryFlatConfigFileAccess(string? content, bool isMissing = false)
    {
        Content = content;
        IsMissing = isMissing;
    }

    internal string? Content { get; set; }

    internal bool IsMissing { get; set; }

    internal bool FailWrite { get; set; }

    internal int WriteCount { get; private set; }

    internal int ReadCount { get; private set; }

    internal string? LastWritten { get; private set; }

    public ConfigTextReadResult Read()
    {
        ReadCount++;
        if (IsMissing)
            return ConfigTextReadResult.Missing();

        return ConfigTextReadResult.Available(Content ?? string.Empty);
    }

    public ConfigTextWriteResult WriteAtomically(string content)
    {
        WriteCount++;
        LastWritten = content;
        if (FailWrite)
            return ConfigTextWriteResult.Error("config.write-failed");

        Content = content;
        IsMissing = false;
        return ConfigTextWriteResult.Written();
    }
}
