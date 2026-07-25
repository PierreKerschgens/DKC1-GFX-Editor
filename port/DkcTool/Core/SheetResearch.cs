using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// M4 research pass (specs/m4-batch-spec.md Part A) -- measuring the sprite sheets the tool
    /// is actually meant to import, before designing the manifest format around guesses.
    ///
    /// The PRD's §6 says input images are "pre-sliced single poses, already reduced to the target
    /// 16-color palette with a transparent background", and defers sheet slicing to M5. The
    /// material in port/sprites/ is neither pre-sliced nor palette-reduced, so that assumption is
    /// the first thing to check rather than to build on.
    ///
    /// Everything here is measured from the alpha mask and the colour histogram. Char counts come
    /// from the real <see cref="SpriteTiler"/>: its budget depends only on which 8x8 cells are
    /// occupied, not on which colours they hold, so a pose's cost can be measured *before* the
    /// palette question is settled.
    /// </summary>
    public static class SheetResearch
    {
        /// <summary>Islands closer than this (in either axis) are treated as one pose. Sprite
        /// sheets separate poses by a visible gutter but leave limbs and props detached within a
        /// pose; the merge distance is what decides which of those two a gap is.</summary>
        public const int MergeGap = 4;

        /// <summary>Components smaller than this are label text / dust, not poses.</summary>
        public const int MinPosePixels = 64;

        /// <summary>
        /// The sheets annotate their animation rows with rendered text ("Idle", "Walking", …) in
        /// pure black. Black is not in the DKC palette -- its darkest entry is #181000 -- so the
        /// labels are the *only* thing on these sheets that fails the palette check, and they are
        /// not poses at all. Segmenting without excluding them counts every caption as a sprite
        /// and reports the sheet as unimportable for a colour no pose uses.
        /// </summary>
        public static readonly uint AnnotationRgb = 0x000000;

        public sealed class Pose
        {
            public int MinX, MinY, MaxX, MaxY;
            public int OpaquePixels;

            /// <summary>False when every pixel in the component is <see cref="AnnotationRgb"/> --
            /// i.e. it is a caption, not artwork.</summary>
            public bool HasArtwork;
            public int Width => MaxX - MinX + 1;
            public int Height => MaxY - MinY + 1;

            public int CharCount;        // via SpriteTiler, from the occupancy mask
            public int OamEntries;
            public bool OverCharBudget;
            public bool OverCanvas;      // exceeds PoseLoader's 256x256 limit
            public int SerializedBytes;

            /// <summary>Content hash of the cropped pixels. The source video says the author
            /// matched the original's frame counts, and that the original reuses frames -- played
            /// in reverse, looped. So the sheet is expected to hold repeats, and repeats do not
            /// each need their own allocation: several image indices can point at one sprite,
            /// which is exactly the aliasing M2b measured in the stock ROM (11 addresses shared
            /// by 26 indices). Deduplication is therefore a capacity lever, not a micro-optimisation.</summary>
            public string ContentHash = "";
        }

        public sealed class SheetStats
        {
            public string Path = "";
            public int Width, Height;
            public long Transparent, Opaque, PartialAlpha;
            public Dictionary<uint, int> ColorCounts = new Dictionary<uint, int>();
            public List<Pose> Poses = new List<Pose>();
        }

        public static SheetStats Analyse(string path, SKColor[] palette)
        {
            using var bitmap = SKBitmap.Decode(path)
                ?? throw new InvalidOperationException($"could not decode '{path}'.");

            var stats = new SheetStats { Path = path, Width = bitmap.Width, Height = bitmap.Height };
            int w = bitmap.Width, h = bitmap.Height;
            var opaque = new bool[h, w];
            var artwork = new bool[h, w];   // opaque and not annotation black

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    SKColor px = bitmap.GetPixel(x, y);
                    if (px.Alpha == 0) { stats.Transparent++; continue; }
                    if (px.Alpha != 255) stats.PartialAlpha++;

                    stats.Opaque++;
                    opaque[y, x] = true;
                    uint key = ((uint)px.Red << 16) | ((uint)px.Green << 8) | px.Blue;
                    if (key != AnnotationRgb) artwork[y, x] = true;
                    stats.ColorCounts.TryGetValue(key, out int c);
                    stats.ColorCounts[key] = c + 1;
                }
            }

            stats.Poses = Segment(opaque, artwork, w, h);
            foreach (var pose in stats.Poses)
            {
                Measure(pose, opaque);
                pose.ContentHash = HashPose(bitmap, pose);
            }
            return stats;
        }

        /// <summary>
        /// Flood-fills opaque islands, then merges islands whose bboxes are within
        /// <see cref="MergeGap"/> of each other. Deliberately simple: the point is to measure how
        /// many poses a sheet holds and how big they are, not to produce final cuts. Whether this
        /// is good enough to *slice* with is one of the questions the spec has to answer.
        /// </summary>
        private static List<Pose> Segment(bool[,] opaque, bool[,] artwork, int w, int h)
        {
            var seen = new bool[h, w];
            var islands = new List<Pose>();
            var stack = new Stack<(int X, int Y)>();

            for (int y0 = 0; y0 < h; y0++)
            {
                for (int x0 = 0; x0 < w; x0++)
                {
                    if (!opaque[y0, x0] || seen[y0, x0]) continue;

                    var island = new Pose { MinX = x0, MinY = y0, MaxX = x0, MaxY = y0 };
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

            // Merge neighbours until nothing moves.
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

            return islands.Where(p => p.HasArtwork && p.OpaquePixels >= MinPosePixels)
                          .OrderBy(p => p.MinY).ThenBy(p => p.MinX).ToList();
        }

        private static bool Near(Pose a, Pose b) =>
            a.MinX - MergeGap <= b.MaxX && b.MinX - MergeGap <= a.MaxX &&
            a.MinY - MergeGap <= b.MaxY && b.MinY - MergeGap <= a.MaxY;

        /// <summary>
        /// Runs a pose's occupancy mask through the real tiler. Colours are irrelevant to the
        /// char/OAM budget -- only which 8x8 cells are occupied -- so every opaque pixel is set to
        /// index 1. That makes the cost measurable without first solving the palette problem.
        /// </summary>
        private static void Measure(Pose pose, bool[,] opaque)
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

        /// <summary>Hashes a pose's cropped RGBA content, so pixel-identical repeats collapse.</summary>
        private static string HashPose(SKBitmap bitmap, Pose pose)
        {
            var buffer = new byte[pose.Width * pose.Height * 4];
            int i = 0;
            for (int y = pose.MinY; y <= pose.MaxY; y++)
                for (int x = pose.MinX; x <= pose.MaxX; x++)
                {
                    SKColor px = bitmap.GetPixel(x, y);
                    buffer[i++] = px.Red; buffer[i++] = px.Green;
                    buffer[i++] = px.Blue; buffer[i++] = px.Alpha;
                }
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(buffer));
        }

        private static int Pct(IEnumerable<int> xs, double p)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(s.Count * p))];
        }

        /// <summary>Writes a segmentation overlay: the sheet with every detected pose boxed. The
        /// only way to tell a good cut from a plausible-looking number is to look at it.</summary>
        public static void DumpOverlay(string path, SheetStats stats, string outPath)
        {
            using var src = SKBitmap.Decode(path);
            using var surface = new SKBitmap(src.Width, src.Height);
            using (var canvas = new SKCanvas(surface))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(src, 0, 0);
                using var paint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1,
                    Color = new SKColor(0, 255, 0, 200),
                };
                foreach (var p in stats.Poses)
                    canvas.DrawRect(p.MinX - 0.5f, p.MinY - 0.5f, p.Width, p.Height, paint);
            }
            using var img = SKImage.FromBitmap(surface);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.OpenWrite(outPath);
            data.SaveTo(fs);
        }

        public static int Run(string[] paths, SKColor[] palette, string paletteName,
                              string? overlayDir = null)
        {
            Console.WriteLine("=== M4 sheet research ===");
            Console.WriteLine($"target palette: {paletteName}");
            Console.WriteLine();

            long grandBytes = 0, grandDedup = 0;
            int grandPoses = 0, grandDistinct = 0;

            foreach (string path in paths)
            {
                var s = Analyse(path, palette);
                Console.WriteLine($"--- {System.IO.Path.GetFileName(path)}");
                Console.WriteLine($"  {s.Width}x{s.Height} = {(long)s.Width * s.Height:N0} px; " +
                                  $"{s.Opaque:N0} opaque, {s.Transparent:N0} transparent, " +
                                  $"{s.PartialAlpha:N0} partial-alpha");

                // Palette fit: PoseLoader (M2 Q1) demands exact RGB matches and refuses otherwise.
                var paletteRgb = palette.Skip(1)
                    .Select(c => ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue).ToHashSet();
                long matched = s.ColorCounts.Where(kv => paletteRgb.Contains(kv.Key)).Sum(kv => (long)kv.Value);
                Console.WriteLine($"  distinct opaque colours: {s.ColorCounts.Count:N0}");
                Console.WriteLine($"  exact palette matches  : {s.ColorCounts.Count(kv => paletteRgb.Contains(kv.Key))} " +
                                  $"colour(s), {matched:N0} px ({(s.Opaque == 0 ? 0 : 100.0 * matched / s.Opaque):F1} % of opaque)");
                Console.WriteLine("  top colours: " + string.Join(", ", s.ColorCounts
                    .OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => $"#{kv.Key:X6} x{kv.Value:N0}")));

                // The colours PoseLoader would choke on. One unmatched colour is the difference
                // between "import the sheet" and "UnmappedColor refusal on every pose".
                uint zeroRgb = ((uint)palette[0].Red << 16) | ((uint)palette[0].Green << 8) | palette[0].Blue;
                var unmatchedColours = s.ColorCounts.Where(kv => !paletteRgb.Contains(kv.Key))
                                                    .OrderByDescending(kv => kv.Value).ToList();
                foreach (var kv in unmatchedColours)
                    Console.WriteLine($"  UNMATCHED #{kv.Key:X6} x{kv.Value:N0} " +
                                      $"({100.0 * kv.Value / s.Opaque:F1} % of opaque) -- " +
                                      (kv.Key == zeroRgb
                                          ? "equals palette[0]: PoseLoader forces it to index 0 (warning only)"
                                          : "PoseLoader REFUSES the pose (ImportErrorCode.UnmappedColor)"));

                var poses = s.Poses;
                var sized = poses.Where(p => !p.OverCanvas).ToList();
                var fitting = sized.Where(p => !p.OverCharBudget).ToList();
                long bytes = fitting.Sum(p => (long)p.SerializedBytes);

                Console.WriteLine($"  candidate poses        : {poses.Count} " +
                                  $"(merge gap {MergeGap}px, min {MinPosePixels}px)");
                Console.WriteLine($"    over 256x256 canvas  : {poses.Count - sized.Count}");
                Console.WriteLine($"    over {SpriteTiler.MaxChars}-char budget   : {sized.Count - fitting.Count}");
                if (sized.Count > 0)
                {
                    Console.WriteLine($"    bbox w x h  p50 {Pct(sized.Select(p => p.Width), .5)}x{Pct(sized.Select(p => p.Height), .5)}, " +
                                      $"p90 {Pct(sized.Select(p => p.Width), .9)}x{Pct(sized.Select(p => p.Height), .9)}, " +
                                      $"max {sized.Max(p => p.Width)}x{sized.Max(p => p.Height)}");
                    Console.WriteLine($"    chars       p50 {Pct(sized.Select(p => p.CharCount), .5)}, " +
                                      $"p90 {Pct(sized.Select(p => p.CharCount), .9)}, " +
                                      $"max {sized.Max(p => p.CharCount)} (budget {SpriteTiler.MaxChars})");
                }
                var distinct = fitting.GroupBy(p => p.ContentHash).ToList();
                long dedupBytes = distinct.Sum(g => (long)g.First().SerializedBytes);
                Console.WriteLine($"    distinct content     : {distinct.Count} of {fitting.Count} " +
                                  $"({fitting.Count - distinct.Count} exact repeats)");
                if (fitting.Count > 0)
                    Console.WriteLine($"    bytes       p50 0x{Pct(fitting.Select(p => p.SerializedBytes), .5):X}, " +
                                      $"p90 0x{Pct(fitting.Select(p => p.SerializedBytes), .9):X}, " +
                                      $"total {bytes:N0} ({bytes / 1024} KB)");
                Console.WriteLine();

                if (overlayDir != null)
                {
                    System.IO.Directory.CreateDirectory(overlayDir);
                    string outPath = System.IO.Path.Combine(overlayDir,
                        System.IO.Path.GetFileNameWithoutExtension(path) + "-segments.png");
                    DumpOverlay(path, s, outPath);
                    Console.WriteLine($"  overlay -> {outPath}");
                    Console.WriteLine();
                }

                Console.WriteLine($"    bytes after dedup    : {dedupBytes:N0} ({dedupBytes / 1024} KB)");

                // Row bands: poses whose vertical extents overlap belong to one animation strip,
                // which on these sheets is exactly what the captions label. Band count is the
                // manifest's natural size -- naming 30 strips beats naming 533 poses.
                var bands = new List<(int Top, int Bottom, int Count)>();
                foreach (var pose in s.Poses.OrderBy(p => p.MinY))
                {
                    int last = bands.Count - 1;
                    if (last >= 0 && pose.MinY <= bands[last].Bottom)
                        bands[last] = (bands[last].Top, Math.Max(bands[last].Bottom, pose.MaxY), bands[last].Count + 1);
                    else
                        bands.Add((pose.MinY, pose.MaxY, 1));
                }
                Console.WriteLine($"    animation row bands  : {bands.Count} " +
                                  $"(p50 {Pct(bands.Select(b => b.Count), .5)} poses/band, " +
                                  $"max {bands.Max(b => b.Count)})");

                grandBytes += bytes;
                grandDedup += dedupBytes;
                grandPoses += fitting.Count;
                grandDistinct += distinct.Count;
            }

            Console.WriteLine("=== capacity verdict ===");
            Console.WriteLine($"  importable poses across all sheets: {grandPoses} " +
                              $"({grandDistinct} distinct, {grandPoses - grandDistinct} exact repeats)");
            Console.WriteLine($"  total serialized bytes            : {grandBytes:N0} ({grandBytes / 1024} KB)");
            Console.WriteLine($"  after deduplication               : {grandDedup:N0} ({grandDedup / 1024} KB)");
            Console.WriteLine($"  M2c free-space pool               : 94,642 bytes (92 KB), 99 poses");
            Console.WriteLine($"  => {(grandDedup <= 94642 ? "FITS in the stock ROM" : "EXCEEDS the stock pool -- M3 territory")}" +
                              $" (deduped, best case)");
            Console.WriteLine($"  M3 extended space (8 MB ExHiROM)  : ~3,997,696 bytes (3.8 MB)");
            Console.WriteLine($"  => {(grandDedup <= 3997696 ? "fits after expansion" : "would not fit even expanded")}");

            return 0;
        }
    }
}
