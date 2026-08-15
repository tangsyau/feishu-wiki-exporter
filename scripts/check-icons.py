#!/usr/bin/env python3
"""Validate desktop icon masters, derived PNG sizes, and ICO entries."""

from __future__ import annotations

import hashlib
import json
import struct
import sys
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parent.parent
EXPORTER_MASTER = PROJECT_ROOT / "src/FeishuExporter.Desktop/Assets/AppIcon.png"
EXPORTER_ICO = PROJECT_ROOT / "src/FeishuExporter.Desktop/Assets/AppIcon.ico"
READER_ICON_DIR = PROJECT_ROOT / "knowledge-reader/src-tauri/icons"
READER_MASTER = READER_ICON_DIR / "icon.png"
READER_ICO = READER_ICON_DIR / "icon.ico"
REQUIRED_ICO_SIZES = {16, 24, 32, 48, 64, 128, 256}


def png_size(path: Path) -> tuple[int, int]:
    data = path.read_bytes()
    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是有效的 PNG 文件")
    return struct.unpack(">II", data[16:24])


def ico_sizes(path: Path) -> set[int]:
    data = path.read_bytes()
    if len(data) < 6:
        raise ValueError("ICO 文件头不完整")
    reserved, icon_type, count = struct.unpack_from("<HHH", data)
    if reserved != 0 or icon_type != 1 or len(data) < 6 + count * 16:
        raise ValueError("不是有效的 ICO 文件")
    sizes: set[int] = set()
    for index in range(count):
        width, height = struct.unpack_from("BB", data, 6 + index * 16)
        normalized_width = width or 256
        normalized_height = height or 256
        if normalized_width != normalized_height:
            raise ValueError(f"包含非正方形图标：{normalized_width}×{normalized_height}")
        sizes.add(normalized_width)
    return sizes


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    failures: list[str] = []
    expected_pngs = {
        EXPORTER_MASTER: (1024, 1024),
        READER_MASTER: (1024, 1024),
        READER_ICON_DIR / "32x32.png": (32, 32),
        READER_ICON_DIR / "128x128.png": (128, 128),
        READER_ICON_DIR / "128x128@2x.png": (256, 256),
    }

    for path, expected in expected_pngs.items():
        try:
            actual = png_size(path)
            if actual != expected:
                failures.append(f"{path.relative_to(PROJECT_ROOT)}：期望 {expected}，实际 {actual}")
            else:
                print(f"{path.relative_to(PROJECT_ROOT)}: {actual[0]}×{actual[1]} OK")
        except (OSError, ValueError, struct.error) as error:
            failures.append(f"{path.relative_to(PROJECT_ROOT)}：{error}")

    for path in (EXPORTER_ICO, READER_ICO):
        try:
            actual = ico_sizes(path)
            missing = REQUIRED_ICO_SIZES - actual
            if missing:
                failures.append(
                    f"{path.relative_to(PROJECT_ROOT)}：缺少尺寸 {', '.join(map(str, sorted(missing)))}"
                )
            else:
                print(f"{path.relative_to(PROJECT_ROOT)}: {sorted(actual)} OK")
        except (OSError, ValueError, struct.error) as error:
            failures.append(f"{path.relative_to(PROJECT_ROOT)}：{error}")

    try:
        if digest(EXPORTER_MASTER) == digest(READER_MASTER):
            failures.append("Exporter 与 Reader 的图标母版不能相同")
    except OSError as error:
        failures.append(f"无法比较图标母版：{error}")

    try:
        tauri_config = json.loads(
            (PROJECT_ROOT / "knowledge-reader/src-tauri/tauri.conf.json").read_text(encoding="utf-8")
        )
        if "icons/icon.png" not in tauri_config.get("bundle", {}).get("icon", []):
            failures.append("Tauri 2 打包配置没有包含 HiDPI 图标 icons/icon.png")
    except (OSError, json.JSONDecodeError) as error:
        failures.append(f"无法读取 Tauri 2 图标配置：{error}")

    if failures:
        print("应用图标校验失败：", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1

    print("Exporter 与 Reader 图标彼此独立，桌面多分辨率资源完整。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
