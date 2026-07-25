using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// Answers specs/m4-batch-spec.md Part E's last open question -- **which image indices does a
    /// given character own?** -- without the 440-way decode-and-look pass that question assumes.
    ///
    /// The insight is that one index is already known. M0 recorded that image index 0x8C decodes to
    /// a clean, correctly-posed Donkey Kong (specs/prd-sprite-importer.md S9), so DK's block can be
    /// grown from that seed instead of searched for.
    ///
    /// **Method: connected components of the animation-to-index bipartite graph.** An animation
    /// links every index it draws. Starting from the seed index, take every animation that
    /// references it, add all of *their* indices, and repeat to a fixpoint. If characters do not
    /// share sprites, the component containing 0x8C is exactly DK's sprite set -- a stronger and
    /// more falsifiable claim than A.7's statistical "index locality", because it is derived from
    /// the scripts rather than from how close the numbers happen to sit.
    ///
    /// **The known way this can go wrong, reported rather than assumed away.** The mount commands
    /// 0x85/0x86 draw the Kong *and* the animal buddy it is riding (see AnimationTable.Parse), so a
    /// single riding animation genuinely links DK's indices to Rambi's. That is one edge that can
    /// merge two characters into one component. The per-round growth trace below exists to make it
    /// visible: a clean character block converges in a few rounds and stops, whereas a bleed shows
    /// up as a round that suddenly multiplies the set. <see cref="Report"/> also names the
    /// widest-spread animations in the component, which are the bridge candidates to inspect first.
    ///
    /// This writes nothing. It is a research/analysis pass whose output is meant to be read by a
    /// human and then turned into a manifest by hand.
    /// </summary>
    public static class IndexOwnership
    {
        /// <summary>Growth of the component on one closure round, for the bleed trace.</summary>
        public sealed class Round
        {
            public int Number;
            public int NewAnimations;
            public int NewIndices;
            public int TotalAnimations;
            public int TotalIndices;
        }

        public sealed class Component
        {
            public int Seed;
            public SortedSet<int> Indices = new SortedSet<int>();
            public SortedSet<int> Animations = new SortedSet<int>();
            public List<Round> Rounds = new List<Round>();

            public int Low => Indices.Count == 0 ? 0 : Indices.Min;
            public int High => Indices.Count == 0 ? 0 : Indices.Max;

            /// <summary>Indices step by 4 (GfxTable), so a fully packed block of N spans 4*(N-1).</summary>
            public int Span => Indices.Count == 0 ? 0 : High - Low;
            public int PackedSpan => Indices.Count == 0 ? 0 : 4 * (Indices.Count - 1);
            /// <summary>1.0 means the block is contiguous with no foreign index interleaved.</summary>
            public double Density => Span == 0 ? 1.0 : (double)PackedSpan / Span;
        }

        /// <summary>
        /// Grows the component containing <paramref name="seedIndex"/> to a fixpoint over
        /// <paramref name="scripts"/>. Only successfully-parsed scripts participate: a script that
        /// failed to walk has an unreliable index list, and letting it link things would launder a
        /// parse bug into a manifest.
        /// </summary>
        public static Component Grow(List<AnimationTable.Script> scripts, int seedIndex)
        {
            var ok = scripts.Where(s => s.Ok).ToList();
            var component = new Component { Seed = seedIndex };
            component.Indices.Add(seedIndex);

            for (int round = 1; ; round++)
            {
                int animsBefore = component.Animations.Count;
                int indicesBefore = component.Indices.Count;

                foreach (var script in ok)
                {
                    if (component.Animations.Contains(script.Animation)) continue;
                    if (!script.ImageIndices.Any(component.Indices.Contains)) continue;

                    component.Animations.Add(script.Animation);
                    foreach (int index in script.ImageIndices)
                        component.Indices.Add(index);
                }

                int newAnims = component.Animations.Count - animsBefore;
                int newIndices = component.Indices.Count - indicesBefore;
                if (newAnims == 0 && newIndices == 0) break;

                component.Rounds.Add(new Round
                {
                    Number = round,
                    NewAnimations = newAnims,
                    NewIndices = newIndices,
                    TotalAnimations = component.Animations.Count,
                    TotalIndices = component.Indices.Count,
                });
            }

            return component;
        }

        public static int Run(Rom rom, int seedIndex, string? contactPath = null, string paletteName = "Donkey Kong 1P")
        {
            var scripts = AnimationTable.ParseAll(rom);
            int failed = scripts.Count(s => !s.Ok);

            Console.WriteLine($"=== M4: index ownership from seed 0x{seedIndex:X} ===");
            Console.WriteLine($"Scripts          : {scripts.Count} ({failed} failed, excluded)");

            var valid = GfxTable.EnumerateImageIndices(rom).ToHashSet();
            Console.WriteLine($"GFX table        : {valid.Count:N0} indices, " +
                              $"0x{valid.Min():X}..0x{valid.Max():X}");
            if (!valid.Contains(seedIndex))
                Console.WriteLine($"  WARNING: seed 0x{seedIndex:X} is not a populated GFX index.");
            Console.WriteLine();

            var direct = scripts.Where(s => s.Ok && s.ImageIndices.Contains(seedIndex)).ToList();
            Console.WriteLine($"Animations referencing the seed directly: {direct.Count}");
            foreach (var s in direct.Take(20))
            {
                var d = s.Distinct.OrderBy(x => x).ToList();
                Console.WriteLine($"  anim {s.Animation,3} ({s.FrameCount,3} frames, {d.Count,3} distinct): " +
                                  $"0x{d[0]:X}..0x{d[^1]:X}");
            }
            if (direct.Count > 20) Console.WriteLine($"  ... and {direct.Count - 20} more");
            Console.WriteLine();

            var component = Grow(scripts, seedIndex);
            Report(component, scripts, valid);

            if (contactPath != null)
            {
                if (!PalettePointers.Table.TryGetValue(paletteName, out int palAddr))
                {
                    Console.Error.WriteLine($"Unknown palette '{paletteName}'.");
                    return 1;
                }
                var palette = Palette.Read(rom, palAddr);
                var renderable = component.Indices.Where(valid.Contains).ToList();
                WriteContactSheet(rom, renderable, palette, contactPath);
                Console.WriteLine();
                Console.WriteLine($"Wrote {contactPath}: {renderable.Count} index(es), palette '{paletteName}'.");
            }

            return 0;
        }

        private static void Report(Component c, List<AnimationTable.Script> scripts, HashSet<int> validIndices)
        {
            Console.WriteLine("Closure (each round adds animations touching the set, then their indices):");
            foreach (var r in c.Rounds)
                Console.WriteLine($"  round {r.Number}: +{r.NewAnimations,3} anim, +{r.NewIndices,4} idx " +
                                  $"-> {r.TotalAnimations,3} anim, {r.TotalIndices,4} idx");
            Console.WriteLine();

            int inTable = c.Indices.Count(validIndices.Contains);
            Console.WriteLine($"Component        : {c.Animations.Count} animation(s), {c.Indices.Count} index(es)");
            Console.WriteLine($"  index range    : 0x{c.Low:X}..0x{c.High:X} (span 0x{c.Span:X})");
            Console.WriteLine($"  in GFX table   : {inTable}/{c.Indices.Count}");
            Console.WriteLine($"  density        : {c.Density:P1} " +
                              "(100% = contiguous stride-4 block, no foreign index interleaved)");
            Console.WriteLine($"  share of table : {100.0 * inTable / Math.Max(1, validIndices.Count):F1} % " +
                              "of all populated indices");
            Console.WriteLine();

            // Density alone is misleading: a character owning several separate runs scores low
            // without anything being wrong. Print the runs, which distinguishes "two characters
            // merged by a mount edge" (runs far apart, different sprites) from "one character
            // stored in a few blocks" (runs far apart, same sprite) -- the contact sheet settles
            // which, and for the 0x8C seed it is the latter.
            var runs = Runs(c.Indices).ToList();
            Console.WriteLine($"Contiguous stride-4 runs: {runs.Count}");
            foreach (var (lo, hi, n) in runs)
                Console.WriteLine($"  0x{lo:X}..0x{hi:X} ({n} index(es))");
            Console.WriteLine();

            // An animation whose own indices straddle a wide range is the shape a mount/bridge edge
            // takes: it is what pulls a second character's block into the component in one step.
            Console.WriteLine("Widest-spread animations in the component (bridge candidates first):");
            var byId = scripts.Where(s => s.Ok).ToDictionary(s => s.Animation);
            var spread = c.Animations
                .Select(a => byId[a])
                .Where(s => s.Distinct.Count() > 1)
                .Select(s =>
                {
                    var d = s.Distinct.OrderBy(x => x).ToList();
                    return (Script: s, Low: d[0], High: d[^1], Span: d[^1] - d[0], Distinct: d.Count);
                })
                .OrderByDescending(x => x.Span)
                .Take(10)
                .ToList();

            foreach (var x in spread)
                Console.WriteLine($"  anim {x.Script.Animation,3}: 0x{x.Low:X}..0x{x.High:X} " +
                                  $"(span 0x{x.Span:X}, {x.Distinct} distinct, {x.Script.FrameCount} frames)");
            if (spread.Count == 0)
                Console.WriteLine("  (none -- every animation in the component draws a single index)");
        }

        /// <summary>
        /// Renders every index in the component to one labelled grid PNG -- the "look" half of the
        /// decode-and-look pass Part E describes. Each cell is a decoded sprite cropped to its
        /// opaque bounds and captioned with its index, so a human can confirm in one glance that
        /// the component really is one character (and spot the cell where it stops being one).
        ///
        /// Cropping to opaque bounds matters: sprites composite onto a 256x256 canvas and most
        /// occupy a small corner of it, so an uncropped grid is mostly empty space at a scale where
        /// nothing is recognisable.
        /// </summary>
        public static void WriteContactSheet(Rom rom, IEnumerable<int> indices, SKColor[] palette, string outPath,
                                             float maxScale = 1f)
        {
            const int cell = 72, pad = 14, cols = 12;
            var list = indices.ToList();
            int rows = (list.Count + cols - 1) / cols;

            using var sheet = new SKBitmap(cols * cell, rows * (cell + pad));
            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Clear(new SKColor(24, 24, 28));
                using var text = new SKPaint
                {
                    Color = new SKColor(255, 255, 0, 230),
                    TextSize = 10,
                    IsAntialias = true,
                };

                for (int i = 0; i < list.Count; i++)
                {
                    int cx = (i % cols) * cell, cy = (i / cols) * (cell + pad);
                    canvas.DrawText($"0x{list[i]:X}", cx + 2, cy + cell + 10, text);

                    int address = GfxTable.ResolveSpriteAddress(rom, list[i]);
                    if (address == 0) continue;

                    using var sprite = SpriteDecoder.Decode(rom, address, palette);
                    var b = OpaqueBounds(sprite);
                    if (b.Width <= 0 || b.Height <= 0) continue;

                    // Fit the crop into the cell, capped at maxScale. The default 1:1 keeps a big
                    // survey honest -- an 8x8 sprite blown up to 72x72 reads as a colour smear --
                    // but a narrow range of small sprites needs upscaling to be identifiable at all.
                    float scale = Math.Min(maxScale, Math.Min((float)cell / b.Width, (float)cell / b.Height));
                    float w = b.Width * scale, h = b.Height * scale;
                    var dst = SKRect.Create(cx + (cell - w) / 2, cy + (cell - h) / 2, w, h);
                    canvas.DrawBitmap(sprite, b, dst);
                }
            }

            using var img = SKImage.FromBitmap(sheet);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.OpenWrite(outPath);
            data.SaveTo(fs);
        }

        /// <summary>
        /// Lists animations drawing entirely inside an index range, grouped by frame count -- the
        /// query the remaining manual step needs (m4-batch-spec Part E: "which sheet strip maps to
        /// which animation"). Once A.8 has fixed a character's block, a strip of N poses can only
        /// be an animation of N frames *within that block*, which is a far smaller candidate set
        /// than A.7's 440-way frame-count match.
        ///
        /// "Entirely inside" is deliberate: an animation with one index outside the range draws
        /// something that is not this character (a held barrel, a mounted buddy), so its frame
        /// count does not correspond 1:1 to a strip of this character's poses.
        /// </summary>
        public static int RunAnimsIn(Rom rom, int low, int high, int? frameFilter)
        {
            var scripts = AnimationTable.ParseAll(rom).Where(s => s.Ok && s.FrameCount > 0).ToList();
            var inside = scripts.Where(s => s.ImageIndices.All(i => i >= low && i <= high)).ToList();

            Console.WriteLine($"=== animations drawing entirely inside 0x{low:X}..0x{high:X} ===");
            Console.WriteLine($"{inside.Count} of {scripts.Count} non-empty animation(s)");
            Console.WriteLine();

            foreach (var g in inside.GroupBy(s => s.FrameCount).OrderBy(g => g.Key))
            {
                if (frameFilter.HasValue && g.Key != frameFilter.Value) continue;
                Console.WriteLine($"  {g.Key,3} frames -> {g.Count(),2} animation(s): " +
                                  string.Join(", ", g.Select(s => $"{s.Animation}")));
                if (frameFilter.HasValue)
                    foreach (var s in g)
                    {
                        var d = s.Distinct.OrderBy(x => x).ToList();
                        Console.WriteLine($"        anim {s.Animation,3}: {d.Count,3} distinct, " +
                                          $"0x{d[0]:X}..0x{d[^1]:X}");
                        Console.WriteLine($"          {string.Join(" ", s.ImageIndices.Select(i => $"0x{i:X}"))}");
                    }
            }
            return 0;
        }

        /// <summary>
        /// Renders one row per *distinct index set* among the animations drawing entirely inside a
        /// range, each row labelled with the animation ids that share it — the ROM-side counterpart
        /// of the caption montage (A.10), and the tool the strip→animation match needs.
        ///
        /// Grouping by index set rather than by animation is what makes this tractable: DK's block
        /// holds 69 animations but far fewer distinct sets, because several scripts play the same
        /// frames at different speeds or from different entry points (anims 2/14/20 all draw
        /// 0x330..0x37C). Rendering per animation would repeat the same pictures many times over.
        ///
        /// Frames are drawn in the script's own draw order, so a row reads as the animation plays
        /// rather than as an ascending index dump — which is what makes an action recognisable.
        /// </summary>
        public static int RunAnimSheet(Rom rom, int low, int high, string outPath, SKColor[] palette,
                                       int fromGroup, int toGroup, int maxFrames = 24, int cell = 46,
                                       float maxScale = 1f)
        {
            var scripts = AnimationTable.ParseAll(rom).Where(s => s.Ok && s.FrameCount > 0).ToList();
            var inside = scripts.Where(s => s.ImageIndices.All(i => i >= low && i <= high)).ToList();

            var groups = inside
                .GroupBy(s => string.Join(",", s.Distinct.OrderBy(x => x)))
                .Select(g => new
                {
                    Anims = g.Select(s => s.Animation).OrderBy(a => a).ToList(),
                    // Draw order from the longest script in the group: the one that visits the most
                    // frames is the least likely to be a truncated entry point into the sequence.
                    Order = g.OrderByDescending(s => s.FrameCount).First().ImageIndices
                             .Distinct().ToList(),
                })
                .OrderBy(g => g.Order.Min())
                .ToList();

            var page = groups.Skip(fromGroup).Take(Math.Max(0, toGroup - fromGroup + 1)).ToList();
            Console.WriteLine($"=== animation sheet 0x{low:X}..0x{high:X} ===");
            Console.WriteLine($"{inside.Count} in-block animation(s) -> {groups.Count} distinct index set(s); " +
                              $"rendering {fromGroup}..{Math.Min(toGroup, groups.Count - 1)}");

            const int labelW = 168, pad = 4;
            int rowH = cell + pad;
            using var sheet = new SKBitmap(labelW + maxFrames * cell, Math.Max(1, page.Count * rowH));
            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Clear(new SKColor(24, 24, 28));
                using var text = new SKPaint { Color = new SKColor(255, 255, 0, 230), TextSize = 11, IsAntialias = true };

                for (int i = 0; i < page.Count; i++)
                {
                    var g = page[i];
                    int y = i * rowH;
                    string ids = string.Join(",", g.Anims.Take(3)) + (g.Anims.Count > 3 ? "…" : "");
                    canvas.DrawText($"anim {ids}", 4, y + cell / 2 - 2, text);
                    canvas.DrawText($"{g.Order.Count}f 0x{g.Order.Min():X}", 4, y + cell / 2 + 11, text);

                    for (int f = 0; f < Math.Min(maxFrames, g.Order.Count); f++)
                    {
                        int address = GfxTable.ResolveSpriteAddress(rom, g.Order[f]);
                        if (address == 0) continue;
                        using var sprite = SpriteDecoder.Decode(rom, address, palette);
                        var b = OpaqueBounds(sprite);
                        if (b.Width <= 0 || b.Height <= 0) continue;
                        float scale = Math.Min(maxScale, Math.Min((float)cell / b.Width, (float)cell / b.Height));
                        float w = b.Width * scale, h = b.Height * scale;
                        canvas.DrawBitmap(sprite, b,
                            SKRect.Create(labelW + f * cell + (cell - w) / 2, y + (cell - h) / 2, w, h));
                    }
                }
            }

            using var img = SKImage.FromBitmap(sheet);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.OpenWrite(outPath);
            data.SaveTo(fs);
            Console.WriteLine($"Wrote {outPath}");
            return 0;
        }

        /// <summary>Groups a sorted index set into maximal runs of consecutive stride-4 indices.</summary>
        public static IEnumerable<(int Low, int High, int Count)> Runs(IEnumerable<int> indices)
        {
            var sorted = indices.OrderBy(x => x).ToList();
            if (sorted.Count == 0) yield break;

            int low = sorted[0], prev = sorted[0], count = 1;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] == prev + 4) { count++; }
                else { yield return (low, prev, count); low = sorted[i]; count = 1; }
                prev = sorted[i];
            }
            yield return (low, prev, count);
        }

        /// <summary>Bounding box of the non-transparent pixels, or an empty rect if there are none.</summary>
        private static SKRectI OpaqueBounds(SKBitmap bmp)
        {
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (bmp.GetPixel(x, y).Alpha == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
        }
    }
}
