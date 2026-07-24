using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using DkcTool.Core;

// Phase 1 decode harness. Proves the ROM -> palette -> 4bpp char -> Skia -> PNG
// pipeline end-to-end on macOS, with no WinForms and no System.Drawing.
//
//   dotnet run -- <rom.smc|.sfc> [paletteName] [gfxHexAddr tileCount] [imageIndexHex]
//   dotnet run -- <rom.smc|.sfc> --gate1|--gate2|--verify-m1
//
// Always writes palette.png for the chosen palette (default "Donkey Kong 1P").
// If a raw GFX address + tile count are given, also writes tiles.png: a sheet of
// consecutive 0x20-byte chars, 8 per row. Compare both against the Windows tool.
// If an image index (into the gfxArray pointer table) is given, also writes
// sprite.png: the full composited sprite via SpriteDecoder.Decode.
//
// --gate1/--gate2/--verify-m1 run the M1 round-trip gates (specs/m1-encoder-spec.md
// Part D) against every valid sprite in the GFX pointer table instead of rendering.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run -- <rom.smc|.sfc> [paletteName] [gfxHexAddr tileCount] [imageIndexHex]");
    Console.Error.WriteLine("   or: dotnet run -- <rom.smc|.sfc> --gate1|--gate2|--verify-m1");
    return 1;
}

var rom = Rom.Load(args[0]);
Console.WriteLine($"Header title : \"{rom.HeaderTitle.TrimEnd()}\"");
Console.WriteLine($"Looks DKC1?  : {rom.LooksLikeDkc1}");

if (args.Length >= 2 && (args[1] == "--gate1" || args[1] == "--gate2" || args[1] == "--verify-m1"))
{
    return RunVerification(rom, args[1]);
}

if (args.Length >= 2 && args[1] == "--stats")
{
    return RunStats(rom);
}

if (args.Length >= 3 && args[1] == "--inspect")
{
    return RunInspect(rom, Convert.ToInt32(args[2], 16));
}

// --- 1. Palette swatch (always) ---
string palName = args.Length >= 2 ? args[1] : "Donkey Kong 1P";
if (!PalettePointers.Table.TryGetValue(palName, out int palAddr))
{
    Console.Error.WriteLine($"Unknown palette '{palName}'.");
    Console.Error.WriteLine("Known: " + string.Join(", ", PalettePointers.Table.Keys.Take(8)) + ", ...");
    return 1;
}
var palette = Palette.Read(rom, palAddr);
SavePaletteStrip(palette, "palette.png");
Console.WriteLine($"Wrote palette.png  '{palName}' @ 0x{palAddr:X}");

// --- 2. Raw GFX tile sheet (optional) ---
if (args.Length >= 4)
{
    int gfxAddr = Convert.ToInt32(args[2], 16);
    int count = int.Parse(args[3]);
    SaveTileSheet(rom, gfxAddr, count, palette, "tiles.png");
    Console.WriteLine($"Wrote tiles.png    {count} tiles @ 0x{gfxAddr:X}");
}

// --- 3. Full composited sprite via SpriteDecoder (optional) ---
if (args.Length >= 5)
{
    int imageIndex = Convert.ToInt32(args[4], 16);
    int spriteAddr = GfxTable.ResolveSpriteAddress(rom, imageIndex);
    using var sprite = SpriteDecoder.Decode(rom, spriteAddr, palette);
    Save(sprite, "sprite.png");
    Console.WriteLine($"Wrote sprite.png   image index 0x{imageIndex:X} -> 0x{spriteAddr:X}");
}

return 0;

static void SavePaletteStrip(SKColor[] palette, string path)
{
    const int cell = 24;
    using var bmp = new SKBitmap(cell * palette.Length, cell);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(SKColors.Magenta); // index 0 is transparent -> shows through
        using var paint = new SKPaint();
        for (int i = 0; i < palette.Length; i++)
        {
            paint.Color = palette[i];
            canvas.DrawRect(i * cell, 0, cell, cell, paint);
        }
    }
    Save(bmp, path);
}

static void SaveTileSheet(Rom rom, int address, int count, SKColor[] palette, string path)
{
    const int cols = 8;
    int rows = (count + cols - 1) / cols;
    using var sheet = new SKBitmap(cols * 8, rows * 8);
    using (var canvas = new SKCanvas(sheet))
    {
        canvas.Clear(SKColors.Transparent);
        for (int i = 0; i < count; i++)
        {
            byte[] chr = rom.ReadBytes(address + i * 0x20, 0x20);
            using var tile = CharDecoder.Decode(chr, palette);
            canvas.DrawBitmap(tile, (i % cols) * 8, (i / cols) * 8);
        }
    }
    Save(sheet, path);
}

static void Save(SKBitmap bmp, string path)
{
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(path);
    data.SaveTo(fs);
}

// Dumps the exact VRAM-grid structure + per-entry cell assignment for one sprite,
// to ground the M2 2x2 encoder design.
static int RunInspect(Rom rom, int imageIndex)
{
    int addr = GfxTable.ResolveSpriteAddress(rom, imageIndex);
    byte[] d = SpriteDecoder.ReadSpriteBytes(rom, addr);
    int b0 = d[0], b1 = d[1], b2 = d[2], b3 = d[3], b4 = d[4], b5 = d[5], b6 = d[6], b7 = d[7];
    Console.WriteLine($"idx 0x{imageIndex:X} @ 0x{addr:X}  header: b0={b0} b1={b1} b2={b2} b3={b3} b4={b4} b5={b5} b6=0x{b6:X} b7={b7}");

    // Grid shape per SetupVram.
    var rowLen = new List<int>();
    int ci = 0;
    while (ci < b5) { int len = Math.Min(16, b5 - ci); rowLen.Add(len); ci += len; }
    while (rowLen.Count <= (b6 >> 4)) { rowLen.Add(0); rowLen.Add(0); }
    int g2row = b6 >> 4;
    int g2start = rowLen[g2row];
    for (int i = 0; i < b7; i++) rowLen[g2row]++;
    Console.WriteLine($"grid rows={rowLen.Count} lens=[{string.Join(",", rowLen)}]  group2 -> row {g2row} cols {g2start}..{g2start + b7 - 1}");

    bool InB(int r, int c) => r < rowLen.Count && c < rowLen[r];
    string Src(int r, int c) => !InB(r, c) ? "OOB" : (r == g2row && c >= g2start ? "g2" : "g1");

    int p = 8;
    Console.WriteLine("2x2 entries (r,c walk from 0,0):");
    int rr = 0, cc = 0;
    for (int n = 0; n < b0; n++)
    {
        int x = d[p++], y = d[p++];
        Console.WriteLine($"  [{n}] (x{x},y{y}) cells TL({rr},{cc}){Src(rr, cc)} TR({rr},{cc + 1}){Src(rr, cc + 1)} " +
                          $"BL({rr + 1},{cc}){Src(rr + 1, cc)} BR({rr + 1},{cc + 1}){Src(rr + 1, cc + 1)}");
        cc += 2; if (cc == 16) { rr += 2; cc = 0; }
    }
    Console.WriteLine($"1x1 group1 (start r={b2 >> 4},c={b2 & 0xf}):");
    rr = b2 >> 4; cc = b2 & 0xf;
    for (int n = 0; n < b1; n++)
    {
        int x = d[p++], y = d[p++];
        Console.WriteLine($"  [{n}] (x{x},y{y}) cell ({rr},{cc}){Src(rr, cc)}");
        cc++; if (cc == 16) { rr++; cc = 0; }
    }
    return 0;
}

// Mines the VRAM/char budget envelope from every sprite the game itself uses.
static int RunStats(Rom rom)
{
    var chars = new List<int>();
    var vramRows = new List<int>();
    int n = 0, maxChars = 0, maxCharsIdx = 0, maxB5 = 0, maxB7 = 0, maxSize = 0;
    int maxRows = 0, maxRowsIdx = 0, maxEntries = 0, maxRender = 0;
    int maxW = 0, maxWIdx = 0, maxH = 0, maxHIdx = 0, maxB6row = 0;

    foreach (int idx in GfxTable.EnumerateImageIndices(rom))
    {
        int addr = GfxTable.ResolveSpriteAddress(rom, idx);
        byte[] data;
        try { data = SpriteDecoder.ReadSpriteBytes(rom, addr); } catch { continue; }
        int b0 = data[0], b1 = data[1], b3 = data[3], b5 = data[5], b6 = data[6], b7 = data[7];

        int c = b5 + b7;
        int entries = b0 + b1 + b3;
        int render = b0 * 4 + b1 + b3;
        int size = 8 + entries * 2 + (b5 << 5) + b7 * 0x20;
        int rowsG1 = b5 > 0 ? (b5 - 1) / 16 + 1 : 0;
        int rowG2 = b7 > 0 ? (b6 >> 4) : -1;
        int rows = Math.Max(rowsG1, rowG2 + 1);

        // pixel bounding box from placement (x,y) pairs
        int p = 8, minX = 999, minY = 999, mX = 0, mY = 0; bool any = false;
        void Acc(int count, int sz) { for (int k = 0; k < count && p + 1 < data.Length; k++) { int x = data[p++], y = data[p++]; any = true; minX = Math.Min(minX, x); minY = Math.Min(minY, y); mX = Math.Max(mX, x + sz); mY = Math.Max(mY, y + sz); } }
        Acc(b0, 16); Acc(b1, 8); Acc(b3, 8);
        int w = any ? mX - minX : 0, h = any ? mY - minY : 0;

        n++; chars.Add(c); vramRows.Add(rows);
        if (c > maxChars) { maxChars = c; maxCharsIdx = idx; }
        if (b5 > maxB5) maxB5 = b5;
        if (b7 > maxB7) maxB7 = b7;
        if (size > maxSize) maxSize = size;
        if (rows > maxRows) { maxRows = rows; maxRowsIdx = idx; }
        if (entries > maxEntries) maxEntries = entries;
        if (render > maxRender) maxRender = render;
        if (w > maxW) { maxW = w; maxWIdx = idx; }
        if (h > maxH) { maxH = h; maxHIdx = idx; }
        if ((b6 >> 4) > maxB6row) maxB6row = b6 >> 4;
    }

    chars.Sort(); vramRows.Sort();
    int Pct(List<int> l, double q) => l.Count == 0 ? 0 : l[(int)Math.Round(q * (l.Count - 1))];

    Console.WriteLine($"Sprites analyzed        : {n}");
    Console.WriteLine($"Chars/sprite (b5+b7)    : max {maxChars} (idx 0x{maxCharsIdx:X}), " +
                      $"p50 {Pct(chars, .5)}, p90 {Pct(chars, .9)}, p99 {Pct(chars, .99)}");
    Console.WriteLine($"  max group1 (b5)       : {maxB5}");
    Console.WriteLine($"  max group2 (b7)       : {maxB7}");
    Console.WriteLine($"VRAM rows (16 wide)     : max {maxRows} (idx 0x{maxRowsIdx:X}), " +
                      $"p50 {Pct(vramRows, .5)}, p90 {Pct(vramRows, .9)}, p99 {Pct(vramRows, .99)}");
    Console.WriteLine($"  max group2 start row  : {maxB6row}");
    Console.WriteLine($"Placement entries       : max {maxEntries} (b0+b1+b3)");
    Console.WriteLine($"Render placements       : max {maxRender} (b0*4+b1+b3)");
    Console.WriteLine($"Sprite data size        : max 0x{maxSize:X} ({maxSize}) bytes");
    Console.WriteLine($"Pixel bbox              : max W {maxW} (idx 0x{maxWIdx:X}), max H {maxH} (idx 0x{maxHIdx:X})");
    return 0;
}

static int RunVerification(Rom rom, string mode)
{
    bool runGate1 = mode == "--gate1" || mode == "--verify-m1";
    bool runGate2 = mode == "--gate2" || mode == "--verify-m1";
    bool ok = true;

    if (runGate1)
    {
        var g1 = RoundTripHarness.RunGate1(rom);
        Console.WriteLine($"Gate 1 (char codec): {g1.PassChars}/{g1.TotalChars} passed, {g1.FailChars} failed.");
        foreach (var f in g1.Failures.Take(20)) Console.WriteLine("  FAIL " + f);
        if (g1.FailChars > 0) ok = false;
    }

    if (runGate2)
    {
        var g2 = RoundTripHarness.RunGate2(rom);
        Console.WriteLine($"Gate 2 (sprite round-trip): {g2.PassIndices}/{g2.TotalIndices} passed, " +
                           $"{g2.FailIndices} failed, {g2.SkippedIndices} skipped.");
        foreach (var f in g2.Failures.Take(20)) Console.WriteLine("  FAIL " + f);
        foreach (var s in g2.Skips.Take(20)) Console.WriteLine("  SKIP " + s);
        if (g2.FailIndices > 0) ok = false;
    }

    Console.WriteLine(ok ? "M1 verification: PASS" : "M1 verification: FAIL");
    return ok ? 0 : 1;
}
