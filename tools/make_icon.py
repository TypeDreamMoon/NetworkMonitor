#!/usr/bin/env python3
"""Generate Assets/networkmonitor.ico procedurally (no external deps).

Design: dark rounded square, teal down arrow + amber up arrow, drawn with 4x4
supersampling so the 16px variant stays legible in the tray.
"""
import math
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "NetworkMonitor.WinUI", "Assets", "networkmonitor.ico")

BG_TOP = (0x15, 0x1C, 0x28)
BG_BOTTOM = (0x0A, 0x0D, 0x13)
BORDER = (0x2A, 0x36, 0x48)
DOWN = (0x3A, 0xD6, 0xC0)
UP = (0xFF, 0xB4, 0x54)


def rounded_rect_sdf(x, y, left, top, right, bottom, r):
    cx = (left + right) / 2
    cy = (top + bottom) / 2
    hw = (right - left) / 2 - r
    hh = (bottom - top) / 2 - r
    dx = abs(x - cx) - hw
    dy = abs(y - cy) - hh
    ax, ay = max(dx, 0), max(dy, 0)
    return math.hypot(ax, ay) + min(max(dx, dy), 0) - r


def point_in_tri(px, py, a, b, c):
    def sign(p1, p2, p3):
        return (p1[0] - p3[0]) * (p2[1] - p3[1]) - (p2[0] - p3[0]) * (p1[1] - p3[1])

    d1 = sign((px, py), a, b)
    d2 = sign((px, py), b, c)
    d3 = sign((px, py), c, a)
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def arrow(cx, y_tail, y_tip, half_shaft, half_head, head_len):
    """Vertical arrow from y_tail to y_tip (tip is the pointy end)."""
    down = y_tip > y_tail
    if down:
        shaft_top, shaft_bottom = y_tail, y_tip - head_len
        base, tip = y_tip - head_len, y_tip
    else:
        shaft_top, shaft_bottom = y_tip + head_len, y_tail
        base, tip = y_tip + head_len, y_tip

    def test(x, y):
        if abs(x - cx) <= half_shaft and shaft_top <= y <= shaft_bottom:
            return True
        return point_in_tri(x, y, (cx - half_head, base), (cx + half_head, base), (cx, tip))

    return test


def render(size, ss=4):
    w = h = size * ss
    # shape coords in [0,1]
    def shape(px, py):
        # px,py in pixel space -> normalized
        x = (px + 0.5) / w
        y = (py + 0.5) / h
        sdf = rounded_rect_sdf(x, y, 0.055, 0.055, 0.945, 0.945, 0.30)
        if sdf > 0:
            return None
        # vertical gradient
        t = y
        bg = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(3))
        # border: 2.5% inset ring
        inner = rounded_rect_sdf(x, y, 0.085, 0.085, 0.915, 0.915, 0.275)
        if inner > 0:
            bg = BORDER
        # down arrow (left), up arrow (right)
        down = arrow(0.345, 0.230, 0.785, 0.072, 0.190, 0.235)
        up = arrow(0.655, 0.785, 0.215, 0.072, 0.190, 0.235)
        if down(x, y):
            return DOWN
        if up(x, y):
            return UP
        return bg

    # supersample into float accumulation per output pixel
    out = bytearray()
    for oy in range(size):
        for ox in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            n = 0
            for sy in range(ss):
                for sx in range(ss):
                    c = shape(ox * ss + sx, oy * ss + sy)
                    n += 1
                    if c is None:
                        continue
                    acc[0] += c[0]
                    acc[1] += c[1]
                    acc[2] += c[2]
                    acc[3] += 255
            a = acc[3] / n
            if a <= 0:
                out.extend((0, 0, 0, 0))
            else:
                # premultiplied colors were accumulated only where opaque
                cnt = acc[3] / 255
                out.extend((
                    int(acc[0] / cnt),
                    int(acc[1] / cnt),
                    int(acc[2] / cnt),
                    int(a),
                ))
    return bytes(out)


def png(size, rgba):
    raw = b"".join(b"\x00" + rgba[y * size * 4:(y + 1) * size * 4] for y in range(size))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = []
    for s in sizes:
        print("rendering", s)
        images.append((s, png(s, render(s))))

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    payload = b""
    for s, data in images:
        entries += struct.pack(
            "<BBBBHHII",
            0 if s >= 256 else s,
            0 if s >= 256 else s,
            0, 0, 1, 32, len(data), offset + len(payload),
        )
        payload += data

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(header + entries + payload)
    print("wrote", OUT, os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    main()
