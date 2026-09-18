#!/usr/bin/env python3
"""Generate src/RemoteCommanderTray/app.ico.

The application icon is the only binary asset in the repository, so it is
generated from code rather than hand-drawn: run this script to reproduce it
byte-for-byte. Tray *status* icons are not produced here - those are drawn at
runtime by TrayIcons.cs so they follow the user's DPI.

Shapes are described as signed distance fields, which makes anti-aliasing a
one-line blend and keeps the whole rasterizer under a hundred lines.
"""

from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path

OUTPUT = Path(__file__).resolve().parent.parent / "src" / "RemoteCommanderTray" / "app.ico"
SIZES = (16, 24, 32, 48, 64, 128, 256)

BACKGROUND = (37, 99, 235)      # blue-600
GLYPH = (255, 255, 255)


def rounded_rect_sdf(px: float, py: float, cx: float, cy: float, hw: float, hh: float, r: float) -> float:
    dx = abs(px - cx) - (hw - r)
    dy = abs(py - cy) - (hh - r)
    outside = math.hypot(max(dx, 0.0), max(dy, 0.0))
    inside = min(max(dx, dy), 0.0)
    return outside + inside - r


def segment_sdf(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    length_sq = vx * vx + vy * vy
    t = 0.0 if length_sq == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / length_sq))
    return math.hypot(wx - t * vx, wy - t * vy)


def coverage(distance: float) -> float:
    """Convert a signed distance in pixels into an alpha value."""
    return max(0.0, min(1.0, 0.5 - distance))


def render(size: int) -> bytes:
    """Render one RGBA frame as raw bytes."""
    s = size
    # Glyph geometry, expressed as fractions of the icon so every size matches.
    pad = s * 0.06
    radius = s * 0.22
    stroke = max(1.4, s * 0.085)

    chevron_left = s * 0.30
    chevron_mid = s * 0.50
    chevron_top = s * 0.30
    chevron_bottom = s * 0.60
    chevron_center = (chevron_top + chevron_bottom) / 2

    bar_left = s * 0.55
    bar_right = s * 0.74
    bar_y = s * 0.70

    pixels = bytearray()
    for y in range(s):
        for x in range(s):
            px, py = x + 0.5, y + 0.5

            bg_alpha = coverage(rounded_rect_sdf(px, py, s / 2, s / 2, s / 2 - pad, s / 2 - pad, radius))

            glyph_distance = min(
                segment_sdf(px, py, chevron_left, chevron_top, chevron_mid, chevron_center),
                segment_sdf(px, py, chevron_mid, chevron_center, chevron_left, chevron_bottom),
                segment_sdf(px, py, bar_left, bar_y, bar_right, bar_y),
            ) - stroke / 2
            glyph_alpha = coverage(glyph_distance) * bg_alpha

            r = BACKGROUND[0] * (1 - glyph_alpha) + GLYPH[0] * glyph_alpha
            g = BACKGROUND[1] * (1 - glyph_alpha) + GLYPH[1] * glyph_alpha
            b = BACKGROUND[2] * (1 - glyph_alpha) + GLYPH[2] * glyph_alpha

            pixels += bytes((round(r), round(g), round(b), round(bg_alpha * 255)))
    return bytes(pixels)


def png_chunk(tag: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + tag
        + payload
        + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
    )


def to_png(size: int, rgba: bytes) -> bytes:
    stride = size * 4
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter type 0 (None)
        raw += rgba[y * stride:(y + 1) * stride]

    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + png_chunk(b"IEND", b"")
    )


def to_bmp(size: int, rgba: bytes) -> bytes:
    """Classic DIB entry (BITMAPINFOHEADER + bottom-up BGRA + AND mask).

    Windows has read PNG entries since Vista, but only for the large sizes in
    some shell code paths, so every size up to 48px is emitted as a DIB.
    """
    stride = size * 4
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        row = rgba[y * stride:(y + 1) * stride]
        for x in range(size):
            r, g, b, a = row[x * 4:x * 4 + 4]
            xor += bytes((b, g, r, a))

    mask_stride = ((size + 31) // 32) * 4
    and_mask = bytes(mask_stride * size)

    header = struct.pack(
        "<IiiHHIIiiII",
        40,              # biSize
        size,            # biWidth
        size * 2,        # biHeight (XOR + AND)
        1,               # biPlanes
        32,              # biBitCount
        0,               # biCompression (BI_RGB)
        len(xor) + len(and_mask),
        0, 0, 0, 0,
    )
    return header + bytes(xor) + and_mask


def build_ico(frames: list[tuple[int, bytes]]) -> bytes:
    header = struct.pack("<HHH", 0, 1, len(frames))
    directory = b""
    payload = b""
    offset = len(header) + 16 * len(frames)
    for size, data in frames:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0,
            0,
            1,
            32,
            len(data),
            offset,
        )
        payload += data
        offset += len(data)
    return header + directory + payload


def main() -> None:
    frames = [
        (size, to_bmp(size, render(size)) if size <= 48 else to_png(size, render(size)))
        for size in SIZES
    ]
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(build_ico(frames))
    print(f"wrote {OUTPUT} ({OUTPUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
