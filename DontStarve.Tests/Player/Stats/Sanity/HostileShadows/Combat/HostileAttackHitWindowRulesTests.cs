using System.Text.Json.Nodes;
using DontStarve.Player.Combat.InvincibilityFrames;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

/// <summary>
/// 无敌帧抑制（20261113 冻结决议）的资源层窗口规则：窗口列表缺省折叠为单个隐式 Normal
/// 旧窗口；显式窗口必须互斥、并集恰好覆盖激活帧、策略三态合法；未知策略 fail-closed。
/// </summary>
public sealed class HostileAttackHitWindowRulesTests
{
    private static readonly int[] ActiveFrames = { 3, 4 };
    private const int AttackFrameCount = 4;

    private static SanityHostileAttackHitWindowDefinition Window(
        string id,
        int[] frames,
        string mode,
        int duration
    )
    {
        return new SanityHostileAttackHitWindowDefinition(id, frames, mode, duration);
    }

    private static bool Try(
        IReadOnlyList<SanityHostileAttackHitWindowDefinition>? windows,
        out IReadOnlyList<SanityHostileAttackHitWindowDefinition> created,
        out string reason
    )
    {
        return SanityHostileAttackHitWindowRules.TryCreateWindows(
            ActiveFrames,
            AttackFrameCount,
            windows,
            out created,
            out reason
        );
    }

    [Fact]
    public void Absent_or_empty_windows_collapse_into_one_legacy_normal_window()
    {
        Assert.True(Try(null, out var created, out var reason), reason);
        Assert.Single(created);
        Assert.Equal(SanityHostileAttackHitWindowRules.LegacyWindowId, created[0].WindowId);
        Assert.Equal(new[] { 3, 4 }, created[0].ActiveFrames);
        Assert.Equal("Normal", created[0].IFramePolicyMode);
        Assert.Equal(0, created[0].IFramePolicyDurationMilliseconds);

        Assert.True(Try(Array.Empty<SanityHostileAttackHitWindowDefinition>(), out var empty, out _), reason);
        Assert.Single(empty);
    }

    [Fact]
    public void Valid_two_window_combo_passes_validation()
    {
        var windows = new[]
        {
            Window("first", new[] { 3 }, "Strong", 0),
            Window("follow-up", new[] { 4 }, "Weak", 300),
        };

        Assert.True(Try(windows, out var created, out var reason), reason);
        Assert.Equal(2, created.Count);
        Assert.Equal("follow-up", created[1].WindowId);
    }

    [Theory]
    [InlineData("duplicate-window-id", "first", new[] { 3 }, "Strong", 0, "first", new[] { 4 }, "Strong", 0)]
    [InlineData("shared-frame", "first", new[] { 3, 4 }, "Strong", 0, "follow-up", new[] { 4 }, "Normal", 0)]
    [InlineData("duplicate-frame-in-window", "first", new[] { 3 }, "Strong", 0, "follow-up", new[] { 4, 4 }, "Normal", 0)]
    [InlineData("unknown-mode", "first", new[] { 3, 4 }, "Super", 100, "unused", new[] { 3 }, "Normal", 0)]
    [InlineData("normal-with-duration", "first", new[] { 3 }, "Normal", 50, "follow-up", new[] { 4 }, "Normal", 0)]
    [InlineData("negative-duration", "first", new[] { 3 }, "Strong", -1, "follow-up", new[] { 4 }, "Normal", 0)]
    public void Invalid_window_sets_fail_closed(string caseId, string idA, int[] framesA, string modeA, int durationA, string idB, int[] framesB, string modeB, int durationB)
    {
        var windows = new[]
        {
            Window(idA, framesA, modeA, durationA),
            Window(idB, framesB, modeB, durationB),
        };

        Assert.False(
            Try(windows, out _, out var reason),
            $"{caseId} should have been rejected"
        );
        Assert.StartsWith("resource.hostile-attack-metadata.hit-window", reason);
    }

    [Fact]
    public void Window_frames_outside_the_attack_range_are_rejected()
    {
        var windows = new[] { Window("first", new[] { 3, 9 }, "Strong", 0) };
        Assert.False(Try(windows, out _, out var reason));
        Assert.Equal("resource.hostile-attack-metadata.hit-window-frame-out-of-range", reason);
    }

    [Fact]
    public void Coverage_must_exactly_equal_active_frames()
    {
        // 漏帧：窗口只声明帧 3，帧 4 无法归属窗口。
        Assert.False(
            Try(new[] { Window("first", new[] { 3 }, "Strong", 0) }, out _, out var gap)
        );
        Assert.Equal("resource.hostile-attack-metadata.hit-window-coverage-invalid", gap);

        // 超集：窗口声明了非激活帧（扩大命中判定范围）。
        Assert.False(
            Try(
                new[]
                {
                    Window("first", new[] { 3 }, "Strong", 0),
                    Window("follow-up", new[] { 2, 4 }, "Normal", 0),
                },
                out _,
                out var extra
            )
        );
        Assert.Equal("resource.hostile-attack-metadata.hit-window-coverage-invalid", extra);
    }

    [Fact]
    public void Window_id_charset_and_capacity_are_bounded()
    {
        Assert.False(
            Try(new[] { Window("bad id!", new[] { 3, 4 }, "Strong", 0) }, out _, out var charset)
        );
        Assert.Equal("resource.hostile-attack-metadata.hit-window-id-invalid", charset);

        Assert.False(
            Try(
                new[] { Window(new string('a', 65), new[] { 3, 4 }, "Strong", 0) },
                out _,
                out var tooLong
            )
        );
        Assert.Equal("resource.hostile-attack-metadata.hit-window-id-invalid", tooLong);

        var overflow = new List<SanityHostileAttackHitWindowDefinition>();
        for (var index = 0; index < SanityHostileAttackHitWindowRules.MaximumWindows + 1; index++)
        {
            overflow.Add(Window($"w{index}", new[] { ActiveFrames[index % ActiveFrames.Length] }, "Normal", 0));
        }
        Assert.False(
            SanityHostileAttackHitWindowRules.TryCreateWindows(
                ActiveFrames,
                AttackFrameCount,
                overflow,
                out _,
                out var tooMany
            )
        );
        Assert.Equal("resource.hostile-attack-metadata.hit-window-count", tooMany);
    }

    [Fact]
    public void Shipped_metadata_without_windows_still_resolves_the_legacy_window()
    {
        var metadata = ShippedCreeperFearMetadata();
        // 资源层把缺省窗口折叠成校验后的单个隐式旧窗口，而不是保留空列表。
        var storedWindows = metadata!.Collision.AttackHitWindows;
        Assert.Single(storedWindows);
        Assert.Equal(SanityHostileAttackHitWindowRules.LegacyWindowId, storedWindows[0].WindowId);
        Assert.Equal("Normal", storedWindows[0].IFramePolicyMode);

        Assert.True(
            HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                HostileAttackTestFactory.Profile(
                    "sanity.binding.creeper-fear",
                    metadata
                ),
                out var definition,
                out var reason
            ),
            reason
        );
        Assert.True(
            definition!.TryGetWindowForFrame(3, out var window)
        );
        Assert.Equal(HostileAttackHitWindow.LegacyWindowId, window!.WindowId);
        Assert.Equal(InvincibilityFramePolicyMode.Normal, window.Policy.Mode);
    }

    [Fact]
    public void Shipped_metadata_with_declared_windows_resolves_per_frame_policy()
    {
        var metadata = ShippedCreeperFearMetadata(injectWindows: true);
        Assert.Equal(2, metadata!.Collision.AttackHitWindows.Count);

        Assert.True(
            HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                HostileAttackTestFactory.Profile(
                    "sanity.binding.creeper-fear",
                    metadata
                ),
                out var definition,
                out var reason
            ),
            reason
        );
        Assert.True(definition!.TryGetWindowForFrame(3, out var first));
        Assert.Equal("first", first!.WindowId);
        Assert.Equal(InvincibilityFramePolicyMode.Strong, first.Policy.Mode);
        Assert.Equal(0, first.Policy.DurationMilliseconds);
        Assert.True(definition.TryGetWindowForFrame(4, out var followUp));
        Assert.Equal("follow-up", followUp!.WindowId);
        Assert.Equal(InvincibilityFramePolicyMode.Normal, followUp.Policy.Mode);
        // 非激活帧没有窗口。
        Assert.False(definition.TryGetWindowForFrame(1, out _));
        Assert.False(definition.TryGetWindowForFrame(2, out _));
    }

    [Theory]
    [InlineData("shared-frame")]
    [InlineData("unknown-mode")]
    [InlineData("coverage-gap")]
    public void Invalid_window_metadata_is_rejected_at_parse_time(string caseId)
    {
        var data = Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data"
        );
        var animations = JsonNode.Parse(
            File.ReadAllText(Path.Combine(data, "animations.json"))
        )!.AsObject();
        var bindings = File.ReadAllText(
            Path.Combine(data, "resource-bindings.json")
        );
        InjectCreeperFearWindows(animations, caseId);

        Assert.False(
            SanityHostileAttackMetadataCatalog.TryParse(
                animations.ToJsonString(),
                bindings,
                out _,
                out var reason
            ),
            $"{caseId} should have been rejected"
        );
        Assert.StartsWith("resource.hostile-attack-metadata.hit-window", reason);
    }

    private static SanityHostileAttackMetadataDefinition? ShippedCreeperFearMetadata(
        bool injectWindows = false
    )
    {
        var data = Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data"
        );
        var animations = JsonNode.Parse(
            File.ReadAllText(Path.Combine(data, "animations.json"))
        )!.AsObject();
        var bindings = File.ReadAllText(
            Path.Combine(data, "resource-bindings.json")
        );
        if (injectWindows)
        {
            InjectCreeperFearWindows(animations, caseId: null);
        }

        Assert.True(
            SanityHostileAttackMetadataCatalog.TryParse(
                animations.ToJsonString(),
                bindings,
                out var catalog,
                out var reason
            ),
            reason
        );
        Assert.True(
            catalog!.TryGet("sanity.binding.creeper-fear", out var metadata)
        );
        return metadata;
    }

    private static void InjectCreeperFearWindows(JsonObject animations, string? caseId)
    {
        JsonObject? collision = null;
        foreach (var profile in animations["AnimationProfiles"]!.AsArray())
        {
            if (
                profile!["AnimationProfileId"]!
                    .GetValue<string>()
                    .Contains(
                        "creeper-fear",
                        StringComparison.Ordinal
                    )
            )
            {
                collision = profile["Collision"]!.AsObject();
            }
        }
        Assert.NotNull(collision);

        // 出厂元数据 AttackActiveFrames = [3, 4]（帧数 4）。caseId 变体覆盖三类非法输入。
        // 项目语言版本不支持原始字符串字面量，这里用 verbatim 字符串 + 双引号转义。
        var windows = caseId switch
        {
            "shared-frame" => @"[
  { ""WindowId"": ""first"", ""ActiveFrames"": [3, 4], ""IFramePolicy"": { ""Mode"": ""Strong"", ""DurationMilliseconds"": 0 } },
  { ""WindowId"": ""follow-up"", ""ActiveFrames"": [4], ""IFramePolicy"": { ""Mode"": ""Normal"", ""DurationMilliseconds"": 0 } }
]",
            "unknown-mode" => @"[
  { ""WindowId"": ""first"", ""ActiveFrames"": [3, 4], ""IFramePolicy"": { ""Mode"": ""Super"", ""DurationMilliseconds"": 100 } }
]",
            "coverage-gap" => @"[
  { ""WindowId"": ""first"", ""ActiveFrames"": [3], ""IFramePolicy"": { ""Mode"": ""Strong"", ""DurationMilliseconds"": 0 } }
]",
            _ => @"[
  { ""WindowId"": ""first"", ""ActiveFrames"": [3], ""IFramePolicy"": { ""Mode"": ""Strong"", ""DurationMilliseconds"": 0 } },
  { ""WindowId"": ""follow-up"", ""ActiveFrames"": [4], ""IFramePolicy"": { ""Mode"": ""Normal"", ""DurationMilliseconds"": 0 } }
]",
        };
        collision!["AttackHitWindows"] = JsonNode.Parse(windows);
    }
}
