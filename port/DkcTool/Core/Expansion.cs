using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// M3 research pass (specs/m3-expansion-spec.md Part A) -- what expanding a 4 MB HiROM
    /// actually requires.
    ///
    /// The PRD's FR7 says "append banks and bump the size byte". That is wrong for this ROM:
    /// DKC1 is *already* at the 4 MB HiROM ceiling ($FFD5 = 0x31, $FFD7 = 0x0C, file 0x400000),
    /// so there are no banks left to append. Going further means **ExHiROM** (map mode $35),
    /// which changes address translation for part of the space -- and every milestone from M0 on
    /// inherits <see cref="Rom.Mask"/> and M2b's <c>pointer = 0xC00000 + fileOffset</c> rule.
    ///
    /// This class measures the header facts, the checksum rule, and exactly where the existing
    /// mask agrees and disagrees with ExHiROM translation. The mapping itself is a *hypothesis*
    /// until the core probe boots one -- nothing here asserts it is true.
    /// </summary>
    public static class Expansion
    {
        // SNES header fields (HiROM, at file offset 0xFFC0).
        public const int HeaderBase = 0xFFC0;
        public const int MapModeOffset = 0xFFD5;
        public const int RomSizeOffset = 0xFFD7;
        public const int SramSizeOffset = 0xFFD8;
        public const int ComplementOffset = 0xFFDC;
        public const int ChecksumOffset = 0xFFDE;

        public const byte MapModeHiRom = 0x31;    // HiROM + FastROM -- what DKC1 is
        public const byte MapModeExHiRom = 0x35;  // ExHiROM + FastROM -- what >4 MB needs

        /// <summary>
        /// Whether an expanded ROM mirrors $00:8000-FFFF into the extended half. <b>Required for
        /// the ROM to boot at all</b> -- without it the console resets into the zero-filled half
        /// and black-screens, which is precisely what `--verify-m3`'s X1 gate asserts. Only a test
        /// reproducing X1 should pass false.
        /// </summary>
        public const bool MirrorLowBankByDefault = true;

        /// <summary>Size byte is log2(size) - 10, i.e. 0x0C = 4 MB, 0x0D = 8 MB.</summary>
        public static byte SizeByteFor(int bytes) => (byte)(Math.Log2(bytes) - 10);
        public static int SizeFromByte(byte b) => 1 << (b + 10);

        /// <summary>
        /// The SNES checksum: the plain 16-bit sum of every ROM byte. Verified against this ROM --
        /// sum = 0x2BCC = the stored $FFDE, and $FFDC holds its complement 0xD433.
        ///
        /// The stored fields do not need excluding: complement + checksum contribute 0xFF + 0xFF
        /// to the sum whatever the checksum is, so the sum is independent of them. This holds for
        /// power-of-two sizes; 4 MB and 8 MB both are, which is why M3 has no split-sum case.
        /// </summary>
        public static int ComputeChecksum(byte[] rom)
        {
            int sum = 0;
            foreach (byte b in rom) sum += b;
            return sum & 0xFFFF;
        }

        /// <summary>Where an SNES address lands in the file under the *current* HiROM map -- which
        /// is what <see cref="Rom.Mask"/> implements, and what every milestone so far assumes.</summary>
        public static int HiRomOffset(int snesAddress) => Rom.Mask(snesAddress);

        /// <summary>
        /// Where an SNES address lands under ExHiROM, as documented for map mode $35. **Hypothesis
        /// under test** -- the whole point of the core probe is to check a real core agrees:
        ///
        ///   $C0-$FF:0000-FFFF -> file 0x000000-0x3FFFFF   (first 4 MB; unchanged from HiROM)
        ///   $80-$BF:8000-FFFF -> file 0x000000-0x3FFFFF   (mirror of the first 4 MB)
        ///   $40-$7D:0000-FFFF -> file 0x400000-0x7DFFFF   (the extended half -- new space)
        ///   $00-$3F:8000-FFFF -> file 0x400000+           (mirror of the extended half)
        ///
        /// The counter-intuitive part is that the *high* banks keep the *first* 4 MB: an ExHiROM
        /// ROM's original data does not move. That is what makes M2b's existing pointers survive.
        /// Returns -1 for addresses that map to nothing.
        /// </summary>
        public static int ExHiRomOffset(int snesAddress)
        {
            int bank = (snesAddress >> 16) & 0xFF;
            int offset = snesAddress & 0xFFFF;

            if (bank >= 0xC0) return (bank - 0xC0) * 0x10000 + offset;
            if (bank >= 0x80) return offset < 0x8000 ? -1 : (bank - 0x80) * 0x10000 + offset;
            if (bank >= 0x7E) return -1;                                   // WRAM, not ROM
            if (bank >= 0x40) return 0x400000 + (bank - 0x40) * 0x10000 + offset;
            return offset < 0x8000 ? -1 : 0x400000 + bank * 0x10000 + offset;
        }

        public sealed class MapDisagreement
        {
            public int BankLow, BankHigh;
            public string Region = "";
            public string Consequence = "";
        }

        /// <summary>
        /// Every bank where <see cref="Rom.Mask"/> and <see cref="ExHiRomOffset"/> disagree. This
        /// is the real M3 blast radius: wherever they agree, M0-M2b's addressing survives the
        /// map-mode switch untouched.
        /// </summary>
        public static List<MapDisagreement> MapDisagreements()
        {
            var rows = new List<MapDisagreement>();
            MapDisagreement? current = null;

            for (int bank = 0; bank <= 0xFF; bank++)
            {
                // Probe one address per bank half; translation is linear within a half.
                bool disagrees = false;
                foreach (int offset in new[] { 0x0000, 0x8000 })
                {
                    int addr = (bank << 16) | offset;
                    int ex = ExHiRomOffset(addr);
                    if (ex < 0) continue;              // not mapped: no claim to disagree with
                    if (Rom.Mask(addr) != ex) disagrees = true;
                }

                if (disagrees && current != null && current.BankHigh == bank - 1)
                {
                    current.BankHigh = bank;
                }
                else if (disagrees)
                {
                    current = new MapDisagreement { BankLow = bank, BankHigh = bank };
                    rows.Add(current);
                }
            }

            foreach (var row in rows)
            {
                row.Region = $"${row.BankLow:X2}-${row.BankHigh:X2}";
                row.Consequence = row.BankLow < 0x40
                    ? "Mask() returns a first-4MB offset; ExHiROM maps these to the extended half"
                    : "Mask() and ExHiROM differ";
            }
            return rows;
        }

        /// <summary>
        /// Builds an expanded ROM image: original bytes, zero-filled to
        /// <paramref name="targetSize"/>, header patched, checksum recomputed.
        /// <paramref name="mapMode"/> null leaves $FFD5 alone (used by the control experiment that
        /// tests size-only expansion).
        /// </summary>
        /// <summary>
        /// File offset that ExHiROM's <c>$00:8000-FFFF</c> window lands on. Under HiROM that
        /// window is file 0x008000-0x00FFFF; under ExHiROM it is here, 4 MB further up.
        ///
        /// This is what kills a naive expansion. The 65816 fetches its reset and IRQ vectors from
        /// **bank $00 unconditionally**, so after the map-mode switch the console reads them from
        /// 0x40FFE0-0x40FFFF -- zero-filled in a freshly appended half -- and resets into nothing.
        /// The first instruction then runs at $00:8000 = 0x408000, also zeros. The screen is black
        /// from power-on and no amount of checksum or size-byte care changes it.
        /// </summary>
        public const int LowBankMirrorOffset = 0x408000;
        public const int LowBankMirrorSource = 0x008000;
        public const int LowBankMirrorLength = 0x008000;

        /// <summary>Base of the mirrored bank, so mirrored header fields are addressed as
        /// <c>LowBankMirrorBase + ChecksumOffset</c> etc. -- 0x40FFC0 for the header itself.</summary>
        public const int LowBankMirrorBase = 0x400000;

        /// <summary>
        /// First allocatable offset in the extended half (specs/m3-expansion-spec.md Part C.3).
        /// **Not** 0x400000: bank $40's upper half is where ExHiROM puts $00:8000-FFFF, so it
        /// holds the mirrored vectors and boot code (<see cref="LowBankMirrorOffset"/>).
        /// Allocating from 0x400000 overwrote them and the console came up in a different video
        /// mode -- alive, but not running DKC (ExpansionProbe B.3). The whole bank is reserved
        /// rather than just its upper half, so nothing has to reason about a 32 KB hole.
        /// </summary>
        public const int ExtendedStart = 0x410000;

        /// <summary>
        /// Last file offset reachable through the ExHiROM $40-$7D window. Banks $7E/$7F are WRAM,
        /// so the final 128 KB of an 8 MB image is reachable only through the $00-$3F mirror --
        /// which is also the one range where <see cref="Rom.Mask"/> and ExHiROM disagree (A.1).
        /// The allocator stays out of it entirely.
        /// </summary>
        public const int ExtendedLimit = 0x7E0000;

        /// <summary>
        /// Free runs covering the extended half, split per bank so no allocation can cross a bank
        /// boundary (the SNES DMA constraint <see cref="FreeSpace.Allocate"/> asserts). This is
        /// the real allocator source for an expanded ROM: 0x3D0000 (~3.8 MB) of *known*-empty
        /// space, verified addressable by ExpansionProbe X3 -- no scanning needed, unlike
        /// <see cref="FreeSpace.Scan"/>'s constant-byte heuristic over the stock 92 KB pool.
        /// </summary>
        public static List<FreeSpace.Run> ExtendedRuns()
        {
            var runs = new List<FreeSpace.Run>();
            for (int start = ExtendedStart; start < ExtendedLimit; start += 0x10000)
                runs.Add(new FreeSpace.Run { Start = start, Length = 0x10000, Value = 0 });
            return runs;
        }

        /// <summary>
        /// Free-space policy (specs/m3-expansion-spec.md Part C.4): with 3.8 MB of *known*-empty
        /// extended space, the M2c scan/promotion machinery over the stock 92 KB pool stops
        /// mattering for new imports. An expanded ROM gets <see cref="ExtendedRuns"/> only --
        /// never the in-ROM padding scan mixed in. Two reasons, not one: mixing would spend the
        /// scarce, precisely-measured 92 KB pool first for no capacity benefit (the extended half
        /// is ~40x it), and it would put a heuristic (constant-byte runs, never proven free by an
        /// emulator gate for arbitrary bytes) and a structural guarantee (bank $40-$7D is
        /// unwritten by construction) behind the same allocation call, so a failure in one looks
        /// like a failure in both.
        ///
        /// A stock (unexpanded) ROM is unaffected -- it has no extended half, so this is exactly
        /// <see cref="FreeSpace.Scan"/>, same as before M3.
        /// </summary>
        public static List<FreeSpace.Run> FreeRunsFor(Rom rom) =>
            rom.Length > StockSize ? ExtendedRuns() : FreeSpace.Scan(rom);

        /// <summary>Size and map mode of a stock, unexpanded DKC1 ROM -- the only base state
        /// expansion is defined for. Everything in Part A/B was measured against this shape;
        /// expanding an already-expanded ROM, or one at some other size, is untested territory
        /// with no evidence behind it either way.</summary>
        public const int StockSize = 0x400000;

        /// <summary>The size M3's probe and gates expand to -- 8 MB, the next power of two above
        /// the stock 4 MB (M3's checksum rule needs a power-of-two size; see the class doc).</summary>
        public const int ExpandedSize = 0x800000;

        public static void RequireStockBase(Rom baseRom)
        {
            if (baseRom.Length != StockSize)
                throw new InvalidOperationException(
                    $"base ROM is 0x{baseRom.Length:X} bytes, expected the stock 4 MB HiROM " +
                    $"(0x{StockSize:X}). Expansion is only verified starting from that shape -- " +
                    "see specs/m3-expansion-spec.md Part A.");

            byte mapMode = baseRom.Read8(MapModeOffset);
            if (mapMode != MapModeHiRom)
                throw new InvalidOperationException(
                    $"base ROM map mode is 0x{mapMode:X2}, expected 0x{MapModeHiRom:X2} (HiROM). " +
                    "Expansion is only verified starting from a stock HiROM image, not an " +
                    "already-expanded or otherwise-mapped one.");
        }

        /// <summary>
        /// What <c>--expand</c> records in the ledger (specs/m3-expansion-spec.md C.1): enough of
        /// the pre-expansion header to make expansion reversible without re-deriving anything --
        /// <see cref="Revert"/> is a straight truncate-and-restore from these fields, not a
        /// recomputation.
        /// </summary>
        public sealed class ExpansionRecord
        {
            public int OriginalSize;
            public string OriginalSha256 = "";
            public byte OriginalMapMode;
            public byte OriginalSizeByte;
            public int OriginalChecksum;
            public int OriginalComplement;
            public int TargetSize;
            public byte MapMode;
            public bool MirrorLowBank;
            public DateTimeOffset Timestamp;
        }

        /// <summary>Captures the pre-expansion header fields <see cref="Build"/> is about to
        /// overwrite, before it overwrites them.</summary>
        public static ExpansionRecord RecordFor(Rom baseRom, int targetSize, byte mapMode, bool mirrorLowBank)
        {
            RequireStockBase(baseRom);
            return new ExpansionRecord
            {
                OriginalSize = baseRom.Length,
                OriginalSha256 = ImportLedger.ComputeSha256(baseRom.Snapshot()),
                OriginalMapMode = baseRom.Read8(MapModeOffset),
                OriginalSizeByte = baseRom.Read8(RomSizeOffset),
                OriginalChecksum = baseRom.Read16(ChecksumOffset),
                OriginalComplement = baseRom.Read16(ComplementOffset),
                TargetSize = targetSize,
                MapMode = mapMode,
                MirrorLowBank = mirrorLowBank,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }

        /// <summary>Undoes an expansion built from <paramref name="record"/>: truncates back to
        /// <see cref="ExpansionRecord.OriginalSize"/> and restores the header fields expansion
        /// overwrote. The low-bank mirror (if any) lives entirely past the truncation point and
        /// needs no separate undo. This is the "reversible" half of C.1 -- expansion is not a
        /// one-way door.</summary>
        public static byte[] Revert(byte[] expandedRom, ExpansionRecord record)
        {
            if (expandedRom.Length < record.OriginalSize)
                throw new InvalidOperationException(
                    $"image is 0x{expandedRom.Length:X} bytes, smaller than the recorded original " +
                    $"(0x{record.OriginalSize:X}) -- this ledger does not describe this file.");

            var original = new byte[record.OriginalSize];
            Array.Copy(expandedRom, original, record.OriginalSize);

            original[MapModeOffset] = record.OriginalMapMode;
            original[RomSizeOffset] = record.OriginalSizeByte;
            original[ChecksumOffset] = (byte)(record.OriginalChecksum & 0xFF);
            original[ChecksumOffset + 1] = (byte)(record.OriginalChecksum >> 8);
            original[ComplementOffset] = (byte)(record.OriginalComplement & 0xFF);
            original[ComplementOffset + 1] = (byte)(record.OriginalComplement >> 8);
            return original;
        }

        public static byte[] Build(Rom baseRom, int targetSize, byte? mapMode,
                                   bool fixChecksum = true, bool mirrorLowBank = false)
        {
            if (targetSize < baseRom.Length)
                throw new ArgumentException($"target size 0x{targetSize:X} is smaller than the ROM");

            // Only an actual size increase is "expansion" and needs the stock-base guard: a call
            // with targetSize == baseRom.Length is a header/checksum rebuild on an already-built
            // image (see BuildExtendedRom's second pass), and that is defined at any size.
            if (targetSize > baseRom.Length)
                RequireStockBase(baseRom);

            var expanded = new byte[targetSize];
            Array.Copy(baseRom.Snapshot(), expanded, baseRom.Length);

            // Patch the header BEFORE mirroring. Order is load-bearing: the mirror copies
            // $00:8000-FFFF, and the header sits at the top of that window, so mirroring first
            // leaves a *stale* header at 0x40FFC0 -- still reading map mode 0x31 / size 0x0C /
            // the pre-expansion checksum.
            //
            // That is not cosmetic. 0x40FFC0 is where ExHiROM-aware cores look for the header,
            // and bsnes_libretro believed it: it refused to map the extended half and showed a
            // black screen from power-on, while running the stock ROM on the same core fine.
            // Patching this copy is what makes it boot; the other five cores are unaffected
            // either way (they take ExHiROM off the file size).
            if (mapMode is byte mm) expanded[MapModeOffset] = mm;
            expanded[RomSizeOffset] = SizeByteFor(targetSize);

            // Put bank $00's upper half where ExHiROM will look for it: vectors, the boot code the
            // reset vector points at, and -- now -- the corrected header. 32 KB of the 4 MB gained.
            if (mirrorLowBank)
                Array.Copy(expanded, LowBankMirrorSource, expanded, LowBankMirrorOffset, LowBankMirrorLength);

            if (fixChecksum) WriteChecksum(expanded, mirroredHeader: mirrorLowBank);

            return expanded;
        }

        /// <summary>
        /// Writes the checksum/complement pair, to <em>both</em> header copies when the image
        /// carries a mirrored ExHiROM header at 0x40FFC0.
        ///
        /// Zero the fields first so a stale pair is not counted, then add back what a consistent
        /// pair contributes: complement + checksum always sum to 0xFF + 0xFF whatever the value,
        /// so the correction is 0x1FE <em>per pair present</em>. Getting that count wrong is a
        /// silent off-by-0x1FE in the stored checksum, which no emulator here validates -- and so
        /// would not be caught by any gate.
        /// </summary>
        public static void WriteChecksum(byte[] rom, bool mirroredHeader)
        {
            var bases = mirroredHeader ? new[] { 0, LowBankMirrorBase } : new[] { 0 };

            foreach (int b in bases)
            {
                rom[b + ComplementOffset] = rom[b + ComplementOffset + 1] = 0;
                rom[b + ChecksumOffset] = rom[b + ChecksumOffset + 1] = 0;
            }

            int checksum = (ComputeChecksum(rom) + bases.Length * 0x1FE) & 0xFFFF;
            int complement = checksum ^ 0xFFFF;

            foreach (int b in bases)
            {
                rom[b + ChecksumOffset] = (byte)(checksum & 0xFF);
                rom[b + ChecksumOffset + 1] = (byte)(checksum >> 8);
                rom[b + ComplementOffset] = (byte)(complement & 0xFF);
                rom[b + ComplementOffset + 1] = (byte)(complement >> 8);
            }
        }

        /// <summary>True if this image carries the mirrored ExHiROM header -- i.e. the 21-byte
        /// title at 0x40FFC0 matches the one at 0xFFC0.</summary>
        public static bool HasMirroredHeader(byte[] rom)
        {
            if (rom.Length <= LowBankMirrorBase + HeaderBase + 21) return false;
            for (int i = 0; i < 21; i++)
                if (rom[HeaderBase + i] != rom[LowBankMirrorBase + HeaderBase + i]) return false;
            return true;
        }

        public static int Run(Rom rom)
        {
            Console.WriteLine("=== M3 expansion research ===");
            Console.WriteLine();

            byte mapMode = rom.Read8(MapModeOffset);
            byte sizeByte = rom.Read8(RomSizeOffset);
            byte sramByte = rom.Read8(SramSizeOffset);
            int storedChecksum = rom.Read16(ChecksumOffset);
            int storedComplement = rom.Read16(ComplementOffset);
            int computed = ComputeChecksum(rom.Snapshot());

            Console.WriteLine("Header");
            Console.WriteLine($"  $FFD5 map mode   : 0x{mapMode:X2} " +
                              $"({(mapMode == MapModeHiRom ? "HiROM + FastROM" : "?")})");
            Console.WriteLine($"  $FFD7 rom size   : 0x{sizeByte:X2} -> {SizeFromByte(sizeByte) / 1024 / 1024} MB");
            Console.WriteLine($"  file size        : 0x{rom.Length:X} ({rom.Length / 1024 / 1024} MB)" +
                              $"{(SizeFromByte(sizeByte) == rom.Length ? "  (header agrees)" : "  MISMATCH")}");
            Console.WriteLine($"  $FFD8 sram size  : 0x{sramByte:X2} -> {(sramByte == 0 ? 0 : 1 << (sramByte + 10))} bytes");
            Console.WriteLine($"  $FFDE checksum   : 0x{storedChecksum:X4}, computed 0x{computed:X4}" +
                              $"{(storedChecksum == computed ? "  (match)" : "  MISMATCH")}");
            Console.WriteLine($"  $FFDC complement : 0x{storedComplement:X4}" +
                              $"{((storedComplement ^ storedChecksum) == 0xFFFF ? "  (consistent)" : "  INCONSISTENT")}");
            Console.WriteLine();
            Console.WriteLine("  => already at the 4 MB HiROM ceiling. There is nothing to append;");
            Console.WriteLine("     >4 MB means ExHiROM (map mode 0x35), not a bigger size byte.");
            Console.WriteLine();

            // Where the existing addressing survives the map change, and where it does not.
            Console.WriteLine("Rom.Mask() vs ExHiROM translation (hypothesis -- the core probe tests it)");
            var disagreements = MapDisagreements();
            if (disagreements.Count == 0)
            {
                Console.WriteLine("  no disagreement in any mapped bank");
            }
            else
            {
                foreach (var d in disagreements)
                    Console.WriteLine($"  {d.Region}: {d.Consequence}");
            }
            Console.WriteLine();
            Console.WriteLine("  $40-$7D (the new space) translates identically under Mask() and ExHiROM:");
            foreach (int probe in new[] { 0x400000, 0x408000, 0x500000, 0x7D0000 })
                Console.WriteLine($"    ${probe:X6}: Mask -> 0x{Rom.Mask(probe):X6}, " +
                                  $"ExHiROM -> 0x{ExHiRomOffset(probe):X6}" +
                                  $"{(Rom.Mask(probe) == ExHiRomOffset(probe) ? "  same" : "  DIFFER")}");
            Console.WriteLine();

            // Which existing pointers live in a bank whose meaning would change.
            Console.WriteLine("Existing GFX pointers by bank class");
            var byClass = new Dictionary<string, List<int>>
            {
                ["$C0-$FF (unchanged)"] = new List<int>(),
                ["$80-$BF (unchanged)"] = new List<int>(),
                ["$40-$7D (becomes extended space)"] = new List<int>(),
                ["$00-$3F (MEANING CHANGES)"] = new List<int>(),
            };
            foreach (int index in GfxTable.EnumerateImageIndices(rom))
            {
                int addr = GfxTable.ResolveSpriteAddress(rom, index);
                int bank = (addr >> 16) & 0xFF;
                string key = bank >= 0xC0 ? "$C0-$FF (unchanged)"
                    : bank >= 0x80 ? "$80-$BF (unchanged)"
                    : bank >= 0x40 ? "$40-$7D (becomes extended space)"
                    : "$00-$3F (MEANING CHANGES)";
                byClass[key].Add(addr);
            }
            foreach (var kv in byClass)
            {
                Console.Write($"  {kv.Key,-34}: {kv.Value.Count} pointer(s)");
                if (kv.Value.Count > 0 && kv.Value.Count <= 10)
                    Console.Write("  " + string.Join(", ", kv.Value.Distinct().Select(a => $"0x{a:X6}")));
                Console.WriteLine();
            }
            Console.WriteLine();

            // What expansion would buy, in the unit that matters.
            long pool = FreeSpace.Scan(rom).Sum(r => (long)r.Length);
            Console.WriteLine("Capacity");
            Console.WriteLine($"  current free-space pool : {pool} bytes ({pool / 1024} KB) -> 99 poses (V2b cumulative)");
            Console.WriteLine($"  8 MB ExHiROM would add  : {0x400000} bytes (4096 KB) of untouched space");
            Console.WriteLine($"  at the p90 sprite size (0x{FreeSpaceResearch.TypicalSprite:X}): " +
                              $"~{0x400000 / FreeSpaceResearch.TypicalSprite} more poses");
            Console.WriteLine();
            Console.WriteLine("  => expansion is not a capacity tweak; it is ~44x the current pool.");
            Console.WriteLine("     The question is never 'is it enough', only 'does it still run'.");

            return 0;
        }
    }
}
