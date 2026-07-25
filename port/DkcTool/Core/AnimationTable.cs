using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// Headless port of <c>Animation.ParseAnimation</c> (root project's Animation.cs), reduced to
    /// the one question M4 needs answered: **which GFX image indices does each animation use?**
    ///
    /// M4's manifest has to map sheet strips to image indices, and the sheet cannot supply the
    /// index side -- its captions are rendered pixels (m4-batch-spec A.4). The animation scripts
    /// can: each frame entry carries the index it draws. Walking them turns "author 533 indices by
    /// hand" into "name ~30 strips with an animation id".
    ///
    /// No bitmaps are decoded. The original walks the same bytecode to build frame images; this
    /// only collects indices, so it needs neither the palette nor the sprite decoder.
    /// </summary>
    public static class AnimationTable
    {
        /// <summary>Pointer table of 16-bit, bank-relative script addresses (root: Animation.cs).</summary>
        public const int PointerTable = 0xbe8572;
        public const int Bank = 0xbe0000;

        /// <summary>
        /// 440 entries, derived rather than assumed: the scripts begin immediately after the
        /// table, so the lowest pointer marks its end. Reading entries until the first implausible
        /// one (&lt; the table's own start) gives 440, and 0x8572 + 440*2 = 0x88E2 is *exactly* the
        /// minimum pointer over those 440. The two independent readings agreeing is the check --
        /// a wrong count would leave the implied end and the minimum pointer disagreeing.
        /// </summary>
        public const int Count = 440;

        /// <summary>Operand counts for commands 0x80-0x91, indexed by <c>command &amp; 0x7f</c>
        /// (verbatim from Animation.animationParams).</summary>
        private static readonly int[] Params = { 0, 3, 2, 2, 3, 5, 9, 7, 4, 5, 9, 7, 4, 4, 1, 1, 1, 0 };

        /// <summary>Runaway guard: a corrupt walk must terminate with an error, not spin.</summary>
        private const int MaxSteps = 4096;

        public sealed class Script
        {
            public int Animation;
            public int Address;
            /// <summary>Image indices in draw order, with repeats -- a frame shown twice is two
            /// entries, which is what makes the count comparable to a sheet strip's pose count.</summary>
            public List<int> ImageIndices = new List<int>();
            public string? Error;

            public int FrameCount => ImageIndices.Count;
            public IEnumerable<int> Distinct => ImageIndices.Distinct();
            public bool Ok => Error == null;
        }

        public static Script Parse(Rom rom, int animation)
        {
            int pointer = rom.Read16(PointerTable + animation * 2);
            var script = new Script { Animation = animation, Address = Bank | pointer };

            for (int step = 0; step < MaxSteps; step++)
            {
                int address = Bank | (pointer & 0xFFFF);
                int command = rom.Read8(address);

                if (command > 0x91)
                {
                    script.Error = $"command 0x{command:X2} out of range at 0x{address:X}";
                    return script;
                }

                if (command < 0x80)
                {
                    // Timed frame entry: time, then a 16-bit GFX table index.
                    script.ImageIndices.Add(rom.Read8(address + 1) | (rom.Read8(address + 2) << 8));
                    pointer += 3;
                    continue;
                }

                int count = Params[command & 0x7f];
                pointer += count + 1;

                switch (command)
                {
                    case 0x80:
                    case 0x91:
                        return script;

                    // Draw commands carrying one index at arr[2..3].
                    case 0x87:
                    case 0x89:
                    case 0x8a:
                    case 0x8b:
                        script.ImageIndices.Add(rom.Read8(address + 2) | (rom.Read8(address + 3) << 8));
                        break;

                    // Mount commands: the frame at arr[2..3] *and* the mounted sprite at arr[4..5]
                    // (a Kong riding an animal buddy draws both).
                    case 0x85:
                    case 0x86:
                        script.ImageIndices.Add(rom.Read8(address + 2) | (rom.Read8(address + 3) << 8));
                        script.ImageIndices.Add(rom.Read8(address + 4) | (rom.Read8(address + 5) << 8));
                        break;
                }
            }

            script.Error = $"did not terminate within {MaxSteps} steps";
            return script;
        }

        public static List<Script> ParseAll(Rom rom) =>
            Enumerable.Range(0, Count).Select(i => Parse(rom, i)).ToList();

        private static int Pct(IEnumerable<int> xs, double p)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(s.Count * p))];
        }

        public static int Run(Rom rom)
        {
            var scripts = ParseAll(rom);
            var ok = scripts.Where(s => s.Ok).ToList();
            var bad = scripts.Where(s => !s.Ok).ToList();

            Console.WriteLine("=== M4: animation table (index side of the manifest) ===");
            Console.WriteLine($"Animations       : {scripts.Count} ({ok.Count} parsed, {bad.Count} failed)");
            foreach (var b in bad.Take(5))
                Console.WriteLine($"  FAIL anim {b.Animation}: {b.Error}");

            var valid = GfxTable.EnumerateImageIndices(rom).ToHashSet();
            var referenced = ok.SelectMany(s => s.ImageIndices).ToHashSet();
            var inTable = referenced.Where(valid.Contains).ToHashSet();

            Console.WriteLine($"Frame entries    : {ok.Sum(s => s.FrameCount):N0}");
            Console.WriteLine($"Distinct indices : {referenced.Count:N0} referenced, " +
                              $"{inTable.Count:N0} of them in the GFX table ({valid.Count:N0} indices exist)");
            Console.WriteLine($"GFX coverage     : {100.0 * inTable.Count / valid.Count:F1} % of the table is " +
                              "reachable from an animation");
            Console.WriteLine();

            var counts = ok.Where(s => s.FrameCount > 0).Select(s => s.FrameCount).ToList();
            Console.WriteLine($"Frames per animation: p50 {Pct(counts, .5)}, p90 {Pct(counts, .9)}, " +
                              $"max {counts.Max()} ({ok.Count(s => s.FrameCount == 0)} empty)");
            Console.WriteLine();

            // The manifest question: is "strip of N poses" enough to identify an animation?
            Console.WriteLine("Ambiguity of matching a strip to an animation by frame count alone:");
            var byCount = ok.Where(s => s.FrameCount > 0).GroupBy(s => s.FrameCount)
                            .OrderBy(g => g.Key).ToList();
            foreach (int probe in new[] { 8, 12, 15, 18, 24, 30, 34 })
            {
                int matches = byCount.FirstOrDefault(g => g.Key == probe)?.Count() ?? 0;
                Console.WriteLine($"  a {probe,2}-pose strip matches {matches,3} animation(s)");
            }
            Console.WriteLine($"  unique frame counts: {byCount.Count(g => g.Count() == 1)} of {byCount.Count}");
            Console.WriteLine();

            // Do an animation's indices sit together in the table? If they do, a character's
            // animations can be found by index range rather than by inspecting all 440.
            var contiguous = ok.Where(s => s.Distinct.Count() > 1).ToList();
            int tight = contiguous.Count(s =>
            {
                var d = s.Distinct.OrderBy(x => x).ToList();
                return d[^1] - d[0] <= 4 * d.Count * 2;   // within 2x a packed run of stride-4 indices
            });
            Console.WriteLine($"Index locality   : {tight}/{contiguous.Count} multi-frame animations draw " +
                              "from a tight index range (within 2x a packed stride-4 run)");

            return bad.Count == 0 ? 0 : 1;
        }
    }
}
