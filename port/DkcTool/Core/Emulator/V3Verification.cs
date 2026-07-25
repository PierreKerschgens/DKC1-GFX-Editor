using System;
using System.Collections.Generic;
using SkiaSharp;

namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// The V3 gate (specs/v3-emulator-spec.md Part B): boots real ROMs in a real SNES core and
    /// checks the two things every headless gate is structurally blind to -- whether the free
    /// space M2b writes into is actually unused, and whether an imported sprite reaches the
    /// screen at all.
    ///
    /// Deliberately two-sided. "The frame is identical" on its own is unfalsifiable: it is
    /// equally consistent with "the relocated sprite is fine" and with "this index is never
    /// drawn here, so nothing was tested". The vandal control is what gives the negative
    /// control meaning, so both must pass or the gate fails.
    /// </summary>
    public static class V3Verification
    {
        public const string DefaultCore = "port/emu/cores/snes9x_libretro.dylib";

        /// <summary>Indices re-imported when building the control ROMs.</summary>
        public const int DefaultIndexCount = 60;

        /// <summary>A vandalised frame must differ by at least this many pixels -- below it, the
        /// "difference" is more likely emulator noise than a sprite.</summary>
        public const int MinVandalPixels = 100;

        /// <summary>...and the difference must stay localised: a whole-screen change means
        /// something broke (crash, corrupted layer), not that one sprite was replaced.</summary>
        public const double MaxVandalBboxArea = 0.25;

        /// <summary>
        /// Where per-core reference frames live. **Gitignored** -- these are frames of a
        /// copyrighted game, held to the same rule as the ROM itself (.gitignore: "never commit").
        /// Capture with <c>--emu-golden</c> and *look at it* before trusting a core.
        /// </summary>
        public const string GoldenDirectory = "port/emu/goldens";

        public static string GoldenPath(string corePath, int frame) =>
            System.IO.Path.Combine(GoldenDirectory,
                $"{System.IO.Path.GetFileNameWithoutExtension(corePath)}-f{frame:D5}.png");

        public sealed class Result
        {
            public string Core = "";
            public int CaptureFrame;
            public int IndicesImported;
            public string BaselineSummary = "";
            public string GoldenPath = "";
            public string RelocatedDiff = "";
            public string VandalDiff = "";
            public List<string> Failures = new List<string>();
            public bool Passed => Failures.Count == 0;
        }

        public static Result Run(Rom baseRom, string corePath = DefaultCore,
                                 int indexCount = DefaultIndexCount,
                                 int captureFrame = BootScript.InGameFrame)
        {
            var result = new Result { Core = corePath, CaptureFrame = captureFrame };

            byte[] baselineBytes = baseRom.Snapshot();
            var relocated = BuildControlRom(baseRom, indexCount, flattenTo: null, out int imported);
            var vandalised = BuildControlRom(baseRom, indexCount, flattenTo: 5, out _);
            result.IndicesImported = imported;

            using var baselineFrame = BootScript.CaptureAt(corePath, baselineBytes, captureFrame);
            using var relocatedFrame = BootScript.CaptureAt(corePath, relocated, captureFrame);
            using var vandalFrame = BootScript.CaptureAt(corePath, vandalised, captureFrame);

            // Gate 0 -- the baseline must match a human-inspected reference frame for THIS core.
            //
            // An earlier version of this gate merely counted distinct colours, and a run against
            // bsnes-mercury sailed through it: the core rendered a completely garbled jungle
            // (wrong palette, vertical striping) which nonetheless had 65 distinct colours. Both
            // control ROMs were equally garbled, so gates 1 and 2 compared garbage to garbage and
            // reported PASS. Any "is this plausibly a game screen?" heuristic has that failure
            // mode; only a reference frame someone actually looked at does not.
            //
            // A core with no golden therefore FAILS rather than passes -- an unverified core is
            // an unverified result.
            string goldenPath = GoldenPath(corePath, captureFrame);
            result.GoldenPath = goldenPath;
            result.BaselineSummary = $"{baselineFrame.Width}x{baselineFrame.Height}";

            if (!System.IO.File.Exists(goldenPath))
            {
                result.Failures.Add(
                    $"gate0 golden: no reference frame at {goldenPath}. This core is unverified -- " +
                    $"capture one with `--emu-golden --core {corePath} --frames {captureFrame}`, " +
                    "LOOK at the image to confirm it shows the expected in-game scene, then re-run. " +
                    "(Do not skip the looking: a garbled frame passes every automated heuristic.)");
            }
            else
            {
                using var golden = SKBitmap.Decode(goldenPath);
                if (golden.Width != baselineFrame.Width || golden.Height != baselineFrame.Height)
                {
                    result.Failures.Add(
                        $"gate0 golden: size mismatch, golden {golden.Width}x{golden.Height} vs " +
                        $"baseline {baselineFrame.Width}x{baselineFrame.Height} -- the boot script " +
                        "landed on a different screen than when the golden was captured.");
                }
                else
                {
                    var goldenDiff = FrameCapture.Compare(golden, baselineFrame);
                    result.BaselineSummary += goldenDiff.Identical
                        ? ", matches golden"
                        : $", DIFFERS from golden ({goldenDiff})";
                    if (!goldenDiff.Identical)
                        result.Failures.Add(
                            $"gate0 golden: baseline does not match {goldenPath} ({goldenDiff}). Either " +
                            "the boot script no longer lands on the same scene, or the core changed.");
                }
            }

            // Gate 1 -- negative control: relocation + repoint must be invisible in play.
            var relocDiff = FrameCapture.Compare(baselineFrame, relocatedFrame);
            result.RelocatedDiff = relocDiff.ToString();
            if (!relocDiff.Identical)
                result.Failures.Add(
                    $"gate1 relocation: expected an identical frame, got {relocDiff}. Either the free " +
                    "space is not free, the repoint is wrong, or the re-tiled sprite renders differently " +
                    "on hardware (e.g. more OAM entries than the scanline limit allows).");

            // Gate 2 -- positive control: a changed sprite must actually show up, and stay local.
            var vandalDiff = FrameCapture.Compare(baselineFrame, vandalFrame);
            result.VandalDiff = vandalDiff.ToString();
            if (vandalDiff.Identical)
            {
                result.Failures.Add(
                    "gate2 vandal: frame is identical to baseline -- imported sprite data is not " +
                    "reaching the screen, which also invalidates gate1's 'identical' result.");
            }
            else
            {
                if (vandalDiff.DifferingPixels < MinVandalPixels)
                    result.Failures.Add(
                        $"gate2 vandal: only {vandalDiff.DifferingPixels} px differ (< {MinVandalPixels}) -- " +
                        "too small to be a replaced sprite.");

                double bboxArea = (double)(vandalDiff.MaxX - vandalDiff.MinX + 1) *
                                  (vandalDiff.MaxY - vandalDiff.MinY + 1) /
                                  (vandalFrame.Width * vandalFrame.Height);
                if (bboxArea > MaxVandalBboxArea)
                    result.Failures.Add(
                        $"gate2 vandal: difference covers {bboxArea:P0} of the screen (> {MaxVandalBboxArea:P0}) -- " +
                        "a replaced sprite should be localised; this looks like a crash or corrupted layer.");
            }

            return result;
        }

        /// <summary>
        /// Builds a control ROM by re-importing the first <paramref name="indexCount"/> sprites'
        /// own poses through the M2b writer. <paramref name="flattenTo"/> null keeps the original
        /// colours (relocation should be invisible); a palette index flattens every opaque pixel
        /// to it, preserving silhouette and budgets while being unmistakable on screen.
        /// </summary>
        private static byte[] BuildControlRom(Rom baseRom, int indexCount, int? flattenTo, out int imported)
        {
            var clone = baseRom.Clone();
            var ledger = new ImportLedger { SourceRomSha256 = flattenTo == null ? "v3-relocated" : "v3-vandal" };
            var freeRuns = FreeSpace.Scan(baseRom); // pristine scan; the ledger tracks occupancy
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
                    SpriteImporter.Import(clone, index, pose,
                        new ImportOptions { Source = $"v3-0x{index:X}", FreeRuns = freeRuns }, ledger);
                    imported++;
                }
                catch (ImportException) { /* over budget or out of space: a valid skip */ }
            }

            return clone.Snapshot();
        }

    }
}
