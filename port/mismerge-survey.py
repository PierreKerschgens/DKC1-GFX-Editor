"""Find slicer poses that are really two or more figures merged. Produced A.24's table.

    python3 port/mismerge-survey.py "port/sprites/<the DK sheet>.png"

Read-only: shells out to `--slice`, decodes the sheet via sips->BMP (no PIL on this
machine), and reports rects that hold more than one figure. Research instrument, not a
gate -- it is a *lower bound* on the defect, since it only counts full-width parts and
is blind to the separate two-rows-in-one-band failure visible in strip 26.


The crude "runs per band" count is noisy both ways: one figure can contain a fully
transparent column (arm clear of the body) and split, and two figures whose limbs
overlap horizontally can join. So test the specific signature instead --

  a pose rect that splits into >=2 opaque runs, each at least MINFRAC of the strip's
  MEDIAN pose width

-- which is what a merge looks like and what an internal gap does not: an internal
gap leaves one wide part and one narrow one, never two full-width figures.
"""
import re, subprocess, struct, sys, os, statistics

SHEET = sys.argv[1]
MINFRAC = 0.60
ROM = "port/Donkey Kong Country (USA) (Rev 2).sfc"
SCRATCH = os.path.dirname(os.path.abspath(__file__))
REPO = "/Users/pierre/devel/DKC1-GFX-Editor"

out = subprocess.run(["dotnet", "run", "--project", "port/DkcTool", "--", ROM, "--slice", SHEET],
                     capture_output=True, text=True, cwd=REPO).stdout

strips = []
for line in out.splitlines():
    m = re.match(r"\s*strip\s+(\d+) \(band\s+(\d+),\s+(\d+) poses\): (.*)", line)
    if m:
        strips.append(dict(id=int(m[1]), n=int(m[3]),
                           poses=[(int(a), int(b), int(x), int(y))
                                  for a, b, x, y in
                                  re.findall(r"\d+:(\d+)x(\d+)@\((\d+),(\d+)\)", m[4])]))

# Decode the whole sheet once.
png, bmp = f"{SCRATCH}/_full.png", f"{SCRATCH}/_full.bmp"
subprocess.run(["sips", "-s", "format", "bmp", SHEET, "--out", bmp], capture_output=True)
d = open(bmp, "rb").read()
off = struct.unpack_from("<I", d, 10)[0]
W, H = struct.unpack_from("<ii", d, 18)
bpp = struct.unpack_from("<H", d, 28)[0]
bu, H = H > 0, abs(H)
stride = ((W * bpp // 8) + 3) & ~3
PX = bpp // 8
print(f"sheet {W}x{H} {bpp}bpp", file=sys.stderr)


def opaque(x, y):
    row = (H - 1 - y) if bu else y
    o = off + row * stride + x * PX
    b, g, r = d[o], d[o + 1], d[o + 2]
    a = d[o + 3] if PX == 4 else 255
    return a >= 8 and not (r > 240 and g > 240 and b > 240)


def runs(x0, x1, y0, y1):
    occ = [any(opaque(x, y) for y in range(y0, y1)) for x in range(x0, x1)]
    out, i = [], 0
    while i < len(occ):
        if occ[i]:
            s = i
            while i < len(occ) and occ[i]:
                i += 1
            out.append((x0 + s, x0 + i - 1))
        else:
            i += 1
    return out


total_extra = 0
hits = []
for s in strips:
    med = statistics.median(w for w, h, x, y in s["poses"])
    for pi, (w, h, x, y) in enumerate(s["poses"]):
        r = runs(x, min(W, x + w), y, min(H, y + h))
        big = [(a, b) for a, b in r if (b - a + 1) >= MINFRAC * med]
        if len(big) >= 2:
            gaps = [big[i][0] - big[i - 1][1] - 1 for i in range(1, len(big))]
            hits.append((s["id"], pi, w, med, big, gaps, s["n"]))
            total_extra += len(big) - 1

print(f"{len(hits)} merged pose(s) across {len(set(h[0] for h in hits))} strip(s); "
      f"{total_extra} pose(s) missing from the slicer's counts\n")
print(f"{'strip':>5} {'pose':>4} {'poses':>6} {'rect w':>6} {'median':>6}  parts (gap)")
for sid, pi, w, med, big, gaps, n in hits:
    parts = " ".join(f"{b - a + 1}" for a, b in big)
    print(f"{sid:>5} {pi:>4} {n:>6} {w:>6} {med:>6.0f}  {parts}   gaps {gaps}"
          f"   -> {n + len(big) - 1} poses")
