using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// Sheet -> numbered (strip, position) poses (specs/m4-batch-spec.md C.1). This is the
    /// production slicer a manifest addresses poses through -- <see cref="SheetResearch"/> stays
    /// in the tree as the frozen research pass that established the policies encoded here
    /// (Part A), but nothing imports through it.
    ///
    /// Three policies, all measured rather than assumed in Part A:
    /// 1. A component that is entirely pure black (<see cref="AnnotationRgb"/>) is a rendered
    ///    caption, not a pose -- DKC's palette has no pure black, so this is the one thing on the
    ///    sheet that legitimately isn't artwork.
    /// 2. A component under <see cref="MinPosePixels"/> is label dust, not a pose.
    /// 3. Flood-fill 8-connected, merge bboxes within <see cref="MergeGap"/>.
    ///
    /// <b>Stability is a requirement, not a nicety</b>: the manifest addresses poses by the
    /// (strip, position) numbers this produces. A change here silently retargets every existing
    /// manifest. Pin behaviour with a golden slice list (V4a) before touching the segmentation or
    /// grouping logic.
    /// </summary>
    public static class SheetSlicer
    {
        public const int MergeGap = 4;
        public const int MinPosePixels = 64;
        public static readonly uint AnnotationRgb = 0x000000;

        public sealed class SlicedPose
        {
            public int StripIndex;
            public int Position;

            public int MinX, MinY, MaxX, MaxY;
            public int Width => MaxX - MinX + 1;
            public int Height => MaxY - MinY + 1;
            public int OpaquePixels;

            public int CharCount;
            public int OamEntries;
            public bool OverCharBudget;
            public bool OverCanvas;
            public int SerializedBytes;
        }

        public sealed class Strip
        {
            public int Index;

            /// <summary>Which row band this strip was split from -- informational (review/CLI
            /// output), not part of a manifest's addressing.</summary>
            public int Band;
            public List<SlicedPose> Poses = new List<SlicedPose>();
        }

        public sealed class SlicedSheet
        {
            public string Path = "";
            public int Width, Height;
            public List<Strip> Strips = new List<Strip>();
            public IEnumerable<SlicedPose> Poses => Strips.SelectMany(s => s.Poses);
        }

        private sealed class Island
        {
            public int MinX, MinY, MaxX, MaxY;
            public int OpaquePixels;
            public bool HasArtwork;
        }

        public static SlicedSheet Slice(string path)
        {
            using var bitmap = SKBitmap.Decode(path)
                ?? throw new InvalidOperationException($"could not decode '{path}'.");

            int w = bitmap.Width, h = bitmap.Height;
            var opaque = new bool[h, w];
            var artwork = new bool[h, w];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    SKColor px = bitmap.GetPixel(x, y);
                    if (px.Alpha == 0) continue;
                    opaque[y, x] = true;
                    uint key = ((uint)px.Red << 16) | ((uint)px.Green << 8) | px.Blue;
                    if (key != AnnotationRgb) artwork[y, x] = true;
                }
            }

            var islands = Segment(opaque, artwork, w, h);

            var poses = islands
                .Select(i => new SlicedPose
                {
                    MinX = i.MinX,
                    MinY = i.MinY,
                    MaxX = i.MaxX,
                    MaxY = i.MaxY,
                    OpaquePixels = i.OpaquePixels,
                })
                .OrderBy(p => p.MinY).ThenBy(p => p.MinX)
                .ToList();

            foreach (var pose in poses) Measure(pose, opaque);

            var sheet = new SlicedSheet { Path = path, Width = w, Height = h };
            sheet.Strips = GroupIntoStrips(poses);
            return sheet;
        }

        /// <summary>
        /// Flood-fills opaque islands, then merges islands whose bboxes are within
        /// <see cref="MergeGap"/> of each other, dropping caption-only and dust components.
        /// Identical policy and order to the research pass (SheetResearch.Segment) --
        /// determinism here is what V4a pins.
        /// </summary>
        private static List<Island> Segment(bool[,] opaque, bool[,] artwork, int w, int h)
        {
            var seen = new bool[h, w];
            var islands = new List<Island>();
            var stack = new Stack<(int X, int Y)>();

            for (int y0 = 0; y0 < h; y0++)
            {
                for (int x0 = 0; x0 < w; x0++)
                {
                    if (!opaque[y0, x0] || seen[y0, x0]) continue;

                    var island = new Island { MinX = x0, MinY = y0, MaxX = x0, MaxY = y0 };
                    stack.Push((x0, y0));
                    seen[y0, x0] = true;

                    while (stack.Count > 0)
                    {
                        var (x, y) = stack.Pop();
                        island.OpaquePixels++;
                        if (artwork[y, x]) island.HasArtwork = true;
                        if (x < island.MinX) island.MinX = x;
                        if (x > island.MaxX) island.MaxX = x;
                        if (y < island.MinY) island.MinY = y;
                        if (y > island.MaxY) island.MaxY = y;

                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                if (!opaque[ny, nx] || seen[ny, nx]) continue;
                                seen[ny, nx] = true;
                                stack.Push((nx, ny));
                            }
                    }
                    islands.Add(island);
                }
            }

            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < islands.Count && !merged; i++)
                {
                    for (int j = i + 1; j < islands.Count; j++)
                    {
                        if (!Near(islands[i], islands[j])) continue;
                        islands[i].MinX = Math.Min(islands[i].MinX, islands[j].MinX);
                        islands[i].MinY = Math.Min(islands[i].MinY, islands[j].MinY);
                        islands[i].MaxX = Math.Max(islands[i].MaxX, islands[j].MaxX);
                        islands[i].MaxY = Math.Max(islands[i].MaxY, islands[j].MaxY);
                        islands[i].OpaquePixels += islands[j].OpaquePixels;
                        islands[i].HasArtwork |= islands[j].HasArtwork;
                        islands.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }

            return islands.Where(p => p.HasArtwork && p.OpaquePixels >= MinPosePixels).ToList();
        }

        private static bool Near(Island a, Island b) =>
            a.MinX - MergeGap <= b.MaxX && b.MinX - MergeGap <= a.MaxX &&
            a.MinY - MergeGap <= b.MaxY && b.MinY - MergeGap <= a.MaxY;

        /// <summary>Runs a pose's occupancy mask through the real tiler. Colours don't affect the
        /// char/OAM budget -- only which 8x8 cells are occupied -- so every opaque pixel is set to
        /// index 1. That is what lets slicing measure cost before the palette question is settled
        /// (PoseLoader.Load, called later per-pose, is what actually maps colours).</summary>
        private static void Measure(SlicedPose pose, bool[,] opaque)
        {
            pose.OverCanvas = pose.Width > PoseLoader.MaxCanvas || pose.Height > PoseLoader.MaxCanvas;
            if (pose.OverCanvas) return;

            var grid = new int[pose.Height, pose.Width];
            for (int r = 0; r < pose.Height; r++)
                for (int c = 0; c < pose.Width; c++)
                    if (opaque[pose.MinY + r, pose.MinX + c]) grid[r, c] = 1;

            try
            {
                var tiled = SpriteTiler.Build(grid, 0, 0);
                pose.CharCount = tiled.CharCount;
                pose.OamEntries = tiled.OamEntries;
                pose.OverCharBudget = tiled.ExceedsCharBudget;
                if (!tiled.ExceedsCharBudget)
                    pose.SerializedBytes = SpriteEncoder.Serialize(tiled.Model).Length;
            }
            catch
            {
                pose.OverCharBudget = true;
            }
        }

        /// <summary>
        /// Poses whose vertical extents overlap form a row band (A.4); a band is a row, not an
        /// animation -- the sheets put several captioned animations side by side on one line. Each
        /// band is split again at horizontal gaps well above the typical inter-pose spacing to
        /// recover the actual strips, which is the manifest's real unit.
        /// </summary>
        private static List<Strip> GroupIntoStrips(List<SlicedPose> posesByMinY)
        {
            // Bands: a single sweep over poses ordered by MinY (already the input order).
            // A pose either extends the current band (its top lies at or above the band's
            // current bottom) or starts a new one. Because bands only ever grow forward through
            // this ordering, a later pose can never vertically reach back into an earlier,
            // already-closed band -- so band membership is unambiguous, one pose to one band.
            var bands = new List<List<SlicedPose>>();
            int currentBottom = 0;

            foreach (var pose in posesByMinY)
            {
                if (bands.Count > 0 && pose.MinY <= currentBottom)
                {
                    bands[^1].Add(pose);
                    currentBottom = Math.Max(currentBottom, pose.MaxY);
                }
                else
                {
                    bands.Add(new List<SlicedPose> { pose });
                    currentBottom = pose.MaxY;
                }
            }

            var strips = new List<Strip>();
            for (int bandIndex = 0; bandIndex < bands.Count; bandIndex++)
            {
                var row = bands[bandIndex].OrderBy(p => p.MinX).ToList();

                var gaps = new List<int>();
                for (int i = 1; i < row.Count; i++)
                    gaps.Add(Math.Max(0, row[i].MinX - row[i - 1].MaxX));
                int typical = gaps.Count == 0 ? 0 : Percentile(gaps, .5);
                int split = Math.Max(typical * 3, typical + 8);

                var current = new List<SlicedPose>();
                void Flush()
                {
                    if (current.Count == 0) return;
                    var strip = new Strip { Index = strips.Count, Band = bandIndex };
                    for (int i = 0; i < current.Count; i++)
                    {
                        current[i].StripIndex = strip.Index;
                        current[i].Position = i;
                    }
                    strip.Poses = current;
                    strips.Add(strip);
                    current = new List<SlicedPose>();
                }

                for (int i = 0; i < row.Count; i++)
                {
                    if (i > 0 && row[i].MinX - row[i - 1].MaxX > split) Flush();
                    current.Add(row[i]);
                }
                Flush();
            }

            return strips;
        }

        private static int Percentile(IEnumerable<int> xs, double p)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(s.Count * p))];
        }

        /// <summary>Writes a review overlay: the sheet with every pose boxed and labelled
        /// "strip:position" (specs/m4-batch-spec.md C.4's QA overlay, used here at slice time
        /// rather than import time). The only way to tell a correct numbering from a plausible
        /// one is to look at it.</summary>
        public static void DumpOverlay(string path, SlicedSheet sheet, string outPath)
        {
            using var src = SKBitmap.Decode(path);
            using var surface = new SKBitmap(src.Width, src.Height);
            using (var canvas = new SKCanvas(surface))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(src, 0, 0);
                using var box = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1,
                    Color = new SKColor(0, 255, 0, 200),
                };
                using var text = new SKPaint
                {
                    Color = new SKColor(255, 255, 0, 220),
                    TextSize = 10,
                    IsAntialias = true,
                };
                foreach (var strip in sheet.Strips)
                {
                    foreach (var pose in strip.Poses)
                    {
                        canvas.DrawRect(pose.MinX - 0.5f, pose.MinY - 0.5f, pose.Width, pose.Height, box);
                        canvas.DrawText($"{strip.Index}:{pose.Position}", pose.MinX, Math.Max(9, pose.MinY - 2), text);
                    }
                }
            }
            using var img = SKImage.FromBitmap(surface);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.OpenWrite(outPath);
            data.SaveTo(fs);
        }

        /// <summary>
        /// Crops the caption sitting above each strip and stacks them into one montage, upscaled,
        /// each row labelled with its strip number and pose count.
        ///
        /// A.2 established that the captions are *artwork*, not metadata — pure-black pixels the
        /// slicer deliberately excludes — so nothing can parse them. A.9 then established that they
        /// are the only reliable way to identify a strip, since frame count actively misleads. That
        /// makes "render every caption at a readable size, in strip order" the survey's core tool:
        /// a human reads 46 names off two images instead of cropping 46 regions by hand.
        ///
        /// The caption is assumed to sit immediately above the strip's leftmost pose, which is how
        /// both sheets are laid out. A strip whose row comes out blank simply has no caption there.
        /// </summary>
        public static void WriteCaptionSheet(string sheetPath, string outPath, int fromStrip, int toStrip,
                                             int scale = 3, int captionWidth = 150, int captionHeight = 20)
        {
            var sheet = Slice(sheetPath);
            using var src = SKBitmap.Decode(sheetPath);

            var strips = sheet.Strips.Where(s => s.Index >= fromStrip && s.Index <= toStrip)
                                     .OrderBy(s => s.Index).ToList();
            const int labelW = 108, pad = 6;
            int rowH = captionHeight * scale + pad;

            using var montage = new SKBitmap(labelW + captionWidth * scale, Math.Max(1, strips.Count * rowH));
            using (var canvas = new SKCanvas(montage))
            {
                canvas.Clear(new SKColor(255, 255, 255));
                using var text = new SKPaint { Color = SKColors.Black, TextSize = 15, IsAntialias = true };

                for (int i = 0; i < strips.Count; i++)
                {
                    var strip = strips[i];
                    int y = i * rowH;
                    canvas.DrawText($"strip {strip.Index} ({strip.Poses.Count}p)", 4, y + rowH / 2 + 5, text);

                    // Anchor on the *leftmost* pose, not the strip's overall bounding box. A strip
                    // containing one unusually tall pose (or a mis-merge) has a MinY far above its
                    // left edge, which puts the caption window above the text entirely -- exactly
                    // what hid strip 10's "Jump" behind the previous band's sprites.
                    var anchor = strip.Poses.OrderBy(p => p.MinX).First();
                    int x0 = anchor.MinX;
                    int y0 = anchor.MinY;
                    int cropY = Math.Max(0, y0 - captionHeight);
                    int cropH = Math.Min(captionHeight, src.Height - cropY);
                    int cropW = Math.Min(captionWidth, src.Width - x0);
                    if (cropW <= 0 || cropH <= 0) continue;

                    var srcRect = new SKRectI(x0, cropY, x0 + cropW, cropY + cropH);
                    var dstRect = SKRect.Create(labelW, y, cropW * scale, cropH * scale);
                    canvas.DrawBitmap(src, srcRect, dstRect);

                    // Box each crop. Without it a caption drawn near a row boundary reads as
                    // belonging to either neighbour, which is exactly the ambiguity this montage
                    // exists to remove.
                    using var border = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1,
                        Color = new SKColor(200, 0, 0, 160),
                    };
                    canvas.DrawRect(dstRect, border);
                }
            }

            using var img = SKImage.FromBitmap(montage);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.OpenWrite(outPath);
            data.SaveTo(fs);
        }

        public static int Run(string path, string? overlayPath)
        {
            var sheet = Slice(path);
            int poseCount = sheet.Poses.Count();
            int overCanvas = sheet.Poses.Count(p => p.OverCanvas);
            int overBudget = sheet.Poses.Count(p => !p.OverCanvas && p.OverCharBudget);

            Console.WriteLine($"=== slice: {System.IO.Path.GetFileName(path)} ===");
            Console.WriteLine($"  {sheet.Width}x{sheet.Height}");
            Console.WriteLine($"  strips: {sheet.Strips.Count}, poses: {poseCount} " +
                              $"(merge gap {MergeGap}px, min {MinPosePixels}px)");
            Console.WriteLine($"  over 256x256 canvas   : {overCanvas}");
            Console.WriteLine($"  over {SpriteTiler.MaxChars}-char budget    : {overBudget}");
            Console.WriteLine();

            foreach (var strip in sheet.Strips)
            {
                var parts = strip.Poses.Select(p =>
                    $"{p.Position}:{p.Width}x{p.Height}@({p.MinX},{p.MinY}) {p.CharCount}ch" +
                    (p.OverCanvas ? " [OVER-CANVAS]" : p.OverCharBudget ? " [OVER-BUDGET]" : ""));
                Console.WriteLine($"  strip {strip.Index,3} (band {strip.Band,3}, {strip.Poses.Count,2} poses): " +
                                  string.Join(", ", parts));
            }

            if (overlayPath != null)
            {
                DumpOverlay(path, sheet, overlayPath);
                Console.WriteLine();
                Console.WriteLine($"overlay -> {overlayPath}");
            }

            return 0;
        }
    }
}
