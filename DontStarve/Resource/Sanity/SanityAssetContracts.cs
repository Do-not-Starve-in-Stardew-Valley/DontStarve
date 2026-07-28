#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace DontStarve.Resource.Sanity;

internal enum SanityAssetValidationGate
{
    Development,
    Release,
}

internal enum SanityAssetKind
{
    Png,
    Json,
    Wav,
}

internal enum SanityAssetIssueSeverity
{
    Warning,
    Error,
}

internal sealed class SanityAssetManifest
{
    internal SanityAssetManifest(int schemaVersion, IReadOnlyList<SanityAssetSlot> slots)
    {
        SchemaVersion = schemaVersion;
        Slots = Copy(slots);
    }

    internal int SchemaVersion { get; }

    internal IReadOnlyList<SanityAssetSlot> Slots { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        var copy = new T[values.Count];
        for (var index = 0; index < values.Count; index++)
            copy[index] = values[index];

        return Array.AsReadOnly(copy);
    }
}

internal sealed class SanityAssetSlot
{
    internal SanityAssetSlot(
        string slotId,
        string path,
        SanityAssetKind kind,
        string? sha256,
        bool isPlaceholder,
        int contractVersion,
        bool requiredForRelease,
        string creditGroup
    )
    {
        SlotId = slotId;
        Path = path;
        Kind = kind;
        Sha256 = sha256;
        IsPlaceholder = isPlaceholder;
        ContractVersion = contractVersion;
        RequiredForRelease = requiredForRelease;
        CreditGroup = creditGroup;
    }

    internal string SlotId { get; }

    internal string Path { get; }

    internal SanityAssetKind Kind { get; }

    internal string? Sha256 { get; }

    internal bool IsPlaceholder { get; }

    internal int ContractVersion { get; }

    internal bool RequiredForRelease { get; }

    internal string CreditGroup { get; }
}

internal sealed class SanityCreditCatalog
{
    internal SanityCreditCatalog(int schemaVersion, IReadOnlyList<SanityCreditGroup> groups)
    {
        SchemaVersion = schemaVersion;
        var copy = new SanityCreditGroup[groups.Count];
        for (var index = 0; index < groups.Count; index++)
            copy[index] = groups[index];

        Groups = Array.AsReadOnly(copy);
    }

    internal int SchemaVersion { get; }

    internal IReadOnlyList<SanityCreditGroup> Groups { get; }
}

internal sealed class SanityCreditGroup
{
    internal SanityCreditGroup(
        string creditGroup,
        string displayName,
        string attributionText,
        string sourceEvidenceId,
        string permissionScope,
        bool isPlaceholder
    )
    {
        CreditGroup = creditGroup;
        DisplayName = displayName;
        AttributionText = attributionText;
        SourceEvidenceId = sourceEvidenceId;
        PermissionScope = permissionScope;
        IsPlaceholder = isPlaceholder;
    }

    internal string CreditGroup { get; }

    internal string DisplayName { get; }

    internal string AttributionText { get; }

    internal string SourceEvidenceId { get; }

    internal string PermissionScope { get; }

    internal bool IsPlaceholder { get; }
}

internal sealed class SanityAssetManifestParseResult
{
    private SanityAssetManifestParseResult(
        SanityAssetManifest? manifest,
        string code,
        string reason
    )
    {
        Manifest = manifest;
        Code = code;
        Reason = reason;
    }

    internal SanityAssetManifest? Manifest { get; }

    internal string Code { get; }

    internal string Reason { get; }

    internal bool Success => Manifest is not null;

    internal static SanityAssetManifestParseResult Available(SanityAssetManifest manifest)
    {
        return new SanityAssetManifestParseResult(
            manifest,
            "manifest.available",
            "The Sanity asset manifest is structurally valid."
        );
    }

    internal static SanityAssetManifestParseResult Unavailable(string code, string reason)
    {
        return new SanityAssetManifestParseResult(null, code, reason);
    }
}

internal sealed class SanityCreditCatalogParseResult
{
    private SanityCreditCatalogParseResult(
        SanityCreditCatalog? catalog,
        string code,
        string reason
    )
    {
        Catalog = catalog;
        Code = code;
        Reason = reason;
    }

    internal SanityCreditCatalog? Catalog { get; }

    internal string Code { get; }

    internal string Reason { get; }

    internal bool Success => Catalog is not null;

    internal static SanityCreditCatalogParseResult Available(SanityCreditCatalog catalog)
    {
        return new SanityCreditCatalogParseResult(
            catalog,
            "credits.available",
            "The Sanity credit catalog is structurally valid."
        );
    }

    internal static SanityCreditCatalogParseResult Unavailable(string code, string reason)
    {
        return new SanityCreditCatalogParseResult(null, code, reason);
    }
}

internal sealed class SanityAssetValidationIssue
{
    internal SanityAssetValidationIssue(
        SanityAssetIssueSeverity severity,
        string slotId,
        string path,
        string code,
        string reason
    )
    {
        Severity = severity;
        SlotId = slotId;
        Path = path;
        Code = code;
        Reason = reason;
    }

    internal SanityAssetIssueSeverity Severity { get; }

    internal string SlotId { get; }

    internal string Path { get; }

    internal string Code { get; }

    internal string Reason { get; }
}

internal sealed class SanityAssetValidationResult
{
    internal SanityAssetValidationResult(
        SanityAssetValidationGate gate,
        IReadOnlyCollection<SanityAssetValidationIssue> issues,
        IReadOnlyCollection<string> disabledOptionalSlotIds,
        IReadOnlyCollection<string> pendingReplacementSlotIds
    )
    {
        Gate = gate;
        Issues = Copy(issues);
        DisabledOptionalSlotIds = Copy(disabledOptionalSlotIds);
        PendingReplacementSlotIds = Copy(pendingReplacementSlotIds);
    }

    internal SanityAssetValidationGate Gate { get; }

    internal IReadOnlyList<SanityAssetValidationIssue> Issues { get; }

    internal IReadOnlyList<string> DisabledOptionalSlotIds { get; }

    internal IReadOnlyList<string> PendingReplacementSlotIds { get; }

    internal bool Success
    {
        get
        {
            foreach (var issue in Issues)
            {
                if (issue.Severity == SanityAssetIssueSeverity.Error)
                    return false;
            }

            return true;
        }
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyCollection<T> values)
    {
        var copy = new List<T>(values);
        return new ReadOnlyCollection<T>(copy);
    }
}

internal interface ISanityAssetFileAccess
{
    bool FileExists(string absolutePath);

    bool TryReadAllBytes(string absolutePath, out byte[] bytes, out string reason);
}

internal sealed class PhysicalSanityAssetFileAccess : ISanityAssetFileAccess
{
    internal static PhysicalSanityAssetFileAccess Instance { get; } = new();

    private PhysicalSanityAssetFileAccess() { }

    public bool FileExists(string absolutePath)
    {
        return File.Exists(absolutePath);
    }

    public bool TryReadAllBytes(string absolutePath, out byte[] bytes, out string reason)
    {
        try
        {
            bytes = File.ReadAllBytes(absolutePath);
            reason = "asset.read";
            return true;
        }
        catch (IOException)
        {
            bytes = Array.Empty<byte>();
            reason = "asset.read-failed";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            bytes = Array.Empty<byte>();
            reason = "asset.read-failed";
            return false;
        }
    }
}
