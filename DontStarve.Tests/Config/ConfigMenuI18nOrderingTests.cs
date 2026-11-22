#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DontStarve.Config;
using Xunit;

namespace DontStarve.Tests.Config;

// 锁住 i18n 文件里 config.* 键的物理顺序 == 游戏内 GMCM 菜单的实际显示顺序。
// 这是翻译工作面顺序：翻译者必须能从上往下对着游戏内菜单逐条翻译，
// 因此 i18n 物理顺序漂移等同于功能缺陷，而不是格式偏好。
// 排序契约与 SchemaDrivenConfigMenuRegistrar 的运行时行为一致：
//   1. 父页面按 ParentPageOrder 升序，并列时按页面 i18n 键字典序（SortParentPageIds）；
//   2. 同一父页面下的子页面按 PageOrder 升序，并列时按页面 i18n 键字典序（SortChildPageIds）；
//   3. 页面内选项按 Order 升序（ConfigRegistry.Options 已按 Order 全局排序）；
//   4. 枚举值键按 AllowedValues 顺序（菜单下拉项顺序，GetEnumValueI18nKey）。
// 往 config-options.json 新增选项时，必须把对应 i18n 键按上述顺序插入两个 i18n 文件；
// 顺序漂移时本测试失败并指出第一个不一致的键。
public sealed class ConfigMenuI18nOrderingTests
{
    [Fact]
    public void ConfigI18nKeysFollowInGameMenuOrder()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        AssertConfigBlockOrder(ReadPhysicalKeys("default.json"), registry, "default.json");
        AssertConfigBlockOrder(ReadPhysicalKeys("zh.json"), registry, "zh.json");
    }

    private static void AssertConfigBlockOrder(
        IReadOnlyList<string> physicalKeys,
        ConfigRegistry registry,
        string fileName
    )
    {
        var actual = new List<string>();
        foreach (var key in physicalKeys)
        {
            if (key.StartsWith("config.", StringComparison.Ordinal))
                actual.Add(key);
        }

        var expected = BuildExpectedConfigKeyOrder(registry);

        for (var index = 0; index < Math.Min(actual.Count, expected.Count); index++)
        {
            Assert.True(
                string.Equals(actual[index], expected[index], StringComparison.Ordinal),
                $"{fileName}: config key #{index} should be '{expected[index]}' but is "
                + $"'{actual[index]}'. The i18n config block must follow the in-game menu "
                + "order: parent pages by ParentPageOrder, child pages by PageOrder, "
                + "options by Order, enum values by AllowedValues."
            );
        }

        Assert.True(
            actual.Count == expected.Count,
            $"{fileName}: config key count should be {expected.Count} but is {actual.Count}."
        );
    }

    // 复刻菜单注册顺序，产出期望的 i18n 键序列。
    // 页面排序语义与 SchemaDrivenConfigMenuRegistrar 的 GetParentPageOrder /
    // GetPageOrder 相同：取 options（已按 Order 排序）中第一个匹配项的排序值。
    private static List<string> BuildExpectedConfigKeyOrder(ConfigRegistry registry)
    {
        var options = registry.Options;

        var parentPages = new List<string>();
        var seenParents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (seenParents.Add(option.ParentPageI18n))
                parentPages.Add(option.ParentPageI18n);
        }

        parentPages.Sort(
            (left, right) =>
            {
                var order = ParentPageOrderOf(options, left)
                    .CompareTo(ParentPageOrderOf(options, right));
                return order != 0
                    ? order
                    : StringComparer.Ordinal.Compare(left, right);
            }
        );

        var expected = new List<string>();
        foreach (var parentPage in parentPages)
        {
            expected.Add(parentPage);

            var childPages = new List<string>();
            var seenChildren = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                if (
                    string.Equals(option.ParentPageI18n, parentPage, StringComparison.Ordinal)
                    && seenChildren.Add(option.PageI18n)
                )
                {
                    childPages.Add(option.PageI18n);
                }
            }

            childPages.Sort(
                (left, right) =>
                {
                    var order = ChildPageOrderOf(options, parentPage, left)
                        .CompareTo(ChildPageOrderOf(options, parentPage, right));
                    return order != 0
                        ? order
                        : StringComparer.Ordinal.Compare(left, right);
                }
            );

            foreach (var childPage in childPages)
            {
                expected.Add(childPage);

                foreach (var option in options)
                {
                    if (
                        !string.Equals(
                            option.ParentPageI18n,
                            parentPage,
                            StringComparison.Ordinal
                        )
                        || !string.Equals(option.PageI18n, childPage, StringComparison.Ordinal)
                    )
                    {
                        continue;
                    }

                    expected.Add(option.NameI18n);
                    expected.Add(option.TooltipI18n);
                    foreach (var allowedValue in option.AllowedValues)
                    {
                        expected.Add(
                            SchemaDrivenConfigMenuRegistrar.GetEnumValueI18nKey(
                                option,
                                allowedValue
                            )
                        );
                    }
                }
            }
        }

        return expected;
    }

    private static int ParentPageOrderOf(
        IReadOnlyList<ConfigOptionDefinition> options,
        string pageId
    )
    {
        foreach (var option in options)
        {
            if (string.Equals(option.ParentPageI18n, pageId, StringComparison.Ordinal))
                return option.ParentPageOrder;
        }

        return int.MaxValue;
    }

    private static int ChildPageOrderOf(
        IReadOnlyList<ConfigOptionDefinition> options,
        string parentPageId,
        string pageId
    )
    {
        foreach (var option in options)
        {
            if (
                string.Equals(option.ParentPageI18n, parentPageId, StringComparison.Ordinal)
                && string.Equals(option.PageI18n, pageId, StringComparison.Ordinal)
            )
            {
                return option.PageOrder;
            }
        }

        return int.MaxValue;
    }

    // 按 JSON 物理顺序读键名（Dictionary 不保证枚举顺序，必须直接读文件）。
    private static List<string> ReadPhysicalKeys(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "I18n", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var keys = new List<string>();
        foreach (var property in document.RootElement.EnumerateObject())
            keys.Add(property.Name);

        return keys;
    }
}
