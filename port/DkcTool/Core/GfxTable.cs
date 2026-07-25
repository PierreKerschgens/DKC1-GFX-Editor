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

        /// <summary>
        /// Repoints image index <paramref name="imageIndex"/> to <paramref name="address"/>
        /// (specs/m2b-writer-spec.md C.6). <paramref name="address"/> must be
        /// <c>0xC00000 + fileOffset</c> (linear HiROM, matching 2,707/2,714 existing pointers);
        /// this is asserted, not derived, so a caller passing a raw file offset by mistake fails
        /// loudly instead of writing a pointer that reads back a different sprite.
        /// </summary>
        public static void WritePointer(Rom rom, int imageIndex, int address)
        {
            int fileOffset = address - 0xC00000;
            if (Rom.Mask(address) != fileOffset)
                throw new System.InvalidOperationException(
                    $"pointer address 0x{address:X} does not mask to file offset 0x{fileOffset:X} " +
                    "(expected the 0xC00000 + fileOffset convention).");

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
