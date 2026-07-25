using System;
using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// Target-slot introspection for an import (specs/m2b-writer-spec.md C.3). Reads
    /// only -- never mutates the ROM.
    ///
    /// A slot has **two** bounding boxes and they are different quantities. The
    /// placement bbox comes from the placement table (the game's own tile coordinates)
    /// and is 8px-grid aligned; the opaque bbox is the extent of the actually-drawn
    /// pixels. They disagree for 1,437 / 2,714 sprites (--stats-m2b) -- measured on the
    /// first 60 DK indices, 36/60 differ, by up to 7px in Y.
    ///
    /// Which one to use depends on what it is being compared against, and conflating
    /// them is what kept strip alignment from becoming the default for a whole
    /// milestone (spec A.17, resolved in A.19). `--coords <lo>..<hi>` prints both:
    ///
    /// * The Q4 origin rule (m2-importer-spec.md) means the **placement** bbox -- it is
    ///   the coordinate system the ROM's own bytes are written in.
    /// * Anything compared against a *sheet* means the **opaque** bbox, because that is
    ///   what <see cref="SheetSlicer"/> measures. A sheet-derived offset can never
    ///   reproduce a placement-derived one, since the grid slack varies per sprite.
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

        /// <summary>
        /// Bounds of the slot's non-transparent pixels, in the same canvas coordinates as
        /// <see cref="PlacementMinX"/> and co. This is the sheet-comparable box: it measures
        /// the same thing <see cref="SheetSlicer"/> measures on a pose. Falls back to the
        /// placement bbox for a fully transparent sprite.
        /// </summary>
        public int OpaqueMinX;
        public int OpaqueMinY;
        public int OpaqueMaxX;
        public int OpaqueMaxY;

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

            var slot = new SpriteSlot
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

            if (!TryReadOpaqueBounds(rom, spriteAddress,
                    out slot.OpaqueMinX, out slot.OpaqueMinY, out slot.OpaqueMaxX, out slot.OpaqueMaxY))
            {
                slot.OpaqueMinX = slot.PlacementMinX;
                slot.OpaqueMinY = slot.PlacementMinY;
                slot.OpaqueMaxX = slot.PlacementMaxX;
                slot.OpaqueMaxY = slot.PlacementMaxY;
            }

            return slot;
        }

        /// <summary>
        /// Decodes the sprite and measures its non-transparent extent. False when the sprite
        /// draws nothing (or fails to decode), in which case there is no opaque box to report
        /// and the caller should fall back to the placement one.
        /// </summary>
        public static bool TryReadOpaqueBounds(Rom rom, int spriteAddress,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = minY = maxX = maxY = 0;
            int[,] canvas;
            try { canvas = TilerHarness.DecodeIndexCanvas(rom, spriteAddress); }
            catch { return false; }

            int height = canvas.GetLength(0), width = canvas.GetLength(1);
            int loX = width, loY = height, hiX = -1, hiY = -1;
            for (int r = 0; r < height; r++)
            {
                for (int c = 0; c < width; c++)
                {
                    if (canvas[r, c] == 0) continue;
                    if (c < loX) loX = c;
                    if (c > hiX) hiX = c;
                    if (r < loY) loY = r;
                    if (r > hiY) hiY = r;
                }
            }

            if (hiX < 0) return false;
            minX = loX; minY = loY; maxX = hiX; maxY = hiY;
            return true;
        }
    }
}
