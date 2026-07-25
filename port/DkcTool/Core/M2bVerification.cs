using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// V2b (specs/m2b-writer-spec.md Part E): the blocking headless gate for M2b. Per import,
    /// checks pixel-exactness, the pointer, containment (the changed byte-set is exactly the
    /// allocated range plus the 3 pointer bytes), non-destruction of the replaced slot, and
    /// ledger integrity. Reuses the M2a trick: the game's own decoded poses are known-good
    /// input, so re-importing a sprite's own pose into a fresh copy has a known-correct answer.
    /// </summary>
    public static class M2bVerification
    {
        public sealed class IsolatedResult
        {
            public int TotalIndices;
            public int PassIndices;
            public int FailIndices;
            public int EmptySkipped;
            public int ExceedsCharBudgetSkipped;
            public List<string> Failures = new List<string>();
        }

        public sealed class CumulativeResult
        {
            public int Imported;
            public bool NoFreeSpaceHit;
            public long FreeBytesAtStart;
            public long BytesWritten;
            public List<string> Failures = new List<string>();

            /// <summary>Fraction of the scanned free space actually occupied by sprite data when
            /// the run exhausted. A regression that discards partial runs (the re-scan bug) shows
            /// up here as a collapse in utilisation, not as a gate failure -- exhausting after 17
            /// poses and after 100 both look like "a clean NoFreeSpace refusal" otherwise.</summary>
            public double Utilisation => FreeBytesAtStart == 0 ? 0 : (double)BytesWritten / FreeBytesAtStart;
        }

        /// <summary>Minimum share of scanned free space the cumulative run must actually use.
        /// First-fit over variable-sized poses leaves some tail waste in each run, so this is
        /// well below 1.0; the re-scan bug scored 0.16.</summary>
        public const double MinUtilisation = 0.75;

        /// <summary>Decodes a sprite's own pose, cropped to its opaque pixel bbox -- the same
        /// "known-good input" trick M2a's real-ROM corpus uses.</summary>
        public static int[,]? ExtractOwnPose(Rom rom, int address, out int originX, out int originY)
        {
            var canvas = TilerHarness.DecodeIndexCanvas(rom, address);
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

            if (maxX < 0) { originX = 0; originY = 0; return null; }

            int w = maxX - minX + 1, h = maxY - minY + 1;
            var pose = new int[h, w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    pose[r, c] = canvas[minY + r, minX + c];

            originX = minX;
            originY = minY;
            return pose;
        }

        /// <summary>Gates 1-4 for one completed import. <paramref name="preImportSnapshot"/> is the
        /// ROM's byte state immediately before this specific import (not necessarily the very
        /// first snapshot of a cumulative run).</summary>
        private static void RunGates(Rom rom, byte[] preImportSnapshot, ImportResult result, int[,] pose, List<string> failures)
        {
            // Gate 1: pixel -- re-decode the written ROM at the new pointer; must equal the
            // source pose at the origin the importer actually used.
            var canvas = TilerHarness.DecodeIndexCanvas(rom, result.NewPointerAddress);
            int h = pose.GetLength(0), w = pose.GetLength(1);
            int ox = result.Drift.NewMinX, oy = result.Drift.NewMinY;
            string? pixelMismatch = null;
            for (int r = 0; r < h && pixelMismatch == null; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    int expected = pose[r, c];
                    int actual = (oy + r < canvas.GetLength(0) && ox + c < canvas.GetLength(1))
                        ? canvas[oy + r, ox + c] : -1;
                    if (actual != expected)
                    {
                        pixelMismatch = $"({r},{c}) expected {expected}, got {actual}";
                        break;
                    }
                }
            }
            if (pixelMismatch != null)
                failures.Add($"gate1 pixel idx 0x{result.ImageIndex:X}: {pixelMismatch}");

            // Gate 2: pointer -- reads back the expected address and masks to the allocated offset.
            int readBack = GfxTable.ResolveSpriteAddress(rom, result.ImageIndex);
            if (readBack != result.NewPointerAddress)
                failures.Add($"gate2 pointer idx 0x{result.ImageIndex:X}: expected 0x{result.NewPointerAddress:X}, read 0x{readBack:X}");
            else if (Rom.Mask(readBack) != result.AllocatedOffset)
                failures.Add($"gate2 pointer idx 0x{result.ImageIndex:X}: masks to 0x{Rom.Mask(readBack):X}, expected offset 0x{result.AllocatedOffset:X}");

            // Gate 3: containment -- every changed byte must fall within the allocated range or
            // be one of the 3 pointer bytes. Catches any stray write. This is a subset check, not
            // a set-equality check: a filler byte that happens to already equal the serialized
            // byte at that position (0x00 is common in both) is a legitimate no-op write, not a
            // missing one -- the invariant that matters is that nothing *outside* the sanctioned
            // range changed.
            byte[] after = rom.Snapshot();
            var allowedChanged = new HashSet<int>();
            for (int i = 0; i < result.Serialized.Length; i++) allowedChanged.Add(result.AllocatedOffset + i);
            int pointerFileOffset = Rom.Mask(GfxTable.BaseAddress + result.ImageIndex);
            for (int i = 0; i < 3; i++) allowedChanged.Add(pointerFileOffset + i);

            for (int i = 0; i < after.Length; i++)
            {
                if (after[i] == preImportSnapshot[i]) continue;
                if (!allowedChanged.Contains(i))
                {
                    failures.Add($"gate3 containment idx 0x{result.ImageIndex:X}: stray write at 0x{i:X} " +
                                 $"(outside the allocated range and pointer bytes)");
                    break;
                }
            }

            // Gate 4: non-destruction -- the replaced slot's original bytes are untouched.
            int oldFileOffset = Rom.Mask(result.PreviousPointer);
            bool slotIntact = true;
            for (int i = 0; i < result.Slot.Size; i++)
            {
                if (after[oldFileOffset + i] != preImportSnapshot[oldFileOffset + i]) { slotIntact = false; break; }
            }
            if (!slotIntact)
                failures.Add($"gate4 non-destruction idx 0x{result.ImageIndex:X}: original slot at 0x{oldFileOffset:X} changed");
        }

        /// <summary>Gate 5: ledger integrity -- no two allocations overlap, all fall within a
        /// free run drawn from <paramref name="freeRuns"/>, none crosses a bank boundary.</summary>
        private static List<string> ValidateLedger(ImportLedger ledger, List<FreeSpace.Run> freeRuns)
        {
            var failures = new List<string>();
            var scanned = freeRuns;
            var allocations = ledger.Allocations.OrderBy(a => a.Offset).ToList();

            for (int i = 0; i < allocations.Count; i++)
            {
                var a = allocations[i];

                bool within = scanned.Any(r => a.Offset >= r.Start && a.Offset + a.Length <= r.End);
                if (!within)
                    failures.Add($"gate5 ledger idx 0x{a.ImageIndex:X}: 0x{a.Offset:X}+0x{a.Length:X} not within any scanned free run");

                int end = a.Offset + a.Length - 1;
                if ((a.Offset >> 16) != (end >> 16))
                    failures.Add($"gate5 ledger idx 0x{a.ImageIndex:X}: allocation crosses a bank boundary");

                if (i + 1 < allocations.Count && a.Offset + a.Length > allocations[i + 1].Offset)
                    failures.Add($"gate5 ledger idx 0x{a.ImageIndex:X} and 0x{allocations[i + 1].ImageIndex:X}: allocations overlap");
            }

            return failures;
        }

        /// <summary>Isolated corpus: for each real image index, import that sprite's own pose into
        /// a fresh ROM copy and run gates 1-5. Capacity forbids doing this cumulatively (2,714 x
        /// ~756B p50 far exceeds the 77 KB of free space), so each index gets its own copy.</summary>
        public static IsolatedResult RunIsolated(Rom baseRom)
        {
            var result = new IsolatedResult();

            foreach (int index in GfxTable.EnumerateImageIndices(baseRom))
            {
                result.TotalIndices++;
                int address = GfxTable.ResolveSpriteAddress(baseRom, index);

                int[,]? pose;
                try { pose = ExtractOwnPose(baseRom, address, out _, out _); }
                catch (Exception ex)
                {
                    result.FailIndices++;
                    result.Failures.Add($"idx 0x{index:X}: decode failed: {ex.Message}");
                    continue;
                }

                if (pose == null) { result.EmptySkipped++; continue; }

                var clone = baseRom.Clone();
                byte[] preSnapshot = clone.Snapshot();
                var ledger = new ImportLedger { SourceRomSha256 = "isolated-corpus" };

                ImportResult importResult;
                try
                {
                    importResult = SpriteImporter.Import(clone, index, pose,
                        new ImportOptions { DryRun = false, Source = $"idx-0x{index:X}" }, ledger);
                }
                catch (ImportException ex) when (ex.Code == ImportErrorCode.ExceedsCharBudget)
                {
                    result.ExceedsCharBudgetSkipped++;
                    continue;
                }
                catch (Exception ex)
                {
                    result.FailIndices++;
                    result.Failures.Add($"idx 0x{index:X}: unexpected refusal: {ex.Message}");
                    continue;
                }

                var failures = new List<string>();
                RunGates(clone, preSnapshot, importResult, pose, failures);
                failures.AddRange(ValidateLedger(ledger, FreeSpace.Scan(new Rom((byte[])preSnapshot.Clone()))));

                if (failures.Count == 0) result.PassIndices++;
                else { result.FailIndices++; result.Failures.AddRange(failures); }
            }

            return result;
        }

        /// <summary>Cumulative corpus: one run importing poses into a single ROM copy, in table
        /// order, until free space is exhausted -- exercises ledger reuse, fragmentation, and a
        /// clean NoFreeSpace refusal. Uses the stock in-ROM padding pool (92 KB, ~99 poses).</summary>
        public static CumulativeResult RunCumulative(Rom baseRom) =>
            RunCumulative(baseRom, FreeSpace.Scan(baseRom), sourceTag: "cumulative-corpus");

        /// <summary>
        /// Same corpus and gates, against whatever <paramref name="freeRuns"/> supplies. Used
        /// directly by <see cref="RunCumulativeExtended"/> (specs/m3-expansion-spec.md C.5) to run
        /// this same gate at ExHiROM scale: thousands of poses against the extended half instead
        /// of 99 against the stock pool, proving the M2b writer and its gates hold at the capacity
        /// M3 actually buys, not just at the size the pool happened to allow before it.
        /// </summary>
        public static CumulativeResult RunCumulative(Rom baseRom, List<FreeSpace.Run> freeRuns, string sourceTag)
        {
            var result = new CumulativeResult();
            var clone = baseRom.Clone();
            var ledger = new ImportLedger { SourceRomSha256 = sourceTag };

            // Scanned once, from the pristine ROM: re-scanning a written ROM drops each
            // allocation's whole remaining run (see ImportOptions.FreeRuns). The ledger is what
            // tracks occupancy across the run.
            result.FreeBytesAtStart = freeRuns.Sum(r => (long)r.Length);

            foreach (int index in GfxTable.EnumerateImageIndices(baseRom))
            {
                int address = GfxTable.ResolveSpriteAddress(baseRom, index);

                int[,]? pose;
                try { pose = ExtractOwnPose(baseRom, address, out _, out _); }
                catch { continue; }
                if (pose == null) continue;

                byte[] preImportSnapshot = clone.Snapshot();

                ImportResult importResult;
                try
                {
                    importResult = SpriteImporter.Import(clone, index, pose,
                        new ImportOptions { DryRun = false, Source = $"idx-0x{index:X}", FreeRuns = freeRuns }, ledger);
                }
                catch (ImportException ex) when (ex.Code == ImportErrorCode.NoFreeSpace)
                {
                    result.NoFreeSpaceHit = true;
                    break;
                }
                catch (ImportException ex) when (ex.Code == ImportErrorCode.ExceedsCharBudget)
                {
                    continue; // same as isolated: an oversized sprite is a valid skip, not a bug
                }

                result.Imported++;
                result.BytesWritten += importResult.Serialized.Length;
                RunGates(clone, preImportSnapshot, importResult, pose, result.Failures);
            }

            result.Failures.AddRange(ValidateLedger(ledger, freeRuns));

            if (result.NoFreeSpaceHit && result.Utilisation < MinUtilisation)
                result.Failures.Add(
                    $"cumulative utilisation {result.Utilisation:P0} below the {MinUtilisation:P0} floor: " +
                    $"wrote {result.BytesWritten} of {result.FreeBytesAtStart} scanned bytes in " +
                    $"{result.Imported} imports -- free space is being discarded, not filled.");

            return result;
        }

        /// <summary>
        /// V2b cumulative against an expanded ROM (specs/m3-expansion-spec.md C.5): builds an 8 MB
        /// ExHiROM image from <paramref name="baseRom"/> (which must be the stock 4 MB HiROM --
        /// <see cref="Expansion.RequireStockBase"/>) and runs the cumulative corpus against
        /// <see cref="Expansion.ExtendedRuns"/> instead of the stock pool. At ~3.8 MB of capacity
        /// against a corpus that needs only a few MB, this is expected to import the *whole*
        /// pointer table without ever exhausting free space -- a materially different, stronger
        /// claim than the stock gate's "99 poses before NoFreeSpace".
        /// </summary>
        public static CumulativeResult RunCumulativeExtended(Rom baseRom)
        {
            byte[] expandedBytes = Expansion.Build(baseRom, Expansion.ExpandedSize,
                Expansion.MapModeExHiRom, fixChecksum: true, mirrorLowBank: true);
            var expanded = new Rom(expandedBytes);
            return RunCumulative(expanded, Expansion.ExtendedRuns(), sourceTag: "cumulative-corpus-m3-extended");
        }
    }
}
