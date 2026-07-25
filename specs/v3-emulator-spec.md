# V3 Spec: headless emulator harness

**Status:** harness working (2026-07-25); gate not yet wired as `--verify-v3`.
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
| Determinism | same script from power-on → same frame, every run |
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
| `port/emu/fetch-cores.sh` | fetches snes9x + bsnes_mercury_balanced (cores are **not** committed) |

Two implementation details that are load-bearing:

- **Delegates must be rooted.** The core keeps raw function pointers; a collected
  delegate is a crash the GC schedules for an arbitrary later frame. `LibretroCore`
  holds them in a list for its lifetime.
- **The diff bbox is the assertion, not the pixel count.** "1.57% of pixels changed"
  says nothing; "changed, and every changed pixel is inside the sprite's screen rect"
  is the claim worth gating on.

### CLI (spike modes)

```
--emu-boot     [--core P] [--frames N] [--out shot.png]      boot + dump a frame
--emu-explore  --outdir D [--frames N] [--every N]           filmstrip; finds capture points
--emu-diff     <romB> [--frames N] [--outdir D]              two ROMs, same script, diff
--emu-vandal   --out <rom.sfc> [--count N] [--colour I]      positive-control ROM
```

---

## Part D — Remaining work

1. **Wire `--verify-v3`**: run the control pair automatically and fail on either
   (relocation not identical / vandal not visibly different in the expected rect).
2. **Cross-check on bsnes.** `bsnes_mercury_balanced` is downloaded but untested; snes9x
   is permissive, so anything it accepts should be confirmed on the accurate core before
   being trusted.
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
| snes9x accepts something real hardware wouldn't | Cross-check on bsnes (Part D.2) |
| Core nondeterminism breaks A/B comparison | Verified deterministic; no core options overridden, so defaults are used on every run |
| Downloaded cores drift (nightly builds) | `fetch-cores.sh` pins the platform path; cores are gitignored, so a rebuild is explicit |
| "Free space is unused" over-claimed from one level | Stated explicitly in Part B; broaden coverage before trusting it wholesale |
