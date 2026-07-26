"""Crop a region of a sheet and composite it over WHITE, then scale.

    python3 port/flatten-region.py <sheet.png> <x> <y> <w> <h> <scale> <out.png>

Exists because the sheets' captions are pure black (SheetSlicer.AnnotationRgb) on a
transparent background. Anything that flattens alpha to black -- `sips -z`, and the
`--captions` render -- turns them into black-on-black and they vanish silently rather
than obviously. That hid the structural annotations ("(Loop and Reverse)", "(Hold)",
"(Also used in Bonus Games)") for the whole of A.10-A.28; see A.29.

No PIL on this machine, so it decodes via sips->BMP and writes the PNG by hand.
"""
import struct, sys, zlib, subprocess, tempfile, os

src, x0, y0, w, h, scale, out = (sys.argv[1], int(sys.argv[2]), int(sys.argv[3]),
                                 int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6]), sys.argv[7])

tmp = tempfile.mkdtemp(prefix="dkc-cap-")
bmp = os.path.join(tmp, "s.bmp")
subprocess.run(["sips", "-s", "format", "bmp", src, "--out", bmp], capture_output=True, check=True)

d = open(bmp, "rb").read()
off = struct.unpack_from("<I", d, 10)[0]
W, H = struct.unpack_from("<ii", d, 18)
bpp = struct.unpack_from("<H", d, 28)[0]
bu, H = H > 0, abs(H)
stride = ((W * bpp // 8) + 3) & ~3
PX = bpp // 8

w = min(w, W - x0)
h = min(h, H - y0)

rows = []
for y in range(y0, y0 + h):
    row = bytearray()
    srow = (H - 1 - y) if bu else y
    base = off + srow * stride
    for x in range(x0, x0 + w):
        o = base + x * PX
        b, g, r = d[o], d[o + 1], d[o + 2]
        a = d[o + 3] if PX == 4 else 255
        # over white
        r = (r * a + 255 * (255 - a)) // 255
        g = (g * a + 255 * (255 - a)) // 255
        b = (b * a + 255 * (255 - a)) // 255
        row += bytes((r, g, b)) * scale
    for _ in range(scale):
        rows.append(bytes(row))

ow, oh = w * scale, h * scale
raw = b"".join(b"\x00" + r for r in rows)


def chunk(tag, data):
    c = struct.pack(">I", len(data)) + tag + data
    return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", struct.pack(">IIBBBBB", ow, oh, 8, 2, 0, 0, 0))
       + chunk(b"IDAT", zlib.compress(raw, 6))
       + chunk(b"IEND", b""))
open(out, "wb").write(png)
print(f"{out}: {ow}x{oh} (source {w}x{h} @ {x0},{y0})")
