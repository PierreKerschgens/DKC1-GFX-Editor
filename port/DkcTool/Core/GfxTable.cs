using System.Collections.Generic;

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
    }
}
