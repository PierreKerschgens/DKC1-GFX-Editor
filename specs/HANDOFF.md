# Handoff — sprite-importer-port

State of play for whoever picks this up next. Written 2026-07-25, updated 2026-07-26.
The durable record is in the numbered specs; this file is the map and the open edges.

---

## Where things stand

| milestone | state |
|---|---|
| M0 core port, M1 encoder, M2a tiler, M2b writer, M2c free space | done, gated |
| M3 ROM expansion (ExHiROM) | done, `--verify-m3 --all-cores` 6/6. ⚠️ its CLI default shipped broken — A.30 |
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
| 0 "Idle" | `0x8C..0xDC` | anim 4/108. 11 poses over 21 frames, repeated (A.27) |
| 6 "Walk" | `0xE0..0x12C` | anim 3 |
| 7 "Run" | `0x330..0x37C` | anims 2/14/20, needs `--flat-x` (A.20) |
| 10 "Jump" | `0x130..0x17C` | anims 5/7/21/81/8/102/82 — **a shared arc** (A.23) |
| — (enemy bounce) | `0x478..0x4B4` | anims 23/101 (A.22) — no sheet strip claimed yet |
| 8 "Roll" | `0x4B8..0x4EC` | anim 24 draws **11** of these; poses 0..10 (A.26). `--flat-x` |

**Imported so far (7 runs, 120 poses, `port/dk-combined-turn.json`).** Everything else is stock, so
an operator will keep reporting "X is old DK" — that is coverage, not a defect, and the list below is
what to check a report against before investigating it:

| run | indices | animation |
|---|---|---|
| Idle | `0x8C..0xDC` | 4/108 |
| Turn | `0x180..0x184` | 6 |
| Walk | `0xE0..0x12C` | 3 |
| Jump | `0x130..0x17C` | 5/7/21/81/8/102/82 |
| Bang Chest | `0x200..0x25C` | 9/113 |
| Run | `0x330..0x37C` | 2/14/20 |
| Roll | `0x4B8..0x4EC` | 24 |

**Known un-imported and already reported:** enemy bounce `0x478..0x4B4` (anims 23/101), barrel-blast
roll `0x4F0..0x528` (anims 15/96), duck, crawl, swim, ledge, rope, minecart, victory, death.

**Provisional** (identified from draw-order montages, *never observed in motion*): Swim →
`0x3A4..0x3DC`, Death → anim 16. Treat as unverified — the tier is **0 for 5** whenever tested.
~~Ground Slap → `0x2E4..0x32C`~~ is **falsified**: that range is **Barrel Throw** (A.33), confirmed
in play, and Ground Slap was reported unchanged in the same boot.

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

**`0x478..0x4B4` (anims 23/101) is the ENEMY BOUNCE**, confirmed in play — imported art appears when
jumping on an enemy. It is *not* the roll; that prediction was falsified by the same boot.

**Roll = anim 24, `0x4B8..0x4EC`** — **confirmed in play**. Found by poison bisection: ~430 → 96 →
64 → 32 → 16 → 14, every step a boot.

⚠️ **"14 contiguous indices, exactly the sheet's 14 Roll poses" was wrong** (A.26). The *range* holds
14, but **anim 24 draws only 11 of them**, skipping `0x4BC`, `0x4E4`, `0x4E8`. So three imported roll
poses are never displayed and the rest sit at a different offset in the cycle than the sheet drew
them. **Fixed** — poses 0..10 now map onto anim 24's real draw order, and `0x4BC`/`0x4E4`/`0x4E8`
are written by nothing (no animation in the table draws them). The coincidence of 14 and 14 was the
frame-count trap again, arriving through a *range* rather than a count and getting past a reader who
knew to distrust counts.

**Roll wants `--flat-x`.** Stock's roll centre-X jitters **15 px** — worse than the run's 9 px that
visibly swayed — and the default import inherits it frame-for-frame; `--flat-x` takes it to 0 px.
Use it for this strip.

The evidence is **strong on measurement, weak in play**: the operator reports the flat-x build "a
bit smoother" but notes the roll is quick and hard to follow. Unlike Run — where the defect was
plainly visible before and plainly gone after — this one is at the edge of what play can judge, so
the measurement is doing the work and the boot only fails to contradict it. Recorded at that
strength deliberately (cf. gotcha 8).

**Cliff teeter** is confirmed in two phases by two instruments: start (stretched eyes)
`0x3E0..0x3FC`, loop `0x400..0x47C`.

**Roll was `0x400..0x4FC`** — 64 indices (A.22). Poison over `0x380..0x4FC` garbles during a roll;
three inverted "hole" tests clear `0x380..0x3FC`; the remainder is the answer. Every observation in
the search now agrees.

⚠️ **Do not trust a white paint sighting on DK.** Round 2 put roll in `0x380..0x3FC` on a white
sighting; that was a **false positive** — roll actually sits in the part round 2 painted *red*, red
was unreadable, so DK looked normal and his naturally near-white muzzle and hands were read as the
marker. White collides with DK's own palette in *both* directions. `0x3E0..0x3FC`
is the **cliff-teeter start** (its opening frames are DK's eyes stretching downward as he looks over
the edge). `0x3A4..0x3DC` was the provisional *Swim* and overlaps roll's range, so that pairing is
at best partly wrong — the provisional tier is 0 for 4 whenever tested.

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

**Jump = `0x130..0x17C`, confirmed in play** (A.23). The complement-paint round settled it in one
boot: DK went brown when jumping, jumping with a barrel, and falling off a ledge, and stayed white
through walk, run, duck, roll, enemy bounce and barrel blast. The A.20 "climb" reading is dead.

⚠️ **That range is a shared *pose arc*, not an animation** — eight animations traverse the same 20
sprites at different entry points, two of them in reverse (anims 5/7/21/81/8/102/82, plus 104 which
also reaches into Walk). Importing 20 poses there retargets all eight at once. **The unit of import
is the arc, not the animation** — run `--anims-in` on a range before assuming it belongs to one move.

**Open, do not treat as settled:**
- `0x680..0x7FC` paints the **life/balloon HUD**, so it is not purely DK. A.8's ownership claim has
  another shared island in it besides `0x858..0x8A8`.
- **A contiguous index range is not an animation** — now with direct in-play evidence, not just
  draw-order inference: the enemy bounce runs red→white→red across two ranges (A.22).
`0x130..0x17C` is a climb/hang, not the Run — its height swells 40→72→36, a one-shot, and DK is
upright with both arms overhead.

**Desk mapping now works — 3 for 3 on its first outing** (A.33). Strip 9 "Flip" → the enemy bounce,
and both Swap runs, all confirmed in play from one boot, with no paint round and no bisection. What
changed is the *inputs*, not the method: pose counts are correct since A.25, the captions give
identity and structure (A.29), `--anims-in` gives the real draw order rather than a range, and the
length check refuses a wrong pairing before it ships (A.26). Match on **distinct-index count against
draw order**, in a chunk the paint survey supports, and confirm with one boot per batch.

**Frame-count matching: see gotcha 3 — the "0 for 3" verdict is withdrawn** (A.24), though it is
still only a filter. `--baseline`'s *height profile* across a range remains the cheap discriminator:
a loop holds height roughly constant, a one-shot swells and recovers. (It was used to refute a
`0x130..0x17C` reading from the ROM side alone — that range is now confirmed Jump, and the swell it
measured was the jump arc.)

**Next test:** the remaining unmapped actions — turn, landing, deceleration, and the rope/ledge group.
Use the complement paint from A.23; it is now 1 for 1 and is the only instrument that has produced a
positive without a bisection.

**Known unreachable:** *Minecart ×2* and *Steel Keg Ride* cannot be matched through the animation
table — no script draws both DK and a vehicle (A.15). Their DK poses are composited by game code.
Look in `0x538..0x594`, which is DK's mounted/riding pose set.

**Rope is mapped and confirmed in play** (A.33): idle `0x714..0x720` (anim 94), turn `0x724..0x728`
(anim 95), climb `0x72C..0x740` (anims 92/93) — three contiguous animations, sheet poses 4/2/6
exactly, all three loop-and-reverse just as the sheet's annotation says. Ledge and swing are still
open.

⚠️ **A run not standing on the floor needs `"groundRef": "self"`** — rope, swim, ledge, every mount.
The rope strips got it and Steel Keg Ride did not, in the same commit, which put DK 9 px low and
11 px right of the keg (A.33). Check it whenever a strip's feet are not on the ground.

⚠️ **Render the ROM before theorising about what stock looks like.** `--contact-range <lo>..<hi>`
answers it in one command. Two successive wrong conclusions about the barrel throw came from
reasoning about screenshots — one of them the sprite author's own mockup, mistaken for the game —
while the primary source sat locally unrendered (A.33).

⚠️ **Barrel Idle is NOT in `0x8C..0x32C`, `0x538..0x5A4` or `0x680..0x7FC`** — all three tested white
by the operator. Remaining: `0x330..0x534`, `0x5A8..0x67C`, `0x800..0x950`. A candidate at
`0x280..0x288` was proposed on contiguity and **falsified**; it sat inside a range the operator had
already reported white, so the operator's own data refuted it before it was built. **Check a
candidate against every prior paint result before spending a boot on it.**

**The barrel sequence is contiguous** (A.36): pick up `0x28C..0x2A4` (7), **carry walk
`0x2A8..0x2E0` (15)**, throw `0x2E4..0x32C` (19) — matching sheet strip 15's sectors 7 / 15 and strip
16's 19 exactly. Found by *rendering the range* after paint localised the carry; three structural
guesses missed it first (anims 71/75, anims 15/96, the mount set `0x538..0x5A4`). The mount set is
the **keg ride**, confirmed brown by the same paint round.

⚠️ **Prop/mount animations are the ones `--anims-in` EXCLUDES** (A.35). It lists animations drawing
entirely inside a range and prints the rest as "N more touch this range but also draw outside it
(held props / mounts)" — that header is the prop class, 14 animations, and every mapping in this file
was made from the included list. Their draw orders interleave DK and prop frames
(`0x2710 0x58C 0x2714 0x590 …`), so **DK's carry poses are `0x538` + `0x58C..0x5A4`, ~8 indices
shared across every carryable object**. Use explicit `indices` for these — `animation` would derive
the prop's slots too.

⚠️ **A slicer strip can hold several captioned sectors** (A.34) — strip 15 is Barrel Pick Up (0..6) |
Barrel Idle (7..9) | Barrel Walk (10..24), split by drawn rules. `--captions` shows only the *first*
caption per strip, so the A.33 strip→caption table is incomplete; strips 14 and 18 are known to be
multi-sector too. **Check for rules before mapping a strip as one run**, and use `"poses": "lo..hi"`
to address a sector.

⚠️ **`0x4F0..0x528` is Barrel Walk** (anims 15/96) — the pair A.28 could not place. It came back
white in the barrel-blast paint test because it is the barrel *carry*, not the blast.

⚠️ **Prop and mount runs are excluded from the manifest** (A.33) — `port/dk-combined-noprops.json`
is the good build, 14 runs / 172 poses. Barrel Throw and Steel Keg Ride both map correctly and both
look wrong, because the sheet draws its own composition (hip throw, standing balance) while the game
composites the prop against stock's. The keg's **centroid is within 2 px of stock** and it still
reads wrong, so geometry is not the lever. `offsetY` exists now and is deliberately unused: nudging a
correctly-placed character to flatter a prop trades measured correctness for eyeballed.

⚠️ **Decide `flatX` on stock's CENTROID spread, not its centre-X** (A.37). Centre-X is a bounding
box and a held prop inflates it, so a run whose body never moves can still show 6 px of box travel.
The barrel carry (stock centroid spread **1 px**) and keg ride (**3 px**) both had `flatX` off, so
per-pose centring injected that box variation as real body wobble — 6 px into the carry, a 17 px head
swing into the ride. With `flatX` on, both bodies match stock. The earlier blanket rule "never flatX
a run that holds something" was drawn from the *throw*, where the body genuinely lunges 29 px; it is
the centroid that tells the two cases apart.

⚠️ **`HEAD X` is unreliable for arms-out poses.** It averages opaque pixels in the top third, so wide
arms at shoulder height are counted as head. The keg ride reads 16 px of "head" swing that is mostly
the sheet's outstretched arms.

⚠️ **Never `flatX` a run that holds something** (A.33) — superseded by the centroid rule above. The game composites a prop against DK's
position, so the replaced animation's horizontal travel is load-bearing. Stock's barrel throw lunges
**29 px** — the largest in the game — and flattening it left the barrel swinging away from a
stationary DK. Both prop failures this batch (barrel, keg) looked structural and were both knob
errors: `flatX` on the throw, missing `groundRef: "self"` on the ride.

**Where I was blind:** rope / ledge / swing on the **ROM** side. The *sheet* side is settled — strips
22/23/24 are captioned "Rope Idle", "Rope Climb", "Rope Turn" (A.28). Matching them to index ranges
still needs a paint round or someone who knows the game.

**The secondary captions are read — A.29 has the full sweep and the vocabulary.** The sheet
documents its own timing: **(Loop and Reverse)**, **(Reverse to return to idle)**, **(Loop)**,
**(Hold)**, **(Also used in Bonus Games)**, plus vertical rules marking segment boundaries *inside*
a run. Read A.29 before mapping another animation by hand — it is cheaper than a boot and it is
information no instrument in this repo can recover.

⚠️ **They are black-on-transparent, so anything that flattens alpha to black hides them** — `sips -z`
and `--captions` both do. Use `port/flatten-region.py`, which composites over white. This is why six
sections reconstructed by boot what the artist had written down.

⚠️ **Long runs wrap onto the next row** — Swing, Victory, Intro Cutscene, End Credits, Map Stuff, and
Bang Chest's loop. That answers the band question A.25 left open: the two-row strips are **one run
wrapping**, not two animations stacked, so the slicer fix is to *join* them in reading order, not
split them. It also means the chest-beat is strips 3 **and** 5.

---

## Hard-won gotchas (each cost a wrong conclusion)

1. **Contact sheets show what a sprite *looks like*, never what the game *uses it for*.** One boot
   overturned the anchor pairing six passes of static analysis had treated as settled (A.16).
2. **Verify against draw order, never an index range.** An animation's frames are not its contiguous
   index range; rendering the range nearly cost a correct match (A.14).
3. ~~**Frame-count matching does not work**~~ — **void, and the way it broke is the lesson** (A.24).
   Both of A.9's two rows were defective: one judged against a pairing A.16 later falsified, the
   other against a pose count the slicer got wrong. In *both*, the correct animation was inside the
   count-matched set. A negative result assembled from two cases is one bad input away from being
   backwards — and it stood for six sections while both of its inputs were separately overturned.
   What is true is weaker: count matching is a **filter, not an identification** (a 20-pose strip
   matches 5 animations, an 8-pose strip 52, only 21 of 51 counts unique). Confirmation still needs
   a boot — gotcha 1 is untouched.
4. **Palette identity needs all candidates on screen at once** (`--palette-sweep`), not a hand-picked
   few. Judging "coherent" in isolation produced two wrong identifications of the same block (A.13).
5. **`--zoom` is capped by `--cell`** (`scale = min(zoom, cell/w, cell/h)`). Raising zoom alone does
   nothing. A block read as "not DK" purely because it was rendered too small.
6. **`--emu-golden` overwrites the stored golden.** Never run it on a modified ROM — it would
   silently rebase every future gate on a hacked image. Use `--emu-diff`.
7. **DK knuckle-walks.** Upright = idle, on all fours = locomotion. Getting this backwards
   invalidated two "confirmed" pairings at once.
8b. **A fast animation is at the edge of what play can judge.** The roll lasts under a second, and
    the operator could only say the corrected build looked "a bit smoother". For short animations,
    weight `--baseline` over the boot and do not upgrade a hedged report into a confirmation — the
    hedge is the finding (A.22).
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
14. **A range's size is a frame count in disguise, and `indices` disables the check that catches
    it.** The roll's 14-index range and the sheet's 14 Roll poses agreed, so a manifest written with
    explicit `indices` zipped them 1:1 and the length check passed vacuously — while anim 24 draws
    only 11 of those indices (A.26). Written as `"animation": 24` it would have derived 11 and
    refused. **Prefer `animation` over `indices` wherever the animation is known**; an `indices`
    list is an assertion no tool is checking. `--batch` now prints STALE FRAMES for animations that
    straddle imported and stock art, on `--dry-run` too. A strip that covers *more* than its
    animation uses `"poses": "lo..hi"` to select a sub-run, which keeps `animation` and its length
    check. ⚠️ `animation` is **hex**: "anim 24" is `"0x18"`. Bare values now refuse.
13. **When you flag an assumption as needing a check, check the *inputs* too.** A.23 correctly
    flagged one assumption in the drop-`0x130` escape and named it. The escape died anyway — from
    the pose count underneath it, which nobody had thought to doubt because it came from a tool
    (A.24). A.9 had even pre-emptively defended that number ("not a slicing artefact — the band
    genuinely ends after 20 poses, checked at the right edge"): the ends *were* right, and the
    merge was in the middle, where the check didn't look. **A defence that checks the edges does
    not cover the interior.**
12. **A range is not one animation, and it may not even be one *ordering*.** Jump's 20 sprites are a
    single crouch→extend→tuck arc that eight animations traverse from different entry points, two of
    them **backwards** (A.23). "Contiguous range = animation" was already known false; "range =
    animation *set*, played forward" is false too. `--anims-in <range> --frames N` prints the actual
    draw order per animation and takes seconds — run it before writing a manifest entry.
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

**Paint the *complement*, not the candidate.** The single most important lesson of A.22. Paint DK's
whole block white *except* the range under test, and ask **"is any part of him still brown?"** —
a brown patch on a uniformly white DK. This keeps the marker on 100 % of the character at every
scale, which is the property that made the early coarse rounds work and every fine round fail. It is
also self-validating: in any animation not using the candidate, DK must be *entirely* white, so a
broken premise shows up immediately instead of as a false negative.

```
--paint 0x8C..<lo-4>:15 --paint <hi+4>..0x854:15 --paint 0x8AC..0x950:15 --out hole.sfc
```

(Skip `0x858..0x8A8` — not DK, A.13 — so a white Manky can't be mistaken for signal.)

Everything below is the *old* framing, kept because the failure modes are real:

**Paint to localise coarsely; `--poison-index` to confirm finely.** That division is the hard-won
part (A.22):

- **Paint's only advantage is labelling many buckets at once**, and it works while the painted
  fraction of the character is *large*. On DK use white (15) and nothing else — near-black (1)
  vanishes on dark backgrounds, and dark brown (3), amber (7) and red (8) all read as ordinary DK.
- **Paint stops working on small ranges.** Every solid fill is one of DK's own colours, because the
  palette is DK's; white hides in his pale muzzle/hands/chest once only a few sprites are painted.
  Four consecutive "negatives" that provably partitioned a known-positive range were all this.
- **Poison has no such failure mode.** Noise is a *texture*, not a colour, and nothing on a Kong
  looks like high-frequency static. Slower (one bucket per boot) but it does not lie.

**A painted or poisoned DK is usually a *mixture*** — a mid-animation Kong composites sprites from
several ranges. Judge by "is any of him marked", not "is all of him marked".

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

⚠️ **The low-bank mirror is a *snapshot*, and the GFX pointer table lives inside it.** An import
repoints the real table at 0x3BCC9C and leaves a stale copy at 0x7BCC9C, so art read through bank
$3B stays stock while art read through $BB updates — DK's roll kept its stock model through three
imports for exactly this reason. `Rom.RefreshLowBankMirror()` re-syncs before every save; `--refs
<lo>..<hi>` is the instrument that finds a stale duplicate.

⚠️ **`--expand` must mirror the low bank or the ROM does not boot** — it is the default now, but it
shipped opt-in, so the first real expansion black-screened everywhere while both verify suites stayed
green (A.30). `--verify-m3`'s X1 gate *asserts* the un-mirrored form is dead; the CLI selected it
anyway. **Nothing yet boots the output of `--expand`'s default path** — check by hand with
`--emu-boot` and look at the frame.

⚠️ **Expanding invalidates emulator save states** (size + map mode change); `.srm` survives. Tell the
operator to delete the save-state dir after the first expanded build, or they will report "the ROM
does not load" for a second, unrelated reason.

`--flat-x` anchors a run to one horizontal centre instead of matching each replaced frame's. Use it
when the imported art does not lunge and the replaced animation does (A.20) — confirmed in play on
the DK run. It **cannot** become the default: it breaks V4d's identity by construction, so it is a
per-run judgement call. **Check it per run** — the walk wants it off, the run wants it on, and
`--baseline`'s centre-X column on the *stock* range tells you which before you import.
`--revert` undoes imports **and** expansion, byte-exactly, across a chain of runs (V4g).

⚠️ **`--flat-x` is per *strip*, not per run.** Set `"flatX": true|false` on a manifest strip entry;
the command-line flag is only the default for strips that don't say. Two animations in one batch can
want opposite answers — DK's walk matches stock exactly *with* per-pose centring (4 px of real
inherited travel) while his run and roll inherit 9 px and 15 px of the replaced animation's lunge
and need it flattened. A single global flag silently regresses whichever strip disagrees.

⚠️ **`--batch` chains onto an existing ledger sidecar.** Re-running with the same `--out` does *not*
start fresh: the allocator reads `<out>.dkctool.json` and treats its allocations as already spent,
so a rebuild silently imports fewer poses (54/54 became 37/54 this way). Delete the output and its
sidecar, or use a new name. `--dry-run` does not show this, because it never consults the ledger.

⚠️ **Name test ROMs so they cannot be confused at a glance.** `dk-run-flatx.sfc` and
`dk-roll-flatx.sfc` differ by one letter and hold different animations; a boot of the wrong one
produced a confusing report that took a ledger dump and a contact render to unpick. Prefer distinct
words (`dk-TUMBLE-flatx.sfc`) over one-letter variants — the operator reads these under a file
picker, not in a diff.

**Boot through `./port/boot.sh <rom.sfc>`, never by opening the built ROM directly.** The emulator
keys its `.srm` and save states off the *filename*, so every new descriptive name costs a fresh
intro, a save-file selection and a walk back to the test spot. `boot.sh` copies the build to one
fixed `port/dk-BOOT.sfc`, which keeps the save game and last state across every experiment — open
that one in the emulator, always.

Two things it deliberately does *not* do, both for reasons already paid for above:
- It **copies**; the tool still writes the descriptive name. A fixed `--out` would chain the ledger
  sidecar and silently spend the same allocations twice (the 54/54 → 37/54 trap).
- It stamps `port/dk-BOOT.what` with the source name, timestamp and hash. A fixed name is
  unidentifiable at a glance, which is the exact hazard distinct names were adopted to fix — so run
  `./port/boot.sh` with no arguments to print what is currently staged, and do that before trusting
  any surprising report.

Test ROMs in `port/` (gitignored): `dk-walk-test.sfc` (walk art in idle slots),
`dk-move-test.sfc` (walk art in walk slots), `dk-move-anchored.sfc` (`--anchor-bottom`, failed),
`dk-move-aligned.sfc` (vertical-only), `dk-move-aligned2.sfc` (both axes — confirmed in play),
**`dk-move-reconciled.sfc` (A.19, current default — measured best, not yet booted)**.

---

## Open: cross-strip ground alignment (found by the first multi-animation build)

`port/dk-COMBINED.sfc` (Walk+Run+Roll, 54/54, confirmed in play) **bobs on the run→walk
transition**. Measured foot lines: walk `127..130`, run `124..128` — the walk sits 2–3 px lower, so
DK's feet step up as he accelerates.

Stock has the same offset (walk 128, run ~126) but hides it: stock's run swings across **8 px** and
reaches 130, overlapping the walk, so the step falls inside the gallop's own motion. The imported
run is tighter (4 px) and sits consistently high, which exposes it.

**Cause:** `--align-strip` anchors each strip to *its own* reference slot — walk to `0xE0`, run to
`0x330` — and nothing makes two strips agree on a ground line. This is structurally the same mistake
as A.17 (anchoring at the wrong *level*), one level up again: A.17 moved from per-pose to per-strip,
and this needs per-*manifest*.

**This class of defect is invisible when importing one animation at a time.** It only exists
*between* strips, so it could not have appeared before the first combined build — which is an
argument for building combined ROMs early rather than as a victory lap.

**Fixed** by a manifest-level `"groundRef": "0xE0"` — one slot whose opaque bottom every strip lands
on, instead of each strip anchoring to its own first slot. A strip opts out with its own
`"groundRef": "self"`, for runs legitimately off the floor (swim, rope).

| | before | after |
|---|---|---|
| walk | 127..130 | 127..130 |
| run | 124..128 | **126..130** |
| roll | 131..134 | **126..129** |

The run was 2–3 px high and the roll 4 px low; both now sit on the walk's ground line. V4 7/7.
**Not yet re-booted** — the measurement says the step is gone, play has not confirmed it.

### Residual "jitter" after groundRef — probably the 26 *unimported* animations

Transition confirmed smooth in play after `groundRef`, but the operator still reports "something's
off". Placement is unlikely to be the cause: walk/run/roll now bob **3/4/3 px** against stock's
**2/8/7**, i.e. every imported cycle is *steadier* than the one it replaced.

The likelier cause is that **only 3 of ~29 DK animations are imported**. Idle, turn, landing,
deceleration and everything else still draw stock art, so the *character design itself* changes as
DK switches animation — which reads as jitter even with perfect placement. This is a coverage
problem, not a placement problem, and no amount of `--baseline` work will fix it.

**`port/dk-GAPS.sfc` diagnoses it**: `dk-COMBINED.sfc` with every DK range poisoned *except* the
three imported ones, so anything that garbles in play is an animation still running stock art.
Chained across five `--poison-index` runs (487 sprites). Whatever garbles during a run→walk
transition is the next thing worth importing.

**Independently confirmed:** the operator reported the transition "off horizontally" from play at
the same time the 2 px centre-line step was found in the measurements, neither informing the other.

**Vocabulary note, because it cost a wrong first look:** the report was *"bobbing (or jitter?)"* and
the hedge was carrying the answer — **bob = vertical, jitter = horizontal**. Anchoring on the first
word sent the search up the wrong axis. When an operator offers two words for a symptom, treat both
as live rather than picking one.

### The last residual: ~1 px of head height, and it is the artwork

Operator: "check if DK's eyes are lower when walking than running". They are, by ~1 px:

| | walk top | run top |
|---|---|---|
| dk-FLATALL | 78..82 | 77..81 |

The sheet's walk poses are 47–52 px tall and its run poses 48–53, so with the feet on a shared
ground line the heads land ~1 px apart. **When two cycles are drawn at different heights you can
align the feet or the heads, not both** — feet is right for a character on a floor, and the residual
goes to the head. Closing it means editing the artwork, not the importer.

Note also that the imported DK's head sits ~10 px above stock's (top ~80 vs ~90) because the sheet
draws him upright where stock knuckle-walks. Expected, and not a placement error — a reminder that
`--baseline`'s *top* edge is not comparable across art styles the way the foot line is.

**Measure the top edge too.** Everything in this spec compares foot lines and centre-X; the head was
never checked until an operator noticed a 1 px step in play that no existing measurement reported.

### The jitter was real, and it was the HEAD — measure that, not the box or the centroid

Operator, after every geometric measure had come back clean: *"FLATALL jitters, I'm 100 % sure. When
running to the right and then walking, DK's head is suddenly a bit left."* Correct, and directional:

| | walk head | run head | run→walk |
|---|---|---|---|
| stock | 133.6 | 129.4 | **+4.2 (forward)** |
| dk-FLATALL, before | 130.8 | 133.4 | **−2.6 (backward)** |
| dk-FLATALL, after `offsetX: -7` | 130.8 | 126.4 | **+4.4 (forward)** |

**Stock's head continues forward into the walk; the import snapped it backward.** That is why it read
as wrong rather than merely different.

**Neither the bbox nor the centroid could see it.** Both average in the run's outstretched arm, so
the run held a *steady box* and a *steady centroid* while its head sat several px back. Three
horizontal measures, and the first two were blind to the only one a player watches. `--baseline` now
reports **HEAD X** (mean X of opaque pixels in the top third of the box).

Fixed with a per-strip `"offsetX": -7` on the run. Sizing it is direct: read HEAD X for both strips,
compare the step against stock's, nudge by the difference.

**Confirmed in play** — "way better". The head-continuity fix is the one that closed the transition,
after ground line, centre line, centroid and coverage had each been tried and each left it intact.

**The lesson, and it is the session's sharpest.** Every earlier instrument measured what was easy to
compute — box edges, then centre of mass. The operator was tracking a *feature*, and was right three
times while the measurements said "clean". **When a report keeps contradicting the numbers, the
numbers are probably measuring the wrong thing.** Ask what the observer is actually looking at, and
measure that.

### Superseded: the artwork-discontinuity theory

`dk-GAPS.sfc` booted without obvious garbage at the transition, which weakens the coverage
hypothesis for *that moment* specifically. The operator's own reading is more likely: **the arm
jumps between the run's last frame and the walk's first**, because the sheet's strips were drawn as
separate cycles rather than as cycles that hand off to each other. Stock DKC1 has no such seam --
one artist drew the whole set to connect.

**No placement or alignment work fixes this.** The remedies are art-side: redraw the boundary frames
so the cycles meet, or accept the seam. Worth stating plainly, because every instrument in this spec
measures geometry and would keep reporting the transition as clean.

**Implemented and run — and it clears geometry.** `--baseline` now reports `CENTROID X/Y` (spread
and mean) beside the bbox numbers. Comparing the walk→run body-mass step:

| | walk centroid | run centroid | step |
|---|---|---|---|
| stock | 128.9, 108.5 | 126.4, 105.2 | **2.5 X / 3.3 Y** |
| dk-FLATALL | 129.4, 105.3 | 130.4, 103.5 | **1.0 X / 1.8 Y** |

The imported transition moves DK's body **about half as much as stock's does**, on both axes. Stock
reads as smooth, so a smaller step cannot be what makes the import read as harsh. **Geometry is
exonerated**, and the artwork-seam explanation above is the one left standing.

**The measurement, for reuse:** opaque-pixel **centroid**, not bbox.
Every number in this spec comes from a bounding box, which is driven by whatever limb sticks out
furthest — the run's bbox is 44 px wide because of a reaching arm, not because the body moved. A
centroid tracks body mass, which is what a viewer's eye follows. (The operator proposed DK's eye,
then his ear, as landmarks — right instinct; the centroid is the robust form, since the dark palette
indices used for eyes also appear in outlines, and any facial landmark vanishes when he tumbles.)

## Second roll: the barrel-blast

The roll DK does when **blasted out of the house** is a *different animation* from the ground roll.
Same pattern as the two idles: **one player action, two animations.** Assume it for every move until
shown otherwise — that part still holds.

⚠️ **Its range is NOT known.** The old claim ("draws from `0x480..0x4B4` / `0x4F0..0x4FC`, garbled in
`dk-poison-d` and `dk-poison-e`") is **falsified**: a complement paint with the hole at
`0x4F0..0x528` left DK entirely white through the blast, with walk/run white as the control. So the
blast does not draw from `0x4F0..0x528` at all, and anims 15/96 — 15 sequential indices, a pair, the
shape a real move has — are **something else, still unidentified**.

DK stayed white, so the blast *is* inside his block; it is somewhere in the painted remainder. That
is ~440 indices and a complement paint tests one bucket per boot, so **do not bisect for it** — it
would cost ~9 boots for one animation. It will resolve for free as coverage grows. The poison-based
localisation that produced the old claim is now 0 for 2 on this animation, which is consistent with
this file's own warning that poison rounds `d`/`e` were read as ambiguous at the time.

## What M5 probably is

Two candidates, both surfaced by M4 rather than planned:

0. ~~**Fix the slicer's pose merging.**~~ **Done** (A.25) — `FragmentRatio = 0.5` makes absorption
   asymmetric, so a fragment still joins its figure but two pose-sized components never merge. DK
   533 → **678** poses, 46 → 51 strips; DK Jr 542 → **553**. Over-budget 16 → 0, over-canvas 5 → 0,
   V4b 1041/1056 → **1216/1216**. Golden rebuilt, **V4 7/7**, and the confirmed Walk+Run+Roll build
   is byte-for-byte identical.

1. **Animation-script editing — M5. Smaller than it looked: the idle did not need it** (A.27).
   A count mismatch only forces a script rewrite when the animation's extra frames are a *distinct
   sub-motion*. When they are the same motion played slower — the idle's 21 frames against the
   sheet's 11 poses — repeating each pose covers them, preserves the ROM's timing exactly and
   rewrites nothing (`"poses": "0,0,1,1,..."`). **Check that before counting a strip as an M5
   case**; the 9-of-51 figure below is an upper bound, not a work list.

   Where the extra frames *are* a real sub-motion, repeating would smear one pose across a movement
   the artist drew separately, and M5 is genuinely required — the roll's recovery (A.26) is that
   shape. **Recomputed against
   the corrected counts and it survives**: 8 of 46 strips before, **9 of 51 after** (A.25). The
   membership churned — Jump left the list, which is what started this — but the class is the same
   size. (The A.24 guess that it might shrink or vanish was wrong; recorded there so it isn't
   re-made.)

   **Start from the idle, not from the survey.** A.26 traces an artifact an operator actually saw —
   stock DK for one frame at the end of the roll — to anim 25 settling on `0xB4`, and the clean fix
   is the idle run: **21 indices against sheet strip 0's 11 poses.** That is one specific animation,
   one specific missing capability, and a visible defect to verify the fix against, which is a much
   better place to start than a list of nine strips.

2. ~~**The roll is mis-mapped.**~~ **Fixed** (A.26). No art call was needed: the sheet's last three
   Roll poses are DK *standing up*, which is anim 25's job, not anim 24's. Poses 0..10 are the
   tumble. `0x4BC`/`0x4E4`/`0x4E8` are drawn by no animation at all — dead slots, no longer written.
   The build is 74 → 71 poses. **Not yet booted.**

3. **Band membership in the slicer** (A.25) — 6 of 51 DK strips, 1 of 42 DK Jr, all bands 19–20,
   none below strip 25, none used by a manifest. Their rects are right; the grouping is not.
   Renumbers strips ≥25 when it lands.
2. ~~**Strip-level pose placement.**~~ Done — implemented, confirmed in play, and as of A.19 the
   default, with the two coordinate systems reconciled. M5 is (1).

---

## Key numbers

- DK owns `0x8C..0x950`, 562 indices; Diddy begins `0x954` (A.8).
- `0x858..0x8A8` inside that range is **not DK** — most likely Manky Kong. **Exclude from any DK
  manifest** (A.13). Reference at `port/DkcTool/testdata/manky-reference.png`.
- DK sheet: **678 poses, 51 strips**, **29 authorable captioned runs** (a strip is not an animation).
  DK Jr: 553 poses, 42 strips. These are the post-A.25 counts; anything citing **533 / 46 strips**
  predates the merge fix and is wrong. Strips **0–25 kept their numbers** across that fix, strips
  ≥26 did not.
- ⚠️ **Strips ≥25 still group two rows into one strip** on both sheets (A.25) — 6 DK strips, 1 DK Jr.
  Their pose rects are right; the strip grouping and pose order are not. Fixing it renumbers ≥25.
- Capacity: DK alone is 487 KB against a 92 KB stock pool → `--expand` is mandatory for a full
  import, but a single ~20-pose run fits stock with room to spare.
- **In-place writing would not avoid expansion** (A.31): only **1 of 120** poses fits the slot it
  replaces, because the sheet's upright DK is **38 % larger** than stock's compact knuckle pose.
  Even a perfect in-place path still needs 115.8 KB against the 92 KB pool. `--batch` reports
  `IN-PLACE FIT` on every run, `--dry-run` included.
