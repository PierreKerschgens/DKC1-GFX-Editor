using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>One distinct opaque RGB value that could not be represented as-is, with
    /// enough detail to make the refusal actionable (specs/m2b-writer-spec.md C.2 rule 5).</summary>
    public sealed class UnmappedColor
    {
        public SKColor Color;
        public int Count;
        public int FirstX, FirstY;
    }

    public sealed class PoseResult
    {
        /// <summary>Palette-index grid, cropped to the opaque bbox (0 = transparent).</summary>
        public int[,] Pixels = new int[0, 0];

        /// <summary>Bbox top-left within the source PNG.</summary>
        public int BboxX, BboxY;
        public int Width, Height;

        /// <summary>Opaque pixels that exactly matched palette[0]'s RGB -- forced to index 0
        /// because the forced-transparent slot cannot represent an opaque color (C.2 rule 4).
        /// A warning, not a refusal.</summary>
        public List<UnmappedColor> Unmapped = new List<UnmappedColor>();
    }

    /// <summary>
    /// PNG -> palette-index grid (FR3, specs/m2b-writer-spec.md C.2). M2 Q1 locked this to
    /// pre-indexed 16-color input -- no nearest-color RGB matching. An unmatched opaque
    /// color is the single most likely user error, so refusal must name the colors, not
    /// just say "import failed".
    /// </summary>
    public static class PoseLoader
    {
        public const int MaxCanvas = 256;

        public static PoseResult Load(string path, SKColor[] palette)
        {
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap == null)
                throw new InvalidOperationException($"could not decode '{path}' as an image.");

            return LoadRegion(bitmap, 0, 0, bitmap.Width, bitmap.Height, palette);
        }

        /// <summary>
        /// Same load, palette-map and crop as <see cref="Load"/>, over an already-decoded bitmap
        /// instead of a path (specs/m4-batch-spec.md C.3). The batch importer needs this: with
        /// hundreds of poses cut from one sheet, re-decoding and re-encoding a temp PNG per pose
        /// would be pure overhead, and the two paths must refuse identically (same
        /// <see cref="ImportErrorCode.UnmappedColor"/> behaviour) since that is the error an
        /// operator will actually hit. <see cref="Load"/> is now the single-file wrapper over
        /// this; M2b's <c>--import</c> path is unchanged.
        /// </summary>
        public static PoseResult LoadRegion(SKBitmap bitmap, int regionX, int regionY, int width, int height, SKColor[] palette)
        {
            if (width > MaxCanvas || height > MaxCanvas)
                throw new ImportException(ImportErrorCode.PoseTooLarge,
                    $"pose is {width}x{height}, exceeds the {MaxCanvas}x{MaxCanvas} canvas limit.");

            var indices = new int[height, width];
            var forcedTransparent = new Dictionary<uint, UnmappedColor>();
            var unmatched = new Dictionary<uint, UnmappedColor>();
            SKColor colorZero = palette[0];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    SKColor px = bitmap.GetPixel(regionX + x, regionY + y);
                    if (px.Alpha == 0)
                    {
                        indices[y, x] = 0;
                        continue;
                    }

                    int matched = -1;
                    for (int i = 1; i < palette.Length; i++)
                    {
                        if (SameRgb(palette[i], px)) { matched = i; break; }
                    }
                    if (matched >= 0)
                    {
                        indices[y, x] = matched;
                        continue;
                    }

                    if (SameRgb(colorZero, px))
                    {
                        indices[y, x] = 0;
                        Record(forcedTransparent, px, x, y);
                        continue;
                    }

                    Record(unmatched, px, x, y);
                }
            }

            if (unmatched.Count > 0)
            {
                string detail = string.Join("; ", unmatched.Values
                    .OrderByDescending(u => u.Count)
                    .Select(u => $"RGB({u.Color.Red},{u.Color.Green},{u.Color.Blue}) x{u.Count}, first at ({u.FirstX},{u.FirstY})"));
                throw new ImportException(ImportErrorCode.UnmappedColor,
                    $"{unmatched.Count} opaque color(s) not in the target palette: {detail}");
            }

            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (indices[y, x] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0)
            {
                return new PoseResult
                {
                    Pixels = new int[0, 0],
                    BboxX = 0,
                    BboxY = 0,
                    Width = 0,
                    Height = 0,
                    Unmapped = forcedTransparent.Values.ToList(),
                };
            }

            int cropW = maxX - minX + 1, cropH = maxY - minY + 1;
            var cropped = new int[cropH, cropW];
            for (int r = 0; r < cropH; r++)
                for (int c = 0; c < cropW; c++)
                    cropped[r, c] = indices[minY + r, minX + c];

            return new PoseResult
            {
                Pixels = cropped,
                BboxX = minX,
                BboxY = minY,
                Width = cropW,
                Height = cropH,
                Unmapped = forcedTransparent.Values.ToList(),
            };
        }

        private static bool SameRgb(SKColor a, SKColor b) =>
            a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

        private static void Record(Dictionary<uint, UnmappedColor> dict, SKColor color, int x, int y)
        {
            uint key = ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
            if (!dict.TryGetValue(key, out var entry))
            {
                entry = new UnmappedColor { Color = color, Count = 0, FirstX = x, FirstY = y };
                dict[key] = entry;
            }
            entry.Count++;
        }
    }
}
