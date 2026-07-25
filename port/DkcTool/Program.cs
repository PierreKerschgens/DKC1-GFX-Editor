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
//
// --slice (specs/m4-batch-spec.md C.1/C.5): sheet -> numbered (strip, position) poses, no writes.
//   dotnet run -- <rom> --slice <sheet.png> [--overlay out.png]
//
// --batch (specs/m4-batch-spec.md C.3/C.5): one manifest, one pristine scan, one ledger.
//   dotnet run -- <rom> --batch <manifest.json> --out <rom.sfc> [--dry-run] [--overlay out.png]
//
// --verify-m4 (specs/m4-batch-spec.md Part D): V4a-V4f, the blocking gate for M4.
//   dotnet run -- <rom> --verify-m4 [--all-cores]

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

if (args.Length >= 2 && args[1] == "--stats-freespace")
{
    return FreeSpaceResearch.Run(rom);
}

if (args.Length >= 2 && args[1] == "--stats-m4")
{
    // dotnet run -- <rom> --stats-m4 <sheet.png> [<sheet.png> ...] [--palette <name>]
    string m4PalName = ArgValue(args, "--palette") ?? "Donkey Kong 1P";
    if (!PalettePointers.Table.TryGetValue(m4PalName, out int m4PalAddr))
    {
        Console.Error.WriteLine($"Unknown palette '{m4PalName}'.");
        return 1;
    }
    var m4Sheets = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).ToArray();
    if (m4Sheets.Length == 0)
    {
        Console.Error.WriteLine("--stats-m4 needs one or more sheet PNGs.");
        return 1;
    }
    return SheetResearch.Run(m4Sheets, Palette.Read(rom, m4PalAddr), m4PalName,
        ArgValue(args, "--overlay"));
}

if (args.Length >= 2 && args[1] == "--stats-anim")
{
    return AnimationTable.Run(rom);
}

if (args.Length >= 2 && args[1] == "--whose")
{
    // dotnet run -- <rom> --whose [seedIndexHex]   (default 0x8c: M0's confirmed DK sprite)
    string seed = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : "8c";
    return IndexOwnership.Run(rom, Convert.ToInt32(seed, 16), ArgValue(args, "--contact"),
                              ArgValue(args, "--palette") ?? "Donkey Kong 1P");
}

if (args.Length >= 3 && args[1] == "--captions")
{
    // dotnet run -- <rom> --captions <sheet.png> --out c.png [--from N] [--to N] [--scale N]
    string capSheet = args[2];
    string capOut = ArgValue(args, "--out") ?? "captions.png";
    int capFrom = int.Parse(ArgValue(args, "--from") ?? "0");
    int capTo = int.Parse(ArgValue(args, "--to") ?? int.MaxValue.ToString());
    int capScale = int.Parse(ArgValue(args, "--scale") ?? "3");
    int capH = int.Parse(ArgValue(args, "--capheight") ?? "20");
    int capW = int.Parse(ArgValue(args, "--capwidth") ?? "150");
    SheetSlicer.WriteCaptionSheet(capSheet, capOut, capFrom, capTo, capScale, capW, capH);
    Console.WriteLine($"Wrote {capOut} (strips {capFrom}..{capTo}, {capScale}x)");
    return 0;
}

if (args.Length >= 3 && args[1] == "--anims-in")
{
    // dotnet run -- <rom> --anims-in <loHex>..<hiHex> [--frames N]
    string[] aiBounds = args[2].Split("..");
    if (aiBounds.Length != 2)
    {
        Console.Error.WriteLine("--anims-in needs <loHex>..<hiHex>, e.g. 0x8C..0x950");
        return 1;
    }
    int aiFrames = int.TryParse(ArgValue(args, "--frames"), out int f) ? f : -1;
    return IndexOwnership.RunAnimsIn(rom, Convert.ToInt32(aiBounds[0], 16),
                                     Convert.ToInt32(aiBounds[1], 16),
                                     aiFrames < 0 ? (int?)null : aiFrames);
}

if (args.Length >= 3 && args[1] == "--near")
{
    // dotnet run -- <rom> --near <indexHex> [--count N] [--contact out.png] [--palette name]
    string nearPal = ArgValue(args, "--palette") ?? "Donkey Kong 1P";
    if (!PalettePointers.Table.TryGetValue(nearPal, out int nearAddr))
    {
        Console.Error.WriteLine($"Unknown palette '{nearPal}'."); return 1;
    }
    return IndexOwnership.RunNear(rom, Convert.ToInt32(args[2], 16),
                                  int.Parse(ArgValue(args, "--count") ?? "16"),
                                  ArgValue(args, "--contact"), Palette.Read(rom, nearAddr));
}

if (args.Length >= 3 && args[1] == "--baseline")
{
    // dotnet run -- <rom> --baseline <lo>..<hi> [--palette name]
    string[] blB = args[2].Split("..");
    if (blB.Length != 2) { Console.Error.WriteLine("--baseline needs <loHex>..<hiHex>"); return 1; }
    string blPal = ArgValue(args, "--palette") ?? "Donkey Kong 1P";
    if (!PalettePointers.Table.TryGetValue(blPal, out int blAddr))
    { Console.Error.WriteLine($"Unknown palette '{blPal}'."); return 1; }
    return IndexOwnership.RunBaseline(rom, Convert.ToInt32(blB[0], 16), Convert.ToInt32(blB[1], 16),
                                      Palette.Read(rom, blAddr));
}

if (args.Length >= 3 && args[1] == "--palette-sweep")
{
    // dotnet run -- <rom> --palette-sweep <indexHex> --out sweep.png
    return IndexOwnership.RunPaletteSweep(rom, Convert.ToInt32(args[2], 16),
                                          ArgValue(args, "--out") ?? "sweep.png",
                                          int.Parse(ArgValue(args, "--cell") ?? "108"));
}

if (args.Length >= 3 && args[1] == "--anim-sheet")
{
    // dotnet run -- <rom> --anim-sheet <lo>..<hi> --out s.png [--from N] [--to N] [--palette name]
    string[] asB = args[2].Split("..");
    if (asB.Length != 2) { Console.Error.WriteLine("--anim-sheet needs <loHex>..<hiHex>"); return 1; }
    string asPal = ArgValue(args, "--palette") ?? "Donkey Kong 1P";
    if (!PalettePointers.Table.TryGetValue(asPal, out int asAddr))
    {
        Console.Error.WriteLine($"Unknown palette '{asPal}'."); return 1;
    }
    return IndexOwnership.RunAnimSheet(rom,
        Convert.ToInt32(asB[0], 16), Convert.ToInt32(asB[1], 16),
        ArgValue(args, "--out") ?? "anim-sheet.png", Palette.Read(rom, asAddr),
        int.Parse(ArgValue(args, "--from") ?? "0"),
        int.Parse(ArgValue(args, "--to") ?? "999"),
        int.Parse(ArgValue(args, "--frames") ?? "24"),
        int.Parse(ArgValue(args, "--cell") ?? "46"),
        float.Parse(ArgValue(args, "--zoom") ?? "1"),
        Array.IndexOf(args, "--props") >= 0);
}

if (args.Length >= 3 && args[1] == "--contact-range")
{
    // dotnet run -- <rom> --contact-range <loHex>..<hiHex> --contact out.png [--palette <name>]
    // Renders an arbitrary index range, to see what sits between a component's runs.
    string[] bounds = args[2].Split("..");
    if (bounds.Length != 2)
    {
        Console.Error.WriteLine("--contact-range needs <loHex>..<hiHex>, e.g. 0xE0..0x200");
        return 1;
    }
    int lo = Convert.ToInt32(bounds[0], 16), hi = Convert.ToInt32(bounds[1], 16);
    string contactOut = ArgValue(args, "--contact") ?? "contact.png";
    string contactPal = ArgValue(args, "--palette") ?? "Donkey Kong 1P";
    if (!PalettePointers.Table.TryGetValue(contactPal, out int cpAddr))
    {
        Console.Error.WriteLine($"Unknown palette '{contactPal}'.");
        return 1;
    }
    var cpPalette = Palette.Read(rom, cpAddr);
    var cpValid = GfxTable.EnumerateImageIndices(rom).ToHashSet();
    // --stride N samples every Nth populated index, to scan a wide span in one sheet when
    // locating a block boundary; stride 1 (default) renders every index in the range.
    int cpStride = int.Parse(ArgValue(args, "--stride") ?? "1");
    var cpRange = Enumerable.Range(0, (hi - lo) / 4 + 1).Select(i => lo + i * 4)
                            .Where(cpValid.Contains)
                            .Where((_, i) => i % cpStride == 0).ToList();
    float cpZoom = float.Parse(ArgValue(args, "--zoom") ?? "1");
    IndexOwnership.WriteContactSheet(rom, cpRange, cpPalette, contactOut, cpZoom,
                                     int.Parse(ArgValue(args, "--cell") ?? "72"),
                                     int.Parse(ArgValue(args, "--cols") ?? "12"));
    Console.WriteLine($"Wrote {contactOut}: 0x{lo:X}..0x{hi:X}, {cpRange.Count} populated index(es), palette '{contactPal}'.");
    return 0;
}

if (args.Length >= 2 && args[1] == "--slice")
{
    // dotnet run -- <rom> --slice <sheet.png> [--overlay out.png]
    string? slicePath = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : null;
    if (slicePath == null)
    {
        Console.Error.WriteLine("--slice needs a sheet PNG.");
        return 1;
    }
    return SheetSlicer.Run(slicePath, ArgValue(args, "--overlay"));
}

if (args.Length >= 2 && args[1] == "--stats-m3")
{
    return Expansion.Run(rom);
}

if (args.Length >= 2 && args[1] == "--verify-m3")
{
    return RunVerifyM3(rom, args);
}

if (args.Length >= 2 && args[1] == "--expand")
{
    // Writes an expanded ROM so it can be booted by hand / by --emu-boot, and a ledger sidecar
    // recording the expansion (specs/m3-expansion-spec.md C.1) so it is reversible later via
    // Expansion.Revert -- expansion is not meant to be a one-way door.
    string expOut = ArgValue(args, "--out") ?? throw new ArgumentException("--expand needs --out");
    bool expMirror = Array.IndexOf(args, "--mirror") >= 0;
    byte expMode = ArgValue(args, "--map") is string m ? Convert.ToByte(m, 16) : Expansion.MapModeExHiRom;
    int expSize = Convert.ToInt32(ArgValue(args, "--size") ?? "0x800000", 16);

    var expRecord = Expansion.RecordFor(rom, expSize, expMode, expMirror);
    File.WriteAllBytes(expOut, Expansion.Build(rom, expSize, expMode, fixChecksum: true, mirrorLowBank: expMirror));

    string expLedgerPath = ImportLedger.SidecarPath(expOut);
    var expLedger = new ImportLedger { SourceRomSha256 = expRecord.OriginalSha256, RomExpansion = expRecord };
    expLedger.Save(expLedgerPath);

    Console.WriteLine($"wrote {expOut}: 0x{expSize:X} bytes, map mode 0x{expMode:X2}");
    Console.WriteLine($"wrote {expLedgerPath} (reversible via Expansion.Revert)");
    return 0;
}

if (args.Length >= 2 && args[1] == "--revert")
{
    // Undoes --expand from the ledger sidecar (specs/m3-expansion-spec.md C.1). The sha256 check
    // is the point, not a formality: it is what turns "reversible in principle" into a claim
    // this command proves every time it runs.
    string revOut = ArgValue(args, "--out") ?? throw new ArgumentException("--revert needs --out");
    string revLedgerPath = ArgValue(args, "--ledger") ?? ImportLedger.SidecarPath(args[0]);
    if (!File.Exists(revLedgerPath))
    {
        Console.Error.WriteLine($"no ledger at {revLedgerPath} -- an expansion can only be " +
                                "reverted from the record --expand wrote alongside it.");
        return 1;
    }

    var revLedger = ImportLedger.Load(revLedgerPath);
    bool hasImports = revLedger.Allocations.Count > 0;
    var revRecordOrNull = revLedger.RomExpansion;
    if (!hasImports && revRecordOrNull is null)
    {
        Console.Error.WriteLine($"ledger {revLedgerPath} records neither imports nor an expansion -- " +
                                "nothing to revert.");
        return 1;
    }

    // Imports first, then the expansion: restoring pointers is a first-4 MB edit, and an
    // expansion revert truncates everything above it. Doing it the other way round would drop
    // the pointers that still need restoring.
    var revWorking = rom.Clone();
    bool byteExact = true;
    if (hasImports)
    {
        var (revertedCount, refilled) = revLedger.RevertAllocations(revWorking);
        byteExact = refilled == revertedCount;
        Console.WriteLine($"reverted {revertedCount} import allocation(s); refilled {refilled}.");
        if (!byteExact)
            Console.WriteLine("  note: some allocations predate the ledger's fillByte field, so their " +
                              "bytes stay orphaned in the pool. Pointers are restored and the ROM is " +
                              "logically the original, but it is not byte-identical and no sha256 " +
                              "check can prove it.");
    }

    byte[] reverted = revWorking.Snapshot();
    string expectedSha = revLedger.SourceRomSha256;
    if (revRecordOrNull is Expansion.ExpansionRecord revRecord)
    {
        reverted = Expansion.Revert(reverted, revRecord);
        expectedSha = revRecord.OriginalSha256;
    }

    string revSha = ImportLedger.ComputeSha256(reverted);
    bool shaMatches = string.Equals(revSha, expectedSha, StringComparison.OrdinalIgnoreCase);
    if (byteExact && !string.IsNullOrEmpty(expectedSha) && !shaMatches)
    {
        Console.Error.WriteLine($"revert produced sha256 {revSha}, expected {expectedSha}. " +
                                "Every allocation was restorable, so this should have reproduced the " +
                                "original exactly -- the image was modified by something other than " +
                                "this ledger. Refusing to write.");
        return 1;
    }

    File.WriteAllBytes(revOut, reverted);
    if (revRecordOrNull is not null)
        Console.WriteLine($"reverted 0x{rom.Length:X} -> 0x{reverted.Length:X} bytes.");
    Console.WriteLine(shaMatches
        ? $"sha256 matches the original ({revSha[..16]}...)."
        : $"sha256 {revSha[..16]}... (not asserted -- see the note above).");
    Console.WriteLine($"wrote {revOut}");
    return 0;
}

if (args.Length >= 2 && args[1] == "--poison")
{
    return RunPoison(rom, args);
}

if (args.Length >= 2 && args[1] == "--emu-state")
{
    return RunEmuState(args[0], args);
}

if (args.Length >= 2 && args[1] == "--emu-golden")
{
    return RunEmuGolden(args[0], args);
}

if (args.Length >= 2 && args[1] == "--verify-v3")
{
    return RunVerifyV3(rom, args);
}

if (args.Length >= 2 && args[1] == "--emu-vandal")
{
    return RunEmuVandal(rom, args);
}

if (args.Length >= 2 && args[1] == "--emu-diff")
{
    return RunEmuDiff(args[0], args);
}

if (args.Length >= 2 && args[1] == "--emu-explore")
{
    return RunEmuExplore(args[0], args);
}

if (args.Length >= 2 && args[1] == "--emu-boot")
{
    return RunEmuBoot(args[0], args);
}

if (args.Length >= 2 && args[1] == "--verify-m2b")
{
    return RunVerifyM2b(rom);
}

if (args.Length >= 2 && args[1] == "--import")
{
    return RunImportCli(rom, args[0], args);
}

if (args.Length >= 2 && args[1] == "--batch")
{
    return RunBatchCli(rom, args);
}

if (args.Length >= 2 && args[1] == "--verify-m4")
{
    return M4Verification.Run(rom, args);
}

if (args.Length >= 3 && args[1] == "--inspect")
{
    return RunInspect(rom, Convert.ToInt32(args[2], 16));
}

if (args.Length >= 3 && args[1] == "--poison-index")
{
    // dotnet run -- <rom> --poison-index <lo>..<hi> --out <rom.sfc>
    //
    // A mapping oracle. Overwrites a range's *char data* with noise, leaving the header,
    // placement table, size and pointer untouched -- so every animation still plays exactly
    // as before and only the pixels become garbage. Boot it, perform actions, and whichever
    // action turns DK into static is drawn from this range.
    //
    // This exists because "does the new art appear?" turned out to be a question operators
    // cannot always answer (spec A.21): imported art can resemble stock closely enough, or be
    // placed off-screen-ish, or the tester may not have triggered the move at all. Garbage is
    // unmistakable, which makes a *negative* result trustworthy -- and a negative result is
    // what the whole strip->animation search keeps needing.
    //
    // Bisects: poison a wide range, and if the action garbles, halve it.
    string[] pzB = args[2].Split("..");
    if (pzB.Length != 2) { Console.Error.WriteLine("--poison-index needs <loHex>..<hiHex>"); return 1; }
    string pzOut = ArgValue(args, "--out") ?? throw new ArgumentException("--poison-index needs --out");
    int pzLo = Convert.ToInt32(pzB[0], 16), pzHi = Convert.ToInt32(pzB[1], 16);

    var pzSeen = new HashSet<int>();
    int pzCount = 0, pzBytes = 0;
    foreach (int index in DkcTool.Core.GfxTable.EnumerateImageIndices(rom))
    {
        if (index < pzLo || index > pzHi) continue;
        int addr = DkcTool.Core.GfxTable.ResolveSpriteAddress(rom, index);
        // Aliased indices share one sprite; poisoning it twice is harmless but miscounts.
        if (!pzSeen.Add(addr)) continue;

        byte[] head = rom.ReadBytes(addr, 8);
        int charStart = addr + 8 + 2 * (head[0] + head[1] + head[3]);
        int charLen = (head[5] << 5) + head[7] * 0x20;
        if (charLen <= 0) continue;

        var noise = new byte[charLen];
        for (int i = 0; i < charLen; i++)
            noise[i] = DkcTool.Core.Emulator.PoisonProbe.Noise(charStart + i);
        rom.WriteBytes(charStart, noise);
        pzCount++;
        pzBytes += charLen;
    }

    rom.Save(pzOut);
    Console.WriteLine($"Poisoned {pzCount} sprite(s) ({pzBytes} bytes of char data) in 0x{pzLo:X}..0x{pzHi:X}.");
    Console.WriteLine($"Wrote {pzOut}");
    Console.WriteLine("Headers, placements and pointers are untouched -- animations play as before,");
    Console.WriteLine("only the pixels are noise. Whichever action turns DK into static uses this range.");
    return 0;
}

if (args.Length >= 2 && args[1] == "--paint")
{
    // dotnet run -- <rom> --paint <lo>..<hi>:<colour> [--paint ...] --out <rom.sfc>
    //
    // --poison-index's discriminating sibling. Poisoning answers "is the roll in here?" one
    // range per boot, which costs log2(N) boots. Painting fills each range's char data with a
    // *constant* palette index instead of noise, so DK renders as a flat silhouette in a colour
    // that names the range he came from -- and one boot can separate as many ranges as there
    // are distinguishable colours.
    //
    // Same contract as --poison-index: header, placements, size and pointer untouched, so every
    // animation plays exactly as before.
    string ptOut = ArgValue(args, "--out") ?? throw new ArgumentException("--paint needs --out");
    var ptSpecs = new List<(int Lo, int Hi, int Colour)>();
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] != "--paint") continue;
        string[] halves = args[i + 1].Split(':');
        if (halves.Length != 2) { Console.Error.WriteLine($"--paint needs <lo>..<hi>:<colour>, got '{args[i + 1]}'"); return 1; }
        string[] bounds = halves[0].Split("..");
        if (bounds.Length != 2) { Console.Error.WriteLine($"--paint needs <lo>..<hi>:<colour>, got '{args[i + 1]}'"); return 1; }
        int colour = int.Parse(halves[1]);
        if (colour < 1 || colour > 15) { Console.Error.WriteLine("--paint colour must be 1..15 (0 is transparent)."); return 1; }
        ptSpecs.Add((Convert.ToInt32(bounds[0], 16), Convert.ToInt32(bounds[1], 16), colour));
    }
    if (ptSpecs.Count == 0) { Console.Error.WriteLine("--paint needs at least one <lo>..<hi>:<colour>."); return 1; }

    // SNES 4bpp planar, per CharDecoder: row i reads chr[2i]/chr[2i+1] for bitplanes 0/1 and
    // chr[2i+16]/chr[2i+17] for bitplanes 2/3. A constant colour means each bitplane is
    // uniformly on or off, so every byte is 0xFF or 0x00 by whether that bit of the index is set.
    static byte[] SolidChar(int colour)
    {
        var chr = new byte[0x20];
        for (int i = 0; i < 8; i++)
        {
            chr[2 * i] = (byte)(((colour >> 0) & 1) != 0 ? 0xFF : 0x00);
            chr[2 * i + 1] = (byte)(((colour >> 1) & 1) != 0 ? 0xFF : 0x00);
            chr[2 * i + 16] = (byte)(((colour >> 2) & 1) != 0 ? 0xFF : 0x00);
            chr[2 * i + 17] = (byte)(((colour >> 3) & 1) != 0 ? 0xFF : 0x00);
        }
        return chr;
    }

    var ptDone = new HashSet<int>();
    foreach (var (lo, hi, colour) in ptSpecs)
    {
        byte[] solid = SolidChar(colour);
        int painted = 0;
        foreach (int index in DkcTool.Core.GfxTable.EnumerateImageIndices(rom))
        {
            if (index < lo || index > hi) continue;
            int addr = DkcTool.Core.GfxTable.ResolveSpriteAddress(rom, index);
            if (!ptDone.Add(addr)) continue;   // aliased sprites, and earlier --paint ranges, win

            byte[] head = rom.ReadBytes(addr, 8);
            int charStart = addr + 8 + 2 * (head[0] + head[1] + head[3]);
            int charLen = (head[5] << 5) + head[7] * 0x20;
            if (charLen <= 0) continue;

            var fill = new byte[charLen];
            for (int off = 0; off < charLen; off += 0x20)
                Array.Copy(solid, 0, fill, off, Math.Min(0x20, charLen - off));
            rom.WriteBytes(charStart, fill);
            painted++;
        }
        Console.WriteLine($"  0x{lo:X}..0x{hi:X} -> colour {colour,2}: {painted} sprite(s)");
    }

    rom.Save(ptOut);
    Console.WriteLine($"Wrote {ptOut}");
    return 0;
}

if (args.Length >= 3 && args[1] == "--coords")
{
    // dotnet run -- <rom> --coords <lo>..<hi>
    //
    // Prints a slot's two bounding boxes side by side: the tile-placement one the ROM's
    // bytes are written in, and the opaque-pixel one a sheet is measured in. They are
    // different quantities (SpriteSlot's doc comment), and mistaking one for the other is
    // what made strip alignment place a run systematically low (spec A.17/A.19). When a
    // placement calculation looks right and lands wrong, this is the first thing to check.
    string[] cdB = args[2].Split("..");
    if (cdB.Length != 2) { Console.Error.WriteLine("--coords needs <loHex>..<hiHex>"); return 1; }
    int cdLo = Convert.ToInt32(cdB[0], 16), cdHi = Convert.ToInt32(cdB[1], 16);
    int seen = 0, differ = 0, maxSlackY = 0, maxSlackX = 0;

    Console.WriteLine("idx      placement (minX minY maxX maxY)   opaque (minX minY maxX maxY)   slack (dMinX dMinY dMaxY)");
    foreach (int index in DkcTool.Core.GfxTable.EnumerateImageIndices(rom))
    {
        if (index < cdLo || index > cdHi) continue;
        var slot = DkcTool.Core.SpriteSlot.Read(rom, index);
        seen++;

        int dMinX = slot.OpaqueMinX - slot.PlacementMinX;
        int dMinY = slot.OpaqueMinY - slot.PlacementMinY;
        int dMaxY = slot.OpaqueMaxY - slot.PlacementMaxY;
        if (dMinX != 0 || dMinY != 0 || dMaxY != 0) differ++;
        maxSlackY = Math.Max(maxSlackY, Math.Abs(dMinY));
        maxSlackX = Math.Max(maxSlackX, Math.Abs(dMinX));

        Console.WriteLine($"0x{index:X4}   {slot.PlacementMinX,4} {slot.PlacementMinY,4} {slot.PlacementMaxX,4} {slot.PlacementMaxY,4}" +
                          $"              {slot.OpaqueMinX,4} {slot.OpaqueMinY,4} {slot.OpaqueMaxX,4} {slot.OpaqueMaxY,4}" +
                          $"            {dMinX,5} {dMinY,5} {dMaxY,5}");
    }

    Console.WriteLine();
    Console.WriteLine($"{seen} sprite(s); {differ} where the two boxes disagree; " +
                      $"max |dMinY| = {maxSlackY} px, max |dMinX| = {maxSlackX} px");
    Console.WriteLine("Anything compared against a sheet must use the opaque box; the ROM's own bytes use placement.");
    return 0;
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
/// <summary>
/// Picks the ledger an import should append to, and the path it should be saved to.
///
/// Three cases, and the middle one is why this exists. A first import starts a fresh ledger. A
/// repeat run against the same output appends to that output's sidecar. A **chained** import --
/// writing to a new file from a ROM that was itself already imported into -- must carry the input's
/// ledger forward, keeping its original `sourceRomSha256`, so the chain stays revertible to the
/// original in one step and no intermediate sidecar has to be kept.
///
/// The chain case cannot use <see cref="ImportLedger.LoadOrCreate"/>'s sha256 equality guard: the
/// whole point is that the input is no longer the ROM the ledger started from, so that guard
/// rejects exactly the case it needs to allow. <see cref="ImportLedger.MatchesRom"/> replaces it --
/// the ledger is checked against what the ROM actually contains, which still rejects a stale or
/// foreign ledger.
/// </summary>
static bool TryResolveLedger(string romPath, string? outPath, Rom rom, string sha,
                             out ImportLedger ledger, out string ledgerPath)
{
    ledgerPath = ImportLedger.SidecarPath(outPath ?? romPath);
    string inputLedgerPath = ImportLedger.SidecarPath(romPath);
    string? source = File.Exists(ledgerPath) ? ledgerPath
                   : File.Exists(inputLedgerPath) ? inputLedgerPath
                   : null;

    if (source == null)
    {
        ledger = new ImportLedger { SourceRomSha256 = sha };
        return true;
    }

    ledger = ImportLedger.Load(source);

    // Built from this exact ROM: a fresh chain start, or a repeat run against the same output.
    if (string.Equals(ledger.SourceRomSha256, sha, StringComparison.OrdinalIgnoreCase))
        return true;

    // Otherwise this ROM must be the product of that ledger, or the ledger does not belong here.
    if (ledger.MatchesRom(rom, out string detail))
    {
        Console.WriteLine($"Chained import   : carrying {ledger.Allocations.Count} allocation(s) " +
                          $"forward from {source}");
        Console.WriteLine($"                   (ledger's original ROM sha256 {ledger.SourceRomSha256[..16]}... is preserved, " +
                          "so one --revert returns to it)");
        return true;
    }

    Console.Error.WriteLine($"[LedgerMismatch] ledger '{source}' does not describe this ROM: {detail}");
    return false;
}

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

    if (!TryResolveLedger(romPath, outPath, rom, sha, out ImportLedger ledger, out string ledgerPath))
        return 1;

    ImportResult result;
    try
    {
        // Expansion.FreeRunsFor (specs/m3-expansion-spec.md C.4): an expanded ROM allocates from
        // the extended half only, never mixed with the stock in-ROM padding pool.
        result = SpriteImporter.Import(working, imageIndex, pose.Pixels,
            new ImportOptions { DryRun = dryRun, Source = Path.GetFileName(posePath), FreeRuns = Expansion.FreeRunsFor(rom), AnchorBottom = Array.IndexOf(args, "--anchor-bottom") >= 0 }, ledger);
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
                       d.SeverityLabel);
    Console.WriteLine($"Current hitbox       : x={r.Hitbox.SignedX} y={r.Hitbox.SignedY} w={r.Hitbox.Width} h={r.Hitbox.Height} " +
                       $"(pointer 0x{r.Hitbox.PointerAddress:X} -> 0x{r.Hitbox.RecordAddress:X})");
}


// --batch (specs/m4-batch-spec.md C.3): sheet + manifest -> N imports against one pristine scan
// and one ledger. Never expands implicitly (Part B): a manifest that plainly cannot fit refuses
// up front, naming --expand, rather than grinding through hundreds of NoFreeSpace refusals.
static int RunBatchCli(Rom rom, string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: dotnet run -- <rom> --batch <manifest.json> --out <rom.sfc> [--dry-run] [--overlay out.png]");
        return 1;
    }

    string manifestPath = args[2];
    string? outPath = ArgValue(args, "--out");
    string? overlayPath = ArgValue(args, "--overlay");
    bool dryRun = Array.IndexOf(args, "--dry-run") >= 0;
    bool force = Array.IndexOf(args, "--force") >= 0;

    if (!dryRun)
    {
        if (outPath == null)
        {
            Console.Error.WriteLine("--out is required for any write (use --dry-run to preview without writing)");
            return 1;
        }
        // args[0] is the raw ROM path passed on the command line; rom is already loaded from it.
        if (Path.GetFullPath(outPath) == Path.GetFullPath(args[0]) && !force)
        {
            Console.Error.WriteLine("--out resolves to the input ROM path; pass --force to overwrite the input, or choose a different --out.");
            return 1;
        }
    }

    Manifest manifest;
    try { manifest = Manifest.Load(manifestPath); }
    catch (Exception ex) { Console.Error.WriteLine($"could not load manifest '{manifestPath}': {ex.Message}"); return 1; }

    string sheetPath = Path.IsPathRooted(manifest.Sheet)
        ? manifest.Sheet
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.Sheet);
    if (!File.Exists(sheetPath))
    {
        Console.Error.WriteLine($"manifest sheet '{manifest.Sheet}' not found at '{sheetPath}'.");
        return 1;
    }

    if (!PalettePointers.Table.TryGetValue(manifest.Palette, out int palAddr))
    {
        Console.Error.WriteLine($"Unknown palette '{manifest.Palette}' (from manifest).");
        return 1;
    }
    var palette = Palette.Read(rom, palAddr);

    var sheet = SheetSlicer.Slice(sheetPath);
    using var sheetBitmap = SKBitmap.Decode(sheetPath);
    Console.WriteLine($"Sheet                : {sheetPath} ({sheet.Strips.Count} strips, {sheet.Poses.Count()} poses)");

    List<PlannedPose> plan;
    try { plan = manifest.Resolve(rom, sheet, sheetBitmap); }
    catch (ManifestException ex) { Console.Error.WriteLine($"[{ex.Code}] {ex.Message}"); return 1; }
    Console.WriteLine($"Manifest             : {manifest.Strips.Count} strip(s) -> {plan.Count} planned pose(s)");

    // Expansion.FreeRunsFor (specs/m3-expansion-spec.md C.4), scanned once from the pristine ROM
    // and shared by every import below (ImportOptions.FreeRuns) -- the re-scan trap this class's
    // doc comment warns about.
    var freeRuns = Expansion.FreeRunsFor(rom);
    long pool = freeRuns.Sum(r => (long)r.Length);
    long needed = BatchImporter.EstimateBytes(rom, plan, sheetBitmap, palette);
    Console.WriteLine($"Capacity             : need ~{needed:N0} bytes, pool has {pool:N0} bytes " +
                      $"({(rom.Length > Expansion.StockSize ? "expanded" : "stock")})");
    if (needed > pool)
    {
        Console.Error.WriteLine(
            $"manifest needs ~{needed:N0} bytes but the pool only has {pool:N0} -- refusing rather " +
            "than grinding through per-pose NoFreeSpace refusals. Run --expand first.");
        return 1;
    }

    var working = rom.Clone();
    string sha = ImportLedger.ComputeSha256(rom.Snapshot());
    if (!TryResolveLedger(args[0], outPath ?? manifestPath, rom, sha,
                          out ImportLedger ledger, out string ledgerPath))
        return 1;

    var report = BatchImporter.Run(working, plan, sheetBitmap, palette, ledger, freeRuns, dryRun,
        sourceTag: Path.GetFileName(manifestPath),
        anchorBottom: Array.IndexOf(args, "--anchor-bottom") >= 0,
        // Strip alignment is the default (spec A.19). `--align-strip` is kept as a no-op so
        // existing invocations and the specs' recorded command lines still work.
        alignStrip: Array.IndexOf(args, "--no-align-strip") < 0,
        // Opt-in, and it must stay opt-in: it breaks the V4d identity by construction (A.20).
        flatX: Array.IndexOf(args, "--flat-x") >= 0);

    Console.WriteLine();
    Console.WriteLine($"Imported             : {report.Imported.Count()}/{plan.Count}, " +
                      $"{report.BytesWritten:N0} bytes, pool remaining ~{pool - report.BytesWritten:N0}");
    var byReason = report.Refused.GroupBy(o => o.RefusalCode).OrderByDescending(g => g.Count());
    foreach (var g in byReason)
        Console.WriteLine($"  refused [{g.Key}]: {g.Count()}");
    foreach (var o in report.Refused.Take(20))
        Console.WriteLine($"    strip {o.Planned.Strip}:{o.Planned.Position} -> 0x{o.Planned.ImageIndex:X}: {o.RefusalMessage}");

    foreach (var o in report.Imported)
    {
        var r = o.Result!;
        Console.WriteLine($"  strip {o.Planned.Strip}:{o.Planned.Position} -> 0x{r.ImageIndex:X}: " +
                          $"{r.CharCount}ch/{r.OamEntries}oam, 0x{r.Serialized.Length:X}B @ 0x{r.AllocatedOffset:X}" +
                          r.Drift.SeverityLabel);
    }

    if (overlayPath != null)
    {
        BatchImporter.DumpOverlay(sheetPath, report, overlayPath);
        Console.WriteLine();
        Console.WriteLine($"overlay -> {overlayPath}");
    }

    if (dryRun)
    {
        Console.WriteLine();
        Console.WriteLine("Dry run: no bytes written.");
        return report.Refused.Any() ? 1 : 0;
    }

    working.Save(outPath!);
    ledger.Save(ledgerPath);
    Console.WriteLine();
    Console.WriteLine($"Wrote {outPath}");
    Console.WriteLine($"Ledger {ledgerPath}");
    return report.Refused.Any() ? 1 : 0;
}

// V3 spike: boot a ROM in a libretro core, run N frames, dump the framebuffer.
//   dotnet run -- <rom> --emu-boot [--core <path>] [--frames N] [--out shot.png]
static int RunEmuBoot(string romPath, string[] args)
{
    string core = ArgValue(args, "--core") ?? "port/emu/cores/snes9x_libretro.dylib";
    int frames = int.Parse(ArgValue(args, "--frames") ?? "600");
    string outPath = ArgValue(args, "--out") ?? "emu-frame.png";

    byte[] romBytes = File.ReadAllBytes(romPath);
    Console.WriteLine($"Core         : {core}");
    Console.WriteLine($"ROM          : {romPath} ({romBytes.Length} bytes)");

    using var emu = new DkcTool.Core.Emulator.LibretroCore(core);
    emu.LoadGame(romBytes, romPath);

    Console.WriteLine($"Core ident   : {emu.LibraryName} {emu.LibraryVersion} (need_fullpath={emu.NeedsFullPath})");
    var (w, h, fps) = emu.GetAvInfo();
    Console.WriteLine($"AV info      : {w}x{h} @ {fps:F2} Hz, pixel format {emu.CorePixelFormat}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    string? script = ArgValue(args, "--script");
    if (script == "taps")
        DkcTool.Core.Emulator.BootScript.RunTo(emu, frames, pressStart: true,
            int.Parse(ArgValue(args, "--tap-window") ?? DkcTool.Core.Emulator.BootScript.TapWindow.ToString()));
    else
        emu.RunFrames(frames);

    int holdRight = int.Parse(ArgValue(args, "--hold-right") ?? "0");
    if (holdRight > 0) emu.HoldFor(holdRight, DkcTool.Core.Emulator.Joypad.Right);
    sw.Stop();

    Console.WriteLine($"Ran          : {frames} frames in {sw.ElapsedMilliseconds} ms " +
                      $"({frames * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} fps, " +
                      $"{frames / 60.0:F1}s of game time)");
    int bpp = emu.CorePixelFormat == DkcTool.Core.Emulator.LibretroCore.PixelFormat.Xrgb8888 ? 4 : 2;
    Console.WriteLine($"Framebuffer  : {emu.FrameWidth}x{emu.FrameHeight}, {emu.FramesRun} refresh callbacks");
    Console.WriteLine($"Pitch        : {emu.LastPitch} bytes/row; width*bpp = {emu.FrameWidth * bpp} " +
                      $"({(emu.LastPitch == emu.FrameWidth * bpp ? "match" : "MISMATCH -> core renders wider than it reports")})");

    int bppLog = emu.CorePixelFormat == DkcTool.Core.Emulator.LibretroCore.PixelFormat.Xrgb8888 ? 4 : 2;
    if (emu.CoreOptions.Count > 0)
        Console.WriteLine($"Core options : {emu.CoreOptions.Count} declared -> " +
            string.Join(", ", emu.CoreOptions.Take(6).Select(kv => $"{kv.Key}={kv.Value}")) +
            (emu.CoreOptions.Count > 6 ? ", ..." : ""));
    if (emu.UnhandledEnvironment.Count > 0)
        Console.WriteLine("Unhandled env: " + string.Join(", ",
            emu.UnhandledEnvironment.OrderByDescending(kv => kv.Value).Select(kv => $"cmd {kv.Key} x{kv.Value}")));
    Console.WriteLine("Geometry log :");
    foreach (var (gw, gh, gp) in emu.GeometryLog)
        Console.WriteLine($"   {gw}x{gh} pitch {gp}  -> pitch/width = {(double)gp / gw:F2} bytes/px " +
                          $"(announced {bppLog})");

    DkcTool.Core.Emulator.FrameCapture.Save(emu, outPath);
    Console.WriteLine($"Wrote        : {outPath}");
    return 0;
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// V3 spike: map the boot sequence -- run frames, press Start periodically, dump a
// filmstrip so a deterministic capture point can be picked for the gate.
//   dotnet run -- <rom> --emu-explore --outdir <dir> [--frames N] [--every N]
static int RunEmuExplore(string romPath, string[] args)
{
    string core = ArgValue(args, "--core") ?? "port/emu/cores/snes9x_libretro.dylib";
    int total = int.Parse(ArgValue(args, "--frames") ?? "5400");
    int every = int.Parse(ArgValue(args, "--every") ?? "300");
    string outDir = ArgValue(args, "--outdir") ?? "filmstrip";
    Directory.CreateDirectory(outDir);

    using var emu = new DkcTool.Core.Emulator.LibretroCore(core);
    emu.LoadGame(File.ReadAllBytes(romPath), romPath);

    bool noInput = Array.IndexOf(args, "--no-input") >= 0;
    for (int f = 0; f < total; f += every)
    {
        if (noInput || f >= DkcTool.Core.Emulator.BootScript.TapWindow)
        {
            emu.RunFrames(every);
        }
        else
        {
            // A Start tap every interval: enough to walk intro -> title -> file select.
            emu.RunFrames(every - 8);
            emu.HoldFor(4, DkcTool.Core.Emulator.Joypad.Start);
            emu.RunFrames(4);
        }

        string path = Path.Combine(outDir, $"f{f + every:D5}.png");
        DkcTool.Core.Emulator.FrameCapture.Save(emu, path);
        Console.WriteLine($"  frame {f + every,5} -> {path}");
    }
    return 0;
}

// V3 spike: boot two ROMs through the same deterministic script and diff the frame.
//   dotnet run -- <romA> --emu-diff <romB> [--frames N] [--outdir <dir>]
static int RunEmuDiff(string romA, string[] args)
{
    string core = ArgValue(args, "--core") ?? "port/emu/cores/snes9x_libretro.dylib";
    string romB = ArgValue(args, "--emu-diff") ?? throw new ArgumentException("--emu-diff needs a second ROM");
    int frames = int.Parse(ArgValue(args, "--frames") ?? DkcTool.Core.Emulator.BootScript.InGameFrame.ToString());
    string outDir = ArgValue(args, "--outdir") ?? ".";
    Directory.CreateDirectory(outDir);

    Console.WriteLine($"Core         : {core}");
    Console.WriteLine($"Capture frame: {frames}");

    bool tap = frames >= DkcTool.Core.Emulator.BootScript.FileSelectFrame;
    using var a = DkcTool.Core.Emulator.BootScript.CaptureAt(core, File.ReadAllBytes(romA), frames, tap);
    using var b = DkcTool.Core.Emulator.BootScript.CaptureAt(core, File.ReadAllBytes(romB), frames, tap);

    string pathA = Path.Combine(outDir, "emu-a.png"), pathB = Path.Combine(outDir, "emu-b.png");
    SaveBitmap(a, pathA); SaveBitmap(b, pathB);

    var diff = DkcTool.Core.Emulator.FrameCapture.Compare(a, b);
    Console.WriteLine($"A            : {romA} -> {pathA}");
    Console.WriteLine($"B            : {romB} -> {pathB}");
    Console.WriteLine($"Frame diff   : {diff}");
    return 0;
}

static void SaveBitmap(SKBitmap bmp, string path)
{
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(path);
    data.SaveTo(fs);
}

// V3 spike, positive control: import a deliberately recoloured pose into a range of
// image indices, so *something* on screen must visibly change. Without this, an
// "identical" frame diff is unfalsifiable -- it could just mean the index isn't drawn.
//   dotnet run -- <rom> --emu-vandal --out <rom.sfc> [--count N] [--colour 5]
static int RunEmuVandal(Rom rom, string[] args)
{
    int count = int.Parse(ArgValue(args, "--count") ?? "60");
    int colour = int.Parse(ArgValue(args, "--colour") ?? "5");
    string outPath = ArgValue(args, "--out") ?? throw new ArgumentException("--out required");

    var clone = rom.Clone();
    var ledger = new ImportLedger { SourceRomSha256 = "vandal" };
    var freeRuns = FreeSpace.Scan(rom);
    int done = 0, refused = 0;

    foreach (int index in GfxTable.EnumerateImageIndices(rom))
    {
        if (done >= count) break;
        int address = GfxTable.ResolveSpriteAddress(rom, index);
        var canvas = TilerHarness.DecodeIndexCanvas(rom, address);

        int H = canvas.GetLength(0), W = canvas.GetLength(1);
        int minX = W, minY = H, maxX = -1, maxY = -1;
        for (int r = 0; r < H; r++) for (int c = 0; c < W; c++)
            if (canvas[r, c] != 0) { if (c < minX) minX = c; if (c > maxX) maxX = c; if (r < minY) minY = r; if (r > maxY) maxY = r; }
        if (maxX < 0) continue;

        // Same silhouette, every colour flattened to one index: fits the same budgets and
        // geometry, but is unmistakable on screen.
        var pose = new int[maxY - minY + 1, maxX - minX + 1];
        for (int r = 0; r < pose.GetLength(0); r++)
            for (int c = 0; c < pose.GetLength(1); c++)
                pose[r, c] = canvas[minY + r, minX + c] == 0 ? 0 : colour;

        try
        {
            SpriteImporter.Import(clone, index, pose,
                new ImportOptions { Source = $"vandal-0x{index:X}", FreeRuns = freeRuns }, ledger);
            done++;
        }
        catch (ImportException) { refused++; }
    }

    clone.Save(outPath);
    Console.WriteLine($"Vandalised   : {done} indices flattened to palette index {colour}, {refused} refused");
    Console.WriteLine($"Wrote        : {outPath}");
    return 0;
}

// V3 gate (specs/v3-emulator-spec.md Part B): the two-sided emulator check.
//   dotnet run -- <rom> --verify-v3 [--core P] [--count N] [--frames N]
static int RunVerifyV3(Rom rom, string[] args)
{
    string core = ArgValue(args, "--core") ?? DkcTool.Core.Emulator.V3Verification.DefaultCore;
    int count = int.Parse(ArgValue(args, "--count")
        ?? DkcTool.Core.Emulator.V3Verification.DefaultIndexCount.ToString());
    int frame = int.Parse(ArgValue(args, "--frames")
        ?? DkcTool.Core.Emulator.BootScript.InGameFrame.ToString());

    int settle = int.Parse(ArgValue(args, "--settle")
        ?? DkcTool.Core.Emulator.StateCapture.SettleFrames.ToString());
    bool walk = Array.IndexOf(args, "--no-walk") < 0;
    var r = DkcTool.Core.Emulator.V3Verification.Run(rom, core, count, frame,
        DkcTool.Core.Emulator.V3Verification.DefaultStateName, settle, walk);

    Console.WriteLine($"Core            : {r.Core}");
    Console.WriteLine($"Capture point   : {r.CapturePoint} ({r.IndicesImported} indices re-imported per control ROM)");
    Console.WriteLine($"Baseline        : {r.BaselineSummary}");
    Console.WriteLine($"Gate1 relocated : {r.RelocatedDiff}   (expect: identical)");
    Console.WriteLine($"Gate2 vandal    : {r.VandalDiff}   (expect: localised difference)");
    foreach (var f in r.Failures) Console.WriteLine("  FAIL " + f);
    Console.WriteLine(r.Passed ? "V3 verification: PASS" : "V3 verification: FAIL");
    return r.Passed ? 0 : 1;
}

// M3 expansion probe (specs/m3-expansion-spec.md Part B): four experiments on whether an
// 8 MB ExHiROM DKC1 boots and whether its new space is addressable.
//   dotnet run -- <rom> --verify-m3 [--core P | --all-cores] [--name STATE] [--count N]
static int RunVerifyM3(Rom rom, string[] args)
{
    string stateName = ArgValue(args, "--name") ?? DkcTool.Core.Emulator.V3Verification.DefaultStateName;
    int count = int.Parse(ArgValue(args, "--count")
        ?? DkcTool.Core.Emulator.V3Verification.DefaultIndexCount.ToString());
    int settle = int.Parse(ArgValue(args, "--settle")
        ?? DkcTool.Core.Emulator.StateCapture.SettleFrames.ToString());
    bool walk = Array.IndexOf(args, "--no-walk") < 0;

    // --all-cores (specs/m3-expansion-spec.md C.5): the gate is only as good as the union of
    // cores it has actually run against -- one core passing says nothing about the other five.
    bool allCores = Array.IndexOf(args, "--all-cores") >= 0;
    string[] cores = allCores
        ? DkcTool.Core.Emulator.V3Verification.AllCores
        : new[] { ArgValue(args, "--core") ?? DkcTool.Core.Emulator.V3Verification.DefaultCore };

    // Headless preflight: expansion must be reversible, and nothing else in the gate would
    // notice if it stopped being. Runs before the cores because it costs milliseconds and
    // because a one-way expansion is a worse problem than a core disagreeing.
    var revRecord = Expansion.RecordFor(rom, Expansion.ExpandedSize, Expansion.MapModeExHiRom, true);
    byte[] revBuilt = Expansion.Build(rom, Expansion.ExpandedSize, Expansion.MapModeExHiRom,
                                      fixChecksum: true, mirrorLowBank: true);
    byte[] revBack = Expansion.Revert(revBuilt, revRecord);
    bool revOk = ImportLedger.ComputeSha256(revBack) == revRecord.OriginalSha256;
    Console.WriteLine($"Revert round-trip: {(revOk ? "PASS" : "FAIL")} " +
                      $"(expand 0x{Expansion.ExpandedSize:X} -> revert -> sha256 " +
                      $"{(revOk ? "matches" : "DIFFERS from")} the original)");

    // ...and both header copies must agree, which is what bsnes_libretro reads.
    bool hdrOk = Expansion.HasMirroredHeader(revBuilt)
                 && revBuilt[Expansion.MapModeOffset] == revBuilt[Expansion.LowBankMirrorBase + Expansion.MapModeOffset]
                 && revBuilt[Expansion.RomSizeOffset] == revBuilt[Expansion.LowBankMirrorBase + Expansion.RomSizeOffset]
                 && revBuilt[Expansion.ChecksumOffset] == revBuilt[Expansion.LowBankMirrorBase + Expansion.ChecksumOffset]
                 && revBuilt[Expansion.ChecksumOffset + 1] == revBuilt[Expansion.LowBankMirrorBase + Expansion.ChecksumOffset + 1];
    Console.WriteLine($"Mirrored header  : {(hdrOk ? "PASS" : "FAIL")} " +
                      "(0x40FFC0 matches 0x00FFC0 -- map mode, size byte, checksum)");
    Console.WriteLine();

    bool allPassed = revOk && hdrOk;
    foreach (string core in cores)
    {
        var r = DkcTool.Core.Emulator.ExpansionProbe.Run(rom, core, stateName, count, settle, walk);

        Console.WriteLine($"Core         : {r.Core}");
        Console.WriteLine($"Capture point: state {r.CapturePoint}");
        Console.WriteLine();
        foreach (var e in r.Experiments)
        {
            Console.WriteLine($"{e.Id}  {e.What}");
            Console.WriteLine($"    expect : {e.Expectation}");
            Console.WriteLine($"    got    : {e.Observed}");
            Console.WriteLine($"    {(e.Passed ? "PASS" : "FAIL")}   ({e.Note})");
            Console.WriteLine();
        }
        Console.WriteLine(r.Passed ? $"M3 probe ({core}): PASS" : $"M3 probe ({core}): FAIL");
        Console.WriteLine();
        allPassed &= r.Passed;
    }

    if (allCores)
        Console.WriteLine(allPassed ? "M3 probe, all six cores: PASS" : "M3 probe, all six cores: FAIL");

    // V2b cumulative against an expanded ROM (specs/m3-expansion-spec.md C.5): headless, no core
    // involved -- this is "does the M2b writer and its gates still hold once the free-space
    // source is the extended half instead of the 92 KB pool", at the scale that actually matters
    // (thousands of poses, not 99).
    var extCumulative = M2bVerification.RunCumulativeExtended(rom);
    Console.WriteLine();
    Console.WriteLine($"V2b cumulative (expanded ROM): {extCumulative.Imported} imported, " +
                       $"NoFreeSpace hit: {extCumulative.NoFreeSpaceHit}, {extCumulative.Failures.Count} gate failures, " +
                       $"utilisation {extCumulative.Utilisation:P0} " +
                       $"({extCumulative.BytesWritten}/{extCumulative.FreeBytesAtStart} bytes of extended space).");
    foreach (var f in extCumulative.Failures.Take(20)) Console.WriteLine("  FAIL " + f);
    bool extOk = extCumulative.Failures.Count == 0;
    Console.WriteLine(extOk ? "V2b cumulative (expanded ROM): PASS" : "V2b cumulative (expanded ROM): FAIL");

    return allPassed && extOk ? 0 : 1;
}

// M2c poison probe: overwrite candidate free space with noise and check the game does not
// notice. Tests the *whole* pool, unlike the V3 gate, which only exercises the runs 60
// relocations happened to land in.
//   dotnet run -- <rom> --poison [--class a|straddle|nonembedded|b|all] [--core P]
//                               [--name STATE] [--bisect] [--settle N] [--no-walk]
static int RunPoison(Rom rom, string[] args)
{
    string core = ArgValue(args, "--core") ?? DkcTool.Core.Emulator.V3Verification.DefaultCore;
    string stateName = ArgValue(args, "--name") ?? DkcTool.Core.Emulator.V3Verification.DefaultStateName;
    int settle = int.Parse(ArgValue(args, "--settle")
        ?? DkcTool.Core.Emulator.StateCapture.SettleFrames.ToString());
    bool walk = Array.IndexOf(args, "--no-walk") < 0;
    bool bisect = Array.IndexOf(args, "--bisect") >= 0;
    string which = (ArgValue(args, "--class") ?? "all").ToLowerInvariant();

    var inventory = FreeSpaceResearch.Inventory(rom);

    // The four classes the M2c research pass distinguishes. Each is a separate hypothesis:
    // lumping them into one probe would tell us only that *something* is read.
    var classes = new List<(string Label, List<FreeSpace.Run> Runs)>();
    List<FreeSpace.Run> Runs(IEnumerable<FreeSpaceResearch.Candidate> cs) =>
        cs.SelectMany(c => FreeSpaceResearch.SplitAtBanks(c.Run)).OrderBy(r => r.Start).ToList();

    var padding = inventory.Where(c => c.EndOfBankPadding).ToList();
    var straddle = inventory.Where(c => !c.EndOfBankPadding && c.StraddledBoundary > 0)
        .Select(c => new FreeSpace.Run { Start = c.Start, Length = c.PaddingLength, Value = c.Run.Value })
        .ToList();
    var nonEmbedded = inventory
        .Where(c => !c.EndOfBankPadding && c.StraddledBoundary <= 0 && c.Embedded < 0.9).ToList();
    var embedded = inventory
        .Where(c => !c.EndOfBankPadding && c.StraddledBoundary <= 0 && c.Embedded >= 0.9).ToList();

    // Positive control. Every class below can only ever report "the frame did not change", and
    // that sentence is worthless until something is shown to be *able* to change it: a probe that
    // silently fails to write, or a capture point that renders nothing, reports exactly the same
    // clean sweep. Poisoning known sprite data must break the picture -- if it does not, no other
    // result from this run means anything. (Same reasoning as V3's two-sided gate; the repo has
    // already been burned once by a one-sided "PASS".)
    if (which is "control" or "all")
    {
        var spriteRuns = FreeSpace.KnownSpriteRanges(rom)
            .Select(s => new FreeSpace.Run { Start = s.Offset, Length = s.Size, Value = 0 })
            .OrderBy(r => r.Start).ToList();
        classes.Add(("CONTROL: known sprite data (MUST show a difference)", spriteRuns));
    }

    if (which is "a" or "all") classes.Add(("A end-of-bank padding (allocated)", Runs(padding)));
    if (which is "straddle" or "all") classes.Add(("A' boundary-straddling padding (allocated since M2c)", straddle));
    if (which is "nonembedded" or "b" or "all")
        classes.Add(("B mid-bank, not tilemap-embedded (candidate, not allocated)", Runs(nonEmbedded)));
    // Kept in the sweep precisely because it passes: it is live level data on the structural
    // evidence (m2c spec B.1), so a clean result here is the calibration showing how little a
    // single capture point proves. Do not read it as a promotion.
    if (which is "embedded" or "b" or "all")
        classes.Add(("B mid-bank, tilemap-embedded (live data; passes anyway -- see m2c spec B.4)", Runs(embedded)));

    Console.WriteLine($"Core         : {core}");
    Console.WriteLine($"Capture point: state {stateName}, settle {settle}, walk {walk}");
    using var baseline = DkcTool.Core.Emulator.PoisonProbe.CaptureBaseline(core, rom, stateName, settle, walk);
    Console.WriteLine($"Baseline     : {baseline.Width}x{baseline.Height}, matches golden");
    Console.WriteLine();

    int failures = 0;
    bool controlProved = false;
    foreach (var (label, runs) in classes)
    {
        bool isControl = label.StartsWith("CONTROL");
        var outcome = DkcTool.Core.Emulator.PoisonProbe.Probe(core, rom, baseline, stateName, runs,
                                                              label, settle, walk);
        Console.WriteLine($"{label}");
        Console.WriteLine($"  {outcome.RunCount} run(s), {outcome.Bytes} bytes ({outcome.Bytes / 1024} KB)");

        if (isControl)
        {
            controlProved = !outcome.Identical;
            Console.WriteLine(controlProved
                ? $"  OK -- poison reaches the screen ({outcome.Diff})"
                : "  BROKEN -- poisoning live sprite data changed nothing. The probe is not " +
                  "testing what it claims; every 'unread' below is meaningless.");
            if (!controlProved) failures++;
            Console.WriteLine();
            continue;
        }

        Console.WriteLine(outcome.Identical
            ? "  UNREAD at this capture point (frame identical)"
            : $"  READ -- {outcome.Diff}");
        if (!outcome.Identical)
        {
            failures++;
            if (bisect)
            {
                Console.WriteLine("  bisecting:");
                var guilty = DkcTool.Core.Emulator.PoisonProbe.Bisect(core, rom, baseline, stateName,
                    runs, settle, walk, Console.WriteLine);
                Console.WriteLine($"  {guilty.Count} guilty run(s), " +
                                  $"{guilty.Sum(r => (long)r.Length)} bytes");
            }
        }
        Console.WriteLine();
    }

    if (which is "control" or "all" && !controlProved)
        Console.WriteLine("RESULT VOID: the positive control did not fire.");
    else
        Console.WriteLine("Reminder: 'unread' means unread on the paths this capture point exercises, " +
                          "not free. One state is one scene.");
    return failures == 0 ? 0 : 1;
}

// Captures the V3 reference frame for a core (specs/v3-emulator-spec.md Part B, gate 0).
// The image MUST be inspected by a human before it is trusted -- that inspection is the
// whole point: a garbled frame passes every automated "looks like a game" heuristic.
//   dotnet run -- <rom> --emu-golden [--core P] [--frames N]
static int RunEmuGolden(string romPath, string[] args)
{
    string core = ArgValue(args, "--core") ?? DkcTool.Core.Emulator.V3Verification.DefaultCore;
    int frame = int.Parse(ArgValue(args, "--frames")
        ?? DkcTool.Core.Emulator.BootScript.InGameFrame.ToString());

    // Match the gate: if a save state exists for this core, the golden must come from the
    // same capture point the gate will use, or it would be comparing different scenes.
    string stateName = ArgValue(args, "--name") ?? DkcTool.Core.Emulator.V3Verification.DefaultStateName;
    string statePath = DkcTool.Core.Emulator.StateCapture.PathFor(core, stateName);
    bool useState = File.Exists(statePath);

    string path = useState
        ? DkcTool.Core.Emulator.V3Verification.GoldenPath(core, stateName)
        : DkcTool.Core.Emulator.V3Verification.GoldenPath(core, frame);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    int gsettle = int.Parse(ArgValue(args, "--settle")
        ?? DkcTool.Core.Emulator.StateCapture.SettleFrames.ToString());
    bool gwalk = Array.IndexOf(args, "--no-walk") < 0; // match the gate default
    string gout = ArgValue(args, "--out") ?? "";
    using var bmp = useState
        ? DkcTool.Core.Emulator.StateCapture.CaptureFromState(core, File.ReadAllBytes(romPath),
                                                              File.ReadAllBytes(statePath), gsettle, gwalk)
        : DkcTool.Core.Emulator.BootScript.CaptureAt(core, File.ReadAllBytes(romPath), frame,
              pressStart: frame >= DkcTool.Core.Emulator.BootScript.FileSelectFrame);
    Console.WriteLine($"Capture point: {(useState ? "state " + statePath : "scripted frame " + frame)}");
    if (gout.Length > 0) path = gout;
    SaveBitmap(bmp, path);

    Console.WriteLine($"Wrote golden : {path} ({bmp.Width}x{bmp.Height})");
    Console.WriteLine("NOW LOOK AT IT. It must show the expected in-game scene. A garbled or black");
    Console.WriteLine("frame will be accepted as the reference and make every later run pass on garbage.");
    return 0;
}

// Creates a save-state capture point for a core (specs/v3-emulator-spec.md).
// Drives the tap script to --frames, serializes, and writes both the state and a
// preview PNG which MUST be inspected before the state is trusted.
//   dotnet run -- <rom> --emu-state --name jungle [--core P] [--frames N]
static int RunEmuState(string romPath, string[] args)
{
    string core = ArgValue(args, "--core") ?? DkcTool.Core.Emulator.V3Verification.DefaultCore;
    string name = ArgValue(args, "--name") ?? "default";
    int frames = int.Parse(ArgValue(args, "--frames")
        ?? DkcTool.Core.Emulator.BootScript.InGameFrame.ToString());

    string path = DkcTool.Core.Emulator.StateCapture.PathFor(core, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    int tapWindow = int.Parse(ArgValue(args, "--tap-window")
        ?? DkcTool.Core.Emulator.BootScript.TapWindow.ToString());
    int stHoldRight = int.Parse(ArgValue(args, "--hold-right") ?? "0");
    var (state, frame) = DkcTool.Core.Emulator.StateCapture.Create(core, File.ReadAllBytes(romPath),
                                                                   frames, tapWindow, stHoldRight);
    using (frame)
    {
        File.WriteAllBytes(path, state);
        SaveBitmap(frame, path + ".png");
        Console.WriteLine($"Wrote state  : {path} ({state.Length} bytes) at frame {frames}");
        Console.WriteLine($"Preview      : {path}.png ({frame.Width}x{frame.Height})");
    }
    Console.WriteLine("LOOK AT THE PREVIEW. If it isn't the scene you want, adjust --frames.");
    return 0;
}
