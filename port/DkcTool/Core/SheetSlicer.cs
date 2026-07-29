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
    /// 3. Flood-fill 8-connected, then absorb bboxes within <see cref="MergeGap"/> — but only
    ///    asymmetrically, a fragment into a figure, never figure into figure
    ///    (<see cref="FragmentRatio"/>).
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

        /// <summary>
        /// Absorption is <b>asymmetric</b>: a component only joins a neighbour within
        /// <see cref="MergeGap"/> if it is smaller than this fraction of it.
        ///
        /// <para><see cref="MergeGap"/> exists to reattach a <i>fragment</i> — a hand clear of the
        /// body across transparent pixels — to its figure. It was never meant to join two figures,
        /// but a symmetric proximity test cannot tell the two cases apart, and the DK sheet lays
        /// poses <b>1–3 px apart</b>, inside the gap. That merged 39 rects across 21 of 46 strips
        /// and hid ≥89 poses (A.24), which is what made strip 10 "Jump" read as 19 poses against
        /// its animation's 20 and look like it needed animation-script editing. It did not.</para>
        ///
        /// <para>A fragment is a small share of its figure; two adjacent poses are comparable in
        /// size. 0.5 sits in the empty middle of that distribution rather than at a measured edge —
        /// see the margin figures in A.25 before tightening it.</para>
        /// </summary>
        public const double FragmentRatio = 0.5;

        /// <summary>A black component this small is dust, not a glyph. Far below
        /// <see cref="MinPosePixels"/> — caption letters are only a few pixels tall.</summary>
        public const int MinGlyphPixels = 4;

        /// <summary>Horizontal gap bridged when merging black glyphs into one headline. Wide enough
        /// to cross the space in "Rope Idle", narrow enough not to join two headlines that sit side
        /// by side over adjacent runs ("Rope Idle" / "Rope Climb" are ~165 px apart).</summary>
        public const int CaptionGlyphGap = 12;

        /// <summary>A black mark at least this tall and no wider than <see cref="RuleMaxWidth"/> is
        /// one of the sheet's drawn segment rules (A.29), not a headline glyph. Measured: every rule
        /// on the DK sheet is exactly 1 px wide and 46..58 px tall, while the tallest bracket glyph
        /// is 4x17 — so the two populations are far apart and this threshold has room.</summary>
        public const int RuleMinHeight = 30;
        public const int RuleMaxWidth = 3;

        /// <summary>
        /// A glyph at least this many times taller than it is wide, and no wider than
        /// <see cref="BracketMaxWidth"/>, is a parenthesis.
        ///
        /// <para>Measured brackets are 2x9, 3x11 and 4x17 (ratios 4.5, 3.7, 4.3); the letters that
        /// open headlines are 7x9, 12x13, 18x11 and one 60x17 blob (ratios 1.3 and below). Nothing
        /// on either sheet sits between.</para>
        /// </summary>
        public const double BracketAspect = 3.0;
        public const int BracketMaxWidth = 4;

        /// <summary>
        /// Vertical gap that separates two <i>rows</i> of the same run, measured between successive
        /// pose tops within one column.
        ///
        /// <para>Rows are clustered per column rather than per band because sectors on the same band
        /// keep independent row rhythms — on the DK sheet "Swing" wraps at 1458/1520 while "Map
        /// Stuff", level with it, wraps at 1449/1480/1508. Clustering tops across the whole band
        /// merges 1449 with 1458 and reinstates the very interleaving this fixes (A.39).</para>
        /// </summary>
        public const int RowGap = 20;

        /// <summary>Horizontal distance within which two poses at the same height belong to the same
        /// row <i>of the same sector</i>. Wide enough to bridge the rules between runs sharing a row
        /// ("Rope Idle" to "Rope Climb" is 23 px), far below the whitespace between sectors sitting
        /// side by side ("Swing" to "Map Stuff" is 301 px).</summary>
        public const int RowLinkGapX = 40;

        /// <summary>How far above a row's poses its headline may sit. The sheet keeps titles tight
        /// to their run — the measured reaches are 4..16 px — so this only has to exclude the
        /// headline of the sector *above*, which is a row-height away.</summary>
        public const int CaptionReach = 30;

        /// <summary>Vertical gap a wrapped row may leave below the row it continues. Measured wraps
        /// on the DK sheet are 4..9 px; the sector *below* an uncaptioned run is always further.
        /// </summary>
        public const int ContinuationReach = 40;

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

            /// <summary>How many sheet rows this strip's poses wrap across. &gt;1 means the run was
            /// joined in reading order by the band fix (A.39); 1 is the ordinary case.</summary>
            public int Rows = 1;

            /// <summary>Bounding box of the black headline sitting above this strip's first pose,
            /// or null if the strip is a wrap continuation with no headline of its own. Informational
            /// -- the caption *text* still has to be read by eye (A.29), but its presence is what
            /// tells the grouper a new run starts here.</summary>
            public Caption? Caption;

            public List<SlicedPose> Poses = new List<SlicedPose>();
        }

        /// <summary>
        /// A black headline: the sector title the sheet writes at the top-left of each captioned run
        /// (A.29 lists them). Individual glyphs are separate components -- this is a whole line of
        /// them merged back together.
        /// </summary>
        public sealed class Caption
        {
            public int MinX, MinY, MaxX, MaxY;
            public int Width => MaxX - MinX + 1;
            public int Height => MaxY - MinY + 1;

            /// <summary>
            /// The line is wrapped in brackets, which on these sheets means it qualifies the
            /// headline above it rather than naming a run of its own — "(Reverse to return to
            /// idle)" tells you Bang Chest is played backwards to get back to the idle, so the row
            /// under it is still Bang Chest.
            ///
            /// <para>An annotation must never start a strip. Treated as a headline it does the
            /// opposite of the band fix: it cuts a wrapped run in half at exactly the row the
            /// artist was annotating.</para>
            /// </summary>
            public bool IsAnnotation;
        }

        public sealed class SlicedSheet
        {
            public string Path = "";
            public int Width, Height;
            public List<Strip> Strips = new List<Strip>();
            public List<Caption> Captions = new List<Caption>();
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
                .Where(i => i.HasArtwork)
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

            var captions = GroupCaptions(islands.Where(i => !i.HasArtwork));

            var sheet = new SlicedSheet { Path = path, Width = w, Height = h, Captions = captions };
            sheet.Strips = GroupIntoStrips(poses, captions);
            return sheet;
        }

        /// <summary>
        /// Flood-fills opaque islands, then absorbs islands whose bboxes are within
        /// <see cref="MergeGap"/> of each other <i>and</i> differ enough in size to be a fragment
        /// and its figure (<see cref="FragmentRatio"/>), dropping caption-only and dust components.
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
                        if (!Absorbs(islands[i], islands[j])) continue;
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

            return islands
                .Where(p => p.HasArtwork
                    ? p.OpaquePixels >= MinPosePixels
                    : p.OpaquePixels >= MinGlyphPixels)
                .ToList();
        }

        /// <summary>
        /// Merges the surviving black components into headline boxes: glyphs on a shared text line,
        /// close enough horizontally to be one title.
        ///
        /// <para>Drops the sheet's drawn segment rules first (A.29) — a thin tall bar is a boundary
        /// mark, not a letter, and one left in would stretch a headline's box across the run it
        /// divides.</para>
        /// </summary>
        private static List<Caption> GroupCaptions(IEnumerable<Island> annotations)
        {
            var glyphs = annotations
                .Where(a => !(a.MaxX - a.MinX + 1 <= RuleMaxWidth && a.MaxY - a.MinY + 1 >= RuleMinHeight))
                .OrderBy(a => a.MinY).ThenBy(a => a.MinX)
                .ToList();


            var lines = new List<List<Island>>();
            foreach (var g in glyphs)
            {
                // Same text line = vertical ranges overlap; same title = within one glyph gap.
                var host = lines.FirstOrDefault(l => Adjacent(Box(l), g));
                if (host == null) lines.Add(new List<Island> { g });
                else host.Add(g);
            }

            // One pass is not enough: a glyph can bridge two boxes that were opened separately
            // before the letter between them arrived.
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < lines.Count && !merged; i++)
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (!Adjacent(Box(lines[i]), Box(lines[j]))) continue;
                        lines[i].AddRange(lines[j]);
                        lines.RemoveAt(j);
                        merged = true;
                        break;
                    }
            }

            return lines
                .Select(l =>
                {
                    var b = Box(l);
                    return new Caption
                    {
                        MinX = b.MinX,
                        MinY = b.MinY,
                        MaxX = b.MaxX,
                        MaxY = b.MaxY,
                        IsAnnotation = IsBracket(l.Aggregate((a, x) => x.MinX < a.MinX ? x : a))
                                    && IsBracket(l.Aggregate((a, x) => x.MaxX > a.MaxX ? x : a)),
                    };
                })
                .OrderBy(c => c.MinY).ThenBy(c => c.MinX)
                .ToList();

            static Island Box(List<Island> l) => new Island
            {
                MinX = l.Min(g => g.MinX),
                MinY = l.Min(g => g.MinY),
                MaxX = l.Max(g => g.MaxX),
                MaxY = l.Max(g => g.MaxY),
            };

            static bool Adjacent(Island a, Island b) =>
                a.MinY <= b.MaxY && b.MinY <= a.MaxY &&
                a.MinX - CaptionGlyphGap <= b.MaxX && b.MinX - CaptionGlyphGap <= a.MaxX;
        }

        /// <summary>A parenthesis: markedly taller than it is wide, and only a few pixels across.
        /// </summary>
        private static bool IsBracket(Island g)
        {
            int w = g.MaxX - g.MinX + 1, h = g.MaxY - g.MinY + 1;
            return w <= BracketMaxWidth && h >= w * BracketAspect;
        }

        private static bool Near(Island a, Island b) =>
            a.MinX - MergeGap <= b.MaxX && b.MinX - MergeGap <= a.MaxX &&
            a.MinY - MergeGap <= b.MaxY && b.MinY - MergeGap <= a.MaxY;

        /// <summary>One of the two is a fragment of the other — see <see cref="FragmentRatio"/>.
        /// Compared on opaque pixel count, not bbox area, because a figure's bbox is inflated by
        /// whichever limb reaches furthest while its pixel count is not.
        ///
        /// <para>Artwork never absorbs annotation. Policy 1 drops a pure-black component as a
        /// caption, but that filter runs *after* merging, so a black mark touching a pose was
        /// absorbed into it and carried through — the pose then contains a colour DK's palette does
        /// not have. The sheet rules segment boundaries with thin drawn bars (A.29), and two of them
        /// landed inside poses this way; the importer caught it as an UnmappedColor refusal rather
        /// than importing a black bar, which is the refusal doing its job but well after the
        /// mistake.</para></summary>
        private static bool Absorbs(Island a, Island b) =>
            a.HasArtwork == b.HasArtwork
            && Math.Min(a.OpaquePixels, b.OpaquePixels)
                < FragmentRatio * Math.Max(a.OpaquePixels, b.OpaquePixels);

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
        private static List<Strip> GroupIntoStrips(List<SlicedPose> posesByMinY, List<Caption> captions)
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
                // A band is not a row. One tall pose reaching from row N into row N+1 pulls both
                // into the same band, and ordering that by MinX interleaves them -- which is the
                // whole defect (A.39). Decompose the band into *rows of one sector* first, so every
                // later step sees a genuine single row.
                foreach (var row in RowComponents(bands[bandIndex]))
                {
                    foreach (var segment in SplitRowAtGaps(row))
                    {
                        int segMinX = segment.Min(p => p.MinX);
                        int segMaxX = segment.Max(p => p.MaxX);

                        // The sheet writes a headline at the top of each sector. One over this
                        // segment means a new run starts; none means the segment is the previous
                        // run continuing onto another row.
                        var caption = FindHeadline(captions, segment);

                        Strip? host = caption == null
                            ? FindWrappedRun(strips, segment, segMinX, segMaxX)
                            : null;

                        if (host == null)
                        {
                            host = new Strip
                            {
                                Index = strips.Count,
                                Band = bandIndex,
                                Caption = caption,
                            };
                            strips.Add(host);
                        }
                        else host.Rows++;

                        foreach (var pose in segment)
                        {
                            pose.StripIndex = host.Index;
                            pose.Position = host.Poses.Count;
                            host.Poses.Add(pose);
                        }
                    }
                }
            }

            return strips;
        }

        /// <summary>
        /// The headline sitting over this segment, or null if the segment carries none and is
        /// therefore a wrapped continuation of the run above it.
        ///
        /// <para>Anchored on the poses the caption is actually <i>over</i>, not on the segment as a
        /// whole. A run's poses are not level: strip 10 "Jump" rises through its arc to y 513 while
        /// its headline sits at y 514..524 above the leftmost pose, which starts at y 542. Tested
        /// against the segment's overall top the title reads as *below* the run and the whole strip
        /// is mistaken for a continuation -- which merged Jump into the run above it.</para>
        /// </summary>
        private static Caption? FindHeadline(List<Caption> captions, List<SlicedPose> segment)
        {
            Caption? best = null;
            foreach (var c in captions)
            {
                if (c.IsAnnotation) continue;

                int top = int.MaxValue;
                foreach (var p in segment)
                    if (p.MinX <= c.MaxX && c.MinX <= p.MaxX && p.MinY < top) top = p.MinY;
                if (top == int.MaxValue) continue;

                if (c.MaxY >= top || top - c.MaxY > CaptionReach) continue;
                if (best == null || c.MinX < best.MinX) best = c;
            }
            return best;
        }

        /// <summary>
        /// Splits a band into rows that each belong to one sector: poses link when they sit at the
        /// same height <i>and</i> are horizontally adjacent.
        ///
        /// <para>Height alone is not enough. Sectors sharing a band keep independent row rhythms --
        /// "Swing" wraps at 1458/1520 while "Map Stuff", level with it, wraps at 1449/1480/1508 --
        /// so clustering tops across the band merges Map Stuff's 1449 row into Swing's 1458 row and
        /// reinstates the interleaving. Requiring horizontal adjacency keeps the 301 px of
        /// whitespace between the two sectors doing its job.</para>
        /// </summary>
        private static List<List<SlicedPose>> RowComponents(List<SlicedPose> band)
        {
            var ordered = band.OrderBy(p => p.MinY).ThenBy(p => p.MinX).ToList();
            var owner = Enumerable.Range(0, ordered.Count).ToArray();

            int Find(int i) => owner[i] == i ? i : owner[i] = Find(owner[i]);
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) owner[b] = a; }

            for (int i = 0; i < ordered.Count; i++)
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    if (Math.Abs(ordered[i].MinY - ordered[j].MinY) > RowGap) continue;
                    int gap = Math.Max(ordered[j].MinX - ordered[i].MaxX, ordered[i].MinX - ordered[j].MaxX);
                    if (gap > RowLinkGapX) continue;
                    Union(i, j);
                }

            var rows = ordered
                .Select((p, i) => (Pose: p, Root: Find(i)))
                .GroupBy(x => x.Root)
                .Select(g => g.Select(x => x.Pose).OrderBy(p => p.MinX).ToList())
                .ToList();

            return OrderAsRead(rows);
        }

        /// <summary>
        /// Puts a band's rows into reading order: sectors left to right, and the rows *within* a
        /// sector top to bottom.
        ///
        /// <para>Sorting rows by top alone is wrong in both directions. Across a band it scrambles
        /// stacked sectors -- Map Stuff's 1449/1480/1508 rows interleave with Swing's 1458/1520 by
        /// height, so its own rows come out of order. Sorting by left alone stacks nothing. Grouping
        /// rows into columns by horizontal overlap first keeps each sector's rows together, and
        /// leaves single-row bands ordered exactly left to right as before -- which is what holds
        /// strips 0..24, and every manifest that addresses them, still.</para>
        /// </summary>
        private static List<List<SlicedPose>> OrderAsRead(List<List<SlicedPose>> rows)
        {
            var owner = Enumerable.Range(0, rows.Count).ToArray();
            int Find(int i) => owner[i] == i ? i : owner[i] = Find(owner[i]);

            var span = rows.Select(r => (Lo: r.Min(p => p.MinX), Hi: r.Max(p => p.MaxX))).ToList();
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                {
                    if (span[i].Hi < span[j].Lo || span[j].Hi < span[i].Lo) continue;
                    int a = Find(i), b = Find(j);
                    if (a != b) owner[b] = a;
                }

            return rows
                .Select((r, i) => (Row: r, Index: i, Column: Find(i)))
                .GroupBy(x => x.Column)
                .Select(g => (
                    Lo: g.Min(x => span[x.Index].Lo),
                    Rows: g.OrderBy(x => x.Row.Min(p => p.MinY)).Select(x => x.Row).ToList()))
                .OrderBy(c => c.Lo)
                .SelectMany(c => c.Rows)
                .ToList();
        }

        /// <summary>
        /// Splits one row at horizontal gaps well above its own typical inter-pose spacing -- the
        /// sheets put several captioned runs side by side on one line.
        /// </summary>
        private static List<List<SlicedPose>> SplitRowAtGaps(List<SlicedPose> row)
        {
            var gaps = new List<int>();
            for (int i = 1; i < row.Count; i++)
                gaps.Add(Math.Max(0, row[i].MinX - row[i - 1].MaxX));
            int typical = gaps.Count == 0 ? 0 : Percentile(gaps, .5);
            int split = Math.Max(typical * 3, typical + 8);

            var segments = new List<List<SlicedPose>>();
            var current = new List<SlicedPose>();
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0 && row[i].MinX - row[i - 1].MaxX > split && current.Count > 0)
                {
                    segments.Add(current);
                    current = new List<SlicedPose>();
                }
                current.Add(row[i]);
            }
            if (current.Count > 0) segments.Add(current);
            return segments;
        }

        /// <summary>
        /// Finds the run this uncaptioned segment continues: the nearest strip directly above it
        /// that overlaps it horizontally (A.29 -- "Swing", "Victory", "Map Stuff" and the End
        /// Credits runs each carry on for another row or two under one headline).
        ///
        /// <para>The search deliberately crosses band boundaries. Map Stuff wraps five rows and the
        /// vertical-overlap banding cuts it in two, so a continuation confined to its own band would
        /// rejoin only part of it.</para>
        /// </summary>
        private static Strip? FindWrappedRun(List<Strip> strips, List<SlicedPose> segment, int minX, int maxX)
        {
            int top = segment.Min(p => p.MinY);
            Strip? best = null;
            int bestOverlap = 0;

            foreach (var strip in strips)
            {
                if (strip.Poses.Count == 0) continue;
                int sMinX = strip.Poses.Min(p => p.MinX);
                int sMaxX = strip.Poses.Max(p => p.MaxX);
                int overlap = Math.Min(maxX, sMaxX) - Math.Max(minX, sMinX);
                if (overlap <= 0) continue;

                int gap = top - strip.Poses.Max(p => p.MaxY);
                if (gap < -RowGap || gap > ContinuationReach) continue;

                if (overlap >= bestOverlap) { bestOverlap = overlap; best = strip; }
            }

            return best;
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

            int wrapped = sheet.Strips.Count(s => s.Rows > 1);
            int annotations = sheet.Captions.Count(c => c.IsAnnotation);
            int used = sheet.Strips.Count(s => s.Caption != null);
            int idle = sheet.Captions.Count - annotations - used;

            Console.WriteLine($"  headlines naming a run: {used}");
            Console.WriteLine($"  bracketed annotations : {annotations}   (qualify the headline above; never start a run)");
            // Black text with no poses under it: the sheet's credit line, and anything else the
            // artist wrote in the margin. It anchors nothing, so it needs no special case -- but
            // count it separately rather than reporting it as a headline that found no run.
            Console.WriteLine($"  other black text      : {idle}   (nothing beneath it; ignored)");
            Console.WriteLine($"  runs wrapping rows    : {wrapped}");
            Console.WriteLine();

            foreach (var strip in sheet.Strips)
            {
                var parts = strip.Poses.Select(p =>
                    $"{p.Position}:{p.Width}x{p.Height}@({p.MinX},{p.MinY}) {p.CharCount}ch" +
                    (p.OverCanvas ? " [OVER-CANVAS]" : p.OverCharBudget ? " [OVER-BUDGET]" : ""));
                Console.WriteLine($"  strip {strip.Index,3} (band {strip.Band,3}, {strip.Poses.Count,2} poses" +
                                  (strip.Rows > 1 ? $", {strip.Rows} rows" : "") + "): " +
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
