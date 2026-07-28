from __future__ import annotations

import hashlib
import json
import platform
import shutil
from pathlib import Path
from typing import Any, Iterable

from PIL import Image, ImageDraw, ImageSequence, __version__ as PILLOW_VERSION


SCHEMA_VERSION = 1
CONTRACT_VERSION = 1
TEMPLATE_VERSION = "sanity-hostile-visuals-v1"
BINDING_TEMPLATE_VERSION = "sanity-resource-bindings-v1"
ATTACK_MOTION_POLICY_ID = "sanity.attack-motion.one-tile-v1"
PNG_COMPRESS_LEVEL = 9
TRANSPARENT = (0, 0, 0, 0)
GUIDE_MAGENTA = (255, 0, 255, 255)
GUIDE_RED = (255, 0, 0, 255)
GUIDE_GREEN = (0, 220, 80, 255)

CREEPER_FRAME_ROOT = (
    "references/san值系统设计稿重整/素材/待加工原始素材/视觉/"
    "05-爬行恐惧-50pct及以下/PNG原帧"
)
CREEPER_GIF_ROOT = (
    "references/san值系统设计稿重整/素材/仅供预览/GIF/05-爬行恐惧"
)
TERRORBEAK_SOURCE = (
    "references/san值系统设计稿重整/素材/可直接集成候选/视觉/"
    "敌对影怪/恐怖尖喙/恐怖尖喙-4x12-48x64候选精灵表.png"
)
COLLISION_GUIDE_ROOT = "references/san值系统设计稿重整/素材/说明图/碰撞箱"

EXPECTED_SOURCE_PROOFS = {
    CREEPER_FRAME_ROOT: (40, "0CAE760C0D5C54E30B4FDD333E6DFAE2ED6BEA7460390DFC6CA570F35671D575"),
    CREEPER_GIF_ROOT: (10, "D72BBBACD6EA08CEEC046F8E90FDBE18DF41185815F9698E2420F43BDE17325E"),
    COLLISION_GUIDE_ROOT: (6, "BC4B091128BE1709E581FBCEC832E3ECB21C1CB811A8FD72813368CD707528A2"),
}
EXPECTED_TERRORBEAK_SHA256 = "E1D8805044ED3E8C692D46530A9AAEE94E9C968DB03D9D4770A7F3D6ADB3E3CF"
EXPECTED_HARMLESS_PROFILE_IDS = {
    "sanity.animation.mr-skitts.profile",
    "sanity.animation.dark-hand.profile",
    "sanity.animation.dark-watcher.profile",
    "sanity.animation.eyes.profile",
}

CREEPER_ROWS = (
    (0, "向下行走", False),
    (1, "侧向行走", False),
    (2, "向上行走", False),
    (3, "侧向行走", True),
    (4, "向下攻击", False),
    (5, "侧向攻击", False),
    (6, "向上攻击", False),
    (7, "侧向攻击", True),
    (8, "死亡", False),
    (9, "出现", False),
    (10, "静息", False),
    (11, "恐吓", False),
)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def tree_proof(root: Path, files: Iterable[Path]) -> tuple[int, str]:
    rows = [f"{path.relative_to(root).as_posix()}:{sha256_file(path)}" for path in files]
    rows.sort()
    return len(rows), sha256_bytes("\n".join(rows).encode("utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(text)
    temporary.replace(path)


def save_png(path: Path, image: Image.Image) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    image.convert("RGBA").save(
        temporary,
        format="PNG",
        compress_level=PNG_COMPRESS_LEVEL,
        optimize=False,
    )
    temporary.replace(path)


def validate_source_proofs(repository_root: Path) -> list[dict[str, Any]]:
    proofs: list[dict[str, Any]] = []
    for relative_root, (expected_count, expected_hash) in EXPECTED_SOURCE_PROOFS.items():
        root = repository_root / relative_root
        suffix = ".gif" if relative_root == CREEPER_GIF_ROOT else ".png"
        files = sorted(
            (path for path in root.rglob(f"*{suffix}") if path.is_file()),
            key=lambda path: path.as_posix(),
        )
        count, digest = tree_proof(root, files)
        if (count, digest) != (expected_count, expected_hash):
            raise RuntimeError(
                f"read-only source drifted at {relative_root}: expected "
                f"{expected_count}/{expected_hash}, got {count}/{digest}."
            )
        proofs.append(
            {
                "SourceRoot": relative_root,
                "FileCount": count,
                "TreeSha256": digest,
            }
        )

    terrorbeak = repository_root / TERRORBEAK_SOURCE
    digest = sha256_file(terrorbeak)
    if digest != EXPECTED_TERRORBEAK_SHA256:
        raise RuntimeError(
            f"read-only Terrorbeak candidate drifted: expected {EXPECTED_TERRORBEAK_SHA256}, got {digest}."
        )
    proofs.append(
        {
            "SourceRoot": TERRORBEAK_SOURCE,
            "FileCount": 1,
            "TreeSha256": digest,
        }
    )
    return proofs


def validate_gif_evidence(repository_root: Path) -> list[dict[str, Any]]:
    expected = {
        "appear.gif": (6, (60,), 360),
        "atk down.gif": (11, (40,), 440),
        "atk side.gif": (14, (40,), 560),
        "atk up.gif": (11, (40,), 440),
        "disappear.gif": (10, (40,), 400),
        "idle.gif": (12, (130,), 1560),
        "taunt.gif": (15, (130,), 1950),
        "walk down.gif": (10, (60,), 600),
        "walk side.gif": (10, (60,), 600),
        "walk up.gif": (10, (60,), 600),
    }
    evidence: list[dict[str, Any]] = []
    root = repository_root / CREEPER_GIF_ROOT
    for name, (expected_frames, expected_durations, expected_total) in expected.items():
        path = root / name
        with Image.open(path) as opened:
            durations = tuple(
                frame.info.get("duration", opened.info.get("duration", 0))
                for frame in ImageSequence.Iterator(opened)
            )
        if (
            len(durations) != expected_frames
            or tuple(sorted(set(durations))) != expected_durations
            or sum(durations) != expected_total
        ):
            raise RuntimeError(f"GIF timing evidence drifted for {path.relative_to(repository_root)}.")
        evidence.append(
            {
                "Path": path.relative_to(repository_root).as_posix(),
                "Sha256": sha256_file(path),
                "FrameCount": len(durations),
                "FrameDurationsMs": list(expected_durations),
                "TotalDurationMs": sum(durations),
            }
        )
    return evidence


def load_source_frames(root: Path, prefix: str) -> list[Image.Image]:
    paths = sorted(root.glob(f"{prefix}_*.png"), key=lambda path: path.name)
    if len(paths) != 4:
        raise RuntimeError(f"{prefix} must have exactly four source frames; got {len(paths)}.")
    frames: list[Image.Image] = []
    for path in paths:
        with Image.open(path) as opened:
            rgba = opened.convert("RGBA")
        if rgba.getchannel("A").getbbox() is None:
            raise RuntimeError(f"{path} is alpha-empty.")
        frames.append(rgba)
    return frames


def fit_state_frames(frames: list[Image.Image], frame_size: tuple[int, int]) -> tuple[list[Image.Image], float]:
    cropped: list[Image.Image] = []
    maximum_width = 0
    maximum_height = 0
    for frame in frames:
        alpha_bounds = frame.getchannel("A").getbbox()
        if alpha_bounds is None:
            raise RuntimeError("source frame unexpectedly became alpha-empty.")
        crop = frame.crop(alpha_bounds)
        cropped.append(crop)
        maximum_width = max(maximum_width, crop.width)
        maximum_height = max(maximum_height, crop.height)

    frame_width, frame_height = frame_size
    content_width = frame_width - 4
    content_height = frame_height - 4
    scale = min(content_width / maximum_width, content_height / maximum_height, 1.0)
    output: list[Image.Image] = []
    for crop in cropped:
        width = max(1, round(crop.width * scale))
        height = max(1, round(crop.height * scale))
        resized = crop.resize((width, height), Image.Resampling.LANCZOS)
        cell = Image.new("RGBA", frame_size, TRANSPARENT)
        left = (frame_width - width) // 2
        top = frame_height - 2 - height
        if left < 0 or top < 0:
            raise RuntimeError("normalized frame escaped its frozen cell.")
        cell.paste(resized, (left, top), resized)
        output.append(cell)
    return output, scale


def build_creeper_sheet(repository_root: Path) -> tuple[Path, list[dict[str, Any]]]:
    frame_size = (64, 64)
    source_root = repository_root / CREEPER_FRAME_ROOT
    sheet = Image.new("RGBA", (frame_size[0] * 4, frame_size[1] * 12), TRANSPARENT)
    normalized_by_prefix: dict[str, list[Image.Image]] = {}
    processing: list[dict[str, Any]] = []
    for prefix in sorted({prefix for _, prefix, _ in CREEPER_ROWS}):
        cells, scale = fit_state_frames(load_source_frames(source_root, prefix), frame_size)
        normalized_by_prefix[prefix] = cells
        processing.append(
            {
                "SourcePrefix": prefix,
                "SourceFrames": 4,
                "StateScale": f"{scale:.12f}",
                "Alignment": "alpha-crop-bottom-center-with-2px-margin",
            }
        )

    for row, prefix, mirror in CREEPER_ROWS:
        for column, cell in enumerate(normalized_by_prefix[prefix]):
            shown = cell.transpose(Image.Transpose.FLIP_LEFT_RIGHT) if mirror else cell
            sheet.paste(shown, (column * frame_size[0], row * frame_size[1]), shown)

    output = repository_root / "DontStarve/Asset/Sanity/Sprites/Monsters/creeper-fear.png"
    save_png(output, sheet)
    return output, processing


def build_terrorbeak_sheet(repository_root: Path) -> Path:
    source = repository_root / TERRORBEAK_SOURCE
    with Image.open(source) as opened:
        rgba = opened.convert("RGBA")
        if rgba.size != (192, 768) or opened.mode != "RGBA":
            raise RuntimeError("Terrorbeak candidate must remain RGBA 4x12 at 48x64 per cell.")
        for row in range(12):
            for column in range(4):
                cell = rgba.crop((column * 48, row * 64, (column + 1) * 48, (row + 1) * 64))
                if cell.getchannel("A").getbbox() is None:
                    raise RuntimeError(f"Terrorbeak row {row} frame {column + 1} is alpha-empty.")

    output = repository_root / "DontStarve/Asset/Sanity/Sprites/Monsters/terrorbeak.png"
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    shutil.copyfile(source, temporary)
    temporary.replace(output)
    return output


def direction_rows(first_row: int) -> list[dict[str, Any]]:
    return [
        {"Direction": "Down", "Row": first_row, "Mirror": "None"},
        {"Direction": "Right", "Row": first_row + 1, "Mirror": "None"},
        {"Direction": "Up", "Row": first_row + 2, "Mirror": "None"},
        {"Direction": "Left", "Row": first_row + 3, "Mirror": "Baked"},
    ]


def hostile_state(
    animation_id: str,
    row: int,
    duration_ms: int,
    loop: bool,
    pivot: tuple[int, int],
    direction_first_row: int | None,
    timing_reason: str,
    hit_frames: tuple[int, ...] = (),
) -> dict[str, Any]:
    return {
        "AnimationId": animation_id,
        "Row": row,
        "FrameCount": 4,
        "FrameDurationMs": duration_ms,
        "Loop": loop,
        "PivotSourcePx": {"X": pivot[0], "Y": pivot[1]},
        "DrawScale": 4.0,
        "Mirror": "BakedFourWayRows" if direction_first_row is not None else "None",
        "SortLayer": "Actor",
        "DirectionRows": direction_rows(direction_first_row) if direction_first_row is not None else [],
        "HitFrames": list(hit_frames),
        "AllowedEmptyFrames": [],
        "IsProvisional": True,
        "ProvisionalReason": timing_reason,
    }


def build_hostile_profile(actor: str) -> dict[str, Any]:
    if actor == "creeper-fear":
        frame_width, frame_height = 64, 64
        pivot = (32, 48)
        credit_group = "ART-05"
        timing = {
            "move": (150, "The 600 ms movement preview total is normalized across four runtime keys; game cadence remains provisional."),
            "attack": (125, "The 440-560 ms directional preview totals are normalized to a 500 ms four-key midpoint; game cadence remains provisional."),
            "death": (100, "No death-specific GIF exists; 100 ms is an explicit four-key hostile-transition fallback pending game preview."),
            "spawn": (90, "The 360 ms appear preview total is normalized across four runtime keys; game cadence remains provisional."),
            "idle": (390, "The 1560 ms idle preview total is normalized across four runtime keys; game cadence remains provisional."),
            "taunt": (488, "The 1950 ms taunt preview total is rounded to 1952 ms across four runtime keys; game cadence remains provisional."),
        }
        collision = {
            "CoordinateSpace": "ActorOriginRelativeSourcePx",
            "HurtBoxSourcePx": {"X": 4, "Y": 48, "Width": 56, "Height": 48},
            "AttackBoxSourcePx": {"X": -8, "Y": 56, "Width": 80, "Height": 80},
            "AttackActiveFrames": [3, 4],
            "IsProvisional": True,
            "ProvisionalReason": "ART-05 collision guides and the D11 source-pixel baseline still require debug-rectangle game calibration.",
        }
    elif actor == "terrorbeak":
        frame_width, frame_height = 48, 64
        pivot = (24, 48)
        credit_group = "ART-06"
        fallback = "ART-06 has no GIF timing evidence; 100 ms is an explicit development fallback, not an accepted cadence."
        timing = {state: (100, fallback) for state in ("move", "attack", "death", "spawn", "idle", "taunt")}
        collision = {
            "CoordinateSpace": "ActorOriginRelativeSourcePx",
            "HurtBoxSourcePx": {"X": 8, "Y": 32, "Width": 32, "Height": 32},
            "AttackBoxSourcePx": {"X": -16, "Y": 8, "Width": 80, "Height": 80},
            "AttackActiveFrames": [3, 4],
            "IsProvisional": True,
            "ProvisionalReason": "ART-06 collision guides and the D11 source-pixel baseline still require debug-rectangle game calibration.",
        }
    else:
        raise ValueError(actor)

    profile_id = f"sanity.animation.{actor}.profile"
    move_duration, move_reason = timing["move"]
    attack_duration, attack_reason = timing["attack"]
    death_duration, death_reason = timing["death"]
    spawn_duration, spawn_reason = timing["spawn"]
    idle_duration, idle_reason = timing["idle"]
    taunt_duration, taunt_reason = timing["taunt"]
    return {
        "AnimationProfileId": profile_id,
        "TextureSlotId": f"sanity.asset.{actor}.sprite",
        "FrameWidth": frame_width,
        "FrameHeight": frame_height,
        "SheetRows": 12,
        "DirectionMode": "FourWayRows",
        "OwnerLocalOnly": False,
        "IsPlaceholder": True,
        "ContractVersion": CONTRACT_VERSION,
        "TemplateVersion": TEMPLATE_VERSION,
        "CreditGroup": credit_group,
        "ActorOriginSourcePx": {"X": 0, "Y": 0},
        "Collision": collision,
        "States": [
            hostile_state(
                f"sanity.animation.{actor}.move",
                0,
                move_duration,
                True,
                pivot,
                0,
                move_reason,
            ),
            hostile_state(
                f"sanity.animation.{actor}.attack",
                4,
                attack_duration,
                False,
                pivot,
                4,
                attack_reason,
                hit_frames=(3, 4),
            ),
            hostile_state(
                f"sanity.animation.{actor}.death",
                8,
                death_duration,
                False,
                pivot,
                None,
                death_reason,
            ),
            hostile_state(
                f"sanity.animation.{actor}.spawn",
                9,
                spawn_duration,
                False,
                pivot,
                None,
                spawn_reason,
            ),
            hostile_state(
                f"sanity.animation.{actor}.idle",
                10,
                idle_duration,
                True,
                pivot,
                None,
                idle_reason,
            ),
            hostile_state(
                f"sanity.animation.{actor}.taunt",
                11,
                taunt_duration,
                False,
                pivot,
                None,
                taunt_reason,
            ),
        ],
    }


def merge_animation_metadata(repository_root: Path, hostile_profiles: list[dict[str, Any]]) -> Path:
    path = repository_root / "DontStarve/Asset/Sanity/Data/animations.json"
    current = json.loads(path.read_text(encoding="utf-8"))
    if current.get("SchemaVersion") != SCHEMA_VERSION or current.get("ContractVersion") != CONTRACT_VERSION:
        raise RuntimeError("animations.json schema/contract drifted; refusing to rewrite it.")
    current_profiles = current.get("AnimationProfiles")
    if not isinstance(current_profiles, list):
        raise RuntimeError("animations.json AnimationProfiles is not an array.")
    current_ids = [profile.get("AnimationProfileId") for profile in current_profiles]
    if not EXPECTED_HARMLESS_PROFILE_IDS.issubset(set(current_ids)):
        raise RuntimeError("stage 03 harmless animation profiles are incomplete; refusing to hide upstream drift.")
    hostile_ids = {profile["AnimationProfileId"] for profile in hostile_profiles}
    preserved = [profile for profile in current_profiles if profile.get("AnimationProfileId") not in hostile_ids]
    current["HostileTemplateVersion"] = TEMPLATE_VERSION
    current["AnimationProfiles"] = preserved + hostile_profiles
    write_json(path, current)
    return path


def merge_resource_bindings(repository_root: Path) -> Path:
    path = repository_root / "DontStarve/Asset/Sanity/Data/resource-bindings.json"
    if path.exists():
        current = json.loads(path.read_text(encoding="utf-8"))
        if current.get("SchemaVersion") != SCHEMA_VERSION or current.get("ContractVersion") != CONTRACT_VERSION:
            raise RuntimeError("resource-bindings.json schema/contract drifted; refusing to rewrite it.")
    else:
        current = {
            "SchemaVersion": SCHEMA_VERSION,
            "ContractVersion": CONTRACT_VERSION,
            "TemplateVersion": BINDING_TEMPLATE_VERSION,
            "AttackMotionPolicies": [],
            "Bindings": [],
        }

    policies = current.get("AttackMotionPolicies")
    bindings = current.get("Bindings")
    if not isinstance(policies, list) or not isinstance(bindings, list):
        raise RuntimeError("resource binding arrays are malformed.")
    policy = {
        "AttackMotionPolicyId": ATTACK_MOTION_POLICY_ID,
        "ContractVersion": CONTRACT_VERSION,
        "TotalAdvanceTiles": 1.0,
        "FrameAdvanceTiles": [0.5, 0.5, 0.0, 0.0],
        "ResetAfterAnimation": True,
    }
    policies = [item for item in policies if item.get("AttackMotionPolicyId") != ATTACK_MOTION_POLICY_ID]
    policies.append(policy)

    hostile_bindings = [
        {
            "AssetBindingId": "sanity.binding.creeper-fear",
            "SlotId": "sanity.asset.creeper-fear.sprite",
            "AnimationProfileId": "sanity.animation.creeper-fear.profile",
            "CueSetId": "sanity.cue.creeper-fear",
            "AttackMotionPolicyId": ATTACK_MOTION_POLICY_ID,
            "ContractVersion": CONTRACT_VERSION,
        },
        {
            "AssetBindingId": "sanity.binding.terrorbeak",
            "SlotId": "sanity.asset.terrorbeak.sprite",
            "AnimationProfileId": "sanity.animation.terrorbeak.profile",
            "CueSetId": "sanity.cue.terrorbeak",
            "AttackMotionPolicyId": ATTACK_MOTION_POLICY_ID,
            "ContractVersion": CONTRACT_VERSION,
        },
    ]
    hostile_ids = {item["AssetBindingId"] for item in hostile_bindings}
    bindings = [item for item in bindings if item.get("AssetBindingId") not in hostile_ids]
    bindings.extend(hostile_bindings)
    current["TemplateVersion"] = BINDING_TEMPLATE_VERSION
    current["AttackMotionPolicies"] = policies
    current["Bindings"] = bindings
    write_json(path, current)
    return path


def update_manifest_hashes(repository_root: Path, sheet_paths: list[Path], metadata_path: Path) -> Path:
    path = repository_root / "DontStarve/Asset/Sanity/Data/sanity-assets.json"
    manifest = json.loads(path.read_text(encoding="utf-8"))
    slots = manifest.get("Slots")
    if manifest.get("SchemaVersion") != SCHEMA_VERSION or not isinstance(slots, list):
        raise RuntimeError("sanity-assets.json schema drifted; refusing to rewrite it.")
    sheet_slots = {
        "sanity.asset.creeper-fear.sprite": (
            "Asset/Sanity/Sprites/Monsters/creeper-fear.png",
            sha256_file(sheet_paths[0]),
        ),
        "sanity.asset.terrorbeak.sprite": (
            "Asset/Sanity/Sprites/Monsters/terrorbeak.png",
            sha256_file(sheet_paths[1]),
        ),
    }
    matched_sheets: set[str] = set()
    animation_count = 0
    metadata_hash = sha256_file(metadata_path)
    for slot in slots:
        slot_id = slot.get("SlotId")
        if slot_id in sheet_slots:
            expected_path, digest = sheet_slots[slot_id]
            if slot.get("Path") != expected_path or slot.get("Kind") != "Png":
                raise RuntimeError(f"manifest contract drifted for {slot_id}.")
            slot["Sha256"] = digest
            matched_sheets.add(slot_id)
        if slot.get("Path") == "Asset/Sanity/Data/animations.json":
            if slot.get("Kind") != "Json":
                raise RuntimeError(f"animation slot {slot_id} has an unexpected kind.")
            slot["Sha256"] = metadata_hash
            animation_count += 1
    if matched_sheets != set(sheet_slots) or animation_count != 24:
        raise RuntimeError("manifest hostile sheet or shared animation slot count drifted.")
    write_json(path, manifest)
    return path


def checkerboard(width: int, height: int, tile: int = 8) -> Image.Image:
    image = Image.new("RGBA", (width, height), (240, 240, 240, 255))
    draw = ImageDraw.Draw(image)
    for y in range(0, height, tile):
        for x in range(0, width, tile):
            if (x // tile + y // tile) % 2 == 1:
                draw.rectangle(
                    (x, y, min(x + tile - 1, width - 1), min(y + tile - 1, height - 1)),
                    fill=(190, 190, 190, 255),
                )
    return image


def build_sheet_preview(source: Path, destination: Path) -> None:
    with Image.open(source) as opened:
        rgba = opened.convert("RGBA")
    panel = checkerboard(rgba.width, rgba.height)
    panel.paste(rgba, (0, 0), rgba)
    save_png(destination, panel)


def build_grid_template(path: Path, frame_width: int, frame_height: int, pivot: tuple[int, int]) -> None:
    image = Image.new("RGBA", (frame_width * 4, frame_height * 12), TRANSPARENT)
    draw = ImageDraw.Draw(image)
    for row in range(12):
        for column in range(4):
            left = column * frame_width
            top = row * frame_height
            draw.rectangle(
                (left, top, left + frame_width - 1, top + frame_height - 1),
                outline=GUIDE_MAGENTA,
                width=1,
            )
            px = left + pivot[0]
            py = top + pivot[1]
            draw.line((px - 3, py, px + 3, py), fill=GUIDE_RED, width=1)
            draw.line((px, py - 3, px, py + 3), fill=GUIDE_RED, width=1)
    save_png(path, image)


def build_collision_preview(source: Path, destination: Path, profile: dict[str, Any]) -> None:
    frame_width = profile["FrameWidth"]
    frame_height = profile["FrameHeight"]
    collision = profile["Collision"]
    hurt = collision["HurtBoxSourcePx"]
    attack = collision["AttackBoxSourcePx"]
    minimum_x = min(0, hurt["X"], attack["X"]) - 4
    minimum_y = min(0, hurt["Y"], attack["Y"]) - 4
    maximum_x = max(frame_width, hurt["X"] + hurt["Width"], attack["X"] + attack["Width"]) + 4
    maximum_y = max(frame_height, hurt["Y"] + hurt["Height"], attack["Y"] + attack["Height"]) + 4
    scale = 4
    panel = checkerboard((maximum_x - minimum_x) * scale, (maximum_y - minimum_y) * scale, tile=16)
    with Image.open(source) as opened:
        frame = opened.convert("RGBA").crop((0, 10 * frame_height, frame_width, 11 * frame_height))
    shown = frame.resize((frame_width * scale, frame_height * scale), Image.Resampling.NEAREST)
    origin = (-minimum_x * scale, -minimum_y * scale)
    panel.paste(shown, origin, shown)
    draw = ImageDraw.Draw(panel)

    def rectangle(box: dict[str, int], color: tuple[int, int, int, int]) -> None:
        left = (box["X"] - minimum_x) * scale
        top = (box["Y"] - minimum_y) * scale
        right = (box["X"] + box["Width"] - minimum_x) * scale - 1
        bottom = (box["Y"] + box["Height"] - minimum_y) * scale - 1
        draw.rectangle((left, top, right, bottom), outline=color, width=3)

    rectangle(hurt, GUIDE_GREEN)
    rectangle(attack, GUIDE_RED)
    pivot = profile["States"][0]["PivotSourcePx"]
    px = (pivot["X"] - minimum_x) * scale
    py = (pivot["Y"] - minimum_y) * scale
    draw.line((px - 8, py, px + 8, py), fill=GUIDE_MAGENTA, width=2)
    draw.line((px, py - 8, px, py + 8), fill=GUIDE_MAGENTA, width=2)
    save_png(destination, panel)


def build_support_outputs(
    repository_root: Path,
    sheets: list[Path],
    profiles: list[dict[str, Any]],
) -> list[Path]:
    support_root = repository_root / "scripts/sanity-assets"
    template_root = support_root / "templates/hostile"
    preview_root = support_root / "previews/hostile"
    outputs: list[Path] = []
    template_rows = [
        {"State": "move.down", "Row": 0},
        {"State": "move.right", "Row": 1},
        {"State": "move.up", "Row": 2},
        {"State": "move.left", "Row": 3},
        {"State": "attack.down", "Row": 4},
        {"State": "attack.right", "Row": 5},
        {"State": "attack.up", "Row": 6},
        {"State": "attack.left", "Row": 7},
        {"State": "death", "Row": 8},
        {"State": "spawn", "Row": 9},
        {"State": "idle", "Row": 10},
        {"State": "taunt", "Row": 11},
    ]
    specs: list[dict[str, Any]] = []
    for sheet, profile in zip(sheets, profiles, strict=True):
        actor = profile["AnimationProfileId"].removeprefix("sanity.animation.").removesuffix(".profile")
        pivot_value = profile["States"][0]["PivotSourcePx"]
        pivot = (pivot_value["X"], pivot_value["Y"])
        template_path = template_root / f"{actor}-grid.png"
        sheet_preview = preview_root / f"{actor}-sheet.png"
        collision_preview = preview_root / f"{actor}-collision.png"
        build_grid_template(template_path, profile["FrameWidth"], profile["FrameHeight"], pivot)
        build_sheet_preview(sheet, sheet_preview)
        build_collision_preview(sheet, collision_preview, profile)
        outputs.extend((template_path, sheet_preview, collision_preview))
        specs.append(
            {
                "AssetBindingId": f"sanity.binding.{actor}",
                "TextureSlotId": profile["TextureSlotId"],
                "AnimationProfileId": profile["AnimationProfileId"],
                "CueSetId": f"sanity.cue.{actor}",
                "TemplatePath": template_path.relative_to(repository_root).as_posix(),
                "Columns": 4,
                "Rows": 12,
                "FrameWidth": profile["FrameWidth"],
                "FrameHeight": profile["FrameHeight"],
                "ActorOriginSourcePx": profile["ActorOriginSourcePx"],
                "PivotSourcePx": pivot_value,
                "DrawScale": 4.0,
                "StateRows": template_rows,
                "Collision": profile["Collision"],
                "AttackMotionPolicyId": ATTACK_MOTION_POLICY_ID,
                "DeliveryRules": [
                    "deliver one RGBA PNG with a transparent background",
                    "keep the frozen 4x12 grid, row order, actor origin, and pivot",
                    "keep hurt/attack rectangles in actor-origin-relative source pixels",
                    "remove all magenta/red guide pixels from runtime art",
                    "do not include source canvases, GIF previews, collision guides, or gameplay values",
                    "a reviewed metadata contract revision is required before changing dimensions or anchors",
                ],
            }
        )

    specification = template_root / "hostile-artist-template-spec.json"
    write_json(
        specification,
        {
            "SchemaVersion": SCHEMA_VERSION,
            "ContractVersion": CONTRACT_VERSION,
            "TemplateVersion": TEMPLATE_VERSION,
            "AttackMotionPolicyId": ATTACK_MOTION_POLICY_ID,
            "AnimationTemplates": specs,
        },
    )
    outputs.append(specification)
    return outputs


def output_facts(repository_root: Path, paths: Iterable[Path]) -> list[dict[str, Any]]:
    return [
        {
            "Path": path.relative_to(repository_root).as_posix(),
            "Bytes": path.stat().st_size,
            "Sha256": sha256_file(path),
        }
        for path in sorted(paths, key=lambda item: item.as_posix())
    ]


def main() -> None:
    repository_root = Path(__file__).resolve().parents[2]
    source_proofs = validate_source_proofs(repository_root)
    gif_evidence = validate_gif_evidence(repository_root)

    creeper_sheet, creeper_processing = build_creeper_sheet(repository_root)
    terrorbeak_sheet = build_terrorbeak_sheet(repository_root)
    sheets = [creeper_sheet, terrorbeak_sheet]
    profiles = [build_hostile_profile("creeper-fear"), build_hostile_profile("terrorbeak")]
    metadata_path = merge_animation_metadata(repository_root, profiles)
    bindings_path = merge_resource_bindings(repository_root)
    manifest_path = update_manifest_hashes(repository_root, sheets, metadata_path)
    support_outputs = build_support_outputs(repository_root, sheets, profiles)

    runtime_outputs = sheets + [metadata_path, bindings_path, manifest_path]
    lock_path = repository_root / "scripts/sanity-assets/hostile-visuals.recipe.json"
    write_json(
        lock_path,
        {
            "SchemaVersion": SCHEMA_VERSION,
            "ContractVersion": CONTRACT_VERSION,
            "TemplateVersion": TEMPLATE_VERSION,
            "Command": "python -X utf8 scripts/sanity-assets/build_hostile_visuals.py",
            "Toolchain": {
                "Python": platform.python_version(),
                "Pillow": PILLOW_VERSION,
            },
            "Parameters": {
                "CreeperFrameSize": {"Width": 64, "Height": 64},
                "TerrorbeakFrameSize": {"Width": 48, "Height": 64},
                "Columns": 4,
                "Rows": 12,
                "CreeperResampling": "Pillow.Image.Resampling.LANCZOS",
                "CreeperAlignment": "per-state-alpha-crop-bottom-center-with-2px-margin",
                "TerrorbeakProcessing": "byte-for-byte-copy-after-RGBA-grid-validation",
                "PngCompressLevel": PNG_COMPRESS_LEVEL,
                "PngOptimize": False,
            },
            "SourceProofs": source_proofs,
            "GifTimingEvidence": gif_evidence,
            "CreeperStateProcessing": creeper_processing,
            "RuntimeOutputs": output_facts(repository_root, runtime_outputs),
            "SupportOutputs": output_facts(repository_root, support_outputs),
            "ScriptSha256": sha256_file(Path(__file__).resolve()),
            "Boundary": {
                "ReferencesAreReadOnly": True,
                "RuntimeReadsReferences": False,
                "TestsReadReferences": False,
                "BuildReadsReferences": False,
                "GameplayProfilesCreated": False,
                "AudioProcessed": False,
                "AudioCueMetadataCreated": False,
                "RuntimeLoaderImplemented": False,
                "DamageOrAiImplemented": False,
            },
        },
    )

    for fact in output_facts(repository_root, runtime_outputs):
        print(f"{fact['Path']} {fact['Bytes']} {fact['Sha256']}")
    print(f"recipe {lock_path.relative_to(repository_root).as_posix()} {sha256_file(lock_path)}")


if __name__ == "__main__":
    main()
