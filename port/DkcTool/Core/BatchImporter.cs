using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core
{
    public sealed class BatchOutcome
    {
        public PlannedPose Planned = null!;
        public bool Success;
        public ImportResult? Result;
        public ImportErrorCode? RefusalCode;
        public string? RefusalMessage;
    }

    public sealed class BatchReport
    {
        public List<BatchOutcome> Outcomes = new List<BatchOutcome>();
        public IEnumerable<BatchOutcome> Imported => Outcomes.Where(o => o.Success);
        public IEnumerable<BatchOutcome> Refused => Outcomes.Where(o => !o.Success);
        public long BytesWritten => Imported.Sum(o => (long)o.Result!.Serialized.Length);
    }

    /// <summary>
    /// M4 batch orchestration (specs/m4-batch-spec.md C.3): resolves M2b's open item verbatim --
    /// "M4's batch mode should import N poses in <b>one</b> invocation against one pristine scan".
    /// Callers own the ROM clone, the single <see cref="ImportLedger"/> and the single free-run
    /// scan; this class only walks a resolved plan against them.
    /// </summary>
    public static class BatchImporter
    {
        /// <summary>
        /// Preflight capacity check (C.3): sums the serialized size every planned pose *would*
        /// need, against the scanned pool. A manifest that plainly cannot fit refuses fast with a
        /// `--expand` pointer instead of grinding through hundreds of individual NoFreeSpace
        /// refusals. This is a sum, not a bin-packing simulation -- first-fit fragmentation can
        /// still refuse an individual pose even after this check passes; that shows up as a normal
        /// per-pose outcome in <see cref="Run"/>, not here. Poses that would refuse regardless of
        /// capacity (bad index, unmapped colour, over char budget) are skipped rather than
        /// counted, since they never reach the allocator either way.
        /// </summary>
        public static long EstimateBytes(Rom rom, List<PlannedPose> plan, SKBitmap sheetBitmap, SKColor[] palette)
        {
            long total = 0;
            foreach (var p in plan)
            {
                try
                {
                    if (!GfxTable.IsValidIndex(rom, p.ImageIndex)) continue;
                    var slot = SpriteSlot.Read(rom, p.ImageIndex);
                    var pose = PoseLoader.LoadRegion(sheetBitmap, p.RectX, p.RectY, p.RectW, p.RectH, palette);
                    var tiled = SpriteTiler.Build(pose.Pixels, slot.PlacementMinX, slot.PlacementMinY);
                    if (tiled.ExceedsCharBudget) continue;
                    total += SpriteEncoder.Serialize(tiled.Model).Length;
                }
                catch (ImportException)
                {
                    // Surfaced as a normal per-pose refusal in Run(); doesn't consume free space.
                }
            }
            return total;
        }

        /// <summary>
        /// Runs every planned pose against one pristine scan and one ledger (C.3 steps 2-4).
        /// Never aborts on an individual refusal: a run that dies on pose 4 of 533 wastes the
        /// operator's time, so every outcome is recorded and the loop continues.
        /// </summary>
        public static BatchReport Run(Rom rom, List<PlannedPose> plan, SKBitmap sheetBitmap, SKColor[] palette,
            ImportLedger ledger, List<FreeSpace.Run> freeRuns, bool dryRun, string sourceTag,
            bool anchorBottom = false, bool alignStrip = false)
        {
            var report = new BatchReport();

            // Strip-level vertical placement (spec A.17). The sheet already aligns a run's poses to
            // each other -- their bottoms differ only by the bob the artist drew. Anchoring each
            // pose to its own target slot throws that away and substitutes the *replaced*
            // animation's per-frame variation, which is what made an imported walk cycle bob.
            //
            // Instead: measure each pose's height above its strip's own baseline on the sheet, and
            // reproduce exactly that offset below a single ground reference for the whole run. The
            // reference is the first target slot's placement bottom, so the run keeps sitting where
            // the animation it replaces sat.
            var originY = new Dictionary<(int Strip, int Position), int>();
            if (alignStrip)
            {
                foreach (var strip in plan.GroupBy(p => p.Strip))
                {
                    int baseline = strip.Max(p => p.RectY + p.RectH - 1);
                    var first = strip.OrderBy(p => p.Position).First();
                    int reference = SpriteSlot.Read(rom, first.ImageIndex).PlacementMaxY;

                    foreach (var p in strip)
                    {
                        int aboveBaseline = baseline - (p.RectY + p.RectH - 1);
                        originY[(p.Strip, p.Position)] = reference - aboveBaseline - (p.RectH - 1);
                    }
                }
            }

            foreach (var p in plan)
            {
                var outcome = new BatchOutcome { Planned = p };
                try
                {
                    var pose = PoseLoader.LoadRegion(sheetBitmap, p.RectX, p.RectY, p.RectW, p.RectH, palette);
                    var options = new ImportOptions
                    {
                        DryRun = dryRun,
                        Source = $"{sourceTag} strip{p.Strip}:{p.Position}",
                        FreeRuns = freeRuns,
                        AnchorBottom = anchorBottom,
                        OriginY = originY.TryGetValue((p.Strip, p.Position), out int oy) ? oy : (int?)null,
                    };
                    outcome.Result = SpriteImporter.Import(rom, p.ImageIndex, pose.Pixels, options, ledger);
                    outcome.Success = true;
                }
                catch (ImportException ex)
                {
                    outcome.Success = false;
                    outcome.RefusalCode = ex.Code;
                    outcome.RefusalMessage = ex.Message;
                }
                report.Outcomes.Add(outcome);
            }
            return report;
        }

        /// <summary>QA overlay (C.4): the source sheet with every planned pose boxed and annotated
        /// with its target image index -- green for imported, red for refused. The only artefact
        /// that makes a wrong mapping visible before the ROM is booted.</summary>
        public static void DumpOverlay(string sheetPath, BatchReport report, string outPath)
        {
            using var src = SKBitmap.Decode(sheetPath);
            using var surface = new SKBitmap(src.Width, src.Height);
            using (var canvas = new SKCanvas(surface))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(src, 0, 0);
                using var okBox = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0, 255, 0, 200) };
                using var badBox = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 0, 0, 220) };
                using var text = new SKPaint { Color = new SKColor(255, 255, 0, 220), TextSize = 10, IsAntialias = true };

                foreach (var o in report.Outcomes)
                {
                    var p = o.Planned;
                    canvas.DrawRect(p.RectX - 0.5f, p.RectY - 0.5f, p.RectW, p.RectH, o.Success ? okBox : badBox);
                    canvas.DrawText($"0x{p.ImageIndex:X}", p.RectX, Math.Max(9, p.RectY - 2), text);
                }
            }
            using var img = SKImage.FromBitmap(surface);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.OpenWrite(outPath);
            data.SaveTo(fs);
        }
    }
}
