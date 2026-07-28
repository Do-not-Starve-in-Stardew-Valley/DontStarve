from __future__ import annotations

import json
import re
import sys
from pathlib import Path

from docx import Document
from docx.oxml.ns import qn
from docx.table import Table
from docx.text.paragraph import Paragraph


def iter_block_items(parent):
    body = parent.element.body
    for child in body.iterchildren():
        if child.tag == qn("w:p"):
            yield Paragraph(child, parent)
        elif child.tag == qn("w:tbl"):
            yield Table(child, parent)


def clean_text(value: str) -> str:
    value = value.replace("\u3000", " ")
    value = re.sub(r"[ \t]+", " ", value)
    value = re.sub(r"\n{3,}", "\n\n", value)
    return value.strip()


def paragraph_style_level(paragraph: Paragraph) -> int | None:
    name = paragraph.style.name if paragraph.style is not None else ""
    if name.startswith("Heading "):
        try:
            return int(name.rsplit(" ", 1)[-1])
        except ValueError:
            return None
    if name.startswith("标题 "):
        try:
            return int(name.rsplit(" ", 1)[-1])
        except ValueError:
            return None
    return None


def run_color(run) -> str:
    color = run.font.color
    if color is not None and color.rgb is not None:
        return str(color.rgb)
    return ""


def run_highlight(run) -> str:
    highlight = run.font.highlight_color
    return str(highlight) if highlight is not None else ""


def extract_runs(paragraph: Paragraph) -> list[dict]:
    runs = []
    for run in paragraph.runs:
        text = clean_text(run.text)
        if not text:
            continue
        runs.append(
            {
                "text": text,
                "color": run_color(run),
                "highlight": run_highlight(run),
                "bold": bool(run.bold),
                "italic": bool(run.italic),
            }
        )
    return runs


def color_summary(runs: list[dict]) -> dict:
    colors: dict[str, int] = {}
    highlights: dict[str, int] = {}
    for run in runs:
        if run["color"]:
            colors[run["color"]] = colors.get(run["color"], 0) + len(run["text"])
        if run["highlight"]:
            highlights[run["highlight"]] = highlights.get(run["highlight"], 0) + len(run["text"])
    return {
        "colors": dict(sorted(colors.items(), key=lambda item: item[1], reverse=True)),
        "highlights": dict(sorted(highlights.items(), key=lambda item: item[1], reverse=True)),
    }


def detect_heading(text: str, style_level: int | None) -> tuple[bool, int | None]:
    if style_level is not None:
        return True, style_level

    if re.match(r"^第[一二三四五六七八九十]+[章节部分篇]", text):
        return True, 1
    if re.match(r"^\d+(\.\d+)*[、.．]\s*", text):
        depth = text.split(" ", 1)[0].replace("．", ".")
        return True, min(1 + depth.count("."), 4)
    if re.match(r"^[一二三四五六七八九十]+[、.．]\s*", text):
        return True, 2
    return False, None


def extract_docx(path: Path) -> dict:
    doc = Document(path)
    blocks = []
    current_path: list[str] = []
    table_count = 0

    for block in iter_block_items(doc):
        if isinstance(block, Paragraph):
            text = clean_text(block.text)
            if not text:
                continue

            runs = extract_runs(block)
            colors = color_summary(runs)
            style_level = paragraph_style_level(block)
            is_heading, level = detect_heading(text, style_level)
            if is_heading and level is not None:
                current_path = current_path[: level - 1]
                current_path.append(text)
                blocks.append(
                    {
                        "kind": "heading",
                        "level": level,
                        "text": text,
                        "section_path": current_path.copy(),
                        "style": block.style.name if block.style is not None else "",
                        "runs": runs,
                        "color_summary": colors,
                    }
                )
            else:
                blocks.append(
                    {
                        "kind": "paragraph",
                        "text": text,
                        "section_path": current_path.copy(),
                        "style": block.style.name if block.style is not None else "",
                        "runs": runs,
                        "color_summary": colors,
                    }
                )
        elif isinstance(block, Table):
            table_count += 1
            rows = []
            for row in block.rows:
                rows.append([clean_text(cell.text) for cell in row.cells])
            blocks.append(
                {
                    "kind": "table",
                    "table_index": table_count,
                    "rows": rows,
                    "section_path": current_path.copy(),
                }
            )

    return {
        "source": str(path),
        "block_count": len(blocks),
        "table_count": table_count,
        "blocks": blocks,
    }


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: extract_docx.py input.docx output.json", file=sys.stderr)
        return 2

    input_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    data = extract_docx(input_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(
        json.dumps(
            {
                "source": data["source"],
                "block_count": data["block_count"],
                "table_count": data["table_count"],
                "output": str(output_path),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
