"""Generates the web remote icons (icon192.png, icon512.png) with no dependency.

Drawing: dark rounded square, blue ring (directional pad) and a "play" triangle.
Usage: python3 make_icons.py   (writes the PNGs next to the script)
"""
import math
import os
import struct
import zlib

BG = (17, 19, 24)
RING = (62, 166, 255)
FG = (240, 242, 245)


def inside_rounded(x, y, size, radius):
    rx = min(max(x, radius), size - radius)
    ry = min(max(y, radius), size - radius)
    return (x - rx) ** 2 + (y - ry) ** 2 <= radius ** 2


def inside_triangle(x, y, pts):
    (x1, y1), (x2, y2), (x3, y3) = pts
    d1 = (x - x2) * (y1 - y2) - (x1 - x2) * (y - y2)
    d2 = (x - x3) * (y2 - y3) - (x2 - x3) * (y - y3)
    d3 = (x - x1) * (y3 - y1) - (x3 - x1) * (y - y1)
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def pixel(x, y, size):
    """Colour (r, g, b, a) at point (x, y), in real coordinates."""
    if not inside_rounded(x, y, size, size * 0.22):
        return (0, 0, 0, 0)
    c = size / 2
    d = math.hypot(x - c, y - c)
    if size * 0.30 <= d <= size * 0.36:
        return RING + (255,)
    s = size * 0.13
    tri = [(c - s * 0.75, c - s), (c - s * 0.75, c + s), (c + s * 1.05, c)]
    if inside_triangle(x, y, tri):
        return FG + (255,)
    return BG + (255,)


def render(size, ss=4):
    rows = []
    for py in range(size):
        row = bytearray([0])  # PNG filter "none"
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    r, g, b, a = pixel(px + (sx + 0.5) / ss, py + (sy + 0.5) / ss, size)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            n = ss * ss
            alpha = acc[3] / n
            if acc[3]:
                row += bytes((round(acc[0] / acc[3]), round(acc[1] / acc[3]), round(acc[2] / acc[3]), round(alpha)))
            else:
                row += bytes((0, 0, 0, 0))
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    for size in (192, 512):
        with open(os.path.join(here, "icon%d.png" % size), "wb") as f:
            f.write(render(size, ss=4 if size <= 192 else 2))
