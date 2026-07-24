using System;
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
