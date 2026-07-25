# Handoff — sprite-importer-port

State of play for whoever picks this up next. Written 2026-07-25.
The durable record is in the numbered specs; this file is the map and the open edges.

---

## Where things stand

| milestone | state |
|---|---|
| M0 core port, M1 encoder, M2a tiler, M2b writer, M2c free space | done, gated |
| M3 ROM expansion (ExHiROM) | done, `--verify-m3 --all-cores` 6/6 |
| M4 batch import | done, `--verify-m4` **7/7** (V4a–V4g) |
| M5 | not started. Two candidates have emerged — see "What M5 probably is" |

**Always run the tool from the repo root.** Asset paths (`port/sprites`, `port/emu/states`) are
relative, and from the wrong directory sheet-dependent gates report SKIP, which is indistinguishable
from "this machine has no sheets".

```
dotnet run --project port/DkcTool -- "port/Donkey Kong Country (USA) (Rev 2).sfc" --verify-m4
```

`--verify-m4` runs 2 emulator cores by default and 6 under `--all-cores`. Both pass.

---

## The placement bugs — resolved and confirmed in play

**DK bobbed vertically through an imported walk cycle.** Reported from play; invisible in every
still. Full analysis in `m4-batch-spec.md` A.17. **Fixed by `--batch --align-strip`.**

Measured with `--baseline 0xE0..0x12C` (foot-line spread):

| ROM | foot line | centre X |
|---|---|---|
| stock DKC walk | 2 px | 4 px |
| top-anchored (original behaviour) | 10 px | — |
| `--anchor-bottom` (failed attempt) | 8 px | — |
| `--align-strip`, vertical only | 3 px | 7 px |
| **`--align-strip`, both axes** | **3 px** | **4 px** |

**Two defects hid behind one symptom.** Fixing the vertical bob left a horizontal one: X was still
anchored to the slot's left edge, so wider poses dragged the body sideways at nearly twice stock's
travel. Matching the replaced frame's *centre* fixes it. The residual 3-vs-2 vertical is the sheet's
own drawn bob — the artist's motion, not the tool's.

Root cause, for the record: the anchor was applied at the wrong *level*. Each pose was cropped to
its own bbox and re-anchored to *its own* target slot's bbox, substituting the replaced animation's
per-frame variation for the sheet's. A slot's bbox bottom is not a ground line either — in a
knuckle-walk the lowest pixel is sometimes a hand. `--align-strip` places a whole run against one
reference and carries the sheet's relative offsets through; X matches each replaced frame's centre.

**Run `--baseline <lo>..<hi>` on every imported run before calling it done** — compare its foot-line
and centre-X spread against the same range in the stock ROM. Both placement defects were invisible
in stills and found only by that comparison.

**Confirmed in play**: the two-axis build reads as smooth as a stock animation. Three attempts —
two measured plausibly and failed in the emulator; the third matched stock on both axes and held.

`--align-strip` is **opt-in**, and making it the default was tried and backed out — it turns V4d
red. That gate re-imports the ROM's own sprites and demands a pixel-identical frame; alignment moves
them. **The blocker is structural:** the slicer measures *opaque-pixel* bounds while a slot exposes
*tile-placement* bounds, which are 8 px-grid aligned, so a sheet-derived offset cannot reproduce a
slot-derived one exactly. Reconcile those two coordinate systems and the default can flip.

(One fix from that attempt was kept: `BuildSyntheticSheet` now preserves each pose's original origin
instead of top-aligning them all, which makes the fixture resemble a real sheet. It narrowed the
diff from 1801 px to 1062 px and is a better fixture regardless.)

**Still outstanding, and a prerequisite before any 533-pose run:** M2b's drift check fires
`[DRIFT > 4px]` on *100 % of* imported poses, which trained me to read it as noise for an entire
session while it was describing this exact visible fault. It needs a severity split — feet-line
movement is a defect, extent change is a hitbox note.

---

## Strip → animation mapping (the long pole)

The sheet side is done; the ROM side is ~20 % done. `m4-batch-spec.md` A.10–A.16.

**Confirmed in-game** (booted, observed in motion — the only tier that has earned the word):

| sheet run | ROM indices | note |
|---|---|---|
| 0 "Idle" | `0x8C..0xDC` | anim 4/108 |
| 6 "Walk" | `0xE0..0x12C` | anim 3 |

**Provisional** (identified from draw-order montages, *never observed in motion*): Roll →
`0x330..0x37C`, Ground Slap → `0x2E4..0x32C`, Swim → `0x3A4..0x3DC`, Death → anim 16.
Treat as unverified — two equally confident pairings in this tier were later falsified.

**Next obvious test:** Run is very likely `0x130..0x17C` (20 frames vs the sheet's 20-pose *Run*).
Import strip 7 there and boot; if the new art appears while running, that is a third confirmation.

**Known unreachable:** *Minecart ×2* and *Steel Keg Ride* cannot be matched through the animation
table — no script draws both DK and a vehicle (A.15). Their DK poses are composited by game code.
Look in `0x538..0x594`, which is DK's mounted/riding pose set.

**Where I am blind:** rope / ledge / swing. Several hanging-climbing groups exist and I cannot
distinguish them by silhouette. Needs someone who knows the game.

---

## Hard-won gotchas (each cost a wrong conclusion)

1. **Contact sheets show what a sprite *looks like*, never what the game *uses it for*.** One boot
   overturned the anchor pairing six passes of static analysis had treated as settled (A.16).
2. **Verify against draw order, never an index range.** An animation's frames are not its contiguous
   index range; rendering the range nearly cost a correct match (A.14).
3. **Frame-count matching does not work** — 0 for 2, including a *unique* in-block match. The sheet's
   author changed frame counts deliberately (A.9).
4. **Palette identity needs all candidates on screen at once** (`--palette-sweep`), not a hand-picked
   few. Judging "coherent" in isolation produced two wrong identifications of the same block (A.13).
5. **`--zoom` is capped by `--cell`** (`scale = min(zoom, cell/w, cell/h)`). Raising zoom alone does
   nothing. A block read as "not DK" purely because it was rendered too small.
6. **`--emu-golden` overwrites the stored golden.** Never run it on a modified ROM — it would
   silently rebase every future gate on a hacked image. Use `--emu-diff`.
7. **DK knuckle-walks.** Upright = idle, on all fours = locomotion. Getting this backwards
   invalidated two "confirmed" pairings at once.

---

## Tooling map

Research/inspection, all read-only:

```
--slice <sheet> [--overlay f]        strip/pose numbering
--captions <sheet> --out f           read strip captions (they are pixels, not metadata)
--whose [seed] [--contact f]         index ownership closure from a seed
--contact-range <lo>..<hi> --contact f [--zoom N] [--cell N] [--cols N] [--stride N]
--anim-sheet <lo>..<hi> --out f [--props] [--zoom 2] [--cell N]
--anims-in <lo>..<hi> [--frames N]   in-block animations, and the prop-drawing ones it excludes
--palette-sweep <idx> --out f        one sprite under all 79 palettes
--near <idx> [--count N]             neighbours in ROM *data* order, not table order
--baseline <lo>..<hi>                opaque bbox per sprite + foot-line spread (bob measurement)
```

Writing: `--import`, `--batch` (both take `--dry-run`; `--batch` also `--align-strip`),
`--expand`, `--revert`.
`--revert` undoes imports **and** expansion, byte-exactly, across a chain of runs (V4g).

Test ROMs in `port/` (gitignored): `dk-walk-test.sfc` (walk art in idle slots),
`dk-move-test.sfc` (walk art in walk slots), `dk-move-anchored.sfc` (`--anchor-bottom`, failed),
**`dk-move-aligned2.sfc` (`--align-strip`, both axes — the current fix)**; `dk-move-aligned.sfc` is the vertical-only version.

---

## What M5 probably is

Two candidates, both surfaced by M4 rather than planned:

1. **Animation-script editing.** 8 of 46 DK strips have pose counts no DK animation has, and the
   author changed frame counts deliberately. Without script editing, a faithful full-character
   import is impossible for those runs — you can only leave stale frames (A.9).
2. ~~**Strip-level pose placement.**~~ Implemented as `--align-strip`; awaiting in-game
   confirmation. If it holds, M5 is (1) plus the drift-check severity split.

---

## Key numbers

- DK owns `0x8C..0x950`, 562 indices; Diddy begins `0x954` (A.8).
- `0x858..0x8A8` inside that range is **not DK** — most likely Manky Kong. **Exclude from any DK
  manifest** (A.13). Reference at `port/DkcTool/testdata/manky-reference.png`.
- DK sheet: 533 poses, 46 strips, **29 authorable captioned runs** (a strip is not an animation).
- Capacity: DK alone is 487 KB against a 92 KB stock pool → `--expand` is mandatory for a full
  import, but a single ~20-pose run fits stock with room to spare.
