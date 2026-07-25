using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// Port of the GFX pointer table lookup (Form1.cs: gfxArray, Form1.Image.Header.cs).
    /// A sprite "image index" is a 3-byte pointer at gfxArray + index; indices step by 4,
    /// starting at 0x8c, ending at the first all-zero pointer.
    /// </summary>
    public static class GfxTable
    {
        public const int BaseAddress = 0xbbcc9c;
        public const int FirstIndex = 0x8c;

        public static int ResolveSpriteAddress(Rom rom, int imageIndex) => rom.Read24(BaseAddress + imageIndex);

        /// <summary>First file offset that is not reachable through the HiROM $C0-$FF window --
        /// i.e. where a pointer has to use the ExHiROM $40-$7D form instead.</summary>
        public const int HiRomCeiling = 0x400000;

        /// <summary>
        /// The pointer value for a file offset.
        ///
        /// Below 4 MB: <c>0xC00000 + fileOffset</c> (linear HiROM; matches 2,707/2,714 existing
        /// pointers -- M2b spec C.6). At or above 4 MB there is no such window, and the address
        /// *is* the file offset: ExHiROM maps $40-$7D:0000-FFFF straight onto 0x400000+, which is
        /// also what <see cref="Rom.Mask"/> already returns for that range. Verified by
        /// <c>--verify-m3</c> on a real core before anything relies on it.
        /// </summary>
        public static int PointerFor(int fileOffset) =>
            fileOffset < HiRomCeiling ? 0xC00000 + fileOffset : fileOffset;

        /// <summary>
        /// Repoints image index <paramref name="imageIndex"/> to <paramref name="address"/>
        /// (specs/m2b-writer-spec.md C.6). The address must round-trip through
        /// <see cref="PointerFor"/>, asserted rather than derived, so a caller passing a raw file
        /// offset by mistake fails loudly instead of writing a pointer that reads back a
        /// different sprite.
        ///
        /// An extended-space pointer is additionally refused unless the ROM is actually that
        /// large: on a stock 4 MB ROM a $40-$7D pointer addresses nothing, and the only thing
        /// worse than refusing it is writing it.
        /// </summary>
        public static void WritePointer(Rom rom, int imageIndex, int address)
        {
            int fileOffset = Rom.Mask(address);
            if (PointerFor(fileOffset) != address)
                throw new System.InvalidOperationException(
                    $"pointer address 0x{address:X} is not the canonical pointer for file offset " +
                    $"0x{fileOffset:X} (expected 0x{PointerFor(fileOffset):X}).");

            if (fileOffset >= rom.Length)
                throw new System.InvalidOperationException(
                    $"pointer address 0x{address:X} targets file offset 0x{fileOffset:X}, past the " +
                    $"end of a 0x{rom.Length:X}-byte ROM. Extended-space pointers need an expanded " +
                    "(ExHiROM) ROM -- see specs/m3-expansion-spec.md.");

            rom.Write24(BaseAddress + imageIndex, address);
        }

        /// <summary>Walks image indices from <see cref="FirstIndex"/> by 4 until the first all-zero pointer.</summary>
        public static IEnumerable<int> EnumerateImageIndices(Rom rom)
        {
            for (int index = FirstIndex; ; index += 4)
            {
                if (ResolveSpriteAddress(rom, index) == 0)
                    yield break;
                yield return index;
            }
        }

        /// <summary>True if <paramref name="imageIndex"/> is one of the table's populated indices
        /// (specs/m2b-writer-spec.md Part F: <c>IndexNotInTable</c>).</summary>
        public static bool IsValidIndex(Rom rom, int imageIndex) =>
            EnumerateImageIndices(rom).Contains(imageIndex);
    }
}
