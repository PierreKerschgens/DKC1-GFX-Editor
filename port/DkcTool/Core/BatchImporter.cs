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

    /// <summary>One animation that draws both imported and un-imported art, which is what a
    /// stale frame looks like before it is booted.</summary>
    public sealed class StraddlingAnimation
    {
        public int Animation;
        public List<int> Imported = new List<int>();
        public List<int> Stale = new List<int>();
        public int Frames;
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
            bool anchorBottom = false, bool alignStrip = true, bool flatX = false)
        {
            var report = new BatchReport();

            // Strip-level placement (spec A.17). The sheet already aligns a run's poses to each
            // other -- their bottoms differ only by the bob the artist drew. Anchoring each pose to
            // its own target slot throws that away and substitutes the *replaced* animation's
            // per-frame variation, which is what made an imported walk cycle bob.
            //
            // Instead: pick one reference pose, place it exactly where its target slot's artwork
            // sat, and carry every sibling's sheet-relative offset through unchanged.
            //
            // Both sides of that subtraction must be measured in the same currency, which is the
            // part that took two goes to get right (spec A.17, A.19). The sheet speaks
            // **opaque-pixel** bounds; a slot's placement bbox speaks **tile** bounds, 8px-grid
            // aligned and looser than the art by a per-sprite amount (up to 7px in Y, measured).
            // Subtracting one from the other silently injects that slack, so the reference is read
            // from the slot's *opaque* box -- the same quantity SheetSlicer reports.
            //
            // The reference pose supplies the baseline on *both* sides too. Taking the baseline
            // from the strip's deepest pose while taking the reference from the first pose's slot
            // offsets the whole run by the difference between them, which is only zero when the
            // first pose happens to be the deepest.
            //
            // Horizontally the sheet gives nothing usable -- a strip's X positions are page layout,
            // not animation offsets -- so X defaults to per pose, matching the replaced frame's
            // *centre* rather than its left edge. Left-edge anchoring makes a wider pose grow
            // rightwards and drags the body sideways; centring reproduces whatever horizontal
            // travel the original frame had. Measured: 7 px of centre drift before, 4 px in stock.
            //
            // Per-pose X is what keeps alignment an exact identity when a slot's own artwork is
            // re-imported into it (originX lands back on OpaqueMinX for either parity), which is
            // what V4d gate 1 demands and why alignment can be the default at all.
            //
            // But it inherits the replaced animation's per-frame variation -- the very thing
            // per-pose anchoring was condemned for on the Y axis (A.17) -- and on the DK *run* that
            // is a visible defect (A.20). Stock's run lunges: its bbox centre jumps 9 px over two
            // frames, which reads fine there because the drawn body moved with it. Imported art
            // that does not lunge just teleports sideways. `flatX` is the escape hatch: anchor the
            // whole run to one centre and let the sheet's own art carry the horizontal motion.
            //
            // It cannot be the default -- a single shared centre is not each pose's own centre, so
            // it breaks the V4d identity. The two requirements are genuinely opposed, so this is a
            // per-run choice (`--flat-x`), not a new global rule.
            // Keyed by the plan entry itself (reference identity), not by (strip, position):
            // `poses` may repeat a position deliberately, so that pair is not unique. A repeat is
            // how a strip with fewer poses than its animation has frames covers all of them.
            var originY = new Dictionary<PlannedPose, int>();
            var originX = new Dictionary<PlannedPose, int>();
            if (alignStrip)
            {
                foreach (var strip in plan.GroupBy(p => p.Strip))
                {
                    // One read per distinct *index*: SpriteSlot.Read decodes the sprite and scans
                    // the whole pointer table for aliases, so it is far too expensive to repeat.
                    var slots = strip.GroupBy(p => p.ImageIndex)
                                     .ToDictionary(g => g.Key, g => SpriteSlot.Read(rom, g.Key));

                    var refPose = strip.OrderBy(p => p.Position).First();
                    int baseline = refPose.RectY + refPose.RectH - 1;

                    // Shared floor across strips. Anchoring each strip to its own first slot puts
                    // two animations on two different ground lines -- the imported walk sat 2-3 px
                    // below the run, so DK's feet stepped up as he accelerated. That defect exists
                    // only *between* strips, so per-strip anchoring cannot see it: the same
                    // wrong-level mistake as A.17, one level further out. `groundRef` names one
                    // slot whose opaque bottom every strip lands on; "self" (-1) opts a strip out,
                    // for runs that are legitimately off the floor (swim, rope).
                    int reference = refPose.GroundRef is int g && g >= 0
                        ? SpriteSlot.Read(rom, g).OpaqueMaxY
                        : slots[refPose.ImageIndex].OpaqueMaxY;

                    // A strip may override the batch-wide choice, because two animations in one
                    // manifest can want opposite answers -- the walk matches stock with per-pose
                    // centring while the run and roll need it flattened (A.20/A.22).
                    bool stripFlatX = strip.First().FlatX ?? flatX;

                    // The mean of the replaced frames' centres, not the reference pose's own:
                    // a run's first frame is as likely as any to be a horizontal extreme, and
                    // averaging cannot be thrown off by one outlier the way picking can.
                    // Shared floor implies a shared *centre line* too. Flattening each strip to the
                    // mean of its own slots put the walk at x=129 and the run at x=127, so DK
                    // stepped 2 px sideways at the transition -- the horizontal twin of the
                    // per-strip ground bug. When a groundRef is given it anchors both axes.
                    int flatCentre = !stripFlatX ? 0
                        : refPose.GroundRef is int gx && gx >= 0
                            ? ((Func<SpriteSlot, int>)(gs => (gs.OpaqueMinX + gs.OpaqueMaxX) / 2))(SpriteSlot.Read(rom, gx))
                            : (int)Math.Round(slots.Values.Average(s => (s.OpaqueMinX + s.OpaqueMaxX) / 2.0));

                    foreach (var p in strip)
                    {
                        int belowBaseline = (p.RectY + p.RectH - 1) - baseline;
                        originY[p] = reference + belowBaseline - (p.RectH - 1);

                        var slot = slots[p.ImageIndex];
                        int centreX = stripFlatX ? flatCentre : (slot.OpaqueMinX + slot.OpaqueMaxX) / 2;
                        originX[p] = centreX - (p.RectW - 1) / 2 + (p.OffsetX ?? 0);
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
                        OriginY = originY.TryGetValue(p, out int oy) ? oy : (int?)null,
                        OriginX = originX.TryGetValue(p, out int ox) ? ox : (int?)null,
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

        /// <summary>
        /// Animations that draw <i>some</i> of the imported indices and some un-imported ones.
        /// Every such animation shows stock art part-way through an otherwise imported move.
        ///
        /// <para>This is the one defect a manifest cannot see. A manifest is written in index
        /// ranges; an animation is a <b>draw order</b>, and the two do not have to agree — the
        /// range is neither a superset of the animation (anim 25 rolls out of `0x4B8..0x4EC` and
        /// lands on `0xB4`, an idle frame, which is exactly the "old DK for one frame at the end
        /// of the roll" an operator reported) nor a subset (anim 24 plays only 11 of that range's
        /// 14 indices). The spec has said "verify against draw order, never an index range" since
        /// A.14; this makes the tool say it instead of the reader having to remember.</para>
        ///
        /// <para>A warning, never a refusal: importing one animation of a character at a time is
        /// the normal workflow, so straddling is expected and only becomes a defect when the
        /// straddled animation is one you meant to finish. Ordered by fewest stale indices first —
        /// an animation needing one more index is the cheapest thing to fix.</para>
        /// </summary>
        public static List<StraddlingAnimation> FindStaleFrames(Rom rom, IEnumerable<int> importedIndices)
        {
            var imported = new HashSet<int>(importedIndices);
            var straddling = new List<StraddlingAnimation>();

            var scripts = AnimationTable.ParseAll(rom);
            for (int a = 0; a < scripts.Count; a++)
            {
                var drawn = scripts[a].ImageIndices;
                if (drawn.Count == 0) continue;

                var hit = drawn.Where(imported.Contains).Distinct().OrderBy(i => i).ToList();
                if (hit.Count == 0) continue;

                var stale = drawn.Where(i => !imported.Contains(i)).Distinct().OrderBy(i => i).ToList();
                if (stale.Count == 0) continue;

                straddling.Add(new StraddlingAnimation
                {
                    Animation = a,
                    Imported = hit,
                    Stale = stale,
                    Frames = drawn.Count,
                });
            }

            return straddling.OrderBy(s => s.Stale.Count).ThenBy(s => s.Animation).ToList();
        }

        /// <summary>What an in-place write would cost, per pose and in total.</summary>
        public sealed class FitReport
        {
            public int Total, Fits, TooBig, Aliased;
            public long NewBytes, OldBytes, BytesIfInPlace;
        }

        /// <summary>
        /// Would each imported sprite fit in the bytes it replaces?
        ///
        /// <para>The importer always allocates fresh and repoints — never writes in place — which
        /// is what makes `--revert` byte-exact (V4g) and what makes expansion necessary at all. The
        /// question of whether that is *required* has never been measured, so this measures it.</para>
        ///
        /// <para>A pose is only safely in-place if it fits <b>and</b> its slot has no aliases: when
        /// several image indices point at one sprite, overwriting it changes every one of them, and
        /// a slot shared with an animation nobody is importing would be corrupted silently.</para>
        /// </summary>
        public static FitReport MeasureInPlaceFit(Rom rom, BatchReport report)
        {
            var fit = new FitReport();
            foreach (var o in report.Imported)
            {
                var slot = SpriteSlot.Read(rom, o.Result!.ImageIndex);
                int newLen = o.Result!.Serialized.Length;

                fit.Total++;
                fit.NewBytes += newLen;
                fit.OldBytes += slot.Size;

                bool aliased = slot.AliasIndices.Count > 1;
                if (aliased) fit.Aliased++;

                if (newLen <= slot.Size && !aliased) fit.Fits++;
                else { fit.TooBig += newLen > slot.Size ? 1 : 0; fit.BytesIfInPlace += newLen; }
            }
            return fit;
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
