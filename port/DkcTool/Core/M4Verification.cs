using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// V4 verification (specs/m4-batch-spec.md Part D): the blocking gate for M4.
    ///
    /// Sheet-dependent gates (V4a, V4b) need the real sprite sheets under
    /// <see cref="DefaultSheetDir"/> -- gitignored local assets, the same rule as the ROM and the
    /// emulator cores/goldens. When none are found, those gates report SKIPPED rather than failing
    /// the whole run, the same discipline the V3 gate already applies to a missing core golden.
    ///
    /// V4c, V4d, V4e and V4f need only the ROM: V4c/V4d build a synthetic sheet from the ROM's own
    /// decoded sprites (the same "known-good input" trick M2a/M2b/M3's gates already use), so they
    /// always run regardless of what local art happens to be present.
    /// </summary>
    public static class M4Verification
    {
        public const string DefaultSheetDir = "port/sprites";
        public const string GoldenDir = "port/DkcTool/testdata/slice-golden";

        /// <summary>The two representative cores V3's original gate used, before M3 widened to
        /// six for the ExHiROM probe specifically. "Both cores" in the V4d spec line refers to
        /// this pair; --all-cores runs the full six.</summary>
        public static readonly string[] DefaultCores =
        {
            "port/emu/cores/snes9x_libretro.dylib",
            "port/emu/cores/bsnes_libretro.dylib",
        };

        public sealed class GateResult
        {
            public string Name = "";
            public bool Skipped;
            public List<string> Failures = new List<string>();
            public string Summary = "";
            public bool Passed => Skipped || Failures.Count == 0;
        }

        public static string[] FindDefaultSheets() =>
            Directory.Exists(DefaultSheetDir)
                ? Directory.GetFiles(DefaultSheetDir, "*.png").OrderBy(p => p).ToArray()
                : Array.Empty<string>();

        // ------------------------------------------------------------------------------- V4a

        /// <summary>
        /// V4a: slicer determinism. Two checks, not one -- slicing the same file twice in this
        /// process must agree (catches non-determinism within a run, e.g. unordered iteration),
        /// and slicing must agree with a golden list from a previous run (catches a change over
        /// time, e.g. a segmentation tweak). The golden bootstraps itself on first use; commit it
        /// once the numbering is trusted, per C.1's "pin it with a golden slice list".
        /// </summary>
        public static GateResult RunV4a(string[] sheetPaths)
        {
            var result = new GateResult { Name = "V4a slicer determinism" };
            if (sheetPaths.Length == 0)
            {
                result.Skipped = true;
                result.Summary = $"skipped: no sheets found under {DefaultSheetDir}";
                return result;
            }

            Directory.CreateDirectory(GoldenDir);
            int totalStrips = 0, totalPoses = 0, bootstrapped = 0;

            foreach (string path in sheetPaths)
            {
                var a = SheetSlicer.Slice(path);
                var b = SheetSlicer.Slice(path);
                string dumpA = Dump(a), dumpB = Dump(b);
                if (dumpA != dumpB)
                {
                    result.Failures.Add($"{Path.GetFileName(path)}: two slices of the same file in " +
                                        "one process disagree -- the slicer is not deterministic.");
                    continue;
                }

                string goldenPath = Path.Combine(GoldenDir,
                    Path.GetFileNameWithoutExtension(path) + ".slice-golden.txt");
                if (!File.Exists(goldenPath))
                {
                    File.WriteAllText(goldenPath, dumpA);
                    bootstrapped++;
                }
                else
                {
                    string golden = File.ReadAllText(goldenPath);
                    if (golden != dumpA)
                        result.Failures.Add($"{Path.GetFileName(path)}: slice disagrees with the " +
                            $"committed golden {goldenPath} -- pose numbering changed, which silently " +
                            "retargets every manifest addressing this sheet.");
                }

                totalStrips += a.Strips.Count;
                totalPoses += a.Poses.Count();
            }

            result.Summary = $"{sheetPaths.Length} sheet(s), {totalStrips} strip(s), {totalPoses} pose(s)" +
                             (bootstrapped > 0 ? $", {bootstrapped} golden(s) bootstrapped this run (commit them)" : "");
            return result;
        }

        private static string Dump(SheetSlicer.SlicedSheet sheet)
        {
            var sb = new StringBuilder();
            foreach (var strip in sheet.Strips)
                foreach (var pose in strip.Poses)
                    sb.Append(strip.Index).Append(',').Append(pose.Position).Append(',')
                      .Append(pose.MinX).Append(',').Append(pose.MinY).Append(',')
                      .Append(pose.MaxX).Append(',').Append(pose.MaxY).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------- V4b

        /// <summary>V4b: every sliced pose round-trips M1 (encode -&gt; decode -&gt; identical),
        /// the same claim M2a's synthetic battery makes (TilerHarness.RunCase), now over real
        /// sheet content instead of hand-built patterns.</summary>
        public static GateResult RunV4b(string[] sheetPaths, SKColor[] palette)
        {
            var result = new GateResult { Name = "V4b M1 round-trip per sliced pose" };
            if (sheetPaths.Length == 0)
            {
                result.Skipped = true;
                result.Summary = $"skipped: no sheets found under {DefaultSheetDir}";
                return result;
            }

            int total = 0, pass = 0, unmapped = 0, overBudget = 0;
            foreach (string path in sheetPaths)
            {
                using var bitmap = SKBitmap.Decode(path);
                var sheet = SheetSlicer.Slice(path);
                foreach (var strip in sheet.Strips)
                {
                    foreach (var pose in strip.Poses)
                    {
                        if (pose.OverCanvas) continue;

                        PoseResult loaded;
                        try { loaded = PoseLoader.LoadRegion(bitmap, pose.MinX, pose.MinY, pose.Width, pose.Height, palette); }
                        catch (ImportException) { unmapped++; continue; }
                        if (loaded.Width == 0) continue;

                        total++;
                        var caseResult = TilerHarness.RunCase(
                            $"{Path.GetFileName(path)} strip{strip.Index}:{pose.Position}", loaded.Pixels);
                        if (caseResult.ExceedsCharBudget) { overBudget++; continue; }
                        if (caseResult.Passed) pass++;
                        else result.Failures.Add(
                            $"{Path.GetFileName(path)} strip{strip.Index}:{pose.Position}: {caseResult.FailureDetail}");
                    }
                }
            }

            result.Summary = $"{pass}/{total} round-tripped, {unmapped} unmapped-colour skipped, " +
                             $"{overBudget} over-budget skipped";
            return result;
        }

        // ------------------------------------------------------------------------------- V4c / V4d shared setup

        /// <summary>
        /// Builds a synthetic sheet from the ROM's own decoded poses (the same "known-good input"
        /// trick M2a/M2b/M3's gates already use) laid out left-to-right with wide, uniform gaps so
        /// the slicer groups them into exactly one strip. This is what lets V4c/V4d exercise the
        /// real slice -&gt; manifest -&gt; batch pipeline without depending on the gitignored,
        /// third-party sheets under <see cref="DefaultSheetDir"/>.
        /// </summary>
        private static (SKBitmap Bitmap, string Path, List<int> Indices) BuildSyntheticSheet(
            Rom rom, SKColor[] palette, int count)
        {
            // Each pose's original canvas origin is captured, not discarded. Laying every pose out
            // at the same y would flatten the vertical relationships the real sheets carry, and
            // BatchImporter's strip alignment reconstructs placement *from* those relationships --
            // so a flattened fixture cannot round-trip, and V4d's "re-import is a content-preserving
            // relocation" identity would fail for a reason that says nothing about the importer.
            var poses = new List<(int Index, int[,] Pixels, int OriginY)>();
            foreach (int index in GfxTable.EnumerateImageIndices(rom))
            {
                if (poses.Count >= count) break;
                int address = GfxTable.ResolveSpriteAddress(rom, index);
                int[,]? pose;
                int originY;
                try { pose = M2bVerification.ExtractOwnPose(rom, address, out _, out originY); }
                catch { continue; }
                if (pose == null) continue;
                poses.Add((index, pose, originY));
            }

            const int gap = 24;
            int minOriginY = poses.Count == 0 ? 0 : poses.Min(p => p.OriginY);
            int width = gap + poses.Sum(p => p.Pixels.GetLength(1) + gap);
            int height = gap * 2 + (poses.Count == 0 ? 0
                : poses.Max(p => p.OriginY - minOriginY + p.Pixels.GetLength(0)));

            var bitmap = new SKBitmap(Math.Max(1, width), Math.Max(1, height));
            var indices = new List<int>();
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                int x = gap;
                foreach (var (index, pixels, poseOriginY) in poses)
                {
                    int h = pixels.GetLength(0), w = pixels.GetLength(1);
                    int top = gap + (poseOriginY - minOriginY);
                    for (int r = 0; r < h; r++)
                        for (int c = 0; c < w; c++)
                        {
                            int pi = pixels[r, c];
                            if (pi == 0) continue;
                            bitmap.SetPixel(x + c, top + r, palette[pi]);
                        }
                    indices.Add(index);
                    x += w + gap;
                }
            }

            string tmpPath = Path.Combine(Path.GetTempPath(), $"dkctool-m4-synthetic-{Guid.NewGuid():N}.png");
            using (var img = SKImage.FromBitmap(bitmap))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(tmpPath))
                data.SaveTo(fs);

            return (bitmap, tmpPath, indices);
        }

        /// <summary>Slices, wraps in a one-strip manifest addressing every pose back to its own
        /// original index, and resolves. Shared setup for V4c and V4d.</summary>
        private static (SheetSlicer.SlicedSheet Sheet, SKBitmap Bitmap, string TmpPath, List<PlannedPose> Plan)?
            PrepareSyntheticPlan(Rom rom, SKColor[] palette, int count, List<string> failures)
        {
            var (bitmap, tmpPath, indices) = BuildSyntheticSheet(rom, palette, count);
            var sheet = SheetSlicer.Slice(tmpPath);

            if (sheet.Strips.Count != 1 || sheet.Strips[0].Poses.Count != indices.Count)
            {
                failures.Add($"setup: synthetic sheet sliced into {sheet.Strips.Count} strip(s) / " +
                    $"{(sheet.Strips.Count > 0 ? sheet.Strips[0].Poses.Count : 0)} pose(s), " +
                    $"expected 1 strip / {indices.Count} pose(s) -- layout assumption broken.");
                bitmap.Dispose();
                try { File.Delete(tmpPath); } catch { }
                return null;
            }

            var manifest = new Manifest { Version = 1, Sheet = tmpPath, Palette = "Donkey Kong 1P" };
            manifest.Strips.Add(new ManifestStripEntry
            {
                Strip = 0,
                Indices = indices.Select(i => $"0x{i:X}").ToList(),
            });
            var plan = manifest.Resolve(rom, sheet, bitmap);
            return (sheet, bitmap, tmpPath, plan);
        }

        // ------------------------------------------------------------------------------- V4c

        /// <summary>
        /// V4c: batch of N against one pristine scan. Ledger has N non-overlapping allocations
        /// (reuses M2bVerification's gate 5 verbatim -- the invariant doesn't change with scale),
        /// pixel exactness at the real written address for every import, and the whole-batch
        /// containment diff shows exactly the expected changed set (every changed byte falls
        /// inside an allocation or its 3 pointer bytes), plus non-destruction of every replaced
        /// slot. This is the gate that would catch the re-scan trap (Part C.3, Part F).
        /// </summary>
        public static GateResult RunV4c(Rom baseRom, int count = 60)
        {
            var result = new GateResult { Name = "V4c batch of N, one pristine scan" };
            var palette = Palette.Read(baseRom, PalettePointers.Table["Donkey Kong 1P"]);

            var prepared = PrepareSyntheticPlan(baseRom, palette, count, result.Failures);
            if (prepared == null) return result;
            var (_, bitmap, tmpPath, plan) = prepared.Value;

            try
            {
                var clone = baseRom.Clone();
                byte[] preSnapshot = clone.Snapshot();
                var ledger = new ImportLedger { SourceRomSha256 = "v4c-synthetic" };
                var freeRuns = FreeSpace.Scan(baseRom); // one pristine scan, shared by the whole run

                var report = BatchImporter.Run(clone, plan, bitmap, palette, ledger, freeRuns,
                    dryRun: false, sourceTag: "v4c");
                var imported = report.Imported.ToList();

                if (imported.Count == 0)
                {
                    result.Failures.Add("no poses imported -- cannot validate an empty batch.");
                    return result;
                }

                // Gate 5 (ledger): no overlaps, everything within a scanned run, no bank crossing.
                result.Failures.AddRange(M2bVerification.ValidateLedger(ledger, freeRuns));

                // Gate 1 (pixel exactness), per import: re-decode the written ROM at the new
                // pointer and compare against the same region PoseLoader would have cropped.
                foreach (var o in imported)
                {
                    var r = o.Result!;
                    var canvas = TilerHarness.DecodeIndexCanvas(clone, r.NewPointerAddress);
                    var expected = PoseLoader.LoadRegion(bitmap, o.Planned.RectX, o.Planned.RectY,
                        o.Planned.RectW, o.Planned.RectH, palette).Pixels;
                    int h = expected.GetLength(0), w = expected.GetLength(1);
                    int ox = r.Drift.NewMinX, oy = r.Drift.NewMinY;

                    bool ok = true;
                    for (int rr = 0; rr < h && ok; rr++)
                    {
                        for (int cc = 0; cc < w; cc++)
                        {
                            int exp = expected[rr, cc];
                            int act = (oy + rr < canvas.GetLength(0) && ox + cc < canvas.GetLength(1))
                                ? canvas[oy + rr, ox + cc] : -1;
                            if (act != exp)
                            {
                                result.Failures.Add($"pixel mismatch idx 0x{r.ImageIndex:X} at ({rr},{cc}): " +
                                                    $"expected {exp}, got {act}");
                                ok = false;
                                break;
                            }
                        }
                    }
                }

                // Gate 3 (whole-batch containment): every changed byte is inside some allocation
                // or its pointer's 3 bytes -- the V2b containment diff, run once over the batch.
                byte[] postSnapshot = clone.Snapshot();
                var allowed = new HashSet<int>();
                foreach (var o in imported)
                {
                    var r = o.Result!;
                    for (int i = 0; i < r.Serialized.Length; i++) allowed.Add(r.AllocatedOffset + i);
                    int pointerOffset = Rom.Mask(GfxTable.BaseAddress + r.ImageIndex);
                    for (int i = 0; i < 3; i++) allowed.Add(pointerOffset + i);
                }
                int strayWrites = 0;
                for (int i = 0; i < postSnapshot.Length; i++)
                {
                    if (postSnapshot[i] == preSnapshot[i]) continue;
                    if (!allowed.Contains(i)) strayWrites++;
                }
                if (strayWrites > 0)
                    result.Failures.Add($"containment: {strayWrites} byte(s) changed outside every " +
                                        "allocated range and pointer write.");

                // Gate 4 (non-destruction): every replaced slot's original bytes survive the batch.
                foreach (var o in imported)
                {
                    var r = o.Result!;
                    int oldOffset = Rom.Mask(r.PreviousPointer);
                    for (int i = 0; i < r.Slot.Size; i++)
                    {
                        if (postSnapshot[oldOffset + i] != preSnapshot[oldOffset + i])
                        {
                            result.Failures.Add($"non-destruction: idx 0x{r.ImageIndex:X}'s original slot " +
                                                $"at 0x{oldOffset:X} changed.");
                            break;
                        }
                    }
                }

                result.Summary = $"{imported.Count}/{plan.Count} imported, {ledger.Allocations.Count} " +
                                 $"ledger allocation(s), {strayWrites} stray write(s)";
            }
            finally
            {
                bitmap.Dispose();
                try { File.Delete(tmpPath); } catch { }
            }

            return result;
        }

        // ------------------------------------------------------------------------------- V4d

        /// <summary>
        /// V4d: the V3 two-sided emulator gate (specs/v3-emulator-spec.md Part B), applied to a
        /// batch-imported ROM.
        ///
        /// This deliberately does <b>not</b> call <see cref="Emulator.V3Verification.Run"/> on the
        /// batch output directly. That method's own control-building does its own
        /// <c>FreeSpace.Scan(baseRom)</c>, and its contract everywhere else in this codebase is
        /// that <c>baseRom</c> is pristine -- passing it an already-batch-modified ROM re-scans a
        /// ROM whose free space has already been partly consumed, exactly the re-scan trap
        /// <see cref="ImportOptions.FreeRuns"/>'s doc comment warns about (measured elsewhere as
        /// ~85% waste). Caught in this gate's own dogfooding: the vandal control silently imported
        /// zero sprites and "gate2 vandal: frame is identical" fired for a reason that had nothing
        /// to do with M4.
        ///
        /// Instead, three ROMs are each built independently from the one pristine
        /// <paramref name="baseRom"/> -- baseline (untouched), the real M4 batch output, and a
        /// flattened positive control -- and compared pairwise. The batch output is expected to
        /// render <b>identically</b> to baseline (it reimports each pose's own unchanged pixels,
        /// just relocated -- the same "relocation is invisible" claim V3's gate1 makes); the
        /// flattened control must visibly and locally differ, or the "identical" result above is
        /// unfalsifiable (V3's same two-sided reasoning).
        /// </summary>
        public static GateResult RunV4d(Rom baseRom, string[] cores, int count = 60)
        {
            var result = new GateResult { Name = "V4d V3 emulator gate on the batch output" };
            var palette = Palette.Read(baseRom, PalettePointers.Table["Donkey Kong 1P"]);

            var prepared = PrepareSyntheticPlan(baseRom, palette, count, result.Failures);
            if (prepared == null) return result;
            var (_, bitmap, tmpPath, plan) = prepared.Value;

            try
            {
                // Both builds scan FreeSpace.Scan(baseRom) independently from the pristine base --
                // never from each other's output -- so neither can hit the re-scan trap above.
                var freeRuns = FreeSpace.Scan(baseRom);

                var batchClone = baseRom.Clone();
                var batchLedger = new ImportLedger { SourceRomSha256 = "v4d-batch" };
                var batchReport = BatchImporter.Run(batchClone, plan, bitmap, palette, batchLedger,
                    freeRuns, dryRun: false, sourceTag: "v4d-batch");
                if (!batchReport.Imported.Any())
                {
                    result.Failures.Add("no poses imported into the batch ROM -- nothing to gate.");
                    return result;
                }

                byte[] vandalBytes = BuildVandalRom(baseRom, plan, bitmap, palette, freeRuns, out int vandalCount);
                if (vandalCount == 0)
                {
                    result.Failures.Add("vandal control: 0 poses imported -- the positive control did not fire.");
                    return result;
                }

                int passedCores = 0;
                foreach (string core in cores)
                {
                    var (ok, detail) = RunTwoSidedGate(baseRom, batchClone.Snapshot(), vandalBytes, core);
                    if (ok) passedCores++;
                    else result.Failures.Add($"[{Path.GetFileNameWithoutExtension(core)}] {detail}");
                }

                result.Summary = $"{batchReport.Imported.Count()} pose(s) batch-imported, {vandalCount} in " +
                                 $"the vandal control; two-sided gate: {passedCores}/{cores.Length} core(s) passed";
            }
            finally
            {
                bitmap.Dispose();
                try { File.Delete(tmpPath); } catch { }
            }

            return result;
        }

        /// <summary>Positive control: the same plan, but every opaque pixel flattened to one
        /// palette index (silhouette and budgets preserved, colour unmistakable) -- mirrors
        /// <see cref="Emulator.V3Verification"/>'s own vandal control, built from the pristine
        /// <paramref name="baseRom"/> rather than chained after the batch output.</summary>
        private static byte[] BuildVandalRom(Rom baseRom, List<PlannedPose> plan, SKBitmap sheetBitmap,
            SKColor[] palette, List<FreeSpace.Run> freeRuns, out int imported)
        {
            var clone = baseRom.Clone();
            var ledger = new ImportLedger { SourceRomSha256 = "v4d-vandal" };
            imported = 0;

            foreach (var p in plan)
            {
                try
                {
                    var pose = PoseLoader.LoadRegion(sheetBitmap, p.RectX, p.RectY, p.RectW, p.RectH, palette);
                    for (int r = 0; r < pose.Pixels.GetLength(0); r++)
                        for (int c = 0; c < pose.Pixels.GetLength(1); c++)
                            if (pose.Pixels[r, c] != 0) pose.Pixels[r, c] = 5;

                    SpriteImporter.Import(clone, p.ImageIndex, pose.Pixels,
                        new ImportOptions { Source = "v4d-vandal", FreeRuns = freeRuns }, ledger);
                    imported++;
                }
                catch (ImportException) { /* over budget or out of space: a valid skip */ }
            }

            return clone.Snapshot();
        }

        /// <summary>Gate0 (golden) + gate1 (batch output must render identically to baseline) +
        /// gate2 (vandal control must visibly, locally differ) -- V3's own gate structure, run by
        /// hand here since <see cref="Emulator.V3Verification.Run"/> cannot take pre-built ROMs.</summary>
        private static (bool Ok, string Detail) RunTwoSidedGate(Rom baseRom, byte[] batchBytes, byte[] vandalBytes, string core)
        {
            string stateName = Emulator.V3Verification.DefaultStateName;
            string statePath = Emulator.StateCapture.PathFor(core, stateName);
            int settle = Emulator.StateCapture.SettleFrames;

            if (!File.Exists(statePath))
                return (false, $"no captured state at {statePath} -- this core is unverified (run --emu-state first).");

            byte[] state = File.ReadAllBytes(statePath);
            using var baselineFrame = Emulator.StateCapture.CaptureFromState(core, baseRom.Snapshot(), state, settle, true);
            using var batchFrame = Emulator.StateCapture.CaptureFromState(core, batchBytes, state, settle, true);
            using var vandalFrame = Emulator.StateCapture.CaptureFromState(core, vandalBytes, state, settle, true);

            string goldenPath = Emulator.V3Verification.GoldenPath(core, stateName);
            if (!File.Exists(goldenPath))
                return (false, $"no golden at {goldenPath} -- this core is unverified.");
            using var golden = SKBitmap.Decode(goldenPath);
            var goldenDiff = Emulator.FrameCapture.Compare(golden, baselineFrame);
            if (!goldenDiff.Identical)
                return (false, $"baseline does not match golden {goldenPath} ({goldenDiff}).");

            var batchDiff = Emulator.FrameCapture.Compare(baselineFrame, batchFrame);
            if (!batchDiff.Identical)
                return (false, $"gate1: batch output (content-preserving relocation) expected an " +
                              $"identical frame, got {batchDiff}.");

            var vandalDiff = Emulator.FrameCapture.Compare(baselineFrame, vandalFrame);
            if (vandalDiff.Identical)
                return (false, "gate2: vandal control frame is identical to baseline -- imported sprite " +
                              "data is not reaching the screen, which also invalidates gate1's result.");
            if (vandalDiff.DifferingPixels < Emulator.V3Verification.MinVandalPixels)
                return (false, $"gate2: only {vandalDiff.DifferingPixels} px differ " +
                              $"(< {Emulator.V3Verification.MinVandalPixels}).");

            return (true, "");
        }

        // ------------------------------------------------------------------------------- V4e

        /// <summary>
        /// V4e: refusals fire. Each rule is tested at the layer that actually raises it -- an
        /// unmapped colour and an over-budget pose go straight through the real per-pose import
        /// path; the manifest-shape rules (both/neither, length mismatch, duplicate index) go
        /// through <see cref="Manifest.Resolve"/> against a synthetic one-strip sheet, so these
        /// four don't depend on the gitignored real sheets either.
        /// </summary>
        public static GateResult RunV4e(Rom rom)
        {
            var result = new GateResult { Name = "V4e refusals fire" };
            int checks = 0;

            void Expect<TEx>(string label, Action action) where TEx : Exception
            {
                checks++;
                try
                {
                    action();
                    result.Failures.Add($"{label}: expected a refusal ({typeof(TEx).Name}), none was thrown.");
                }
                catch (TEx) { /* expected */ }
                catch (Exception ex)
                {
                    result.Failures.Add($"{label}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}");
                }
            }

            var palette = Palette.Read(rom, PalettePointers.Table["Donkey Kong 1P"]);

            Expect<ImportException>("unmapped colour", () =>
            {
                using var bmp = new SKBitmap(4, 4);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    bmp.SetPixel(1, 1, new SKColor(1, 2, 3, 255)); // not in any real DKC palette
                }
                PoseLoader.LoadRegion(bmp, 0, 0, 4, 4, palette);
            });

            Expect<ImportException>("over char budget", () =>
            {
                int index = GfxTable.EnumerateImageIndices(rom).First();
                var grid = new int[96, 96];
                for (int r = 0; r < 96; r++) for (int c = 0; c < 96; c++) grid[r, c] = 1;
                var ledger = new ImportLedger { SourceRomSha256 = "v4e" };
                SpriteImporter.Import(rom.Clone(), index, grid,
                    new ImportOptions { DryRun = true, Source = "v4e-over-budget" }, ledger);
            });

            var (bitmap, tmpPath, indices) = BuildSyntheticSheet(rom, palette, 4);
            try
            {
                var sheet = SheetSlicer.Slice(tmpPath);
                var strip = sheet.Strips.FirstOrDefault(s => s.Poses.Count >= 2);
                if (strip == null || indices.Count < 2)
                {
                    result.Failures.Add("manifest-shape refusals: synthetic sheet did not produce a " +
                                        ">=2-pose strip -- cannot exercise these checks.");
                }
                else
                {
                    Expect<ManifestException>("both animation and indices", () =>
                    {
                        var m = new Manifest();
                        m.Strips.Add(new ManifestStripEntry
                        {
                            Strip = strip.Index, Animation = "0x0", Indices = new List<string> { "0x8C" },
                        });
                        m.Resolve(rom, sheet, bitmap);
                    });

                    Expect<ManifestException>("neither animation nor indices", () =>
                    {
                        var m = new Manifest();
                        m.Strips.Add(new ManifestStripEntry { Strip = strip.Index });
                        m.Resolve(rom, sheet, bitmap);
                    });

                    Expect<ManifestException>("strip/index length mismatch", () =>
                    {
                        var m = new Manifest();
                        m.Strips.Add(new ManifestStripEntry
                        {
                            Strip = strip.Index, Indices = new List<string> { "0x8C" }, // 1, strip has >=2
                        });
                        m.Resolve(rom, sheet, bitmap);
                    });

                    Expect<ManifestException>("duplicate index with differing poses", () =>
                    {
                        var m = new Manifest();
                        m.Strips.Add(new ManifestStripEntry
                        {
                            Strip = strip.Index,
                            Indices = Enumerable.Repeat("0x8C", strip.Poses.Count).ToList(),
                        });
                        m.Resolve(rom, sheet, bitmap); // distinct source poses -> refuses
                    });
                }
            }
            finally
            {
                bitmap.Dispose();
                try { File.Delete(tmpPath); } catch { }
            }

            result.Summary = $"{checks - result.Failures.Count}/{checks} refusal(s) fired as expected";
            return result;
        }

        // ------------------------------------------------------------------------------- V4f

        /// <summary>V4f: the animation table parses 440/440 with 0 failures (A.7) -- the derived
        /// index side of the manifest has no silent-drift mode otherwise.</summary>
        public static GateResult RunV4f(Rom rom)
        {
            var result = new GateResult { Name = "V4f animation table 440/440" };
            var scripts = AnimationTable.ParseAll(rom);
            int bad = scripts.Count(s => !s.Ok);
            if (bad > 0)
                foreach (var s in scripts.Where(s => !s.Ok).Take(10))
                    result.Failures.Add($"animation {s.Animation}: {s.Error}");
            result.Summary = $"{scripts.Count - bad}/{scripts.Count} parsed";
            return result;
        }

        /// <summary>
        /// V4g: an import chain reverts byte-exactly. Imports N poses in one run, then M more in a
        /// *second* run that carries the first ledger forward, then reverts once and asserts the
        /// result is sha256-identical to the ROM the chain started from.
        ///
        /// This gate exists because the claim came before the test. `ImportLedger` asserted "every
        /// import is reversible" while `--revert` only ever undid an expansion, so the property was
        /// documented, plausible, and never once executed. The two-run shape is the point: a single
        /// run's ledger reverting is the easy half, and the chained case is where the ledger has to
        /// keep the *original* sha256 rather than the intermediate one.
        /// </summary>
        public static GateResult RunV4g(Rom rom)
        {
            var result = new GateResult { Name = "V4g import chain reverts byte-exactly" };
            string originalSha = ImportLedger.ComputeSha256(rom.Snapshot());
            var palette = Palette.Read(rom, PalettePointers.Table["Donkey Kong 1P"]);

            var prepared = PrepareSyntheticPlan(rom, palette, 12, result.Failures);
            if (prepared == null) return result;
            var (_, bitmap, tmpPath, plan) = prepared.Value;

            try
            {
                var working = rom.Clone();
                var freeRuns = Expansion.FreeRunsFor(working);
                var ledger = new ImportLedger { SourceRomSha256 = originalSha };

                // Run 1: the first six poses.
                foreach (var p in plan.Take(6))
                    SpriteImporter.Import(working, p.ImageIndex,
                        PoseLoader.LoadRegion(bitmap, p.RectX, p.RectY, p.RectW, p.RectH, palette).Pixels,
                        new ImportOptions { Source = "v4g-run1", FreeRuns = freeRuns }, ledger);
                int afterRun1 = ledger.Allocations.Count;

                // Run 2: the rest, against the same carried-forward ledger -- the chained case.
                foreach (var p in plan.Skip(6))
                    SpriteImporter.Import(working, p.ImageIndex,
                        PoseLoader.LoadRegion(bitmap, p.RectX, p.RectY, p.RectW, p.RectH, palette).Pixels,
                        new ImportOptions { Source = "v4g-run2", FreeRuns = freeRuns }, ledger);

                if (ledger.Allocations.Count <= afterRun1)
                    result.Failures.Add("second run added no allocations -- the chain is not being exercised.");

                // The ledger must still name the ROM the chain started from, not run 1's output.
                if (!string.Equals(ledger.SourceRomSha256, originalSha, StringComparison.OrdinalIgnoreCase))
                    result.Failures.Add("ledger's sourceRomSha256 drifted from the original ROM.");

                if (ImportLedger.ComputeSha256(working.Snapshot()) == originalSha)
                    result.Failures.Add("ROM is unchanged after importing -- the gate would pass vacuously.");

                if (!ledger.MatchesRom(working, out string mismatch))
                    result.Failures.Add($"ledger does not describe its own output: {mismatch}");

                var (reverted, refilled) = ledger.RevertAllocations(working);
                if (refilled != reverted)
                    result.Failures.Add($"only {refilled}/{reverted} allocation(s) could restore bytes -- " +
                                        "a byte-exact revert is not provable.");

                string revertedSha = ImportLedger.ComputeSha256(working.Snapshot());
                if (revertedSha != originalSha)
                    result.Failures.Add($"revert produced sha256 {revertedSha[..16]}..., expected {originalSha[..16]}...");

                result.Summary = $"{ledger.Allocations.Count} allocation(s) over 2 chained run(s), " +
                                 $"{refilled} refilled, sha256 back to the original";
            }
            finally
            {
                bitmap.Dispose();
                try { File.Delete(tmpPath); } catch { }
            }

            return result;
        }

        // ------------------------------------------------------------------------------- orchestration

        public static int Run(Rom rom, string[] args)
        {
            string[] sheets = FindDefaultSheets();
            var palette = Palette.Read(rom, PalettePointers.Table["Donkey Kong 1P"]);

            bool allCores = Array.IndexOf(args, "--all-cores") >= 0;
            string[] cores = allCores ? Emulator.V3Verification.AllCores : DefaultCores;

            Console.WriteLine("=== M4 verification (V4) ===");
            if (sheets.Length == 0)
                Console.WriteLine($"note: no sheets found under {DefaultSheetDir} -- V4a/V4b will be SKIPPED " +
                                  "(V4c/V4d/V4e/V4f use a synthetic sheet built from the ROM's own sprites and " +
                                  "always run).");
            Console.WriteLine();

            var gates = new List<GateResult>
            {
                RunV4f(rom),
                RunV4a(sheets),
                RunV4b(sheets, palette),
                RunV4e(rom),
                RunV4c(rom),
                RunV4g(rom),
                RunV4d(rom, cores),
            };

            bool allOk = true;
            foreach (var g in gates)
            {
                string status = g.Skipped ? "SKIP" : g.Passed ? "PASS" : "FAIL";
                Console.WriteLine($"[{status}] {g.Name}: {g.Summary}");
                foreach (var f in g.Failures.Take(20)) Console.WriteLine($"    {f}");
                if (!g.Passed) allOk = false;
            }

            Console.WriteLine();
            Console.WriteLine(allOk ? "V4 verification: PASS" : "V4 verification: FAIL");
            return allOk ? 0 : 1;
        }
    }
}
