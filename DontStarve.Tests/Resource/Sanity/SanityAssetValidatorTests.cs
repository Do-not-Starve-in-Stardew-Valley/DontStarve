using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityAssetValidatorTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ShippedManifestPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-assets.json");

    private static string ShippedCreditsPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-credits.json");

    [Fact]
    public void ShippedManifestFreezesEveryAssetAnimationAndCueSetSlot()
    {
        var parsed = SanityAssetManifestParser.Parse(File.ReadAllText(ShippedManifestPath));

        Assert.True(parsed.Success, parsed.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(parsed.Manifest);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(56, manifest.Slots.Count);
        Assert.Equal(
            56,
            manifest.Slots.Select(slot => slot.SlotId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        );
        Assert.Equal(9, manifest.Slots.Count(slot => slot.SlotId.StartsWith("sanity.asset.", StringComparison.Ordinal)));
        Assert.Equal(24, manifest.Slots.Count(slot => slot.SlotId.StartsWith("sanity.animation.", StringComparison.Ordinal)));
        Assert.Equal(23, manifest.Slots.Count(slot => slot.SlotId.StartsWith("sanity.cue.", StringComparison.Ordinal)));
        Assert.Equal(11, manifest.Slots.Select(slot => slot.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(manifest.Slots, slot =>
        {
            Assert.Equal(1, slot.ContractVersion);
            Assert.True(SanityAssetPathPolicy.TryNormalize(slot.Path, out _, out var reason), reason);
        });
    }

    [Fact]
    public void ShippedManifestDevelopmentGateAcceptsExplicitAudioOutputsAndReportsReplacements()
    {
        var result = SanityAssetValidator.ValidateFromFiles(
            ShippedManifestPath,
            ShippedCreditsPath,
            ShippedModRoot,
            SanityAssetValidationGate.Development
        );

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "asset.required-missing");
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "asset.optional-missing");
        Assert.Empty(result.DisabledOptionalSlotIds);
        Assert.Equal(56, result.PendingReplacementSlotIds.Count);
        Assert.DoesNotContain(result.Issues, issue => issue.Code.StartsWith("manifest.", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Issues, issue => issue.Code.StartsWith("credits.", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "asset.unknown-credit-group");
    }

    [Fact]
    public void DevelopmentAllowsAnExistingExplicitPlaceholderAndReportsReplacement()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.placeholder.sprite",
            "Asset/Sanity/Sprites/placeholder.png",
            bytes,
            isPlaceholder: true,
            creditGroup: "DEV-PLACEHOLDER"
        );
        var files = FilesWith(slot, bytes);

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(DevelopmentCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.True(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "asset.placeholder-pending");
        Assert.Equal(new[] { slot.SlotId }, result.PendingReplacementSlotIds);
    }

    [Fact]
    public void ReleaseAcceptsAHashedFinalAssetWithPublicCredit()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.final.sprite",
            "Asset/Sanity/Sprites/final.png",
            bytes,
            creditGroup: "ART-01"
        );
        var files = FilesWith(slot, bytes);

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Release,
            files
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Code)));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ReleaseRejectsAnExistingOptionalPlaceholderToo()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.optional-placeholder.sprite",
            "Asset/Sanity/Sprites/optional-placeholder.png",
            bytes,
            isPlaceholder: true,
            requiredForRelease: false,
            creditGroup: "DEV-PLACEHOLDER"
        );
        var files = FilesWith(slot, bytes);

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(DevelopmentCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Release,
            files
        );

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "release.asset-placeholder");
        Assert.Contains(result.Issues, issue => issue.Code == "release.dev-credit-group");
        Assert.Contains(result.Issues, issue => issue.Code == "release.credit-placeholder");
    }

    [Fact]
    public void RequiredMissingFailsWhileOptionalMissingWarnsAndDisables()
    {
        var required = Slot(
            "sanity.asset.required.sprite",
            "Asset/Sanity/Sprites/required.png",
            sha256: null,
            requiredForRelease: true
        );
        var optional = Slot(
            "sanity.asset.optional.sprite",
            "Asset/Sanity/Sprites/optional.png",
            sha256: null,
            requiredForRelease: false
        );
        var files = new MemorySanityAssetFileAccess();

        var result = SanityAssetValidator.Validate(
            Manifest(required, optional),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.SlotId == required.SlotId && issue.Code == "asset.required-missing");
        Assert.Contains(result.Issues, issue => issue.SlotId == optional.SlotId && issue.Code == "asset.optional-missing");
        Assert.Equal(new[] { optional.SlotId }, result.DisabledOptionalSlotIds);
    }

    [Fact]
    public void SlotIdsAreUniqueUsingOrdinalIgnoreCase()
    {
        var first = Slot(
            "sanity.asset.case.sprite",
            "Asset/Sanity/Sprites/case-a.png",
            sha256: null
        );
        var second = Slot(
            "SANITY.ASSET.CASE.SPRITE",
            "Asset/Sanity/Sprites/case-b.png",
            sha256: null
        );
        var files = new MemorySanityAssetFileAccess();

        var result = SanityAssetValidator.Validate(
            Manifest(first, second),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.Equal(2, result.Issues.Count(issue => issue.Code == "manifest.duplicate-slot-id"));
    }

    [Theory]
    [InlineData("DontStarve/Asset/Sanity/Sprites/wrong.png")]
    [InlineData("C:/mods/DontStarve/Asset/Sanity/Sprites/wrong.png")]
    [InlineData("../Asset/Sanity/Sprites/wrong.png")]
    [InlineData("Asset/Sanity/../wrong.png")]
    [InlineData("Asset/Sanity/Sprites/中文.png")]
    [InlineData("Asset/Sanity/references/wrong.png")]
    [InlineData("references/Asset/Sanity/Sprites/wrong.png")]
    [InlineData("Asset\\Sanity\\Sprites\\wrong.png")]
    public void UnsafeOrSourceRelativePathsAreRejected(string path)
    {
        Assert.False(SanityAssetPathPolicy.TryNormalize(path, out _, out _));
    }

    [Fact]
    public void DeploymentAndRepositoryResolversPreserveTheSameRelativePath()
    {
        var path = "Asset/Sanity/Sprites/Illusions/mr-skitts.png";
        var deploymentRoot = Path.Combine(Path.GetTempPath(), "Mods", "DontStarveCS");
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "repo");

        Assert.True(
            SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                deploymentRoot,
                path,
                out var runtimePath,
                out var runtimeReason
            ),
            runtimeReason
        );
        Assert.True(
            SanityAssetPathPolicy.TryResolveFromRepositoryRoot(
                repositoryRoot,
                path,
                out var repositoryPath,
                out var repositoryReason
            ),
            repositoryReason
        );

        Assert.Equal(
            path,
            Path.GetRelativePath(deploymentRoot, runtimePath).Replace('\\', '/')
        );
        Assert.Equal(
            path,
            Path.GetRelativePath(Path.Combine(repositoryRoot, "DontStarve"), repositoryPath).Replace('\\', '/')
        );
    }

    [Fact]
    public void SharedMetadataPathIsAllowedAndReadOnce()
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        var hash = Sha256(bytes);
        var first = Slot(
            "sanity.animation.first.profile",
            "Asset/Sanity/Data/animations.json",
            kind: "Json",
            sha256: hash
        );
        var second = Slot(
            "sanity.animation.second.profile",
            "Asset/Sanity/Data/animations.json",
            kind: "Json",
            sha256: hash
        );
        var files = FilesWith(first, bytes);

        var result = SanityAssetValidator.Validate(
            Manifest(first, second),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Release,
            files
        );

        Assert.True(result.Success);
        Assert.Equal(1, files.TotalReads);
    }

    [Fact]
    public void ContractVersionDriftFailsClosed()
    {
        var slot = Slot(
            "sanity.asset.drift.sprite",
            "Asset/Sanity/Sprites/drift.png",
            sha256: null,
            contractVersion: 2
        );
        var files = new MemorySanityAssetFileAccess();

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.Contains(result.Issues, issue => issue.Code == "manifest.contract-version-mismatch");
    }

    [Fact]
    public void HashMismatchWarnsButInvalidFileFormatStillFailsClosed()
    {
        var notPng = Encoding.UTF8.GetBytes("not-a-png");
        var slot = Slot(
            "sanity.asset.bad.sprite",
            "Asset/Sanity/Sprites/bad.png",
            sha256: new string('0', 64)
        );
        var files = FilesWith(slot, notPng);

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.Contains(result.Issues, issue => issue.Code == "asset.hash-mismatch");
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "asset.hash-mismatch"
                && issue.Severity == SanityAssetIssueSeverity.Warning
        );
        Assert.Contains(result.Issues, issue => issue.Code == "asset.invalid-format");
    }

    [Fact]
    public void ExistingFileWithoutHashRemainsAvailableWithWarning()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.no-hash.sprite",
            "Asset/Sanity/Sprites/no-hash.png",
            sha256: null
        );
        var files = FilesWith(slot, bytes);

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Code)));
        Assert.Contains(result.Issues, issue => issue.Code == "asset.hash-missing");
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "asset.hash-missing"
                && issue.Severity == SanityAssetIssueSeverity.Warning
        );
    }

    [Fact]
    public void ExistingFileWithHashPropertyOmittedRemainsAvailableWithWarning()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.omitted-hash.sprite",
            "Asset/Sanity/Sprites/omitted-hash.png",
            sha256: null
        );
        var files = FilesWith(slot, bytes);
        var manifest = JsonNode.Parse(Manifest(slot))!.AsObject();
        manifest["Slots"]![0]!.AsObject().Remove("Sha256");

        var result = SanityAssetValidator.Validate(
            manifest.ToJsonString(),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Code)));
        Assert.Contains(result.Issues, issue => issue.Code == "asset.hash-missing");
    }

    [Fact]
    public void UnknownCreditGroupFailsBothGates()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.unknown-credit.sprite",
            "Asset/Sanity/Sprites/unknown-credit.png",
            bytes,
            creditGroup: "ART-UNKNOWN"
        );
        var files = FilesWith(slot, bytes);

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.Contains(result.Issues, issue => issue.Code == "asset.unknown-credit-group");
    }

    [Fact]
    public void ReleaseRejectsEmptyAttribution()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.empty-credit.sprite",
            "Asset/Sanity/Sprites/empty-credit.png",
            bytes
        );
        var files = FilesWith(slot, bytes);
        var credit = FinalCredit() with { AttributionText = " " };

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(credit),
            files.DeploymentRoot,
            SanityAssetValidationGate.Release,
            files
        );

        Assert.Contains(result.Issues, issue => issue.Code == "release.attribution-empty");
    }

    [Fact]
    public void ReleaseRejectsPermissionWithoutPublicDistribution()
    {
        var bytes = ValidPng();
        var slot = Slot(
            "sanity.asset.private-credit.sprite",
            "Asset/Sanity/Sprites/private-credit.png",
            bytes
        );
        var files = FilesWith(slot, bytes);
        var credit = FinalCredit() with { PermissionScope = "Internal testing permitted." };

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            Credits(credit),
            files.DeploymentRoot,
            SanityAssetValidationGate.Release,
            files
        );

        Assert.Contains(result.Issues, issue => issue.Code == "release.permission-insufficient");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"SchemaVersion\":1,\"Slots\":[]}")]
    [InlineData("{\"SchemaVersion\":2,\"Slots\":[{}]}")]
    public void BadManifestJsonOrRootNeverThrows(string json)
    {
        var files = new MemorySanityAssetFileAccess();

        var result = SanityAssetValidator.Validate(
            json,
            Credits(FinalCredit()),
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code.StartsWith("manifest.", StringComparison.Ordinal));
    }

    [Fact]
    public void BadCreditJsonNeverThrows()
    {
        var files = new MemorySanityAssetFileAccess();
        var slot = Slot(
            "sanity.asset.credit-json.sprite",
            "Asset/Sanity/Sprites/credit-json.png",
            sha256: null
        );

        var result = SanityAssetValidator.Validate(
            Manifest(slot),
            "{",
            files.DeploymentRoot,
            SanityAssetValidationGate.Development,
            files
        );

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Code == "credits.invalid-json");
    }

    private static string Manifest(params SlotSpec[] slots)
    {
        return JsonSerializer.Serialize(new { SchemaVersion = 1, Slots = slots });
    }

    private static string Credits(params CreditSpec[] groups)
    {
        return JsonSerializer.Serialize(new { SchemaVersion = 1, Groups = groups });
    }

    private static SlotSpec Slot(
        string slotId,
        string path,
        byte[]? bytes = null,
        string kind = "Png",
        string? sha256 = null,
        bool isPlaceholder = false,
        int contractVersion = 1,
        bool requiredForRelease = true,
        string creditGroup = "ART-01"
    )
    {
        return new SlotSpec
        {
            SlotId = slotId,
            Path = path,
            Kind = kind,
            Sha256 = bytes is null ? sha256 : Sha256(bytes),
            IsPlaceholder = isPlaceholder,
            ContractVersion = contractVersion,
            RequiredForRelease = requiredForRelease,
            CreditGroup = creditGroup,
        };
    }

    private static CreditSpec FinalCredit()
    {
        return new CreditSpec
        {
            CreditGroup = "ART-01",
            DisplayName = "Project contributor",
            AttributionText = "Project contributor",
            SourceEvidenceId = "test:ART-01",
            PermissionScope = "Project-original; public mod distribution permitted.",
            IsPlaceholder = false,
        };
    }

    private static CreditSpec DevelopmentCredit()
    {
        return new CreditSpec
        {
            CreditGroup = "DEV-PLACEHOLDER",
            DisplayName = "Development placeholder",
            AttributionText = "Development only.",
            SourceEvidenceId = "test:placeholder",
            PermissionScope = "Local development and internal testing only.",
            IsPlaceholder = true,
        };
    }

    private static MemorySanityAssetFileAccess FilesWith(SlotSpec slot, byte[] bytes)
    {
        var files = new MemorySanityAssetFileAccess();
        files.Add(slot.Path, bytes);
        return files;
    }

    private static byte[] ValidPng()
    {
        return new byte[]
        {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        };
    }

    private static string Sha256(byte[] bytes)
    {
        return BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", string.Empty);
    }

    private sealed class MemorySanityAssetFileAccess : ISanityAssetFileAccess
    {
        private readonly Dictionary<string, byte[]> files = new(PathComparer);

        internal MemorySanityAssetFileAccess()
        {
            DeploymentRoot = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "dontstarve-sanity-assets-virtual-mod")
            );
        }

        internal string DeploymentRoot { get; }

        internal int TotalReads { get; private set; }

        internal void Add(string relativePath, byte[] bytes)
        {
            Assert.True(
                SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                    DeploymentRoot,
                    relativePath,
                    out var absolutePath,
                    out var reason
                ),
                reason
            );
            files[absolutePath] = bytes;
        }

        public bool FileExists(string absolutePath)
        {
            return files.ContainsKey(Path.GetFullPath(absolutePath));
        }

        public bool TryReadAllBytes(string absolutePath, out byte[] bytes, out string reason)
        {
            if (files.TryGetValue(Path.GetFullPath(absolutePath), out var stored))
            {
                TotalReads++;
                bytes = stored;
                reason = "asset.read";
                return true;
            }

            bytes = Array.Empty<byte>();
            reason = "asset.read-failed";
            return false;
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
    }

    private sealed class SlotSpec
    {
        public string SlotId { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string? Sha256 { get; init; }

        public bool IsPlaceholder { get; init; }

        public int ContractVersion { get; init; }

        public bool RequiredForRelease { get; init; }

        public string CreditGroup { get; init; } = string.Empty;
    }

    private sealed record CreditSpec
    {
        public string CreditGroup { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string AttributionText { get; init; } = string.Empty;

        public string SourceEvidenceId { get; init; } = string.Empty;

        public string PermissionScope { get; init; } = string.Empty;

        public bool IsPlaceholder { get; init; }
    }
}
