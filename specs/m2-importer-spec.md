# M2 Spec: Image → Sprite Importer (tiler + encoder)

**Status:** M2a done (2026-07-24); M2b not started. **Reopens scope** beyond the
locked M0–M1 (see `prd-sprite-importer.md` §0); to be confirmed before M2b.
**Depends on:** M0 (decoder), M1 (`SpriteModel` + `SpriteEncoder.Serialize`, byte-exact).
**Verification:** pixel-exact re-decode (byte-exact is impossible here — see M1 spec §B).

M2 is the first **non-port** milestone: net-new design with no ground-truth oracle.
Correctness is defined by re-decode, and the budgets/algorithm are pinned down here
*before* coding precisely because there's no round-trip to catch mistakes.

---

## Part A — VRAM/char budget (research result)

Measured across all **2,714** sprites the game itself uses (`--stats`):

| Metric | Max | p50 | p90 | p99 |
|--------|-----|-----|-----|-----|
| Chars/sprite `b5+b7` | **88** | 19 | 30 | 72 |
| — group 1 `b5` | 88 | | | |
| — group 2 `b7` | 12 | | | |
| VRAM rows (grid is 16 wide) | **6** | 2 | 2 | 5 |
| Placement entries `b0+b1+b3` | **37** | | | |
| Render placements `b0*4+b1+b3` | 88 | | | |
| Sprite data size | 0xB52 (2898 B) | | | |
| Pixel bbox W×H | 110 × 208 | | | |

### Interpretation — **two independent budgets that trade off**

1. **Char / VRAM budget** = `b5 + b7` = number of distinct 8×8 chars DMA'd into OBJ
   VRAM. **Hard cap (evidence-based): 88 chars / 6 rows (≤96 of the 256 tiles in an
   OBJ name table).** This is *minimized* by covering only **occupied** 8×8 cells and
   emitting each as a 1×1 char. A 2×2 entry always emits 4 chars, so it *wastes* chars
   on any transparent sub-cell.
2. **OAM entry budget** = `b0 + b1 + b3` = number of hardware sprites (a 2×2 = one
   16×16 OBJ = 1 entry; each 1×1 = 1 entry). Observed max **37**. This is *minimized*
   by 2×2 grouping.

All-1×1 minimizes chars but maximizes OAM entries; all-2×2 does the reverse. The
game's strategy — and ours — is the sweet spot: **2×2 only where all four 8×8
sub-cells are occupied** (zero char waste, free OAM savings), **1×1 for the rest**.

### Budget rules for M2
- **Char budget:** generated `b5+b7 ≤ 88` (target ≤ 96 tile slots / 6 rows). Warn > 72.
- **OAM budget:** generated `b0+b1+b3 ≤ 37` (soft; only matters in-game, verified by
  emulator later).
- **In-place (locked: no expansion):** generated **size ≤ the target slot's original
  size**. If the pose needs more, M2 **refuses and reports "needs M3 (expansion)"** —
  it does not overflow neighbouring data.
- The theoretical hardware ceiling (256 tiles / 16 rows) is **not** relied upon without
  a disassembly of the DMA routine; 88/6 is the safe, demonstrated envelope.

---

## Part A.2 — Verified grid model (from `--inspect`)

The VRAM grid is char *storage*, independent of canvas position; placements map grid
chars to arbitrary `(x,y)`. Confirmed against real sprites:

- **The char stream fills group 1 densely, 16 per row, row-major** → grid cell `(r,c)`
  = stream index `r*16 + c` (every row full except the last).
- **Group 2 (`b6`,`b7`) exists to place chars in a specific row `b6>>4` when group 1
  hasn't filled that row.** `0x1B4`: `b5=13` fills only row 0; its three 2×2 tiles take
  TOP halves from group 1 (row 0) and BOTTOM halves from group 2 (row 1, `b6=0x10 b7=6`).
  Group 2 is the *only* way to reach row R without first writing 16 chars in every
  earlier row.
- **2×2 tiles occupy grid 2×2 blocks walking row-pairs** `(0-1),(2-3),(4-5)`, 8 blocks
  per pair; **1×1 tiles fill the remaining cells** starting at `b2`. `0x1654`: `b5=88`
  over 6 rows, 17 2×2 entries fill row-pairs, then 14 1×1 continue at row 4 col 2 — all
  single group 1 (`b7=0`).

Two schemes the game uses: **(A) single group 1**, dense multi-row (`0x1654`); **(B)
group1+group2 split** to reach a row without padding (`0x1B4`). The encoder uses only
Scheme A (see Part C).

## Part A.3 — M2a implementation note: the row-pair packing collision

Building the tiler surfaced a real bug in the general formula sketched in Part A.2/C:
`b2 = (R<<4)|(2*(m%8))` is only safe when the last VRAM row-pair used by 2×2 entries
is **completely full** (`m % 8 == 0`). If it's partially full, the 1×1 continuation's
column-then-row wrap walks straight into flat char-stream slots already claimed by
that row-pair's BL/BR halves — verified by simulation before coding (a partial
row-pair of 3 entries collides at the 11th subsequent 1×1 cell). **Fix shipped:** the
tiler only ever converts 2×2 blocks in whole groups of 8 (`usable = (qualifying.Count
/ 8) * 8`), so `b2` always starts a 1×1 run on a completely fresh row (`b2 = 4*m`).
Any qualifying blocks beyond the last multiple of 8 stay as plain 1×1 cells — a loss
of at most 7 blocks' OAM savings, and always collision-free.

A second bug this surfaced: the flat char stream for a full row-pair is **interleaved
across entries**, not grouped per entry — row R holds every entry's TL,TR in entry
order, then row R+1 holds every entry's BL,BR in entry order (`[TL0,TR0,TL1,TR1,...,
TL7,TR7,BL0,BR0,...,BL7,BR7]`), not `[TL0,TR0,BL0,BR0,TL1,TR1,BL1,BR1,...]`. Caught
immediately by the pixel-exact gate (Part D) on the first multi-entry 2×2 test case.

## Part B — Scope & split

M2 turns one prepared pose into a valid sprite for an existing slot. Split to isolate
the irreversible ROM mutation:

- **M2a — image → `SpriteModel` (no ROM write).** Palette-map + tiler + model build;
  verify by `SpriteDecoder.Decode(Serialize(model))` == input pixels. Pure, safe,
  fully testable headless. **This is the bulk of the new design.**
  ✅ **Done 2026-07-24.** Built `Core/SpriteTiler.cs` (index-grid → `SpriteModel`,
  canonical single-DMA-group scheme, safe row-pair-only 2×2 refinement per Part A.3)
  and `Core/TilerHarness.cs` (decodes the tiler's output through the *real*
  `SpriteDecoder.Decode` via a synthetic in-memory ROM + identity palette, so the
  gate validates against production decode code, not a reimplementation). `--verify-m2a`
  runs 7 synthetic poses covering baseline all-1×1, full 2×2 refinement, the
  leftover/fallback path (qualifying blocks not a multiple of 8), non-8-aligned
  canvas dimensions, and both the 88-char and 37-OAM budget caps: **7/7 pass.** M1's
  gates (`--verify-m1`) still pass unchanged (53,158/53,158 chars, 2,714/2,714
  sprites) — M2a made no changes to M1 code.

  **Real-sprite corpus added** (`Core/M2aRomHarness.cs`, per review feedback that the
  synthetic-only corpus was thinner than the spec's V2 ideal): every sprite the game
  itself uses is decoded to an index canvas (identity palette), its bbox re-tiled
  through `SpriteTiler`, re-serialized, re-decoded, and pixel-compared against the
  original decode — the actual "decode↔tile↔re-decode over the whole game" gate Part
  D calls for, not just hand-built cases. **2,714/2,714 pass, 0 failed, 0 empty-skipped.**
  3 sprites exceed the 88-char cap and 105 exceed the soft 37-OAM budget (both
  correctly flagged, not failures — expected, since the tiler's bbox-aligned cell
  grid doesn't always land on the same 8×8 boundaries the game's own encoding used,
  so a char that was whole in the original can split across two cells when re-tiled
  from an arbitrary bbox origin; costs extra chars but still round-trips pixel-exact).
- **M2b — write + repoint.** Serialize the model into the target slot (in place) and
  update the 3-byte `gfxArray` pointer; guarded by the in-place size rule. Verified by
  re-decode from the written ROM, then emulator.

Reuses M1 entirely: the tiler's output **is** a `SpriteModel`, serialized by the
existing `SpriteEncoder`. M2 adds only the *forward* image→model step.

---

## Part C — Pipeline design

Input: one pose PNG (see open Q1 for indexed-vs-RGB), target palette, target slot index.

1. **To index grid (FR3).** Produce `int[H,W]` of palette indices; transparent → 0.
   Input is **pre-indexed 16-color to the target palette** (decided Q1): direct map
   from the PNG's index/exact-color to `0..15`, transparent → 0. Keeps the gate
   pixel-exact. (RGB nearest-color matching is a deferred, separately-verified add-on.)
2. **Occupied-cell scan.** Over the pose bbox, mark each 8×8 cell occupied if any pixel
   index ≠ 0. `K` = number of occupied cells.
3. **Tiler (FR4) — canonical single-group scheme (`b3=b7=0`).** The encoder controls
   the header, so it always emits **one DMA group** (no group 2, no 1×1-group-2). This
   avoids Scheme B entirely; the only cost is ≤15 padding chars in the 2×2 path, easily
   within the 88-char budget.
   - **Baseline — all 1×1** (`K ≤ OAM budget`): each occupied cell → one 1×1 entry;
     `b0=0, b1=b5=K, b2=0`. The 1×1 walk from (0,0) and the row-major char fill are both
     row-major, so *placement i ↔ char i* — **provably consistent**. Char-optimal;
     OAM = K.
   - **2×2 refinement** (only when `K > OAM budget`): replace each aligned 16×16 block
     whose four 8×8 sub-cells are **all** occupied with one 2×2 entry (−3 OAM entries,
     same char count). Assignment (mirrors the verified walk):
     1. 2×2 tiles `T_0..T_{m-1}` → grid via the decoder walk: `T_j` at `r=2*(j/8),
        c=2*(j%8)`, sub-cells TL/TR/BL/BR at `(r,c)(r,c+1)(r+1,c)(r+1,c+1)`.
     2. 1×1 tiles continue in row-major grid order right after the 2×2 cells;
        `b2 = (R<<4) | (2*(m%8))` where `R = 2*(m/8)`.
     3. `b5` = (highest referenced stream index)+1, padding the final row so every
        referenced cell is backed by a real char (pad ≤15, within budget). `b1 = k`,
        `b0 = m`.
   - Every layout is validated by the re-decode gate (Part D); any assignment slip is
     caught immediately, so the 2×2 path is safe to iterate.
4. **Model/header generation (FR5).** Emit a `SpriteModel`: placements in file order
   (2×2, then 1×1-group-1), char index grids at their assigned stream indices (padded
   cells = transparent char), header `b0=m, b1=k, b2, b3=0, b4=0, b5, b6=0, b7=0`.
5. **Serialize** via `SpriteEncoder.Serialize` (M1). → sprite bytes.

**Placement origin (Q4, decided):** each tile's `(x,y) = origin + cellOffset`, where
`origin` = the **replaced slot's bbox top-left** (`min x,y` over the original sprite's
placements). This keeps the imported pose where the original sat so existing animation
offsets still line up. (For M2a's headless gate, source and re-decode share the origin,
so it's neutral there; it matters for M2b/in-game. Anchor-by-feet/center is a later
refinement.)

---

## Part D — Verification strategy

- **V2 (M2a gate, headless):** for a corpus of test poses,
  `Decode(Serialize(tiler(pose))) == pose` at the **pixel/index level** (not bytes).
  Also assert budgets (Part A). This is the definition of done for M2a.
- **V2b (M2b gate):** after writing, re-decode the slot from the ROM and pixel-compare;
  assert the pointer + size are correct and no neighbouring bytes changed beyond the
  slot.
- **V3 (emulator):** load the modified ROM in the libretro harness (parallel track) and
  confirm the sprite renders in-game. Required before trusting M2b broadly.

---

## Part E — Decisions & remaining open questions

**Resolved**
- **Q1 Input format — pre-indexed 16-color** to the target palette (§C.1). Keeps the
  re-decode gate pixel-exact; RGB matching deferred.
- **Q4 Placement origin — replaced slot's bbox top-left** (§C, end).
- **Grid/2×2 layout — single-group Scheme A**, algorithm in §C.3 (grounded in §A.2).

**Still open**
- **Q2 Pose → slot mapping:** how is "which slot each pose replaces" provided — a
  hand-authored manifest, or derived from the animation tables? *Needed for M2b only;
  M2a does not need it.*
- **Q3 Sequencing:** land **M2a first** (headless, high-confidence), then **M2b** once
  the emulator harness exists. Recommended; confirm before starting M2b.

---

## Part F — Risks

| Risk | Mitigation |
|------|-----------|
| Tiler produces decoder-inconsistent grid/char mapping | Pixel re-decode gate catches every case; 1×1 baseline is provably consistent |
| Pose exceeds 88-char / in-place size budget | Refuse + report "needs M3"; never overflow the slot |
| RGB→index ambiguity degrades fidelity | Prefer pre-indexed input (Q1); pixel-verify tolerance policy |
| OAM entry overflow (in-game only) | 2×2 refinement + emulator check (V3) before trusting M2b |
| VRAM reserve smaller than assumed 96 tiles | Stay ≤ original slot size in-place; confirm via emulator |

---

## Appendix — tooling

`dotnet run -- <rom> --stats` (added to `Program.cs`) reproduces Part A's numbers.
