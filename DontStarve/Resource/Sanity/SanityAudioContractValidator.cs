#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DontStarve.Resource.Sanity;

public sealed record SanityAudioContractIssue(string Code, string Reason);

public sealed class SanityAudioContractValidationResult
{
    internal SanityAudioContractValidationResult(
        IReadOnlyList<SanityAudioContractIssue> issues,
        IReadOnlyList<string> disabledCueIds,
        IReadOnlyList<string> placeholderCueIds,
        IReadOnlyList<string> pendingRealMachineCueIds
    )
    {
        Issues = issues;
        DisabledCueIds = disabledCueIds;
        PlaceholderCueIds = placeholderCueIds;
        PendingRealMachineCueIds = pendingRealMachineCueIds;
    }

    public IReadOnlyList<SanityAudioContractIssue> Issues { get; }

    public IReadOnlyList<string> DisabledCueIds { get; }

    public IReadOnlyList<string> PlaceholderCueIds { get; }

    public IReadOnlyList<string> PendingRealMachineCueIds { get; }

    public bool Success => Issues.Count == 0;
}

/// <summary>
/// Validates stage-05 cue metadata and canonical WAV files without loading audio or owning a
/// playback coordinator. Runtime code may reuse this fail-closed seam in stage 06.
/// </summary>
public static class SanityAudioContractValidator
{
    public const string MetadataDeploymentPath = "Asset/Sanity/Audio/audio-cues.json";
    public const string RuntimeFormatId = "sanity.wav.pcm-s16-stereo-44100-v1";

    private const int ExpectedCueSetCount = 8;
    private const int ExpectedCueCount = 17;
    private const int ExpectedPhysicalClipCount = 35;

    private static readonly IReadOnlyDictionary<string, CueSetExpectation> ExpectedCueSets =
        new Dictionary<string, CueSetExpectation>(StringComparer.Ordinal)
        {
            ["sanity.cue.ambience"] = new(
                "low-sanity-ambience",
                "LazyPerCueSetBounded",
                "StopOnTierExitOrDisabled;ReleaseOnWorldTitleDispose",
                1
            ),
            ["sanity.cue.whispers"] = new(
                "low-sanity-whispers",
                "LazyPerCueSetBounded",
                "StopOnTierExitOrDisabled;ReleaseOnWorldTitleDispose",
                1
            ),
            ["sanity.cue.thresholds"] = new(
                "danger-threshold",
                "LazyPerCueSetBounded",
                "OneShotOnIdempotentEnter;ReleaseOnWorldTitleDispose",
                1
            ),
            ["sanity.cue.darkness"] = new(
                "darkness-warning",
                "LazyPerCueSetBounded",
                "CancelableOneShot;ReleaseOnWorldTitleDispose",
                1
            ),
            ["sanity.cue.dark-hand"] = new(
                "dark-hand-actor",
                "LazyPerCueSetBounded",
                "StopOnActorOrWorldCleanup;ReleaseOnTitleDispose",
                1
            ),
            ["sanity.cue.creeper-fear"] = new(
                "creeper-fear-actor",
                "LazyPerCueSetBounded",
                "StopOnEntityOrWorldCleanup;ReleaseOnTitleDispose",
                1
            ),
            ["sanity.cue.terrorbeak"] = new(
                "terrorbeak-actor",
                "LazyPerCueSetBounded",
                "StopOnEntityOrWorldCleanup;ReleaseOnTitleDispose",
                1
            ),
            ["sanity.cue.sanity-change"] = new(
                "sanity-change-optional",
                "DisabledNoCache",
                "DisabledNoPlayback",
                0
            ),
        };

    private static readonly IReadOnlyDictionary<string, CueExpectation> ExpectedCues =
        CreateExpectedCues();

    private static readonly HashSet<string> ForbiddenFieldNames = new(
        new[]
        {
            "damage",
            "hp",
            "speed",
            "defense",
            "ai",
            "gameplayprofile",
            "thresholdpercent",
            "volume",
            "volumepercent",
            "lufs",
            "loudness",
            "normalizedloudness",
            "normalizationtarget",
            "runtimehookup",
            "playbackcoordinator",
            "musiccoordinator",
            "runtimecache",
            "referencepath",
            "sourcepath",
        },
        StringComparer.OrdinalIgnoreCase
    );

    public static SanityAudioContractValidationResult ValidateFromFiles(
        string metadataPath,
        string manifestPath,
        string deploymentRoot
    )
    {
        var issues = new List<SanityAudioContractIssue>();
        if (!TryReadStrictUtf8(metadataPath, "audio.metadata", issues, out var metadataJson))
            return Empty(issues);
        if (!TryReadStrictUtf8(manifestPath, "audio.manifest", issues, out var manifestJson))
            return Empty(issues);
        return Validate(metadataJson, manifestJson, deploymentRoot);
    }

    public static SanityAudioContractValidationResult Validate(
        string metadataJson,
        string manifestJson,
        string deploymentRoot
    )
    {
        ArgumentNullException.ThrowIfNull(metadataJson);
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(deploymentRoot);

        var issues = new List<SanityAudioContractIssue>();
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = ParseStrict(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("audio.metadata.invalid-root", "The audio metadata root must be an object."));
                return Result(issues, disabled, placeholders, pending);
            }

            ValidateDuplicateProperties(root, issues);
            ValidateForbiddenFieldsAndStrings(root, issues);
            ValidateRoot(root, issues);
            ValidateRuntimeFormat(root, issues);
            ValidateManifest(manifestJson, metadataJson, issues);
            ValidateCueSets(root, deploymentRoot, issues, disabled, placeholders, pending);
        }
        catch (JsonException exception)
        {
            issues.Add(new("audio.metadata.invalid-json", exception.Message));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or FormatException
            or OverflowException
            or IOException
            or UnauthorizedAccessException
        )
        {
            issues.Add(new("audio.metadata.invalid-shape", exception.Message));
        }

        return Result(issues, disabled, placeholders, pending);
    }

    private static JsonDocument ParseStrict(string json)
    {
        return JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }
        );
    }

    private static void ValidateRoot(JsonElement root, ICollection<SanityAudioContractIssue> issues)
    {
        ExpectInt(root, "SchemaVersion", 1, "audio.metadata.unsupported-schema", issues);
        ExpectInt(root, "ContractVersion", 1, "audio.metadata.unsupported-contract", issues);
        ExpectString(
            root,
            "TemplateVersion",
            "sanity-audio-cues-v1",
            "audio.metadata.template-mismatch",
            issues
        );
    }

    private static void ValidateRuntimeFormat(
        JsonElement root,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (!TryObject(root, "RuntimeFormat", "audio.format.missing", issues, out var format))
            return;
        ExpectString(format, "FormatId", RuntimeFormatId, "audio.format.id-mismatch", issues);
        ExpectString(format, "Container", "RIFF", "audio.format.container-mismatch", issues);
        ExpectString(format, "Codec", "PCM", "audio.format.codec-mismatch", issues);
        ExpectInt(format, "FormatCode", 1, "audio.format.code-mismatch", issues);
        ExpectInt(format, "Channels", 2, "audio.format.channels-mismatch", issues);
        ExpectInt(format, "SampleRateHz", 44100, "audio.format.sample-rate-mismatch", issues);
        ExpectInt(format, "BitsPerSample", 16, "audio.format.bit-depth-mismatch", issues);
        ExpectInt(format, "BlockAlign", 4, "audio.format.block-align-mismatch", issues);
        ExpectInt(format, "ByteRate", 176400, "audio.format.byte-rate-mismatch", issues);
    }

    private static void ValidateManifest(
        string manifestJson,
        string metadataJson,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        var parsed = SanityAssetManifestParser.Parse(manifestJson);
        if (!parsed.Success || parsed.Manifest is null)
        {
            issues.Add(new("audio.manifest.invalid", parsed.Reason));
            return;
        }

        var cueSlots = parsed.Manifest.Slots
            .Where(slot => slot.SlotId.StartsWith("sanity.cue.", StringComparison.Ordinal))
            .ToArray();
        if (cueSlots.Length != ExpectedCueCount)
        {
            issues.Add(
                new(
                    "audio.manifest.cue-count-mismatch",
                    $"Expected {ExpectedCueCount} cue slots but found {cueSlots.Length}."
                )
            );
        }
        var metadataHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadataJson)));
        var slotIds = new HashSet<string>(cueSlots.Select(slot => slot.SlotId), StringComparer.Ordinal);
        foreach (var expected in ExpectedCues.Keys)
        {
            if (!slotIds.Contains(expected))
                issues.Add(new("audio.manifest.cue-missing", $"Manifest cue slot '{expected}' is missing."));
        }
        foreach (var slot in cueSlots)
        {
            if (!ExpectedCues.TryGetValue(slot.SlotId, out var expected))
            {
                issues.Add(new("audio.manifest.unknown-cue", $"Unknown manifest cue slot '{slot.SlotId}'."));
                continue;
            }
            if (slot.Path != MetadataDeploymentPath || slot.Kind != SanityAssetKind.Json)
            {
                issues.Add(
                    new(
                        "audio.manifest.path-mismatch",
                        $"Cue slot '{slot.SlotId}' must point to '{MetadataDeploymentPath}' as Json."
                    )
                );
            }
            if (!string.Equals(slot.Sha256, metadataHash, StringComparison.Ordinal))
                issues.Add(new("audio.manifest.hash-mismatch", $"Cue slot '{slot.SlotId}' has a stale metadata hash."));
            if (slot.RequiredForRelease != expected.RequiredForRelease || slot.IsPlaceholder != expected.IsPlaceholder)
            {
                issues.Add(
                    new(
                        "audio.manifest.status-mismatch",
                        $"Cue slot '{slot.SlotId}' changed its required/placeholder contract."
                    )
                );
            }
        }
    }

    private static void ValidateCueSets(
        JsonElement root,
        string deploymentRoot,
        ICollection<SanityAudioContractIssue> issues,
        ISet<string> disabled,
        ISet<string> placeholders,
        ISet<string> pending
    )
    {
        if (!TryArray(root, "CueSets", "audio.cue-sets.missing", issues, out var cueSets))
            return;
        var cueSetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clipIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cueSetCount = 0;
        var cueCount = 0;
        var clipCount = 0;
        foreach (var cueSet in cueSets.EnumerateArray())
        {
            cueSetCount++;
            if (cueSet.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("audio.cue-set.invalid-shape", "Each CueSets item must be an object."));
                continue;
            }
            var cueSetId = RequiredString(cueSet, "CueSetId", "audio.cue-set.id-missing", issues);
            if (!cueSetIds.Add(cueSetId))
                issues.Add(new("audio.cue-set.duplicate-id", $"CueSetId '{cueSetId}' is duplicated case-insensitively."));
            if (!ExpectedCueSets.TryGetValue(cueSetId, out var setExpectation))
            {
                issues.Add(new("audio.cue-set.unknown-id", $"Unknown CueSetId '{cueSetId}'."));
                setExpectation = null;
            }
            if (setExpectation is not null)
            {
                ExpectString(cueSet, "Group", setExpectation.Group, "audio.cue-set.group-mismatch", issues);
                ExpectString(
                    cueSet,
                    "CachePolicy",
                    setExpectation.CachePolicy,
                    "audio.cue-set.cache-policy-mismatch",
                    issues
                );
                ExpectString(
                    cueSet,
                    "LifecyclePolicy",
                    setExpectation.LifecyclePolicy,
                    "audio.cue-set.lifecycle-policy-mismatch",
                    issues
                );
                ExpectInt(
                    cueSet,
                    "MaxConcurrentInstances",
                    setExpectation.MaxConcurrentInstances,
                    "audio.cue-set.instance-bound-mismatch",
                    issues
                );
            }
            ExpectInt(cueSet, "ContractVersion", 1, "audio.cue-set.contract-mismatch", issues);
            if (!TryArray(cueSet, "Cues", "audio.cues.missing", issues, out var cues))
                continue;
            foreach (var cueValue in cues.EnumerateArray())
            {
                cueCount++;
                clipCount += ValidateCue(
                    cueValue,
                    cueSetId,
                    deploymentRoot,
                    issues,
                    disabled,
                    placeholders,
                    pending,
                    cueIds,
                    clipIds,
                    paths
                );
            }
        }

        if (cueSetCount != ExpectedCueSetCount)
            issues.Add(new("audio.cue-set.count-mismatch", $"Expected {ExpectedCueSetCount} cue sets, found {cueSetCount}."));
        if (cueCount != ExpectedCueCount)
            issues.Add(new("audio.cue.count-mismatch", $"Expected {ExpectedCueCount} cues, found {cueCount}."));
        if (clipCount != ExpectedPhysicalClipCount)
            issues.Add(new("audio.clip.count-mismatch", $"Expected {ExpectedPhysicalClipCount} physical clips, found {clipCount}."));
        foreach (var cueSetId in ExpectedCueSets.Keys)
        {
            if (!cueSetIds.Contains(cueSetId))
                issues.Add(new("audio.cue-set.missing", $"Cue set '{cueSetId}' is missing."));
        }
        foreach (var cueId in ExpectedCues.Keys)
        {
            if (!cueIds.Contains(cueId))
                issues.Add(new("audio.cue.missing", $"Cue '{cueId}' is missing."));
        }
    }

    private static int ValidateCue(
        JsonElement cueValue,
        string cueSetId,
        string deploymentRoot,
        ICollection<SanityAudioContractIssue> issues,
        ISet<string> disabled,
        ISet<string> placeholders,
        ISet<string> pending,
        ISet<string> cueIds,
        ISet<string> clipIds,
        ISet<string> paths
    )
    {
        if (cueValue.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("audio.cue.invalid-shape", "Each Cues item must be an object."));
            return 0;
        }
        var cueId = RequiredString(cueValue, "CueId", "audio.cue.id-missing", issues);
        if (!cueIds.Add(cueId))
            issues.Add(new("audio.cue.duplicate-id", $"CueId '{cueId}' is duplicated case-insensitively."));
        if (!ExpectedCues.TryGetValue(cueId, out var expectation))
        {
            issues.Add(new("audio.cue.unknown-id", $"Unknown CueId '{cueId}'."));
            expectation = null;
        }
        if (expectation is not null)
        {
            if (cueSetId != expectation.CueSetId)
                issues.Add(new("audio.cue.set-mismatch", $"Cue '{cueId}' belongs to the wrong CueSetId."));
            ExpectString(cueValue, "PlaybackMode", expectation.PlaybackMode, "audio.cue.playback-mode-mismatch", issues);
            ExpectBool(cueValue, "Enabled", expectation.Enabled, "audio.cue.enabled-mismatch", issues);
            ExpectBool(
                cueValue,
                "RequiredForRelease",
                expectation.RequiredForRelease,
                "audio.cue.required-mismatch",
                issues
            );
            ExpectBool(cueValue, "IsPlaceholder", expectation.IsPlaceholder, "audio.cue.placeholder-mismatch", issues);
        }
        ExpectBool(cueValue, "Loop", false, "audio.cue.loop-mismatch", issues);
        ExpectString(
            cueValue,
            "ListeningStatus",
            "PendingRealMachine",
            "audio.cue.listening-status-mismatch",
            issues
        );
        pending.Add(cueId);
        if (expectation?.IsPlaceholder == true)
            placeholders.Add(cueId);
        if (expectation?.Enabled == false)
            disabled.Add(cueId);

        if (!TryArray(cueValue, "Clips", "audio.clips.missing", issues, out var clips))
            return 0;
        var clipArray = clips.EnumerateArray().ToArray();
        if (expectation is not null && clipArray.Length != expectation.ClipCount)
        {
            issues.Add(
                new(
                    "audio.cue.clip-count-mismatch",
                    $"Cue '{cueId}' expects {expectation.ClipCount} clips but has {clipArray.Length}."
                )
            );
        }
        foreach (var clip in clipArray)
            ValidateClip(clip, deploymentRoot, issues, clipIds, paths);
        return clipArray.Length;
    }

    private static void ValidateClip(
        JsonElement clip,
        string deploymentRoot,
        ICollection<SanityAudioContractIssue> issues,
        ISet<string> clipIds,
        ISet<string> paths
    )
    {
        if (clip.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("audio.clip.invalid-shape", "Each clip must be an object."));
            return;
        }
        var clipId = RequiredString(clip, "ClipId", "audio.clip.id-missing", issues);
        if (!clipIds.Add(clipId))
            issues.Add(new("audio.clip.duplicate-id", $"ClipId '{clipId}' is duplicated case-insensitively."));
        var path = RequiredString(clip, "Path", "audio.clip.path-missing", issues);
        if (!paths.Add(path))
            issues.Add(new("audio.clip.duplicate-path", $"Clip path '{path}' is duplicated case-insensitively."));
        if (!path.StartsWith("Asset/Sanity/Audio/", StringComparison.Ordinal) || !path.EndsWith(".wav", StringComparison.Ordinal))
            issues.Add(new("audio.clip.path-outside-root", $"Clip path '{path}' must be an ASCII WAV path below Asset/Sanity/Audio/."));
        if (!IsAscii(path))
            issues.Add(new("audio.clip.path-non-ascii", $"Clip path '{path}' must contain ASCII characters only."));
        if (!SanityAssetPathPolicy.TryResolveFromDeploymentRoot(deploymentRoot, path, out var absolutePath, out var reason))
        {
            issues.Add(new("audio.clip.invalid-path", reason));
            return;
        }
        ExpectString(clip, "FormatId", RuntimeFormatId, "audio.clip.format-id-mismatch", issues);
        ExpectBool(clip, "Loop", false, "audio.clip.loop-mismatch", issues);
        var declaredHash = RequiredString(clip, "Sha256", "audio.clip.hash-missing", issues);
        var declaredFrames = RequiredInt64(clip, "DurationFrames", "audio.clip.duration-frames-missing", issues);
        var declaredSeconds = RequiredDouble(clip, "DurationSeconds", "audio.clip.duration-seconds-missing", issues);
        if (!File.Exists(absolutePath))
        {
            issues.Add(new("audio.clip.file-missing", $"Clip file '{path}' does not exist."));
            return;
        }

        var inspection = SanityWavInspector.InspectFile(absolutePath);
        foreach (var issue in inspection.Issues)
            issues.Add(new($"audio.clip.{issue.Code}", $"{path}: {issue.Reason}"));
        if (!inspection.Success)
            return;
        if (
            inspection.FormatCode != 1
            || inspection.Channels != 2
            || inspection.SampleRateHz != 44100
            || inspection.BitsPerSample != 16
            || inspection.BlockAlign != 4
            || inspection.ByteRate != 176400
        )
        {
            issues.Add(new("audio.clip.runtime-format-mismatch", $"Clip '{path}' does not match the frozen runtime PCM format."));
        }
        if (!string.Equals(declaredHash, inspection.Sha256, StringComparison.Ordinal))
            issues.Add(new("audio.clip.hash-mismatch", $"Clip '{path}' has a stale SHA-256."));
        if (declaredFrames != inspection.Frames)
            issues.Add(new("audio.clip.duration-frames-mismatch", $"Clip '{path}' has a stale frame count."));
        if (Math.Abs(declaredSeconds - inspection.DurationSeconds) > 0.0000005d)
            issues.Add(new("audio.clip.duration-seconds-mismatch", $"Clip '{path}' has a stale duration."));

        if (TryObject(clip, "GainEvidence", "audio.clip.gain-evidence-missing", issues, out var gain))
        {
            ExpectDouble(gain, "GainAppliedDb", 0d, "audio.clip.gain-applied", issues);
            ExpectBool(gain, "NormalizationApplied", false, "audio.clip.normalization-applied", issues);
        }
        if (TryObject(clip, "TailEvidence", "audio.clip.tail-evidence-missing", issues, out var tail))
        {
            ExpectInt(tail, "TrimmedFrames", 0, "audio.clip.unrecorded-trim", issues);
            ExpectBool(tail, "ClickRepairApplied", false, "audio.clip.unrecorded-click-repair", issues);
        }
        if (TryObject(clip, "ListeningEvidence", "audio.clip.listening-evidence-missing", issues, out var listening))
        {
            ExpectString(
                listening,
                "OverallStatus",
                "PendingRealMachine",
                "audio.clip.listening-status-mismatch",
                issues
            );
        }
    }

    private static IReadOnlyDictionary<string, CueExpectation> CreateExpectedCues()
    {
        var values = new Dictionary<string, CueExpectation>(StringComparer.Ordinal)
        {
            ["sanity.cue.ambience.low-sanity"] = new(
                "sanity.cue.ambience",
                "RandomContinuousOneShotPool",
                true,
                true,
                false,
                11
            ),
            ["sanity.cue.whispers.low-sanity"] = new(
                "sanity.cue.whispers",
                "RandomContinuousOneShotPool",
                true,
                true,
                false,
                11
            ),
            ["sanity.cue.thresholds.danger-enter"] = new(
                "sanity.cue.thresholds",
                "OneShot",
                true,
                true,
                false,
                1
            ),
            ["sanity.cue.darkness.warning"] = new(
                "sanity.cue.darkness",
                "CancelableOneShot",
                true,
                true,
                true,
                1
            ),
        };
        AddEvents(values, "dark-hand", new[] { "appear", "interact", "disappear" });
        AddEvents(values, "creeper-fear", new[] { "taunt", "attack", "hurt", "death" });
        AddEvents(values, "terrorbeak", new[] { "taunt", "attack", "hurt", "death" });
        foreach (var eventName in new[] { "gain", "loss" })
        {
            values[$"sanity.cue.sanity-change.{eventName}"] = new(
                "sanity.cue.sanity-change",
                "Disabled",
                false,
                false,
                true,
                0
            );
        }
        return values;
    }

    private static void AddEvents(
        IDictionary<string, CueExpectation> values,
        string group,
        IEnumerable<string> events
    )
    {
        foreach (var eventName in events)
        {
            values[$"sanity.cue.{group}.{eventName}"] = new(
                $"sanity.cue.{group}",
                "OneShot",
                true,
                true,
                true,
                1
            );
        }
    }

    private static void ValidateDuplicateProperties(
        JsonElement value,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    issues.Add(new("audio.metadata.duplicate-property", $"Property '{property.Name}' is duplicated case-insensitively."));
                ValidateDuplicateProperties(property.Value, issues);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                ValidateDuplicateProperties(item, issues);
        }
    }

    private static void ValidateForbiddenFieldsAndStrings(
        JsonElement value,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var normalized = property.Name.Replace("_", string.Empty).Replace("-", string.Empty);
                if (ForbiddenFieldNames.Contains(normalized))
                    issues.Add(new("audio.metadata.forbidden-field", $"Stage-05 metadata must not own field '{property.Name}'."));
                ValidateForbiddenFieldsAndStrings(property.Value, issues);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                ValidateForbiddenFieldsAndStrings(item, issues);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (
                text.Contains("references/", StringComparison.OrdinalIgnoreCase)
                || text.Contains("references\\", StringComparison.OrdinalIgnoreCase)
                || text.Contains("TestPackage", StringComparison.OrdinalIgnoreCase)
                || text.Contains("_work", StringComparison.OrdinalIgnoreCase)
            )
            {
                issues.Add(new("audio.metadata.development-path-forbidden", "Formal audio metadata must not contain development-only paths."));
            }
        }
    }

    private static bool TryReadStrictUtf8(
        string path,
        string codePrefix,
        ICollection<SanityAudioContractIssue> issues,
        out string text
    )
    {
        text = string.Empty;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(false, true));
            return true;
        }
        catch (FileNotFoundException)
        {
            issues.Add(new($"{codePrefix}.missing", $"File '{path}' does not exist."));
        }
        catch (DirectoryNotFoundException)
        {
            issues.Add(new($"{codePrefix}.missing", $"File '{path}' does not exist."));
        }
        catch (DecoderFallbackException)
        {
            issues.Add(new($"{codePrefix}.invalid-utf8", $"File '{path}' is not strict UTF-8."));
        }
        catch (IOException exception)
        {
            issues.Add(new($"{codePrefix}.read-failed", exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            issues.Add(new($"{codePrefix}.read-failed", exception.Message));
        }
        return false;
    }

    private static bool TryObject(
        JsonElement owner,
        string property,
        string code,
        ICollection<SanityAudioContractIssue> issues,
        out JsonElement value
    )
    {
        if (owner.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        issues.Add(new(code, $"Property '{property}' must be an object."));
        return false;
    }

    private static bool TryArray(
        JsonElement owner,
        string property,
        string code,
        ICollection<SanityAudioContractIssue> issues,
        out JsonElement value
    )
    {
        if (owner.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Array)
            return true;
        issues.Add(new(code, $"Property '{property}' must be an array."));
        return false;
    }

    private static string RequiredString(
        JsonElement owner,
        string property,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (
            owner.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
        )
            return value.GetString()!;
        issues.Add(new(code, $"Property '{property}' must be a non-empty string."));
        return string.Empty;
    }

    private static long RequiredInt64(
        JsonElement owner,
        string property,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (owner.TryGetProperty(property, out var value) && value.TryGetInt64(out var number))
            return number;
        issues.Add(new(code, $"Property '{property}' must be an integer."));
        return -1;
    }

    private static double RequiredDouble(
        JsonElement owner,
        string property,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (owner.TryGetProperty(property, out var value) && value.TryGetDouble(out var number))
            return number;
        issues.Add(new(code, $"Property '{property}' must be numeric."));
        return -1d;
    }

    private static void ExpectString(
        JsonElement owner,
        string property,
        string expected,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (
            !owner.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() != expected
        )
            issues.Add(new(code, $"Property '{property}' must equal '{expected}'."));
    }

    private static void ExpectInt(
        JsonElement owner,
        string property,
        int expected,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (!owner.TryGetProperty(property, out var value) || !value.TryGetInt32(out var actual) || actual != expected)
            issues.Add(new(code, $"Property '{property}' must equal {expected}."));
    }

    private static void ExpectDouble(
        JsonElement owner,
        string property,
        double expected,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (!owner.TryGetProperty(property, out var value) || !value.TryGetDouble(out var actual) || actual != expected)
            issues.Add(new(code, $"Property '{property}' must equal {expected}."));
    }

    private static void ExpectBool(
        JsonElement owner,
        string property,
        bool expected,
        string code,
        ICollection<SanityAudioContractIssue> issues
    )
    {
        if (
            !owner.TryGetProperty(property, out var value)
            || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            || value.GetBoolean() != expected
        )
            issues.Add(new(code, $"Property '{property}' must equal {expected.ToString().ToLowerInvariant()}."));
    }

    private static bool IsAscii(string value)
    {
        return value.All(character => character >= 0x20 && character <= 0x7E);
    }

    private static SanityAudioContractValidationResult Empty(
        IReadOnlyList<SanityAudioContractIssue> issues
    )
    {
        return new SanityAudioContractValidationResult(
            issues,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>()
        );
    }

    private static SanityAudioContractValidationResult Result(
        IReadOnlyList<SanityAudioContractIssue> issues,
        IEnumerable<string> disabled,
        IEnumerable<string> placeholders,
        IEnumerable<string> pending
    )
    {
        return new SanityAudioContractValidationResult(
            issues,
            disabled.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            placeholders.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            pending.OrderBy(value => value, StringComparer.Ordinal).ToArray()
        );
    }

    private sealed record CueSetExpectation(
        string Group,
        string CachePolicy,
        string LifecyclePolicy,
        int MaxConcurrentInstances
    );

    private sealed record CueExpectation(
        string CueSetId,
        string PlaybackMode,
        bool Enabled,
        bool RequiredForRelease,
        bool IsPlaceholder,
        int ClipCount
    );
}
