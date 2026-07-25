# M2b Spec: ROM write + repoint

**Status:** ✅ **done 2026-07-25.** **Depends on:** M0 (decoder), M1
(`SpriteModel`/`SpriteEncoder`, byte-exact), M2a (`SpriteTiler`, pixel-exact).
**Gate:** V2b headless (Part E) — `--verify-m2b`: isolated **2,711/2,714** passed
(3 skipped, over the 88-char cap — the same 3 M2a flags), cumulative **84 imports**
at **89% free-space utilisation** before a clean `NoFreeSpace` refusal, 0 gate
failures. M1 (53,158/53,158 + 2,714/2,714) and M2a (7/7 + 2,714/2,714) still pass.
**Supersedes** the "in-place, refuse if bigger" sentence in `m2-importer-spec.md`
Part B — see Part A for why.

M2b is the first milestone that **mutates a ROM**. Everything before it was pure.
The design below is built around that: the only bytes an import ever overwrites are
3 pointer bytes and previously-unused filler, so any import is reversible.

---

## Part A — Research results (`--stats-m2b`)

Measured over all 2,714 sprites in the GFX pointer table of
`port/Donkey Kong Country (USA) (Rev 2).sfc`.

| Question | Result |
|---|---|
| Re-tiled pose fits its original slot? | **45 / 2,714 (1.7%)**; median overshoot **+120 B**, p90 +256 B, max +612 B |
| Same, tiled on the original placement lattice | 37 / 2,714 — **no better** |
| Sprites whose placements sit on an 8×8 lattice from their bbox top-left | **130 / 2,714 (4.8%)** |
| Slack between consecutive sprites | **zero for 2,580 / 2,698 pairs** (p50 0, p90 0) |
| Aliased addresses (>1 image index → same sprite) | 11 addresses shared by 26 indices, max 3 (`0xE4AF71`) |
| Pointer encoding | 2,707 / 2,714 in banks `0xC0–0xFF`; the 7 low-bank ones are HiROM-consistent (low word ≥ `0x8000`) |
| Re-tiled sprite size | p50 `0x2F4`, p90 `0x4AE`, max `0xC68` |
| Candidate free space | 127 filler runs ≥ `0x100` after excluding sprite-internal runs = **151 KB**; of which **end-of-bank padding: 27 runs / 77 KB**, largest `0x1E7A` |

### Interpretation — in-place is not viable, and that is not a tiler bug

The game's sprite data is **hand-authored**: only 4.8% of sprites place their tiles
on a regular 8×8 lattice. Artists nudged tiles to arbitrary pixel offsets so one char
covers content that a fixed grid splits across two. A grid tiler structurally cannot
match that, and the ~4-char median penalty (120 B ÷ 32 B) is the price. **This was
tested, not assumed:** anchoring the tiler's grid to the original placement lattice
was tried and made things marginally *worse* (37 vs 45 fits).

Combined with zero inter-sprite slack, "write in place, refuse if larger" would refuse
**98.3%** of imports. So M2b **always relocates**: write the new sprite into free
space and repoint. This is PRD **FR6** verbatim ("write new sprite data to free space;
update the 3-byte pointer") and needs **no ROM expansion** — FR7/M3 stays deferred.

Two consequences fall out for free, and they are the reason this is the safe choice:

- **The original sprite data is never touched.** A bad import costs 3 pointer bytes,
  restorable from the ledger (Part C.5) or by re-reading the source ROM.
- **Aliasing stops mattering.** Repointing index *i* leaves the other indices sharing
  that address pointing at the untouched original. Under in-place overwrite those
  frames would have silently changed. Still *reported*, never a refusal.

### Capacity

77 KB of end-of-bank padding ÷ 756 B (p50) ≈ ~100 poses; ~66 at p90 size. **Measured
after implementation: 84 poses at 89% utilisation** before a clean `NoFreeSpace`
refusal (`--verify-m2b` cumulative). Enough for a character's frame set, **not** enough
for a full two-character sheet — that is what M3 (expansion) is for. Exhaustion is a
clean, reported refusal, not a failure.

> **Implementation note — the free-space re-scan trap.** The first implementation
> re-scanned the ROM inside every import. Because `Scan` excludes runs overlapping
> known sprite data, a sprite written into a run makes that run reachable from the
> pointer table, so the run — *including all its unused tail* — vanishes from the
> candidate list on the next import. Each run then yields exactly one allocation:
> **17 poses, 79 KB consumed to write 13 KB (16% utilisation)**. The scan must be
> taken **once from the pristine ROM** (`ImportOptions.FreeRuns`) with the **ledger**
> as the sole occupancy authority. This is invisible to gates 1–5 — exhausting at 17
> and at 84 both look like "a clean `NoFreeSpace` refusal" — so Part E gained a
> utilisation floor to catch it.

---

## Part B — Scope

**In scope:** PNG pose → index grid → tiler (M2a) → serialize (M1) → allocate free
space → write → repoint → verify by re-decode. Single pose per invocation.

**Out of scope:** ROM expansion (M3); batch manifests (M4); hitbox updates (PRD §7,
but see Part G); animation-script edits; RGB nearest-color matching (M2 Q1 locked
pre-indexed input).

---

## Part C — Components

### C.1 `Core/Rom.cs` — add a write path

Currently read-only (`readonly byte[] _data`, `Read8/16/24/ReadBytes`). Add:

```csharp
public Rom Clone();                                  // deep copy; imports work on a copy
public void Write8(int address, byte value);
public void Write24(int address, int value);         // little-endian, 3 bytes
public void WriteBytes(int address, byte[] src);
public void Save(string path);
public byte[] Snapshot();                            // for the containment diff
```

All writes go through the existing `Mask()`. Every write asserts
`Mask(addr) + len <= _data.Length`. `Rom.Load` keeps its own buffer; the **input file
is never written to** — `Save` targets `--out` (Part D).

### C.2 `Core/PoseLoader.cs` — PNG → index grid (FR3)

```csharp
public static PoseResult Load(string path, SKColor[] palette);
// PoseResult { int[,] Pixels; int BboxX, BboxY, Width, Height; List<UnmappedColor> Unmapped; }
```

Rules (M2 Q1 locked **pre-indexed 16-color** input — no nearest-color matching):

1. Decode via `SKBitmap.Decode`. Reject if width or height > 256 (canvas limit).
2. `Alpha == 0` → index 0.
3. Otherwise **exact RGB match** against `palette[1..15]` → that index.
4. An opaque pixel exactly equal to `palette[0]`'s RGB → index 0, **with a warning**
   (index 0 is forced-transparent; the pixel cannot be represented as opaque).
5. Any unmatched opaque color → **refuse**, listing each distinct color with its pixel
   count and first `(x,y)`. This is the single most likely user error; the message must
   be actionable, not "import failed".
6. Crop to the opaque bbox and return it plus its offset within the PNG.

### C.3 `Core/SpriteSlot.cs` — target-slot introspection

```csharp
public static SpriteSlot Read(Rom rom, int imageIndex);
// { ImageIndex, PointerAddress, SpriteAddress, FileOffset, Size,
//   PlacementMinX/MinY, PlacementMaxX/MaxY, AliasIndices }
```

`PointerAddress = GfxTable.BaseAddress + imageIndex`. Bbox comes from the **placement
table** (`x,y` pairs; extent `+16` for 2×2 entries, `+8` for 1×1) — *not* from the
decoded pixel bbox. The two disagree for 1,437 / 2,714 sprites, and the placement bbox
is the one the spec's Q4 origin rule means. `AliasIndices` = other indices resolving to
the same address (report only).

### C.4 `Core/FreeSpace.cs` — scan + allocate

Scan (deterministic, ascending offset):

1. Runs of constant `0x00` or `0xFF`, length ≥ `0x100`.
2. **Exclude any run overlapping known sprite data.** A fully transparent char is 32
   zero bytes, so runs *inside* sprites read as filler — this filter removed 17 runs
   and 36 KB from the raw scan.
3. Keep only **end-of-bank padding**: run end ≡ 0 (mod `0x8000`). The safest class,
   and allocating from the run start cannot cross a bank boundary.

Allocation: first-fit over runs sorted by ascending start, minus everything already in
the ledger. Assert `(start >> 16) == ((start + len - 1) >> 16)` — **SNES DMA must not
cross a bank boundary**. No free run large enough → refuse with `NoFreeSpace`
("needs M3 (expansion)").

> These runs are *candidates*: constant bytes prove nothing about whether the game
> reads them. Nothing here is trusted for gameplay until V3 (Part E).

### C.5 `Core/ImportLedger.cs` — allocation bookkeeping

JSON sidecar at `<out>.dkctool.json`, so repeat and batch runs never hand out the same
bytes twice, and every import is reversible:

```json
{
  "version": 1,
  "sourceRomSha256": "…",
  "allocations": [
    { "imageIndex": "0x8C", "offset": "0x1FE186", "length": "0x4A2",
      "previousPointer": "0xE4AF71", "source": "dk-walk-01.png",
      "timestamp": "2026-07-25T12:00:00Z" }
  ]
}
```

On load, verify `sourceRomSha256` against the input ROM and refuse on mismatch (a
ledger from a different base ROM would hand out occupied bytes). `previousPointer`
makes rollback a 3-byte write.

### C.6 `Core/GfxTable.cs` — repoint

```csharp
public static void WritePointer(Rom rom, int imageIndex, int address);
```

`address = 0xC00000 + fileOffset` (linear HiROM; matches 2,707/2,714 existing
pointers). Assert `Mask(address) == fileOffset` before writing.

### C.7 `Core/SpriteImporter.cs` — orchestration

```csharp
public static ImportResult Import(Rom rom, int imageIndex, int[,] pose,
                                  ImportOptions options, ImportLedger ledger);
```

1. `SpriteSlot.Read` → origin = `(PlacementMinX, PlacementMinY)` (M2 Q4).
2. `SpriteTiler.Build(pose, originX, originY)`.
3. Budget check: `CharCount > 88` → refuse `ExceedsCharBudget`. OAM over 37 → warn.
4. `SpriteEncoder.Serialize`.
5. **Geometry drift report** (Part G).
6. `FreeSpace.Allocate(bytes.Length)`.
7. `rom.WriteBytes(offset, bytes)`; `GfxTable.WritePointer`.
8. Append to ledger. **Never** zero the old slot.

---

## Part D — CLI

```
dotnet run -- <rom> --import <pose.png> --index <hex> --out <rom.sfc>
                    [--palette <name>] [--dry-run] [--force]
dotnet run -- <rom> --verify-m2b
```

- `--out` is **required** for any write; refuse if it resolves to the input path
  unless `--force`.
- `--dry-run` runs steps 1–6 and prints the plan (tiled size, chars, OAM, chosen
  offset, drift report) without writing. Should be the documented first step.
- `--palette` defaults to `Donkey Kong 1P`, resolved via `PalettePointers.Table`.

---

## Part E — Verification

**V2b (blocking gate for M2b, headless).** Per import:

1. **Pixel** — re-decode the *written* ROM at the new pointer via
   `TilerHarness.DecodeIndexCanvas`; must equal the source pose at its origin.
2. **Pointer** — `Read24(BaseAddress + index)` equals the expected address, and masks
   to the allocated offset.
3. **Containment** — byte-diff the written ROM against the input snapshot. The changed
   set must be **exactly** the allocated range ∪ the 3 pointer bytes. This is the gate
   that catches a stray write, and it is the reason `Snapshot()` exists.
4. **Non-destruction** — the original slot's bytes are unchanged.
5. **Ledger** — no two allocations overlap; all within scanned free runs; none crosses
   a bank boundary.

**Corpus.** Reuse the M2a trick: the game's own decoded poses are known-good input.
- *Isolated:* for each of the 2,714 indices, import that sprite's own pose into a
  **fresh ROM copy** and run gates 1–5. (Capacity forbids doing this cumulatively:
  2,714 × 756 B ≫ 77 KB.)
- *Cumulative:* one run importing poses into a single ROM until exhaustion, to exercise
  ledger reuse, fragmentation, and a clean `NoFreeSpace` refusal.
  **Utilisation floor (≥75%):** gates 1–5 pass whether the run exhausts at 17 poses or
  84, so the cumulative result also asserts that the bytes actually written are a
  reasonable share of the free space consumed. This is the only gate that catches
  free-space being *discarded* rather than filled; it was verified to fail (17%) when
  the re-scan bug is reintroduced.

**V3 (emulator, follow-up — does not block the headless gate).** Load a written ROM in
the libretro harness and confirm the sprite renders in-game. Required before trusting
*any* import for gameplay, for two reasons that V2b cannot see: free-space runs are
only *presumed* unused, and pose geometry can be pixel-perfect yet wrong in play
(Part G). The harness does not exist yet; M2b lands on V2b, as M1 did.

---

## Part F — Error taxonomy (all refusals, no partial writes)

| Code | Trigger |
|---|---|
| `UnmappedColor` | opaque pixel not in the target palette (lists colors + counts) |
| `PoseTooLarge` | PNG exceeds the 256×256 canvas |
| `ExceedsCharBudget` | tiler output > 88 chars |
| `NoFreeSpace` | no run fits → "needs M3 (expansion)" |
| `IndexNotInTable` | image index absent from the GFX pointer table |
| `LedgerMismatch` | ledger's `sourceRomSha256` ≠ input ROM |

Refusals happen **before** any byte is written. An import either completes fully or
changes nothing.

---

## Part G — Pose geometry drift (and why hitboxes are a real hazard)

The Q4 origin rule places the imported pose at the **replaced slot's** bbox top-left,
which silently assumes the new pose occupies roughly the same volume as the old one.
For a redesigned pose that assumption breaks. The source sheet's author re-posed
things deliberately — the transcript (~14:37) describes replacing DK's *overhand*
barrel throw with an *underhand* one — and a barrel carried in front of the body
instead of above the head moves the sprite's occupied volume substantially.

Two distinct problems follow, and V2b's pixel compare sees **neither**, because it
compares the import against itself:

1. **Placement drift.** Different bbox extent against a fixed origin renders the
   character shifted relative to where the game expects it.
2. **Stale hitboxes.** The collision box is a *separate* table — pointer at
   `0xbb8000` indexed by `imageIndex / 2`, 8-byte `x,y,w,h` record at `0xbb0000 + ptr`
   (PRD §7, `Form1.HitboxAndZoom.cs`). M2b does not touch it, so a re-posed frame
   keeps the **old** pose's collision box.

**Required in M2b (cheap, headless):** the import result reports, and `--dry-run`
prints, the new pose's bbox origin/extent against the replaced slot's, flagging any
delta > 4 px per axis, plus the current hitbox record for that index so the drift is
visible next to it. **Not in M2b:** editing hitboxes. This is a report, not a fix —
but the report is what makes the drift discoverable before it reaches gameplay, and it
is the concrete argument for prioritising the V3 harness after M2b.

---

## Part H — Decisions & open questions

**Resolved (2026-07-25)**
- **Write strategy — always relocate + repoint.** Never in-place (Part A: 1.7% fit).
- **Free space — auto-scan + ledger**, end-of-bank padding class only (C.4/C.5).
- **Pointer encoding — `0xC00000 + fileOffset`**, verified against 2,707/2,714.
- **Origin — replaced slot's *placement* bbox top-left** (not the pixel bbox).
- **Old slot bytes — left intact**, for reversibility.
- **Aliasing — report, never refuse** (harmless under relocation).
- **Gate — V2b headless blocks M2b; V3 tracked as the next follow-up.**

**Still open**
- **Q2 (from M2) pose → slot mapping.** M2b takes an explicit `--index`; the manifest
  is M4's problem. Unchanged.
- **Chained imports.** Using a written ROM as the *input* of a later run re-introduces
  the re-scan trap (its free runs now hold sprites) and trips the ledger's
  `sourceRomSha256` check. M4's batch mode should import N poses in **one** invocation
  against one pristine scan; multi-session chaining needs the scan to consult the
  ledger, and is unsolved.
- **Hitbox authoring** — needed for re-posed frames (Part G). Candidate follow-up PRD;
  auto-derivable from the opaque-pixel bbox.
- **Free-run trust** — the end-of-bank-padding heuristic is unverified until V3.

---

## Part I — Risks

| Risk | Mitigation |
|---|---|
| "Free" space is actually read by the game | End-of-bank padding only; sprite-overlap filter; ledger records everything; V3 before gameplay trust |
| Stray write corrupts unrelated data | V2b gate 3 diffs the whole ROM and requires an exact changed-set match |
| Allocation crosses a bank boundary (DMA fault) | Explicit assertion in the allocator; run-end alignment makes it structural |
| Ledger applied to the wrong base ROM | `sourceRomSha256` check → `LedgerMismatch` |
| Re-posed frame breaks gameplay/hitbox | Part G drift report; hitbox follow-up; V3 |
| Free space exhausted mid-sheet | Clean `NoFreeSpace` refusal → M3 |
| Free space silently discarded rather than filled | Scan once from the pristine ROM; Part E utilisation floor |

---

## Appendix — implementation order & tooling

Suggested order (each step independently testable):

1. `Rom` write path + `Snapshot`/`Clone` (C.1) — no behavior change to existing gates.
2. `SpriteSlot` (C.3) and `FreeSpace` scan (C.4) — pure reads, assert against the
   `--stats-m2b` numbers in Part A.
3. `PoseLoader` (C.2) — round-trip a decoded sprite exported as PNG.
4. `ImportLedger` (C.5), `GfxTable.WritePointer` (C.6), `SpriteImporter` (C.7).
5. `--verify-m2b` harness (Part E), then the CLI (Part D).

**Tooling:** `Core/M2bFeasibility.cs` + `dotnet run -- <rom> --stats-m2b` reproduces
every number in Part A. It stays in the tree as the regression check for the free-space
scan — step 2's assertions should be written against its output.
