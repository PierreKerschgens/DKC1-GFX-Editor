using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// M3 research probe (specs/m3-expansion-spec.md Part B): does an expanded, ExHiROM-mapped
    /// DKC1 still run, and is the new space actually addressable?
    ///
    /// Four experiments, each isolating one variable, because "the expanded ROM is broken" is
    /// useless as a finding -- it could be the size, the map mode, the checksum, or the pointers:
    ///
    ///   X1  8 MB, map mode left at HiROM ($31)     -- does size alone break the boot?
    ///   X2  8 MB, map mode ExHiROM ($35)           -- does the map-mode switch break the boot?
    ///   X3a X2 + sprites relocated into 0x400000+  -- is the new space readable? (expect identical)
    ///   X3b X2 + the same, recoloured              -- ...and is that really what we are seeing?
    ///
    /// X3 is deliberately two-sided for the same reason V3's gate is. "The frame is unchanged"
    /// after relocating sprites into extended space is equally consistent with "the new space
    /// works" and with "the pointers were ignored and the game drew the originals". Only the
    /// recoloured control tells those apart, and it is the difference between confirming ExHiROM
    /// and merely failing to notice it silently doing nothing.
    /// </summary>
    public static class ExpansionProbe
    {
        public const int ExpandedSize = 0x800000;   // 8 MB, the next power of two

        /// <summary>
        /// Last file offset reachable through the ExHiROM $40-$7D window. Banks $7E/$7F are WRAM,
        /// so the final 128 KB of an 8 MB image is reachable only through the $00-$3F mirror --
        /// which is also the one range where <see cref="Rom.Mask"/> and ExHiROM disagree. The
        /// probe stays out of it entirely.
        /// </summary>
        public const int ExtendedLimit = 0x7E0000;

        /// <summary>
        /// First allocatable offset in the extended half. **Not** 0x400000: bank $40's upper half
        /// is where ExHiROM puts $00:8000-FFFF, so it holds the mirrored vectors and boot code
        /// (<see cref="Expansion.LowBankMirrorOffset"/>). Allocating from 0x400000 overwrote them
        /// and the console came up in a different video mode -- alive, but not running DKC. The
        /// whole bank is reserved rather than just its upper half, so nothing has to reason about
        /// a 32 KB hole.
        /// </summary>
        public const int ExtendedStart = 0x410000;

        /// <summary>Free runs covering the extended half, split per bank so no allocation can
        /// cross a bank boundary (the SNES DMA constraint FreeSpace.Allocate asserts).</summary>
        public static List<FreeSpace.Run> ExtendedRuns()
        {
            var runs = new List<FreeSpace.Run>();
            for (int start = ExtendedStart; start < ExtendedLimit; start += 0x10000)
                runs.Add(new FreeSpace.Run { Start = start, Length = 0x10000, Value = 0 });
            return runs;
        }

        public sealed class Experiment
        {
            public string Id = "";
            public string What = "";
            public string Expectation = "";
            public string Observed = "";
            public bool Passed;
            public string Note = "";
        }

        public sealed class Result
        {
            public string Core = "";
            public string CapturePoint = "";
            public List<Experiment> Experiments = new List<Experiment>();
            public bool Passed => Experiments.All(e => e.Passed);
        }

        public static Result Run(Rom baseRom, string corePath, string stateName,
                                 int indexCount, int settleFrames, bool walk)
        {
            var result = new Result { Core = corePath, CapturePoint = stateName };

            using var baseline = PoisonProbe.CaptureBaseline(corePath, baseRom, stateName, settleFrames, walk);
            byte[] state = System.IO.File.ReadAllBytes(StateCapture.PathFor(corePath, stateName));

            // Expected outcomes. "Localised" exists because a crashed ROM differs from the
            // baseline too: an earlier version of this probe scored the recoloured control as a
            // PASS while the console was showing a black screen, since "differs" was all it
            // checked. Same trap V3's gate2 documents; the thresholds are borrowed from it.
            const int MinPixels = V3Verification.MinVandalPixels;
            const double MaxArea = V3Verification.MaxVandalBboxArea;

            (bool ok, string summary) Capture(byte[] romBytes, string expect)
            {
                try
                {
                    using var frame = StateCapture.CaptureFromState(corePath, romBytes, state, settleFrames, walk);
                    var diff = FrameCapture.Compare(baseline, frame);
                    string summary = diff.Identical ? "identical" : diff.ToString();

                    if (expect == "identical") return (diff.Identical, summary);
                    if (expect == "differs") return (!diff.Identical, summary);

                    // "localised": a real sprite change -- big enough to see, small enough to be
                    // a sprite rather than a dead console.
                    if (diff.Identical) return (false, summary + "  (nothing changed)");
                    double area = (double)(diff.MaxX - diff.MinX + 1) * (diff.MaxY - diff.MinY + 1)
                                  / (frame.Width * frame.Height);
                    if (diff.DifferingPixels < MinPixels) return (false, summary + "  (too small)");
                    if (area > MaxArea) return (false, summary + $"  (covers {area:P0} -- looks like a crash)");
                    return (true, summary);
                }
                catch (Exception ex)
                {
                    // A core refusing the ROM or the state is itself the finding, not a crash.
                    return (false, $"core error: {ex.Message}");
                }
            }

            // X1 -- the naive expansion the PRD's FR7 describes. Kept as an experiment rather than
            // a footnote because it is the trap: it fails identically whether the map mode byte
            // says $31 or $35 (snes9x picks ExHiROM off the 8 MB size and ignores the byte), and
            // the failure is total -- black screen from power-on, no vectors.
            byte[] x1 = Expansion.Build(baseRom, ExpandedSize, Expansion.MapModeExHiRom,
                                        mirrorLowBank: false);
            var (x1ok, x1sum) = Capture(x1, "differs");
            result.Experiments.Add(new Experiment
            {
                Id = "X1",
                What = "8 MB ExHiROM, size byte + checksum fixed, NO low-bank mirror",
                Expectation = "differs -- the console resets into the zero-filled half and dies",
                Observed = x1sum,
                Passed = x1ok,
                Note = "documents why 'append banks and bump the size byte' does not work",
            });

            // X2 -- the same, plus the 32 KB that makes bank $00 exist again.
            byte[] x2 = Expansion.Build(baseRom, ExpandedSize, Expansion.MapModeExHiRom,
                                        mirrorLowBank: true);
            var (x2ok, x2sum) = Capture(x2, "identical");
            result.Experiments.Add(new Experiment
            {
                Id = "X2",
                What = "8 MB ExHiROM + $00:8000-FFFF mirrored to 0x408000 (32 KB)",
                Expectation = "identical (ExHiROM keeps the first 4 MB at $C0-$FF, so nothing moves)",
                Observed = x2sum,
                Passed = x2ok,
                Note = "the claim that existing pointers survive the map change",
            });

            // X3 -- the decisive pair: put real sprites in the new half and repoint into $40-$7D.
            byte[] x3a = BuildExtendedRom(baseRom, indexCount, flattenTo: null, out int imported);
            var (x3aok, x3asum) = Capture(x3a, "identical");
            result.Experiments.Add(new Experiment
            {
                Id = "X3a",
                What = $"X2 + {imported} sprites relocated to 0x400000+, repointed into $40-$7D",
                Expectation = "identical (same pixels, read from extended space)",
                Observed = x3asum,
                Passed = x3aok,
                Note = "meaningless without X3b -- see below",
            });

            byte[] x3b = BuildExtendedRom(baseRom, indexCount, flattenTo: 5, out _);
            var (x3bok, x3bsum) = Capture(x3b, "localised");
            result.Experiments.Add(new Experiment
            {
                Id = "X3b",
                What = "X3a with every opaque pixel flattened to palette index 5",
                Expectation = "a localised difference (proves the extended data is what is drawn)",
                Observed = x3bsum,
                Passed = x3bok,
                Note = "if this is identical, X3a proves nothing: the pointers were ignored",
            });

            return result;
        }

        /// <summary>
        /// An expanded ExHiROM whose sprites live *only* in the new half. Mirrors V3's control-ROM
        /// construction, but allocates from <see cref="ExtendedRuns"/> instead of the free-space
        /// scan -- the point is to exercise addresses that do not exist on a 4 MB ROM at all.
        /// </summary>
        private static byte[] BuildExtendedRom(Rom baseRom, int indexCount, int? flattenTo, out int imported)
        {
            // Expand first: the importer refuses an extended-space pointer on a ROM too small to
            // contain it, which is exactly the guard we want to be relying on here.
            var expanded = new Rom(Expansion.Build(baseRom, ExpandedSize, Expansion.MapModeExHiRom,
                                                   mirrorLowBank: true));
            var ledger = new ImportLedger { SourceRomSha256 = flattenTo == null ? "m3-relocated" : "m3-vandal" };
            var runs = ExtendedRuns();
            imported = 0;

            foreach (int index in GfxTable.EnumerateImageIndices(baseRom))
            {
                if (imported >= indexCount) break;

                int address = GfxTable.ResolveSpriteAddress(baseRom, index);
                int[,]? pose;
                try { pose = M2bVerification.ExtractOwnPose(baseRom, address, out _, out _); }
                catch { continue; }
                if (pose == null) continue;

                if (flattenTo is int colour)
                {
                    for (int r = 0; r < pose.GetLength(0); r++)
                        for (int c = 0; c < pose.GetLength(1); c++)
                            if (pose[r, c] != 0) pose[r, c] = colour;
                }

                try
                {
                    SpriteImporter.Import(expanded, index, pose,
                        new ImportOptions { Source = $"m3-0x{index:X}", FreeRuns = runs }, ledger);
                    imported++;
                }
                catch (ImportException) { /* over budget: a valid skip */ }
            }

            // The checksum changed the moment sprites were written; recompute it, so a core that
            // validates it is testing ExHiROM rather than rejecting a stale header.
            var bytes = expanded.Snapshot();
            return Expansion.Build(new Rom(bytes), ExpandedSize, Expansion.MapModeExHiRom, mirrorLowBank: false);
        }
    }
}
