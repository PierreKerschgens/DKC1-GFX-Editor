using System;
using System.IO;
using SkiaSharp;

namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// Turns a <see cref="LibretroCore"/> framebuffer into a PNG / comparable bitmap
    /// (specs/v3-emulator-spec.md). The framebuffer is already BGRA8888, so this is a wrap
    /// rather than a conversion.
    /// </summary>
    public static class FrameCapture
    {
        public static SKBitmap ToBitmap(LibretroCore core)
        {
            if (core.FrameBuffer == null)
                throw new InvalidOperationException(
                    "no frame captured yet -- run at least one frame that isn't duped.");

            var bmp = new SKBitmap(new SKImageInfo(core.FrameWidth, core.FrameHeight,
                                                   SKColorType.Bgra8888, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(
                core.FrameBuffer, 0, bmp.GetPixels(), core.FrameBuffer.Length);
            return bmp;
        }

        public static void Save(LibretroCore core, string path)
        {
            using var bmp = ToBitmap(core);
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }

        /// <summary>Fraction of pixels that differ between two frames, plus the bounding box of the
        /// difference. The bbox is what makes a diff actionable: "3% of pixels changed" says
        /// nothing, "3% changed, all inside the sprite's screen rect" is the V3 assertion.</summary>
        public static DiffResult Compare(SKBitmap a, SKBitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height)
                throw new ArgumentException($"frame size mismatch: {a.Width}x{a.Height} vs {b.Width}x{b.Height}");

            var result = new DiffResult { Width = a.Width, Height = a.Height, MinX = a.Width, MinY = a.Height };

            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    if (a.GetPixel(x, y) == b.GetPixel(x, y)) continue;
                    result.DifferingPixels++;
                    if (x < result.MinX) result.MinX = x;
                    if (x > result.MaxX) result.MaxX = x;
                    if (y < result.MinY) result.MinY = y;
                    if (y > result.MaxY) result.MaxY = y;
                }
            }

            return result;
        }

        public sealed class DiffResult
        {
            public int Width, Height;
            public int DifferingPixels;
            public int MinX, MinY;
            public int MaxX = -1, MaxY = -1;

            public bool Identical => DifferingPixels == 0;
            public double Fraction => Width * Height == 0 ? 0 : (double)DifferingPixels / (Width * Height);

            public override string ToString() => Identical
                ? "identical"
                : $"{DifferingPixels} px ({Fraction:P2}) differ, bbox ({MinX},{MinY})-({MaxX},{MaxY})";
        }
    }
}
