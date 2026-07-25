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
//   dotnet run -- <rom.smc|.sfc> --verify-m2a
//
// Always writes palette.png for the chosen palette (default "Donkey Kong 1P").
// If a raw GFX address + tile count are given, also writes tiles.png: a sheet of
// consecutive 0x20-byte chars, 8 per row. Compare both against the Windows tool.
// If an image index (into the gfxArray pointer table) is given, also writes
// sprite.png: the full composited sprite via SpriteDecoder.Decode.
//
// --gate1/--gate2/--verify-m1 run the M1 round-trip gates (specs/m1-encoder-spec.md
// Part D) against every valid sprite in the GFX pointer table instead of rendering.
//
// --verify-m2a runs the M2a tiler gate (specs/m2-importer-spec.md Part D, V2) against
// a battery of synthetic index-grid poses; a ROM path is still required as args[0]
// but is not otherwise used by this mode.
//
// --import/--verify-m2b (specs/m2b-writer-spec.md Part D/E): the write + repoint path.
//   dotnet run -- <rom> --import <pose.png> --index <hex> --out <rom.sfc>
//                       [--palette <name>] [--dry-run] [--force]
//   dotnet run -- <rom> --verify-m2b

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run -- <rom.smc|.sfc> [paletteName] [gfxHexAddr tileCount] [imageIndexHex]");
    Console.Error.WriteLine("   or: dotnet run -- <rom.smc|.sfc> --gate1|--gate2|--verify-m1|--verify-m2a|--verify-m2b");
    Console.Error.WriteLine("   or: dotnet run -- <rom.smc|.sfc> --import <pose.png> --index <hex> --out <rom.sfc> [--palette <name>] [--dry-run] [--force]");
    return 1;
}

var rom = Rom.Load(args[0]);
Console.WriteLine($"Header title : \"{rom.HeaderTitle.TrimEnd()}\"");
Console.WriteLine($"Looks DKC1?  : {rom.LooksLikeDkc1}");

if (args.Length >= 2 && (args[1] == "--gate1" || args[1] == "--gate2" || args[1] == "--verify-m1"))
{
    return RunVerification(rom, args[1]);
}

if (args.Length >= 2 && args[1] == "--verify-m2a")
{
    return RunVerifyM2a(rom);
}

if (args.Length >= 2 && args[1] == "--stats")
{
    return RunStats(rom);
}

if (args.Length >= 2 && args[1] == "--stats-m2b")
{
    return M2bFeasibility.Run(rom);
}

if (args.Length >= 2 && args[1] == "--verify-m2b")
{
    return RunVerifyM2b(rom);
}

if (args.Length >= 2 && args[1] == "--import")
{
    return RunImportCli(rom, args[0], args);
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

// Battery of synthetic index-grid poses exercising the tiler's branches: baseline
// all-1x1, full 2x2 refinement, the leftover/fallback path when qualifying blocks
// aren't a multiple of 8, non-8-aligned canvas dimensions, and both budget caps.
static int RunVerifyM2a(Rom rom)
{
    var cases = new List<(string name, int[,] pixels, int ox, int oy)>();

    {
        var px = new int[8, 8];
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                px[r, c] = (r + c) % 16;
        px[0, 0] = 0; // keep at least one transparent pixel
        cases.Add(("one-cell-pattern", px, 0, 0));
    }

    {
        const int cells = 3;
        var px = new int[cells * 8, cells * 8];
        for (int cr = 0; cr < cells; cr++)
        {
            for (int cc = 0; cc < cells; cc++)
            {
                if ((cr + cc) % 2 != 0) continue;
                for (int r = 0; r < 8; r++)
                    for (int c = 0; c < 8; c++)
                        px[cr * 8 + r, cc * 8 + c] = 1 + ((r * 8 + c) % 15);
            }
        }
        cases.Add(("sparse-checkerboard", px, 0, 0));
    }

    cases.Add(("baseline-16-cells", FillOpaque(4, 4), 0, 0));       // K=16 < OAM budget: no 2x2 conversion
    cases.Add(("full-refine-64-cells", FillOpaque(8, 8), 5, 9));    // K=64 > OAM budget: all 16 blocks convert

    {
        // K > 37 but a scattered pattern with few/no fully-occupied aligned 2x2
        // blocks: exercises the "usable rounds down to 0" fallback (soft OAM warn).
        const int cells = 10;
        var px = new int[cells * 8, cells * 8];
        for (int cr = 0; cr < cells; cr++)
        {
            for (int cc = 0; cc < cells; cc++)
            {
                if ((cr * 7 + cc * 3) % 4 == 0) continue;
                for (int r = 0; r < 8; r++)
                    for (int c = 0; c < 8; c++)
                        px[cr * 8 + r, cc * 8 + c] = 1 + ((cr + cc + r + c) % 15);
            }
        }
        cases.Add(("scattered-leftover", px, 0, 0));
    }

    {
        const int h = 20, w = 13;
        var px = new int[h, w];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                px[r, c] = (r / 3 + c / 2) % 2 == 0 ? 1 + ((r + c) % 15) : 0;
        cases.Add(("non-multiple-of-8", px, 0, 0));
    }

    cases.Add(("over-char-budget-144-cells", FillOpaque(12, 12), 0, 0)); // K=144 > 88 hard cap

    int pass = 0, fail = 0;
    foreach (var (name, px, ox, oy) in cases)
    {
        var r = TilerHarness.RunCase(name, px, ox, oy);
        string status = r.Passed ? "PASS" : "FAIL";
        string budgetNote = (r.ExceedsCharBudget ? " [EXCEEDS 88-char cap]" : "") +
                             (r.ExceedsOamBudget ? " [over 37-OAM soft budget]" : "");
        Console.WriteLine($"[{status}] {name}: chars={r.CharCount}, oam={r.OamEntries}{budgetNote}");
        if (r.Passed) { pass++; }
        else { fail++; Console.WriteLine("  " + r.FailureDetail); }
    }

    Console.WriteLine($"M2a synthetic: {pass}/{pass + fail} cases passed.");

    var romResult = M2aRomHarness.Run(rom);
    Console.WriteLine(
        $"M2a real-ROM corpus: {romResult.PassIndices}/{romResult.TotalIndices} passed, " +
        $"{romResult.FailIndices} failed, {romResult.EmptySkipped} empty-skipped " +
        $"({romResult.ExceedsCharBudgetCount} over char cap, {romResult.ExceedsOamBudgetCount} over OAM budget).");
    foreach (var f in romResult.Failures.Take(20)) Console.WriteLine("  FAIL " + f);

    bool ok = fail == 0 && romResult.FailIndices == 0;
    Console.WriteLine(ok ? "M2a verification: PASS" : "M2a verification: FAIL");
    return ok ? 0 : 1;
}

static int[,] FillOpaque(int cellRows, int cellCols)
{
    var px = new int[cellRows * 8, cellCols * 8];
    for (int r = 0; r < px.GetLength(0); r++)
        for (int c = 0; c < px.GetLength(1); c++)
            px[r, c] = 1 + ((r + c) % 15);
    return px;
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

// V2b (specs/m2b-writer-spec.md Part E): isolated corpus (each of the 2,714 real indices,
// its own pose, into a fresh ROM copy) + cumulative corpus (~80 poses into one ROM, until
// free space is exhausted). Both run gates 1-5; this is the blocking gate for M2b.
static int RunVerifyM2b(Rom rom)
{
    var isolated = M2bVerification.RunIsolated(rom);
    Console.WriteLine($"V2b isolated    : {isolated.PassIndices}/{isolated.TotalIndices} passed, " +
                       $"{isolated.FailIndices} failed, {isolated.EmptySkipped} empty-skipped, " +
                       $"{isolated.ExceedsCharBudgetSkipped} exceeds-char-budget-skipped.");
    foreach (var f in isolated.Failures.Take(20)) Console.WriteLine("  FAIL " + f);

    var cumulative = M2bVerification.RunCumulative(rom);
    Console.WriteLine($"V2b cumulative  : {cumulative.Imported} imported, " +
                       $"NoFreeSpace hit: {cumulative.NoFreeSpaceHit}, {cumulative.Failures.Count} gate failures, " +
                       $"utilisation {cumulative.Utilisation:P0} " +
                       $"({cumulative.BytesWritten}/{cumulative.FreeBytesAtStart} bytes).");
    foreach (var f in cumulative.Failures.Take(20)) Console.WriteLine("  FAIL " + f);

    bool ok = isolated.FailIndices == 0 && cumulative.Failures.Count == 0 && cumulative.NoFreeSpaceHit;
    Console.WriteLine(ok ? "V2b verification: PASS" : "V2b verification: FAIL");
    return ok ? 0 : 1;
}

// --import (specs/m2b-writer-spec.md Part D): PNG pose -> index grid -> tiler -> serialize ->
// allocate free space -> write -> repoint -> ledger. --dry-run runs the same steps 1-6 and
// prints the plan without touching the ROM buffer or the disk.
static int RunImportCli(Rom rom, string romPath, string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: dotnet run -- <rom> --import <pose.png> --index <hex> --out <rom.sfc> [--palette <name>] [--dry-run] [--force]");
        return 1;
    }

    string posePath = args[2];
    string? indexHex = null, outPath = null;
    string paletteName = "Donkey Kong 1P";
    bool dryRun = false, force = false;

    for (int i = 3; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--index": indexHex = args[++i]; break;
            case "--out": outPath = args[++i]; break;
            case "--palette": paletteName = args[++i]; break;
            case "--dry-run": dryRun = true; break;
            case "--force": force = true; break;
            default:
                Console.Error.WriteLine($"unknown argument '{args[i]}'");
                return 1;
        }
    }

    if (indexHex == null)
    {
        Console.Error.WriteLine("--index is required");
        return 1;
    }
    int imageIndex = Convert.ToInt32(indexHex, 16);

    if (!dryRun)
    {
        if (outPath == null)
        {
            Console.Error.WriteLine("--out is required for any write (use --dry-run to preview without writing)");
            return 1;
        }
        if (Path.GetFullPath(outPath) == Path.GetFullPath(romPath) && !force)
        {
            Console.Error.WriteLine("--out resolves to the input ROM path; pass --force to overwrite the input, or choose a different --out.");
            return 1;
        }
    }

    if (!PalettePointers.Table.TryGetValue(paletteName, out int palAddr))
    {
        Console.Error.WriteLine($"Unknown palette '{paletteName}'.");
        return 1;
    }
    var palette = Palette.Read(rom, palAddr);

    PoseResult pose;
    try { pose = PoseLoader.Load(posePath, palette); }
    catch (ImportException ex) { Console.Error.WriteLine($"[{ex.Code}] {ex.Message}"); return 1; }

    foreach (var w in pose.Unmapped)
        Console.WriteLine($"WARNING: opaque pixel matching palette[0] (forced-transparent) forced to index 0: " +
                           $"RGB({w.Color.Red},{w.Color.Green},{w.Color.Blue}) x{w.Count}, first at ({w.FirstX},{w.FirstY})");

    var working = rom.Clone(); // imports work on a copy (C.1); the loaded original is never mutated
    string sha = ImportLedger.ComputeSha256(rom.Snapshot());

    string ledgerPath = ImportLedger.SidecarPath(outPath ?? romPath);
    ImportLedger ledger;
    try { ledger = ImportLedger.LoadOrCreate(ledgerPath, sha); }
    catch (ImportException ex) { Console.Error.WriteLine($"[{ex.Code}] {ex.Message}"); return 1; }

    ImportResult result;
    try
    {
        result = SpriteImporter.Import(working, imageIndex, pose.Pixels,
            new ImportOptions { DryRun = dryRun, Source = Path.GetFileName(posePath) }, ledger);
    }
    catch (ImportException ex)
    {
        Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
        return 1;
    }

    PrintImportPlan(result);

    if (dryRun)
    {
        Console.WriteLine("Dry run: no bytes written.");
        return 0;
    }

    working.Save(outPath!);
    ledger.Save(ledgerPath);
    Console.WriteLine($"Wrote {outPath}");
    Console.WriteLine($"Ledger {ledgerPath}");
    return 0;
}

static void PrintImportPlan(ImportResult r)
{
    Console.WriteLine($"Image index          : 0x{r.ImageIndex:X}");
    Console.WriteLine($"Tiled size           : 0x{r.Serialized.Length:X} bytes");
    Console.WriteLine($"Chars / OAM entries  : {r.CharCount} chars, {r.OamEntries} OAM entries" +
                       (r.ExceedsOamBudget ? " [over 37-OAM soft budget]" : ""));
    Console.WriteLine($"Allocated offset     : 0x{r.AllocatedOffset:X} (pointer 0x{r.NewPointerAddress:X})");
    Console.WriteLine($"Previous pointer     : 0x{r.PreviousPointer:X}");
    if (r.Slot.AliasIndices.Count > 0)
        Console.WriteLine($"Aliased indices      : {string.Join(", ", r.Slot.AliasIndices.Select(i => $"0x{i:X}"))} (still point at the untouched original)");

    var d = r.Drift;
    Console.WriteLine($"Geometry drift       : old bbox ({d.OldMinX},{d.OldMinY})-({d.OldMaxX},{d.OldMaxY}), " +
                       $"new bbox ({d.NewMinX},{d.NewMinY})-({d.NewMaxX},{d.NewMaxY})" +
                       (d.ExceedsThreshold ? "  [DRIFT > 4px]" : ""));
    Console.WriteLine($"Current hitbox       : x={r.Hitbox.SignedX} y={r.Hitbox.SignedY} w={r.Hitbox.Width} h={r.Hitbox.Height} " +
                       $"(pointer 0x{r.Hitbox.PointerAddress:X} -> 0x{r.Hitbox.RecordAddress:X})");
}

