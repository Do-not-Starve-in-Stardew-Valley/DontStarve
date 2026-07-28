using System.Security.Cryptography;
using System.Text.Json;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityAudioVisualAssetStatusGateTests
{
    private static readonly string[] TaskSlotIds =
    {
        "sanity.asset.danger-border.overlay",
        "sanity.cue.ambience.low-sanity",
        "sanity.cue.thresholds.danger-enter",
        "sanity.cue.whispers.low-sanity",
    };

    private static string ShippedModRoot => Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ManifestPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-assets.json");

    private static string CreditsPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-credits.json");

    private static string AudioMetadataPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Audio", "audio-cues.json");

    private static string AnimationMetadataPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "animations.json");

    [Fact]
    public void TaskFiveOwnsExactlyFourFrozenReleaseSlots()
    {
        var manifest = ReadManifest();
        var slots = manifest.Slots
            .Where(slot => TaskSlotIds.Contains(slot.SlotId, StringComparer.Ordinal))
            .OrderBy(slot => slot.SlotId, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TaskSlotIds, slots.Select(slot => slot.SlotId));
        Assert.All(slots, slot =>
        {
            Assert.Equal(1, slot.ContractVersion);
            Assert.True(slot.RequiredForRelease);
            Assert.NotNull(slot.Sha256);
            Assert.Equal(slot.Sha256, Sha256(ResolveShippedPath(slot.Path)));
        });

        var border = slots[0];
        Assert.Equal("Asset/Sanity/Overlays/danger-border.png", border.Path);
        Assert.Equal(SanityAssetKind.Png, border.Kind);
        Assert.True(border.IsPlaceholder);
        Assert.Equal("DEV-PLACEHOLDER", border.CreditGroup);

        var audioSlots = slots.Skip(1).ToArray();
        Assert.All(audioSlots, slot =>
        {
            Assert.Equal("Asset/Sanity/Audio/audio-cues.json", slot.Path);
            Assert.Equal(SanityAssetKind.Json, slot.Kind);
            Assert.False(slot.IsPlaceholder);
        });
        Assert.Equal(new[] { "ART-07", "ART-09", "ART-08" }, audioSlots.Select(slot => slot.CreditGroup));
    }

    [Fact]
    public void DevelopmentGateAcceptsTheFourSlotsButKeepsThemPendingReplacement()
    {
        var result = Validate(SanityAssetValidationGate.Development);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Code)));
        Assert.All(TaskSlotIds, slotId => Assert.Contains(slotId, result.PendingReplacementSlotIds));
        Assert.DoesNotContain(
            result.Issues,
            issue => TaskSlotIds.Contains(issue.SlotId, StringComparer.Ordinal)
                && issue.Severity == SanityAssetIssueSeverity.Error
        );
    }

    [Fact]
    public void ReleaseGateRejectsEachSlotForOnlyItsCurrentFrozenBlockers()
    {
        var result = Validate(SanityAssetValidationGate.Release);

        Assert.False(result.Success);
        Assert.Equal(
            new[]
            {
                "release.asset-placeholder",
                "release.credit-placeholder",
                "release.dev-credit-group",
                "release.permission-insufficient",
            },
            ReleaseCodes(result, "sanity.asset.danger-border.overlay")
        );
        Assert.Equal(
            new[] { "release.credit-placeholder" },
            ReleaseCodes(result, "sanity.cue.ambience.low-sanity")
        );
        Assert.Equal(
            new[] { "release.credit-placeholder" },
            ReleaseCodes(result, "sanity.cue.whispers.low-sanity")
        );
        Assert.Equal(
            new[] { "release.credit-placeholder" },
            ReleaseCodes(result, "sanity.cue.thresholds.danger-enter")
        );
    }

    [Fact]
    public void TaskAudioIsMachineValidButStillNeedsListeningAndFinalAttribution()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AudioMetadataPath));
        var root = document.RootElement;
        var policy = root.GetProperty("ListeningPolicy");
        Assert.Equal("LocalVerified", policy.GetProperty("MachineEvidenceStatus").GetString());
        Assert.Equal("PendingRealMachine", policy.GetProperty("RealPlaybackStatus").GetString());
        Assert.True(policy.GetProperty("NoListeningInferenceFromMetrics").GetBoolean());

        var expectedClipCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sanity.cue.ambience.low-sanity"] = 11,
            ["sanity.cue.whispers.low-sanity"] = 11,
            ["sanity.cue.thresholds.danger-enter"] = 1,
        };
        foreach (var pair in expectedClipCounts)
        {
            var cue = FindCue(root, pair.Key);
            Assert.True(cue.GetProperty("Enabled").GetBoolean());
            Assert.True(cue.GetProperty("RequiredForRelease").GetBoolean());
            Assert.False(cue.GetProperty("IsPlaceholder").GetBoolean());
            Assert.Equal("PendingRealMachine", cue.GetProperty("ListeningStatus").GetString());
            var clips = cue.GetProperty("Clips").EnumerateArray().ToArray();
            Assert.Equal(pair.Value, clips.Length);
            Assert.All(clips, clip =>
            {
                Assert.False(clip.GetProperty("IsPlaceholder").GetBoolean());
                Assert.Equal(
                    "PendingRealMachine",
                    clip.GetProperty("ListeningEvidence").GetProperty("OverallStatus").GetString()
                );
                var inspection = SanityWavInspector.InspectFile(
                    ResolveShippedPath(clip.GetProperty("Path").GetString()!)
                );
                Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Issues));
                Assert.Equal(1, inspection.FormatCode);
                Assert.Equal(2, inspection.Channels);
                Assert.Equal(44100, inspection.SampleRateHz);
                Assert.Equal(16, inspection.BitsPerSample);
                Assert.Equal(clip.GetProperty("Sha256").GetString(), inspection.Sha256);
                Assert.Equal(clip.GetProperty("DurationFrames").GetInt64(), inspection.Frames);
            });
        }

        var credits = ReadCredits();
        foreach (var creditGroup in new[] { "ART-07", "ART-08", "ART-09" })
        {
            var credit = Assert.Single(credits.Groups.Where(group => group.CreditGroup == creditGroup));
            Assert.True(credit.IsPlaceholder);
            Assert.Equal("PENDING-PUBLIC-ATTRIBUTION", credit.AttributionText);
            Assert.Contains("public mod distribution permitted", credit.PermissionScope, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DangerBorderKeepsItsNineSliceContractWithoutClaimingFinalVisualAcceptance()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AnimationMetadataPath));
        var profile = Assert.Single(document.RootElement.GetProperty("OverlayProfiles").EnumerateArray());
        Assert.Equal("sanity.overlay.danger-border.profile", profile.GetProperty("OverlayProfileId").GetString());
        Assert.Equal("sanity.asset.danger-border.overlay", profile.GetProperty("TextureSlotId").GetString());
        Assert.Equal("NineSlice", profile.GetProperty("StretchMode").GetString());
        Assert.Equal(64, profile.GetProperty("Width").GetInt32());
        Assert.Equal(64, profile.GetProperty("Height").GetInt32());
        Assert.True(profile.GetProperty("ViewportSafe").GetBoolean());
        Assert.True(profile.GetProperty("UiScaleSafe").GetBoolean());
        Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
        Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());
        Assert.True(profile.GetProperty("IsProvisional").GetBoolean());
        Assert.Equal("DEV-PLACEHOLDER", profile.GetProperty("CreditGroup").GetString());
        Assert.Contains("final", profile.GetProperty("ProvisionalReason").GetString()!, StringComparison.OrdinalIgnoreCase);

        var slice = profile.GetProperty("SliceSourcePx");
        Assert.Equal(16, slice.GetProperty("Left").GetInt32());
        Assert.Equal(16, slice.GetProperty("Top").GetInt32());
        Assert.Equal(16, slice.GetProperty("Right").GetInt32());
        Assert.Equal(16, slice.GetProperty("Bottom").GetInt32());
        var image = PngRgbaImage.Decode(ResolveShippedPath("Asset/Sanity/Overlays/danger-border.png"));
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        Assert.Equal(0, image.CountNonTransparentPixels(16, 16, 32, 32));
        Assert.True(image.ContainsColor(255, 0, 255, 255));

        var credit = Assert.Single(ReadCredits().Groups.Where(group => group.CreditGroup == "DEV-PLACEHOLDER"));
        Assert.True(credit.IsPlaceholder);
    }

    private static SanityAssetManifest ReadManifest()
    {
        var result = SanityAssetManifestParser.Parse(File.ReadAllText(ManifestPath));
        Assert.True(result.Success, result.Reason);
        return Assert.IsType<SanityAssetManifest>(result.Manifest);
    }

    private static SanityCreditCatalog ReadCredits()
    {
        var result = SanityCreditCatalogParser.Parse(File.ReadAllText(CreditsPath));
        Assert.True(result.Success, result.Reason);
        return Assert.IsType<SanityCreditCatalog>(result.Catalog);
    }

    private static SanityAssetValidationResult Validate(SanityAssetValidationGate gate)
    {
        return SanityAssetValidator.ValidateFromFiles(ManifestPath, CreditsPath, ShippedModRoot, gate);
    }

    private static string[] ReleaseCodes(SanityAssetValidationResult result, string slotId)
    {
        return result.Issues
            .Where(issue => issue.SlotId == slotId && issue.Code.StartsWith("release.", StringComparison.Ordinal))
            .Select(issue => issue.Code)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonElement FindCue(JsonElement root, string cueId)
    {
        return Assert.Single(
            root.GetProperty("CueSets")
                .EnumerateArray()
                .SelectMany(set => set.GetProperty("Cues").EnumerateArray())
                .Where(cue => cue.GetProperty("CueId").GetString() == cueId)
        );
    }

    private static string ResolveShippedPath(string relativePath)
    {
        Assert.True(
            SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                ShippedModRoot,
                relativePath,
                out var absolutePath,
                out var reason
            ),
            reason
        );
        return absolutePath;
    }

    private static string Sha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
