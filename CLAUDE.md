# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows Forms (.NET Framework 4.7.2) graphics editor for the SNES game *Donkey Kong Country 1*. It opens a DKC1 ROM (`.smc`/`.sfc`), decodes its sprite graphics, palettes, and animations, lets the user edit them, and writes the bytes back into the ROM. The assembly name is `StandAloneGFXDKC1`; namespace is `StandAloneGFXDKC1`. The README describes it as "Messy Code" — expect informal style, commented-out dead code, `//FIXME`/`//FIX?` markers, and hard-coded magic addresses throughout. This is the tool's normal state, not something to "clean up" unless asked.

## Build / run

- Windows-only. Requires MSBuild + .NET Framework 4.7.2 (Visual Studio or Build Tools). There is no test suite, linter, or CI.
- Build: `msbuild StandAloneGFXDKC1.csproj /p:Configuration=Release` (or open in Visual Studio and build).
- A prebuilt `StandAloneGFXDKC1.exe` is committed at the repo root and is the primary distributed artifact (linked from the README).
- **Caveat:** `StandAloneGFXDKC1.csproj` references `Properties\AssemblyInfo.cs`, `Properties\Resources.resx`, and `Properties\Settings.settings`, but the `Properties/` folder is **not present** in the repo. A clean checkout will not build until those generated files exist (Visual Studio recreates them, or they must be added). Keep this in mind before assuming a build failure is your change's fault.

## Architecture

Two long-lived object graphs, connected in `Form1`'s constructor:

- **`ROM`** (`ROM.cs` + partials `ROM.Palette.cs`, `ROM.Palette.Pointers.cs`, `ROM.Sprites.cs`) — owns the ROM bytes and all decode/encode logic. It is the model.
- **`Form1`** (`Form1.cs` + many `Form1.*.cs` partials) — the single main window and all UI event handlers. It is the controller/view. `Form1.Designer.cs` is huge (auto-generated WinForms layout) — do not hand-edit it; change the UI through the designer.

`Program.cs` just runs `new Form1()`. On construction `Form1` creates a `ROM` and tries to auto-load the last ROM path from persisted settings; failure disables the Edit menu rather than crashing.

### ROM byte access (`ROM.cs`)
Central `Read8/16/24/32` and `Write8/16/…/WriteArr/WriteString` helpers over an in-memory `List<byte> rom`. Every access masks the address: `address &= (address > 0x7fffff ? 0x3fffff : 0xffffff)` — this maps SNES LoROM-style bank addresses (e.g. `0xbbcc9c`) down into the file. Use these helpers rather than indexing `rom` directly; they encode the game's addressing convention. `backupRom` holds the last-saved state; `IsROMChanged()` diffs against it and drives the unsaved-changes prompts. Loading validates the SNES header title against `"DONKEY KONG COUNTRY  "` (or a known hack title) and strips a 0x200-byte copier header if the file is 0x400200 bytes.

### Sprite decoding (`ROM.Sprites.cs`) — the core of the app
`ReadFromSpriteHeader(address, palette)` is the heart. DKC sprites are stored as an 8-byte **header** (counts of 2x2 and 1x1 char groups + DMA/VRAM placement info — see the byte-by-byte comment in that file) followed by 4bpp 8x8 char (tile) data. The method:
1. Reads the header, builds a `List<Tiles>` (2x2 = 16x16, 1x1 = 8x8 placements) and a "chars to render" list.
2. Emulates SNES VRAM layout via `SetupVRAM` into `_vram` (rows of 16 chars).
3. `DecodeChar` turns each 0x20-byte 4bpp char into an 8x8 `Bitmap` using the palette; the whole sprite composites onto a 256x256 bitmap.

`Tiles`, `CharsToRender`, and `VRAM` are the key data structures. `pass preview=true` reuses the already-loaded `@char` buffer instead of re-reading. This encoding is intricate and address-arithmetic-heavy; change it carefully and verify visually against a real ROM.

### Palettes (`ROM.Palette*.cs`)
`palettePointers` (`ROM.Palette.Pointers.cs`) is a hard-coded `Dictionary<string,int>` mapping human names (enemies, Kongs, objects) to ROM addresses — this is the authoritative sprite/palette catalog. Colors are SNES 15-bit BGR (`xbbbbbgggggrrrrr`); `ReadPalette` unpacks to `Color` (index 0 forced transparent, 15 colors read), `ConvertColorToSNES`/`WritePaletteToROM` pack back. Preserve the `>>3`/`<<3` 5-bit rounding when touching color code.

### Animations (`Animation.cs`, `AnimationIndex.cs`, `Form1.Animation*.cs`)
`Animation` parses a bytecode animation script starting from a pointer table (`0xbe8572` normal, `0xbcc388` alt). Commands `0x80–0x91` have per-command operand counts in `animationParams`; commands `< 0x80` are timed frame entries. Each `AnimationIndex` decodes to a frame `Bitmap` via `ReadFromSpriteHeader`, and can be edited/re-serialized (`ApplyStringToArr`, `WriteIndexToROM`, `ReplaceImage`, `ReplaceGFXPointer`). `Form1.gfxArray` (`0xbbcc9c`) is the base of the GFX pointer table used to resolve frame images. `Form1.Animation.Dkc2Import.cs` handles importing animation data from DKC2.

### UI tabs (`Form1.*.cs` partials)
The main `tabControl_sprites` has three modes, and most handlers branch on `tabControl_sprites.SelectedIndex`:
- **0 = Palette/GFX view** (`Form1.PaletteEdit.cs`, `Form1.PalettePreview.cs`)
- **1 = Image/tile editing** (`Form1.Art.cs` — pen/line/fill/replace tools, `Form1.Image.Header.cs`, `Form1.Image.TileInfo.cs`, `Form1.HitboxAndZoom.cs`)
- **2 = Animation** (`Form1.Animation.cs`)

Global hotkeys are wired by recursively attaching handlers to every control (`AddGlobalHotkeyToAll`); `GlobalHotkey` dispatches F3–F9 to different actions depending on the active tab. A `timer_100ticks` provides debouncing (the `timer`/`timerConstant` fields).

### Persistence (`StoredData.cs`)
`StoredData` is a hand-rolled INI-like store in `SDName.rbs` (categories in `[brackets]`, `key=value` lines) held as a `Dictionary<string,Dictionary<string,string>>`. Used for the last ROM path and import bookkeeping (e.g. the `Dixie` category maps imported `.bin` filenames to sprite pointers). Call `SaveRbs()` after `Write(...)` to persist; `RefreshRbs()` reloads from disk.

### Import/export flow
Sprites export as raw `.bin` (`button_export_Click`) and import back (`ImportFile`/`WriteArrFromFile`), with a bulk-import mode iterating a folder. `Form1.cs` also has PNG import (`SimplifyImage`/`SimplifiedColor`) that snaps arbitrary image colors to the nearest palette entry by squared RGB distance.

## Gotchas

- **Hard-coded absolute paths exist** in dead/experimental code (e.g. `ReadPaletteFromCustomFile` references `E:\OneDrive\...`). Don't treat these as configuration; they're leftovers.
- **`Version.cs`** phones home to a Pastebin URL to check for updates and pops MessageBoxes. The auto-check on load is commented out in `Form1`'s constructor; `ManualCheck()` is menu-triggered.
- Address arithmetic mixes SNES bank addresses and file offsets freely — always route reads/writes through the `ROM.Read*/Write*` masking helpers.
- No automated verification exists: validate changes by building on Windows and loading a real DKC1 ROM in the GUI.
