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
| `0x858..0x8AC` | small DK-proportioned sprites (distant/scaled DK) — **the one block worth a second look** |
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

**Palette as the identity test, not a caveat.** A survey rendered in one palette locates boundaries
by silhouette but cannot prove *identity* — so the palette-flip check above does that job instead,
and it is strictly better evidence than a silhouette. `0x858..0x8AC` (small DK-proportioned sprites)
is still flagged rather than claimed: it reads as DK in DK's palette, but nobody has run the flip
against a plausible alternative for it.

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
  owner.** Located from M0's `0x8C` seed with `--whose` + `--contact-range`. One small follow-up
  remains: `0x858..0x8AC` (small DK-proportioned sprites) is flagged but unconfirmed. It does not
  block authoring a manifest for any strip well inside the block.
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
