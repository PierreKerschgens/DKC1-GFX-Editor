using System;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// M2a gate (specs/m2-importer-spec.md Part D, V2): for a test pose, verify
    /// Decode(Serialize(Tiler.Build(pose))) reproduces the same pixel-index grid.
    /// Crucially this decodes through the *real*, production <see cref="SpriteDecoder.Decode"/>
    /// path (the same code that will read a real ROM) rather than re-checking the
    /// tiler against itself -- an identity palette (index i -&gt; a color encoding i)
    /// lets pixel colors be mapped straight back to indices after decode.
    /// </summary>
    public static class TilerHarness
    {
        public sealed class CaseResult
        {
            public string Name = "";
            public bool Passed;
            public string? FailureDetail;
            public int CharCount;
            public int OamEntries;
            public bool ExceedsCharBudget;
            public bool ExceedsOamBudget;
        }

        /// <summary>index i -&gt; a color encoding i (index 0 transparent); lets any decoded
        /// bitmap be read straight back into palette indices via <see cref="ReadIndexPixel"/>.</summary>
        public static readonly SKColor[] IdentityPalette = BuildIdentityPalette();

        private static SKColor[] BuildIdentityPalette()
        {
            var palette = new SKColor[16];
            palette[0] = new SKColor(0, 0, 0, 0); // index 0 = transparent, matches real convention
            for (int i = 1; i < 16; i++)
                palette[i] = new SKColor((byte)i, 0, 0, 255); // red channel carries the index
            return palette;
        }

        public static int ReadIndexPixel(SKBitmap bmp, int x, int y)
        {
            SKColor px = bmp.GetPixel(x, y);
            return px.Alpha == 0 ? 0 : px.Red;
        }

        /// <summary>Decodes a sprite (real ROM or synthetic) straight to a palette-index canvas.</summary>
        public static int[,] DecodeIndexCanvas(Rom rom, int address)
        {
            using SKBitmap bmp = SpriteDecoder.Decode(rom, address, IdentityPalette);
            var canvas = new int[bmp.Height, bmp.Width];
            for (int r = 0; r < bmp.Height; r++)
                for (int c = 0; c < bmp.Width; c++)
                    canvas[r, c] = ReadIndexPixel(bmp, c, r);
            return canvas;
        }

        /// <summary>Runs the tiler on `pixels`, serializes, decodes via the real decoder, and compares.</summary>
        public static CaseResult RunCase(string name, int[,] pixels, int originX = 0, int originY = 0)
        {
            var tilerResult = SpriteTiler.Build(pixels, originX, originY);
            byte[] serialized = SpriteEncoder.Serialize(tilerResult.Model);

            var synthRom = new Rom(serialized);
            using SKBitmap bmp = SpriteDecoder.Decode(synthRom, 0, IdentityPalette);

            int h = pixels.GetLength(0), w = pixels.GetLength(1);
            string? mismatch = null;
            for (int r = 0; r < h && mismatch == null; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    int expected = pixels[r, c];
                    int actual = ReadIndexPixel(bmp, originX + c, originY + r);
                    if (actual != expected)
                    {
                        mismatch = $"pixel ({r},{c}): expected index {expected}, got {actual}";
                        break;
                    }
                }
            }

            return new CaseResult
            {
                Name = name,
                Passed = mismatch == null,
                FailureDetail = mismatch,
                CharCount = tilerResult.CharCount,
                OamEntries = tilerResult.OamEntries,
                ExceedsCharBudget = tilerResult.ExceedsCharBudget,
                ExceedsOamBudget = tilerResult.ExceedsOamBudget,
            };
        }
    }
}
