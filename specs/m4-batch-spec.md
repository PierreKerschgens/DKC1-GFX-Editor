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

> **Qualified by A.11: a strip is not always an animation.** 17 of the DK sheet's 46 strips carry no
> caption and continue the preceding captioned one — 12 as a deliberate new row, 5 as a genuine
> horizontal-gap fragment. The authorable unit is the *captioned run*, not the strip, which makes it
> 29 units for DK rather than 46. The claim above is right that the structure is derivable and the
> names are not; it is wrong that strip and animation coincide.

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

> **Superseded in part by A.9.** Everything above holds as a way to *write down* a mapping. The
> paragraphs below, which treat frame count as the thing that narrows the search, do not survive
> contact with a strip whose identity is independently known. A.9 has the measurement.

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

**Now answered — see A.8.** Nothing in the *scripts* labels an animation as DK's, but the seed
`0x8C` plus a rendered contact sheet settles it directly.

### A.8 DK's block, located from the M0 seed

M0 recorded that image index `0x8C` decodes to a correctly-posed Donkey Kong
(`prd-sprite-importer.md` §9), so DK's indices can be grown from a known point rather than searched
for. `Core/IndexOwnership.cs` (`--whose`, `--contact-range`) does both halves of the pass.

**The graph method alone is not sufficient — recorded because it looks like it should be.** Treating
animation↔index as a bipartite graph and taking the connected component containing `0x8C` converges
in two rounds to 14 animations / 56 indices in five runs. It is tempting to stop there: it is
derived, deterministic, and the closure terminating fast reads as convergence on the truth. It is a
**lower bound**. Rendering the gap between its runs (`0xE0..0x1FC`, which the closure never reached)
shows 72 *more* indices, every one of them DK. The component only spans indices that DK animations
share; DK sprites reachable only from animations outside the component, or from no animation at all,
are invisible to it — and A.7 already measured that 32.7 % of the GFX table is animation-unreachable.

**What settles it is the contact sheet.** `--contact-range` decodes an index range, crops each
sprite to its opaque bounds and captions it with its index. A stride-12 survey of the whole
table (227 cells) shows the character layout at a glance, and the boundary then binary-searches in
two renders:

| range | contents |
|---|---|
| `0x8C..0x854` | DK, continuous |
| `0x858..0x8A8` | **not DK** — a foreign island, most likely Manky Kong (A.13). Must be excluded from a DK manifest |
| `0x8B0..0x950` | DK, continuous |
| `0x954..` | Diddy Kong — the character boundary, **confirmed by palette flip** (below) |

**The boundary is confirmed, not eyeballed.** Rendering one range twice — once in `Donkey Kong 1P`,
once in `Diddy Kong 1P` — inverts cleanly at `0x954`: below it DK is correct and Diddy is a garish
red mess, above it Diddy resolves with a bright red cap and DK washes out. A sprite drawn with the
wrong character's palette is visibly wrong, so the flip point *is* the ownership boundary. This is
the check that answers the A.8 caveat about palette-based identity, and it is cheap enough to repeat
for any other character: `--contact-range <lo>..<hi> --palette <name>`.

So **DK owns `0x8C..0x950` — 562 indices**. That the DK sheet holds 533 poses (A.1) against 562
stock DK slots is the corroboration: the sheet's author matched frame counts to the original, and
the two land within 5 % of each other. Nothing in A.7's frame-count ambiguity could have produced
that agreement, which is why the visual pass was the right instrument.

The last slot was read off the render by the project owner, not inferred: `0x950` decodes dark and
indistinct, and was initially written up here as "transitional or unused". It is DK. Recorded
because it is the failure mode of this whole method — a slot too murky to caption confidently is a
slot to *show someone*, not to guess at.

### A.9 Frame-count matching does not work — it selects the wrong animation (negative result)

A.7 called count-matching *ambiguous* and treated A.8's block as the fix: restrict to DK's 69
in-block animations and a strip's pose count should narrow it to a handful. Tested against strips
whose identity is known independently — **the sheet's captions are legible to a human at 3× zoom,
even though no parser can read them** — it does not narrow toward the right answer. It excludes it.

| sheet strip | poses | count-matched candidates in DK's block | correct? |
|---|---|---|---|
| 6, captioned **"Walk"** | 20 | 5 (anims 2, 3, 5, 14, 20 — crawl, climb, tumble) | **none.** The real walk is anim 4/108, `0x8C..0xDC`, **21 frames** — excluded by the count filter |
| 10, captioned **"Jump"** | 19 | 1 (anim 74, `0x2E4..0x32C`) — a *unique* match | **no.** Anim 74 keeps DK grounded with arms forward, ending bent over; it reads as the hand-slap, not a jump |

Two attempts, two wrong answers, the second from the strongest signal the method can emit (a unique
in-block match). The 20 is not a slicing artefact — the band genuinely ends after 20 poses, checked
at the right edge.

**Why it cannot work, which A.5 already implied.** The author redrew the set to his own taste and
said so: he removed frames that were "the same frames played in reverse" and others that "are
looped". His frame counts are therefore *independent* of the ROM's, so agreement between a strip's
pose count and an animation's frame count carries no information. A.7 read coincidence as evidence.

**Consequences.**
- The `animation` shorthand (C.2) stays, but only as a way to *record* a mapping already known. It
  is not a discovery mechanism, and this spec should never have implied it was.
- `LengthMismatch` will fire often and correctly. It is the guard that caught this.
- **8 of 46 DK strips have pose counts (13, 17, 22, 25) that no DK animation has at all.** Those
  cannot map 1:1 under M4's scope in principle, not merely in practice.
- M4 replaces sprite images and repoints the GFX table; it does not rewrite animation bytecode. So
  wherever the counts differ, a faithful import needs either stale leftover frames or script
  editing — **the latter is a new milestone, not a detail of this one.**

The mapping is therefore a per-strip caption-read plus an action match, with both sides now tooled:
`--slice` for the sheet, `--anims-in` + `--contact-range` for the ROM.

### A.10 The DK sheet's captions, read (survey, sheet side)

A.9 established that the strip→animation mapping has to come from captions and actions. The
captions are artwork, so no parser can read them — but `--captions` crops the region above each
strip's **leftmost** pose, upscales it and stacks the results with each crop boxed, so a human reads
all 46 off three images.

> Anchor on the leftmost pose, not the strip's bounding box. Strip 10's `MinY` is pulled 28 px above
> its left edge by one tall pose far to the right, which put the window above the text entirely and
> made "Jump" look like it had no caption at all. Boxing each crop matters for the same reason: an
> unboxed caption near a row boundary reads as belonging to either neighbour.

**29 of 46 strips are captioned** (30 names — strip 14's box holds both "Hit" and "Death"):

| strip | p | caption | strip | p | caption | strip | p | caption |
|---|---|---|---|---|---|---|---|---|
| 0 | 6 | Idle | 16 | 17 | Barrel Throw | 30 | 14 | Swim |
| 1 | 2 | Turn | 17 | 12 | Steel Keg Ride | 31 | 9 | Victory |
| 2 | 10 | Swap (Partner) | 18 | 18 | …Ride Look | 33 | 11 | Sad/Failure |
| 3 | 7 | Bang Chest | 19 | 22 | Ledge | 34 | 24 | Intro Cutscene |
| 4 | 5 | Swap (Leader) | 20 | 7 | **Ground Slap** | 42 | 15 | End Credits |
| 6 | 20 | Walk | 22 | 4 | Rope Idle | 44 | 16 | End Credits Part 2 |
| 7 | 20 | Run | 23 | 6 | Rope Climb | | | |
| 8 | 14 | Roll | 24 | 2 | Rope Turn | | | |
| 10 | 19 | Jump | 26 | 2 | Swing | | | |
| 12 | 4 | Start Crawl | 28 | 2 | Minecart Idle | | | |
| 14 | 13 | Hit / Death | 29 | 14 | Minecart Up and Down | | | |
| 15 | 25 | Barrel Pick Up | | | | | | |

**17 strips carry no caption:** 5, 9, 11, 13, 21, 25, 27, 32, 35–41, 43, 45.

### A.11 Why those 17 are uncaptioned — checked, and it is two mechanisms, not one

The first guess was "the horizontal-gap split (A.4) cut one captioned animation in two". Checking
strip geometry against band membership falsified it as a *general* explanation and split the group:

| group | strips | shape | mechanism |
|---|---|---|---|
| A | 5, 9, 11, 13, 21, 32, 35, 36, 38, 41, 43, 45 | own band, `firstX` 27–41 (left margin) | a deliberate **new row**, not a split |
| B | 25, 27, 37, 39, 40 | share a band with a captioned strip, `firstX` 300–1019 | the horizontal-gap split, as originally guessed |

Only group B matches the original hypothesis. Group A occupies a full row of its own, so nothing was
cut — and it is not a *space-driven* wrap either: the captioned row above each one stops well short
of the 1236 px margin ("Roll" ends at 713, "Start Crawl" at 273, "Bang Chest" at 757). The author
simply started a new row.

**They carry no caption anywhere, not just at the left.** Widening the search to the full sheet
width above each group-A strip turns up no animation name — only the sheet's attribution line,
"Sprites by Michael Ropple (spacepig22)", sitting to the right of the Roll row. (A.2 says black
appears "only in the rendered row captions"; the credit is a second black-text element. It changes
nothing — both are excluded as annotation — but the claim as written is incomplete.)

**So the substance of the hypothesis holds even though the mechanism did not.** Each group-A strip
directly follows a captioned one and continues it visually: strip 8 "Roll" → strip 9, still curled
and tumbling; strip 10 "Jump" → strip 11, upright landing; strip 12 "Start Crawl" → strip 13,
crawling. They belong to the preceding named animation.

**One distinction the sheet cannot settle.** "Continuation of the same animation" and "a distinct,
unnamed follow-on animation" look identical here, and strip 12→13 is the case that shows it: a
4-pose "Start Crawl" followed by 14 poses of crawling reads at least as naturally as *Start Crawl*
then an unnamed *Crawl* loop — which the ROM would hold as two animations, not one.

**Consequence for A.4.** "The manifest's natural unit is the strip" does not hold: 17 of 46 strips
are not animations, by either mechanism. A manifest entry per strip would address fragments. The
unit is the **captioned run** — a captioned strip plus any uncaptioned strips following it — which
brings the DK sheet to **29 authorable units, not 46**.

**A.9, confirmed from the other direction.** Strip 20 is captioned **"Ground Slap"** — and anim 74,
the animation count-matching selected for the 19-pose *Jump* strip, is the one that keeps DK grounded
with arms forward. By action it belongs to strip 20. Its counts are 19 frames against 7 poses, so the
correct pairing is one that count-matching could never produce, while the pairing it *did* produce
was wrong. That is A.9's claim demonstrated in a single pair.

**Still open: the ROM side.** Naming the sheet's 46 strips does not name the ROM's 69 in-block
animations. Two pairings are established — Walk ↔ anim 4/108 (`0x8C..0xDC`, confirmed visually) and
Ground Slap ↔ anim 74 (`0x2E4..0x32C`, by action) — leaving the rest to a `--contact-range` pass per
animation.

### A.12 The ROM side: 69 animations are 53 distinct index sets

`--anim-sheet <lo>..<hi>` is the ROM-side counterpart of A.10's caption montage: one row per
**distinct index set** among the in-block animations, labelled with the animation ids sharing it,
frames drawn in the script's own order so a row reads as the animation plays.

**Grouping by index set is what makes the pass tractable.** DK's block holds 69 animations but only
**53 distinct sets** — several scripts play the same frames at different speeds or from different
entry points (anims 2/14/20 all draw `0x330..0x37C`; 4/108 both draw `0x8C..0xDC`). Rendering per
animation would repeat the same pictures 16 times over.

**Render at `--zoom 2`.** DK's sprites are ~40 px, so at 1:1 an action is not reliably identifiable
— the first pass at cell 46 produced plausible-looking guesses and no defensible ones. Raising the
cell alone does nothing, because the montage inherits the contact sheet's deliberate no-upscale
rule; `--zoom 2` is what makes the difference between "probably a jump" and knowing.

**Every row being the same character is the expected result, and has a negative control.** A sheet
of 53 rows that all look like stock DK is a reasonable thing to be suspicious of — it is equally
consistent with the montage ignoring its input. Running the same command on `0x954..0xF00` with
`Diddy Kong 1P` produces unmistakable Diddy (red cap, red shirt, tail, cartwheel), so the tool
renders what the indices actually hold. It also re-confirms A.8's boundary from the far side: the
first Diddy group begins at `0x964`, with nothing DK-shaped above `0x950`.

**Established pairings** — each from an action match against a caption, never a frame count:

| sheet run | ROM animation | indices | frames vs poses |
|---|---|---|---|
| strip 6 "Walk" | anim 4 / 108 | `0x8C..0xDC` | 21 vs 20 |
| strip 8 "Roll" (+ 9) | anim 2 / 14 / 20 | `0x330..0x37C` | 20 vs 14 (+15) |
| strip 12 "Start Crawl" (+ 13) | anim 3 | `0xE0..0x12C` | 20 vs 4 (+14) |
| strip 20 "Ground Slap" | anim 74 | `0x2E4..0x32C` | 19 vs 7 |

Not one pair has matching counts, which is A.9 holding across every case checked so far.

### A.14 Matching the captioned runs — results, and a filter that hid the riding ones

**A structural finding that outranks any individual match.** `--anims-in` selects animations drawing
*entirely inside* a range. That is right for count-matching, and it silently excludes exactly the
animations that draw DK **plus a prop or a mount** — 14 of them for DK's block. Their outside
indices are things like `0x2710..0x2724`, which renders as **Rambi**. So several sheet captions
(*Steel Keg Ride*, *Ride Look*, *Minecart Idle*, *Minecart Up and Down*, and probably the barrel
pair) cannot be matched from the 69-group set at all: their animations were never candidates. This
is the mount-command case A.8 flagged as a risk to the closure, reappearing as a blind spot in the
candidate list.

**Confirmed** — action match against a caption, each checked at `--zoom 2`:

| captioned run | animation | indices | status |
|---|---|---|---|
| ~~6 "Walk"~~ **0 "Idle"** | 4 / 108 | `0x8C..0xDC` | **corrected in-game — see A.16** |
| 8 "Roll" (+9) | 2 / 14 / 20 | `0x330..0x37C` | holds |
| ~~12 "Start Crawl"~~ | ~~3~~ | `0xE0..0x12C` | **withdrawn — see A.16** |
| 20 "Ground Slap" | 74 | `0x2E4..0x32C` | holds |
| 30 "Swim" | 90 | `0x3A4..0x3DC` | holds |
| 14 "Death" | 16 | `0x27C` (draw order) | holds |

**Checked against the sheet, one by one.** Each proposal from the first pass was verified by
cropping its captioned strip and comparing it with the animation. **Two survived, one was rejected
outright, one resolved only to a region, and two remain unconfirmed** — a 2-of-6 hit rate, which is
roughly what A.9 should have led anyone to expect.

| captioned run | proposal | verdict |
|---|---|---|
| 30 "Swim" | 90 (`0x3A4..0x3DC`) | **confirmed** — horizontal breaststroke, head right, arms reaching; identical posture |
| 14 "Death" | 16 (`0x27C`) | **confirmed** — front recoil, head-over-heels tumble, ends prone |
| 3 "Bang Chest" | 100 (`0x7A4`) | **region confirmed, animation ambiguous** — anim 99 draws the same front-facing chest-beating |
| 33 "Sad/Failure" | 84 / 85 (`0x5B4`) | **rejected** — the sheet is DK *standing* dejected; 84/85 has him lying down |
| 31 "Victory" | 72 (`0x6B8`) | **unconfirmed** — the sheet runs neutral → arms overhead; anim 72 is 3 frames of arms-up only |
| 2 / 4 "Swap" | 78 (`0x55C`) | **unconfirmed** — the sheet is a turn-and-wave handoff; anim 78 is arms spread, walking |

> **A verification method that was itself wrong, and nearly cost a correct match.** Death was first
> checked by rendering the *index range* `0x27C..0x2C4`, which showed no tumble and looked like a
> rejection. But an animation's frames are not its index range — anim 16's draw order visits frames
> the contiguous range does not contain, and vice versa. Only `--anim-sheet`, which walks the script,
> shows what an animation actually plays. **Verify against draw order, never against an index range.**

**Running total after A.16: 4 confirmed** (Roll, Ground Slap, Swim, Death) plus **Idle** newly
identified in-game; Walk and Start Crawl withdrawn; 1 region-level (Bang Chest); 2 rejected.

**Unresolved, with the reason recorded rather than left blank:**

- *Steel Keg Ride, Ride Look, Minecart ×2, Barrel Pick Up / Throw* — **the filter was lifted
  (`--anim-sheet --props`) and this did not solve them.** See A.15.
- *Rope Idle / Climb / Turn, Swing, Ledge* — several hanging and climbing groups exist
  (`0x130..0x17C`, `0x158`, `0x164`), but rope, ledge and swing are not distinguishable from one
  another by silhouette, and guessing among them is precisely the error mode A.9 documents.
- *Idle, Turn, Jump, Intro Cutscene, End Credits ×2* — plausible candidates exist for each; none is
  distinctive enough to assert.

**Score: 4 confirmed, 6 proposed, 19 open of 29 runs.** The honest summary is that the ROM side is
tooled and inventoried but not solved, and the largest remaining obstacle is structural (the prop
filter), not perceptual.

### A.15 Lifting the prop filter — a prediction that failed, and one fact gained

`--anim-sheet --props` selects the complement of the default: animations touching the range that
*also* draw outside it. A.14 predicted this would close roughly six runs, since the sheet names
several riding animations. **It closed none.**

All 14 prop-drawing animations (12 distinct index sets) are **animal-buddy mounts**, frames
alternating DK with the buddy: Rambi, Expresso, Winky, Enguarde. There is no minecart animation and
no steel keg among them. The sheet's *Steel Keg Ride*, *Ride Look* and both *Minecart* captions
remain unmatched, and the reason is not the one A.14 gave.

**What was actually gained.** Every one of those animations draws its DK frames from
`0x538..0x594`, so that region is **DK's mounted/riding pose set** — a fact no other pass had
established. `anim 27` (8 frames, `0x538`) draws DK alone from the same region, which makes
`0x538..0x594` the region to search for *Ride Look* and *Steel Keg Ride* rather than the whole block.

**Where the missing animations are — hypothesis tested and confirmed.** The guess was that a
minecart animation draws only the *vehicle*, with DK composited by game code, so it never touches
DK's range and no selection can surface it. Chased from the vehicle side:

- The cart sprites are `0x1C84..0x1CC8` (confirmed by rendering).
- Their animation coverage is almost nil: `anim 361` draws a single cart frame (`0x1CA4`),
  `anim 226` a single barrel frame (`0x1C80`), and `anim 220`/`232` are 4-frame scripts around
  `0x1C74..0x1C7C`. Every other sampled cart index is referenced by **0** of the 440 scripts.
- **No animation draws both a DK index and a cart index.** The 14 prop animations' outside indices
  are all animal buddies (`0x1ECC`, `0x1F4C`, `0x2324`, `0x2578`, `0x2710`, `0x28F0`); none lands in
  the cart range.

So *Minecart Idle* and *Minecart Up and Down* **cannot be matched through the animation table at
all** — there is no DK+cart animation to match them to. Their DK poses exist as sprites but are
composited by game code, which the table does not model. The same reasoning covers *Steel Keg Ride*.

**What that means for the manifest.** These runs are matchable only by locating DK's riding poses
visually, most likely in the `0x538..0x594` region this section identified. They are not blocked by
judgement or tooling; they are outside what `Core/AnimationTable.cs` can see, and no amount of
rendering animations will surface them.

**Recorded because the prediction was confident and wrong.** "Lift the filter and ~6 runs close" was
reasoning from the caption list to the ROM without checking the ROM. The filter was worth lifting —
it produced a real fact — but not for the stated reason.

### A.16 The in-game test that falsified the anchor pairing

Everything above was verified by *looking at sprites*. The first time the work was verified by
**running it**, the oldest and most-trusted pairing turned out to be wrong.

The 20-pose *Walk* strip was imported into `0x8C..0xDC` and the ROM booted in zsnes. The project
owner's report: **idling shows the new artwork and looks like DK slowly walking; moving shows the
old artwork.** That is only consistent with one reading — `0x8C..0xDC` is DK's **idle**, not his
walk. The new poses appear when he stands still because those are the frames the idle plays; his
movement animation was never touched.

**Two "confirmed" pairings die with it:**

- *Walk → anim 4/108* is wrong. `0x8C..0xDC` is **Idle** (sheet strip 0).
- *Start Crawl → anim 3* is wrong. It rested on reading `0xE0..0x12C` as a crawl.

**And the reason both were wrong is one mistaken premise:** DK **knuckle-walks** in DKC1, on all
fours. So the upright standing set at `0x8C..0xDC` is idle, and the on-all-fours set at
`0xE0..0x12C` — which this spec called "Crawl" — is his locomotion. Every downstream reading
inherited that swap.

**What it suggests, now testable:** `0xE0..0x12C` is 20 frames and the sheet's *Walk* is 20 poses;
`0x130..0x17C` is 20 frames and the sheet's *Run* is 20 poses. `port/dk-move-test.sfc` imports the
Walk strip into `0xE0..0x12C` — if the new artwork appears **while moving**, that confirms it.

**The methodological point, which outlives these two pairings.** Contact sheets and draw-order
montages establish what a sprite *looks like*, never what the game *uses it for*. Only running the
ROM does that, and it cost one boot to overturn a conclusion six passes of static analysis had
treated as settled. **Every pairing in this spec that has not been observed in motion should be read
as provisional**, including the four still marked as holding.

### A.17 Walk confirmed in-game, and a real importer defect it exposed

**Walk is `0xE0..0x12C`** — confirmed by booting `port/dk-move-test.sfc`: the new artwork appears
while moving slowly. The run is still stock, so it is almost certainly `0x130..0x17C` (20 frames
against the sheet's 20-pose *Run*). A.16's knuckle-walk correction holds.

**The defect: DK bobs vertically through the imported cycle.** Reported from play, not visible in any
still. The cause is `SpriteImporter` step 1, which anchors every imported pose at the replaced slot's
placement-bbox **top-left**. The sheet's Walk poses range **47..52 px** tall, so pinning the top
lets the feet fall by the height difference:

| index | original top / bottom | top-anchored new bottom | bottom-anchored |
|---|---|---|---|
| `0xE0` | 88 / 128 | 136 | 128 |
| `0xF4` | 92 / 133 | 143 | 133 |
| `0x118` | 90 / 133 | 139 | 133 |

A 7 px swing across three frames of a ground-contact animation. Top-anchoring reproduces the
original's **head** position and lets the feet drift; bottom-anchoring reproduces the original's own
**foot** positions exactly, inheriting whatever intentional bob the stock animation had.

`ImportOptions.AnchorBottom` (`--anchor-bottom`) does the latter. **It was tested in-game and did
not fix the bob — if anything it was worse.** The option is kept (default off, gates unchanged) but
it is not the fix, and the diagnosis above is incomplete.

**Corrected diagnosis: the anchor is at the wrong *level*, not the wrong *edge*.**

- The sheet's poses are already correctly aligned to each other. Strip 6's bottoms span **300..303**
  — a 3 px spread that is the artist's intended foot bob.
- The importer discards that. It crops each pose to its own bbox and re-anchors it to **its own
  target slot's** bbox, and those vary independently (`0xE0`/`0xF4`/`0x118` bottoms are 128/133/133).
- Worse, a target slot's bbox bottom is **not a ground line**: in a knuckle-walk the lowest point is
  sometimes a hand and sometimes a foot, so neither edge of the original bbox is a stable reference.

So per-pose anchoring cannot work by construction — *either* edge injects the original animation's
per-frame variation on top of the new artwork's. **The fix is strip-level:** place every pose of a
run against **one** reference, carrying the sheet's own relative vertical offsets through unchanged.
That requires strip context, which `SpriteImporter.Import` (one pose, no siblings) does not have —
it belongs in `BatchImporter`, which already iterates a strip's poses together.

**Implemented** as `BatchImporter`'s `alignStrip` (`--batch --align-strip`), via a new
`ImportOptions.OriginY` override: for each strip, `baseline = max(RectY + RectH - 1)`; each pose's
height above that baseline is reproduced below a single ground reference — the first target slot's
`PlacementMaxY` — so the run keeps sitting where the animation it replaces sat.

Measured on the DK walk, the placement now tracks the sheet instead of the slots:

| index | pose h | sheet bottom | aligned bottom | old (top-anchored) |
|---|---|---|---|---|
| `0xE0` | 49 | 300 | 125 | 136 |
| `0xE4` | 48 | 301 | 126 | — |
| `0xF4` | 52 | 303 | 128 | 143 |
| `0x118` | 50 | 302 | 127 | 139 |
| `0x12C` | 49 | 300 | 125 | — |

**Resolved, and measured rather than eyeballed.** `--baseline <lo>..<hi>` prints each sprite's
opaque bbox in canvas coordinates and the spread of its bottom edge — the foot line. Run over
`0xE0..0x12C` on each build:

| ROM | foot-line spread |
|---|---|
| **stock DKC walk** | **2 px** |
| top-anchored (first import) | 10 px |
| bottom-anchored (`--anchor-bottom`) | 8 px |
| **`--align-strip`** | **3 px** |

The sheet's own poses carry a 3 px baseline spread, so a faithful import *should* bob by 3 px. It
does. The residual motion the operator noticed is **the artist's, not the tool's** — 1 px livelier
than stock. Matching stock exactly would mean editing the artwork, not the importer.

The measurement also explains why the second attempt read as "the same if not worse": 10 → 8 px is
within noticing distance of no change at all.

**Then a second axis, found the same way.** With the bob fixed the cycle was reported as still "not
as smooth as Diddy's" — Diddy being stock, and therefore the right yardstick. Extending `--baseline`
to report **centre-X** drift located it immediately:

| | foot line | centre X |
|---|---|---|
| stock DK walk | 2 px | 4 px |
| `--align-strip`, vertical only | 3 px | **7 px** |
| `--align-strip`, both axes | 3 px | **4 px** |

X had been left on the original rule — anchor the pose's *left edge* to the slot's — so a wider pose
grew rightwards and dragged the body sideways, nearly doubling stock's horizontal travel. The sheet
offers nothing usable here (a strip's X positions are page layout, not animation offsets), so X is
handled per pose instead: match the replaced frame's **centre**, which reproduces whatever
horizontal travel that frame actually had. Both axes now sit at stock's numbers, the residual 3-vs-2
vertical being the sheet's own drawn bob.

**Confirmed in play** — the two-axis build was booted and the cycle reads as smooth as a stock
animation. That closes the bug: three attempts, two of which measured plausibly and failed in the
emulator, and one that matched stock on both axes and held.

**The general lesson.** Two separate placement defects hid behind one symptom, and neither was
visible in a still image; both were found only by measuring the imported cycle against the stock one
on the same axis. `--baseline` is that instrument, and it should be run on every imported run before
the run is called done. V4 7/7 unchanged.

**Attempted to make `--align-strip` the default, and backed it out.** It is measurably correct for
new artwork, so defaulting it on looked obviously right. It turns **V4d red**.

V4d's gate 1 re-imports the ROM's *own* sprites and requires the resulting frame to be
pixel-identical — a content-preserving relocation. Alignment moves them, so the frame differs
(1801 px initially). Two fixes narrowed it and neither closed it:

- The synthetic fixture laid every pose out at the same `y`, flattening the vertical relationships
  alignment reconstructs from. Preserving each pose's original origin took the diff to 1062 px.
  *(Kept — the fixture is more faithful either way.)*
- Centring X inside the slot's own box rather than about a computed centre point removes a 1 px
  truncation. It changed nothing here, so X was not the residual.

**The actual blocker, and it is structural:** the slicer measures **opaque-pixel** bounds, while a
slot exposes **tile-placement** bounds, which are 8 px-grid aligned. The two "baselines" are not the
same quantity, so a sheet-derived offset cannot reproduce a slot-derived one exactly. Making
alignment the default requires reconciling those two coordinate systems first.

Left opt-in. That is the conservative choice and it is the gate's call, not a preference: **a gate
that says an identity no longer holds is evidence, and this one was right to refuse.**

**One residual risk, untested.** The run's *absolute* height is now tied to the first pose's slot
bottom. If that particular frame's lowest pixel is a knuckle rather than a foot, the whole cycle
will sit a few pixels off — consistently, which is far better than bobbing, but still wrong. If that
shows in play, the reference wants to be a per-run constant chosen by eye rather than derived.

**Why this was invisible to every check built so far.** M2b's Part G drift report *did* flag it —
every pose printed `[DRIFT > 4px]` — and it was read as advisory noise about hitboxes. It was
describing a visible rendering fault the whole time. The spec's own Part E predicted this ("500
re-posed frames make the drift report load-bearing rather than advisory") and the prediction was
right, but nothing acted on it until someone played the game. **A warning that fires on 100 % of
cases teaches operators to ignore it**; the drift check needs a severity split — feet-line movement
is a defect, extent change is a hitbox note — before a 533-pose import.

### A.18 Drift severity split, and what it still cannot tell you

M2b's drift report fired `[DRIFT > 4px]` on **100 % of** imported poses. A warning that always fires
is one operators learn to skip, and this one was skipped for a whole session while it was describing
the visible bob of A.17. Split into two:

- **`[FEET MOVED ±Npx]`** — the bottom edge moved relative to the frame being replaced. This is the
  half that can be a rendering fault.
- **`[extent changed — hitbox note]`** — different volume, feet unchanged. Advisory: the separate,
  untouched hitbox table no longer matches the art.

On the DK walk this cuts the flagged set from 20/20 to 15 (unaligned) and 9 (aligned) — the label
now varies with the thing it is meant to describe, which the old one never did.

**It is still a proxy, and the spec should not pretend otherwise.** Per-pose drift compares each
pose against *its own* replaced frame. Bobbing is a property of the **run**: how much the feet line
moves across the cycle. Those are different quantities, which is why 9 poses still flag as
feet-moved on a build that plays perfectly smoothly — alignment deliberately repositions frames
relative to the run's baseline, so individual deltas are expected.

**The authoritative check is `--baseline <lo>..<hi>`**, comparing foot-line and centre-X spread
against the same range in the stock ROM. The drift label is a per-pose hint; the spread is the
measurement.

**Palette as the identity test, not a caveat.** A survey rendered in one palette locates boundaries
by silhouette but cannot prove *identity* — so the palette-flip check above does that job instead,
and it is strictly better evidence than a silhouette.

### A.19 The two coordinate systems, reconciled — alignment is now the default

A.17 left `--align-strip` opt-in behind a blocker it called structural: the slicer measures
**opaque-pixel** bounds while a slot exposes **tile-placement** bounds, so a sheet-derived offset
could not reproduce a slot-derived one and V4d's identity gate refused. That is resolved. Alignment
is the **default**; `--no-align-strip` opts out (`--align-strip` is kept as an accepted no-op).

**First, the gap was measured rather than assumed.** New read-only command `--coords <lo>..<hi>`
prints both boxes per slot:

| range | slots | boxes disagree | max \|dMinY\| | max \|dMinX\| |
|---|---|---|---|---|
| first 60 indices | 60 | 36 | 7 px | 1 px |
| walk `0xE0..0x12C` | 20 | **20** | 6 px | 0 px |

So the slack is real, it is per-sprite, and it is almost entirely **vertical** — which is why the
vertical bob was the hard one and X fell out on the first try.

**Two defects, and both had to go for the identity to close.**

1. **Wrong currency.** `reference` was the target slot's `PlacementMaxY` — a *tile* bottom —
   subtracted from a *sheet* baseline, which is an ink bottom. That injects the slot's grid slack
   into the run's absolute height. Fixed by reading the slot's **opaque** box:
   `SpriteSlot` now carries `OpaqueMinX/MinY/MaxX/MaxY` alongside the placement four, computed in
   the same canvas coordinates, so both boxes live on one type with a doc comment saying which to
   use when.

2. **Mismatched reference pose.** The baseline came from the strip's *deepest* pose while the
   reference came from the *first* pose's slot. Those are the same pose only by luck; otherwise the
   whole run is offset by the difference. Fixed by having one reference pose supply both sides.

With both, the arithmetic collapses to an exact identity when a slot's own art is re-imported into
it — `originY = OpaqueMinY`, and the X centring algebra cancels to `OpaqueMinX` for either parity —
which is precisely what V4d gate 1 demands.

**Result: V4 7/7, V4d 6/6 cores** with alignment on by default. M1, M2a, M2b and M3 unaffected.

**And it corrected a real placement error, not just a gate.** Re-measuring the DK walk with
`--baseline 0xE0..0x12C`:

| ROM | bottom edge | foot-line spread | centre X | centre-X spread |
|---|---|---|---|---|
| **stock DKC walk** | **127..129** | **2 px** | **126..130** | **4 px** |
| old `--align-strip` | 125..128 | 3 px | 127..131 | 4 px |
| **reconciled** | **127..130** | **3 px** | **126..130** | **4 px** |

The spreads were already right; the *absolute* placement was not. The old build sat 2 px high and
1 px right — small, constant, and exactly the residual risk A.17 flagged in its last paragraph
("the run's absolute height is now tied to the first pose's slot bottom … if that frame's lowest
pixel is a knuckle rather than a foot, the whole cycle will sit a few pixels off"). It was, and
using the opaque box removes it. The reconciled build's centre-X range now matches stock's exactly.

**The lesson, and it is the reusable one.** The blocker was labelled structural and it was really a
units error — two quantities with the same name ("the slot's bottom") measured in different
systems. A gate caught it, refused to be argued with, and was right; the fix was to make the two
systems commensurable rather than to weaken the gate. **When a placement calculation is obviously
correct and lands wrong by a small constant, suspect the units before the algebra** — `--coords` is
the instrument for that, as `--baseline` is for the run-level spread.

**Booted, and the result is worth stating carefully.** `port/dk-move-reconciled.sfc` was played:
**still smooth, and no visible difference to the feet.** Three separate claims come out of that with
three different strengths, and collapsing them into "confirmed" would be the same overclaiming A.16
warned about:

- **Confirmed — no regression.** The reconciled build plays as smoothly as `dk-move-aligned2.sfc`,
  which was itself confirmed in play. Defaulting alignment on is safe. This was the load-bearing
  risk and it is retired.
- **Not confirmed — the 2 px correction.** It is below the threshold of visibility in play. The
  measurement says the placement is more correct; the boot cannot tell. **The practical value of
  this change is the gate, not visible quality** — it makes alignment defaultable by making the
  identity exact. Anyone expecting to *see* A.19 in motion is expecting the wrong thing.
- **Partially retired — the knuckle-anchor risk.** Its failure signature (A.17's closing paragraph)
  was a *consistently* sunk or hovering DK across the whole cycle — gross, not subtle, and it was
  not observed. So `0xE0`'s lowest opaque pixel is at worst near-foot. Not proven; the obvious
  failure simply did not appear.

**The generalisable point.** Sub-3px placement error is not play-observable, so `--baseline` and
`--coords` are not merely convenient here — for this size of defect they are the *only* instruments
that work. That inverts A.16's rule without repealing it: booting remains the only thing that
establishes what a sprite is *used for*, and measurement remains the only thing that resolves where
it *sits* to within a few pixels. Use each for what it can actually settle.

### A.20 Run confirmed at `0x330..0x37C`, and the X axis has A.17's bug

**Three mappings settled by one boot, two of them corrections.**

| claim | before | after |
|---|---|---|
| Run | "very likely `0x130..0x17C`" (20f vs 20 poses) | **`0x330..0x37C`**, confirmed in play |
| `0x330..0x37C` | provisionally **Roll** | **falsified** — it is Run |
| `0x130..0x17C` | the Run candidate | a **climb/hang**, not locomotion |
| Roll | `0x330..0x37C` | **unknown again** |

**Frame-count matching is now 0 for 3.** The `0x130..0x17C` prediction rested entirely on 20 frames
against 20 poses. `--baseline` killed it before a boot was spent: the foot line is steady but DK's
*height* swells 40 → 72 → 36, a windup/hold/recovery one-shot rather than a loop, and the contact
sheet shows him upright with both arms overhead. That is one of the hanging/climbing groups.

`--anims-in` then showed only three 20-frame index sets exist in DK's whole block, so `0x330..0x37C`
was the last candidate by elimination. Booted: **new art while running, stock art while walking.**
The operator also reports the roll unchanged (the stand-up after a roll is still regular DK), which
independently falsifies the provisional Roll pairing — that tier is now 0 for 3 as well.

**The negative result is worth as much as the positive one.** `--baseline` refuted a mapping from
the ROM side alone, before an import existed. Height *profile* across a range is a cheap and strong
discriminator: a loop holds height roughly constant, a one-shot swells and recovers.

**And the run bobs — X inherits the replaced animation's per-frame variation.**

Alignment sets each pose's X to its replaced frame's bbox centre (A.17), so the imported centre-X
sequence is stock's, frame for frame. For this animation that sequence contains a discontinuity:

```
frame:    0x358  0x35C  0x360  0x364  0x368
centreX:   130    131    128    122    123      <- -9 px over two frames
```

Stock absorbs it because its gallop art genuinely lunges: the bbox centre moves *because* the drawn
body moved, so the body reads as continuous. The sheet's Run poses are **upright** DK, each centred
in its own bbox and not lunging, so the same centres teleport the whole body sideways.

**This is exactly the defect A.17 fixed for Y, on the axis A.17 chose to leave per-pose** — and its
reasoning ("a strip's X positions are page layout, not animation offsets") is still correct about
the *sheet*, which is why the bug survived: the sheet genuinely cannot supply relative X. What was
missed is that the fallback inherits the replaced animation's variation, which is the thing
per-pose anchoring was condemned for in the first place.

The walk never exposed it. Stock's walk centre-X spans 4 px and moves smoothly; stock's run spans
9 px with a 6 px single-frame step. **A fix validated on one animation is not validated.**

Vertical is fine here: imported bottoms span 4 px (the sheet's own bob) against stock's 8 px.

**Implemented as `--flat-x`, and it stays opt-in.** Strip-level X anchors a run to one centre and
lets the sheet's art carry the horizontal motion. It **cannot** be the default: a single shared
centre is not each pose's own centre, so it breaks V4d's identity, which needs `originX =
OpaqueMinX` per pose. The two requirements are genuinely opposed — unlike A.19's, this tension does
not dissolve under a change of coordinates — so it is a per-run choice.

The centre is the **mean** of the replaced frames' centres, not the reference pose's own: a run's
first frame is as likely as any to be a horizontal extreme, and averaging cannot be thrown off by
one outlier the way picking can. (Here both give 126–127, so the choice is not load-bearing on this
run — it is chosen for the runs where it would be.)

| build | foot line | centre X |
|---|---|---|
| stock run | 122..130, 8 px | 122..131, **9 px** |
| `--align-strip` (default) | 124..128, 4 px | 122..131, **9 px** — stock's, frame for frame |
| **`--flat-x`** | 124..128, 4 px | **127..127, 0 px** |

**Y is deliberately left alone.** The 4 px vertical is a *double* oscillation — the sheet's strip 7
bottoms run 374 373 374 375 377 377 375 373 373 374 375 374 376 376 377 377 376 375 373 373, two
footfalls per cycle, which is what a run should do. It is the artist's motion and flattening it
would be the error A.17 warned about from the other direction. Only X was inheriting something it
should not.

**Note what `--flat-x` gives up.** Cropping each pose to its own bbox destroys whatever inter-pose
horizontal relationship the sheet had, so the choice is between *inherited* jitter (the replaced
animation's) and *no* horizontal motion at all. There is no third option available from a sheet, and
A.17's original reasoning about page layout is why. For a cycle whose horizontal travel is driven by
the game moving the sprite — which a run is — zero is the right answer.

**Confirmed in play.** `port/dk-run-flatx.sfc` was booted and the run reads correctly; the sideways
snap is gone. V4 stays 7/7 (the flag defaults off).

Unlike A.19's 2 px correction, this one *is* a claim play can settle: the defect was visible before
and is absent after, at a magnitude (9 px over two frames) well above what the screen can resolve.
Gotcha 8 cuts both ways — the question is not whether to trust the boot, it is whether the effect is
large enough for the boot to see. Here it was.

**The DK run is now complete**: mapping confirmed in motion, placement confirmed in motion, both
axes measured against stock.

### A.21 Roll falsified again, and the instrument that should have been built first

`0x188..0x1FC` was imported into and booted: **the roll art is unchanged**. The identification is
wrong. Static identification of DK animations is now **0 for 4** (Walk→4/108, Start Crawl→3,
Roll→`0x330..0x37C`, Roll→`0x188..0x1FC`).

**The aspect-ratio scan was a better method that still failed.** It is genuinely stronger than
frame-count matching — it found a real, distinctive tumble that no counting would have surfaced, and
it found it in seconds. But it answers *what does this sprite look like*, and gotcha 1 says in as
many words that this never establishes what the game uses it for. The lesson did not need
rediscovering; it needed **applying**, and a scan that produces a confident-looking picture is
exactly the thing that makes the rule feel skippable.

**The deeper problem is that the test itself is unreliable.** "Does the new art appear?" asks the
operator to notice an *absence*, against art they have seen a hundred times, during a move lasting
under a second. The screenshots from this boot show DK low with arms extended forward — a posture
that matches stock `0x33C..0x36C` (Run) closely enough that the frames cannot be told apart from a
still. A negative result from that question is not worth much, and the whole strip→animation search
consists of negative results.

**So the question was changed.** `--poison-index <lo>..<hi> --out <rom>` overwrites a range's
**char data** with `PoisonProbe`'s noise, leaving the header, placement table, size and pointer
untouched. Every animation still plays exactly as before; only the pixels become garbage. Verified:
the poisoned sprites render as unmistakable static in DK's own silhouette.

That converts the question from "did the art change?" to "**did DK explode?**" — which needs no
comparison, no memory of what stock looks like, and no still-frame judgement. It also makes
*negative* results trustworthy for the first time, and it **bisects**: poison a wide range, and if
the action garbles, halve it. log₂ boots instead of one guess per boot.

**Run a positive control with it.** `port/dk-poison-run.sfc` poisons the *confirmed* Run range, so
"running turns DK to static" validates the method on this exact ROM and level before any negative
result is trusted. Every past falsification in this spec cost a wrong conclusion partly because the
negative half was never controlled.

**Why this was not built five sections ago,** which is the reusable part: each individual guess
looked cheap — one manifest, one boot — so building an instrument never won against just trying the
next candidate. Four failures in, the guesses have cost far more than the tool did. **When a search
is producing repeated negative results, stop improving the guesses and start improving the test.**

**Both halves fired, and the negative is now worth something.** `dk-poison-run.sfc`: DK turns to
red-and-white static while running, movement otherwise normal — the method works on this ROM and
level, and Run is independently re-confirmed. `dk-poison-roll.sfc`: entirely normal, including the
roll. So `0x188..0x1FC` is genuinely not drawn during a roll. **First trustworthy negative in the
whole search.**

#### `--paint` — the discriminating version, and why one boot now maps many actions

Poisoning bisects at one range per boot: ~430 unmapped DK indices is ~7 boots for *one* animation,
and there are ~19 left. `--paint <lo>..<hi>:<colour>` fills a range's char data with a **constant
palette index** instead of noise, so DK renders as a flat silhouette *in a colour that names the
range*. Same contract — header, placements, size and pointer untouched.

Two properties matter more than the colour trick itself:

1. **Confirmed ranges are left unpainted**, so idle, walk and run look completely normal. The game
   is playable and only *unmapped* actions light up. That makes the test non-disruptive enough to
   explore with, rather than a single scripted check.
2. **Every action tested in one boot is a separate data point.** Roll, jump, climb, swim, hit,
   victory — each reports which chunk drew it. The search stops being one-hypothesis-per-boot and
   becomes a survey.

DK's palette limits this to about five silhouette colours that cannot be confused with each other or
with normal DK (white 15, near-black 1, bright red 8, light pink 12, dark brown 3) — the browns and
oranges are what DK already is. Five chunks per boot against ~430 unknown indices is two rounds to a
~20-index answer, for every remaining animation at once.

Verified before shipping: `0x200..0x214` renders as clean white silhouettes, `0x500..0x514` as clean
red ones.

### A.22 The paint survey — ~15 animations localised in one boot

One boot of `dk-paint-hunt.sfc`, an operator playing normally and naming a colour per action. This
is more mapping progress than A.9–A.21 combined, and it cost one boot.

| action | colour | chunk |
|---|---|---|
| **roll** | black | **`0x380..0x4FC`** |
| getting hit | black | `0x380..0x4FC` |
| jumping on an enemy | black | `0x380..0x4FC` |
| shot out of a barrel (flight) | black | `0x380..0x4FC` |
| crouching in a narrow place | black *(or dark brown — reported uncertain)* | `0x380..0x4FC` |
| turning left↔right | white | `0x180..0x32C` |
| idle | white | `0x180..0x32C` |
| walking + throwing a barrel | white | `0x180..0x32C` |
| ducking | red | `0x500..0x67C` |
| sitting on the rhino | red | `0x500..0x67C` |
| rope swing, left→middle | red | `0x500..0x67C` |
| entering the banana cave | pink | `0x680..0x7FC` |
| carrying a barrel, standing | pink | `0x680..0x7FC` |
| swapping to Diddy | pink | `0x680..0x7FC` |
| climbing a rope | pink | `0x680..0x7FC` |
| rope swing, middle→right | pink | `0x680..0x7FC` |
| life/balloon HUD indicator | pink | `0x680..0x7FC` |
| swimming | alternates red / black | spans two chunks |
| **jump** | **unchanged** | one of the *unpainted* ranges |

**Four things fall out of this that no amount of montage-reading would have produced.**

1. **Roll is in `0x380..0x4FC`** — and so are hit, enemy-bounce, barrel flight and the narrow
   crouch. Bisecting that one chunk separates *five* animations at once, which is why the next round
   is worth more than a single answer.

2. **`0x680..0x7FC` is not purely DK.** The life/balloon HUD indicator paints from it. That may be
   legitimate reuse — DKC1's life icon *is* a Kong head — but it means A.8's "DK owns `0x8C..0x950`"
   has at least one more foreign or shared island in it besides `0x858..0x8A8` (A.13). Do not build a
   DK manifest over that range without checking.

3. **A rope swing crosses a chunk boundary mid-animation** (red left→middle, pink middle→right), and
   **swimming alternates red/black**. Animations are not confined to one contiguous block, which is
   the same lesson as gotcha 2 arriving from a new direction: an index *range* is not an animation.

4. **Jump is unchanged, and that is a real clue.** Every unmapped range was painted, so jump is drawn
   from something already confirmed or characterised: `0x8C..0xDC` (Idle), `0xE0..0x12C` (Walk),
   `0x130..0x17C`, `0x188..0x1FC` (the somersault), or `0x330..0x37C` (Run). `0x130..0x17C` — whose
   height swells 40→72→36 with arms overhead, a windup/hold/recovery one-shot (A.20) — fits a jump
   far better than it fits the climb this spec guessed. **Treat "the climb" as unresolved.**

**The idle conflict resolved itself: there are two idle animations.** The operator confirms DK has a
standing idle and, after several seconds, one where he screams and beats his chest. So `0x8C..0xDC`
(A.16, confirmed in play) and the white `0x180..0x32C` sighting are different animations, not a
contradiction. The sheet has a strip for the second one — **strip 3 "Bang Chest", 7 poses** — which
makes it independently authorable.

#### The colour legend was half unusable — a real limit on this method

The operator also reports that **black and dark brown could not be told apart**, so every "black"
sighting in the table above is ambiguous between `0x380..0x4FC` (colour 1) and
`0x800..0x854`/`0x8AC..0x950` (colour 3). Roll included. Rendering the candidates side by side shows
two independent failures, and only one of them was the one being worried about:

- **Near-black (1) disappears** against DKC1's dark jungle backgrounds.
- **Dark brown (3) reads as normal DK**, so it is confusable with *unpainted* as well as with black.

**Marker colours must be bright, saturated and unlike the character.** On DK's palette that leaves
four: **white (15), red (8), light pink (14), amber (7)**. Four buckets per boot, not five — and the
browns and dark reds that make up most of a 16-colour Kong palette are all unusable. A survey
instrument's resolution is set by what an operator can *name under motion*, not by what the palette
technically contains.

Round 2 therefore paints **both** ambiguous regions at once: `0x380..0x4FC` split in thirds
(white/red/light pink) against the brown region in amber. One boot both resolves the ambiguity and
bisects — the re-run costs nothing extra because the mistake was caught before the boot, not after.

#### Round 2 result — Roll is `0x380..0x3FC`

| action | colour | range |
|---|---|---|
| **roll** | white | **`0x380..0x3FC`** |
| crouching in a narrow place | white | `0x380..0x3FC` |
| getting hit | red | `0x400..0x47C` |
| bouncing on an enemy | red → **white** → red | spans both |
| teetering on a cliff edge | white, then loops red | spans both |
| swimming | alternates normal / white | spans `0x380..0x3FC` and an unpainted range |
| both idle animations | normal | (unpainted, as expected) |
| — | **no amber seen at all** | — |
| — | **no light pink seen at all** | — |

**Roll went from ~430 candidate indices to 32 in two boots.** For comparison, four rounds of
silhouette-guessing moved it zero.

**Three results beyond the roll:**

1. **The brown region is not used by any tested action** — nothing painted amber. Combined with
   round 1, `0x800..0x854`/`0x8AC..0x950` and `0x480..0x4FC` are both untouched by ~20 common
   actions. Those are the first ranges to suspect of being *not DK*, like `0x858..0x8A8` (A.13).

2. **Jump is now cornered.** It was not amber here and not white in round 1, so it is drawn from a
   confirmed or characterised range: `0x8C..0xDC`, `0xE0..0x12C`, `0x130..0x17C`, `0x188..0x1FC` or
   `0x330..0x37C`. Idle, Walk and Run are independently confirmed as other things, which leaves
   **`0x130..0x17C`** — whose 40→72→36 height arc reads exactly like crouch, apex, landing. The
   "climb" reading (A.20) is probably wrong and this is probably Jump. One paint round settles it.

3. **Animations interleave across ranges.** The enemy bounce goes red→white→red and the cliff teeter
   starts white then loops red, so `0x380..0x3FC` and `0x400..0x47C` are a shared *pool*, not one
   animation each. Swimming alternates between a painted and an unpainted range. **A contiguous
   index range is not an animation** — gotcha 2 again, now with direct in-play evidence rather than
   draw-order inference.

The contact sheet for `0x380..0x3FC` shows horizontal, arm-forward poses in `0x380..0x3DC` (which
matches the swim sighting and the old provisional Swim guess of `0x3A4..0x3DC`) and upright poses in
`0x3E0..0x3FC`. **Prediction, recorded before the boot so it can be scored:** roll is
`0x3E0..0x3FC`. Round 3 splits the 32 into four 8-index buckets to check.

#### Round 3 contradicted itself, and chasing it found a second colour failure

Round 3 reported **roll unchanged** — impossible, since it painted all 32 indices that rounds 1 and 2
both pointed at. The ROM was verified first (32 sprites in four clean bands, 18 KB from stock), which
ruled out the artefact and left the observation.

A **single-variable control** settled it: `0x380..0x3FC` painted one colour, everything else removed.
Roll turns white. So the mapping was right and **round 3's "unchanged" was an instrument failure**.

The control screenshot also shows *why*: DK is only **partially** painted mid-roll — a pale mass plus
normal brown. Round 3 gave `0x3E0..0x3FC` **amber**, and amber over a partly-painted brown gorilla
reads as ordinary DK.

**So amber joins the unusable list, for exactly the reason dark brown did.** The rule is not "bright
and saturated" — it is **contrast with the character**, and DK is a warm brown-and-orange character,
so every warm marker fails no matter how bright. The usable set on DK is **three**: white (15),
red (8), light pink (14). Amber was on the "legible" list in this very section one round earlier;
the lesson had been written down and still not applied, which is the same shape of mistake as A.21.

**Partial painting is itself a finding.** A rolling DK composites sprites from inside *and* outside
`0x380..0x3FC` — consistent with the swim sighting (only the reaching arm turned white) and the
enemy bounce crossing ranges. Expect a mid-animation DK to be several sprites from several ranges,
and expect a paint result to be a *mixture* rather than a clean silhouette.

Round 4 tests the standing prediction with two colours only: `0x380..0x3DC` red against
`0x3E0..0x3FC` white.

#### Round 4: the prediction was wrong, red is unusable, and only white survives

Result: **roll normal, and only the cliff-teeter's first frames white.** Combined with the control
this is decidable without another boot:

| boot | painted white | roll white? |
|---|---|---|
| control | `0x380..0x3FC` (all 32) | **yes** |
| round 4 | `0x3E0..0x3FC` only | **no** |

⇒ roll's frames are in **`0x380..0x3DC`**, which round 4 painted *red* and the operator read as
normal. **Red is unusable too.** Three warm markers have now failed — dark brown, amber, red — and
the reason is the same each time, compounded by partial painting: a warm marker over part of a warm
character is not a signal. **On DK the only reliable marker is white.** One trustworthy bit per boot
beats three buckets that lie.

**The prediction is scored and it failed.** `0x3E0..0x3FC` was called for roll on the strength of
its upright poses; it is the cliff-teeter start, matching the operator's round-2 sighting exactly.
The horizontal arm-forward poses in `0x380..0x3DC` — read here as "swim-like" — are the roll, which
in hindsight is obvious: a DKC1 roll *is* a horizontal tumble. **That is the fifth time reading
silhouettes has produced a wrong answer in this spec** (A.16 ×2, A.20, A.21, A.22). The record is
now unambiguous enough to state as a rule rather than a caution: *do not use contact sheets to
predict identity, only to describe geometry after the mapping is known.*

Worth noting what the survey got right anyway: every *boot* result has been consistent. Rounds 1, 2
and the control all pointed at ranges containing the roll; only the interpretations layered on top —
colour legends and silhouette predictions — have failed. **The instrument is sound; the readings off
it were not.**

Roll is down to 24 indices. Round 5 bisects white-only: `0x380..0x3AC` painted, `0x3B0..0x3DC` left
alone.

#### Rounds 5–6: white-only bisection works, and it is boring, which is the point

Round 5: **everything normal**, including the cliff teeter — which is *consistent*, since round 5
left `0x3E0..0x3FC` (the teeter) unpainted. Two independent checks agreeing on a null is what a
working instrument looks like.

⇒ **roll is in `0x3B0..0x3DC`** — 12 indices.

The operator also pinned the teeter's opening frames precisely: **DK's eyes stretch downward as he
looks over the edge**. That is a description of art, from play, and it is worth more than any
contact-sheet reading in this spec — it identifies the animation *and* its phase.

**`0x3A4..0x3DC` was the provisional Swim** (A.14). It overlaps roll's remaining range, so that
pairing is at best partly wrong — the fourth provisional to fall. The provisional tier is now 0 for 4
whenever it has been tested.

**Note how the last three rounds have gone.** No cleverness, no predictions, one colour, one
question, one bit. Roll went 430 → 96 → 32 → 24 → 12 without a single wrong turn, after five rounds
of clever methods produced five wrong answers. Round 6 paints `0x3B0..0x3C4`, leaving `0x3C8..0x3DC`.

#### Round 6 broke the method: four negatives cover one positive

Both halves of the remaining 12 came back negative. Tabulated against everything else:

| boot | painted white | roll white? |
|---|---|---|
| control | `0x380..0x3FC` (32) | **yes** |
| round 4 | `0x3E0..0x3FC` (8) | no |
| round 5 | `0x380..0x3AC` (12) | no |
| round 6 | `0x3B0..0x3C4` (6) | no |
| round 6b | `0x3C8..0x3DC` (6) | no |

The four negatives **partition the control's range exactly**. They cannot all be true. Bisection has
been assuming a negative is a clean exclusion, and at this granularity it is not.

**The likely cause: white is one of DK's own colours.** His muzzle, hands and chest are pale, so a
white-painted *subset* looks like his normal light areas. With all 32 painted he went obviously
white; with 6–12 painted, the marker hides inside the character. This is the same failure as amber
and dark brown — *marker resembles character* — and white escaped it only while the painted fraction
was large. **Every solid fill is some DK colour, because the palette is DK's.** There is no safe
marker colour at small fractions, only a safe *fraction*.

**So paint does not scale down, and poison does.** Noise is not a colour, it is a texture, and
nothing on a Kong looks like high-frequency multi-colour static — the operator described the poisoned
run unprompted as "a red and white pixel mess". Poison should have been the fine instrument from the
start; paint's advantage was only ever that it can label *many* buckets at once.

**Use paint to localise coarsely, poison to confirm finely.** Rounds A–C repeat the last split as
poison over thirds of `0x380..0x3FC`.

**And the honest possibility that the control itself was misread:** it painted `0x3E0..0x3FC` too,
which round 4 identified as the cliff teeter. If the operator was near an edge, the white DK in that
screenshot may have been a teeter frame rather than a roll frame. The poison rounds settle that as
well — `dk-poison-c.sfc` covers exactly that range.

#### The fix: invert the test — paint everything *except* the candidate

The operator's observation, and it is the one that repairs the method: *"it worked well when DK went
all white, black, red — why not do it that way again?"*

Exactly right, and it identifies the real variable. Those rounds worked because the painted range
covered **every sprite DK was composited from**, so he went uniformly coloured. Shrinking the range
breaks that property, and the failure was never about *which* colour — it was about **what fraction
of the character carries the marker**.

So keep the fraction at 100 % and make the candidate a **hole**: paint DK's entire block *except*
the range under test. The question inverts from "is any part of him white?" — which fails, because
a white patch hides among DK's pale muzzle and hands — to:

> **Is any part of him still brown?**

A brown patch on a uniformly white DK is the same unmistakable signal the coarse rounds had, and it
does not degrade as the candidate shrinks. Better still, **the test is self-validating**: during any
animation that does *not* use the candidate, DK must be 100 % white. If he shows brown during idle
or walking, the premise is broken (sprites from outside the painted block) and the operator sees
that immediately rather than reporting a false negative.

`0x858..0x8A8` is left unpainted deliberately (A.13 — not DK), so a white Manky Kong cannot be
mistaken for signal.

**The general lesson, which is worth more than the roll.** Five rounds were spent tuning the
*marker* — brown, amber, red, white, noise — when the broken variable was the *coverage*. When an
instrument works at one scale and fails at another, suspect the thing that changed with scale, not
the thing you have been adjusting. It took an operator asking why the early rounds worked to see it.

#### The inverted test cleared the range — and indicted every paint result for roll

All three holes: **DK stayed white through the roll.** They partition `0x380..0x3FC` exactly, so
**the roll uses nothing in it** — contradicting both round 2 and the control.

**One mechanism explains every contradiction, and it is the one already half-identified: white is a
DK colour, and that cuts both ways.** It was written up as causing false *negatives* (a white patch
hiding among his pale muzzle and hands). It causes false *positives* just as easily — during a roll
DK's hands and muzzle are prominent and naturally near-white, so *"DK turns white when rolling"* may
never have been the paint at all.

That retro-fits the whole sequence: the paint positives (round 1 "black", round 2 "white", the
control) are the unreliable observations, and the negatives — which required noticing *nothing* —
were right. **Roll is not localised at all.** Rounds 1–6 for roll are withdrawn.

**Scoreboard, because it is the only defence against repeating this.** For the roll specifically:
paint produced 3 positives, all now believed false. Poison produced 1 negative (`0x188..0x1FC`),
never contradicted. Holes produced 3 negatives, mutually consistent. **The colour-based instrument
has never once produced a roll result that survived.**

**Where the paint survey still stands:** the ~15 *other* localisations in the A.22 table used the
same instrument and deserve the same suspicion — but most were reported as *non-white* colours
(red, pink), which do not collide with DK's palette the way white does, and several were
cross-checked (the teeter's white start was independently re-seen in round 4 with the eye detail).
Treat white sightings as suspect and coloured sightings as provisional.

**Next**: `dk-poison-wide.sfc` poisons all of `0x380..0x4FC` — the original round-1 chunk — in one
boot. Garbage during a roll means the region is real and only the fine-grained paint work was wrong;
a clean roll means the region was never right and the search restarts across DK's whole block with
poison, which is the only instrument here with an unbroken record.

#### Resolved: roll is `0x400..0x4FC`, and every observation now agrees

`dk-poison-wide.sfc`: **DK garbles when rolling.** So the region is real, and with the three holes
clearing `0x380..0x3FC`, roll is in **`0x400..0x4FC`** — 64 indices — by subtraction.

Every result in the sequence is now consistent, including the ones that looked contradictory:

| observation | verdict |
|---|---|
| round 1: roll "black" = `0x380..0x4FC` | **correct** |
| round 2: roll "white" = `0x380..0x3FC` | **false positive** — DK's own pale muzzle/hands |
| control: roll white in `0x380..0x3FC` | **false positive**, same cause |
| holes a/b/c: roll not in `0x380..0x3FC` | **correct** |
| poison-wide: roll in `0x380..0x4FC` | **correct** |

**The false positives are precisely explained rather than waved at.** Roll lives in `0x400..0x47C`,
which round 2 painted **red** — a colour later shown unreadable on DK. So during the roll the paint
was invisible, DK looked normal, and his naturally near-white hands and muzzle were reported as the
white marker. One unreadable colour plus one colour that collides with the character's own palette
produced three consecutive wrong answers that all pointed at the same wrong range.

**What actually held up across the whole search: the coarsest test.** Round 1 — five buckets, whole
chunks, the crudest possible resolution — was right. Everything that went wrong came from trying to
*refine* it. The refinements changed two variables at once (smaller ranges *and* new colours) and
the instrument's failure mode tracked the change, not the target.

**Rule for the next search, and the one this section exists to transmit:** *change one variable per
round.* Shrink the range or change the marker, never both. Had the fine rounds kept round 1's
colours, the red-unreadable problem would have surfaced immediately instead of after six boots.

**A confound, reported alongside the result: the cliff teeter garbles too.** Both roll and teeter
draw from `0x380..0x4FC` — consistent with round 2, which put the teeter's start at `0x380..0x3FC`
and its loop at `0x400..0x47C`.

That is a hazard, not just a fact. **The two animations are adjacent in play and similar when
garbled**, so a test performed near a ledge cannot distinguish them, and the control screenshot —
DK pale and low to the ground — is now more likely to have been a teeter frame than a roll frame.
Some part of the false-positive story above may be *misattribution* rather than palette collision;
both mechanisms were available and they are not mutually exclusive.

**Test the roll on open flat ground, well away from any edge.** Cheap to arrange, and it removes a
confound that has plausibly been present since round 1.

Next: `dk-poison-{d,e}.sfc` split `0x400..0x4FC` in half with the instrument that has never been
wrong here — and separate roll from teeter in the same boots, since both garble.

#### `dk-poison-d` (`0x400..0x47C`): the teeter falls, the roll stays ambiguous

| action | result | conclusion |
|---|---|---|
| cliff teeter, **start** (stretched eyes) | normal | `0x3E0..0x3FC`, as round 4 found |
| cliff teeter, **loop** | pixelated | **`0x400..0x47C`** |
| blasted out of the house (level intro) | pixelated | **`0x400..0x47C`** |
| roll | "maybe some pixels, not sure" | unresolved |

**The teeter is now pinned in two phases by two independent instruments**, and both agree with
round 2's original paint sighting ("starts white, loops red"). That is the first animation in this
spec confirmed by *method triangulation* rather than by a single boot — and it partly rehabilitates
the red channel: red *was* readable here, which narrows the round-2 failure specifically to the roll
rather than condemning every coloured sighting.

#### `dk-poison-e` (`0x480..0x4FC`): roll found, and the animation table names it

| action | result |
|---|---|
| **roll** | **pixelated** |
| shot out of the house | pixelated (spans `d` and `e`) |
| bouncing on an enemy | pixelated |
| cliff teeter | **normal** — correct, it is `0x3E0..0x47C` |

**Roll is `0x480..0x4FC`**, 32 indices, and the teeter's clean result is a built-in control:
the instrument distinguished two animations that garble in the neighbouring range.

`--anims-in` then names the candidates exactly, which is the authoritative draw-order route
(gotcha 2) rather than another guess:

| anim | frames | range |
|---|---|---|
| **23 / 101** | 16 | `0x478..0x4B4` |
| 24 | 11 | `0x4B8..0x4EC` |

**Anims 23/101, on two independent grounds.** They include `0x478`/`0x47C`, which are inside
`dk-poison-d` — exactly explaining the operator's "maybe some pixels, not sure" on that boot, since
2 of 16 frames were poisoned. Anim 24 lies wholly inside `e` and would have produced *nothing* in
`d`. And 23/101 is a **pair of animation entries over one range**, the shape every confirmed DK move
has (Idle 4/108, Walk 3, Run 2/14/20); anim 24 is unpaired.

**The uncertain report turned out to be the decisive datum.** "Maybe some pixels" looked like noise
at the time and was worth more than either clean answer — it located a boundary crossing. Worth
remembering before pushing an operator to resolve an ambiguous observation into a yes or no.

The contact sheet for `0x480..0x4DC` shows unmistakable tumbling — curled, inverted, rotating. That
reading is *permitted here* under the rule from A.22: the range was established by boot first, and
the sheet is only describing geometry afterwards.

**The roll's ambiguity does not need resolving directly.** Roll is already known to be in
`0x400..0x4FC`, and `d ∪ e` partitions it, so `dk-poison-e` decides by elimination whichever way it
lands. **A clean result there is as informative as a garbled one** — which matters, because it
removes the operator's judgement call about faint pixels entirely. Designing the next test so that
*both* outcomes are decisive is the fix for an uncertain report, and it is cheaper than asking for a
more careful look.

**The methodological result.** Every previous round asked one yes/no question and mostly got "no".
This round asked an operator to *play the game and report colours*, and returned ~15 localisations,
two structural corrections and a falsifiable lead on jump. The change was not a better hypothesis —
it was making the instrument report which of several buckets fired, and then getting out of the way.

### A.23 Jump confirmed at `0x130..0x17C` — and it is a shared *pose arc*, not an animation

The complement-paint round A.22 called for. DK's whole block white **except** `0x130..0x17C`:

```
--paint 0x8C..0x12C:15 --paint 0x180..0x854:15 --paint 0x8AC..0x950:15 --out port/dk-JUMPHOLE.sfc
```

521 sprites painted, 20 left unpainted, 21 skipped as Manky (A.13). 521 + 20 + 21 = 562, so the
arithmetic closes against A.8's block and nothing was missed. Operator report:

| action | DK | reading |
|---|---|---|
| **jump** | **brown** | draws from the range |
| **jump carrying a barrel** | **brown** | " |
| **falling off a ledge** | **brown** | " |
| walk, run, duck, roll | white | control holds |
| jump onto an enemy | white | the bounce is a *separate* airborne set |
| shot out of the house | white | the barrel-blast roll is separate too |

**First unambiguous positive the paint instrument has produced**, and the first range identified
without a bisection. Six white actions — three of them independently confirmed ranges — are the
positive control, so the negatives are load-bearing rather than "we saw nothing".

**Jump is `0x130..0x17C`. The A.20 "climb/hang" reading is dead**, and the elimination that predicted
this (not amber in round 2, not white in round 1, Idle/Walk/Run independently placed) held.

#### The structural finding: eight animations share one ordered arc

`--anims-in 0x130..0x17C --frames N` for each length:

| anim | frames | indices | direction |
|---|---|---|---|
| 5 | 20 | `0x130..0x17C` | forward, the whole range |
| 7 | 18 | `0x138..0x17C` | forward, minus the first two |
| 21 | 10 | `0x158..0x17C` | forward, second half |
| 81 | 7 | `0x158..0x170` | forward |
| **8 / 102** | 6 | `0x178..0x164` | **reverse** |
| 82 | 5 | `0x174..0x164` | **reverse** |
| 104 | 12 | this range **+ `0xE0`** | excluded — reaches into Walk |

Read against `--baseline`'s height profile over the same range — 40 → 72 (held six frames) → 36 → 42,
feet pinned within 6 px while the top edge rises 38 px — the range is **one monotonic pose arc**:
crouched at `0x130`, fully extended around `0x144..0x158`, tucked tightest at `0x178`. Every
animation above is a *sub-span of that arc*, entered at a different point and walked in one direction
or the other. The three actions the operator saw fall out of it directly: a full jump traverses it
from the crouch, a fall off a ledge starts already extended (anim 21 begins mid-arc, which is what a
ledge-fall is — no launch), and the reverse-order pairs are the landing uncurl.

**8/102 is a pair over one range** — the shape every confirmed DK move has (Idle 4/108, Walk 3,
Run 2/14/20, enemy bounce 23/101). That the pattern reappears unprompted here is corroboration.

**This changes the manifest's unit.** Every entry so far assumed strip → animation, and the manifest
happens to list explicit indices, which is what saves it: importing 20 poses into `0x130..0x17C`
retargets **all eight** animations at once, because they share the sprites. That is a large coverage
win per import — but it also means two strips can never be assigned to two animations in this range.
**The unit of import is the pose arc, not the animation.** Check for a shared arc with `--anims-in`
before assuming a range belongs to one move.

#### It lands squarely on the M5 problem

Strip 10 is captioned **"Jump"** and has **19 poses**. The arc has **20** indices. Off by one, so the
importer's length check refuses — this is A.9's scope finding arriving on the very next range mapped,
and it is exactly what M5 (animation-script editing) exists for.

There is a cheap non-M5 option here that there was not for other strips: drop **`0x130`**, the
shallowest crouch, and import 19 poses into `0x134..0x17C`. `0x130` is used by **anim 5 only** —
every other animation on the arc starts at `0x138` or later — so one stale frame costs the first
frame of one animation and nothing else. Whether the sheet's 19 poses actually begin at the *second*
crouch is an assumption about the artwork and must be checked on the slice, not asserted.

> **Checked, and the premise was false — see A.24.** The strip has **20** poses; the slicer merged
> two of them across a 3 px gap. There is no mismatch, no escape needed and no M5 dependency here.
> The check also found the same defect in 21 of 46 strips and took A.9's negative result down with
> it. Left above as written because the *reasoning* was sound — it was the input that was wrong, and
> that is the point: the assumption flagged as needing a check was not the one that broke.

### A.24 The escape was not needed — the slicer under-counts poses, and it contaminated A.9

Checking A.23's "drop `0x130`" escape against the sheet dissolved the problem it was solving.

**Strip 10 "Jump" has 20 poses, not 19.** The slicer's pose 9 is a 117 px rect holding *two* 57 px
DK figures separated by a **3 px gap**. Verified three ways: visually (two unmistakable Kongs in the
crop), by opaque-column runs over the band (20 runs, and pose 9's rect splits 485..541 / 545..601),
and by the ROM — 20 poses is exactly the arc's 20 indices, and the strip's caption reads "Jump".

So there is **no length mismatch, no stale frame, and no M5 dependency for this strip**. It imports
1:1. The escape, and the assumption it rested on, are both moot.

#### Root cause: the merge gap is wider than the sheet's real gaps

`SheetSlicer.MergeGap = 4` joins two flood-fill components whose bboxes are within 4 px. It exists to
reattach a *fragment* to its figure — a hand clear of the body across transparent pixels. But this
sheet lays poses out **1–3 px apart**, which is inside that radius, so wherever the author packed two
figures tightly the slicer emits them as one pose.

Surveying every pose rect for the specific signature — splits into ≥2 opaque runs, each ≥60 % of the
strip's median pose width, which a genuine internal gap cannot produce because it leaves one wide
part and one narrow one:

**39 merged rects across 21 of 46 strips, ≥89 poses missing.** Every gap measured 1–3 px. Examples:

| strip | rect w | splits into | gaps | count |
|---|---|---|---|---|
| 10 "Jump" | 117 | 57 + 57 | 3 | 19 → **20** |
| 21 | 533 | 8 × 64 | 3 | 6 → **13** |
| 11 | 263 | 5 × ~50 | 3 | 13 → **17** |
| 20 | 622 | 10 × ~60 | 1–3 | 7 → **16** |

**89 is a lower bound**, for two reasons. The heuristic only counts full-width parts, so a merged
fragment is missed. And strip 26's "pose 0" is a 677 px rect that the survey scored as 2 parts but
which is visibly **~30 figures across two rows** — there the *row-band* detection failed, not the
merge gap, so that is a second and separate defect this survey does not measure.

⚠️ **A.3's "automatic segmentation works, at about 96 %" is withdrawn.** It counted strips that
looked plausible, not figures. The real figure-level rate is at most ~85 % and the true number is
unknown until the band defect is measured too.

#### The damage: A.9's negative result does not survive, and both rows fail differently

A.9 concluded frame-count matching "does not narrow toward the right answer — it excludes it", from
two rows. **Both are now known to be broken, and in both the right answer was inside the
count-matched set:**

| A.9 row | what it claimed | what is now known |
|---|---|---|
| strip 6 "Walk", 20 poses → anims 2, 3, 5, 14, 20 | "**none** correct; the real walk is anim 4/108, 21 frames" | **Anim 3 *is* the Walk**, confirmed in play, and it is right there in the list. A.9 judged it against the `0x8C..0xDC` pairing that **A.16 later falsified** — that range is Idle. |
| strip 10 "Jump", 19 poses → anim 74, a *unique* match | "**no**, anim 74 is the hand-slap" | The **count was wrong**. At the true 20, anim 5 — the Jump confirmed in A.23 — is in the candidate set. The spurious uniqueness at 19 is a slicer artefact. |

A.9 even anticipated the objection and dismissed it: *"The 20 is not a slicing artefact — the band
genuinely ends after 20 poses, checked at the right edge."* True and irrelevant — the strip's *ends*
were right, and the merge was in the *middle*, where nobody looked.

**Withdraw the negative result.** What survives is much weaker and was already in `--stats-anim`:
count matching is a **filter, not an identification** — a 20-pose strip still matches 5 animations,
an 8-pose strip matches 52, and only 21 of 51 counts are unique. It does not select the right answer;
it just no longer stands accused of excluding it. **Gotcha 3 ("0 for 2/0 for 3") is void** — those
were never clean tests. Confirmation still comes only from a boot (gotcha 1), which is untouched.

**This also undercuts M5's justification.** "8 of 46 strips have pose counts no DK animation has" was
computed from these counts, and 21 of 46 strips have wrong ones. That number must be recomputed
before M5 is scoped on it.

#### What is safe, and what the fix is

**The three confirmed imports are unaffected.** Strips 6 (Walk), 7 (Run) and 8 (Roll) contain no
merged rects, so their pose numbering — the thing the manifests address — is already correct. Nothing
that has been booted needs revisiting.

**The fix is not "lower `MergeGap`".** The gap exists to absorb fragments, and shrinking it would
start splitting figures whose limbs clear the body. The rule that matches the intent is: **never
merge two components that are both pose-sized, however close they are.** Absorption should be
asymmetric — a small component joins a large neighbour; two large ones are two poses.

That change renumbers poses in 21 strips, which is exactly what the class doc warns about
("a change here silently retargets every existing manifest") and what V4a's golden slice list pins.
It needs the golden rebuilt deliberately, in its own change, with the 21 strips' new counts reviewed
against the sheet — not folded into other work.

### A.25 The slicer fix: absorption is asymmetric

A.24's defect, fixed. `SheetSlicer.FragmentRatio = 0.5`: a component within `MergeGap` is absorbed
into its neighbour **only if it is under half the neighbour's opaque pixel count**. A fragment is a
small share of its figure; two adjacent poses are comparable. Compared on pixel count rather than
bbox area, because a figure's bbox is inflated by whichever limb reaches furthest.

| | DK sheet | DK Jr sheet |
|---|---|---|
| strips | 46 → **51** | 42 → 42 |
| poses | 533 → **678** | 542 → **553** |
| over 88-char budget | 16 → **0** | — |
| over 256×256 canvas | 5 → **0** | — |

**Six independent checks, because renumbering poses is the one change this spec says to be afraid
of:**

1. **The merge survey goes to zero.** `port/mismerge-survey.py` found 39 merged rects before, 0
   after, on *both* sheets.
2. **No over-correction.** The opposite failure — a figure split into parts — would leave poses much
   smaller than their strip's median. 0 poses under half the median width on either sheet.
3. **The counts were predicted.** The survey named each rect's split *before* the fix existed. For
   strips 0–21 the new counts match its predictions exactly, all eleven of them: strip 0 6→11,
   5 11→19, 9 15→16, 10 19→20, 11 13→22, 13 14→20, 14 13→18, 16 17→19, 20 7→17, 21 6→13. Two
   instruments built from different evidence agreeing to the pose.
4. **Every "impossible" pose became legal.** V4b went 1041/1056 with **15 over-budget skipped** to
   **1216/1216 with 0 skipped**, and over-canvas went 5 → 0. Those poses were never over budget:
   they were several figures in one rect. The encoder had been reporting the defect all along.
5. **Strips 0–25 keep their identity** (same origin pose); renumbering starts at 26, where the
   two-row rect split. Strips 6/7/8 — Walk, Run, Roll — keep their numbers *and* their exact rects.
6. **The confirmed build is bit-identical.** `--batch dk-combined-flatall.json --dry-run` emits
   byte-for-byte the same 54 poses before and after. Nothing confirmed in play is affected.

Golden rebuilt deliberately (delete + re-bootstrap), **V4 7/7**.

#### Still broken: bands merge two rows

Fixing the merge exposed the second defect A.24 predicted, and did not fix it. A band is formed by
*vertical overlap*, so one tall pose reaching from row N into row N+1 pulls both rows into one band;
the horizontal split then cuts across both and produces strips whose poses alternate between rows.

**6 of 51 DK strips (68 poses) and 1 of 42 DK Jr strips (19 poses)** — all in bands 19–20, none below
strip 25, none used by any manifest. Their pose *rects* are now correct; only the strip grouping and
`Position` order are wrong.

This is a redesign, not a tweak — band membership would have to come from clustering pose tops rather
than transitive overlap, and the right grouping for a grid of small items is genuinely ambiguous.
Deliberately left. It renumbers only strips ≥25 on each sheet when it lands.

#### M5's justification survives — the recount did not go as expected

A.24 said the "8 of 46 strips have pose counts no DK animation has" figure had to be recomputed and
that M5 might shrink or vanish. Recomputed against the 27 distinct frame counts of DK-block
animations:

| | strips with no matching count |
|---|---|
| before (46 strips) | **8** — 11, 14, 15, 16, 19, 27, 35, 41 |
| after (51 strips) | **9** — 11, 15, 19, 20, 21, 37, 38, 39, 45 |

The old counts reproduce A.9's 8/46 exactly, which validates the method; the corrected ones give
9/51. **The headline number is unchanged.** The membership churned — strip 10 "Jump" left the list,
which is the case that started this — but the class is as large as it was.

**So M5 is still justified, and the prediction that it might not be was wrong.** Recorded because the
prediction was made in writing one section earlier: a correction that invalidates an input does not
necessarily move the conclusion that was drawn from it, and the tempting inference — "the input was
wrong, so the finding must fall" — is the same shape of error as the one A.24 caught. The 9 strips
carry the usual caveats: those above 25 rest on counts the band defect still disturbs, and the range
scanned includes Manky's animations (A.13).

### A.26 Stale frames: a manifest is a range, an animation is a draw order

Reported from play, unprompted, on the Walk+Run+Roll+Jump build: *"one frame at the end of rolling
where old DK appears."* Diagnosed exactly.

`anim 25` is the **roll's exit**, and its draw order is:

```
0x4E0   0x4EC   0x4EC   0xB4   0xB4
```

It rolls out of the imported `0x4B8..0x4EC` and settles on **`0xB4`** — an index in the *idle* range
that no manifest touched. Two frames of one index, so it reads as a single held frame of stock DK.
Confirmed against the ledger: 74 indices imported, `0xB4` not among them.

**Neither containment holds.** A manifest is written in index ranges; an animation is a draw order:

- `anim 25` draws **outside** the range — the range is not a superset.
- `anim 24` draws only **11 of the range's 14** indices, skipping `0x4BC`, `0x4E4`, `0x4E8` — the
  range is not a subset either.

The second is a **live defect in an import this spec calls confirmed**: three sheet poses are never
displayed, and every pose after the first skip sits at a different point in the cycle than the sheet
drew it. It plausibly explains why the roll only ever measured well and never clearly *looked*
better (A.22's hedged report).

#### The manifest's own length check would have caught it

`animation` derives indices in draw order and length-mismatches against the strip; `indices` is the
explicit escape hatch. The roll used `indices`, listing 14 contiguous slots — so the 14-pose strip
zipped onto 14 indices and **the check that exists for exactly this passed vacuously**. Written as
`"animation": 24` it would have derived 11 indices and refused against 14 poses, which is the true
state of affairs.

⚠️ **`indices` disables the strongest check in the manifest.** C.2 calls it "the explicit escape
hatch when no single animation matches"; it is also an escape from verification. Prefer `animation`
wherever an animation is known, and treat an `indices` list as an assertion no tool is checking.

That the two 14s agreed was the frame-count trap (A.9) arriving through a *range* instead of a count,
and getting past a reader who had already learned to distrust counts. **A range's size is a frame
count in disguise.**

#### `FindStaleFrames`, and what it says about the whole build

`BatchImporter.FindStaleFrames` reports every animation drawing both imported and stock indices,
ordered by fewest missing, on `--dry-run` as well — catching this before a boot is the point. A
warning, never a refusal: importing one animation at a time is the normal workflow, so straddling is
expected until a character is finished.

Over the 74-index Walk+Run+Roll+Jump build it reports **exactly one animation — anim 25 needs
`0xB4`** — the single observed defect and no false positives.

**Not fixed, and the reason is the interesting part.** `0xB4` is also frame 11 of the idle (anims
4/108, 21 indices). Importing it alone trades a stale frame in the roll, seen rarely, for a lone
new-art frame in the idle, which plays constantly — a worse deal. The clean fix is the whole idle
run: **21 indices against sheet strip 0's 11 poses.** That is an M5 case, and a far more concrete
argument for M5 than the strip-count survey (A.25) — a specific artifact an operator saw, traced to
a specific animation, blocked on a specific missing capability.

#### Fixed, and the fix restores the check rather than working around it

`0x4BC`, `0x4E4` and `0x4E8` are drawn by **no animation in the 438-script table** — dead slots.
(Verified with a positive control: the same probe on `0x4B8`, `0x4E0` and `0xB4` names the animations
that draw them, so the negative is a real absence, not a broken probe.) Importing into them was pure
waste: three poses paying for ROM space that nothing can display.

The obvious fix — list anim 24's 11 indices under `indices` — would have re-committed the original
sin, since `indices` is what disabled the length check. Instead the manifest gained **`poses`**, an
inclusive `"lo..hi"` of slicer pose positions, so `animation` stays usable when a sheet strip covers
*more* than its animation does:

```json
{ "strip": 8, "animation": "0x18", "poses": "0..10", "flatX": true }
```

11 selected poses against 11 derived indices — the check is live again, and a wrong range still
refuses. The Roll now maps onto anim 24's actual draw order, and the whole build drops 74 → **71**
poses with the freed space returned to the pool.

**Which 11 was not an arbitrary art call.** The sheet's Roll strip runs 45, 37, 32, 32, 33, 39, 32,
34, 40, 43, 41, **44, 46, 47** — the last three climb steadily, DK standing back up. Anim 24 stays
curled throughout (29–40 across all 11 frames), and the standing-up is a *different* animation: anim
25, which is precisely the one ending on `0xB4`. The strip covers the tumble **and** the recovery;
the ROM splits them across two scripts. So poses 0..10 are the tumble and 11..13 are the recovery
that has nowhere to go until the idle is imported.

⚠️ **`animation` is parsed as hex, and everything else in this project says decimal.** `--anims-in`
prints "anim 24"; the correct manifest value is `"0x18"`. A bare `"24"` silently selects anim 36.
This was hit while writing the entry above, one section after a gotcha about ranges silently meaning
the wrong thing. `animation` now **refuses** any value without a `0x` prefix and its message does the
conversion for you.

#### Method note

The operator could not answer the question that was asked — whether the jump floats — because the
comparison pose is *itself* stock art, so there was nothing to judge it against. They answered a
question nobody asked instead, and that answer was worth more than the one requested. Gotcha 11 says
to ask for a positive, unmistakable observation; this adds that **an operator playing an
half-imported character will notice the seams between imported and stock art whether or not you ask
about them**, and those seams are exactly where the defects are. Ask what looked *wrong*, not only
what was predicted to look wrong.

### A.27 The idle did not need M5 — repeat a pose instead of rewriting a script

Two operator reports closed one loop and opened this one.

**First: "after rolling while running and continuing to run, old DK appears for a frame between the
rolling and running animation."** That is *not* a second defect. `anim 25` is the roll's **exit**, so
its `0xB4` tail plays before whatever comes next — stand, run, anything. `FindStaleFrames` had
reported exactly one straddling animation for the whole build, and the operator has now seen that
one animation in two different contexts. **The instrument's completeness claim survived a test it
could have failed**, which is worth more than the original report.

**Second: authorisation to start M5 on the idle.** It turned out not to be needed.

#### What the two sides actually are

| | frames/poses | what it is |
|---|---|---|
| ROM `0x8C..0xDC` (anims 4/108) | 21 | DK hunched on his knuckles: ~5 settling frames, then ~16 near-identical resting ones |
| sheet strip 0, captioned "Idle" | 11 | DK standing fully upright, a breathing loop |

M5's premise is that a count mismatch forces a script rewrite. But 21 ≠ 11 does not force one here,
because the ROM's extra frames are **a slower version of the same motion, not a distinct sub-motion**.
So each sheet pose is held for two frames (the last for one):

```json
{ "strip": 0, "animation": "0x4", "poses": "0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10" }
```

21 covered, **the ROM's own timing preserved exactly**, and nothing rewritten. `poses` therefore
takes a *list*, not just a range — and the mapping is spelled out pose by pose deliberately, because
this substitution is *not* always legitimate and a reader must be able to argue with it. Where an
animation's extra frames are a real sub-motion (the roll's `0x4B8..0x4EC` recovery, A.26), repeating
would smear one pose across a movement the artist drew separately, and M5 is genuinely required.

**The stale frame is gone.** `0xB4` is imported; `FindStaleFrames` no longer lists anim 25.

#### It measures as well as anything in this spec

| | stock idle | imported idle |
|---|---|---|
| foot line | 127..129 (2 px) | **127..129 (2 px)** |
| centre X | 125..130 (5 px) | 129..130 (1 px) |
| head X, mean | 132.2 | 131.2 |

Foot line identical. Head X puts idle→walk at 0.4 px *backwards* where stock steps 1.4 px forwards —
a 1.8 px discrepancy against the 6.8 px that was plainly visible on run→walk (A.20). Recorded, not
chased: below what play resolves is exactly where this spec has agreed to stop.

#### The cost, stated rather than buried

Importing the idle made **three** animations straddle that did not before: anim 1 (315 frames — the
intro cutscene) and anims 9/113 (96 frames), which draw idle indices *and* a `0x200..` block. The
trade is a stale frame in a move the player hits constantly, against inconsistency in cutscenes.
Worth taking, but it is a trade and `FindStaleFrames` is what makes it visible instead of a surprise.

**The general shape:** every import straddles more animations until the character is finished, so the
stale-frame count is not a defect count — it is a *coverage* readout. It should fall to zero only at
the end. The pool is down to 7.6 KB, so `0x200..` needs `--expand` (M3, already built).

#### Method note

A latent crash surfaced here: `BatchImporter` keyed its alignment maps by `(strip, position)`, which
a repeated pose collides on. Keyed by plan-entry identity now, with slots read once per distinct
index. Worth noting that the feature that exposed it was one line of manifest syntax — the assumption
"a position appears once per strip" was load-bearing and unwritten.

### A.28 The coverage wall, and the captions were carrying structure all along

Four reports from one boot of the Walk+Run+Roll+Jump+Idle build:

| report | cause |
|---|---|
| "bouncing from an enemy is regular DK" | `0x478..0x4B4` (anims 23/101) never imported |
| "idle flips between both models" | the chest-beat **borrows idle frames**, below |
| "chest bumping is regular DK all the way" | `0x200..0x25C` never imported |
| "ducked idling is regular DK all the way" | the duck range never imported |

**None is a defect.** All four are coverage, and two are the same range. That is the expected shape
once a character is partly imported — and it is why `FindStaleFrames` (A.26) counts *coverage*, not
bugs.

#### Why the idle "flips": the chest-beat borrows the idle's frames

`anim 9`/`113`, 96 frames, is the chest-beat, and its draw order is three parts:

```
0xB0 0xAC 0xA8 ... 0x8C     10 idle indices, REVERSED   -- rising out of the hunch
0x200 ... 0x25C             74 frames of pounding       -- 0x218..0x23C looped and reversed x3
0x8C 0x90 ... 0xB0          10 idle indices, forward    -- settling back down
```

The idle indices are imported and `0x200..` is not, so DK rises as the new model, pounds as the old
one and settles as the new one. **An animation reaching into a *neighbouring* range is now the second
distinct way a range has failed to be an animation** — A.23 had one arc shared by eight animations,
A.26 had an animation escaping its range, and this is an animation borrowing another animation's
range wholesale. The generalisation is simply: **ranges are storage, animations are draw orders, and
there is no relationship between them worth assuming.**

#### The captions encode structure, not just names

Strip 3 is captioned **"Bang Chest"** — and also, in smaller text the caption survey never used,
**"(Loop and Reverse)"** and **"(Reverse to return to idle)"**, with a drawn vertical rule splitting
its 15 poses 10 | 5.

Read against the ROM, those annotations *are* the pose→frame mapping:

| sheet | ROM |
|---|---|
| "(Reverse to return to idle)" | anim 9 plays the idle indices reversed on the way in, forward on the way out |
| "(Loop and Reverse)" | `0x218..0x23C` played forward then backward, three times |
| the 10 \| 5 rule | wind-up/recovery vs the pounding loop |

**A.10/A.11 read the captions for identity and stopped.** They also carry timing, looping and
direction — precisely the information the strip→animation mapping has been reconstructing from boots
and paint rounds for six sections. Worth a systematic re-read of every strip's *secondary* caption
before mapping another animation by hand.

It also settles the sheet side of a standing blind spot: strips 22/23/24 are captioned "Rope Idle",
"Rope Climb", "Rope Turn". The ROM side is still unknown.

#### The wall

The stock pool is down to **7.6 KB** after 92 poses. `0x200..0x25C` alone is 24 indices ≈ 24 KB, so
every remaining animation needs `--expand` (M3, built and gated at 6/6 but **never yet used for real
work**). Coverage from here is an expanded-ROM exercise, not another one-off strip.

### A.29 The secondary captions, read — the sheet documents its own timing

A.28 spotted one structural annotation. A full sweep of the DK sheet finds a small, consistent
**vocabulary**, and it answers questions this spec has been paying boots to answer.

⚠️ **Reading them at all requires compositing over white.** The captions are pure black
(`AnnotationRgb`) on transparency, so anything that flattens alpha to black — `sips -z`, and the
`--captions` render at default settings — makes them *invisible* rather than obviously missing. A
whole class of information was on the sheet, in plain text, and every previous look flattened it to
black-on-black. `port/flatten-region.py` composites properly.

#### The vocabulary

| annotation | meaning | ROM counterpart |
|---|---|---|
| **(Loop and Reverse)** / **(Reverse and Loop)** | play forward, then backward, repeat | anim 9's `0x218..0x23C` forward-then-back ×3 |
| **(Reverse to return to idle)** | this segment is played backwards to exit | anim 9 plays the idle indices reversed in, forward out |
| **(Loop)** | segment repeats (on *Death*) | — |
| **(Hold)** | hold this frame | a long frame duration |
| **(Also used in Bonus Games)** | **the same art serves two contexts** | the ROM's aliasing / "one action, two animations" |
| **vertical rule** | a segment boundary — *within* a captioned run as well as between runs | the sub-motions a script switches between |

**`--slice` cannot see any of this.** Vertical rules are thin black marks the slicer drops as
caption dust, and the annotations are separated from their strip by whitespace. Every one of them
has to be read by eye — but they only have to be read *once*.

#### The captions, in sheet order

Idle | Turn | Swap (Partner) · **Bang Chest** [(Reverse to return to idle), 2 rules] | Swap (Leader) ·
**[(Loop and Reverse)]** · Walk · Run · Roll · Flip · Jump · Duck [rule] · Start Crawl · Crawl ·
Hit | Death [(Loop)] · Barrel Pick Up | Barrel Idle | Barrel Walk · Barrel Throw · Steel Keg Ride ·
Ride Look | Ride Attack | Ride Idle · Ledge [rule] · Ground Slap ·
Rope Idle | Rope Climb | Rope Turn · Map Stuff · **Swing** · Minecart Idle | Minecart Up and Down ·
Swim · **Victory** · Sad/Failure · **Intro Cutscene** [(Reverse and Loop) ×2] ·
**End Credits** [(Also used in Bonus Games), (Reverse and Loop) ×2, (Hold)] ·
**End Credits Part 2** [(Reverse and Loop) ×2]

#### Two structural facts that change earlier conclusions

**1. Long runs wrap onto the next row.** *Swing*, *Victory*, *Intro Cutscene*, *End Credits* and
*Map Stuff* each continue on a second (or third) row, and **Bang Chest's loop is on the row below its
name**. That is the answer to the band question A.25 left open and could not resolve from geometry:
the two-row strips are **not** two animations stacked, they are *one run wrapping*. The slicer's band
grouping is wrong there in a specific, now-known way, and the fix is not "split the rows" but "join
them in reading order".

It also means **A.28's reading of strip 3 was incomplete**: "(Loop and Reverse)" belongs to the row
*below* Bang Chest, so the chest-beat is strip 3 **plus** strip 5 — 15 + 19 poses across two rows,
matching anim 9's wind-up / loop / recovery structure far better than 15 poses alone.

**2. Frame counts were never going to match, and now we know the mechanism.** A.9 blamed the author's
taste ("removed frames that were the same played in reverse"). The sheet says so *explicitly*, in
writing, on the strips where it happens: a run marked **(Loop and Reverse)** stores N poses for an
animation that plays 2N−2 frames. That is not a mismatch to be resolved by script editing — it is a
**compression the sheet documents**, and the importer should expand it the way A.27 expands a held
pose.

**The lesson.** Six sections reconstructed timing, looping and direction from boots, paint rounds and
poison bisections. The artist had written it down. **Before building an instrument to recover
information, check whether the source already states it** — and check that "the source doesn't say"
isn't really "our renderer drew black on black".

### A.30 `--expand`'s default was the configuration its own gate proves is dead

The first real use of `--expand` produced an 8 MB ROM that **black-screened in every emulator**, while
`--verify-m3` reported 6/6 and `--verify-m4` 7/7.

The cause, in the gate's own words. `--verify-m3` X1:

> **8 MB ExHiROM, size byte + checksum fixed, NO low-bank mirror** — expect: differs, *"the console
> resets into the zero-filled half and dies"*. **PASS** (documents why "append banks and bump the
> size byte" does not work)

Every gate that expects a *working* ROM — X2, X3a, X3b — builds it with `$00:8000-FFFF` mirrored to
`0x408000`. And the CLI read the mirror as opt-in:

```csharp
bool expMirror = Array.IndexOf(args, "--mirror") >= 0;   // default: false
```

So `--expand` with no flags built X1. The knowledge was not missing, was not wrong, and was not even
undocumented — it was asserted by a passing gate three lines from the code that contradicted it.

**Fixed:** mirroring is the default (`Expansion.MirrorLowBankByDefault`), `--no-mirror` opts out and
warns that it builds the non-booting form. Verified the whole way through: `--expand` with no flags,
then the 116-pose batch, is now **byte-identical** to the build confirmed booting under snes9x.

#### Why every gate missed it

- **M3's gates test `Expansion.Build(...)` directly**, with the mirror passed explicitly. They never
  exercise the CLI, so the CLI's default was outside every gate.
- **M4's gates never expand.** V4c/V4d import into the stock pool by design (C.3: "`--batch` never
  expands"), so 7/7 says nothing about expanded output.
- **X1 passing is what made it invisible.** A gate whose success condition is *"this ROM is broken"*
  reads as reassurance in a summary line. `--verify-m3: 6/6` counted the proof-of-brokenness as one
  of its six.

**The lesson, and it is not "add a gate".** There was already a gate; the defect lived in the gap
between what the gate constructed and what the tool constructs. **Gate the artifact the user actually
gets** — for anything with a CLI default, the default is part of the artifact. This is the same shape
as A.26, where the manifest's length check passed vacuously because `indices` bypassed it: in both
cases a real check was aimed at something adjacent to the thing that broke.

⚠️ **Still not gated:** nothing boots the output of `--expand`'s default path. The check that caught
this was run by hand (`--emu-boot`, then counting non-black pixels). A "does the shipped default
boot" gate is the obvious next addition and does not exist yet.

#### Operator-facing note

An expanded ROM changes size and map mode, so **emulator save states made against the 4 MB LoROM will
not load** — states embed the memory map. SRAM (`.srm`) is unaffected. Delete the save-state directory
after the first expanded build.

### A.31 Is expansion actually necessary? Measured: yes, overwhelmingly

Fair challenge from the operator — *"why ExHiROM at all? why can't you just swap the animations?"* —
because the importer **never writes in place**. `SpriteImporter.Import` always allocates fresh space
and repoints, even when the new sprite would fit the bytes it replaces. That is deliberate (it is
what makes `--revert` byte-exact, V4g, and it sidesteps aliasing), but whether it is *necessary* had
never been measured. `--batch` now measures it:

```
IN-PLACE FIT : 1/120 pose(s) would fit their existing slot (1 %)
  too big for the slot : 119
  aliased (unsafe in place, whatever the size) : 0
  new art 116.662 B vs 84.354 B replaced; free space still needed: 115.804 B
```

**One pose in 120.** The new art is **38 % larger** than the art it replaces, and the reason is
inherent rather than incidental: the sheet draws DK upright and full-height where stock draws him
compact on his knuckles, so nearly every pose covers more 8×8 cells. No amount of allocator cleverness
recovers that.

Even granting a perfect in-place path, the remaining 119 poses still need **115.8 KB** against a
**92 KB** stock pool — so **expansion is required for this sheet whatever the write strategy is**,
and would have been required at roughly 100 poses even if in-place worked flawlessly.

Aliasing turned out to be a non-issue here (0 of 120), which is worth knowing: it was one of the two
stated reasons for the allocate-and-repoint design, and on this manifest it never applies. The
byte-exact-revert reason still stands on its own.

**Recorded because the question was right even though the answer was no.** The design had two
justifications and neither had a number attached; one of them (aliasing) turns out not to bind at
all, and the other is now backed by a measurement rather than an assumption. A challenge that
confirms a decision is worth as much as one that overturns it — and it cost one commit.

### A.32 The roll kept its stock art because the mirror is a snapshot

**Confirmed fixed in play.** Symptom: DK's roll showed the stock model through three successive
imports while walk, run, jump, idle and the chest-beat all updated correctly.

Two paint rounds located it, and the *pair* is what made them decisive:

| build | roll reads | conclusion |
|---|---|---|
| complement paint, hole at `0x4B8..0x4EC` | **brown** | draws from the hole… or from outside DK's block |
| DK's entire block painted, no hole | **white** | inside the block — so the hole reading was a hit |

with walking, running and jumping white in both as the control. **anim 24 was right all along and the
slots were correct** — which meant the bug had to be in the write path, not the mapping.

#### The cause

The low-bank mirror (A.30) is a **snapshot taken at expansion time**, and `GfxTable.BaseAddress`
resolves to `0x3BCC9C` — *inside* a mirrored upper half. So an import rewrites the real table entry
and leaves a stale copy at `0x7BCC9C + index` holding the pre-import address. Read through bank `$BB`
the game finds the new sprite; read through `$3B` it finds the old one — which is exactly why the
failure hit some animations and not others.

`Rom.RefreshLowBankMirror()` re-syncs before every save. It is safe against the allocator **by
construction**: `ExtendedRuns` hands out the lower half of each extended bank and the mirror only
writes upper halves, so re-copying can never overwrite an imported sprite. That separation was made
in A.30 for an unrelated reason and turned out to be load-bearing here.

#### `--refs`, and how nearly the evidence was thrown away

`--refs <lo>..<hi>` counts where each sprite's 24-bit address appears. One hit is its table entry; a
second is a duplicate reference.

The first theory was a *second reference path* (a DMA list or second table). `--refs` on the roll
returned **1 reference per index — theory refuted** — and it was nearly abandoned there. The answer
was in the same output, in the rows for the three indices we deliberately **do not** import
(`0x4BC`, `0x4E4`, `0x4E8`): those were the only ones showing a duplicate, at `0x7BD158`, `0x7BD180`,
`0x7BD184`. The imported ones showed one hit because their *new* address existed only in the real
table — the stale mirror still held the old one, so it did not match the search term.

**The rows that refuted the hypothesis were the rows carrying the answer**, and the reason is worth
keeping: a search for "where does X appear" cannot see the places that still hold *not-X*. Searching
for the value you expect makes the stale copy invisible precisely when staleness is the bug. The
untouched controls are what made it visible.

### A.13 `0x858..0x8A8` is **not** DK — it is a foreign island (corrected)

> **This section previously concluded "DK at reduced scale". That was wrong**, and it was wrong in
> the way this spec keeps finding: a confident visual call made without a control. The error was
> caught by the project owner noticing the colours were off in the very render offered as proof.
> The original reasoning is kept below the correction, because the way it failed is the useful part.

**What the block actually is.** `0x858..0x8A8` is two ROM-contiguous groups, and neither is stored
anywhere near DK:

| indices | ROM data | ROM-adjacent to | content |
|---|---|---|---|
| `0x858..0x87C` | `0x1AF580…` | `0x12AC..0x12CC` (identical creature) | orange ape, on all fours |
| `0x880..0x8A8` | `0x2C09A6…` | `0x1094..0x109C` | same creature, arm raised — throwing |

**Most likely Manky Kong**, on evidence stronger than a palette impression. The project owner
supplied a reference rip of DKC1's Manky, and it matches structurally rather than merely in colour:

| reference | ROM |
|---|---|
| Manky on all fours, walking | `0x858..0x87C` + `0x12AC..0x12CC` |
| Manky with arm extended, throwing | `0x880..0x8A8` + `0x1094..0x109C` |
| orange fur, pale face and hands | coherent only under `Manky` |
| throws barrels | barrel sprite `0x16F0`, ROM-adjacent |

**Two pose-sets in the reference, two groups in the ROM** — that correspondence is the argument, not
the hue. A sweep of the whole table in `Manky`'s palette turns up no larger Manky anywhere else
(the big apes at `0x1210..0x1288` are DK cutscene frames), so these are not a small copy of a
full-size set stored elsewhere.

**Held at "most likely", not "confirmed", for two reasons.** Nothing in the 440 scripts reaches any
of these indices, so there is no script-level confirmation; and this block has now been identified
wrongly twice in this spec (as reduced-scale DK, then withdrawn), which is reason enough to keep the
claim one notch below certain.

**What is actually established, and it is enough for the manifest:**

- **Not DK.** DK's palette renders these incorrectly (the observation that reopened this), and the
  sprites are stored nowhere near DK's data.
- **~7 chars.** `0x858` occupies exactly `0x1AF580..0x1AF670` = 240 bytes = an 8-byte header plus
  7×32 — the header's char count and the ROM gap agree, so the decode is not truncating. DK's crawl
  frame is 21 chars. Whatever this is, it is a *small* sprite, about 24×24 px.
- **Animation-unreachable.** `--anims-in 0x1200..0x1400` returns 0 animations for the whole
  neighbouring region, so no script-level identification is available.

**A rendering artefact that misled the reading, twice.** These were shown at `--zoom 8`, which turns
a 24 px sprite into chunky blocks and makes it read as "a low-res DK". The apparent low resolution
is the upscale, not the data. Judge small sprites beside a known-size reference, not alone at high
zoom.

**Consequence, and it matters for the manifest.** A.8's "all DK, no foreign island" is **false**.
About 21 indices inside `0x8C..0x950` belong to another entity, and importing DK artwork over them
would replace that entity's sprites. **A DK manifest must exclude `0x858..0x8A8`.**

<details><summary>The original (wrong) reasoning, kept as a worked failure</summary>

Three checks were said to agree: that only `Donkey Kong 1P` rendered coherently; that the posture
matched DK's crawl at `0xE0`; and that the headers gave 6–7 chars against DK's 21, read as "DK at a
third scale". The third was a real measurement and is still true — it is simply evidence of *a small
sprite*, not of a small **DK**. The first two were the failure:

- **"Only DK's palette is coherent" was judged against four hand-picked alternatives**, all of them
  wrong ones (Diddy, Krusha, Klump, Kritter, Rambi). Manky was never tried. Picking the least-bad of
  five guesses and calling it a match is not a palette flip; the DK/Diddy check at `0x954` worked
  because the *correct* palette was in the comparison and inverted cleanly.
- **"The posture matches DK's crawl" was pattern-matching a hunched quadruped silhouette**, which an
  ape on all fours has whoever it is.

`--palette-sweep` exists because of this: it renders one sprite under all 79 known palettes at once,
so the right one can be seen next to the wrong ones instead of judged in isolation. `--near` exists
for the same reason from the other side — it lists indices by *ROM data* order rather than table
order, and it was ROM adjacency, not colour, that broke this open by showing the block sitting next
to `0x12AC..0x12CC` and a barrel, nowhere near DK.

**The transferable lesson:** "this looks right in palette X" is worthless without the alternatives
on screen, and "this looks wrong" at small scale is worthless too (that reading is what kept the
block flagged for two sessions). Identity claims need either a clean inversion against the true
alternative, a script-level link, or ROM adjacency to something already identified.

</details>

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

**Chained imports are supported, not refused — this line used to say the opposite and was wrong.**
The claim was that chaining "stays unsolved and stays refused (`sourceRomSha256`)". Testing it
found no refusal at all: importing onto an already-imported ROM silently succeeded and wrote a
ledger whose `sourceRomSha256` named the *intermediate* image, recording only its own allocation.
The earlier run's allocations were absent, so the two sidecars formed an unlinked chain that had to
be replayed in reverse order by hand — and writing the second import to the same filename would
have overwritten the first sidecar and lost them outright.

Chaining now carries the input's ledger forward and **preserves the original `sourceRomSha256`**, so
one `--revert` returns to the ROM the chain began from and no intermediate sidecar has to be kept.
`ImportLedger.MatchesRom` replaces the sha256 equality guard for this case: the ledger is checked
against what the ROM actually contains (every allocated index must still point at its newest
allocation), which admits a real chain and rejects a stale or foreign ledger with a precise
diagnostic. Gated by **V4g**.

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
dotnet run -- <rom> --whose [seedHex] [--contact out.png]          # index ownership from a seed (A.8)
dotnet run -- <rom> --captions <sheet.png> --out c.png \
                    [--from N] [--to N] [--scale N] [--capheight N] # read strip captions (A.10)
dotnet run -- <rom> --anims-in <lo>..<hi> [--frames N]             # in-block animations (A.9)
dotnet run -- <rom> --anim-sheet <lo>..<hi> --out s.png \
                    [--from N] [--to N] [--zoom 2] [--cell N]      # animations as pictures (A.12)
dotnet run -- <rom> --palette-sweep <idx> --out sweep.png          # one sprite, all 79 palettes (A.13)
dotnet run -- <rom> --near <idx> [--count N] [--contact out.png]   # neighbours in ROM data order (A.13)
dotnet run -- <rom> --contact-range <lo>..<hi> --contact out.png \
                    [--stride N] [--zoom N] [--palette <name>]     # decode-and-look sheet (A.8)
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
| V4g | An import chain reverts byte-exactly: N poses, then M more carrying the first ledger forward, then one revert back to the original's sha256 |

V4c is the one that matters most — it is where the re-scan trap (85 % waste, 17 poses instead of
~100) would resurface.

V4g exists because the claim preceded the test. `ImportLedger` asserted that "every import is
reversible" while `--revert` only ever undid an *expansion*, so import rollback was documented,
plausible, and had never once been executed — the ledger even lacked the filler byte needed to make
it byte-exact rather than pointer-only. The gate's two-run shape is deliberate: a single run's
ledger reverting is the easy half, and the chained case is the one where the ledger must keep the
original sha256 rather than the intermediate one.

---

## Part E — Open questions

- ~~**Which image indices does DK actually own?**~~ **Answered (A.8): `0x8C..0x950`, 562 indices;
  Diddy begins at `0x954`, confirmed by palette flip and read off the render by the project
  owner.** Located from M0's `0x8C` seed with `--whose` + `--contact-range`. The one flagged block,
  `0x858..0x8A8`, is resolved in A.13 — and resolved *against* the original guess: it is **not**
  DK but a foreign island of ~21 indices, most likely Manky Kong. A DK manifest must exclude it
  either way — they are 5–9 char sprites and cannot take full-size poses.
- **Assigning sheet strips to index runs.** A.8 gives the index *block*; it does not say which
  strip maps to which run within it, and A.9 shows frame count cannot decide it. The remaining step
  is a per-strip caption-read plus action match against `--contact-range` renders — mechanical, but
  46 of them, and not automatable by any signal measured so far.
- **What to do where the counts differ (A.9).** A strip with no equal-length animation cannot be
  imported 1:1 without either leaving stale ROM frames or editing the animation scripts. Script
  editing is out of M4's scope and is the natural M5. Until it exists, a faithful full-character
  import is not achievable for every strip — which is a scope finding the PRD does not yet reflect.
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
| **Strip assigned by frame count** | **A.9: does not work — 0 for 2, including a unique match. Match by caption + action, never by count** |
| **Foreign sprites inside a character's index range** | **A.13: `0x858..0x8A8` is not DK. Verify a block with `--palette-sweep` (all palettes at once) and `--near` (ROM adjacency), never against hand-picked alternatives** |
