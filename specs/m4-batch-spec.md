# M4 — Batch import (research spec)

**Status:** implemented, V4a–V4f green. V4d runs V3's original two cores by default (2/2) and the
full six under `--verify-m4 --all-cores` (6/6); both verified. `Core/SheetSlicer.cs` (`--slice`),
`Core/Manifest.cs`, `Core/BatchImporter.cs` (`--batch`) and `Core/M4Verification.cs`
(`--verify-m4`) are in the tree, alongside the original research passes (`--stats-m4` with
`--overlay`, and `--stats-anim`). V4d builds its two controls from the pristine ROM rather than
chaining `V3Verification.Run` off the batch output — the latter re-scans an already-modified ROM
internally and hits the same re-scan trap `ImportOptions.FreeRuns` warns about, caught live while
building this gate. Amended after M3 shipped — see the amendment note below.
**Predecessors:** `m2b-writer-spec.md` (single import), `m2c-free-space-spec.md` (92 KB pool /
99 poses), `m3-expansion-spec.md` (ExHiROM, **implemented, 6/6 cores green**).

> **Amendment pass.** This spec was written before M3 was built, and reviewing it against the
> finished M3 surfaced four things: C.3 said "scan free space once", which predates
> `Expansion.FreeRunsFor` and would have allocated from the 92 KB pool on an expanded ROM;
> whether `--batch` expands was never stated; the slicer→importer interface implied 533 temp PNGs;
> and the index side of the manifest had no source at all. A.7 (new) measures the last one, and
> in doing so falsified A.4's original "band = animation" unit.
**Amends:** the PRD's §6 input assumptions and FR8, both of which describe material that does
not match what is actually in `port/sprites/`.

---

## Part A — What the material actually is

Two sheets, ripped from the source project the PRD is based on:

| sheet | size | opaque px | poses | bytes once encoded |
|---|---|---|---|---|
| DK Jr (DKC-Style) | 1137×1943 | 352,726 | 542 | 329,334 (321 KB) |
| DK (Classic, DKC-Style) | 1236×2590 | 720,914 | 533 | 498,940 (487 KB) |

### A.1 They are sheets, not pre-sliced poses — PRD §6 is half wrong

§6 assumes "pre-sliced single poses, already reduced to the target 16-color palette with a
transparent background", and defers sheet slicing to M5. Of that:

- **"Pre-sliced" is false.** Each file is one large sheet of hundreds of poses, arranged in
  captioned animation strips. Slicing is not deferrable to M5 — nothing can be imported without
  it.
- **"Reduced to the target palette" is true, and better than hoped.** Both sheets hold exactly
  **16 distinct opaque colours, 15 of which match `Donkey Kong 1P` exactly** — 96.4 % and 97.9 %
  of opaque pixels. No partial alpha anywhere. The artwork is already in the ROM's palette.

### A.2 The one unmatched colour is captions, not artwork

The 16th colour is pure `#000000`, 2–4 % of opaque pixels. DKC's palette has no pure black — its
darkest entry is `#181000` — so black appears on these sheets **only in the rendered row captions**
("Idle", "Walking", "Cartwheel", …).

This matters more than its size suggests. `PoseLoader` (M2 Q1) demands exact RGB matches and
throws `UnmappedColor` otherwise, so a naive import refuses **every pose on the sheet for a colour
no pose contains**. A slicer that does not treat pure black as an annotation colour reports the
material as unimportable.

It also inflates measurement: counting captions as poses gave 610 for *both* sheets — a
coincidence suspicious enough to catch, and the reason for looking. Excluding all-black components
gives the real 542 / 533.

### A.3 Automatic segmentation works, at about 96 %

Flood-fill of opaque islands, then merge islands whose bboxes are within 4 px. Verified by
rendering the cuts back over the sheet and **looking at it** (`--overlay`) — every pose in the
inspected region is boxed individually and correctly.

The failures are visible in the statistics as over-budget outliers, and they are mis-merges of
adjacent poses, not tiling failures:

| | DK Jr | DK |
|---|---|---|
| over the 256×256 canvas | 0 | 5 |
| over the 88-char budget | 0 | 16 |
| max bbox | 131×49 | **252×65** |
| max chars | 74 | **201** |

A 252×65 "pose" with 201 chars is several poses that touch. **~4 % of the DK sheet needs manual
review**; the rest is clean. The overlay is the review tool, and a refusal at the char budget is
the automatic backstop — a mis-merge cannot be silently imported.

### A.4 The manifest's natural unit is the strip, not the pose

Poses whose vertical extents overlap form row bands. But a **band is a row, not an animation** —
the sheets put several captioned animations side by side on one line ("Hit  Death"; "Barrel jump
Barrel walk"). Splitting each band again at horizontal gaps well above the normal inter-pose
spacing recovers the actual strips:

| | row bands | strips | poses per strip |
|---|---|---|---|
| DK Jr | 30 | **42** | p50 13, p90 21, max 24 |
| DK | 35 | **46** | p50 13, p90 20, max 25 |

So a human names ~45 strips instead of ~533 poses. The captions themselves are rendered pixels,
not metadata, so the *names* cannot be read out of the file — but the *structure* can, and that is
what makes an authored manifest tractable rather than a 533-line chore.

> The band figure came first and was wrong to build on. A.7 is what exposed it: the longest band
> held 34 poses and **no animation in the ROM has 34 frames**, which is not a thing that can be
> true if bands are animations. The two-axis split brings the maximum to 24–25, where the ROM has
> matches.

### A.5 Deduplication is a dead end (negative result)

The source video says the author matched frame counts one-to-one with the original, and that the
original reuses frames — "some of these frames were the same frames played in reverse and some are
looped… I have fixed this in the new sprite sheet." Since M2b measured real aliasing in the stock
ROM (11 addresses shared by 26 indices), sharing one allocation between repeated poses looked like
a free capacity win.

It is not. **1 exact repeat out of 542; 0 out of 512.** He did fix it. Dedup saves 586 bytes
across both sheets — 0.07 %. Recorded so nobody builds it expecting capacity.

### A.6 Capacity: this settles M3

```
importable poses          : 1054 (1053 distinct)
total serialized bytes    : 828,274 (808 KB)
after deduplication       : 827,688 (808 KB)
M2c free-space pool       : 94,642 (92 KB) -> 99 poses
```

**8.8× over the stock pool.** And this is not an artefact of importing both characters: **DK alone
is 487 KB, 5.3× the pool.** Because the sheet's frame counts were matched to the original's, there
is no large subset to drop — importing DK *as a character* means importing on the order of 500
poses.

Against M3's extended space (3.8 MB usable) it is 21 % — comfortable.

### A.7 The index side is derivable — the *assignment* is not

The manifest needs image indices, and A.4 establishes the sheet cannot supply them. The animation
scripts can. `Core/AnimationTable.cs` is a headless port of `Animation.ParseAnimation` reduced to
"which GFX indices does each animation draw" — no bitmaps, so it needs neither palette nor decoder.

**Table extent, derived rather than assumed.** Scripts begin immediately after the pointer table at
`0xbe8572`, so the lowest pointer marks its end. Reading entries until the first implausible one
gives **440**, and `0x8572 + 440×2 = 0x88E2` is exactly the minimum pointer over those 440. Two
independent readings agreeing is the check; a wrong count leaves them disagreeing.

```
Animations       : 440 (440 parsed, 0 failed)
Frame entries    : 5,297
Distinct indices : 1,853 referenced, 1,827 in the GFX table (2,714 exist)
GFX coverage     : 67.3 % of the table is reachable from an animation
Frames/animation : p50 8, p90 21, max 315 (2 empty)
```

440/440 parsing with zero failures is itself the correctness check on the port: a wrong operand
table derails within a few instructions and hits an out-of-range command.

**What this buys.** A manifest entry can name an *animation id* and let the indices derive from it,
instead of listing 13 hex indices per strip by hand. ~45 strips × one id each.

**What it does not buy.** Matching a strip to an animation by frame count alone is ambiguous:

```
an  8-pose strip matches 52 animations
a  12-pose strip matches 17
a  18-pose strip matches  7
a  24-pose strip matches  2
unique frame counts: 21 of 51
```

So the assignment still needs a human. Two things make that tractable rather than a 440-way
search: frame count narrows most strips to single digits, and **292 of 338 multi-frame animations
draw from a tight index range** — so once one DK animation is identified, its neighbours in the
table are overwhelmingly likely to be DK's too. Identifying the character's block once, by
inspection, is the actual manual step.

**Still open:** nothing here labels an animation as DK's. That is one decode-and-look pass over a
few hundred candidates, not a research problem — but it is not done, and M4 cannot produce a
working import until it is.

---

## Part B — What this changes

**The milestone ordering, and I had it backwards.** The M2c and M3 specs both recommended M4 first
on the grounds that M3 might never be needed. That was a reasonable bet before the sheets were
measured; it is now falsified. A full character import needs ~5× more space than the stock ROM
can offer, and no amount of free-space widening closes a 5× gap (M2c's entire recovery was 15 KB).

**M3 is a prerequisite for M4 doing its actual job**, not an alternative to it.

**PRD amendments:**
- §6 — drop "pre-sliced"; slicing is M4's, not M5's. Keep the palette assumption; it holds.
- §6 — add: input sheets may carry annotation pixels in a colour outside the palette.
- FR8 — "manifest of {image PNG → target index}" understates it. The unit is a sheet plus a
  strip→indices mapping, not a directory of single-pose PNGs.
- §12 open question "hand-authored manifest vs derived from the animation tables" — the sheet
  cannot answer it (captions are pixels). Deriving the *index* side from `Animation.cs`'s tables
  remains open and is the one thing that would shrink authoring further.

---

## Part C — Design

### C.1 `Core/SheetSlicer.cs`

Promote the research segmenter, with the two policies the research established:

1. Exclude components that are entirely `AnnotationRgb` (pure black) — captions.
2. Drop components under `MinPosePixels` (64).
3. Flood-fill 8-connected, merge bboxes within `MergeGap` (4 px), order row-major by band then x.

Output: stable, numbered `(band, position)` coordinates per pose. **Stability is a requirement**,
not a nicety: the manifest addresses poses by those numbers, so a slicer change that renumbers
poses silently retargets every import. Pin it with a golden slice list (V4a).

### C.2 Manifest

```json
{
  "version": 1,
  "sheet": "dk-classic.png",
  "palette": "Donkey Kong 1P",
  "strips": [
    { "strip": 3, "name": "walk", "animation": "0x1A" },
    { "strip": 7, "name": "roll", "indices": ["0x8C", "0x90", "0x94"] }
  ],
  "overrides": [
    { "strip": 12, "pose": 5, "rect": [1044, 812, 48, 52] }
  ]
}
```

- Strips are numbered by the slicer (A.4: row band, then horizontal split), poses within one are
  ordered left-to-right.
- `animation` names an entry in the 440-script table (A.7); its indices are derived in draw order
  and zip 1:1 with the strip's poses. `indices` is the explicit escape hatch when no single
  animation matches. **Exactly one of the two is required** — a strip carrying both is a refusal,
  not a precedence rule to remember.
- **Length mismatch is a refusal, never a guess** — the PRD's own risk table says a wrong
  pose↔frame mapping means the wrong sprite is replaced. With `animation` this is a real check,
  not a formality: it is the thing that catches a mis-assigned animation id.
- Repeats within an animation are expected (A.5 measured the sheet has none, but the *scripts*
  reuse indices). A repeated index means two strip poses target one slot: refuse unless both
  poses are pixel-identical, since otherwise the second silently overwrites the first.
- `overrides` supply explicit rects for the ~4 % the segmenter mis-merges (A.3).
- `name` is documentation only; nothing keys off it.

### C.3 Batch orchestration

Resolves M2b's open item verbatim: *"M4's batch mode should import N poses in **one** invocation
against one pristine scan"*.

1. Load ROM. **Free runs come from `Expansion.FreeRunsFor(rom)`, not `FreeSpace.Scan`** — an
   expanded ROM must draw from the extended half (m3 C.4). Scanning the stock pool on an expanded
   ROM would hit `NoFreeSpace` at pose 99 of 533 while 3.8 MB sat unused. Scanned **once**;
   one `ImportLedger`.
2. Slice the sheet once.
3. For each manifest entry: pose grid → `SpriteImporter.Import` with the shared `FreeRuns` and
   ledger.
4. Collect per-pose outcome; **never abort the batch on one refusal** — a 533-pose run that dies
   on pose 4 wastes the operator's time. Report and continue.
5. Write ROM + ledger sidecar together.

**Expansion is a precondition, not a step.** `--batch` does not expand; it refuses when the
manifest's total exceeds the available pool, naming `--expand` in the refusal. Two reasons:
expansion is the one irreversible-feeling operation in the toolchain (it has `--revert`, but it
rewrites the file), and folding it into a 533-pose run would put an `ExpansionRecord` and 533
allocations in one sidecar written at the end — so a crash mid-batch leaves an expanded ROM with
no record of it. Expand first, verify, then batch against the result.

**Slicer → importer is in-memory.** `PoseLoader.Load` takes a path and re-decodes a PNG; a batch
would be writing and re-reading 533 temp files of regions it already holds. Both need a shared
core: extract the palette-mapping and crop from `PoseLoader.Load` into a method over an in-memory
region, keep `Load` as the single-file wrapper (so M2b's `--import` path is untouched), and have
the slicer call the shared one. The refusal behaviour — `UnmappedColor` naming the offending
colours — must be identical on both paths, since that is the error operators will actually hit.

Chained imports across sessions stay unsolved and stay refused (`sourceRomSha256`).

### C.4 Report (FR8's "emit a report", PRD's "QA overlay sheet")

- Per pose: band/position, target index, chars, OAM, bytes, allocated offset, geometry drift,
  outcome.
- Totals: imported / refused by reason, bytes used, pool remaining.
- **QA overlay**: the source sheet with each pose boxed and annotated with its target index —
  the same `--overlay` that validated segmentation here. It is the only artefact that makes a
  wrong mapping visible before the ROM is booted.

### C.5 CLI

```
dotnet run -- <rom> --slice <sheet.png> [--overlay out.png]        # strip/pose numbering, no writes
dotnet run -- <rom> --stats-anim                                   # animation id -> indices (A.7)
dotnet run -- <rom> --batch <manifest.json> --out <rom.sfc> [--dry-run]
dotnet run -- <rom> --verify-m4
```

`--batch` on a stock 4 MB ROM refuses a manifest it cannot fit and names `--expand`; it never
expands implicitly (C.3).

`--dry-run` must be the documented first step, as in M2b.

---

## Part D — Verification (V4)

| gate | what it checks |
|---|---|
| V4a | Slicer determinism: the same sheet yields byte-identical pose numbering (golden list) |
| V4b | Every sliced pose round-trips M1 (encode → decode → identical), as M2a does for synthetic poses |
| V4c | Batch of N against one pristine scan: ledger has N non-overlapping allocations, and the V2b containment diff shows exactly the expected changed set |
| V4d | V3 emulator gate on the batch output, both cores |
| V4e | Refusals fire: strip/index length mismatch, unmapped colour, over-budget pose, both `animation` and `indices` on one strip, duplicate index with differing poses |
| V4f | Animation table parses 440/440 with 0 failures (A.7) — the derived index side has no silent-drift mode otherwise |

V4c is the one that matters most — it is where the re-scan trap (85 % waste, 17 poses instead of
~100) would resurface.

---

## Part E — Open questions

- **Which image indices does DK actually own?** Half-answered by A.7: the tables *are* ported now
  (`--stats-anim`), so a strip can name an animation id instead of 13 hex indices. What remains is
  labelling — nothing marks an animation as DK's. Frame count narrows most strips to single
  digits and index locality clusters a character's animations together, so this is one
  decode-and-look pass, not a research problem. **It is the one thing still blocking a working
  import**, and it is the first thing to do in M4.
- **Hitboxes.** 500 re-posed frames make M2b's Part G drift report load-bearing rather than
  advisory. Still out of scope, still auto-derivable from the opaque bbox.
- **Does the target hack need palette edits?** A.1 says no for these two sheets — they are already
  `Donkey Kong 1P`. Unverified for any other character.

---

## Part F — Risks

| Risk | Mitigation |
|---|---|
| Captions treated as artwork | C.1 policy 1; caught here by the 610/610 coincidence |
| Mis-merged poses imported silently | Char-budget refusal is automatic; `overrides` + QA overlay for the ~4 % |
| Slicer renumbering retargets a manifest | V4a golden slice list |
| Wrong pose ↔ index mapping | Length mismatch refuses; QA overlay makes it visible pre-boot |
| Re-scan trap on batch | C.3 step 1: one scan, one ledger; V4c |
| Batch aborts mid-run | C.3 step 4: report and continue |
| Built against the 92 KB pool | Part B: M3 first (done) — 808 KB does not fit and never will |
| Batch allocates from the stock pool on an expanded ROM | C.3 step 1: `Expansion.FreeRunsFor`, never a bare `FreeSpace.Scan` |
| Crash mid-batch leaves an expanded ROM with no expansion record | C.3: `--batch` never expands; `--expand` is a separate, already-gated step |
| Derived indices drift silently if the walk breaks | V4f: 440/440 parse asserted |
| Strip assigned the wrong animation id | Length mismatch refuses (C.2); QA overlay annotates each pose with its resolved index |
