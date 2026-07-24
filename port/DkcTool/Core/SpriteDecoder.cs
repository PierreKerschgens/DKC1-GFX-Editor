using System.Collections.Generic;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// Port of ROM.ReadFromSpriteHeader + ROM.SetupVRAM (ROM.Sprites.cs).
    ///
    /// The original also builds a `tiles` list of per-entry TL/TR/BL/BR char byte
    /// arrays via a second pair of loops over the header. That list is populated but
    /// never read by the render path (the draw loop indexes the VRAM emulation grid,
    /// not `tiles`), so it's dead code for decoding and is intentionally not ported
    /// here. What's ported is exactly what produces pixels: header parse, 2x2/1x1
    /// placement, VRAM emulation, composite.
    /// </summary>
    public static class SpriteDecoder
    {
        // Render-path placement: one per 8x8 draw call (2x2 entries expand to four of
        // these). Distinct from the top-level `Placement` (entry-level, used by
        // SpriteModel/DecodeStructured) to avoid the nested type shadowing it.
        private readonly struct RenderPlacement
        {
            public readonly int X, Y, VramRow, VramCol;
            public RenderPlacement(int x, int y, int vramRow, int vramCol)
            {
                X = x; Y = y; VramRow = vramRow; VramCol = vramCol;
            }
        }

        public static SKBitmap Decode(Rom rom, int address, SKColor[] palette)
        {
            byte[] data = ReadSpriteBytes(rom, address);

            int p = 0;
            int b0 = data[p++]; // # of 2x2 chars
            int b1 = data[p++]; // # of 1x1 chars, group 1
            int b2 = data[p++]; // position of group 1
            int b3 = data[p++]; // # of 1x1 chars, group 2
            int b4 = data[p++]; // position of group 2
            int b5 = data[p++]; // # of chars in DMA group 1
            int b6 = data[p++]; // VRAM placement of DMA group 2
            int b7 = data[p++]; // # of chars in DMA group 2

            var placements = new List<RenderPlacement>();
            ReadGroup2x2(data, ref p, b0, placements);
            ReadGroup1x1(data, ref p, b1, b2, placements);
            ReadGroup1x1(data, ref p, b3, b4, placements);

            // p now sits at sizeOfNonTiles: 8 header bytes + 2 bytes per placed tile.
            var vram = SetupVram(data, p, b5, b6, b7);

            var bmp = new SKBitmap(256, 256);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            foreach (var pl in placements)
            {
                byte[] charBytes = vram[pl.VramRow][pl.VramCol];
                using var tile = CharDecoder.Decode(charBytes, palette);
                canvas.DrawBitmap(tile, pl.X, pl.Y);
            }
            return bmp;
        }

        /// <summary>Reads the full raw byte range of a sprite (header + placements + chars).</summary>
        public static byte[] ReadSpriteBytes(Rom rom, int address)
        {
            byte[] header = rom.ReadBytes(address, 8);
            int b0 = header[0], b1 = header[1], b3 = header[3], b5 = header[5], b7 = header[7];
            int size = 8 + b0 * 2 + b1 * 2 + b3 * 2 + (b5 << 5) + b7 * 0x20;
            return rom.ReadBytes(address, size);
        }

        /// <summary>
        /// Index-level structured decode for round-tripping (M1). Retains the header,
        /// the placement (x,y) table in file order, and the char data as palette-index
        /// grids in file order -- exactly what <see cref="SpriteEncoder.Serialize"/>
        /// needs to reproduce the original bytes.
        /// </summary>
        public static SpriteModel DecodeStructured(Rom rom, int address)
        {
            byte[] data = ReadSpriteBytes(rom, address);

            var model = new SpriteModel();
            System.Array.Copy(data, 0, model.Header, 0, 8);

            int b0 = data[0], b1 = data[1], b3 = data[3], b5 = data[5], b7 = data[7];
            int p = 8;

            for (int n = 0; n < b0; n++)
            {
                int x = data[p++], y = data[p++];
                model.Placements.Add(new Placement(x, y, TileType.TwoByTwo));
            }
            for (int n = 0; n < b1; n++)
            {
                int x = data[p++], y = data[p++];
                model.Placements.Add(new Placement(x, y, TileType.OneByOne));
            }
            for (int n = 0; n < b3; n++)
            {
                int x = data[p++], y = data[p++];
                model.Placements.Add(new Placement(x, y, TileType.OneByOne));
            }

            int charCount = b5 + b7;
            for (int i = 0; i < charCount; i++)
            {
                byte[] chr = Slice(data, p, 0x20);
                model.Chars.Add(CharCodec.DecodeIndices(chr));
                p += 0x20;
            }

            return model;
        }

        // Places 16x16 "2x2" chars: each entry is one (x,y) pair expanding to four
        // 8x8 placements (TL/TR/BL/BR), walking the VRAM grid two columns at a time.
        private static void ReadGroup2x2(byte[] data, ref int p, int count, List<RenderPlacement> placements)
        {
            int r = 0, c = 0;
            for (int n = 0; n < count; n++)
            {
                int x = data[p++];
                int y = data[p++];
                placements.Add(new RenderPlacement(x + 0, y + 0, r + 0, c + 0));
                placements.Add(new RenderPlacement(x + 8, y + 0, r + 0, c + 1));
                placements.Add(new RenderPlacement(x + 0, y + 8, r + 1, c + 0));
                placements.Add(new RenderPlacement(x + 8, y + 8, r + 1, c + 1));
                c += 2;
                if (c == 16) { r += 2; c = 0; }
            }
        }

        // Places 8x8 "1x1" chars starting at VRAM grid position `start` (row = start>>4,
        // col = start&0xf), walking one column at a time.
        private static void ReadGroup1x1(byte[] data, ref int p, int count, int start, List<RenderPlacement> placements)
        {
            int r = start >> 4, c = start & 0xf;
            for (int n = 0; n < count; n++)
            {
                int x = data[p++];
                int y = data[p++];
                placements.Add(new RenderPlacement(x, y, r, c));
                c++;
                if (c == 16) { r += 1; c = 0; }
            }
        }

        // Emulates the SNES VRAM layout: `endGroup1` chars fill rows of 16 starting
        // at `address`, then (after padding with blank rows up to row `startGroup2>>4`
        // if needed) `countGroup2` chars are appended to whatever's already in that
        // row -- which may be a fresh blank row or one partially filled by group 1.
        private static List<List<byte[]>> SetupVram(byte[] data, int address, int endGroup1, int startGroup2, int countGroup2)
        {
            var rows = new List<List<byte[]>>();
            int currentIndex = 0;
            while (currentIndex < endGroup1)
            {
                var row = new List<byte[]>();
                rows.Add(row);
                for (int j = 0; j < 16 && currentIndex < endGroup1; j++, currentIndex++)
                {
                    row.Add(Slice(data, address, 0x20));
                    address += 0x20;
                }
            }

            while (rows.Count <= (startGroup2 >> 4))
            {
                rows.Add(new List<byte[]>());
                rows.Add(new List<byte[]>());
            }

            for (int i = 0; i < countGroup2; i++)
            {
                rows[startGroup2 >> 4].Add(Slice(data, address, 0x20));
                address += 0x20;
            }

            return rows;
        }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            var result = new byte[count];
            System.Array.Copy(data, offset, result, 0, count);
            return result;
        }
    }
}
