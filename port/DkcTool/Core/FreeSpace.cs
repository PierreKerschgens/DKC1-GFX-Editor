using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// Free-space scan + allocation (specs/m2b-writer-spec.md C.4). Candidates are
    /// constant-byte filler runs; only the end-of-bank-padding class is trusted for
    /// allocation (C.4's numbers are reproduced by <c>--stats-m2b</c>, which stays in
    /// the tree as the regression check for this scan).
    ///
    /// These runs are *candidates* only: constant bytes prove nothing about whether
    /// the game reads them. Nothing here is trusted for gameplay until the V3
    /// emulator harness (Part E).
    /// </summary>
    public static class FreeSpace
    {
        public const int MinRunLength = 0x100;

        /// <summary>End-of-bank alignment: a run whose end sits on this boundary cannot have its
        /// start (or any allocation carved from that start) cross a bank when allocated forward.</summary>
        public const int PaddingAlignment = 0x8000;

        public sealed class Run
        {
            public int Start;
            public int Length;
            public byte Value;
            public int End => Start + Length;
        }

        /// <summary>Every distinct sprite's (offset, size) reachable from the GFX pointer table.
        /// A fully-transparent char is 0x20 zero bytes, so a filler run *inside* a sprite reads as
        /// candidate free space -- this excludes those.</summary>
        public static List<(int Offset, int Size)> KnownSpriteRanges(Rom rom)
        {
            var sizeByAddress = new Dictionary<int, int>();
            foreach (int index in GfxTable.EnumerateImageIndices(rom))
            {
                int address = GfxTable.ResolveSpriteAddress(rom, index);
                if (sizeByAddress.ContainsKey(address)) continue;
                byte[] data;
                try { data = SpriteDecoder.ReadSpriteBytes(rom, address); }
                catch { continue; }
                sizeByAddress[address] = data.Length;
            }
            return sizeByAddress.Select(kv => (Rom.Mask(kv.Key), kv.Value)).ToList();
        }

        /// <summary>All constant-byte runs >= <see cref="MinRunLength"/>, before excluding
        /// sprite-internal runs or filtering to end-of-bank padding. Diagnostic use
        /// (<c>--stats-m2b</c>'s "raw" count) -- <see cref="Scan"/> is what allocation uses.</summary>
        public static List<Run> RawRuns(Rom rom)
        {
            var romSize = rom.Length;
            var runs = new List<Run>();

            int i = 0;
            while (i < romSize)
            {
                byte v = rom.Read8(i);
                if (v != 0x00 && v != 0xFF) { i++; continue; }
                int j = i;
                while (j < romSize && rom.Read8(j) == v) j++;
                if (j - i >= MinRunLength) runs.Add(new Run { Start = i, Length = j - i, Value = v });
                i = j;
            }
            return runs;
        }

        /// <summary><see cref="RawRuns"/> with anything overlapping known sprite data excluded.
        /// Diagnostic use (<c>--stats-m2b</c>'s "clean" count) -- <see cref="Scan"/> narrows this
        /// further to the end-of-bank-padding class actually used for allocation.</summary>
        public static List<Run> CleanRuns(Rom rom)
        {
            var sprites = KnownSpriteRanges(rom).OrderBy(s => s.Offset).ToList();
            bool OverlapsSprite(int start, int length) =>
                sprites.Any(s => s.Offset < start + length && start < s.Offset + s.Size);

            return RawRuns(rom).Where(r => !OverlapsSprite(r.Start, r.Length)).ToList();
        }

        /// <summary>Scans the whole ROM for candidate free space: constant filler runs
        /// >= <see cref="MinRunLength"/>, excluding anything overlapping known sprite data,
        /// keeping only end-of-bank padding (run end aligned to <see cref="PaddingAlignment"/>).
        /// Ascending by start. This is the list allocation draws from.</summary>
        public static List<Run> Scan(Rom rom) =>
            CleanRuns(rom).Where(r => r.End % PaddingAlignment == 0).OrderBy(r => r.Start).ToList();

        /// <summary>
        /// First-fit allocation of <paramref name="length"/> bytes over <paramref name="runs"/>
        /// (ascending by start), treating <paramref name="alreadyAllocated"/> ranges (from the
        /// import ledger) as consumed from the front of whichever run they fall in -- so repeat
        /// and batch runs never hand out the same bytes twice.
        /// </summary>
        public static int Allocate(List<Run> runs, int length, IEnumerable<(int Offset, int Length)> alreadyAllocated)
        {
            var allocated = alreadyAllocated.ToList();

            foreach (var run in runs)
            {
                int consumed = allocated
                    .Where(a => a.Offset >= run.Start && a.Offset + a.Length <= run.End)
                    .Sum(a => a.Length);

                int availableStart = run.Start + consumed;
                int availableLength = run.Length - consumed;
                if (availableLength < length) continue;

                int end = availableStart + length - 1;
                if ((availableStart >> 16) != (end >> 16))
                    throw new System.InvalidOperationException(
                        $"allocation at 0x{availableStart:X} length 0x{length:X} would cross a bank " +
                        "boundary -- this should be structurally impossible for an end-of-bank-padding run.");

                return availableStart;
            }

            throw new ImportException(ImportErrorCode.NoFreeSpace,
                $"no free run fits {length} (0x{length:X}) bytes -- needs M3 (expansion).");
        }
    }
}
