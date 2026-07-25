# M2c — Widening the free-space class

**Status:** implemented (scan change + poison probe), research complete.
**Predecessor:** `m2b-writer-spec.md` C.4 (scan + allocate), `v3-emulator-spec.md` (the gate).
**Motive:** M2b's allocator draws from 77 KB and runs out after 84 poses. The obvious next
milestone was M3 (ROM expansion), which for a 4 MB HiROM game means ExHiROM — the one change
that can render the ROM unbootable on real hardware. Before paying that, this spec asks whether
the pool can be widened instead, and by how much.

---

## Part A — The question

M2b's scan keeps constant-filler runs ≥ 0x100 bytes, drops anything overlapping known sprite
data, then keeps only runs whose **end is congruent to 0 mod 0x8000**. That last filter throws
away 100 of 127 runs — 74 KB of the 151 KB of candidate filler.

The filter was the right call when nothing could be verified in-game. But it is a single proxy
standing in for two independent questions:

1. **Can an allocation here cross a bank boundary?** (SNES DMA must not.) An end-aligned run
   answers this structurally — but so does splitting any run at its 0x10000 boundaries, which
   costs nothing and works for every run.
2. **Does the game read these bytes?** End-of-bank padding is *probably* assembler slack. That
   is the real content of the filter, and it gives mid-bank runs no way to ever earn trust —
   including runs that are equally dead.

V3 changed what is answerable. Six cores boot headlessly from a save state, so question 2 can be
tested rather than argued. This spec measures both halves before changing anything.

Tooling added: `--stats-freespace` (static inventory, `Core/FreeSpaceResearch.cs`) and
`--poison` (dynamic probe, `Core/Emulator/PoisonProbe.cs`). Both stay in the tree as the
regression checks for the scan, the same way `--stats-m2b` does.

---

## Part B — What the measurements say

### B.1 The excluded pool is mostly not padding

A SNES BG tilemap entry is a 16-bit word whose high byte is `vhopppcc`. DKC's level maps use
palette 0 and low tile indices, so their high bytes land in `{0x00,0x01,0x40,0x41,…}` —
`(b & 0x3C) == 0`. Measuring how many words on *each side* of a run look like that, and taking
the **minimum** of the two sides (a run genuinely inside a structure matches on both; padding
often matches on one side by accident, because the blob starting after it happens to be a
tilemap), separates the classes cleanly:

| class | runs | bytes | median embeddedness |
|---|---|---|---|
| A — end-of-bank padding (trusted today) | 27 | 79,195 (77 KB) | 22 % |
| B — mid-bank (excluded) | 100 | 76,016 (74 KB) | **100 %** |

79 of the 100 excluded runs — 41 KB — sit inside tilemap-shaped data. Those bytes are **blank
sky**, read every frame that map is on screen. They are not free space that a conservative filter
happened to miss; the filter was buying real protection from them.

This falsifies the premise M2c started from. "Promoting even half the excluded 74 KB roughly
doubles capacity" was wrong: half of that 74 KB is live level data.

### B.2 The filter has an off-by-one worth 15 KB

`End % 0x8000 == 0` tests the wrong thing. Padding whose *following* blob happens to begin with
a filler byte measures as a run ending at boundary+1, and fails the test outright:

```
0x31EAE5..0x320001   overshoot 1   ->  0x151B usable
0x24EC4F..0x250001   overshoot 1   ->  0x13B1 usable
0x2FF309..0x300002   overshoot 2   ->  0x0CF7 usable
… 8 runs total, 15,447 bytes, median embeddedness 5 %
```

Three of the ROM's four largest padding runs are in this state. These are the same structural
class as A — assembler slack at the tail of a blob — differing only in whether the next blob's
first byte is zero. Truncating at the boundary recovers them while never handing out the
overshoot bytes, which belong to the next blob.

### B.3 The static reference sweep does not work, and that is worth recording

Sweeping every byte position for 24-bit little-endian values (bank ≥ 0xC0) landing inside a run
produces hundreds of hits for **every** run in **both** classes, including the 27 M2b already
allocates from. About a quarter of all byte-windows read as a plausible bank, and they land
somewhere. Only the ratio to chance carries information, and only at the extremes (median 1.55×
for class A, 0.47× for class B — i.e. the excluded runs are *less* referenced than chance).

An unaligned pointer sweep of a 4 MB ROM cannot answer "is this region referenced". Kept in
`--stats-freespace` as a normalised ratio, labelled as the negative result it is, so nobody
rebuilds it.

### B.4 The dynamic probe works — and is far weaker than it looks

`--poison` overwrites a class with deterministic per-offset noise (never 0x00/0xFF: writing
filler over filler tests nothing), restores the jungle save state captured on the pristine ROM,
walks 90 frames so the game re-DMAs, and compares. Result on snes9x:

```
CONTROL: known sprite data      2699 runs, 1719 KB   OK -- 7145 px (12.46 %) differ
A  end-of-bank padding            27 runs,   77 KB   UNREAD (frame identical)
A' boundary-straddling padding     8 runs,   15 KB   UNREAD (frame identical)
B  mid-bank, not tilemap-embedded 13 runs,   17 KB   UNREAD (frame identical)
B  mid-bank, tilemap-embedded     79 runs,   41 KB   UNREAD (frame identical)
```

The control is load-bearing. Every other line can only ever say "the frame did not change", and
that sentence is worthless until something is shown to be able to change it — a probe that fails
to write, or a capture point that renders nothing, produces exactly the same clean sweep. The
control fires (12 % of pixels), so the machinery is live.

**And the tilemap-embedded class still passed.** That class is, on the structural evidence in
B.1, live data — it just belongs to levels this capture point never visits. So the probe's
negative result at one capture point has very low power: it cleared 41 KB that should not be
cleared.

Two conclusions follow, and the second is the important one:

- A pass from `--poison` at N capture points means "unread on the paths those N points
  exercise", not "free". With N = 1 that is a weak statement.
- **Here the static signal is the stronger evidence.** Embeddedness correctly flags the 41 KB the
  emulator cleared. Trusting the dynamic result over the structural one would have been the
  expensive mistake.

---

## Part C — Decision

**Change made.** `FreeSpace.Scan` now defines end-of-bank padding as *reaches* a 0x8000 boundary
rather than *ends exactly on* one, truncating each run at the boundary it reaches
(`TruncateToBoundary`). Purely mid-bank runs stay excluded.

Nothing else changed. No new trust is required: the recovered runs are the same structural class
as the ones already in use, and the truncation preserves the property that made that class safe.

**Effect, measured:**

| | before | after |
|---|---|---|
| pool | 27 runs, 79,195 B (77 KB) | 35 runs, 94,642 B (92 KB) |
| poses before `NoFreeSpace` (V2b cumulative) | 84 | **99** |
| utilisation at exhaustion | — | 87 % |

Gates re-run after the change: `--verify-m2b` PASS (2711/2714 isolated, 0 gate failures),
`--verify-v3` PASS on snes9x **and** bsnes-mercury-balanced (gate1 identical, gate2 localised).

**Not promoted:**

- **B tilemap-embedded (79 runs, 41 KB)** — structurally live. The emulator clearing it is
  evidence about the probe, not the space.
- **B non-embedded (13 runs, 17 KB)** — a genuine maybe. Unread at the jungle capture point and
  not tilemap-shaped, but one capture point is not enough to promote 17 KB, and the largest
  members (0x38F67B at 14.9× chance, 0x2FF309 at 4.0×) are exactly where the reference ratio is
  anomalous. Earning these needs Part D.

---

## Part D — What promoting the remaining 17 KB would take

Not "run the probe again". The probe's power is bounded by capture-point coverage, so the work is
capture points, not code:

1. One save state per distinct graphics context — the six worlds, plus map screen, bonus room,
   boss, mine cart, underwater, animal buddy, Funky/Candy/Cranky huts. Each needs a golden that
   a human has *looked at* (v3-emulator-spec Part B, gate 0).
2. `--poison --class nonembedded --bisect` at every one of them, on ≥ 2 cores.
3. A run is promoted only if it is non-embedded **and** clean at every capture point.

Even then the claim is "unread on every path we exercised". That is the ceiling of this method;
it should be stated in the spec that promotes anything under it, not quietly dropped.

---

## Part E — Where this leaves M3

The pool went 77 → 92 KB and capacity 84 → 99 poses, for a scan change that required no new
trust. That is a fifth more room, not a doubling — the widening thesis was mostly wrong, and the
measurement is what showed it.

So M3 is still the answer if a real character set exceeds 99 poses, and the ExHiROM analysis in
the M3 spec is still the thing that has to be got right (map mode $FFD5 = $31 → $35, the
`Mask()` / `0xC00000 + fileOffset` invariant that M0–M2b all inherit, checksum maintenance at
$FFDC/$FFDE, per-core behaviour — now testable, since V3 boots six cores).

Ordering unchanged from the recommendation that opened this work, with one item resolved:

1. ~~Widen the free-space class~~ — done, +15 KB, +15 poses.
2. **M4 (batch import)** — needed before importing a real sheet regardless, independent of M3.
3. **M3 (expansion)** — when 99 poses actually proves insufficient, with the ExHiROM decision
   made deliberately.

---

## Part F — Risks

| Risk | Mitigation |
|---|---|
| Truncated run's overshoot bytes belong to the next blob | Truncation stops *at* the boundary; overshoot is never allocated |
| A recovered run is not really padding | Same structural class as A; V2b + V3 re-run on both cores after the change |
| `--poison` clean sweep read as "this space is free" | Positive control required; result voided without it; every report states the capture-point caveat |
| Someone rebuilds the static pointer sweep | B.3 keeps it in the tool as a normalised ratio with the negative result documented |
| Tilemap-embedded space promoted on a passing probe | Explicitly refused in Part C, with the reasoning |
