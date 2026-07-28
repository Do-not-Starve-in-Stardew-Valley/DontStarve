from __future__ import annotations

import hashlib
import json
import math
import platform
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

from PIL import Image, ImageDraw, __version__ as PILLOW_VERSION


TEMPLATE_VERSION = "sanity-harmless-visuals-v1"
CONTRACT_VERSION = 1
SOURCE_SCALE_DENOMINATOR = 4
CELL_PADDING_PX = 4
CELL_ALIGNMENT_PX = 8
PLACEHOLDER_MAGENTA = (255, 0, 255, 255)
PLACEHOLDER_RED = (220, 24, 48, 255)
TRANSPARENT = (0, 0, 0, 0)


@dataclass(frozen=True)
class StateRecipe:
    animation_id: str
    source_prefix: str
    frame_duration_ms: int
    loop: bool
    timing_evidence: str
    allowed_empty_frames: tuple[int, ...] = ()


@dataclass(frozen=True)
class ActorRecipe:
    name: str
    group_prefix: str
    expected_png_count: int
    expected_source_tree_sha256: str
    output_relative_path: str
    animation_profile_id: str
    texture_slot_id: str
    credit_group: str
    cell_width: int
    cell_height: int
    sort_layer: str
    states: tuple[StateRecipe, ...]


ACTORS = (
    ActorRecipe(
        name="mr-skitts",
        group_prefix="01-MrSkitts-83.5pct",
        expected_png_count=8,
        expected_source_tree_sha256="0129C44C617059C59E6EB8E7DB77421D83D4B8B99A455A77B446B9152B1BBF9E",
        output_relative_path="DontStarve/Asset/Sanity/Sprites/Illusions/mr-skitts.png",
        animation_profile_id="sanity.animation.mr-skitts.profile",
        texture_slot_id="sanity.asset.mr-skitts.sprite",
        credit_group="ART-01",
        cell_width=184,
        cell_height=112,
        sort_layer="World",
        states=(
            StateRecipe(
                "sanity.animation.mr-skitts.idle",
                "\u9759\u606f_",
                420,
                True,
                "ART-01 GIF total 1680ms divided across four selected PNG frames; provisional until game preview.",
            ),
            StateRecipe(
                "sanity.animation.mr-skitts.disappear",
                "\u6d88\u5931_",
                100,
                False,
                "ART-01 GIF total 400ms divided across four selected PNG frames; provisional until game preview.",
            ),
        ),
    ),
    ActorRecipe(
        name="dark-hand",
        group_prefix="02-",
        expected_png_count=20,
        expected_source_tree_sha256="48B0068C6F9F452364C19EC97AD2526CF71ED8B25E504EA74AC256DA87473983",
        output_relative_path="DontStarve/Asset/Sanity/Sprites/Illusions/dark-hand.png",
        animation_profile_id="sanity.animation.dark-hand.profile",
        texture_slot_id="sanity.asset.dark-hand.sprite",
        credit_group="ART-02",
        cell_width=192,
        cell_height=176,
        sort_layer="World",
        states=(
            StateRecipe(
                "sanity.animation.dark-hand.appear",
                "\u624b\u51fa\u73b0_",
                275,
                False,
                "ART-02 GIF total 1100ms divided across four selected PNG frames; provisional until game preview.",
                allowed_empty_frames=(0,),
            ),
            StateRecipe(
                "sanity.animation.dark-hand.move",
                "\u624b\u884c\u8d70_",
                300,
                True,
                "ART-02 loop GIF total 1200ms divided across four selected PNG frames; provisional until game preview.",
            ),
            StateRecipe(
                "sanity.animation.dark-hand.interact",
                "\u624b\u64cd\u4f5c_",
                175,
                False,
                "ART-02 catch GIF total 700ms divided across four selected PNG frames; provisional until game preview.",
            ),
            StateRecipe(
                "sanity.animation.dark-hand.retreat",
                "\u624b\u540e\u9000_",
                275,
                False,
                "No dedicated GIF exists; uses the appear total only as an explicit provisional rhythm proxy.",
            ),
            StateRecipe(
                "sanity.animation.dark-hand.disappear",
                "\u624b\u6d88\u5931_",
                90,
                False,
                "ART-02 die GIF total 360ms divided across four selected PNG frames; provisional until game preview.",
            ),
        ),
    ),
    ActorRecipe(
        name="dark-watcher",
        group_prefix="03-",
        expected_png_count=12,
        expected_source_tree_sha256="6D490EA3ECDA31C741F0E322A8B66BF5363B966AF921F2A633EBAFB16C513012",
        output_relative_path="DontStarve/Asset/Sanity/Sprites/Illusions/dark-watcher.png",
        animation_profile_id="sanity.animation.dark-watcher.profile",
        texture_slot_id="sanity.asset.dark-watcher.sprite",
        credit_group="ART-03",
        cell_width=904,
        cell_height=128,
        sort_layer="World",
        states=(
            StateRecipe(
                "sanity.animation.dark-watcher.appear",
                "\u6697\u5f71\u89c2\u5bdf\u8005\u51fa\u73b0_",
                510,
                False,
                "ART-03 pre GIF total 2040ms divided across four selected PNG frames; provisional until game preview.",
            ),
            StateRecipe(
                "sanity.animation.dark-watcher.idle",
                "\u6697\u5f71\u89c2\u5bdf\u8005\u9759\u606f_",
                510,
                True,
                "ART-03 loop GIF total 2040ms divided across four selected PNG frames; provisional until game preview.",
            ),
            StateRecipe(
                "sanity.animation.dark-watcher.disappear",
                "\u6697\u5f71\u89c2\u5bdf\u8005\u6d88\u5931_",
                150,
                False,
                "ART-03 pst GIF total 600ms divided across four selected PNG frames; provisional until game preview.",
            ),
        ),
    ),
)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def tree_proof(root: Path, files: Iterable[Path]) -> tuple[int, str]:
    rows = []
    for path in files:
        relative = path.relative_to(root).as_posix()
        rows.append(f"{relative}:{sha256_file(path)}")
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
    image.save(temporary, format="PNG", optimize=False, compress_level=9)
    temporary.replace(path)


def find_source_group(references_root: Path, prefix: str) -> Path:
    matches = [
        path
        for path in references_root.rglob("*")
        if path.is_dir()
        and path.name.startswith(prefix)
        and any(path.rglob("*.png"))
    ]
    matches.sort(
        key=lambda path: (len(list(path.rglob("*.png"))), len(path.parts)),
        reverse=True,
    )
    if len(matches) != 1:
        descriptions = ", ".join(path.as_posix() for path in matches)
        raise RuntimeError(
            f"Expected exactly one PNG source group for prefix {prefix!r}; found {len(matches)}: {descriptions}"
        )
    return matches[0]


def union_alpha_box(images: list[Image.Image]) -> tuple[int, int, int, int]:
    boxes = [image.getchannel("A").getbbox() for image in images]
    nonempty = [box for box in boxes if box is not None]
    if not nonempty:
        raise RuntimeError("A four-frame source state cannot be entirely transparent.")
    return (
        min(box[0] for box in nonempty),
        min(box[1] for box in nonempty),
        max(box[2] for box in nonempty),
        max(box[3] for box in nonempty),
    )


def scaled_size(source_box: tuple[int, int, int, int]) -> tuple[int, int]:
    width = source_box[2] - source_box[0]
    height = source_box[3] - source_box[1]
    return (
        math.ceil(width / SOURCE_SCALE_DENOMINATOR),
        math.ceil(height / SOURCE_SCALE_DENOMINATOR),
    )


def aligned_cell_size(maximum_content: int) -> int:
    unaligned = maximum_content + CELL_PADDING_PX * 2
    return math.ceil(unaligned / CELL_ALIGNMENT_PX) * CELL_ALIGNMENT_PX


def build_actor_sheet(
    repository_root: Path,
    references_root: Path,
    recipe: ActorRecipe,
) -> tuple[dict[str, Any], list[dict[str, str]]]:
    source_root = find_source_group(references_root, recipe.group_prefix)
    all_png = sorted(source_root.rglob("*.png"), key=lambda path: path.as_posix())
    count, source_tree_sha256 = tree_proof(source_root, all_png)
    if count != recipe.expected_png_count:
        raise RuntimeError(
            f"{recipe.name} input count drifted: expected {recipe.expected_png_count}, got {count}."
        )
    if source_tree_sha256 != recipe.expected_source_tree_sha256:
        raise RuntimeError(
            f"{recipe.name} input tree drifted: expected {recipe.expected_source_tree_sha256}, got {source_tree_sha256}."
        )

    prepared_states: list[
        tuple[StateRecipe, list[Path], list[Image.Image], tuple[int, int, int, int], int, int]
    ] = []
    maximum_width = 0
    maximum_height = 0
    try:
        for state in recipe.states:
            paths = sorted(
                [path for path in all_png if path.name.startswith(state.source_prefix)],
                key=lambda path: path.name,
            )
            if len(paths) != 4:
                raise RuntimeError(
                    f"{state.animation_id} must resolve to exactly four PNG frames; found {len(paths)}."
                )
            images = [Image.open(path).convert("RGBA") for path in paths]
            for frame_index, image in enumerate(images):
                if image.getchannel("A").getbbox() is None and frame_index not in state.allowed_empty_frames:
                    raise RuntimeError(
                        f"{state.animation_id} frame {frame_index} is unexpectedly alpha-empty."
                    )
            source_box = union_alpha_box(images)
            width, height = scaled_size(source_box)
            maximum_width = max(maximum_width, width)
            maximum_height = max(maximum_height, height)
            prepared_states.append((state, paths, images, source_box, width, height))

        computed_width = aligned_cell_size(maximum_width)
        computed_height = aligned_cell_size(maximum_height)
        if (computed_width, computed_height) != (recipe.cell_width, recipe.cell_height):
            raise RuntimeError(
                f"{recipe.name} computed cell drifted: expected {recipe.cell_width}x{recipe.cell_height}, "
                f"got {computed_width}x{computed_height}."
            )

        sheet = Image.new(
            "RGBA",
            (recipe.cell_width * 4, recipe.cell_height * len(prepared_states)),
            TRANSPARENT,
        )
        state_metadata: list[dict[str, Any]] = []
        input_files: list[dict[str, str]] = []
        for row, (state, paths, images, source_box, width, height) in enumerate(prepared_states):
            for column, (path, image) in enumerate(zip(paths, images, strict=True)):
                cropped = image.crop(source_box).resize((width, height), Image.Resampling.BOX)
                x = column * recipe.cell_width + (recipe.cell_width - width) // 2
                y = row * recipe.cell_height + recipe.cell_height - CELL_PADDING_PX - height
                sheet.alpha_composite(cropped, (x, y))
                input_files.append(
                    {
                        "Path": path.relative_to(repository_root).as_posix(),
                        "Sha256": sha256_file(path),
                    }
                )

            state_metadata.append(
                {
                    "AnimationId": state.animation_id,
                    "Row": row,
                    "FrameCount": 4,
                    "FrameDurationMs": state.frame_duration_ms,
                    "Loop": state.loop,
                    "PivotSourcePx": {
                        "X": recipe.cell_width // 2,
                        "Y": recipe.cell_height - CELL_PADDING_PX,
                    },
                    "DrawScale": 1.0,
                    "Mirror": "None",
                    "SortLayer": recipe.sort_layer,
                    "AllowedEmptyFrames": list(state.allowed_empty_frames),
                    "IsProvisional": True,
                    "ProvisionalReason": state.timing_evidence
                    + " Bottom-center pivot, draw scale, and sort layer remain subject to game preview.",
                }
            )

        output = repository_root / recipe.output_relative_path
        save_png(output, sheet)
        profile = {
            "AnimationProfileId": recipe.animation_profile_id,
            "TextureSlotId": recipe.texture_slot_id,
            "FrameWidth": recipe.cell_width,
            "FrameHeight": recipe.cell_height,
            "DirectionMode": "None",
            "OwnerLocalOnly": True,
            "IsPlaceholder": True,
            "ContractVersion": CONTRACT_VERSION,
            "CreditGroup": recipe.credit_group,
            "States": state_metadata,
        }
        return profile, input_files
    finally:
        for prepared in prepared_states:
            for image in prepared[2]:
                image.close()


PIXEL_FONT = {
    "D": ("110", "101", "101", "101", "110"),
    "E": ("111", "100", "110", "100", "111"),
    "V": ("101", "101", "101", "101", "010"),
}


def draw_dev_marker(
    draw: ImageDraw.ImageDraw,
    origin: tuple[int, int],
    scale: int = 1,
) -> None:
    x0, y0 = origin
    cursor = x0
    for character in "DEV":
        glyph = PIXEL_FONT[character]
        for y, row in enumerate(glyph):
            for x, value in enumerate(row):
                if value == "1":
                    draw.rectangle(
                        (
                            cursor + x * scale,
                            y0 + y * scale,
                            cursor + (x + 1) * scale - 1,
                            y0 + (y + 1) * scale - 1,
                        ),
                        fill=PLACEHOLDER_MAGENTA,
                    )
        cursor += 4 * scale


def build_eyes_placeholder(repository_root: Path) -> dict[str, Any]:
    frame_width = 64
    frame_height = 32
    sheet = Image.new("RGBA", (frame_width * 4, frame_height), TRANSPARENT)
    openness = (10, 5, 1, 5)
    for frame, half_height in enumerate(openness):
        frame_image = Image.new("RGBA", (frame_width, frame_height), TRANSPARENT)
        draw = ImageDraw.Draw(frame_image)
        center_y = 17
        draw.line((8, center_y, 56, center_y), fill=PLACEHOLDER_MAGENTA, width=2)
        if half_height > 1:
            draw.arc(
                (8, center_y - half_height, 56, center_y + half_height),
                180,
                360,
                fill=PLACEHOLDER_MAGENTA,
                width=2,
            )
            draw.arc(
                (8, center_y - half_height, 56, center_y + half_height),
                0,
                180,
                fill=PLACEHOLDER_MAGENTA,
                width=2,
            )
            draw.ellipse((29, center_y - 3, 35, center_y + 3), fill=(255, 255, 255, 255))
        draw_dev_marker(draw, (2, 2), scale=1)
        sheet.alpha_composite(frame_image, (frame * frame_width, 0))

    output = repository_root / "DontStarve/Asset/Sanity/Sprites/Illusions/eyes.png"
    save_png(output, sheet)
    return {
        "AnimationProfileId": "sanity.animation.eyes.profile",
        "TextureSlotId": "sanity.asset.eyes.sprite",
        "FrameWidth": frame_width,
        "FrameHeight": frame_height,
        "DirectionMode": "None",
        "OwnerLocalOnly": True,
        "IsPlaceholder": True,
        "ContractVersion": CONTRACT_VERSION,
        "CreditGroup": "DEV-PLACEHOLDER",
        "States": [
            {
                "AnimationId": "sanity.animation.eyes.blink",
                "Row": 0,
                "FrameCount": 4,
                "FrameDurationMs": 200,
                "Loop": True,
                "PivotSourcePx": {"X": 32, "Y": 16},
                "DrawScale": 1.0,
                "Mirror": "None",
                "SortLayer": "Screen",
                "AllowedEmptyFrames": [],
                "IsProvisional": True,
                "ProvisionalReason": "Four-frame DEV placeholder proves the sheet contract only; final art, blink cadence, anchor, and scale are pending.",
            }
        ],
    }


def build_danger_border(repository_root: Path) -> dict[str, Any]:
    size = 64
    inset = 16
    image = Image.new("RGBA", (size, size), TRANSPARENT)
    draw = ImageDraw.Draw(image)
    for y in range(size):
        for x in range(size):
            if inset <= x < size - inset and inset <= y < size - inset:
                continue
            color = PLACEHOLDER_MAGENTA if ((x // 4) + (y // 4)) % 2 == 0 else PLACEHOLDER_RED
            image.putpixel((x, y), color)
    draw_dev_marker(draw, (2, 2), scale=1)
    output = repository_root / "DontStarve/Asset/Sanity/Overlays/danger-border.png"
    save_png(output, image)
    return {
        "OverlayProfileId": "sanity.overlay.danger-border.profile",
        "TextureSlotId": "sanity.asset.danger-border.overlay",
        "Width": size,
        "Height": size,
        "StretchMode": "NineSlice",
        "SliceSourcePx": {"Left": inset, "Top": inset, "Right": inset, "Bottom": inset},
        "ViewportSafe": True,
        "UiScaleSafe": True,
        "OwnerLocalOnly": True,
        "IsPlaceholder": True,
        "ContractVersion": CONTRACT_VERSION,
        "CreditGroup": "DEV-PLACEHOLDER",
        "IsProvisional": True,
        "ProvisionalReason": "DEV checker border proves nine-slice and viewport contracts only; final curled red border art is pending.",
    }


def build_beard_rabbit(repository_root: Path) -> dict[str, Any]:
    size = 64
    image = Image.new("RGBA", (size, size), TRANSPARENT)
    draw = ImageDraw.Draw(image)
    draw.ellipse((18, 24, 50, 57), fill=PLACEHOLDER_MAGENTA)
    draw.ellipse((14, 6, 26, 37), fill=PLACEHOLDER_MAGENTA)
    draw.ellipse((30, 4, 42, 35), fill=PLACEHOLDER_MAGENTA)
    draw.ellipse((42, 28, 60, 46), fill=PLACEHOLDER_MAGENTA)
    draw.ellipse((42, 39, 51, 48), fill=(255, 255, 255, 255))
    draw_dev_marker(draw, (2, 2), scale=1)
    output = repository_root / "DontStarve/Asset/Sanity/Sprites/World/beard-rabbit.png"
    save_png(output, image)
    return {
        "StaticSpriteProfileId": "sanity.static.beard-rabbit.profile",
        "TextureSlotId": "sanity.asset.beard-rabbit.sprite",
        "Width": size,
        "Height": size,
        "PivotSourcePx": {"X": 32, "Y": 60},
        "DrawScale": 1.0,
        "SortLayer": "World",
        "OwnerLocalOnly": True,
        "SharedObjectIdMutation": False,
        "IsPlaceholder": True,
        "ContractVersion": CONTRACT_VERSION,
        "CreditGroup": "DEV-PLACEHOLDER",
        "IsProvisional": True,
        "ProvisionalReason": "DEV silhouette proves owner-local projection dimensions only; final rabbit art and anchor are pending.",
    }


def build_beard_icon(repository_root: Path) -> dict[str, Any]:
    size = 16
    image = Image.new("RGBA", (size, size), TRANSPARENT)
    draw = ImageDraw.Draw(image)
    draw.polygon(
        ((2, 4), (5, 3), (8, 6), (11, 3), (14, 4), (13, 10), (10, 14), (8, 11), (6, 14), (3, 10)),
        fill=PLACEHOLDER_MAGENTA,
    )
    draw.rectangle((0, 0, 2, 1), fill=PLACEHOLDER_RED)
    draw.rectangle((13, 0, 15, 1), fill=PLACEHOLDER_RED)
    output = repository_root / "DontStarve/Asset/Sanity/Sprites/Items/beard.png"
    save_png(output, image)
    return {
        "StaticSpriteProfileId": "sanity.static.beard-item.profile",
        "TextureSlotId": "sanity.asset.beard-item.icon",
        "Width": size,
        "Height": size,
        "PivotSourcePx": {"X": 8, "Y": 8},
        "DrawScale": 1.0,
        "SortLayer": "Ui",
        "OwnerLocalOnly": True,
        "SharedObjectIdMutation": False,
        "IsPlaceholder": True,
        "ContractVersion": CONTRACT_VERSION,
        "CreditGroup": "DEV-PLACEHOLDER",
        "IsProvisional": True,
        "ProvisionalReason": "DEV icon proves the 16x16 replacement slot only; final item art is pending.",
    }


def checkerboard(width: int, height: int, tile: int = 8) -> Image.Image:
    image = Image.new("RGB", (width, height), (240, 240, 240))
    draw = ImageDraw.Draw(image)
    for y in range(0, height, tile):
        for x in range(0, width, tile):
            if (x // tile + y // tile) % 2 == 1:
                draw.rectangle(
                    (x, y, min(x + tile - 1, width - 1), min(y + tile - 1, height - 1)),
                    fill=(190, 190, 190),
                )
    return image


def build_preview(source: Path, destination: Path, maximum_width: int = 1024) -> None:
    with Image.open(source) as opened:
        rgba = opened.convert("RGBA")
    if rgba.width > maximum_width:
        shown_height = max(1, round(rgba.height * maximum_width / rgba.width))
        shown = rgba.resize((maximum_width, shown_height), Image.Resampling.NEAREST)
    else:
        shown = rgba
    panel = checkerboard(shown.width, shown.height)
    panel.paste(shown, (0, 0), shown)
    save_png(destination, panel.convert("RGBA"))


def build_grid_template(
    path: Path,
    frame_width: int,
    frame_height: int,
    columns: int,
    rows: int,
    pivot: tuple[int, int],
) -> None:
    image = Image.new("RGBA", (frame_width * columns, frame_height * rows), TRANSPARENT)
    draw = ImageDraw.Draw(image)
    for row in range(rows):
        for column in range(columns):
            left = column * frame_width
            top = row * frame_height
            draw.rectangle(
                (left, top, left + frame_width - 1, top + frame_height - 1),
                outline=PLACEHOLDER_MAGENTA,
                width=1,
            )
            px = left + pivot[0]
            py = top + pivot[1]
            draw.line((px - 3, py, px + 3, py), fill=PLACEHOLDER_RED, width=1)
            draw.line((px, py - 3, px, py + 3), fill=PLACEHOLDER_RED, width=1)
    save_png(path, image)


def build_support_artifacts(
    repository_root: Path,
    animation_profiles: list[dict[str, Any]],
    runtime_outputs: list[Path],
) -> list[Path]:
    support_root = repository_root / "scripts/sanity-assets"
    templates_root = support_root / "templates"
    previews_root = support_root / "previews"
    support_outputs: list[Path] = []

    template_specs: list[dict[str, Any]] = []
    for profile in animation_profiles:
        profile_id = profile["AnimationProfileId"]
        name = profile_id.removeprefix("sanity.animation.").removesuffix(".profile")
        frame_width = profile["FrameWidth"]
        frame_height = profile["FrameHeight"]
        rows = len(profile["States"])
        pivot = profile["States"][0]["PivotSourcePx"]
        template_path = templates_root / f"{name}-grid.png"
        build_grid_template(
            template_path,
            frame_width,
            frame_height,
            4,
            rows,
            (pivot["X"], pivot["Y"]),
        )
        support_outputs.append(template_path)
        template_specs.append(
            {
                "AnimationProfileId": profile_id,
                "TemplatePath": template_path.relative_to(repository_root).as_posix(),
                "Columns": 4,
                "Rows": rows,
                "FrameWidth": frame_width,
                "FrameHeight": frame_height,
                "PivotSourcePx": pivot,
                "StateRows": [
                    {"AnimationId": state["AnimationId"], "Row": state["Row"]}
                    for state in profile["States"]
                ],
                "DeliveryRules": [
                    "RGBA PNG with transparent background",
                    "keep the frozen grid and pivot unless a reviewed metadata revision accompanies the art",
                    "remove magenta guide pixels from the delivered runtime sheet",
                    "do not include source canvases, GIF previews, or collision guides",
                ],
            }
        )

    border_template = templates_root / "danger-border-nine-slice-grid.png"
    border_image = Image.new("RGBA", (64, 64), TRANSPARENT)
    border_draw = ImageDraw.Draw(border_image)
    border_draw.rectangle((0, 0, 63, 63), outline=PLACEHOLDER_MAGENTA, width=1)
    border_draw.line((16, 0, 16, 63), fill=PLACEHOLDER_RED, width=1)
    border_draw.line((48, 0, 48, 63), fill=PLACEHOLDER_RED, width=1)
    border_draw.line((0, 16, 63, 16), fill=PLACEHOLDER_RED, width=1)
    border_draw.line((0, 48, 63, 48), fill=PLACEHOLDER_RED, width=1)
    save_png(border_template, border_image)
    support_outputs.append(border_template)

    template_spec_path = templates_root / "artist-template-spec.json"
    write_json(
        template_spec_path,
        {
            "SchemaVersion": 1,
            "TemplateVersion": TEMPLATE_VERSION,
            "AnimationTemplates": template_specs,
            "OverlayTemplates": [
                {
                    "OverlayProfileId": "sanity.overlay.danger-border.profile",
                    "TemplatePath": border_template.relative_to(repository_root).as_posix(),
                    "Width": 64,
                    "Height": 64,
                    "SliceSourcePx": {"Left": 16, "Top": 16, "Right": 16, "Bottom": 16},
                    "DeliveryRules": [
                        "RGBA PNG with transparent center",
                        "corner regions are never stretched",
                        "edge regions may stretch only along their long axis",
                        "remove magenta and red guide pixels from final art",
                    ],
                }
            ],
            "StaticSpriteTemplates": [
                {
                    "TextureSlotId": "sanity.asset.beard-rabbit.sprite",
                    "Width": 64,
                    "Height": 64,
                    "PivotSourcePx": {"X": 32, "Y": 60},
                    "OwnerLocalOnly": True,
                    "SharedObjectIdMutation": False,
                },
                {
                    "TextureSlotId": "sanity.asset.beard-item.icon",
                    "Width": 16,
                    "Height": 16,
                    "PivotSourcePx": {"X": 8, "Y": 8},
                    "OwnerLocalOnly": True,
                    "SharedObjectIdMutation": False,
                },
            ],
        },
    )
    support_outputs.append(template_spec_path)

    for runtime_output in runtime_outputs:
        slot_name = runtime_output.stem
        preview_path = previews_root / f"{slot_name}-preview.png"
        build_preview(runtime_output, preview_path)
        support_outputs.append(preview_path)
    return support_outputs


def main() -> None:
    repository_root = Path(__file__).resolve().parents[2]
    references_root = repository_root / "references"

    animation_profiles: list[dict[str, Any]] = []
    input_files: list[dict[str, str]] = []
    for recipe in ACTORS:
        profile, actor_inputs = build_actor_sheet(repository_root, references_root, recipe)
        animation_profiles.append(profile)
        input_files.extend(actor_inputs)

    animation_profiles.append(build_eyes_placeholder(repository_root))
    overlay_profile = build_danger_border(repository_root)
    static_profiles = [
        build_beard_rabbit(repository_root),
        build_beard_icon(repository_root),
    ]

    metadata_path = repository_root / "DontStarve/Asset/Sanity/Data/animations.json"
    write_json(
        metadata_path,
        {
            "SchemaVersion": 1,
            "ContractVersion": CONTRACT_VERSION,
            "TemplateVersion": TEMPLATE_VERSION,
            "AnimationProfiles": animation_profiles,
            "OverlayProfiles": [overlay_profile],
            "StaticSpriteProfiles": static_profiles,
        },
    )

    runtime_outputs = [
        repository_root / recipe.output_relative_path for recipe in ACTORS
    ] + [
        repository_root / "DontStarve/Asset/Sanity/Sprites/Illusions/eyes.png",
        repository_root / "DontStarve/Asset/Sanity/Overlays/danger-border.png",
        repository_root / "DontStarve/Asset/Sanity/Sprites/World/beard-rabbit.png",
        repository_root / "DontStarve/Asset/Sanity/Sprites/Items/beard.png",
        metadata_path,
    ]
    support_outputs = build_support_artifacts(
        repository_root,
        animation_profiles,
        runtime_outputs[:-1],
    )

    source_groups = []
    for recipe in ACTORS:
        source_root = find_source_group(references_root, recipe.group_prefix)
        png_files = sorted(source_root.rglob("*.png"), key=lambda path: path.as_posix())
        count, group_hash = tree_proof(source_root, png_files)
        source_groups.append(
            {
                "Actor": recipe.name,
                "SourceRoot": source_root.relative_to(repository_root).as_posix(),
                "PngCount": count,
                "TreeSha256": group_hash,
            }
        )

    lock_path = repository_root / "scripts/sanity-assets/harmless-visuals.recipe.json"
    write_json(
        lock_path,
        {
            "SchemaVersion": 1,
            "TemplateVersion": TEMPLATE_VERSION,
            "Command": "python -X utf8 scripts/sanity-assets/build_harmless_visuals.py",
            "Toolchain": {
                "Python": platform.python_version(),
                "Pillow": PILLOW_VERSION,
            },
            "Parameters": {
                "SourceScale": "1/4",
                "Resampling": "Pillow.Image.Resampling.BOX",
                "StateCrop": "four-frame-alpha-union",
                "StateAlignment": "provisional-bottom-center",
                "CellPaddingPx": CELL_PADDING_PX,
                "CellAlignmentPx": CELL_ALIGNMENT_PX,
                "PngCompressLevel": 9,
                "PngOptimize": False,
            },
            "SourceGroups": source_groups,
            "InputFiles": sorted(input_files, key=lambda item: item["Path"]),
            "RuntimeOutputs": [
                {
                    "Path": path.relative_to(repository_root).as_posix(),
                    "Bytes": path.stat().st_size,
                    "Sha256": sha256_file(path),
                }
                for path in runtime_outputs
            ],
            "SupportOutputs": [
                {
                    "Path": path.relative_to(repository_root).as_posix(),
                    "Bytes": path.stat().st_size,
                    "Sha256": sha256_file(path),
                }
                for path in sorted(support_outputs, key=lambda path: path.as_posix())
            ],
            "ScriptSha256": sha256_file(Path(__file__).resolve()),
            "Boundary": {
                "ReferencesAreReadOnly": True,
                "RuntimeReadsReferences": False,
                "TestsReadReferences": False,
                "BuildReadsReferences": False,
                "EnemyVisualsProcessed": False,
                "AudioProcessed": False,
                "RuntimeLoaderImplemented": False,
            },
        },
    )

    for output in runtime_outputs:
        print(
            f"{output.relative_to(repository_root).as_posix()} "
            f"{output.stat().st_size} {sha256_file(output)}"
        )
    print(f"recipe {lock_path.relative_to(repository_root).as_posix()} {sha256_file(lock_path)}")


if __name__ == "__main__":
    main()
