#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DontStarve.Resource.Sanity;

internal static class SanityAssetValidator
{
    internal const int SupportedContractVersion = 1;

    private const string DevelopmentPlaceholderCreditGroup = "DEV-PLACEHOLDER";

    internal static SanityAssetValidationResult ValidateFromFiles(
        string manifestPath,
        string creditsPath,
        string deploymentRoot,
        SanityAssetValidationGate gate
    )
    {
        var issues = new List<SanityAssetValidationIssue>();
        if (!TryReadStrictUtf8(manifestPath, "manifest", issues, out var manifestJson))
            return EmptyResult(gate, issues);

        if (!TryReadStrictUtf8(creditsPath, "credits", issues, out var creditsJson))
            return EmptyResult(gate, issues);

        return Validate(
            manifestJson,
            creditsJson,
            deploymentRoot,
            gate,
            PhysicalSanityAssetFileAccess.Instance
        );
    }

    internal static SanityAssetValidationResult Validate(
        string manifestJson,
        string creditsJson,
        string deploymentRoot,
        SanityAssetValidationGate gate,
        ISanityAssetFileAccess fileAccess
    )
    {
        ArgumentNullException.ThrowIfNull(fileAccess);

        var issues = new List<SanityAssetValidationIssue>();
        var disabledOptionalSlotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingReplacementSlotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manifestResult = SanityAssetManifestParser.Parse(manifestJson);
        if (!manifestResult.Success || manifestResult.Manifest is null)
        {
            AddError(issues, string.Empty, string.Empty, manifestResult.Code, manifestResult.Reason);
        }

        var creditsResult = SanityCreditCatalogParser.Parse(creditsJson);
        if (!creditsResult.Success || creditsResult.Catalog is null)
        {
            AddError(issues, string.Empty, string.Empty, creditsResult.Code, creditsResult.Reason);
        }

        if (manifestResult.Manifest is null || creditsResult.Catalog is null)
        {
            return new SanityAssetValidationResult(
                gate,
                issues,
                disabledOptionalSlotIds,
                pendingReplacementSlotIds
            );
        }

        var credits = ValidateCredits(creditsResult.Catalog, issues);
        ValidateSlots(
            manifestResult.Manifest,
            credits,
            deploymentRoot,
            gate,
            fileAccess,
            issues,
            disabledOptionalSlotIds,
            pendingReplacementSlotIds
        );

        return new SanityAssetValidationResult(
            gate,
            issues,
            disabledOptionalSlotIds,
            pendingReplacementSlotIds
        );
    }

    private static Dictionary<string, SanityCreditGroup> ValidateCredits(
        SanityCreditCatalog catalog,
        ICollection<SanityAssetValidationIssue> issues
    )
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in catalog.Groups)
        {
            counts.TryGetValue(group.CreditGroup, out var count);
            counts[group.CreditGroup] = count + 1;
        }

        var result = new Dictionary<string, SanityCreditGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in catalog.Groups)
        {
            if (!IsCreditGroupId(group.CreditGroup))
            {
                AddError(
                    issues,
                    string.Empty,
                    string.Empty,
                    "credits.invalid-group-id",
                    $"Credit group '{group.CreditGroup}' must be non-empty ASCII letters, numbers, or hyphens."
                );
                continue;
            }

            if (counts[group.CreditGroup] > 1)
            {
                AddError(
                    issues,
                    string.Empty,
                    string.Empty,
                    "credits.duplicate-group",
                    $"Credit group '{group.CreditGroup}' is duplicated case-insensitively."
                );
                continue;
            }

            if (
                string.IsNullOrWhiteSpace(group.DisplayName)
                || string.IsNullOrWhiteSpace(group.SourceEvidenceId)
                || string.IsNullOrWhiteSpace(group.PermissionScope)
            )
            {
                AddError(
                    issues,
                    string.Empty,
                    string.Empty,
                    "credits.incomplete-group",
                    $"Credit group '{group.CreditGroup}' is missing required non-empty metadata."
                );
                continue;
            }

            result.Add(group.CreditGroup, group);
        }

        return result;
    }

    private static void ValidateSlots(
        SanityAssetManifest manifest,
        IReadOnlyDictionary<string, SanityCreditGroup> credits,
        string deploymentRoot,
        SanityAssetValidationGate gate,
        ISanityAssetFileAccess fileAccess,
        ICollection<SanityAssetValidationIssue> issues,
        ISet<string> disabledOptionalSlotIds,
        ISet<string> pendingReplacementSlotIds
    )
    {
        var idCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in manifest.Slots)
        {
            idCounts.TryGetValue(slot.SlotId, out var count);
            idCounts[slot.SlotId] = count + 1;
        }

        var sharedPaths = new Dictionary<string, SanityAssetSlot>(StringComparer.Ordinal);
        // animation/cue 状态逐 slot 登记但共享 metadata 文件；同一物理文件只读/hash 一次。
        var inspections = new Dictionary<string, SanityAssetFileInspection>(PathComparer);

        foreach (var slot in manifest.Slots)
        {
            var canInspectFile = true;
            if (idCounts[slot.SlotId] > 1)
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "manifest.duplicate-slot-id",
                    "SlotId values must be unique using ordinal-ignore-case comparison."
                );
            }

            if (!IsStableSlotId(slot.SlotId))
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "manifest.invalid-slot-id",
                    "SlotId must be lowercase ASCII and use a frozen sanity.asset, sanity.animation, or sanity.cue namespace."
                );
            }

            if (slot.ContractVersion != SupportedContractVersion)
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "manifest.contract-version-mismatch",
                    $"ContractVersion {slot.ContractVersion} does not match supported version {SupportedContractVersion}."
                );
            }

            if (
                !SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                    deploymentRoot,
                    slot.Path,
                    out var absolutePath,
                    out var pathReason
                )
            )
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "manifest.invalid-path",
                    pathReason
                );
                absolutePath = string.Empty;
                canInspectFile = false;
            }

            if (!SanityAssetFileInspector.ExtensionMatchesKind(slot.Path, slot.Kind))
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "manifest.kind-path-mismatch",
                    $"Path extension does not match declared kind {slot.Kind}."
                );
                canInspectFile = false;
            }

            if (sharedPaths.TryGetValue(slot.Path, out var existing))
            {
                if (existing.Kind != slot.Kind)
                {
                    AddError(
                        issues,
                        slot.SlotId,
                        slot.Path,
                        "manifest.shared-path-conflict",
                        "Slots sharing one path must declare the same Kind."
                    );
                    canInspectFile = false;
                }
                else if (
                    !string.Equals(existing.Sha256, slot.Sha256, StringComparison.OrdinalIgnoreCase)
                )
                {
                    AddWarning(
                        issues,
                        slot.SlotId,
                        slot.Path,
                        "manifest.shared-path-hash-conflict",
                        "Slots sharing one path declare different advisory SHA-256 values; the path and file remain authoritative."
                    );
                }
            }
            else
            {
                sharedPaths.Add(slot.Path, slot);
            }

            credits.TryGetValue(slot.CreditGroup, out var credit);
            if (credit is null)
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "asset.unknown-credit-group",
                    $"CreditGroup '{slot.CreditGroup}' is not registered in sanity-credits.json."
                );
            }

            if (!canInspectFile)
                continue;

            var exists = fileAccess.FileExists(absolutePath);
            if (!exists)
            {
                if (slot.RequiredForRelease)
                {
                    AddError(
                        issues,
                        slot.SlotId,
                        slot.Path,
                        "asset.required-missing",
                        "Required asset file is missing."
                    );
                }
                else
                {
                    AddWarning(
                        issues,
                        slot.SlotId,
                        slot.Path,
                        "asset.optional-missing",
                        "Optional asset file is missing and the slot must remain disabled."
                    );
                    disabledOptionalSlotIds.Add(slot.SlotId);
                }

                continue;
            }

            if (!inspections.TryGetValue(absolutePath, out var inspection))
            {
                inspection = SanityAssetFileInspector.Inspect(absolutePath, fileAccess);
                inspections.Add(absolutePath, inspection);
            }

            if (!inspection.ReadSucceeded)
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "asset.read-failed",
                    inspection.Reason
                );
                continue;
            }

            if (string.IsNullOrWhiteSpace(slot.Sha256))
            {
                AddWarning(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "asset.hash-missing",
                    "The asset is identified by its deployment-relative path; a missing SHA-256 is advisory only."
                );
            }
            else if (!SanityAssetFileInspector.IsSha256(slot.Sha256))
            {
                AddWarning(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "asset.hash-invalid",
                    "The declared SHA-256 is malformed; the deployment-relative path remains authoritative."
                );
            }
            else if (!string.Equals(slot.Sha256, inspection.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AddWarning(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "asset.hash-mismatch",
                    $"Declared SHA-256 does not match '{slot.Path}'; the file is still eligible by path and format."
                );
            }

            if (!SanityAssetFileInspector.IsFormatValid(slot.Kind, inspection.Bytes))
            {
                AddError(
                    issues,
                    slot.SlotId,
                    slot.Path,
                    "asset.invalid-format",
                    $"File content does not match declared kind {slot.Kind}."
                );
            }

            if (gate == SanityAssetValidationGate.Development)
            {
                // Development 只放行“文件真实存在且显式登记”的 placeholder；空文件或缺 required 仍在上方失败。
                if (
                    slot.IsPlaceholder
                    || string.Equals(
                        slot.CreditGroup,
                        DevelopmentPlaceholderCreditGroup,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || credit?.IsPlaceholder == true
                )
                {
                    AddWarning(
                        issues,
                        slot.SlotId,
                        slot.Path,
                        "asset.placeholder-pending",
                        "Development may use this explicit placeholder, but it must be replaced before Release."
                    );
                    pendingReplacementSlotIds.Add(slot.SlotId);
                }
            }
            else
            {
                ValidateReleaseEligibility(slot, credit, issues);
            }
        }
    }

    private static void ValidateReleaseEligibility(
        SanityAssetSlot slot,
        SanityCreditGroup? credit,
        ICollection<SanityAssetValidationIssue> issues
    )
    {
        if (slot.IsPlaceholder)
        {
            AddError(
                issues,
                slot.SlotId,
                slot.Path,
                "release.asset-placeholder",
                "Release rejects every packaged slot marked IsPlaceholder, including optional slots."
            );
        }

        if (
            string.Equals(
                slot.CreditGroup,
                DevelopmentPlaceholderCreditGroup,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            AddError(
                issues,
                slot.SlotId,
                slot.Path,
                "release.dev-credit-group",
                "Release rejects DEV-PLACEHOLDER for every packaged slot."
            );
        }

        if (credit is null)
            return;

        if (credit.IsPlaceholder)
        {
            AddError(
                issues,
                slot.SlotId,
                slot.Path,
                "release.credit-placeholder",
                $"Credit group '{credit.CreditGroup}' is still a placeholder."
            );
        }

        if (string.IsNullOrWhiteSpace(credit.AttributionText))
        {
            AddError(
                issues,
                slot.SlotId,
                slot.Path,
                "release.attribution-empty",
                $"Credit group '{credit.CreditGroup}' has no public attribution text."
            );
        }

        if (!AllowsPublicDistribution(credit.PermissionScope))
        {
            AddError(
                issues,
                slot.SlotId,
                slot.Path,
                "release.permission-insufficient",
                $"Credit group '{credit.CreditGroup}' does not explicitly permit public distribution."
            );
        }
    }

    private static bool TryReadStrictUtf8(
        string path,
        string contractName,
        ICollection<SanityAssetValidationIssue> issues,
        out string text
    )
    {
        text = string.Empty;
        if (!File.Exists(path))
        {
            AddError(
                issues,
                string.Empty,
                path,
                $"{contractName}.missing",
                $"The {contractName} file is missing."
            );
            return false;
        }

        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(false, true));
            return true;
        }
        catch (DecoderFallbackException)
        {
            AddError(
                issues,
                string.Empty,
                path,
                $"{contractName}.invalid-utf8",
                $"The {contractName} file is not strict UTF-8."
            );
            return false;
        }
        catch (IOException)
        {
            AddError(
                issues,
                string.Empty,
                path,
                $"{contractName}.read-failed",
                $"The {contractName} file could not be read."
            );
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            AddError(
                issues,
                string.Empty,
                path,
                $"{contractName}.read-failed",
                $"The {contractName} file could not be read."
            );
            return false;
        }
    }

    private static SanityAssetValidationResult EmptyResult(
        SanityAssetValidationGate gate,
        IReadOnlyCollection<SanityAssetValidationIssue> issues
    )
    {
        return new SanityAssetValidationResult(
            gate,
            issues,
            Array.Empty<string>(),
            Array.Empty<string>()
        );
    }

    private static bool IsStableSlotId(string slotId)
    {
        if (
            !slotId.StartsWith("sanity.asset.", StringComparison.Ordinal)
            && !slotId.StartsWith("sanity.animation.", StringComparison.Ordinal)
            && !slotId.StartsWith("sanity.cue.", StringComparison.Ordinal)
        )
        {
            return false;
        }

        foreach (var character in slotId)
        {
            if (
                (character < 'a' || character > 'z')
                && (character < '0' || character > '9')
                && character != '.'
                && character != '-'
            )
            {
                return false;
            }
        }

        return !slotId.EndsWith(".", StringComparison.Ordinal)
            && !slotId.EndsWith("-", StringComparison.Ordinal);
    }

    private static bool IsCreditGroupId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var character in value)
        {
            var isAsciiLetter = (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z');
            if (!isAsciiLetter && (character < '0' || character > '9') && character != '-')
                return false;
        }

        return true;
    }

    private static bool AllowsPublicDistribution(string permissionScope)
    {
        return permissionScope.Contains("public", StringComparison.OrdinalIgnoreCase)
            && permissionScope.Contains("distribution", StringComparison.OrdinalIgnoreCase)
            && permissionScope.Contains("permitted", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddError(
        ICollection<SanityAssetValidationIssue> issues,
        string slotId,
        string path,
        string code,
        string reason
    )
    {
        issues.Add(
            new SanityAssetValidationIssue(
                SanityAssetIssueSeverity.Error,
                slotId,
                path,
                code,
                reason
            )
        );
    }

    private static void AddWarning(
        ICollection<SanityAssetValidationIssue> issues,
        string slotId,
        string path,
        string code,
        string reason
    )
    {
        issues.Add(
            new SanityAssetValidationIssue(
                SanityAssetIssueSeverity.Warning,
                slotId,
                path,
                code,
                reason
            )
        );
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

}
