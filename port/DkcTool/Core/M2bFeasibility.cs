using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// M2b research pass (specs/m2b-writer-spec.md Part A). Answers the questions the
    /// in-place writer's design hinges on, measured over every sprite the game itself
    /// uses -- same evidence-first approach as <c>--stats</c> did for M2's budgets:
    ///
    ///  1. **Aliasing** -- do several GFX image indices resolve to the same sprite
    ///     address? If so, an in-place overwrite silently rewrites every frame that
    ///     shares that address.
    ///  2. **Trailing slack** -- is there dead space between the end of one sprite and
    ///     the start of the next, or is the region densely packed? Determines whether
    ///     "in place" can ever mean "slightly bigger than the original".
    ///  3. **Re-tile fit rate** -- re-tiling the game's own pose through SpriteTiler
    ///     and re-serializing, how often does the result fit the original slot? This is
    ///     the pessimistic proxy for "will a hand-drawn pose of similar complexity fit".
    ///  4. **Origin agreement** -- does the placement-table bbox origin (spec Q4) match
    ///     the decoded pixel bbox origin the M2a harness used?
    /// </summary>
    public static class M2bFeasibility
    {
        /// <summary>
        /// Reports candidate free space via <see cref="FreeSpace"/> -- the same scan the
        /// real allocator (C.4) uses, so this stays the regression check for it rather
        /// than a parallel implementation that could drift.
        /// </summary>
        private static void ReportFreeSpace(Rom rom)
        {
            var raw = FreeSpace.RawRuns(rom);
            var clean = FreeSpace.CleanRuns(rom);
            var padding = FreeSpace.Scan(rom); // already end-of-bank-padding only; what allocation uses

            long Total(List<FreeSpace.Run> l) => l.Sum(r => (long)r.Length);

            Console.WriteLine($"Filler runs >= 0x{FreeSpace.MinRunLength:X} : {raw.Count} raw, " +
                              $"{clean.Count} after excluding sprite-internal runs");
            Console.WriteLine($"  usable candidates      : {Total(clean)} bytes ({Total(clean) / 1024} KB)");
            Console.WriteLine($"  end-of-bank padding    : {padding.Count} runs, {Total(padding)} bytes " +
                              $"({Total(padding) / 1024} KB), largest 0x{(padding.Count > 0 ? padding.Max(r => r.Length) : 0):X}");
            foreach (var r in padding.OrderByDescending(r => r.Length).Take(8))
                Console.WriteLine($"  0x{r.Start:X6}..0x{r.End:X6}  " +
                                  $"0x{r.Length:X} bytes  filler 0x{r.Value:X2}");
        }

        private static int Mask(int address) =>
            address & (address > 0x7fffff ? 0x3fffff : 0xffffff);

        public sealed class SlotInfo
        {
            public int ImageIndex;
            public int Address;       // as stored in the pointer table (SNES bank address)
            public int FileOffset;    // masked
            public int Size;          // header + placements + char data
            public int PlacementMinX;
            public int PlacementMinY;
            public List<(int X, int Y)> Placements = new List<(int, int)>();

            /// <summary>True if every placement sits on an 8x8 lattice anchored at the bbox top-left.</summary>
            public bool LatticeConsistent =>
                Placements.All(p => (p.X - PlacementMinX) % 8 == 0 && (p.Y - PlacementMinY) % 8 == 0);
        }

        public static int Run(Rom rom)
        {
            var slots = new List<SlotInfo>();
            var byAddress = new Dictionary<int, List<int>>();

            foreach (int imageIndex in GfxTable.EnumerateImageIndices(rom))
            {
                int address = GfxTable.ResolveSpriteAddress(rom, imageIndex);
                byte[] data;
                try { data = SpriteDecoder.ReadSpriteBytes(rom, address); }
                catch { continue; }

                int b0 = data[0], b1 = data[1], b3 = data[3];
                int p = 8, minX = 999, minY = 999;
                var placements = new List<(int, int)>();
                for (int k = 0; k < b0 + b1 + b3 && p + 1 < data.Length; k++)
                {
                    int x = data[p++], y = data[p++];
                    placements.Add((x, y));
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                }

                slots.Add(new SlotInfo
                {
                    Placements = placements,
                    ImageIndex = imageIndex,
                    Address = address,
                    FileOffset = Mask(address),
                    Size = data.Length,
                    PlacementMinX = minX == 999 ? 0 : minX,
                    PlacementMinY = minY == 999 ? 0 : minY,
                });

                if (!byAddress.TryGetValue(address, out var list))
                    byAddress[address] = list = new List<int>();
                list.Add(imageIndex);
            }

            // --- 1. Aliasing ---
            int aliased = byAddress.Count(kv => kv.Value.Count > 1);
            int maxAlias = byAddress.Count == 0 ? 0 : byAddress.Max(kv => kv.Value.Count);
            var worstAlias = byAddress.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
            int indicesOnAliased = byAddress.Where(kv => kv.Value.Count > 1).Sum(kv => kv.Value.Count);

            Console.WriteLine($"Image indices            : {slots.Count}");
            Console.WriteLine($"Distinct sprite addresses: {byAddress.Count}");
            Console.WriteLine($"Aliased addresses        : {aliased} (shared by {indicesOnAliased} indices), " +
                              $"max {maxAlias} indices on one address" +
                              (maxAlias > 1 ? $" (0x{worstAlias.Key:X})" : ""));

            // Pointer encoding: which SNES banks do real sprite pointers use? A
            // relocation target's file offset has to be written back as an address in
            // the same convention (mask(addr) == fileOffset).
            var banks = slots.Select(s => s.Address >> 16).Distinct().OrderBy(b => b).ToList();
            bool allC0 = slots.All(s => s.Address == 0xC00000 + s.FileOffset);
            Console.WriteLine($"Pointer banks used       : 0x{banks.First():X2}..0x{banks.Last():X2} " +
                              $"({banks.Count} distinct); addr == 0xC00000 + fileOffset for all: {allC0}");

            // HiROM check: banks 0xC0-0xFF map ROM linearly; banks 0x80-0xBF expose only
            // the upper half ($8000-$FFFF) of each 64K block. If every low-bank pointer
            // has low word >= 0x8000, the table is plain HiROM and a relocation target
            // can always be expressed as 0xC00000 + fileOffset.
            var lowBank = slots.Where(s => s.Address < 0xC00000).ToList();
            int lowBankBad = lowBank.Count(s => (s.Address & 0xFFFF) < 0x8000);
            Console.WriteLine($"  low-bank (0x80-0xBF)   : {lowBank.Count} pointers, " +
                              $"{lowBankBad} with low word < 0x8000 (HiROM-inconsistent)");
            Console.WriteLine($"  high-bank (0xC0-0xFF)  : {slots.Count - lowBank.Count} pointers");

            // --- 2. Trailing slack between consecutive distinct sprites ---
            var distinct = byAddress.Keys
                .Select(a => new { Address = a, Offset = Mask(a), Size = slots.First(s => s.Address == a).Size })
                .OrderBy(s => s.Offset)
                .ToList();

            var slack = new List<int>();
            int overlapping = 0, zeroSlack = 0;
            for (int i = 0; i + 1 < distinct.Count; i++)
            {
                int gap = distinct[i + 1].Offset - (distinct[i].Offset + distinct[i].Size);
                if (gap < 0) { overlapping++; continue; }
                if (gap == 0) zeroSlack++;
                slack.Add(gap);
            }
            slack.Sort();
            int Pct(List<int> l, double q) => l.Count == 0 ? 0 : l[(int)Math.Round(q * (l.Count - 1))];

            Console.WriteLine($"Consecutive pairs        : {slack.Count} measured, {overlapping} overlapping/out-of-order");
            Console.WriteLine($"Trailing slack (bytes)   : zero for {zeroSlack} pairs, " +
                              $"p50 {Pct(slack, .5)}, p90 {Pct(slack, .9)}, p99 {Pct(slack, .99)}, " +
                              $"max {(slack.Count > 0 ? slack[^1] : 0)}");

            // --- 3. Re-tile fit rate + 4. origin agreement ---
            int fits = 0, tooBig = 0, empty = 0, failed = 0, originMismatch = 0;
            var deltas = new List<int>();
            var newSizes = new List<int>();
            int worstDelta = int.MinValue, worstIndex = 0;

            // Variant B: tile on the *original placement lattice* (origin = placement
            // bbox top-left) instead of the pixel bbox, so cell boundaries land where
            // the game's own chars did. Hypothesis: the pixel-bbox origin splits chars
            // across cells and is what inflates the re-tile.
            int fitsB = 0, tooBigB = 0, latticeConsistent = 0;
            var deltasB = new List<int>();

            foreach (var slot in slots)
            {
                int[,] canvas;
                try { canvas = TilerHarness.DecodeIndexCanvas(rom, slot.Address); }
                catch { failed++; continue; }

                int height = canvas.GetLength(0), width = canvas.GetLength(1);
                int minX = width, minY = height, maxX = -1, maxY = -1;
                for (int r = 0; r < height; r++)
                {
                    for (int c = 0; c < width; c++)
                    {
                        if (canvas[r, c] == 0) continue;
                        if (c < minX) minX = c;
                        if (c > maxX) maxX = c;
                        if (r < minY) minY = r;
                        if (r > maxY) maxY = r;
                    }
                }
                if (maxX < 0) { empty++; continue; }

                if (minX != slot.PlacementMinX || minY != slot.PlacementMinY) originMismatch++;

                int w = maxX - minX + 1, h = maxY - minY + 1;
                var pose = new int[h, w];
                for (int r = 0; r < h; r++)
                    for (int c = 0; c < w; c++)
                        pose[r, c] = canvas[minY + r, minX + c];

                var built = SpriteTiler.Build(pose, minX, minY);
                int newSize = SpriteEncoder.Serialize(built.Model).Length;
                int delta = newSize - slot.Size;
                newSizes.Add(newSize);
                deltas.Add(delta);
                if (delta > worstDelta) { worstDelta = delta; worstIndex = slot.ImageIndex; }
                if (delta <= 0) fits++; else tooBig++;

                // Variant B: same pose, but the cell grid is anchored at the original
                // placement lattice, so cells land on the game's own char boundaries.
                if (slot.LatticeConsistent) latticeConsistent++;
                int ox = slot.PlacementMinX, oy = slot.PlacementMinY;
                if (ox <= minX && oy <= minY && maxX >= ox && maxY >= oy)
                {
                    int wB = maxX - ox + 1, hB = maxY - oy + 1;
                    var poseB = new int[hB, wB];
                    for (int r = 0; r < hB; r++)
                        for (int c = 0; c < wB; c++)
                            poseB[r, c] = canvas[oy + r, ox + c];

                    var builtB = SpriteTiler.Build(poseB, ox, oy);
                    int deltaB = SpriteEncoder.Serialize(builtB.Model).Length - slot.Size;
                    deltasB.Add(deltaB);
                    if (deltaB <= 0) fitsB++; else tooBigB++;
                }
            }

            deltas.Sort();
            Console.WriteLine($"Re-tile vs original slot : {fits} fit, {tooBig} too big, " +
                              $"{empty} empty-skipped, {failed} decode-failed");
            Console.WriteLine($"  size delta (new - old) : p10 {Pct(deltas, .1)}, p50 {Pct(deltas, .5)}, " +
                              $"p90 {Pct(deltas, .9)}, max {worstDelta} (idx 0x{worstIndex:X})");
            newSizes.Sort();
            Console.WriteLine($"Re-tiled sprite size     : p50 0x{Pct(newSizes, .5):X}, p90 0x{Pct(newSizes, .9):X}, " +
                              $"max 0x{(newSizes.Count > 0 ? newSizes[^1] : 0):X}");
            Console.WriteLine($"Origin agreement         : {originMismatch} of {deltas.Count} " +
                              $"placement-bbox vs pixel-bbox mismatches");

            deltasB.Sort();
            Console.WriteLine($"Placement-lattice tiling : {fitsB} fit, {tooBigB} too big " +
                              $"(of {deltasB.Count} measured)");
            Console.WriteLine($"  size delta (new - old) : p10 {Pct(deltasB, .1)}, p50 {Pct(deltasB, .5)}, " +
                              $"p90 {Pct(deltasB, .9)}, max {(deltasB.Count > 0 ? deltasB[^1] : 0)}");
            Console.WriteLine($"  lattice-consistent     : {latticeConsistent} of {slots.Count} sprites " +
                              $"(all placements on an 8x8 grid from bbox top-left)");

            ReportFreeSpace(rom);
            return 0;
        }
    }
}
