using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// Target-slot introspection for an import (specs/m2b-writer-spec.md C.3). Reads
    /// only -- never mutates the ROM. The bbox comes from the placement table (the
    /// game's own tile coordinates), not the decoded pixel bbox: those disagree for
    /// 1,437 / 2,714 sprites (--stats-m2b), and the placement bbox is what the Q4
    /// origin rule (m2-importer-spec.md) means by "the replaced slot's bbox".
    /// </summary>
    public sealed class SpriteSlot
    {
        public int ImageIndex;
        public int PointerAddress;
        public int SpriteAddress;
        public int FileOffset;
        public int Size;
        public int PlacementMinX;
        public int PlacementMinY;
        public int PlacementMaxX;
        public int PlacementMaxY;

        /// <summary>Other image indices whose pointer resolves to the same sprite address (report only).</summary>
        public List<int> AliasIndices = new List<int>();

        public static SpriteSlot Read(Rom rom, int imageIndex)
        {
            int spriteAddress = GfxTable.ResolveSpriteAddress(rom, imageIndex);
            byte[] data = SpriteDecoder.ReadSpriteBytes(rom, spriteAddress);

            int b0 = data[0], b1 = data[1], b3 = data[3];
            int p = 8;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            void Accumulate(int count, int extent)
            {
                for (int k = 0; k < count; k++)
                {
                    int x = data[p++], y = data[p++];
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x + extent);
                    maxY = Math.Max(maxY, y + extent);
                }
            }

            Accumulate(b0, 16); // 2x2 entries
            Accumulate(b1, 8);  // 1x1 group 1
            Accumulate(b3, 8);  // 1x1 group 2

            var aliases = GfxTable.EnumerateImageIndices(rom)
                .Where(i => i != imageIndex && GfxTable.ResolveSpriteAddress(rom, i) == spriteAddress)
                .ToList();

            return new SpriteSlot
            {
                ImageIndex = imageIndex,
                PointerAddress = GfxTable.BaseAddress + imageIndex,
                SpriteAddress = spriteAddress,
                FileOffset = Rom.Mask(spriteAddress),
                Size = data.Length,
                PlacementMinX = minX == int.MaxValue ? 0 : minX,
                PlacementMinY = minY == int.MaxValue ? 0 : minY,
                PlacementMaxX = maxX == int.MinValue ? 0 : maxX,
                PlacementMaxY = maxY == int.MinValue ? 0 : maxY,
                AliasIndices = aliases,
            };
        }
    }
}
