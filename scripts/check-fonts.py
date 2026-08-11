#!/usr/bin/env python3
"""Validate the embedded Noto Sans SC font files without third-party modules."""

from __future__ import annotations

import hashlib
import struct
import sys
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parent.parent
FONT_DIRECTORY = PROJECT_ROOT / "src" / "FeishuExporter.Desktop" / "Assets" / "Fonts"
EXPECTED_SHA256 = {
    "NotoSansSC_400Regular.ttf": "d45f67f0a7c0ca3f256950777ce6a61cc7ce5f9696d02900cbbaac25f8aa7d16",
    "NotoSansSC_600SemiBold.ttf": "b5eb7510dff58e0626c72c0861d83a3ed2be1d03047cf90a623682b8667bc5ff",
}
REQUIRED_TABLES = {b"cmap", b"head", b"maxp", b"name"}


class FontValidationError(Exception):
    pass


def read_u16(data: bytes, offset: int) -> int:
    return struct.unpack_from(">H", data, offset)[0]


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from(">I", data, offset)[0]


def validate_font(path: Path, expected_sha256: str) -> None:
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != expected_sha256:
        raise FontValidationError(
            f"SHA-256 不匹配：期望 {expected_sha256}，实际 {digest}"
        )
    if len(data) < 12:
        raise FontValidationError("文件短于字体头。")
    if data[:4] not in {b"\x00\x01\x00\x00", b"OTTO", b"true", b"typ1"}:
        raise FontValidationError("不是受支持的 OpenType/TrueType 字体。")

    table_count = read_u16(data, 4)
    directory_end = 12 + table_count * 16
    if table_count == 0 or directory_end > len(data):
        raise FontValidationError("字体表目录不完整。")

    tables: dict[bytes, tuple[int, int]] = {}
    for index in range(table_count):
        record = 12 + index * 16
        tag = data[record : record + 4]
        offset = read_u32(data, record + 8)
        length = read_u32(data, record + 12)
        end = offset + length
        if offset > len(data) or end > len(data):
            display_tag = tag.decode("ascii", errors="replace")
            raise FontValidationError(
                f"{display_tag} 表超出文件结尾：需要 {end} 字节，实际 {len(data)} 字节。"
            )
        tables[tag] = (offset, length)

    missing_tables = sorted(REQUIRED_TABLES - tables.keys())
    if missing_tables:
        names = ", ".join(tag.decode("ascii") for tag in missing_tables)
        raise FontValidationError(f"缺少必要字体表：{names}。")
    if not ({b"glyf", b"loca"} <= tables.keys() or b"CFF " in tables or b"CFF2" in tables):
        raise FontValidationError("缺少可识别的字形轮廓表。")

    maxp_offset, maxp_length = tables[b"maxp"]
    if maxp_length < 6:
        raise FontValidationError("maxp 表不完整。")
    glyph_count = read_u16(data, maxp_offset + 4)
    if glyph_count < 20_000:
        raise FontValidationError(f"字形数量异常：只有 {glyph_count} 个。")

    if {b"glyf", b"loca", b"head"} <= tables.keys():
        head_offset, head_length = tables[b"head"]
        loca_offset, loca_length = tables[b"loca"]
        _, glyf_length = tables[b"glyf"]
        if head_length < 52:
            raise FontValidationError("head 表不完整。")
        loca_format = struct.unpack_from(">h", data, head_offset + 50)[0]
        entry_size = 2 if loca_format == 0 else 4 if loca_format == 1 else 0
        if entry_size == 0 or loca_length < (glyph_count + 1) * entry_size:
            raise FontValidationError("loca 表长度或格式无效。")
        last_entry = loca_offset + glyph_count * entry_size
        final_glyph_offset = (
            read_u16(data, last_entry) * 2
            if loca_format == 0
            else read_u32(data, last_entry)
        )
        if final_glyph_offset > glyf_length:
            raise FontValidationError(
                f"字形索引超出 glyf 表：需要 {final_glyph_offset} 字节，实际 {glyf_length} 字节。"
            )

    print(f"{path.relative_to(PROJECT_ROOT)}: OK ({len(data)} bytes, {glyph_count} glyphs)")


def main() -> int:
    failures: list[str] = []
    for file_name, expected_sha256 in EXPECTED_SHA256.items():
        path = FONT_DIRECTORY / file_name
        try:
            validate_font(path, expected_sha256)
        except (FontValidationError, OSError, struct.error) as error:
            failures.append(f"{path.relative_to(PROJECT_ROOT)}: {error}")
    if failures:
        print("嵌入字体校验失败：", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
