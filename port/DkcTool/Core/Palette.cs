using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// Port of the palette read/write logic from ROM.Palette.cs.
    /// SNES colors are 15-bit BGR packed as  xbbbbbgggggrrrrr.
    /// Index 0 is forced transparent (matching the original), then 15 colors
    /// are read, giving the 16 entries a 4bpp char indexes into.
    /// </summary>
    public static class Palette
    {
        public static SKColor[] Read(Rom rom, int address)
        {
            var colors = new SKColor[16];
            colors[0] = SKColors.Transparent;

            for (int i = 1; i < 16; i++)
            {
                int raw = rom.Read16(address);
                address += 2;

                int r = ((raw >> 0) & 0x1f) << 3;
                int g = ((raw >> 5) & 0x1f) << 3;
                int b = ((raw >> 10) & 0x1f) << 3;

                colors[i] = new SKColor((byte)r, (byte)g, (byte)b);
            }
            return colors;
        }

        /// <summary>Pack a color back to SNES 15-bit BGR (for the future write path).</summary>
        public static int ToSnes(SKColor c) =>
            ((c.Red >> 3) << 0) | ((c.Green >> 3) << 5) | ((c.Blue >> 3) << 10);
    }
}
