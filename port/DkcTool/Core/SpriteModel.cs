using System.Collections.Generic;

namespace DkcTool.Core
{
    public enum TileType
    {
        TwoByTwo,
        OneByOne,
    }

    public readonly struct Placement
    {
        public readonly int X, Y;
        public readonly TileType Type;

        public Placement(int x, int y, TileType type)
        {
            X = x; Y = y; Type = type;
        }
    }

    /// <summary>
    /// Structured, index-level capture of a decoded sprite: enough to re-serialize
    /// byte-identically via <see cref="SpriteEncoder"/>, without ever going through
    /// RGB. Placements are in file order: 2x2 entries, then 1x1 group 1, then 1x1
    /// group 2 (mirrors header fields b0/b1/b3). Chars are in file order (b5+b7
    /// contiguous chars), as 8x8 palette-index grids.
    /// </summary>
    public sealed class SpriteModel
    {
        public byte[] Header = new byte[8];
        public List<Placement> Placements = new List<Placement>();
        public List<int[,]> Chars = new List<int[,]>();
    }
}
