"""Strips that interleave two rows of figures.

A band is formed by vertical overlap, so if a tall pose in row N reaches down into
row N+1, both rows land in one band and the horizontal split then cuts across both --
producing "strips" whose poses alternate between rows. Flag any strip whose pose tops
are bimodal: a gap in sorted MinY larger than half the median pose height.
"""
import re, subprocess, sys, statistics

ROM = "port/Donkey Kong Country (USA) (Rev 2).sfc"
REPO = "/Users/pierre/devel/DKC1-GFX-Editor"

for SHEET in sys.argv[1:]:
    out = subprocess.run(["dotnet", "run", "--project", "port/DkcTool", "--", ROM, "--slice", SHEET],
                         capture_output=True, text=True, cwd=REPO).stdout
    strips = []
    for line in out.splitlines():
        m = re.match(r"\s*strip\s+(\d+) \(band\s+(\d+),\s+(\d+) poses\): (.*)", line)
        if m:
            strips.append(dict(id=int(m[1]), band=int(m[2]), n=int(m[3]),
                               poses=[(int(a), int(b), int(x), int(y))
                                      for a, b, x, y in
                                      re.findall(r"\d+:(\d+)x(\d+)@\((\d+),(\d+)\)", m[4])]))

    print(f"\n=== {SHEET.split(' - ')[-1]} : {len(strips)} strips ===")
    bad, badposes = [], 0
    for s in strips:
        if s["n"] < 2:
            continue
        medh = statistics.median(h for w, h, x, y in s["poses"])
        tops = sorted(y for w, h, x, y in s["poses"])
        gaps = [(tops[i] - tops[i - 1], tops[i - 1], tops[i]) for i in range(1, len(tops))]
        big = [g for g in gaps if g[0] > medh * 0.5]
        if big:
            bad.append((s, big))
            badposes += s["n"]
    for s, big in bad:
        rows = len(big) + 1
        print(f"  strip {s['id']:>3} (band {s['band']:>2}, {s['n']:>3} poses): "
              f"{rows} row(s), tops split at {[f'{a}|{b}' for _, a, b in big]}")
    print(f"  {len(bad)} of {len(strips)} strips interleave rows, covering {badposes} poses")
