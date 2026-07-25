# V3 Spec: headless emulator harness

**Status:** ✅ gate passing on **snes9x and bsnes-mercury (accurate)** (2026-07-25) —
`--verify-v3`. Capture points are save states, so a full run takes ~3 s.
**Accuracy cross-check: achieved.** Every core problem in this investigation had one
root cause, and it was in this harness, not in any core (Part D.2).
**Depends on:** M2b (`--import` writes + repoints).
**Answers:** the two questions no headless gate can reach — is the "free" space we
write into genuinely unused, and does an imported sprite actually render in play?

---

## Part A — Findings from the spike

Measured on arm64 macOS with `snes9x_libretro.dylib`, driving the core directly via
P/Invoke (no RetroArch, no window, no GL context, no audio device).

| Property | Result |
|---|---|
| Throughput | **948 fps** headless (~16× realtime); 2,700 frames ≈ 3 s |
| Pixel format | RGB565 (negotiated via `SET_PIXEL_FORMAT`) |
| Frame size | **varies by screen** — 512×224 during the hi-res intro, 256×224 in-game |
| Determinism | verified: byte-identical PNGs across repeated runs, on both snes9x and bsnes-mercury |
| ROM mapping | core logs `Map_HiROMMap` — **independent confirmation** of the HiROM `0xC00000 + fileOffset` pointer convention M2b relies on |

### Boot script (USA Rev 2), found with `--emu-explore`

A Start tap every 300 frames walks the intro without needing each screen's exact
timing. Landmarks:

| Frame | Screen |
|---|---|
| 600 | "Nintendo Presents" |
| 1,500 | "SELECT A GAME" file select |
| **2,700** | **In-game, Jungle Hijinxs, DK standing at the hut** — the default capture point |

At frame 2,700 the scene is static and a Kong sprite is on screen, which is exactly
what a frame comparison needs.

---

## Part B — The gate is two-sided, and must stay that way

A one-sided emulator check is worthless here, and it is worth being explicit about why.

**Negative control — relocation must be invisible.** Import a sprite's *own* pose back
into its index (M2b relocates it into free space and repoints), boot both ROMs, compare
frame 2,700:

> **Result: `identical`.** A byte-different ROM — sprite relocated to `0x7A22`, pointer
> rewritten — plays pixel-for-pixel the same.

**Positive control — a changed sprite must be visible.** On its own, "identical" is
unfalsifiable: it is equally consistent with *"the sprite is fine"* and with *"this
index is never drawn here, so the test proves nothing."* So the harness also builds a
**vandalised ROM** (`--emu-vandal`): the first 60 indices re-imported with the same
silhouette but every colour flattened to one palette index — same budgets, same
geometry, unmistakable on screen.

> **Result: 901 px (1.57%) differ, bbox (55,129)–(92,168)** — a 38×40 box exactly where
> DK stands. Everything else (background, bananas, crate) unchanged.

The pair is the gate. Neither half means anything alone.

### Capture points are save states

The gate restores a per-core save state rather than replaying an input script to a
frame number (`--emu-state` to capture one, `port/emu/states/`, gitignored). This
removes the entire class of problems above — no tap can mistime, no cross-core drift
applies — and a full three-ROM gate run drops from ~30 s to **~3 s**.

The state carries CPU/PPU/WRAM/VRAM but **not the ROM**, so one captured on the
baseline is restored while running each control ROM: all three start from a
byte-identical machine state. No core rejected a state captured from a different ROM.

**The VRAM caveat is real and is why the gate walks.** A restored state's VRAM still
holds the tiles the *original* ROM had DMA'd, so an imported sprite is invisible until
the game re-uploads that frame. Standing still, DKC never re-DMAs an idle Kong: the
gate ran, passed gate 1, and failed gate 2 with the sprite genuinely not on screen.
Holding Right during the settle forces new animation frames, each a fresh DMA from the
(possibly modified) GFX table — and gate 2 then sees the vandalised sprite. This is
also why the per-core golden must be captured through the *same* settle path as the
gate, or it compares different scenes.

Choosing a state is a judgement call that has to be eyeballed: mercury's first state
sat near the level exit, so 240 settle frames walked DK out onto the world map.

### Gate 0 — the reference frame, and why a heuristic isn't enough

**This gate exists because the first version of it failed.** Gate 0 originally just
counted distinct colours in the baseline frame, on the theory that a black or crashed
screen would be uniform. Running the gate against `bsnes_mercury_balanced` then
reported **PASS** — on frames that were completely garbled (wrong palette, vertical
striping, see D.2). The garbage had 65 distinct colours, so the heuristic passed it;
both control ROMs were *equally* garbled, so gates 1 and 2 dutifully compared garbage
to garbage and agreed.

Every "does this look like a game screen?" heuristic has that failure mode. The gate
now compares the baseline against a **per-core reference frame that a human has
looked at** (`port/emu/goldens/<core>-f<frame>.png`, captured with `--emu-golden`).
A core with no golden **fails** — an unverified core is an unverified result, and
"fail closed" is the only safe default for a check whose job is catching garbage.

Goldens are **gitignored**: they are frames of a copyrighted game, held to the same
rule `.gitignore` already applies to the ROM. Capture locally, inspect, then run.

### What this does and does not prove about free space

60 sprites were written into the scanned free runs and the game booted, played, and
rendered normally. That is real evidence the end-of-bank padding is unused — far better
than "the bytes were constant" — **but it covers only the runs actually allocated and
only the code paths the script exercises.** Booting to Jungle Hijinxs does not exercise
every bank. A stronger claim needs broader play (more levels, more Kongs, the bonus
screens); this is the honest limit of the current gate.

---

## Part C — Components (built)

| File | Role |
|---|---|
| `Core/Emulator/LibretroCore.cs` | P/Invoke host: loads the core, wires the 6 callbacks, negotiates pixel format, boots a ROM **from memory**, runs frames, scripted input, `retro_serialize` |
| `Core/Emulator/FrameCapture.cs` | framebuffer → `SKBitmap`/PNG, plus `Compare` returning differing-pixel count **and the diff bbox** |
| `Core/Emulator/BootScript.cs` | the deterministic power-on script and its frame landmarks |
| `Core/Emulator/V3Verification.cs` | the gate itself: builds both control ROMs, captures, asserts gates 0-2 |
| `Core/Emulator/StateCapture.cs` | save-state capture points: create, restore, settle |
| `port/emu/fetch-cores.sh` | fetches snes9x + bsnes_mercury_balanced (cores are **not** committed) |

Two implementation details that are load-bearing:

- **`retro_get_system_av_info` must be called after `retro_load_game`.** The spec says
  so, and bsnes-family cores render garbage or nothing without it. It is called inside
  `LoadGame` so no code path can forget (Part D.2).
- **Always hand the core a ROM path**, temp file if needed: bsnes cores derive their
  save-RAM path from it and behave differently without one.
- **Delegates must be rooted.** The core keeps raw function pointers; a collected
  delegate is a crash the GC schedules for an arbitrary later frame. `LibretroCore`
  holds them in a list for its lifetime.
- **The diff bbox is the assertion, not the pixel count.** "1.57% of pixels changed"
  says nothing; "changed, and every changed pixel is inside the sprite's screen rect"
  is the claim worth gating on.

### CLI (spike modes)

```
--emu-boot     [--core P] [--frames N] [--out shot.png]      boot + dump a frame
--emu-explore  --outdir D [--frames N] [--every N] [--no-input] filmstrip; finds capture points
--emu-diff     <romB> [--frames N] [--outdir D]              two ROMs, same script, diff
--emu-vandal   --out <rom.sfc> [--count N] [--colour I]      positive-control ROM
--emu-state    --name N [--core P] [--frames N] [--tap-window N] [--hold-right N]
--emu-golden   [--core P] [--frames N] [--settle N] [--no-walk]  gate 0's reference frame
--verify-v3    [--core P] [--count N] [--frames N]           the gate
```

Frame landmarks are **core-specific**, with or without input (D.2). Gate 0's per-core
golden makes that visible instead of silently comparing unrelated scenes.

The host also serves **core options**: `SET_VARIABLES` is parsed and `GET_VARIABLE`
answers with each option's declared default. Previously it answered "unset" for
everything, leaving cores on whatever their uninitialised option state happened to be.
(This turned out not to be mercury's problem, but a frontend that ignores core options
is wrong regardless.) `--emu-boot` prints the declared options, the geometry log, and
any environment command the host refused — the diagnostics that made D.2 solvable.

---

## Part D — Remaining work

1. ~~Wire `--verify-v3`~~ ✅ done — gates 0-2 in `Core/Emulator/V3Verification.cs`.
2. ~~Accuracy cross-check~~ ✅ **done — one harness bug explained everything.**

   **Root cause: the harness never called `retro_get_system_av_info()` after
   `retro_load_game()`.** The libretro spec requires it, and bsnes-family cores rely on
   it to finish initialising. Without it they ran the game but rendered garbage or
   nothing at all.

   It hid for so long because exactly one code path called it — `--emu-boot`, which
   called it only to *print* the AV info. That path rendered perfectly; the gate,
   `--emu-explore` and state capture did not call it and were all broken. Two runs with
   provably identical parameters therefore disagreed, which is what finally isolated it.

   After the fix, **all six cores render DKC1 correctly**:

   | Core | Before | After |
   |---|---|---|
   | `snes9x 1.63` | ✅ | ✅ |
   | `bsnes-mercury v094 Balanced` | garbled in-game | ✅ |
   | `bsnes-mercury v094 Accuracy` | garbled in-game | ✅ |
   | `bsnes 115` | black | ✅ (also needs `need_fullpath`) |
   | `bsnes2014 v094 Balanced` | black | ✅ |
   | `bsnes2014 v094 Accuracy` | black | ✅ |

   **The gate passes on both snes9x and bsnes-mercury Balanced, and they agree to within
   one pixel**: vandal diff 866 px / bbox (57,122)-(93,162) on snes9x, 865 px / bbox
   (56,121)-(93,161) on mercury. An accurate core now confirms what the fast one reports.

   Two earlier diagnoses in this spec were wrong and are corrected here: mercury was
   never garbling (harness bug), and the cross-core frame divergence — while real and
   worth avoiding — was not the cause of the garbling either.

3. **Broaden free-space coverage** (Part B): more capture points across more levels, so
   more banks are exercised.
4. **Save states.** `retro_serialize` is bound but unused — replaying 45 s of script
   costs ~3 s, so it is not yet worth it. It becomes worth it when capture points are
   deep in a level.

### Out of reach for a frame diff — the barrel/hitbox case

A static frame comparison verifies that a sprite *renders*. It cannot verify that a
re-posed frame *plays* correctly: DK carrying a barrel in front instead of above his
head is pixel-perfect and still wrong, because the hitbox lives in a separate table
M2b never touches (`m2b-writer-spec.md` Part G). Catching that needs scripted gameplay
plus state inspection (did the collision fire?), which is a different and larger
harness. V3 as specified does **not** cover it; the drift report remains the only
signal, and it is a report, not a check.

---

## Part E — Risks

| Risk | Mitigation |
|---|---|
| Frame size differs between capture points (512×224 vs 256×224) | `Compare` throws on size mismatch rather than silently rescaling; capture points are fixed constants |
| snes9x accepts something real hardware wouldn't | **Closed** — the gate passes on bsnes-mercury too, agreeing to within one pixel (D.2) |
| A restored state hides an imported sprite (stale VRAM) | The gate walks during settle to force re-DMA; gate 2 fails loudly if the sprite never appears |
| Comparing frames across cores by frame number | Invalid: cores drift (11% → 82% by frame 1,800 with no input). Compare within one core; goldens are per-core |
| A broken/garbled core silently "passes" the gate | Gate 0 golden-frame check, failing closed on any core without an inspected reference (Part B) |
| Core nondeterminism breaks A/B comparison | Verified deterministic; no core options overridden, so defaults are used on every run |
| Downloaded cores drift (nightly builds) | `fetch-cores.sh` pins the platform path; cores are gitignored, so a rebuild is explicit |
| "Free space is unused" over-claimed from one level | Stated explicitly in Part B; broaden coverage before trusting it wholesale |
