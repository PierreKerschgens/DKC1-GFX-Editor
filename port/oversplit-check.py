"""The opposite error to a merge: a figure split into parts.

If the asymmetric rule over-corrected, a figure whose limb clears the body now stays
split, and shows up as a pose much smaller than its strip's median with a tiny gap to
its neighbour. Report every pose under HALF the strip's median opaque-pixel count,
with the gap to the nearest neighbour in the strip.
"""
import re, subprocess, sys, statistics

SHEET = sys.argv[1]
ROM = "port/Donkey Kong Country (USA) (Rev 2).sfc"
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

print(f"{'strip':>5} {'pose':>4} {'w':>4} {'h':>4} {'med w':>6} {'frac':>5}  gap L / R")
runts = 0
for s in strips:
    if s["n"] < 3:
        continue
    medw = statistics.median(w for w, h, x, y in s["poses"])
    ordered = sorted(s["poses"], key=lambda p: p[2])
    for i, (w, h, x, y) in enumerate(ordered):
        if w >= 0.5 * medw:
            continue
        gl = x - (ordered[i - 1][2] + ordered[i - 1][0]) if i else None
        gr = ordered[i + 1][2] - (x + w) if i + 1 < len(ordered) else None
        runts += 1
        print(f"{s['id']:>5} {i:>4} {w:>4} {h:>4} {medw:>6.0f} {w / medw:>5.2f}  "
              f"{gl if gl is not None else '-':>4} / {gr if gr is not None else '-':<4}")

print(f"\n{runts} pose(s) under half their strip's median width")
