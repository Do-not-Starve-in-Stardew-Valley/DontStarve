#!/usr/bin/env python3
"""Build the stage-05 Sanity WAV assets and their offline evidence contract.

This script is intentionally standard-library-only.  It reads the frozen reference WAVs as
offline evidence, verifies every source hash before processing, and writes only formal
Asset/Sanity/Audio outputs plus stage-owned metadata/template/recipe files.  Runtime and tests
must never call this script or read its reference paths.
"""

from __future__ import annotations

from array import array
import hashlib
import json
from pathlib import Path
import platform
import struct
import sys
import wave
from typing import Any, Iterable


RUNTIME_FORMAT = {
    "FormatId": "sanity.wav.pcm-s16-stereo-44100-v1",
    "Container": "RIFF",
    "Codec": "PCM",
    "FormatCode": 1,
    "Channels": 2,
    "SampleRateHz": 44100,
    "BitsPerSample": 16,
    "BlockAlign": 4,
    "ByteRate": 176400,
}

SOURCE_ROOT = Path(
    "references/san值系统设计稿重整/素材/待加工原始素材/音频"
)


def source_specs() -> list[dict[str, Any]]:
    ambience_hashes = (
        "C702115BEA67EE8EE1BCF7D6FC7D62A812288852AEBE10E1DB4D12A9FD83628D",
        "7746186A52831C921931F94F339C41269FBDE81AA70A84E019DDF0D750C98FA3",
        "EF6BA30F3A3EFAFC8850EB874C96CFA477BEA0810E1D52C0303823001AB4B78F",
        "2DD94D8EBC8EE9B5AC9D854E61360A2494372A6D5C430E60B017813553DEE2B9",
        "035652855F22390A2B53C2D296575C93E2280C74619FA92136B077EFDC58F44D",
        "A0C578313293543FF079F3C7E288D10B165CBEBB174BBD283566686121243100",
        "A755C15B70D8DD82A5EFCFD4C0B0D29A4B48019C68FE000CA6B3F3B3E8CA92A3",
        "93C8A50C0E46AEC69311FC2B4297651FC8234341C7B92E18732BEBCCC009489B",
        "0EBBBAB17B844D24DE534F9BD7D146B4E68880DBD68B349E61EDE6724F208DEE",
        "ACC86E24B33F774A749ED823387E17E63E857ECD95D1FB0F6AAE7FA341CB993E",
        "250E387335D7BE71CECFCD960641A9FCDCDBEE8C4FBD8FDF4E28198F3F133C4F",
    )
    whisper_hashes = (
        "5502C90B465866833B16716D4BDE2BB564667B9FF8F1898A7318F2B04E0724BE",
        "0D7E034289AC90C678FE67F47CE073B671662FA63FC8891F4197EC1CABC1367D",
        "E25200679E166E7A856D48D7A4DDD625DE37AE9B2458DCF712952887432E8EC4",
        "6DA3EBD05A6CD9C7174A7A7E50240A2C4685DD5C52723E90DC2DD0410E17C0F3",
        "E592068F7E10D5ABE0414EFF3545E2DAA9FF2AA158BA5AD5557DD6AF9B25532C",
        "D87F345BB52AEBEC431ECA9962F948E7962B6616275DD2CF0EB86903E1B61943",
        "EB794170788579D3CB48D86FB329C1B125E2AAE30B1D459FA401A93F3E42D9B8",
        "6ECEDD9DC050343B4955808E8F910C2A4878E6AA74A3D1E9B02A7B2661A75A9C",
        "4B99DC05554471658C33350A4E2385520DAC2BEB89F0F1E044E59C2337E7ECAD",
        "91AB66BA92BEF1FDFCCB5CF4ABF8E2EFE7F36829D159E0CF5AEC93063C37B919",
        "CFC2E4A174B2D23ACCA82D9A5590C5C6AEF56BBF6B38EF50E2E1CAC2A027529A",
    )
    result: list[dict[str, Any]] = []
    for index, expected_hash in enumerate(ambience_hashes):
        result.append(
            {
                "EvidenceId": "ART-07",
                "Group": "ambience",
                "Source": SOURCE_ROOT
                / "01-低理智环境声-50pct"
                / f"dontstarve_sanity_sanity_{index:03d}.wav",
                "ExpectedSha256": expected_hash,
                "ClipId": f"sanity.clip.ambience.{index + 1:02d}",
                "Output": Path("DontStarve/Asset/Sanity/Audio/Ambience")
                / f"ambience-{index + 1:02d}.wav",
            }
        )
    for index, expected_hash in enumerate(whisper_hashes):
        result.append(
            {
                "EvidenceId": "ART-08",
                "Group": "whispers",
                "Source": SOURCE_ROOT
                / "02-低语-45pct"
                / f"dontstarve_sanity_sanity_random_2_{index:03d}.wav",
                "ExpectedSha256": expected_hash,
                "ClipId": f"sanity.clip.whispers.{index + 1:02d}",
                "Output": Path("DontStarve/Asset/Sanity/Audio/Whispers")
                / f"whisper-{index + 1:02d}.wav",
            }
        )
    result.append(
        {
            "EvidenceId": "ART-09",
            "Group": "thresholds",
            "Source": SOURCE_ROOT / "03-危险阈值-15pct" / "低san阈值音效.wav",
            "ExpectedSha256": "DAB3249DE3D6F53BDD1270EEBE6E0B71A2DEBCA9E464206B9DBDDE21F0E9CC5D",
            "ClipId": "sanity.clip.thresholds.danger-enter",
            "Output": Path("DontStarve/Asset/Sanity/Audio/Thresholds/danger-enter.wav"),
        }
    )
    return result


PLACEHOLDERS = (
    ("sanity.clip.darkness.warning", "Events/darkness-warning.wav", 330),
    ("sanity.clip.dark-hand.appear", "Creatures/dark-hand/appear.wav", 370),
    ("sanity.clip.dark-hand.interact", "Creatures/dark-hand/interact.wav", 410),
    ("sanity.clip.dark-hand.disappear", "Creatures/dark-hand/disappear.wav", 450),
    ("sanity.clip.creeper-fear.taunt", "Creatures/creeper-fear/taunt.wav", 490),
    ("sanity.clip.creeper-fear.attack", "Creatures/creeper-fear/attack.wav", 530),
    ("sanity.clip.creeper-fear.hurt", "Creatures/creeper-fear/hurt.wav", 570),
    ("sanity.clip.creeper-fear.death", "Creatures/creeper-fear/death.wav", 610),
    ("sanity.clip.terrorbeak.taunt", "Creatures/terrorbeak/taunt.wav", 650),
    ("sanity.clip.terrorbeak.attack", "Creatures/terrorbeak/attack.wav", 690),
    ("sanity.clip.terrorbeak.hurt", "Creatures/terrorbeak/hurt.wav", 730),
    ("sanity.clip.terrorbeak.death", "Creatures/terrorbeak/death.wav", 770),
)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def read_pcm16_stereo(path: Path) -> tuple[int, array, dict[str, Any]]:
    # All 23 evidence WAVs declare the outer RIFF length four bytes short while their data
    # chunks are complete.  Parse chunks against the physical file, record that defect, and
    # rebuild a canonical header; using wave.open here would silently drop the last frame.
    payload_bytes = path.read_bytes()
    if len(payload_bytes) < 44 or payload_bytes[:4] != b"RIFF" or payload_bytes[8:12] != b"WAVE":
        raise ValueError(f"{path}: missing RIFF/WAVE header")
    riff_declared_file_bytes = struct.unpack_from("<I", payload_bytes, 4)[0] + 8
    offset = 12
    format_values: tuple[int, int, int, int, int, int] | None = None
    data_payload: bytes | None = None
    data_declared_bytes: int | None = None
    while offset + 8 <= len(payload_bytes):
        chunk_id = payload_bytes[offset : offset + 4]
        chunk_size = struct.unpack_from("<I", payload_bytes, offset + 4)[0]
        chunk_start = offset + 8
        chunk_end = chunk_start + chunk_size
        if chunk_end > len(payload_bytes):
            raise ValueError(f"{path}: chunk {chunk_id!r} exceeds the physical file")
        if chunk_id == b"fmt ":
            if chunk_size < 16:
                raise ValueError(f"{path}: fmt chunk is shorter than 16 bytes")
            format_values = struct.unpack_from("<HHIIHH", payload_bytes, chunk_start)
        elif chunk_id == b"data":
            data_declared_bytes = chunk_size
            data_payload = payload_bytes[chunk_start:chunk_end]
            break
        offset = chunk_end + (chunk_size & 1)
    if format_values is None or data_payload is None or data_declared_bytes is None:
        raise ValueError(f"{path}: missing fmt or data chunk")
    format_code, channels, rate, byte_rate, block_align, bits = format_values
    if format_code != 1:
        raise ValueError(f"{path}: compressed or unknown format code {format_code}")
    if channels != 2 or bits != 16 or block_align != 4 or byte_rate != rate * 4:
        raise ValueError(f"{path}: expected coherent PCM 16-bit stereo input")
    if not data_payload or len(data_payload) % block_align != 0:
        raise ValueError(f"{path}: empty or partial PCM frame payload")
    samples = array("h")
    samples.frombytes(data_payload)
    if sys.byteorder != "little":
        samples.byteswap()
    frames = len(data_payload) // block_align
    if len(samples) != frames * 2 or frames <= 0:
        raise ValueError(f"{path}: empty or malformed PCM payload")
    container_evidence = {
        "ActualFileBytes": len(payload_bytes),
        "RiffDeclaredFileBytes": riff_declared_file_bytes,
        "RiffLengthDeltaBytes": len(payload_bytes) - riff_declared_file_bytes,
        "DataDeclaredBytes": data_declared_bytes,
        "DataDecodedBytes": len(data_payload),
        "CanonicalHeaderRebuilt": riff_declared_file_bytes != len(payload_bytes),
        "CanonicalHeaderRepairReason": (
            "Source RIFF outer length was four bytes short; the complete declared data chunk was retained."
            if riff_declared_file_bytes != len(payload_bytes)
            else None
        ),
    }
    return rate, samples, container_evidence


def round_ratio(numerator: int, denominator: int) -> int:
    if numerator >= 0:
        return (numerator + denominator // 2) // denominator
    return -((-numerator + denominator // 2) // denominator)


def resample_linear(samples: array, source_rate: int, target_rate: int) -> array:
    source_frames = len(samples) // 2
    if source_rate == target_rate:
        return array("h", samples)
    target_frames = max(1, round_ratio(source_frames * target_rate, source_rate))
    result = array("h")
    result.extend([0] * (target_frames * 2))
    for target_frame in range(target_frames):
        position_numerator = target_frame * source_rate
        left = position_numerator // target_rate
        fraction = position_numerator % target_rate
        if left >= source_frames - 1:
            left = source_frames - 1
            right = left
            fraction = 0
        else:
            right = left + 1
        for channel in (0, 1):
            left_sample = samples[left * 2 + channel]
            right_sample = samples[right * 2 + channel]
            mixed = left_sample * (target_rate - fraction) + right_sample * fraction
            value = round_ratio(mixed, target_rate)
            result[target_frame * 2 + channel] = max(-32768, min(32767, value))
    return result


def samples_to_bytes(samples: array) -> bytes:
    output = array("h", samples)
    if sys.byteorder != "little":
        output.byteswap()
    return output.tobytes()


def write_wav(path: Path, samples: array) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as writer:
        writer.setnchannels(RUNTIME_FORMAT["Channels"])
        writer.setsampwidth(RUNTIME_FORMAT["BitsPerSample"] // 8)
        writer.setframerate(RUNTIME_FORMAT["SampleRateHz"])
        writer.setcomptype("NONE", "not compressed")
        writer.writeframes(samples_to_bytes(samples))


def dbfs(value: float) -> float | None:
    if value <= 0:
        return None
    import math

    return round(20.0 * math.log10(value), 6)


def tail_window_seconds(samples: array, rate: int, threshold_dbfs: int) -> float:
    import math

    frames = len(samples) // 2
    window_frames = max(1, rate // 20)
    threshold = 10.0 ** (threshold_dbfs / 20.0)
    trailing_frames = 0
    end = frames
    while end > 0:
        start = max(0, end - window_frames)
        total = 0
        count = (end - start) * 2
        for index in range(start * 2, end * 2):
            sample = samples[index]
            total += sample * sample
        window_rms = math.sqrt(total / count) / 32768.0 if count else 0.0
        if window_rms > threshold:
            break
        trailing_frames += end - start
        end = start
    return round(trailing_frames / rate, 6)


def audio_evidence(samples: array, rate: int) -> dict[str, Any]:
    import math

    sample_count = len(samples)
    peak_sample = max(abs(sample) for sample in samples)
    squared_sum = sum(sample * sample for sample in samples)
    peak = peak_sample / 32768.0
    rms = math.sqrt(squared_sum / sample_count) / 32768.0
    frames = sample_count // 2
    exact_zero_frames = 0
    for frame in range(frames - 1, -1, -1):
        if samples[frame * 2] != 0 or samples[frame * 2 + 1] != 0:
            break
        exact_zero_frames += 1
    first = [int(samples[0]), int(samples[1])]
    last = [int(samples[-2]), int(samples[-1])]
    boundary_peak = max(abs(value) for value in first + last) / 32768.0
    return {
        "Frames": frames,
        "DurationSeconds": round(frames / rate, 6),
        "PeakLinear": round(peak, 9),
        "PeakDbfs": dbfs(peak),
        "RmsLinear": round(rms, 9),
        "RmsDbfs": dbfs(rms),
        "ExactZeroTailFrames": exact_zero_frames,
        "ExactZeroTailSeconds": round(exact_zero_frames / rate, 6),
        "TrailingBelowMinus60DbfsSeconds": tail_window_seconds(samples, rate, -60),
        "TrailingBelowMinus50DbfsSeconds": tail_window_seconds(samples, rate, -50),
        "TrailingBelowMinus40DbfsSeconds": tail_window_seconds(samples, rate, -40),
        "FirstFrameSamples": first,
        "LastFrameSamples": last,
        "BoundaryPeakLinear": round(boundary_peak, 9),
    }


def source_format(rate: int, frames: int) -> dict[str, Any]:
    return {
        "Container": "RIFF",
        "Codec": "PCM",
        "FormatCode": 1,
        "Channels": 2,
        "SampleRateHz": rate,
        "BitsPerSample": 16,
        "BlockAlign": 4,
        "ByteRate": rate * 4,
        "Frames": frames,
    }


def runtime_path(repository_path: Path) -> str:
    parts = repository_path.as_posix().split("/", 1)
    if parts[0] != "DontStarve" or len(parts) != 2:
        raise ValueError(f"output is not beneath the deployed mod root: {repository_path}")
    return parts[1]


def clip_record(
    clip_id: str,
    output_path: Path,
    output_samples: array,
    *,
    source: dict[str, Any],
    placeholder: bool,
) -> dict[str, Any]:
    output_evidence = audio_evidence(output_samples, RUNTIME_FORMAT["SampleRateHz"])
    return {
        "ClipId": clip_id,
        "Path": runtime_path(output_path),
        "Sha256": sha256_file(output_path),
        "FormatId": RUNTIME_FORMAT["FormatId"],
        "DurationFrames": output_evidence["Frames"],
        "DurationSeconds": output_evidence["DurationSeconds"],
        "IsPlaceholder": placeholder,
        "Loop": False,
        "SourceEvidence": source,
        "GainEvidence": {
            "GainAppliedDb": 0.0,
            "NormalizationApplied": False,
            "SourcePeakLinear": source.get("PeakLinear"),
            "SourcePeakDbfs": source.get("PeakDbfs"),
            "SourceRmsLinear": source.get("RmsLinear"),
            "SourceRmsDbfs": source.get("RmsDbfs"),
            "OutputPeakLinear": output_evidence["PeakLinear"],
            "OutputPeakDbfs": output_evidence["PeakDbfs"],
            "OutputRmsLinear": output_evidence["RmsLinear"],
            "OutputRmsDbfs": output_evidence["RmsDbfs"],
        },
        "TailEvidence": {
            "TrimmedFrames": 0,
            "ClickRepairApplied": False,
            "SourceExactZeroTailFrames": source.get("ExactZeroTailFrames"),
            "SourceExactZeroTailSeconds": source.get("ExactZeroTailSeconds"),
            "OutputExactZeroTailFrames": output_evidence["ExactZeroTailFrames"],
            "OutputExactZeroTailSeconds": output_evidence["ExactZeroTailSeconds"],
            "OutputTrailingBelowMinus60DbfsSeconds": output_evidence[
                "TrailingBelowMinus60DbfsSeconds"
            ],
            "OutputTrailingBelowMinus50DbfsSeconds": output_evidence[
                "TrailingBelowMinus50DbfsSeconds"
            ],
            "OutputTrailingBelowMinus40DbfsSeconds": output_evidence[
                "TrailingBelowMinus40DbfsSeconds"
            ],
            "OutputFirstFrameSamples": output_evidence["FirstFrameSamples"],
            "OutputLastFrameSamples": output_evidence["LastFrameSamples"],
            "OutputBoundaryPeakLinear": output_evidence["BoundaryPeakLinear"],
        },
        "ListeningEvidence": {
            "OverallStatus": "PendingRealMachine",
            "OneShotStatus": "PendingRealMachine",
            "LoopSeamStatus": "NotApplicableIndividualClip",
            "PoolTransitionStatus": "PendingRealMachine",
            "StopTailStatus": "PendingRealMachine",
            "Reason": "No game-audio playback session was performed in stage 05.",
        },
    }


def build_real_clips(repository_root: Path) -> tuple[dict[str, dict[str, Any]], list[dict[str, Any]]]:
    clips: dict[str, dict[str, Any]] = {}
    recipe_inputs: list[dict[str, Any]] = []
    specs = source_specs()
    # Verify the complete read-only input set before replacing any generated output.
    for spec in specs:
        source_path = repository_root / spec["Source"]
        actual_hash = sha256_file(source_path)
        if actual_hash != spec["ExpectedSha256"]:
            raise ValueError(
                f"source drift for {spec['Source']}: expected {spec['ExpectedSha256']}, got {actual_hash}"
            )
    for spec in specs:
        source_path = repository_root / spec["Source"]
        actual_hash = spec["ExpectedSha256"]
        source_rate, source_samples, container_evidence = read_pcm16_stereo(source_path)
        source_audio = audio_evidence(source_samples, source_rate)
        source_detail = {
            "Status": "AvailableReadOnlyEvidence",
            "EvidenceId": spec["EvidenceId"],
            "Sha256": actual_hash,
            "Bytes": source_path.stat().st_size,
            "Format": source_format(source_rate, source_audio["Frames"]),
            "ContainerEvidence": container_evidence,
            **source_audio,
        }
        output_samples = resample_linear(
            source_samples, source_rate, RUNTIME_FORMAT["SampleRateHz"]
        )
        output_path = repository_root / spec["Output"]
        write_wav(output_path, output_samples)
        clips[spec["ClipId"]] = clip_record(
            spec["ClipId"],
            spec["Output"],
            output_samples,
            source=source_detail,
            placeholder=False,
        )
        recipe_inputs.append(
            {
                "ClipId": spec["ClipId"],
                "EvidenceId": spec["EvidenceId"],
                "ReferencePath": spec["Source"].as_posix(),
                "Sha256": actual_hash,
                "Bytes": source_path.stat().st_size,
                "Format": source_detail["Format"],
                "ContainerEvidence": container_evidence,
                "AudioEvidence": source_audio,
            }
        )
    return clips, recipe_inputs


def placeholder_samples(base_frequency_hz: int) -> array:
    rate = RUNTIME_FORMAT["SampleRateHz"]
    total_frames = rate * 12 // 25  # 480 ms
    pulse_frames = rate * 9 // 100  # 90 ms
    gap_frames = rate * 4 // 100  # 40 ms
    fade_frames = rate // 100  # 10 ms
    amplitude = 3932  # 12% full scale; obvious but not a final loudness choice.
    result = array("h")
    for frame in range(total_frames):
        pulse_index = frame // (pulse_frames + gap_frames)
        within = frame % (pulse_frames + gap_frames)
        value = 0
        if pulse_index < 3 and within < pulse_frames:
            frequency = base_frequency_hz + pulse_index * 73
            phase = (within * frequency * 2) // rate
            phase_position = (within * frequency * 4) % rate
            quarter = rate // 4
            if phase_position < quarter:
                triangle = phase_position
            elif phase_position < quarter * 3:
                triangle = quarter * 2 - phase_position
            else:
                triangle = phase_position - rate
            value = round_ratio(triangle * amplitude, quarter)
            if phase & 1:
                value = -value
            envelope = min(within, pulse_frames - 1 - within, fade_frames)
            value = round_ratio(value * max(0, envelope), fade_frames)
        result.append(value)
        result.append(value)
    return result


def build_placeholder_clips(repository_root: Path) -> dict[str, dict[str, Any]]:
    clips: dict[str, dict[str, Any]] = {}
    for clip_id, relative_output, frequency in PLACEHOLDERS:
        output = Path("DontStarve/Asset/Sanity/Audio") / relative_output
        samples = placeholder_samples(frequency)
        write_wav(repository_root / output, samples)
        source = {
            "Status": "MissingFinalAsset",
            "EvidenceId": "sanity4-missing-asset-ledger",
            "Sha256": None,
            "Bytes": None,
            "Format": None,
            "PeakLinear": None,
            "PeakDbfs": None,
            "RmsLinear": None,
            "RmsDbfs": None,
            "ExactZeroTailFrames": None,
            "ExactZeroTailSeconds": None,
        }
        record = clip_record(
            clip_id,
            output,
            samples,
            source=source,
            placeholder=True,
        )
        record["PlaceholderGenerator"] = {
            "Kind": "DEV-PLACEHOLDER-TRIPLE-TRIANGLE-BEEP",
            "BaseFrequencyHz": frequency,
            "PulseCount": 3,
            "DurationMilliseconds": 480,
            "PeakTargetLinear": 0.12,
            "FinalAssetEligible": False,
        }
        clips[clip_id] = record
    return clips


def cue(
    cue_id: str,
    playback_mode: str,
    clip_ids: Iterable[str],
    clips: dict[str, dict[str, Any]],
    *,
    required: bool,
    placeholder: bool,
    enabled: bool = True,
) -> dict[str, Any]:
    return {
        "CueId": cue_id,
        "PlaybackMode": playback_mode,
        "Enabled": enabled,
        "RequiredForRelease": required,
        "IsPlaceholder": placeholder,
        "Loop": False,
        "LoopDecisionStatus": (
            "ProvisionalPoolUsesOneShotClips" if "Pool" in playback_mode else "NotLooped"
        ),
        "ListeningStatus": "PendingRealMachine",
        "Clips": [clips[clip_id] for clip_id in clip_ids],
    }


def cue_set(
    cue_set_id: str,
    group: str,
    cache_policy: str,
    lifecycle_policy: str,
    max_concurrent: int,
    cues: list[dict[str, Any]],
) -> dict[str, Any]:
    return {
        "CueSetId": cue_set_id,
        "Group": group,
        "CachePolicy": cache_policy,
        "LifecyclePolicy": lifecycle_policy,
        "MaxConcurrentInstances": max_concurrent,
        "ContractVersion": 1,
        "Cues": cues,
    }


def build_metadata(clips: dict[str, dict[str, Any]]) -> dict[str, Any]:
    ambience_ids = [f"sanity.clip.ambience.{index:02d}" for index in range(1, 12)]
    whisper_ids = [f"sanity.clip.whispers.{index:02d}" for index in range(1, 12)]
    sets = [
        cue_set(
            "sanity.cue.ambience",
            "low-sanity-ambience",
            "LazyPerCueSetBounded",
            "StopOnTierExitOrDisabled;ReleaseOnWorldTitleDispose",
            1,
            [
                cue(
                    "sanity.cue.ambience.low-sanity",
                    "RandomContinuousOneShotPool",
                    ambience_ids,
                    clips,
                    required=True,
                    placeholder=False,
                )
            ],
        ),
        cue_set(
            "sanity.cue.whispers",
            "low-sanity-whispers",
            "LazyPerCueSetBounded",
            "StopOnTierExitOrDisabled;ReleaseOnWorldTitleDispose",
            1,
            [
                cue(
                    "sanity.cue.whispers.low-sanity",
                    "RandomContinuousOneShotPool",
                    whisper_ids,
                    clips,
                    required=True,
                    placeholder=False,
                )
            ],
        ),
        cue_set(
            "sanity.cue.thresholds",
            "danger-threshold",
            "LazyPerCueSetBounded",
            "OneShotOnIdempotentEnter;ReleaseOnWorldTitleDispose",
            1,
            [
                cue(
                    "sanity.cue.thresholds.danger-enter",
                    "OneShot",
                    ["sanity.clip.thresholds.danger-enter"],
                    clips,
                    required=True,
                    placeholder=False,
                )
            ],
        ),
        cue_set(
            "sanity.cue.darkness",
            "darkness-warning",
            "LazyPerCueSetBounded",
            "CancelableOneShot;ReleaseOnWorldTitleDispose",
            1,
            [
                cue(
                    "sanity.cue.darkness.warning",
                    "CancelableOneShot",
                    ["sanity.clip.darkness.warning"],
                    clips,
                    required=True,
                    placeholder=True,
                )
            ],
        ),
        cue_set(
            "sanity.cue.dark-hand",
            "dark-hand-actor",
            "LazyPerCueSetBounded",
            "StopOnActorOrWorldCleanup;ReleaseOnTitleDispose",
            1,
            [
                cue(
                    f"sanity.cue.dark-hand.{event}",
                    "OneShot",
                    [f"sanity.clip.dark-hand.{event}"],
                    clips,
                    required=True,
                    placeholder=True,
                )
                for event in ("appear", "interact", "disappear")
            ],
        ),
        cue_set(
            "sanity.cue.creeper-fear",
            "creeper-fear-actor",
            "LazyPerCueSetBounded",
            "StopOnEntityOrWorldCleanup;ReleaseOnTitleDispose",
            1,
            [
                cue(
                    f"sanity.cue.creeper-fear.{event}",
                    "OneShot",
                    [f"sanity.clip.creeper-fear.{event}"],
                    clips,
                    required=True,
                    placeholder=True,
                )
                for event in ("taunt", "attack", "hurt", "death")
            ],
        ),
        cue_set(
            "sanity.cue.terrorbeak",
            "terrorbeak-actor",
            "LazyPerCueSetBounded",
            "StopOnEntityOrWorldCleanup;ReleaseOnTitleDispose",
            1,
            [
                cue(
                    f"sanity.cue.terrorbeak.{event}",
                    "OneShot",
                    [f"sanity.clip.terrorbeak.{event}"],
                    clips,
                    required=True,
                    placeholder=True,
                )
                for event in ("taunt", "attack", "hurt", "death")
            ],
        ),
        cue_set(
            "sanity.cue.sanity-change",
            "sanity-change-optional",
            "DisabledNoCache",
            "DisabledNoPlayback",
            0,
            [
                cue(
                    f"sanity.cue.sanity-change.{event}",
                    "Disabled",
                    [],
                    clips,
                    required=False,
                    placeholder=True,
                    enabled=False,
                )
                for event in ("gain", "loss")
            ],
        ),
    ]
    return {
        "SchemaVersion": 1,
        "ContractVersion": 1,
        "TemplateVersion": "sanity-audio-cues-v1",
        "RuntimeFormat": RUNTIME_FORMAT,
        "ListeningPolicy": {
            "MachineEvidenceStatus": "LocalVerified",
            "RealPlaybackStatus": "PendingRealMachine",
            "NoListeningInferenceFromMetrics": True,
        },
        "CueSets": sets,
    }


def update_manifest(repository_root: Path, audio_metadata_sha256: str) -> Path:
    path = repository_root / "DontStarve/Asset/Sanity/Data/sanity-assets.json"
    manifest = json.loads(path.read_text(encoding="utf-8"))
    cue_count = 0
    for slot in manifest["Slots"]:
        if slot["SlotId"].startswith("sanity.cue."):
            slot["Path"] = "Asset/Sanity/Audio/audio-cues.json"
            slot["Sha256"] = audio_metadata_sha256
            cue_count += 1
    if cue_count != 17:
        raise ValueError(f"expected 17 cue slots in sanity-assets.json, found {cue_count}")
    write_json(path, manifest)
    return path


def build_template(metadata: dict[str, Any]) -> dict[str, Any]:
    slots = []
    for cue_set_value in metadata["CueSets"]:
        for cue_value in cue_set_value["Cues"]:
            slots.append(
                {
                    "CueSetId": cue_set_value["CueSetId"],
                    "CueId": cue_value["CueId"],
                    "Group": cue_set_value["Group"],
                    "RequiredForRelease": cue_value["RequiredForRelease"],
                    "CurrentStatus": (
                        "DisabledOptional"
                        if not cue_value["Enabled"]
                        else "DEV-PLACEHOLDER"
                        if cue_value["IsPlaceholder"]
                        else "ConvertedCandidatePendingListening"
                    ),
                    "ReplacementPathRule": "Asset/Sanity/Audio/<stable-ascii-path>.wav",
                    "Loop": cue_value["Loop"],
                    "ListeningRequired": [
                        "one-shot-or-pool-transition",
                        "loop-seam-when-enabled",
                        "stop-tail",
                        "relative-level-with-original-gain-preserved",
                    ],
                }
            )
    return {
        "SchemaVersion": 1,
        "TemplateVersion": "sanity-audio-replacement-template-v1",
        "RuntimeFormat": RUNTIME_FORMAT,
        "Rules": {
            "PreserveOriginalGain": True,
            "DefaultLoudnessNormalization": False,
            "TailTrimRequiresRecordedParameters": True,
            "ClickRepairRequiresRecordedParameters": True,
            "ReferencesRemainReadOnly": True,
            "RuntimePathMustStartWith": "Asset/Sanity/Audio/",
            "RealPlaybackStatusUntilListened": "PendingRealMachine",
            "ReleaseAssetEligibleUntilFinalAndListened": False,
        },
        "ReplacementSlots": slots,
    }


def main() -> None:
    repository_root = Path(__file__).resolve().parents[2]
    clips, recipe_inputs = build_real_clips(repository_root)
    clips.update(build_placeholder_clips(repository_root))

    metadata = build_metadata(clips)
    metadata_path = repository_root / "DontStarve/Asset/Sanity/Audio/audio-cues.json"
    write_json(metadata_path, metadata)
    metadata_sha256 = sha256_file(metadata_path)
    manifest_path = update_manifest(repository_root, metadata_sha256)

    template_path = (
        repository_root / "scripts/sanity-assets/templates/audio/audio-replacement-template.json"
    )
    write_json(template_path, build_template(metadata))

    ordered_clips = sorted(clips.values(), key=lambda value: value["ClipId"])
    recipe = {
        "SchemaVersion": 1,
        "RecipeVersion": "sanity-audio-assets-v1",
        "Toolchain": {
            "Python": platform.python_version(),
            "Implementation": platform.python_implementation(),
            "Dependencies": "Python standard library only",
        },
        "Conversion": {
            "RuntimeFormat": RUNTIME_FORMAT,
            "Resampler": "deterministic signed-integer linear interpolation",
            "GainAppliedDb": 0.0,
            "NormalizationApplied": False,
            "TrimmedFrames": 0,
            "ClickRepairApplied": False,
            "ListeningStatus": "PendingRealMachine",
        },
        "Inputs": recipe_inputs,
        "Outputs": [
            {
                "ClipId": clip["ClipId"],
                "Path": clip["Path"],
                "Sha256": clip["Sha256"],
                "DurationFrames": clip["DurationFrames"],
                "DurationSeconds": clip["DurationSeconds"],
                "IsPlaceholder": clip["IsPlaceholder"],
                "GainEvidence": clip["GainEvidence"],
                "TailEvidence": clip["TailEvidence"],
                "ListeningEvidence": clip["ListeningEvidence"],
            }
            for clip in ordered_clips
        ],
        "Metadata": {
            "Path": runtime_path(metadata_path.relative_to(repository_root)),
            "Sha256": metadata_sha256,
            "CueSetCount": len(metadata["CueSets"]),
            "CueCount": sum(len(value["Cues"]) for value in metadata["CueSets"]),
            "PhysicalClipCount": len(ordered_clips),
        },
        "Manifest": {
            "Path": manifest_path.relative_to(repository_root).as_posix(),
            "Sha256": sha256_file(manifest_path),
        },
        "Template": {
            "Path": template_path.relative_to(repository_root).as_posix(),
            "Sha256": sha256_file(template_path),
        },
        "ForbiddenOutputs": [
            "references runtime dependency",
            "tests fixture audio copy",
            "manual TestPackage candidate",
            "runtime loader/cache/coordinator",
            "loudness-normalized derivative",
        ],
    }
    recipe_path = repository_root / "scripts/sanity-assets/audio-assets.recipe.json"
    write_json(recipe_path, recipe)

    print(
        f"audio clips={len(ordered_clips)} real=23 placeholder=12 "
        f"metadata={metadata_sha256} manifest={sha256_file(manifest_path)}"
    )
    print(f"recipe {recipe_path.relative_to(repository_root).as_posix()} {sha256_file(recipe_path)}")
    print(f"template {template_path.relative_to(repository_root).as_posix()} {sha256_file(template_path)}")


if __name__ == "__main__":
    main()
