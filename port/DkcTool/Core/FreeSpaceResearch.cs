using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// M2c research pass -- the evidence behind widening the free-space class beyond
    /// end-of-bank padding (m2b-writer-spec.md C.4 keeps only runs ending on a 0x8000
    /// boundary: 27 runs / 77 KB out of 127 clean runs / 151 KB).
    ///
    /// That filter was the right call when nothing could be verified in-game, but it is a
    /// *proxy* for two different questions that it conflates:
    ///
    ///  1. **Can an allocation here cross a bank boundary?** (SNES DMA must not.) An
    ///     end-aligned run answers this structurally. So does splitting any run at its
    ///     0x10000 boundaries, which costs nothing and works for every run.
    ///  2. **Does the game read these bytes?** End-of-bank padding is *likely* assembler
    ///     slack, but "likely" is all it is, and the filter gives mid-bank runs no way to
    ///     ever earn trust -- including runs that are equally dead.
    ///
    /// This pass measures question 2's static half: for each candidate run, does anything
    /// in the ROM look like a pointer into it, how far is it from real sprite data, and
    /// what sits on either side. Static evidence can only ever *demote* a run (a hit means
    /// "something may reference this"); promotion needs the dynamic half, which is the V3
    /// poison test.
    /// </summary>
    public static class FreeSpaceResearch
    {
        /// <summary>Bank granularity an allocation must not cross (SNES DMA A-bus).</summary>
        public const int BankSize = 0x10000;

        public sealed class Candidate
        {
            public FreeSpace.Run Run = null!;
            public bool EndOfBankPadding;      // the class M2b already trusts
            public int Pointer24Hits;          // 24-bit LE values (bank >= 0xC0) landing inside
            public double Pointer24Expected;   // ...and how many pure chance predicts (see ChanceRatio)
            public int Word16Hits;             // 16-bit LE values in the same bank landing inside (noisy)
            public double TilemapBefore;       // share of neighbouring words that look like BG tilemap entries
            public double TilemapAfter;

            /// <summary>Observed 24-bit hits over chance. In a 4 MB ROM an unaligned byte-window
            /// sweep hits any run by accident constantly, so the raw count means nothing on its
            /// own -- only the ratio does, and only when it is far from 1.</summary>
            public double ChanceRatio => Pointer24Expected <= 0 ? 0 : Pointer24Hits / Pointer24Expected;

            /// <summary>
            /// Tilemap-likeness of the *weaker* neighbourhood. A run genuinely embedded in a BG
            /// tilemap matches on both sides; end-of-bank padding often scores high on one side
            /// only, because the byte after it is the start of an unrelated blob that happens to
            /// be a tilemap. Taking the min is what separates those two.
            ///
            /// This matters because an embedded run is *blank sky*, not dead space: the game reads
            /// it every frame that map is on screen. It is the risk the end-of-bank filter was
            /// implicitly buying protection from.
            /// </summary>
            public double Embedded => Math.Min(TilemapBefore, TilemapAfter);

            /// <summary>
            /// The 0x8000 boundary this run straddles, or -1. The M2b filter tests
            /// <c>End % 0x8000 == 0</c>, which misses padding whose following blob simply *starts*
            /// with a filler byte -- the measured run then ends at boundary+1 and is thrown away.
            /// The bytes up to the boundary are the same assembler slack either way.
            /// </summary>
            public int StraddledBoundary
            {
                get
                {
                    int b = (Start / FreeSpace.PaddingAlignment + 1) * FreeSpace.PaddingAlignment;
                    return b < End ? b : -1;
                }
            }

            /// <summary>Usable length under the boundary-straddle reading: everything up to the
            /// boundary. Equals <see cref="Length"/> for a run that already ends on one.</summary>
            public int PaddingLength =>
                End % FreeSpace.PaddingAlignment == 0 ? Length
                : StraddledBoundary > 0 ? StraddledBoundary - Start
                : 0;
            public int GapBefore;              // bytes from the previous sprite's end
            public int GapAfter;               // bytes to the next sprite's start
            public string Before = "";         // 8 bytes preceding the run
            public string After = "";          // 8 bytes following the run

            public int Start => Run.Start;
            public int End => Run.End;
            public int Length => Run.Length;

            /// <summary>Largest contiguous chunk once the run is split at 0x10000 boundaries --
            /// the real cap on a single allocation, whatever class the run is in.</summary>
            public int LargestBankChunk => SplitAtBanks(Run).Max(c => c.Length);
        }

        /// <summary>Splits a run into per-bank pieces, so no piece can straddle a 0x10000
        /// boundary. An end-of-bank-padding run is already a single piece by construction.</summary>
        public static List<FreeSpace.Run> SplitAtBanks(FreeSpace.Run run)
        {
            var pieces = new List<FreeSpace.Run>();
            int pos = run.Start;
            while (pos < run.End)
            {
                int bankEnd = Math.Min(run.End, (pos / BankSize + 1) * BankSize);
                pieces.Add(new FreeSpace.Run { Start = pos, Length = bankEnd - pos, Value = run.Value });
                pos = bankEnd;
            }
            return pieces;
        }

        public static List<Candidate> Inventory(Rom rom)
        {
            var clean = FreeSpace.CleanRuns(rom).OrderBy(r => r.Start).ToList();
            var sprites = FreeSpace.KnownSpriteRanges(rom).OrderBy(s => s.Offset).ToList();

            // Run-id per byte, so the pointer sweep is one linear pass rather than 127 scans.
            var runIdAt = new int[rom.Length];
            for (int i = 0; i < runIdAt.Length; i++) runIdAt[i] = -1;
            for (int i = 0; i < clean.Count; i++)
                for (int p = clean[i].Start; p < clean[i].End; p++)
                    runIdAt[p] = i;

            var pointerHits = new int[clean.Count];
            var wordHits = new int[clean.Count];
            long qualifyingWindows = 0;   // null model for ChanceRatio

            for (int p = 0; p + 2 < rom.Length; p++)
            {
                // A position inside a candidate run is constant filler; the "pointer" it forms
                // (0x000000 / 0xFFFFFF) is an artefact of the run, not a reference to one.
                if (runIdAt[p] >= 0) continue;

                int v24 = rom.Read8(p) | (rom.Read8(p + 1) << 8) | (rom.Read8(p + 2) << 16);
                if ((v24 >> 16) >= 0xC0)
                {
                    qualifyingWindows++;
                    int target = Rom.Mask(v24);
                    if (target < runIdAt.Length && runIdAt[target] >= 0) pointerHits[runIdAt[target]]++;
                }

                // 16-bit intra-bank reference: low word of an address in this same bank. Very
                // noisy (any small-valued data pair scores), reported only as a weak signal.
                int v16 = rom.Read8(p) | (rom.Read8(p + 1) << 8);
                int sameBankTarget = (p & ~(BankSize - 1)) | v16;
                if (sameBankTarget < runIdAt.Length && runIdAt[sameBankTarget] >= 0)
                    wordHits[runIdAt[sameBankTarget]]++;
            }

            string Hex(int at, int count)
            {
                if (at < 0 || at + count > rom.Length) return "--";
                return string.Join(" ", Enumerable.Range(0, count).Select(k => rom.Read8(at + k).ToString("X2")));
            }

            // A SNES BG tilemap entry is a 16-bit word: low byte = tile index low, high byte =
            // vhopppcc. DKC's level maps use palette 0 and the low tile bits, so their high bytes
            // land in {0x00,0x01,0x40,0x41,...} -- i.e. (b & 0x3C) == 0. Measuring how many
            // neighbouring words look like that separates "padding between blobs" from
            // "blank sky inside a tilemap", which is the distinction that actually matters.
            double TilemapLikeness(int at, int count, int parity)
            {
                if (at < 0 || at + count * 2 + 1 >= rom.Length) return 0;
                int look = 0, like = 0;
                for (int k = 0; k < count; k++)
                {
                    int w = at + parity + k * 2;
                    if (w + 1 >= rom.Length) break;
                    look++;
                    if ((rom.Read8(w + 1) & 0x3C) == 0) like++;
                }
                return look == 0 ? 0 : (double)like / look;
            }

            double expectedPerByte = (double)qualifyingWindows / 0x400000;

            var result = new List<Candidate>();
            for (int i = 0; i < clean.Count; i++)
            {
                var run = clean[i];
                var before = sprites.Where(s => s.Offset + s.Size <= run.Start)
                                    .Select(s => run.Start - (s.Offset + s.Size))
                                    .DefaultIfEmpty(int.MaxValue).Min();
                var after = sprites.Where(s => s.Offset >= run.End)
                                   .Select(s => s.Offset - run.End)
                                   .DefaultIfEmpty(int.MaxValue).Min();

                result.Add(new Candidate
                {
                    Run = run,
                    EndOfBankPadding = run.End % FreeSpace.PaddingAlignment == 0,
                    Pointer24Hits = pointerHits[i],
                    Pointer24Expected = expectedPerByte * run.Length,
                    Word16Hits = wordHits[i],
                    TilemapBefore = TilemapLikeness(run.Start - 0x80, 0x40, run.Start & 1),
                    TilemapAfter = TilemapLikeness(run.End, 0x40, run.End & 1),
                    GapBefore = before,
                    GapAfter = after,
                    Before = Hex(run.Start - 8, 8),
                    After = Hex(run.End, 8),
                });
            }
            return result;
        }

        /// <summary>A typical re-tiled sprite is 0x2F4 (p50) / 0x4AE (p90) bytes -- runs too small
        /// to hold one are pool padding, not capacity.</summary>
        public const int TypicalSprite = 0x4AE;

        private static double Median(IEnumerable<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[s.Count / 2];
        }

        public static int Run(Rom rom)
        {
            var all = Inventory(rom);
            var padding = all.Where(c => c.EndOfBankPadding).ToList();
            var midBank = all.Where(c => !c.EndOfBankPadding).ToList();

            long Bytes(IEnumerable<Candidate> cs) => cs.Sum(c => (long)c.Length);

            Console.WriteLine("=== M2c free-space inventory ===");
            Console.WriteLine($"Clean candidate runs      : {all.Count}, {Bytes(all)} bytes ({Bytes(all) / 1024} KB)");
            Console.WriteLine($"  end-of-bank padding (A) : {padding.Count} runs, {Bytes(padding)} bytes " +
                              $"({Bytes(padding) / 1024} KB)   <- runs ending exactly on a boundary");
            Console.WriteLine($"  mid-bank (B)            : {midBank.Count} runs, {Bytes(midBank)} bytes " +
                              $"({Bytes(midBank) / 1024} KB)   <- of which the straddlers below are now allocated too");
            Console.WriteLine($"  allocator pool today    : {FreeSpace.Scan(rom).Count} runs, " +
                              $"{FreeSpace.Scan(rom).Sum(r => (long)r.Length)} bytes " +
                              $"(A + boundary-straddling, see specs/m2c-free-space-spec.md C)");
            Console.WriteLine();

            // Negative result, kept deliberately: an unaligned 24-bit sweep of a 4 MB ROM cannot
            // answer "is this region referenced". Every run in BOTH classes scores hundreds of
            // hits, including the class M2b already trusts, because ~25% of all byte-windows read
            // as a bank >= 0xC0 pointer and they land somewhere. Only the ratio to chance carries
            // information, and only at the extremes.
            Console.WriteLine("Static reference sweep, normalised to chance (raw counts are meaningless):");
            foreach (var (label, set) in new[] { ("A padding", padding), ("B mid-bank", midBank) })
                Console.WriteLine($"  {label,-12}: median ratio {Median(set.Select(c => c.ChanceRatio)):F2}x, " +
                                  $"{set.Count(c => c.ChanceRatio > 4)} runs above 4x chance, " +
                                  $"{set.Count(c => c.Pointer24Hits == 0)} with zero raw hits");
            Console.WriteLine();

            // The signal that actually separates the two classes.
            Console.WriteLine("Neighbourhood shape -- how much the bytes around a run look like BG tilemap words:");
            foreach (var (label, set) in new[] { ("A padding", padding), ("B mid-bank", midBank) })
            {
                var embedded = set.Where(c => c.Embedded >= 0.9).ToList();
                Console.WriteLine($"  {label,-12}: median {Median(set.Select(c => c.Embedded)):P0}; " +
                                  $"{embedded.Count}/{set.Count} runs sit in tilemap-shaped data " +
                                  $"({Bytes(embedded)} bytes, {Bytes(embedded) / 1024} KB)");
            }
            Console.WriteLine();

            Console.WriteLine("Bank-split reach (largest single allocation a run can serve):");
            foreach (var (label, set) in new[] { ("A padding", padding), ("B mid-bank", midBank) })
            {
                var crossing = set.Where(c => c.LargestBankChunk != c.Length).ToList();
                Console.WriteLine($"  {label,-12}: {crossing.Count} runs cross a bank boundary; " +
                                  $"largest chunk overall 0x{set.Max(c => c.LargestBankChunk):X}");
            }
            Console.WriteLine();

            Console.WriteLine($"Runs that can hold a p90 sprite (0x{TypicalSprite:X} bytes) after bank split:");
            foreach (var (label, set) in new[] { ("A padding", padding), ("B mid-bank", midBank) })
            {
                var fit = set.Where(c => c.LargestBankChunk >= TypicalSprite).ToList();
                long capacity = fit.Sum(c => (long)SplitAtBanks(c.Run).Where(p => p.Length >= TypicalSprite).Sum(p => p.Length));
                Console.WriteLine($"  {label,-12}: {fit.Count} runs, {capacity} usable bytes " +
                                  $"(~{capacity / TypicalSprite} p90 sprites)");
            }
            Console.WriteLine();

            // The class the alignment test drops on an off-by-one.
            var straddlers = midBank.Where(c => c.StraddledBoundary > 0).ToList();
            Console.WriteLine("Runs straddling a 0x8000 boundary (the class the old `End % 0x8000` test rejected):");
            Console.WriteLine($"  {straddlers.Count} runs, {straddlers.Sum(c => (long)c.PaddingLength)} usable bytes " +
                              $"up to the boundary");
            foreach (var c in straddlers.OrderByDescending(c => c.PaddingLength))
                Console.WriteLine($"  0x{c.Start:X6}..0x{c.End:X6}  len 0x{c.Length:X5}  " +
                                  $"-> 0x{c.Start:X6}..0x{c.StraddledBoundary:X6} = 0x{c.PaddingLength:X} usable  " +
                                  $"(overshoot {c.End - c.StraddledBoundary} byte(s), embedded {c.Embedded:P0})");
            Console.WriteLine();

            Console.WriteLine("Mid-bank runs by size (top 20):");
            Console.WriteLine("  start..end            len     chance  tilemap  before / after");
            foreach (var c in midBank.OrderByDescending(c => c.Length).Take(20))
                Console.WriteLine($"  0x{c.Start:X6}..0x{c.End:X6}  0x{c.Length:X5}  {c.ChanceRatio,5:F1}x  " +
                                  $"{c.Embedded,6:P0}  [{c.Before}] [{c.After}]");
            Console.WriteLine();

            var promotable = midBank.Where(c => c.Embedded < 0.9 && c.LargestBankChunk >= TypicalSprite).ToList();
            Console.WriteLine($"Mid-bank runs NOT tilemap-embedded and big enough to hold a p90 sprite: " +
                              $"{promotable.Count} runs, {Bytes(promotable)} bytes ({Bytes(promotable) / 1024} KB)");
            foreach (var c in promotable.OrderByDescending(c => c.Length))
                Console.WriteLine($"  0x{c.Start:X6}..0x{c.End:X6}  0x{c.Length:X5}  " +
                                  $"tilemap {c.Embedded,4:P0}  chance {c.ChanceRatio,5:F1}x  " +
                                  $"[{c.Before}] [{c.After}]");

            return 0;
        }
    }
}
