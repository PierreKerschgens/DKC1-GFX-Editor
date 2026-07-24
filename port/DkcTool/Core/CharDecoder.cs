using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// Port of ROM.DecodeChar (ROM.Sprites.cs). Decodes one SNES 4bpp 8x8 char
    /// (0x20 bytes, planar/bitplane layout) into an 8x8 bitmap.
    ///
    /// This is the atomic unit of the whole graphics pipeline. If a decoded tile
    /// here matches the Windows tool's GFX view pixel-for-pixel, the System.Drawing
    /// -> SkiaSharp swap is proven for the hard part.
    /// </summary>
    public static class CharDecoder
    {
        public static SKBitmap Decode(byte[] chr, SKColor[] palette, int bpp = 4)
        {
            var bmp = new SKBitmap(8, 8);

            for (int i = 0, index = 0; i < 8; i++)      // rows
            {
                for (int j = 0; j < 8; j++)             // columns
                {
                    int colorIndex = 0;
                    for (int k = 0; k < bpp / 2; k++)   // bitplane pairs
                    {
                        int x = ((chr[index + k * 16] >> (7 - j)) & 1) << (k * 2);
                        int y = ((chr[index + 1 + k * 16] >> (7 - j)) & 1) << (k * 2 + 1);
                        colorIndex |= x | y;
                    }
                    bmp.SetPixel(j, i, palette[colorIndex]);
                }
                index += 2;
            }
            return bmp;
        }
    }
}
