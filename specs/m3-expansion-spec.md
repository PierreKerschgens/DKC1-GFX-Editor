# M3 — ROM expansion (research spec)

**Status:** research complete, **not implemented**. The probe is in the tree
(`--stats-m3`, `--verify-m3`); the writer is not.
**Predecessors:** `m2b-writer-spec.md` (relocate + repoint), `m2c-free-space-spec.md`
(pool at 92 KB / 99 poses), `v3-emulator-spec.md` (the emulator gate this reuses).
**Supersedes:** the PRD's FR7, which says expansion means appending banks and bumping the
size byte. That is wrong for this ROM, and the failure is total — see B.1.

---

## Part A — What the header settles

```
$FFD5 map mode   : 0x31  (HiROM + FastROM)
$FFD7 rom size   : 0x0C  -> 4 MB
file size        : 0x400000 (4 MB, header agrees)
$FFD8 sram size  : 0x01  -> 2048 bytes
$FFDE checksum   : 0x2BCC, computed 0x2BCC  (match)
$FFDC complement : 0xD433  (consistent)
```

DKC1 is **already at the 4 MB HiROM ceiling**. There is nothing to append. Going further means
**ExHiROM** (map mode `$35`), which maps the extra space into banks `$40–$7D` — a mapping only a
couple of commercial games ever used.

**Checksum rule** (measured, not assumed): the SNES checksum is the plain 16-bit sum of every ROM
byte — sum = `0x2BCC` = stored `$FFDE`, and `$FFDC` holds its complement. The stored fields need
no special-casing, because a consistent complement/checksum pair contributes `0xFF + 0xFF` to the
sum regardless of its value. 4 MB and 8 MB are both powers of two, so M3 never hits the split-sum
case that non-power-of-two images require.

### A.1 The invariant M0–M2b inherit — and it survives

The concern that motivated this spec: `Rom.Mask()` is `addr & 0x3FFFFF`, and M2b's repoint rule is
`pointer = 0xC00000 + fileOffset`, verified against 2,707/2,714 real pointers. If ExHiROM changed
address translation under them, every earlier milestone would silently half-work.

It does not. Comparing `Rom.Mask()` against ExHiROM translation bank by bank, they disagree in
**exactly one range**:

| banks | under ExHiROM | agrees with `Mask()`? |
|---|---|---|
| `$C0–$FF` | file 0x000000–0x3FFFFF (first 4 MB) | yes |
| `$80–$BF:8000-FFFF` | mirror of the first 4 MB | yes |
| `$40–$7D` | file 0x400000+ (**the new space**) | yes — `Mask()` is identity here |
| `$00–$3F:8000-FFFF` | file 0x400000+ (mirror) | **no** — `Mask()` returns a first-4MB offset |

Two things fall out, both load-bearing:

1. **ExHiROM does not move the original 4 MB.** It stays at `$C0–$FF`. The counter-intuitive part
   of ExHiROM is that the *high* banks keep the *first* half.
2. **`Mask()` is already correct for the new space.** `$40–$7D` translates identically under both
   maps, so `Mask()` needs no change — only the `0xC00000 + fileOffset` rule does, and only for
   offsets ≥ 4 MB.

And the pointer census says nothing is exposed to the one disagreement:

```
$C0-$FF (unchanged)              : 2707 pointers
$80-$BF (unchanged)              :    7 pointers  (0x8AB539 … 0x8AEECD)
$40-$7D (becomes extended space) :    0
$00-$3F (MEANING CHANGES)        :    0
```

**Zero GFX pointers live in the one range whose meaning changes.** The invariant holds; M3 extends
the pointer rule rather than breaking it.

---

## Part B — What the cores say

`--verify-m3` runs four experiments from the jungle save state, each isolating one variable,
because "the expanded ROM is broken" is not a usable finding.

### B.1 X1 — the naive expansion is dead on arrival

8 MB, size byte `0x0D`, map mode `$35`, checksum recomputed → **black screen from power-on**
(90.6 % of pixels differ, full-screen bbox; confirmed by booting the dumped ROM with no input at
all and looking at the frame).

The cause is not the size byte or the checksum. **The 65816 fetches its reset and IRQ vectors from
bank `$00` unconditionally.** Under ExHiROM, `$00:FFFC` maps to file `0x40FFFC` — inside the
freshly appended, zero-filled half. The console resets to `$0000` and executes nothing. The reset
vector it *should* find points at `$00:8000`, which maps to `0x408000`, also zeros.

Two details worth keeping:

- snes9x logs `Map_ExtendedHiROMMap` even when the map-mode byte still says `$31`. It picks
  ExHiROM off the 8 MB **file size** and ignores the header byte, so "leave the mode alone" is not
  an option that exists.
- This is what FR7 describes, and it produces a ROM that never draws a frame. Not a subtle
  regression — a brick.

### B.2 X2 — a 32 KB mirror fixes it

Copy file `0x008000–0x00FFFF` (bank `$00`'s upper half: vectors + boot code) to `0x408000`, where
ExHiROM will look for it. Result: **frame identical to baseline** on both cores tested. 32 KB out
of the 4 MB gained.

### B.3 X3 — the new space is genuinely addressable

60 sprites relocated to file 0x410000+ and repointed into `$40–$7D`:

| | snes9x | bsnes-mercury-balanced |
|---|---|---|
| X3a same pixels | identical | identical |
| X3b recoloured | 866 px, bbox (57,122)-(93,162) | 865 px, bbox (56,121)-(93,161) |

X3b's difference is the same sprite, in the same place, at the same size that V3's gate2 finds
when the sprite lives in *normal* space (866 px, identical bbox). ExHiROM extended space behaves
exactly like the first 4 MB.

X3 is two-sided for the reason V3's gate is: "identical after relocating into extended space" is
equally consistent with "the new space works" and with "the pointers were ignored and the game
drew the originals". Only the recoloured control separates them.

**The probe caught itself being wrong twice**, and both are recorded because both are the kind of
thing that ships silently:

- An early version scored X3b as PASS while the console showed a **black screen** — "differs" was
  all it checked, and a dead ROM differs. Now a positive result must also be *localised*
  (thresholds borrowed from V3 gate2).
- The first working run allocated from `0x400000`, overwriting the mirror it had just installed at
  `0x408000`. The console came up alive but in a different video mode. Hence `ExtendedStart =
  0x410000`: bank `$40` is reserved whole.

### B.4 Coverage limits

- Two of six cores. The other four (bsnes, bsnes2014 ×2, bsnes-mercury-accuracy) need their own
  save state + human-inspected golden first — cheap, not free.
- One capture point (jungle), so this tests boot + one scene. Same ceiling as M2c Part D.
- **No hardware, and no flashcart.** Everything above is emulator behaviour. ExHiROM support on
  real hardware and on flashcarts varies more than it does across emulators, and nothing here
  speaks to it.

---

## Part C — What implementing M3 would require

Not started. In dependency order:

1. **`Expansion.Build`** — exists (size, map mode, size byte, checksum, low-bank mirror). Needs:
   refusal when the base is not 4 MB HiROM; a `--expand` path that writes a ledger entry so the
   expansion itself is recorded and reversible.
2. **Pointer rule** — `GfxTable.PointerFor(fileOffset)` exists: `0xC00000 + offset` below 4 MB,
   `offset` at or above it. `WritePointer` refuses an extended pointer when the ROM is too small
   to contain it, so a `$40–$7D` pointer can never be written into a stock 4 MB image.
   *(These two are already in the tree because the probe needed them; M2b/V3 gates re-run green.)*
3. **Allocator** — extended runs start at `0x410000`, end at `0x7E0000` (banks `$7E/$7F` are WRAM;
   the last 128 KB of an 8 MB image is reachable only through the `$00–$3F` mirror, the one range
   where `Mask()` disagrees — stay out of it). Usable: **0x3D0000, ~3.8 MB**.
4. **Free-space policy** — with 3.8 MB of *known*-empty space, the M2c scan/promotion machinery
   stops mattering for new imports. Prefer extended space and leave the 92 KB of in-ROM padding
   alone, rather than mixing the two.
5. **Gate** — `--verify-m3` extended to all six cores, plus a re-run of V2b cumulative against an
   expanded ROM.

**Capacity:** 3.8 MB at the p90 sprite size (0x4AE) is roughly **3,400 poses**, against 99 today.
Expansion is not a capacity tweak, it is ~40× the current pool. The question is never "is it
enough" — only "does it still run".

---

## Part D — Recommendation

**Do not build M3 yet.** The research retires the risk that mattered (the map change does not
break M0–M2b, and the new space demonstrably works on two accurate cores), and that result keeps.
What has not been established is *need*: nothing has yet hit the 99-pose ceiling, because no real
character sheet has been imported.

M4 (batch import) is still the next thing. It is what turns "99 poses" from a number into a
verdict, it is independent of M3, and if a character set fits in 92 KB then the ExHiROM decision —
the one change that can make the ROM unbootable on hardware nobody here can test — never has to be
taken at all.

If it does have to be taken, Part C is the plan and Part B is the evidence it rests on.

---

## Part E — Risks

| Risk | Status / mitigation |
|---|---|
| Map change breaks existing pointers | **Retired** — A.1: zero pointers in the one range that changes; X2 identical on two cores |
| Expanded ROM does not boot | **Understood** — B.1/B.2: 32 KB low-bank mirror, verified |
| New space not actually readable | **Retired for 2 cores** — B.3, two-sided |
| Allocation clobbers the mirror | `ExtendedStart = 0x410000`, bank `$40` reserved whole |
| `$00–$3F` mirror region used by mistake | Allocator capped at `0x7E0000`; `Mask()` disagreement documented in A.1 |
| Checksum left stale | Recomputed on every build; rule verified against the stock ROM |
| Other four cores reject ExHiROM | **Open** — needs a state + golden each |
| Real hardware / flashcart rejects ExHiROM | **Open, and untestable here.** The one risk that emulator work cannot close |
| Built before it is needed | Part D: M4 first |
