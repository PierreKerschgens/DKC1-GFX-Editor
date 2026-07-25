using System;

namespace DkcTool.Core
{
    /// <summary>Error taxonomy for imports (specs/m2b-writer-spec.md Part F). Every code is a
    /// refusal raised before any byte is written -- an import either completes fully or
    /// changes nothing.</summary>
    public enum ImportErrorCode
    {
        /// <summary>Opaque pixel not in the target palette.</summary>
        UnmappedColor,
        /// <summary>PNG exceeds the 256x256 canvas.</summary>
        PoseTooLarge,
        /// <summary>Tiler output > 88 chars.</summary>
        ExceedsCharBudget,
        /// <summary>No free run fits -- needs M3 (expansion).</summary>
        NoFreeSpace,
        /// <summary>Image index absent from the GFX pointer table.</summary>
        IndexNotInTable,
        /// <summary>Ledger's sourceRomSha256 does not match the input ROM.</summary>
        LedgerMismatch,
    }

    public sealed class ImportException : Exception
    {
        public ImportErrorCode Code { get; }

        public ImportException(ImportErrorCode code, string message) : base(message)
        {
            Code = code;
        }
    }
}
