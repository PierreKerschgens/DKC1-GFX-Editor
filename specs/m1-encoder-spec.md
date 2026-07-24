# M1 Spec: Decode Contract + Encoder

**Status:** ✅ Done 2026-07-24 — Gate 1 and Gate 2 both pass on the real ROM:
53,158/53,158 chars round-trip byte-exact (Gate 1), 2,714/2,714 sprites in the
GFX pointer table round-trip byte-exact (Gate 2), 0 failures, 0 skips. Verified via
`dotnet run -- "port/Donkey Kong Country (USA) (Rev 2).sfc" --verify-m1`.
**Depends on:** M0 (`port/DkcTool/Core/SpriteDecoder.cs`, verified against image `0x1B4`)
**Gates:** byte-exact structural round-trip (see §3)

This doc is the "decode contract" promised in the PRD: the **verified** forward byte
layout, which is the specification the encoder must invert. Part A is fact
(established and visually confirmed in M0). Parts B–D are the M1 design that follows
from it.

---

## Part A — Decode contract (verified in M0)

Sprite data begins at the address resolved from `gfxArray` (`GfxTable`). Layout:

```
+0x00  header        8 bytes           b0..b7
+0x08  placements    (b0+b1+b3)*2 bytes (x,y) per placed tile, in group order
+....  char data     (b5 + b7) * 0x20   group-1 chars then group-2 chars
```

Total size = `8 + (b0+b1+b3)*2 + (b5<<5) + b7*0x20`. **There are no gaps** — header +
placements + char data cover every byte (verified: `0x1B4` → `0x27C`).

### Header bytes
| Byte | Meaning | Role in decode |
|------|---------|----------------|
| b0 | # of 2×2 (16×16) chars | placement group A, 4 grid cells each |
| b1 | # of 1×1 (8×8) chars, group 1 | placement group B |
| b2 | VRAM start of 1×1 group 1 | seeds `r=b2>>4, c=b2&0xf` |
| b3 | # of 1×1 chars, group 2 | placement group C |
| b4 | VRAM start of 1×1 group 2 | seeds `r=b4>>4, c=b4&0xf` |
| b5 | # chars in DMA group 1 (`= size>>5`) | `endGroup1` for VRAM fill |
| b6 | VRAM row placement of DMA group 2 | `startGroup2`; row `= b6>>4` |
| b7 | # chars in DMA group 2 | `countGroup2` |

### Placement table (order matters)
`b0` 2×2 entries, then `b1` 1×1 group-1 entries, then `b3` 1×1 group-2 entries. Each
entry is 2 bytes: `(x, y)` = pixel position on the 256×256 canvas.

- **2×2 entry** → 4 char placements at canvas `(x,y),(x+8,y),(x,y+8),(x+8,y+8)` and
  VRAM grid `(r,c),(r,c+1),(r+1,c),(r+1,c+1)`; walk `c+=2`, wrap `c==16 → r+=2,c=0`.
  Grid walk starts at `r=0,c=0`.
- **1×1 entry** → 1 placement at canvas `(x,y)`, VRAM grid `(r,c)`; walk `c+=1`, wrap
  `c==16 → r+=1,c=0`. Starts at `r=b2>>4,c=b2&0xf` (group 1) or `b4` (group 2).

### VRAM grid fill (from the char-data stream)
Rows of ≤16 chars. Fill `b5` group-1 chars row-major from `(0,0)`. Then pad with blank
rows until `rows.Count > (b6>>4)` (original adds **two** blank rows per step — a quirk
that is faithfully preserved). Then append `b7` group-2 chars to row `b6>>4`.

Placements index this grid to fetch the 0x20-byte char to draw. **For byte-exact
serialization the grid is irrelevant** — the char-data stream is simply `b5+b7`
contiguous chars in file order (see §3).

### Char (8×8, 4bpp) bit layout — the inverse the encoder needs
For pixel `(row i, col j)`, 4-bit index `p0 p1 p2 p3` (p0 = LSB):
```
p0 → byte[i*2 + 0]   bit (7-j)
p1 → byte[i*2 + 1]   bit (7-j)
p2 → byte[i*2 + 16]  bit (7-j)
p3 → byte[i*2 + 17]  bit (7-j)
```
(Derived from `CharDecoder.Decode`; `index=i*2`, plane pair `k` at `+k*16 / +1+k*16`.)

---

## Part B — Key insight: the encoding is **non-canonical**

Multiple different sprite encodings produce the **same pixels**:
- a 16×16 area can be one 2×2 entry *or* four 1×1 entries;
- fully-transparent cells may or may not be emitted as chars;
- char/VRAM ordering and group split carry the artist's choices, not the image's.

**Consequence:** a byte-exact round-trip **from RGB pixels alone is impossible in
general** — flattening to pixels discards structure. This reshapes the M1 gate:

- **M1 (this spec)** proves the *codec* is a correct inverse using a
  **structure-preserving, index-level** round-trip → **byte-exact**, achievable.
- **M2 (out of scope now)** is the freeform image→tiling generator (FR4) + RGB→index
  palette matching (FR3). Those are inherently non-canonical and will be verified
  **visually / by re-decode pixel compare**, never byte-for-byte.

Also: round-trip must run on **palette indices, not RGB**. Palettes can contain
duplicate colors, so `color→index` is ambiguous; only `index→bytes→index` is lossless.
RGB↔index matching is deferred to M2.

---

## Part C — M1 design

New/changed components under `port/DkcTool/Core/`:

1. **`CharCodec`** (index-level, lossless)
   - `int[8,8] DecodeIndices(byte[] chr)` — bit layout above, returns 0..15 indices.
   - `byte[] EncodeIndices(int[8,8] px)` — exact inverse; zero 0x20 bytes, set the 4
     bits per pixel. **Gate 1:** `EncodeIndices(DecodeIndices(b)) == b` for every char.
   - (Existing `CharDecoder.Decode`→SKColor stays as the display/render path.)

2. **`SpriteModel`** — captured by a structured decode:
   - `byte[8] Header`
   - `List<(int x,int y,TileType type)> Placements` (in file order: 2×2, 1×1 g1, 1×1 g2)
   - `List<int[8,8]> Chars` — the `b5+b7` chars, in **file order**, as index grids.

3. **`SpriteDecoder.DecodeStructured(rom, addr) → SpriteModel`** — extends M0's decoder
   to also retain header, placements, and the flat char list (via `DecodeIndices`).

4. **`SpriteEncoder.Serialize(SpriteModel) → byte[]`**
   - emit `Header` (8 bytes)
   - emit each placement `(x,y)` in order → `(b0+b1+b3)*2` bytes
   - emit `EncodeIndices(c)` for each char in `Chars` → `(b5+b7)*0x20` bytes

---

## Part D — Test plan (the M1 gate)

**Gate 1 (char codec):** for a large char sample across many sprites,
`EncodeIndices(DecodeIndices(chr)) == chr`.

**Gate 2 (full sprite, byte-exact):** for every valid image index (from `0x8c`,
step 4, until the first zero pointer):
```
B  = rom.ReadBytes(addr, size)
M  = SpriteDecoder.DecodeStructured(rom, addr)
B' = SpriteEncoder.Serialize(M)
assert B' == B          // byte-identical
```
Harness prints pass/fail counts and the first differing offset on any mismatch.
**Definition of done for M1:** Gate 1 and Gate 2 pass for the full sprite table.

This is a strict, non-trivial test: it exercises the bitplane inverse, the char
ordering, the placement-byte order, and the size math. The only thing reused from
decode is the *structure* (which tiles exist / where) — correctly so, since
reconstructing that from pixels is M2's job.

---

## Part E — Edge cases & risks

- **Duplicate palette colors** → handled by staying index-level (Part B).
- **Placements referencing empty grid cells** — shouldn't occur in real data; the
  harness will surface it as a decode exception on that index (report, skip, note).
- **Group-2 appended to a partially-filled group-1 row** (`b6>>4` collides with a row
  group 1 already touched) — serialization is order-based so unaffected; only note if
  a sprite exercises it, for M2's tiler awareness.
- **Odd total sizes / pointer table end** — trust the `size` formula; stop the batch at
  the first all-zero 3-byte pointer (matches `Form1` `button_nextImage_Click`).
- **Non-goal reminder:** no RGB→index matching, no tiler, no ROM writing in M1.
