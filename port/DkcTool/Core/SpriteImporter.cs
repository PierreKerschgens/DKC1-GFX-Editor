using System;
using System.Linq;

namespace DkcTool.Core
{
    public sealed class ImportOptions
    {
        public bool DryRun;

        /// <summary>Recorded in the ledger (e.g. the source PNG's filename).</summary>
        public string Source = "";

        /// <summary>
        /// Free-space runs to allocate from, scanned **once from the pristine ROM**. Must be
        /// supplied by any caller doing more than one import into the same ROM: a re-scan of an
        /// already-written ROM sees the new sprite as reachable from the pointer table and drops
        /// its *entire* run from the candidate list, throwing away all the unused bytes after the
        /// allocation. Measured cost of re-scanning: 17 imports consumed 79 KB to write 13 KB
        /// (~85% waste) and hit NoFreeSpace at 17 poses instead of ~100.
        ///
        /// The ledger -- not the scan -- is the authority on what is already occupied
        /// (see <see cref="FreeSpace.Allocate"/>'s <c>alreadyAllocated</c>).
        /// Null is safe only for a single import into a freshly-loaded ROM.
        /// </summary>
        public System.Collections.Generic.List<FreeSpace.Run>? FreeRuns;
    }

    /// <summary>
    /// The new pose's bbox origin/extent against the replaced slot's (specs/m2b-writer-spec.md
    /// Part G). The origin side is always zero delta by construction -- the tiler is anchored at
    /// the replaced slot's own placement-bbox top-left (Q4) -- so what actually varies, and what
    /// this exists to catch, is the *extent*: a redesigned pose that occupies a different volume
    /// than the frame it replaces. Not a refusal; a report, since fixing it means either
    /// re-authoring the pose or editing the (separate, untouched) hitbox table.
    /// </summary>
    public sealed class GeometryDrift
    {
        public int OldMinX, OldMinY, OldMaxX, OldMaxY;
        public int NewMinX, NewMinY, NewMaxX, NewMaxY;

        public int DeltaMinX => NewMinX - OldMinX;
        public int DeltaMinY => NewMinY - OldMinY;
        public int DeltaMaxX => NewMaxX - OldMaxX;
        public int DeltaMaxY => NewMaxY - OldMaxY;

        /// <summary>Any axis delta over 4px (Part G's threshold).</summary>
        public bool ExceedsThreshold =>
            Math.Abs(DeltaMinX) > 4 || Math.Abs(DeltaMinY) > 4 ||
            Math.Abs(DeltaMaxX) > 4 || Math.Abs(DeltaMaxY) > 4;
    }

    public sealed class ImportResult
    {
        public int ImageIndex;
        public SpriteSlot Slot = null!;

        public int CharCount;
        public int OamEntries;
        public bool ExceedsOamBudget;

        public byte[] Serialized = Array.Empty<byte>();
        public int AllocatedOffset;
        public int NewPointerAddress;
        public int PreviousPointer;

        public GeometryDrift Drift = null!;
        public HitboxTable.Record Hitbox;

        public bool DryRun;
        public bool Written;
    }

    /// <summary>
    /// Import orchestration (specs/m2b-writer-spec.md C.7): pose -> tile -> budget-check ->
    /// serialize -> drift report -> allocate free space -> write -> repoint -> ledger. Every
    /// refusal (Part F) happens before step 6 (allocate) writes anything, so an import either
    /// completes fully or changes nothing (the original slot's bytes are never touched either
    /// way -- M2b always relocates, per Part A).
    /// </summary>
    public static class SpriteImporter
    {
        public static ImportResult Import(Rom rom, int imageIndex, int[,] pose, ImportOptions options, ImportLedger ledger)
        {
            if (!GfxTable.IsValidIndex(rom, imageIndex))
                throw new ImportException(ImportErrorCode.IndexNotInTable,
                    $"image index 0x{imageIndex:X} is not in the GFX pointer table.");

            // 1. Target slot -> origin (Q4: replaced slot's placement bbox top-left).
            var slot = SpriteSlot.Read(rom, imageIndex);
            int originX = slot.PlacementMinX, originY = slot.PlacementMinY;

            // 2. Tile the pose onto that origin.
            var tiled = SpriteTiler.Build(pose, originX, originY);

            // 3. Budget check: hard cap on chars, soft (reported) cap on OAM entries.
            if (tiled.ExceedsCharBudget)
                throw new ImportException(ImportErrorCode.ExceedsCharBudget,
                    $"tiled pose needs {tiled.CharCount} chars, exceeds the {SpriteTiler.MaxChars}-char budget.");

            // 4. Serialize (M1's byte-exact encoder).
            byte[] serialized = SpriteEncoder.Serialize(tiled.Model);

            // 5. Geometry drift report (Part G) + current hitbox, for visibility only.
            int poseHeight = pose.GetLength(0), poseWidth = pose.GetLength(1);
            var drift = new GeometryDrift
            {
                OldMinX = slot.PlacementMinX,
                OldMinY = slot.PlacementMinY,
                OldMaxX = slot.PlacementMaxX,
                OldMaxY = slot.PlacementMaxY,
                NewMinX = originX,
                NewMinY = originY,
                NewMaxX = originX + poseWidth,
                NewMaxY = originY + poseHeight,
            };
            var hitbox = HitboxTable.Read(rom, imageIndex);

            var result = new ImportResult
            {
                ImageIndex = imageIndex,
                Slot = slot,
                CharCount = tiled.CharCount,
                OamEntries = tiled.OamEntries,
                ExceedsOamBudget = tiled.ExceedsOamBudget,
                Serialized = serialized,
                Drift = drift,
                Hitbox = hitbox,
                PreviousPointer = slot.SpriteAddress,
                DryRun = options.DryRun,
            };

            // 6. Allocate free space (refuses with NoFreeSpace if nothing fits). Prefer the
            // caller's pristine scan; see ImportOptions.FreeRuns for why re-scanning here is
            // only safe for a one-shot import.
            var freeRuns = options.FreeRuns ?? FreeSpace.Scan(rom);
            int offset = FreeSpace.Allocate(freeRuns, serialized.Length, ledger.AllocatedRanges);
            int newAddress = GfxTable.PointerFor(offset);
            result.AllocatedOffset = offset;
            result.NewPointerAddress = newAddress;

            if (options.DryRun)
            {
                result.Written = false;
                return result;
            }

            // The filler byte of the run this came from, captured *before* the write overwrites it,
            // so a rollback can restore the bytes and not just the pointer (ImportAllocation.FillByte).
            var sourceRun = freeRuns.FirstOrDefault(r => offset >= r.Start && offset + serialized.Length <= r.End);
            int fillByte = sourceRun?.Value ?? -1;

            // 7. Write + repoint. 8. Ledger. The original slot's bytes are never touched.
            rom.WriteBytes(offset, serialized);
            GfxTable.WritePointer(rom, imageIndex, newAddress);
            ledger.Append(imageIndex, offset, serialized.Length, slot.SpriteAddress, options.Source, fillByte);
            result.Written = true;

            return result;
        }
    }
}
