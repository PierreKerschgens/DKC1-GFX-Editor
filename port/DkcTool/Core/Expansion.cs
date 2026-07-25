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

        public static byte[] Build(Rom baseRom, int targetSize, byte? mapMode,
                                   bool fixChecksum = true, bool mirrorLowBank = false)
        {
            if (targetSize < baseRom.Length)
                throw new ArgumentException($"target size 0x{targetSize:X} is smaller than the ROM");

            var expanded = new byte[targetSize];
            Array.Copy(baseRom.Snapshot(), expanded, baseRom.Length);

            // Put bank $00's upper half where ExHiROM will look for it: vectors, and the boot code
            // the reset vector points at. 32 KB out of the 4 MB gained.
            if (mirrorLowBank)
                Array.Copy(expanded, LowBankMirrorSource, expanded, LowBankMirrorOffset, LowBankMirrorLength);

            if (mapMode is byte mm) expanded[MapModeOffset] = mm;
            expanded[RomSizeOffset] = SizeByteFor(targetSize);

            if (fixChecksum)
            {
                // Zero the fields first so the sum does not include a stale pair, then write the
                // pair the sum implies. (With the fields zeroed the sum is short by 0x1FE, which
                // is exactly what a consistent complement/checksum pair contributes.)
                expanded[ComplementOffset] = expanded[ComplementOffset + 1] = 0;
                expanded[ChecksumOffset] = expanded[ChecksumOffset + 1] = 0;

                int checksum = (ComputeChecksum(expanded) + 0x1FE) & 0xFFFF;
                int complement = checksum ^ 0xFFFF;

                expanded[ChecksumOffset] = (byte)(checksum & 0xFF);
                expanded[ChecksumOffset + 1] = (byte)(checksum >> 8);
                expanded[ComplementOffset] = (byte)(complement & 0xFF);
                expanded[ComplementOffset + 1] = (byte)(complement >> 8);
            }

            return expanded;
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
