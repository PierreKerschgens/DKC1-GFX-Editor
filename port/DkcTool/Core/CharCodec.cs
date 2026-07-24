namespace DkcTool.Core
{
    /// <summary>
    /// Index-level (lossless) inverse pair for one SNES 4bpp 8x8 char (0x20 bytes).
    /// Unlike <see cref="CharDecoder"/> (which resolves through a palette to SKColor
    /// for display), this stays at the 0..15 palette-index level so it can be
    /// round-tripped exactly -- palettes can contain duplicate colors, making
    /// color-&gt;index ambiguous, but index-&gt;bytes-&gt;index is not.
    /// </summary>
    public static class CharCodec
    {
        public static int[,] DecodeIndices(byte[] chr)
        {
            var px = new int[8, 8];
            int index = 0;
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    int colorIndex = 0;
                    for (int k = 0; k < 2; k++)
                    {
                        int x = ((chr[index + k * 16] >> (7 - j)) & 1) << (k * 2);
                        int y = ((chr[index + 1 + k * 16] >> (7 - j)) & 1) << (k * 2 + 1);
                        colorIndex |= x | y;
                    }
                    px[i, j] = colorIndex;
                }
                index += 2;
            }
            return px;
        }

        public static byte[] EncodeIndices(int[,] px)
        {
            var chr = new byte[0x20];
            int index = 0;
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    int colorIndex = px[i, j];
                    int bit = 7 - j;
                    for (int k = 0; k < 2; k++)
                    {
                        int p0 = (colorIndex >> (k * 2)) & 1;
                        int p1 = (colorIndex >> (k * 2 + 1)) & 1;
                        if (p0 != 0) chr[index + k * 16] |= (byte)(1 << bit);
                        if (p1 != 0) chr[index + 1 + k * 16] |= (byte)(1 << bit);
                    }
                }
                index += 2;
            }
            return chr;
        }
    }
}
