# V3 Spec: headless emulator harness

**Status:** ✅ gate wired and passing on snes9x (2026-07-25) — `--verify-v3`.
**Accuracy cross-check: unresolved** — see Part D.2; no accurate core renders DKC1
correctly in this harness yet, and the gate now *fails closed* on them rather than
passing.
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
--emu-golden   [--core P] [--frames N]                       capture gate 0's reference frame
--verify-v3    [--core P] [--count N] [--frames N]           the gate
```

Frame landmarks are **core-specific**: the same script lands on different game state on
different cores (bsnes-mercury at frame 2,700 is elsewhere than snes9x). Gate 0's
per-core golden makes that visible instead of silently comparing unrelated scenes.

---

## Part D — Remaining work

1. ~~Wire `--verify-v3`~~ ✅ done — gates 0-2 in `Core/Emulator/V3Verification.cs`.
2. **Accuracy cross-check — attempted, unresolved.** Four cores tried on arm64 macOS:

   | Core | `need_fullpath` | Result |
   |---|---|---|
   | `snes9x 1.63` | false | ✅ renders correctly; the gate's reference core |
   | `bsnes-mercury v094 (Balanced)` | false | ⚠️ intro renders **perfectly**, in-game is garbled (wrong palette + vertical striping) |
   | `bsnes 115` | **true** | ❌ black screen in-game |
   | `bsnes2014 v094 (Balanced)` | false | ❌ black screen in-game |

   Ruled out by measurement, not assumption: **pixel format** (mercury negotiates
   XRGB8888, snes9x RGB565; both handled, and mercury's intro is pixel-perfect, so the
   conversion path is right); **pitch/geometry** (snes9x renders into a 512-wide buffer
   and reports 256 — the row-stride handling is already correct for both); and
   **`need_fullpath`** (now honoured — `bsnes 115` requires a real file on disk, which
   the harness writes to a temp path — but it stays black anyway, so that was not the
   cause).

   Remaining suspects, untested: core options answered as "unset" via `GET_VARIABLE`
   (mercury exposes PPU/renderer options), an unhandled environment command these cores
   require, or missing `SET_SYSTEM_AV_INFO` handling on a mid-run resolution change.
   **Until this is resolved, "accurate-core verified" is not a claim this project can
   make** — snes9x is permissive, and V3 currently only proves things about snes9x.
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
| snes9x accepts something real hardware wouldn't | **Open** — no accurate core works in this harness yet (D.2). Current V3 results are snes9x-only |
| A broken/garbled core silently "passes" the gate | Gate 0 golden-frame check, failing closed on any core without an inspected reference (Part B) |
| Core nondeterminism breaks A/B comparison | Verified deterministic; no core options overridden, so defaults are used on every run |
| Downloaded cores drift (nightly builds) | `fetch-cores.sh` pins the platform path; cores are gitignored, so a rebuild is explicit |
| "Free space is unused" over-claimed from one level | Stated explicitly in Part B; broaden coverage before trusting it wholesale |
