# PRD: DKC1 Automated Sprite Importer

**Status:** M0, M1 complete (2026-07-24) — starting M2
**Author:** Pierre (with Claude Code)
**Created:** 2026-07-24
**Location of work:** `port/`

---

## 0. Decisions locked (2026-07-24)

Confirmed via scoping interview; supersede conflicting text below.

- **Engagement scope = M0–M1 only.** Port the full decoder and build the encoder;
  success gate is a **byte-exact decode→encode round-trip** against the target ROM.
  Custom-art import (M2+) is **out of scope for now**.
- **Custom sprite art: deferred.** The sheets in `port/sprites/` are not needed for
  M0–M1. Slicing/recolor decided later.
- **No ROM expansion.** Only in-place work; expansion (M3) explicitly deferred.
- **Verification: headless emulator via a libretro-core harness** (accurate core, e.g.
  bsnes; snes9x for speed) that runs N frames and dumps the framebuffer to PNG. Built
  as a **parallel track**; it does **not** gate M1 (the round-trip does).
- **Target framework: `net10.0`** (installed SDK is 10.0.302; no net8 pack present).
- **Target ROM:** `port/Donkey Kong Country (USA) (Rev 2).sfc` — 0x400000, no copier
  header, verified title `DONKEY KONG COUNTRY`.

## 1. Summary

Build a **headless, batch tool** that imports a custom sprite image into a Donkey
Kong Country 1 (SNES) ROM by automatically decomposing it into the game's 8×8/16×16
character tiles, generating a valid sprite header, and writing the result back to
the ROM — expanding the ROM if the new data doesn't fit.

This is the tool the sprite-sheet author explicitly wished for and could not find
(see `port/video-transcription.txt`, ~21:26–22:08):

> "What I would really need is a program that can import the image, automatically
> chop it into tiles, and then place them appropriately, adding and removing new
> tiles whenever needed. And it would honestly probably have to extend the ROM so
> that there's room."

Today this is done **by hand** in the existing WinForms editor (`StandAloneGFXDKC1`)
by typing hex coordinates for every tile box on every frame — infeasible for the
hundreds of frames in a full character sprite sheet.

## 2. Goals

- **G1** — Given one recolored pose (≤16 colors, transparent background), produce
  byte-exact DKC1 sprite data: char tiles + 8-byte header.
- **G2** — Automatically choose and place tiles (16×16 "2×2" and 8×8 "1×1" chars),
  skipping empty regions to minimize char/VRAM usage. No manual coordinate entry.
- **G3** — Write new sprite data into the ROM and repoint its GFX pointer-table
  entry; expand the ROM when the new sprite is larger than the original.
- **G4** — Batch-process many poses in one run.
- **G5** — Be verifiable without a GUI, via decode↔encode round-tripping against a
  real ROM.

### Non-goals (this PRD)

- Collision **hitboxes** (the separate `0xbb8000`→`0xbb0000` system). Documented in
  §7 for completeness; may become a follow-up PRD.
- Animation script editing (`Animation.cs` bytecode). Out of scope; frames are
  replaced in place by repointing existing GFX pointers.
- A graphical UI. A QA image viewer is "nice to have" (§9), not required.
- Automatic **pose slicing** from a full sprite sheet (§6, treated as a separate
  front-end step / manual input for v1).

## 3. Users & use case

Primary user: a ROM hacker / sprite artist who has a sprite sheet of custom,
game-accurate poses (e.g. the DK / DK Jr. "Cranky Kong Country" customs in
`port/sprites/`) and wants them in a playable ROM without hand-placing tiles.

## 4. Background: the on-ROM data model (established from source)

All facts below are derived from the existing C# editor and verified against the
target ROM.

### 4.1 Target ROM
- `port/Donkey Kong Country (USA) (Rev 2).sfc` — 0x400000 bytes (4 MB), **no**
  copier header, internal title `DONKEY KONG COUNTRY`. LoROM.
- Address convention (from `ROM.cs`): every access is masked
  `addr & (addr > 0x7fffff ? 0x3fffff : 0xffffff)`, mapping SNES bank addresses
  (e.g. `0xbbcc9c`) to file offsets.

### 4.2 GFX pointer table
- Base `gfxArray = 0xbbcc9c` (`Form1.cs`). A sprite "image index" reads a **3-byte**
  pointer at `gfxArray + index`. Indices step by **4**; the first valid index is
  `0x8c`; the table ends at the first all-zero pointer (`Form1.Image.Header.cs`).

### 4.3 Sprite header (8 bytes) — see `ROM.Sprites.cs` / `Form1.Image.Header.cs`
| Byte | Meaning |
|------|---------|
| 0 | # of 2×2 (16×16) chars |
| 1 | # of 1×1 (8×8) chars, group 1 |
| 2 | relative position (VRAM col/row start) of group 1 |
| 3 | # of 1×1 chars, group 2 |
| 4 | position of group 2 |
| 5 | size sent to VRAM (DMA group 1), `<< 5` |
| 6 | VRAM placement of DMA group 2 (0 if none) |
| 7 | # of chars in DMA group 2 |

Header length = `8 + (b0*2) + (b1*2) + (b3*2)` (2 position bytes per char pair/entry).
Total data size = `headerLength + (b5 << 5) + (b7 * 0x20)`.
Each char is **0x20 bytes** (4bpp, 8×8).

### 4.4 Tile placement
- After the header, each 2×2 and 1×1 entry has an `(x, y)` byte pair giving its
  pixel position on the 256×256 sprite canvas (`Read2x2`/`Read1x1`).
- Char pixel data follows; `SetupVRAM` reconstructs the SNES VRAM grid (rows of 16
  chars) that OAM indexes into. **The encoder must invert `SetupVRAM` +
  `ReadFromSpriteHeader` exactly.**

### 4.5 Char (tile) format — `DecodeChar` in `ROM.Sprites.cs`
4bpp planar SNES format, 0x20 bytes/char. Bitplanes 0–1 interleaved by row in bytes
0–15, planes 2–3 in bytes 16–31. The importer needs the **inverse** (`EncodeChar`).

### 4.6 Palette — `ROM.Palette*.cs`
15-bit BGR (`xbbbbbgggggrrrrr`), 15 colors + forced-transparent index 0 = 16 total.
Named palette→address catalog in `ROM.Palette.Pointers.cs` (e.g. `Donkey Kong 1P` =
`0x3C849A`). Ported already in `port/DkcTool/Core/`.

## 5. Functional requirements

- **FR1 — Decode (oracle).** Given ROM + image index, decode to a 256×256 RGBA
  bitmap and parse header/tiles. (Extends the Phase-1 sketch; port
  `ReadFromSpriteHeader`.)
- **FR2 — Char encode.** `EncodeChar(8×8 indexed block) → 0x20 bytes`, exact inverse
  of `DecodeChar`.
- **FR3 — Palette map.** Map an input image's pixels to a target 16-color palette
  index (reuse `SimplifyImage` nearest-color logic); transparent → index 0.
- **FR4 — Tiler.** Cover the opaque region with 16×16 "2×2" chars where a full
  16×16 area has content, else 8×8 "1×1" chars; skip fully-transparent 8×8 cells;
  minimize total chars.
- **FR5 — Header/layout generation.** Emit valid header bytes + per-tile `(x,y)` +
  char data + DMA/VRAM grouping such that FR1 decodes it back identically.
- **FR6 — ROM write + repoint.** Write new sprite data to free space; update the
  3-byte pointer at `gfxArray + index`.
- **FR7 — ROM expansion.** When new data exceeds the original slot and no free space
  fits, expand the ROM (add LoROM banks, fix internal header/map) and place data
  there.
- **FR8 — Batch.** Process a manifest of `{image PNG → target index}` in one run;
  emit a report.

## 6. Input assumptions (v1)

- Input images are **pre-sliced single poses**, already reduced to the target 16-color
  palette with a transparent background (the video's Photoshop step produces exactly
  this). Sheet auto-slicing is a **separate, later** concern.
- A caller-supplied mapping of pose → target image index (which animation frame each
  pose replaces).

## 7. Out of scope but documented: hitboxes

Collision hitbox = pointer table at `0xbb8000` indexed by `imageIndex/2`, pointing to
an 8-byte record `x,y,w,h` at `0xbb0000+ptr` (signed; centered on sprite)
(`Form1.HitboxAndZoom.cs`). Fully automatable later via opaque-pixel bounding box.
Not part of this PRD.

## 8. Verification strategy

- **V1 — Round-trip (primary gate).** For many real indices: decode → PNG → encode →
  assert **byte-identical** char+header data. Proves FR2/FR4/FR5 before any custom art.
- **V2 — Visual re-decode.** Import a custom pose, decode the written ROM slot, dump a
  PNG overlay; compare to the source image.
- **V3 — In-emulator.** Load the modified ROM in an SNES emulator and confirm the
  sprite renders in-game (manual, milestone 3+).

## 9. Milestones

- **M0 — Core port (extends Phase-1 `port/DkcTool/`).** Port `ReadFromSpriteHeader` +
  `SetupVRAM`; render a full sprite to PNG from a real index. *Exit:* known sprite
  decodes correctly. ✅ **Done 2026-07-24.** Phase-1 (`Rom`/`Palette`/`PalettePointers`/
  `CharDecoder`) verified first — ran the harness against the real ROM at gfx image
  index `0x8c`, decoded tiles look structurally correct (coherent shapes, no noise).
  Then ported header parse + 2x2/1x1 placement + VRAM emulation into
  `Core/SpriteDecoder.cs`, plus `Core/GfxTable.cs` for the `gfxArray` pointer-table
  lookup. Decoding image index `0x8c` end-to-end (pointer → header → VRAM → composite)
  produced a clean, correctly-posed Donkey Kong sprite from the real ROM — no
  misplaced tiles, no palette corruption.
  **Scope note:** the original's `tiles` list (per-entry `TLtile/TRtile/BLtile/BRtile`
  byte arrays, built by a second pair of loops over the header) is populated but never
  read by the render path — the draw loop indexes the VRAM emulation grid, not `tiles`.
  Confirmed this by tracing the code, so it was intentionally **not** ported; M1's
  encoder builds its own layout rather than inverting that dead bookkeeping.
- **M1 — Encoder + round-trip (highest-risk, do first).** `EncodeChar`, tiler, header
  generator; V1 passes on a broad sample of indices. *Exit:* byte-exact round-trip.
  ✅ **Done 2026-07-24.** Built index-level `CharCodec` (exact bit-layout inverse of
  `CharDecoder`), `SpriteModel`/`SpriteDecoder.DecodeStructured` (header + placement
  table + file-order char list), and `SpriteEncoder.Serialize` (exact inverse
  serialization). Added `RoundTripHarness` running both spec gates over every valid
  image index in the real ROM's GFX pointer table (`--verify-m1` CLI flag):
  Gate 1 (char codec) 53,158/53,158 pass; Gate 2 (full sprite byte-exact round-trip)
  2,714/2,714 pass; 0 failures, 0 skips. Per Part B of the spec, this is an
  index-level structural round-trip (not RGB→bytes), which is the correct and
  achievable M1 gate — RGB/tiler work is M2.
- **M2 — Import one custom pose (no expansion).** Palette-map + encode a pose; write +
  repoint; V2 passes. **M2a ✅ done 2026-07-24** (`specs/m2-importer-spec.md`): image →
  `SpriteModel` tiler, pixel-exact over 2,714 real sprites. **M2b ✅ done 2026-07-25**
  (`specs/m2b-writer-spec.md`): `--import` writes + repoints, V2b gate passes
  (2,711/2,714 isolated; 84 cumulative imports at 89% free-space utilisation).
  Note the wording above ("fits an
  existing slot") did not survive contact with the data: a re-tiled pose fits its
  original slot only 1.7% of the time, so M2b implements FR6 as **relocation into
  scanned free space + repoint** — still no ROM expansion, so M3 is unaffected.
- **M2c — Widening the free-space pool** (unplanned, inserted before M3).
  ✅ **Done 2026-07-25** (`specs/m2c-free-space-spec.md`). Asked whether the allocator's
  pool could be widened instead of paying for ExHiROM. It can, by 15 KB (77 → 92 KB,
  99 poses) — and the same pass measured *why* the other 74 KB is not free. The 15 KB
  is real but nowhere near a character's worth, which is what made M3 unavoidable.
- **M3 — ROM expansion.** FR7; import a pose larger than its slot.
  ✅ **Done 2026-07-25** (`specs/m3-expansion-spec.md`). `--expand` writes a reversible
  ledger entry and `--revert` reconstructs the original (sha256-checked and gated).
  `--revert` also rolls back **imports** — restoring each GFX pointer and refilling the
  bytes the allocation claimed, byte-exactly and sha256-asserted, across a chain of
  runs (M4 V4g). It was expansion-only until then, despite the ledger claiming
  otherwise;
  `Expansion.ExtendedRuns`/`FreeRunsFor` are the allocator source for an expanded ROM.
  `--verify-m3 --all-cores`: **6/6 cores pass**. Getting there meant fixing a real bug —
  a stale mirrored ExHiROM header at `0x40FFC0`, which only `bsnes_libretro` caught.
  V2b cumulative against an expanded ROM: 2,711/2,714 sprites round-trip into the
  extended half, 53 % of 3.8 MB used.
  **Supersedes FR7**, which describes expansion as appending banks and bumping the size
  byte. That is wrong for this ROM and fails totally — see the spec's B.1.
- **M4 — Batch + report.** FR8 over a manifest; QA overlay sheet.
  ✅ **Done 2026-07-25** (`specs/m4-batch-spec.md`). `--slice` (segmentation + stable
  strip/pose numbering), `Manifest`, `--batch` (one pristine scan, one ledger,
  report-and-continue, QA overlay), `--verify-m4` (V4a–V4f green; V4d 2/2 cores by
  default, 6/6 under `--all-cores`). Also answered the milestone's blocking unknown:
  **DK owns image indices `0x8C..0x950` (562 slots); Diddy begins at `0x954`** — located
  from M0's `0x8C` seed and confirmed by palette flip (spec A.8).
  **Amends §6 and FR8** — the input sheets are not pre-sliced, so slicing landed here
  rather than in M5.
- **M5 (optional) —** ~~Sheet auto-slicing~~ (absorbed into M4) and/or **QA GUI viewer.**

## 10. Technical approach

- **Language: C# (.NET 10), headless CLI.** Chosen so the encoder can be inverted and
  cross-checked directly against the existing, correct decoder/header parser (the
  oracle). Reuses `port/DkcTool/Core/` (Rom, Palette, PalettePointers, CharDecoder).
- **Imaging: SkiaSharp** (cross-platform; replaces Windows-only `System.Drawing`).
- Runs on macOS/Linux/Windows. Requires the .NET 8 SDK (`brew install dotnet-sdk`);
  not yet installed on the dev machine.

## 11. Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Header/VRAM/DMA encoding subtly wrong | Corrupt/garbled sprites | M1 byte-exact round-trip gates everything |
| ROM expansion breaks LoROM mapping | ROM won't boot | Follow established SNES expansion practice; test in emulator early |
| Tiler exceeds per-sprite VRAM/char budget | Sprite won't fit hardware limits | Cap char count; warn; fall back to fewer 2×2 splits |
| Pose ↔ frame mapping unknown | Wrong sprite replaced | Require explicit manifest (FR8); no guessing |
| Sheet slicing ambiguity | Bad auto-cuts | Deferred to M5; v1 takes pre-sliced input |

## 12. Open questions

- ~~Per-sprite VRAM/char ceiling?~~ **Answered (M2b): 88 chars.** Enforced as a refusal;
  it is also what automatically catches the slicer's ~4 % mis-merges (m4 A.3).
- ~~Safe free space vs. required expansion for DK/DK Jr.'s frame set?~~ **Answered
  (M2c + M4 A.6): expansion is required, not optional.** The stock pool is 92 KB / 99
  poses after widening; the two sheets need 808 KB, and **DK alone needs 487 KB — 5.3×
  the pool.** No amount of widening closes that (M2c's entire recovery was 15 KB).
  Against M3's extended space it is 21 %.
- ~~How is the pose→image-index mapping provided?~~ **Answered (M4 A.7/A.8): both, split
  by axis.** The *index* side derives from the animation tables (440 scripts ported,
  440/440 parse); the *assignment* of a sheet strip to an animation is hand-authored,
  because the sheet's captions are rendered pixels and cannot be read as metadata.
  A.8 narrows the search space to DK's 562-slot block.
- ~~Does the target hack need palette edits?~~ **Answered (M4 A.1): no, for these two
  sheets.** Both are already `Donkey Kong 1P` — 16 distinct opaque colours, 15 matching
  exactly, the 16th being pure black used only in row captions. Unverified for any
  other character's sheet.

**Still open:**
- **Which sheet strip maps to which animation.** A.8 gives DK's index block; it does not
  say which of the ~46 strips belongs to which run inside it. This is the remaining
  manual step and the one place a wrong answer replaces the right sprite on the wrong
  frame. Mitigated by the length-mismatch refusal and the QA overlay, not eliminated.
- **Hitboxes** (§7) — still out of scope, still auto-derivable from the opaque bbox.
  ~500 re-posed frames make M2b's drift report load-bearing rather than advisory.
