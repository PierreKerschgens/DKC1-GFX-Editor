using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DkcTool.Core
{
    public sealed class ImportAllocation
    {
        public int ImageIndex;
        public int Offset;
        public int Length;
        public int PreviousPointer;
        public string Source = "";
        public DateTimeOffset Timestamp;

        /// <summary>
        /// The constant filler byte of the free-space run this allocation was carved from
        /// (<see cref="FreeSpace.Run.Value"/>), or -1 for ledgers written before this field
        /// existed.
        ///
        /// Recorded so a rollback can be *byte-exact* rather than pointer-only: restoring
        /// <see cref="PreviousPointer"/> alone leaves the written sprite bytes orphaned in the
        /// pool, so the result is logically reverted but not identical to the original, and no
        /// sha256 check can prove it. With the filler byte, revert refills the range and the
        /// result is provably the input ROM again -- the same self-proving discipline
        /// <c>--revert</c> already applies to an expansion.
        /// </summary>
        public int FillByte = -1;

        public bool CanRestoreBytes => FillByte >= 0;
    }

    /// <summary>
    /// JSON sidecar at <c>&lt;out&gt;.dkctool.json</c> (specs/m2b-writer-spec.md C.5), so repeat
    /// and batch runs never hand out the same free-space bytes twice, and imports can be rolled
    /// back: <see cref="ImportAllocation.PreviousPointer"/> restores the GFX pointer and
    /// <see cref="ImportAllocation.FillByte"/> restores the bytes.
    ///
    /// A ledger is a chain, not a snapshot. <see cref="SourceRomSha256"/> always names the
    /// *original* ROM the chain started from, even when the immediate input is an already-imported
    /// image -- so a single revert returns to the original rather than to the previous step, and
    /// no intermediate sidecar has to be kept. <see cref="MatchesRom"/> is what makes carrying a
    /// ledger forward safe.
    /// </summary>
    public sealed class ImportLedger
    {
        public string SourceRomSha256 = "";
        public List<ImportAllocation> Allocations = new List<ImportAllocation>();

        /// <summary>Set when this ledger's ROM is the output of <c>--expand</c>
        /// (specs/m3-expansion-spec.md C.1): the pre-expansion header fields, so the expansion
        /// itself is recorded and <see cref="Core.Expansion.Revert"/> can undo it later.</summary>
        public Expansion.ExpansionRecord? RomExpansion;

        public IEnumerable<(int Offset, int Length)> AllocatedRanges =>
            Allocations.Select(a => (a.Offset, a.Length));

        public static string SidecarPath(string outPath) => outPath + ".dkctool.json";

        public static string ComputeSha256(byte[] romBytes) =>
            Convert.ToHexString(SHA256.HashData(romBytes)).ToLowerInvariant();

        /// <summary>Loads the ledger at <paramref name="path"/> if it exists, otherwise starts a
        /// fresh one. Refuses (<see cref="ImportErrorCode.LedgerMismatch"/>) if an existing
        /// ledger was built against a different ROM -- it would otherwise hand out bytes the
        /// current ROM has never actually reserved.</summary>
        public static ImportLedger LoadOrCreate(string path, string sourceRomSha256)
        {
            if (!File.Exists(path))
                return new ImportLedger { SourceRomSha256 = sourceRomSha256 };

            var ledger = Load(path);

            if (!string.Equals(ledger.SourceRomSha256, sourceRomSha256, StringComparison.OrdinalIgnoreCase))
                throw new ImportException(ImportErrorCode.LedgerMismatch,
                    $"ledger '{path}' was built against a different ROM " +
                    $"(sha256 {ledger.SourceRomSha256}), not the current input ({sourceRomSha256}).");

            return ledger;
        }

        /// <summary>
        /// Parses a ledger without the source-ROM guard. Only for callers whose input is
        /// legitimately *not* the ROM the ledger was built from -- notably <c>--revert</c>, where
        /// the input is the expanded image and `sourceRomSha256` names the pre-expansion original.
        /// Every other caller wants <see cref="LoadOrCreate"/> and its mismatch refusal.
        /// </summary>
        public static ImportLedger Load(string path)
        {
            var doc = JsonSerializer.Deserialize<LedgerJson>(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"could not parse ledger '{path}'.");

            var ledger = new ImportLedger { SourceRomSha256 = doc.sourceRomSha256 };
            foreach (var a in doc.allocations ?? new List<AllocationJson>())
            {
                ledger.Allocations.Add(new ImportAllocation
                {
                    ImageIndex = Convert.ToInt32(a.imageIndex, 16),
                    Offset = Convert.ToInt32(a.offset, 16),
                    Length = Convert.ToInt32(a.length, 16),
                    PreviousPointer = Convert.ToInt32(a.previousPointer, 16),
                    Source = a.source ?? "",
                    Timestamp = DateTimeOffset.Parse(a.timestamp ?? DateTimeOffset.UtcNow.ToString("O")),
                    FillByte = a.fillByte is string fb ? Convert.ToInt32(fb, 16) : -1,
                });
            }
            if (doc.expansion is ExpansionJson e)
            {
                ledger.RomExpansion = new Expansion.ExpansionRecord
                {
                    OriginalSize = Convert.ToInt32(e.originalSize, 16),
                    OriginalSha256 = e.originalSha256,
                    OriginalMapMode = Convert.ToByte(e.originalMapMode, 16),
                    OriginalSizeByte = Convert.ToByte(e.originalSizeByte, 16),
                    OriginalChecksum = Convert.ToInt32(e.originalChecksum, 16),
                    OriginalComplement = Convert.ToInt32(e.originalComplement, 16),
                    TargetSize = Convert.ToInt32(e.targetSize, 16),
                    MapMode = Convert.ToByte(e.mapMode, 16),
                    MirrorLowBank = e.mirrorLowBank,
                    Timestamp = DateTimeOffset.Parse(e.timestamp),
                };
            }
            return ledger;
        }

        public void Append(int imageIndex, int offset, int length, int previousPointer, string source,
                           int fillByte = -1)
        {
            Allocations.Add(new ImportAllocation
            {
                ImageIndex = imageIndex,
                Offset = offset,
                Length = length,
                PreviousPointer = previousPointer,
                Source = source,
                Timestamp = DateTimeOffset.UtcNow,
                FillByte = fillByte,
            });
        }

        /// <summary>
        /// True if <paramref name="rom"/> really is the product of this ledger: every image index
        /// the ledger allocated for must currently point at the allocation that last claimed it.
        ///
        /// This is the guard that makes carrying a ledger forward onto an already-imported ROM
        /// safe. <see cref="LoadOrCreate"/>'s sha256 equality cannot be used there -- the whole
        /// point of a chained import is that the input is *not* the original ROM -- so the ledger
        /// is checked against what the ROM actually contains instead. A stale or foreign ledger
        /// fails here, which is what stops it handing out bytes this ROM never reserved.
        /// </summary>
        public bool MatchesRom(Rom rom, out string detail)
        {
            // Last allocation wins: re-importing the same index legitimately supersedes an earlier
            // allocation, and only the most recent one is what the pointer should reflect.
            foreach (var group in Allocations.GroupBy(a => a.ImageIndex))
            {
                var last = group.OrderBy(a => a.Timestamp).Last();
                int actual = GfxTable.ResolveSpriteAddress(rom, last.ImageIndex);
                int expected = GfxTable.PointerFor(last.Offset);
                if (actual != expected)
                {
                    detail = $"image index 0x{last.ImageIndex:X} points at 0x{actual:X}, but the " +
                             $"ledger's newest allocation for it is 0x{last.Offset:X} " +
                             $"(pointer 0x{expected:X}).";
                    return false;
                }
            }
            detail = "";
            return true;
        }

        /// <summary>
        /// Undoes every allocation in this ledger, newest first: restores each image index's
        /// previous GFX pointer and refills the bytes the import claimed.
        ///
        /// Newest-first matters when one index was imported more than once -- the oldest
        /// allocation holds the pointer the *original* ROM had, so it must be the last write to
        /// land. Refilling is skipped for any allocation predating
        /// <see cref="ImportAllocation.FillByte"/>; the caller is told, because without it the
        /// result cannot be claimed byte-exact.
        /// </summary>
        public (int Reverted, int Refilled) RevertAllocations(Rom rom)
        {
            int refilled = 0;
            foreach (var a in Allocations.OrderByDescending(x => x.Timestamp))
            {
                GfxTable.WritePointer(rom, a.ImageIndex, a.PreviousPointer);
                if (!a.CanRestoreBytes) continue;

                // Extended-half allocations vanish when an expansion is reverted afterwards, but
                // refilling them is harmless and keeps the no-expansion case exact.
                if (a.Offset + a.Length > rom.Length) continue;
                // Rom.Mask is the identity for a raw file offset below 0x800000, so the offset can
                // be passed straight through -- these are file offsets, not bank addresses.
                for (int i = 0; i < a.Length; i++) rom.Write8(a.Offset + i, (byte)a.FillByte);
                refilled++;
            }
            return (Allocations.Count, refilled);
        }

        public void Save(string path)
        {
            var json = new LedgerJson
            {
                version = 1,
                sourceRomSha256 = SourceRomSha256,
                allocations = Allocations.Select(a => new AllocationJson
                {
                    imageIndex = $"0x{a.ImageIndex:X}",
                    offset = $"0x{a.Offset:X}",
                    length = $"0x{a.Length:X}",
                    previousPointer = $"0x{a.PreviousPointer:X}",
                    source = a.Source,
                    timestamp = a.Timestamp.ToString("O"),
                    fillByte = a.CanRestoreBytes ? $"0x{a.FillByte:X2}" : null,
                }).ToList(),
                expansion = RomExpansion is Expansion.ExpansionRecord e ? new ExpansionJson
                {
                    originalSize = $"0x{e.OriginalSize:X}",
                    originalSha256 = e.OriginalSha256,
                    originalMapMode = $"0x{e.OriginalMapMode:X2}",
                    originalSizeByte = $"0x{e.OriginalSizeByte:X2}",
                    originalChecksum = $"0x{e.OriginalChecksum:X4}",
                    originalComplement = $"0x{e.OriginalComplement:X4}",
                    targetSize = $"0x{e.TargetSize:X}",
                    mapMode = $"0x{e.MapMode:X2}",
                    mirrorLowBank = e.MirrorLowBank,
                    timestamp = e.Timestamp.ToString("O"),
                } : null,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        }

        private sealed class LedgerJson
        {
            [JsonPropertyName("version")] public int version { get; set; }
            [JsonPropertyName("sourceRomSha256")] public string sourceRomSha256 { get; set; } = "";
            [JsonPropertyName("allocations")] public List<AllocationJson>? allocations { get; set; }
            [JsonPropertyName("expansion")] public ExpansionJson? expansion { get; set; }
        }

        /// <summary>The reversible record of a <c>--expand</c> run (specs/m3-expansion-spec.md
        /// C.1): everything <see cref="Expansion.Revert"/> needs to undo it.</summary>
        private sealed class ExpansionJson
        {
            [JsonPropertyName("originalSize")] public string originalSize { get; set; } = "";
            [JsonPropertyName("originalSha256")] public string originalSha256 { get; set; } = "";
            [JsonPropertyName("originalMapMode")] public string originalMapMode { get; set; } = "";
            [JsonPropertyName("originalSizeByte")] public string originalSizeByte { get; set; } = "";
            [JsonPropertyName("originalChecksum")] public string originalChecksum { get; set; } = "";
            [JsonPropertyName("originalComplement")] public string originalComplement { get; set; } = "";
            [JsonPropertyName("targetSize")] public string targetSize { get; set; } = "";
            [JsonPropertyName("mapMode")] public string mapMode { get; set; } = "";
            [JsonPropertyName("mirrorLowBank")] public bool mirrorLowBank { get; set; }
            [JsonPropertyName("timestamp")] public string timestamp { get; set; } = "";
        }

        private sealed class AllocationJson
        {
            [JsonPropertyName("imageIndex")] public string imageIndex { get; set; } = "";
            [JsonPropertyName("offset")] public string offset { get; set; } = "";
            [JsonPropertyName("length")] public string length { get; set; } = "";
            [JsonPropertyName("previousPointer")] public string previousPointer { get; set; } = "";
            [JsonPropertyName("source")] public string? source { get; set; }
            [JsonPropertyName("timestamp")] public string? timestamp { get; set; }
            [JsonPropertyName("fillByte")] public string? fillByte { get; set; }
        }
    }
}
