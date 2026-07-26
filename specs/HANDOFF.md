# Handoff — sprite-importer-port

State of play for whoever picks this up next. Written 2026-07-25, updated 2026-07-26.
The durable record is in the numbered specs; this file is the map and the open edges.

---

## Where things stand

| milestone | state |
|---|---|
| M0 core port, M1 encoder, M2a tiler, M2b writer, M2c free space | done, gated |
| M3 ROM expansion (ExHiROM) | done, `--verify-m3 --all-cores` 6/6 |
| M4 batch import | done, `--verify-m4` **7/7** (V4a–V4g), V4d 6/6 cores with alignment defaulted on |
| M5 | not started. One candidate left — see "What M5 probably is" |

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

**Strip alignment is now the default** (A.19). It was opt-in because defaulting it turned V4d red,
and that was called a structural blocker — the slicer measures *opaque-pixel* bounds while a slot
exposes *tile-placement* bounds. It was really a **units error**, and it is fixed:

- `SpriteSlot` now carries `OpaqueMinX/MinY/MaxX/MaxY` beside the placement four. Anything compared
  against a sheet uses the opaque box; the ROM's own bytes use placement. The doc comment on that
  type says which is which — read it before touching placement code.
- One reference pose supplies the baseline on **both** sides (it used to be the strip's deepest pose
  on the sheet against the *first* pose's slot in the ROM — the same pose only by luck).

Together those make re-importing a slot's own art an exact identity, which is what V4d demands.
**V4 7/7, V4d 6/6 cores.** `--no-align-strip` opts out; `--align-strip` is an accepted no-op.

It also fixed a real error, not just a gate: the old build sat **2 px high and 1 px right**
(bottom 125..128 vs stock's 127..129). The reconciled build is 127..130 / centre-X 126..130, which
matches stock exactly. That was the residual risk A.17 flagged and could not test.

**Booted and confirmed smooth** (`port/dk-move-reconciled.sfc`) — no regression against the
already-confirmed `dk-move-aligned2.sfc`, so the default flip is safe. The 2 px correction itself was
**not** visible in play and should not be claimed as confirmed: sub-3px placement error is below what
play can resolve, which is why `--baseline` and `--coords` exist. A.19 has the three-way split.

(One fix from the earlier attempt was kept: `BuildSyntheticSheet` preserves each pose's original
origin instead of top-aligning them all, which makes the fixture resemble a real sheet.)

**Drift severity split — done** (A.18). The old `[DRIFT > 4px]` fired on 100 % of poses and was
therefore ignored for a whole session while describing this very bug. Now `[FEET MOVED ±Npx]` vs
`[extent changed — hitbox note]`; flagged set drops from 20/20 to 15 (unaligned) / 9 (aligned).

**But treat it as a hint, not a verdict.** Per-pose drift compares a pose to *its own* replaced
frame; bobbing is a property of the *run*. `--baseline <lo>..<hi>` against the stock ROM is the
authoritative check and the one to trust.

---

## Strip → animation mapping (the long pole)

The sheet side is done; the ROM side is ~20 % done. `m4-batch-spec.md` A.10–A.16.

**Confirmed in-game** (booted, observed in motion — the only tier that has earned the word):

| sheet run | ROM indices | note |
|---|---|---|
| 0 "Idle" | `0x8C..0xDC` | anim 4/108 |
| 6 "Walk" | `0xE0..0x12C` | anim 3 |
| 7 "Run" | `0x330..0x37C` | anims 2/14/20, needs `--flat-x` (A.20) |

**Provisional** (identified from draw-order montages, *never observed in motion*): Ground Slap →
`0x2E4..0x32C`, Swim → `0x3A4..0x3DC`, Death → anim 16. Treat as unverified — **three** pairings in
this tier have now been falsified, including Roll.

**Localised by the A.22 paint survey** (one boot, ~15 animations — chunk-level, not exact yet):

| chunk | actions seen |
|---|---|
| `0x380..0x4FC` | **roll**, getting hit, enemy bounce, barrel flight, narrow crouch |
| `0x180..0x32C` | turn, idle(?), walk+throw barrel |
| `0x500..0x67C` | duck, rhino, rope swing (left half) |
| `0x680..0x7FC` | banana cave, carry barrel, Diddy swap, rope climb, rope swing (right half), **life HUD** |

**Known wrong:** `0x330..0x37C` is Run, not Roll (A.20). `0x188..0x1FC` is not Roll either (A.21),
despite being an unmistakable somersault. Static identification is **0 for 4** — use `--paint`, not
montages.

**Round 4/control (A.22):** **Roll is `0x380..0x3DC`** — 24 indices. Established by elimination:
the control painted all of `0x380..0x3FC` white and roll turned white; round 4 painted only
`0x3E0..0x3FC` white and roll did not. `0x3E0..0x3FC` is the **cliff-teeter start**.

**Round 2 (A.22):** Roll localised to `0x380..0x3FC` — 32 indices, from ~430, in two boots. Also: narrow
crouch → `0x380..0x3FC`; getting hit → `0x400..0x47C`; enemy bounce and cliff teeter span *both*
ranges; swimming alternates `0x380..0x3FC` with an unpainted range.

⚠️ That round also reported **no amber and no light pink anywhere**, which was written up as
"those ranges are unused, suspect them of not being DK". **Withdraw that** — amber was later shown
to be unreadable on DK (A.22), so "no amber seen" is equally consistent with amber being invisible.
Light pink is unverified either way. `0x800..0x854`/`0x8AC..0x950` and `0x480..0x4FC` are **not**
known to be unused; re-test them in white before believing anything about them.

**Resolved:** DK has **two** idle animations — standing, and a chest-beating one after several
seconds. `0x8C..0xDC` (A.16) and the white `0x180..0x32C` sighting are different animations, not a
contradiction. The chest-beat has its own sheet strip (3, "Bang Chest", 7 poses).

**Open, do not treat as settled:**
- *Jump* is cornered: not amber in round 2, not white in round 1, so it draws from a confirmed or
  characterised range. Idle/Walk/Run are independently something else, which leaves **`0x130..0x17C`**
  — the 40→72→36 height arc reads as crouch/apex/landing. The A.20 "climb" reading is probably wrong.
  One paint round settles it.
- `0x680..0x7FC` paints the **life/balloon HUD**, so it is not purely DK. A.8's ownership claim has
  another shared island in it besides `0x858..0x8A8`.
- **A contiguous index range is not an animation** — now with direct in-play evidence, not just
  draw-order inference: the enemy bounce runs red→white→red across two ranges (A.22).
`0x130..0x17C` is a climb/hang, not the Run — its height swells 40→72→36, a one-shot, and DK is
upright with both arms overhead.

**Frame-count matching is 0 for 3.** Stop reaching for it. `--baseline`'s *height profile* across a
range is the cheap discriminator that works: a loop holds height roughly constant, a one-shot swells
and recovers. It refuted the `0x130..0x17C` prediction from the ROM side alone, before an import
existed.

**Next test:** find Roll. It is not `0x330..0x37C` and it is not a 20-frame animation (those are all
accounted for: Walk, the climb, and Run).

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
8. **Booting settles *what a sprite is for*; measuring settles *where it sits*.** The companion to
   gotcha 1, and it does not repeal it. A 2 px placement error is invisible in play — the A.19 build
   was booted and read as no different. Don't ask the emulator a question `--baseline`/`--coords`
   answer better, and don't record "looked fine" as confirmation of a sub-3px claim (A.19).
9. **A fix validated on one animation is not validated.** Strip alignment was developed against the
   walk and looked finished; the run exposed an untouched defect on the X axis immediately, because
   stock's walk centre-X spans 4 px smoothly and stock's run spans 9 px with a 6 px single-frame
   step. Before calling a placement rule done, run it against an animation with *different* motion
   characteristics (A.20).
10. **When a search keeps returning negative results, improve the *test*, not the guesses.** Four
    wrong DK pairings were each cheap to try — one manifest, one boot — so building an instrument
    never won against trying the next candidate, and the guesses ended up costing far more than the
    tool did. `--poison-index` took one commit and should have existed at A.14 (A.21).
11. **Never ask an operator to notice an absence.** "Did the new art appear?" failed four times;
    imported art can resemble stock closely, and a move lasting under a second cannot be judged from
    a still. Ask a question with a positive, unmistakable answer — "did DK explode into static?" —
    and always pair a negative result with a positive control on a *confirmed* range (A.21).

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
--coords <lo>..<hi>                  placement bbox vs opaque bbox per slot, and their slack
```

**The mapping oracles** (write output ROMs, but only garbage — never use one as a base for real work):

```
--poison-index <lo>..<hi> --out <rom.sfc>
--paint <lo>..<hi>:<colour> [--paint ...] --out <rom.sfc>
```

`--paint` is the one to reach for. It fills each range with a *constant* palette index, so DK
renders as a flat silhouette whose colour names the range — and **leaving confirmed ranges unpainted
keeps idle/walk/run normal**, so the game stays playable and only unmapped actions light up. Test
many actions in one boot; each is a separate data point.

**On DK, use white (15) and nothing else.** One range painted white, the rest left alone, one
trustworthy bit per boot. Every other marker tried has failed in play: near-black (1) vanishes on
dark backgrounds; dark brown (3), amber (7) and red (8) all read as ordinary DK. The rule is
**contrast with the character**, not brightness — DK is warm brown-and-orange, and partial painting
means a warm marker only ever covers *part* of a warm character. Three wasted rounds are recorded in
A.22; don't buy a fourth by trying to fit more buckets into a boot.

**A painted DK is usually a *mixture*, not a clean silhouette** — a mid-animation Kong composites
sprites from several ranges, so expect part of him painted and part normal. Judge by "is any of him
the marker colour", not "is he entirely the marker colour".

Overwrites a range's char data with noise; headers, placements and pointers untouched, so every
animation plays as before and only the pixels turn to static. Boot it and whichever action turns DK
into garbage is drawn from that range. **Ask "did DK explode?", never "did the art change?"** — the
second question is one operators cannot reliably answer and it has produced four wrong conclusions
(A.21). Bisects: poison wide, halve on a hit. Always pair with a positive control on a *confirmed*
range (`port/dk-poison-run.sfc` poisons Run) before trusting a negative.

`--baseline` measures a *run*; `--coords` explains a *slot*. When a placement calculation looks
right but lands wrong by a small constant, `--coords` is the one that finds it (A.19).

Writing: `--import`, `--batch` (both take `--dry-run`; `--batch` also `--no-align-strip` and
`--flat-x`), `--expand`, `--revert`.

`--flat-x` anchors a run to one horizontal centre instead of matching each replaced frame's. Use it
when the imported art does not lunge and the replaced animation does (A.20) — confirmed in play on
the DK run. It **cannot** become the default: it breaks V4d's identity by construction, so it is a
per-run judgement call. **Check it per run** — the walk wants it off, the run wants it on, and
`--baseline`'s centre-X column on the *stock* range tells you which before you import.
`--revert` undoes imports **and** expansion, byte-exactly, across a chain of runs (V4g).

Test ROMs in `port/` (gitignored): `dk-walk-test.sfc` (walk art in idle slots),
`dk-move-test.sfc` (walk art in walk slots), `dk-move-anchored.sfc` (`--anchor-bottom`, failed),
`dk-move-aligned.sfc` (vertical-only), `dk-move-aligned2.sfc` (both axes — confirmed in play),
**`dk-move-reconciled.sfc` (A.19, current default — measured best, not yet booted)**.

---

## What M5 probably is

Two candidates, both surfaced by M4 rather than planned:

1. **Animation-script editing.** 8 of 46 DK strips have pose counts no DK animation has, and the
   author changed frame counts deliberately. Without script editing, a faithful full-character
   import is impossible for those runs — you can only leave stale frames (A.9).
2. ~~**Strip-level pose placement.**~~ Done — implemented, confirmed in play, and as of A.19 the
   default, with the two coordinate systems reconciled. M5 is (1).

---

## Key numbers

- DK owns `0x8C..0x950`, 562 indices; Diddy begins `0x954` (A.8).
- `0x858..0x8A8` inside that range is **not DK** — most likely Manky Kong. **Exclude from any DK
  manifest** (A.13). Reference at `port/DkcTool/testdata/manky-reference.png`.
- DK sheet: 533 poses, 46 strips, **29 authorable captioned runs** (a strip is not an animation).
- Capacity: DK alone is 487 KB against a 92 KB stock pool → `--expand` is mandatory for a full
  import, but a single ~20-pose run fits stock with room to spare.
