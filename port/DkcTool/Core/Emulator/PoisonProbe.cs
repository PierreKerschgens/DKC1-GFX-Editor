using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// Direct test of the question every free-space heuristic only guesses at: **does the game
    /// read these bytes?**
    ///
    /// Overwrite a set of candidate runs with noise, restore a save state captured on the
    /// pristine ROM, let the game run, and compare frames. Filler that nothing reads produces an
    /// identical frame; filler the game reads produces garbage tiles, a wrong tilemap, or a crash.
    ///
    /// This is strictly stronger than what V3's gate1 does. Gate1 relocates 60 sprites and checks
    /// the frame is unchanged, which only exercises the handful of runs those allocations happened
    /// to land in -- most of the pool is never touched. Poisoning tests the *whole* class at once,
    /// including the 77 KB of end-of-bank padding M2b has been allocating from all along on
    /// structural argument alone.
    ///
    /// **What it cannot do** is prove a run is free. It proves a run is not read *on the paths the
    /// capture points exercise*. A run holding data for a level the probe never enters will pass
    /// while being entirely live. Coverage is the whole game here, and it is bounded by how many
    /// capture points exist -- which is why the result is reported per capture point rather than as
    /// a single verdict.
    /// </summary>
    public static class PoisonProbe
    {
        /// <summary>Deterministic noise, seeded per offset so a run poisoned alone and the same run
        /// poisoned as part of a batch get identical bytes -- otherwise bisection would be chasing
        /// a moving target. Never 0x00 or 0xFF: those are the filler values, and writing filler
        /// over filler tests nothing.</summary>
        public static byte Noise(int offset)
        {
            uint h = (uint)offset * 2654435761u;
            h ^= h >> 13;
            byte b = (byte)(h & 0xFF);
            return b == 0x00 || b == 0xFF ? (byte)0xA5 : b;
        }

        public static byte[] Poison(Rom baseRom, IEnumerable<FreeSpace.Run> runs)
        {
            var clone = baseRom.Clone();
            foreach (var run in runs)
                for (int p = run.Start; p < run.End; p++)
                    clone.Write8(p, Noise(p));
            return clone.Snapshot();
        }

        public sealed class Outcome
        {
            public string Label = "";
            public int RunCount;
            public long Bytes;
            public bool Identical;
            public string Diff = "";
            public List<FreeSpace.Run> Runs = new List<FreeSpace.Run>();
        }

        /// <summary>
        /// Captures the baseline frame for a core/capture-point pair, and fails loudly if it does
        /// not match the human-inspected golden -- a probe run against a core that is not rendering
        /// the expected scene compares garbage to garbage, exactly the failure V3's gate0 exists to
        /// prevent.
        /// </summary>
        public static SKBitmap CaptureBaseline(string corePath, Rom baseRom, string stateName,
                                               int settleFrames, bool walk)
        {
            string statePath = StateCapture.PathFor(corePath, stateName);
            if (!System.IO.File.Exists(statePath))
                throw new InvalidOperationException(
                    $"no save state at {statePath}. Capture one with `--emu-state --core {corePath} " +
                    $"--name {stateName}` and inspect the .png it writes before using it.");

            string goldenPath = V3Verification.GoldenPath(corePath, stateName);
            if (!System.IO.File.Exists(goldenPath))
                throw new InvalidOperationException(
                    $"no golden at {goldenPath} -- this core/capture point is unverified. " +
                    "Capture with `--emu-golden` and LOOK at the image first.");

            byte[] state = System.IO.File.ReadAllBytes(statePath);
            var frame = StateCapture.CaptureFromState(corePath, baseRom.Snapshot(), state, settleFrames, walk);

            using var golden = SKBitmap.Decode(goldenPath);
            var diff = FrameCapture.Compare(golden, frame);
            if (!diff.Identical)
            {
                frame.Dispose();
                throw new InvalidOperationException(
                    $"baseline does not match {goldenPath} ({diff}) -- the probe would be comparing " +
                    "against an unverified scene.");
            }
            return frame;
        }

        public static Outcome Probe(string corePath, Rom baseRom, SKBitmap baseline, string stateName,
                                    List<FreeSpace.Run> runs, string label,
                                    int settleFrames, bool walk)
        {
            byte[] state = System.IO.File.ReadAllBytes(StateCapture.PathFor(corePath, stateName));
            byte[] poisoned = Poison(baseRom, runs);
            using var frame = StateCapture.CaptureFromState(corePath, poisoned, state, settleFrames, walk);
            var diff = FrameCapture.Compare(baseline, frame);

            return new Outcome
            {
                Label = label,
                RunCount = runs.Count,
                Bytes = runs.Sum(r => (long)r.Length),
                Identical = diff.Identical,
                Diff = diff.ToString(),
                Runs = runs,
            };
        }

        /// <summary>
        /// Narrows a failing set to the individual runs responsible, by halving. A set that passes
        /// contributes nothing and is dropped; a single failing run is reported. Costs O(k log n)
        /// boots for k guilty runs, which beats n boots as soon as the pool is mostly clean.
        /// </summary>
        public static List<FreeSpace.Run> Bisect(string corePath, Rom baseRom, SKBitmap baseline,
                                                 string stateName, List<FreeSpace.Run> runs,
                                                 int settleFrames, bool walk, Action<string> log)
        {
            if (runs.Count == 0) return new List<FreeSpace.Run>();

            var outcome = Probe(corePath, baseRom, baseline, stateName, runs, $"{runs.Count} run(s)",
                                settleFrames, walk);
            if (outcome.Identical)
            {
                log($"    clean : {runs.Count} run(s), {outcome.Bytes} bytes");
                return new List<FreeSpace.Run>();
            }

            if (runs.Count == 1)
            {
                log($"    GUILTY: 0x{runs[0].Start:X6}..0x{runs[0].End:X6} " +
                    $"(0x{runs[0].Length:X} bytes) -- {outcome.Diff}");
                return runs;
            }

            log($"    dirty : {runs.Count} run(s) -- {outcome.Diff}; splitting");
            int mid = runs.Count / 2;
            var guilty = Bisect(corePath, baseRom, baseline, stateName, runs.Take(mid).ToList(),
                                settleFrames, walk, log);
            guilty.AddRange(Bisect(corePath, baseRom, baseline, stateName, runs.Skip(mid).ToList(),
                                   settleFrames, walk, log));
            return guilty;
        }
    }
}
