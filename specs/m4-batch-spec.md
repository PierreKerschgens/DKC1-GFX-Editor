# M4 — Batch import (research spec)

**Status:** research complete, **not implemented**. The research pass is in the tree
(`--stats-m4`, with `--overlay`); the slicer, manifest and batch path are not.
**Predecessors:** `m2b-writer-spec.md` (single import), `m2c-free-space-spec.md` (92 KB pool /
99 poses), `m3-expansion-spec.md` (ExHiROM, verified but unbuilt).
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

Poses whose vertical extents overlap form captioned animation strips:

| | bands | poses per band |
|---|---|---|
| DK Jr | **30** | p50 18, max 34 |
| DK | **35** | p50 15, max 30 |

So a human names ~30 strips instead of ~533 poses. The captions themselves are rendered pixels,
not metadata, so the *names* cannot be read out of the file — but the *structure* can, and that is
what makes an authored manifest tractable rather than a 533-line chore.

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
    { "band": 3, "name": "walk", "indices": ["0x8C", "0x90", "0x94"] }
  ],
  "overrides": [
    { "band": 12, "pose": 5, "rect": [1044, 812, 48, 52] }
  ]
}
```

- Poses within a band are ordered left-to-right and zip 1:1 with `indices`.
- **Length mismatch is a refusal, never a guess** — the PRD's own risk table says a wrong
  pose↔frame mapping means the wrong sprite is replaced.
- `overrides` supply explicit rects for the ~4 % the segmenter mis-merges (A.3).
- `name` is documentation only; nothing keys off it.

### C.3 Batch orchestration

Resolves M2b's open item verbatim: *"M4's batch mode should import N poses in **one** invocation
against one pristine scan"*.

1. Load ROM, scan free space **once**, create one `ImportLedger`.
2. Slice the sheet once.
3. For each manifest entry: `PoseLoader` → `SpriteImporter.Import` with the shared `FreeRuns` and
   ledger.
4. Collect per-pose outcome; **never abort the batch on one refusal** — a 533-pose run that dies
   on pose 4 wastes the operator's time. Report and continue.
5. Write ROM + ledger sidecar together.

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
dotnet run -- <rom> --slice <sheet.png> [--overlay out.png]        # numbering, no ROM writes
dotnet run -- <rom> --batch <manifest.json> --out <rom.sfc> [--dry-run]
dotnet run -- <rom> --verify-m4
```

`--dry-run` must be the documented first step, as in M2b.

---

## Part D — Verification (V4)

| gate | what it checks |
|---|---|
| V4a | Slicer determinism: the same sheet yields byte-identical pose numbering (golden list) |
| V4b | Every sliced pose round-trips M1 (encode → decode → identical), as M2a does for synthetic poses |
| V4c | Batch of N against one pristine scan: ledger has N non-overlapping allocations, and the V2b containment diff shows exactly the expected changed set |
| V4d | V3 emulator gate on the batch output, both cores |
| V4e | Refusals fire: strip/index length mismatch, unmapped colour, over-budget pose |

V4c is the one that matters most — it is where the re-scan trap (85 % waste, 17 poses instead of
~100) would resurface.

---

## Part E — Open questions

- **Which image indices does DK actually own?** The manifest's index side still has to come from
  somewhere. `Animation.cs`'s tables are not ported to `port/DkcTool`; porting them is what would
  turn strip→indices from hand-authoring into derivation. This is the single biggest remaining
  authoring cost.
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
| Built against the 92 KB pool | Part B: M3 first — 808 KB does not fit and never will |
