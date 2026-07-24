# port/ — Phase 1 core-extraction sketch

A UI-free .NET 8 console tool that ports the **hardest, riskiest** part of the
DKC1 GFX Editor off Windows: the graphics pipeline. It proves the
`System.Drawing` → **SkiaSharp** swap works on macOS before any UI is written.

This folder is self-contained and does **not** touch the existing WinForms
project at the repo root.

## What's ported (and verifiable)

| Source (original)          | Port                     | Notes |
|----------------------------|--------------------------|-------|
| `ROM.cs` byte access       | `Core/Rom.cs`            | Non-mutating reads; same SNES bank→file address masking. |
| `ROM.Palette.cs`           | `Core/Palette.cs`        | SNES 15-bit BGR → `SKColor`; index 0 transparent. |
| `ROM.Palette.Pointers.cs`  | `Core/PalettePointers.cs`| Full name→address table, verbatim. |
| `ROM.DecodeChar`           | `Core/CharDecoder.cs`    | 4bpp 8x8 bitplane decode → `SKBitmap`. **The atomic unit.** |

The `Program.cs` harness ties them together and writes PNGs.

## Build & run

Requires the .NET 8 SDK (not installed on this machine yet):

```sh
brew install dotnet-sdk          # one-time
cd port/DkcTool
dotnet run -- /path/to/dkc1.smc                          # writes palette.png
dotnet run -- /path/to/dkc1.smc "Cranky Kong"            # a different palette
dotnet run -- /path/to/dkc1.smc "Donkey Kong 1P" 0x... 64  # + tiles.png (64 raw chars)
```

## How to verify (the whole point of Phase 1)

1. **palette.png** — compare the 16 swatches to the same palette in the Windows
   tool's palette view. Colors must match exactly (the 5-bit→8-bit `<<3` rounding
   is preserved).
2. **tiles.png** — point `gfxHexAddr` at a raw char region and compare the decoded
   8×8 grid to the tool's GFX view. Pixel-exact = the Skia swap is proven.

If both match, the imaging swap — the single biggest technical risk in the whole
port — is retired.

## Deliberately **not** in this sketch (Phase 2+)

- **`ReadFromSpriteHeader`** — the full sprite-header parse + VRAM emulation +
  2x2/1x1 char assembly (~200 lines in `ROM.Sprites.cs`). It builds on
  `CharDecoder`, so it slots in once the atomic decode is verified.
- **Animation** bytecode parsing (`Animation.cs`), palette/tile **writing** back to
  the ROM, PNG import/recolor.
- **All UI.** The eventual app would be Avalonia (which itself renders via Skia,
  so the `SKBitmap`s produced here display directly).

> Note: this sketch could not be compiled/run in the session that created it —
> the .NET SDK isn't installed here. Treat it as a reviewed starting scaffold;
> first `dotnet build` may surface a package-version nudge.
