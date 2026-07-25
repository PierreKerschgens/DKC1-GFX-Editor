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
    }

    /// <summary>
    /// JSON sidecar at <c>&lt;out&gt;.dkctool.json</c> (specs/m2b-writer-spec.md C.5), so repeat
    /// and batch runs never hand out the same free-space bytes twice, and every import is
    /// reversible: <see cref="ImportAllocation.PreviousPointer"/> makes rollback a 3-byte write.
    /// </summary>
    public sealed class ImportLedger
    {
        public string SourceRomSha256 = "";
        public List<ImportAllocation> Allocations = new List<ImportAllocation>();

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

            var doc = JsonSerializer.Deserialize<LedgerJson>(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"could not parse ledger '{path}'.");

            if (!string.Equals(doc.sourceRomSha256, sourceRomSha256, StringComparison.OrdinalIgnoreCase))
                throw new ImportException(ImportErrorCode.LedgerMismatch,
                    $"ledger '{path}' was built against a different ROM " +
                    $"(sha256 {doc.sourceRomSha256}), not the current input ({sourceRomSha256}).");

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
                });
            }
            return ledger;
        }

        public void Append(int imageIndex, int offset, int length, int previousPointer, string source)
        {
            Allocations.Add(new ImportAllocation
            {
                ImageIndex = imageIndex,
                Offset = offset,
                Length = length,
                PreviousPointer = previousPointer,
                Source = source,
                Timestamp = DateTimeOffset.UtcNow,
            });
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
                }).ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        }

        private sealed class LedgerJson
        {
            [JsonPropertyName("version")] public int version { get; set; }
            [JsonPropertyName("sourceRomSha256")] public string sourceRomSha256 { get; set; } = "";
            [JsonPropertyName("allocations")] public List<AllocationJson>? allocations { get; set; }
        }

        private sealed class AllocationJson
        {
            [JsonPropertyName("imageIndex")] public string imageIndex { get; set; } = "";
            [JsonPropertyName("offset")] public string offset { get; set; } = "";
            [JsonPropertyName("length")] public string length { get; set; } = "";
            [JsonPropertyName("previousPointer")] public string previousPointer { get; set; } = "";
            [JsonPropertyName("source")] public string? source { get; set; }
            [JsonPropertyName("timestamp")] public string? timestamp { get; set; }
        }
    }
}
