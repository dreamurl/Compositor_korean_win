"""A stand-in image generator for CI: draws a shaded head-and-shoulders bust and writes it as a PNG.

The poster check (scripts/mcp_poster.py) points COMPOSITOR_IMAGE_COMMAND here, so generate_image runs
end to end — template filled, command started, PNG read back and placed — without an account on an
image service. Drawing the figure also keeps the check free of downloaded photographs, whose content
cannot be reviewed before it lands in a public artifact.

Usage: python scripts/stand_in_image.py <output.png> <width> <height> [prompt ...]
The prompt is accepted and ignored.
"""

import struct
import sys
import zlib


def bust(width, height):
    """Rows of RGB bytes: a pale backdrop with a dark suited figure, lit from the upper left."""
    cx = width * 0.5
    head_cx, head_cy = cx, height * 0.30
    head_rx, head_ry = width * 0.17, height * 0.155
    neck_half, neck_top, neck_bottom = width * 0.07, height * 0.40, height * 0.56
    shoulder_cy, shoulder_rx, shoulder_ry = height * 1.02, width * 0.46, height * 0.46

    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            # Backdrop: a soft vertical falloff, like a studio sweep.
            shade = 232 - int(28 * y / height)
            r = g = b = shade

            dx, dy = (x - head_cx) / head_rx, (y - head_cy) / head_ry
            in_head = dx * dx + dy * dy <= 1
            in_neck = abs(x - cx) <= neck_half and neck_top <= y <= neck_bottom
            sx, sy = (x - cx) / shoulder_rx, (y - shoulder_cy) / shoulder_ry
            in_body = sx * sx + sy * sy <= 1

            if in_body:
                # Jacket: dark, lighter towards the upper left; a pale shirt collar in the middle.
                light = max(0.0, 1 - ((x - cx * 0.7) ** 2 + (y - height * 0.6) ** 2) ** 0.5 / (width * 0.8))
                r = g = b = 40 + int(70 * light)
                if abs(x - cx) < width * 0.09 - (y - height * 0.58) * 0.25 and y > height * 0.56:
                    r = g = b = 210
            elif in_head or in_neck:
                # Skin tone with a highlight on the lit side of the head.
                light = 1.0
                if in_head:
                    light = 0.75 + 0.35 * max(0.0, -dx * 0.6 - dy * 0.5)
                r, g, b = (min(255, int(c * light)) for c in (214, 170, 140))
                # Hair: the top of the head.
                if in_head and dy < -0.35 + 0.15 * dx * dx:
                    r, g, b = 58, 42, 34
            row += bytes((r, g, b))
        rows.append(bytes(row))
    return rows


def write_png(path, width, height, rows):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + row for row in rows)
    with open(path, "wb") as file:
        file.write(b"\x89PNG\r\n\x1a\n")
        file.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)))
        file.write(chunk(b"IDAT", zlib.compress(raw, 6)))
        file.write(chunk(b"IEND", b""))


if __name__ == "__main__":
    output, width, height = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    write_png(output, width, height, bust(width, height))
