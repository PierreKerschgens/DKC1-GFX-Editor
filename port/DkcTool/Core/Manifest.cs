using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>Refusal taxonomy for a batch manifest (specs/m4-batch-spec.md C.2). Like
    /// <see cref="ImportErrorCode"/>, every code is raised before any plan entry is produced --
    /// resolving a manifest either succeeds completely or changes nothing.</summary>
    public enum ManifestErrorCode
    {
        /// <summary>A strip entry names both `animation` and `indices`. Exactly one is required --
        /// this is a refusal, not a precedence rule to remember (C.2).</summary>
        BothAnimationAndIndices,
        /// <summary>A strip entry names neither `animation` nor `indices`.</summary>
        NeitherAnimationNorIndices,
        /// <summary>A strip entry names a strip number the slicer did not produce.</summary>
        StripNotFound,
        /// <summary>`animation` names an id outside the 440-entry table (A.7).</summary>
        AnimationNotFound,
        /// <summary>`animation` named a valid id but its script failed to parse.</summary>
        AnimationParseFailed,
        /// <summary>Resolved target-index count != the strip's actual pose count. The PRD's risk
        /// table says a wrong pose&lt;-&gt;frame mapping means the wrong sprite gets replaced, so
        /// this is always a refusal, never a best-effort zip (C.2).</summary>
        LengthMismatch,
        /// <summary>Two poses in one strip resolve to the same image index but are not
        /// pixel-identical -- the second would silently overwrite the first's import.</summary>
        DuplicateIndexDiffers,
        /// <summary>An `overrides` entry targets a strip the slicer did not produce.</summary>
        OverrideTargetsUnknownStrip,
        /// <summary>`poses` is malformed, inverted, or names a position the strip does not have.</summary>
        BadPoseRange,
    }

    public sealed class ManifestException : Exception
    {
        public ManifestErrorCode Code { get; }
        public ManifestException(ManifestErrorCode code, string message) : base(message) { Code = code; }
    }

    public sealed class ManifestOverride
    {
        public int Strip;
        public int Pose;
        public int RectX, RectY, RectW, RectH;
    }

    public sealed class ManifestStripEntry
    {
        public int Strip;
        public string? Name;
        public string? Animation;
        public List<string>? Indices;

        /// <summary>
        /// Per-strip override for horizontal placement (spec A.20/A.22). Null = follow the
        /// command line's `--flat-x`.
        ///
        /// This belongs to the *strip*, not the run, because two animations in one batch can want
        /// opposite answers: DK's walk matches stock exactly with per-pose centring (4px of
        /// inherited travel, which is real), while his run and roll inherit 9px and 15px of the
        /// replaced animation's lunge and need it flattened. A single command-line flag silently
        /// regresses whichever strip disagrees with it.
        /// </summary>
        public bool? FlatX;

        /// <summary>Per-strip floor override; "self" parses to -1, meaning anchor to this strip's
        /// own first slot. Null = inherit the manifest's <see cref="Manifest.GroundRef"/>.</summary>
        public int? GroundRef;

        /// <summary>
        /// Horizontal nudge applied to every pose in this strip, after all other placement.
        ///
        /// Exists because aligning two strips on a shared centre line does not align the thing a
        /// player actually watches: the **head**. Both the bbox centre and the centroid average in
        /// an outstretched arm, so a run whose arm reaches forward can share a centre line with the
        /// walk while its head sits several px further back. Measured on this sheet, the imported
        /// head stepped 2.6 px *backwards* on run->walk where stock's steps 4.2 px forwards -- read
        /// in play as DK's head snapping left. `--baseline`'s HEAD X column is how you size it.
        /// </summary>
        public int? OffsetX;

        /// <summary>
        /// Which of the strip's poses take part, in order — resolved from the `poses` field's
        /// tokens (`"0..10"`, `"0,0,1,1,2"`). Null = all of them, in order.
        ///
        /// <para>Exists so `animation` stays usable when a sheet strip covers <i>more</i> than the
        /// animation does. The sheet's Roll strip has 14 poses, of which the first 11 are the
        /// tumble and the last three are DK standing back up; anim 24 is only the tumble, 11
        /// frames. Without this the strip could only be imported via explicit `indices`, which
        /// zips blind and disables the length check — the exact hole that let the Roll ship
        /// mis-mapped (A.26). With it, `animation` + `poses` keeps the check: 11 selected poses
        /// against 11 derived indices, and a wrong range still refuses.</para>
        ///
        /// <para>It also covers the opposite shape — a strip with <i>fewer</i> poses than its
        /// animation has frames — by repeating a pose, which is why this is a list and not a
        /// range. See <c>ParsePoseList</c> for when that is and is not legitimate.</para>
        /// </summary>
        public List<int>? Poses;
    }

    /// <summary>One resolved (sheet rect -> target image index) instruction, ready for
    /// <see cref="PoseLoader.LoadRegion"/> + <see cref="SpriteImporter.Import"/>
    /// (specs/m4-batch-spec.md C.2/C.3).</summary>
    public sealed class PlannedPose
    {
        public int Strip;
        public int Position;
        public int ImageIndex;
        public int RectX, RectY, RectW, RectH;

        /// <summary>Per-strip horizontal-placement override; null = use the batch default.</summary>
        public bool? FlatX;

        /// <summary>Shared-floor index for this pose's strip; -1 = anchor to the strip's own slot,
        /// null = no shared floor (M4 behaviour).</summary>
        public int? GroundRef;

        /// <summary>Per-strip horizontal nudge, applied after all other placement.</summary>
        public int? OffsetX;
    }

    /// <summary>
    /// Parses and resolves an M4 batch manifest (specs/m4-batch-spec.md C.2): a sheet plus a
    /// strip -&gt; target-indices mapping, with explicit rect overrides for the slicer's ~4%
    /// mis-merges (A.3). Every rule here is a refusal, never a guess -- the same discipline M2b's
    /// importer already applies per-pose, just one level up.
    /// </summary>
    public sealed class Manifest
    {
        public int Version;
        public string Sheet = "";
        public string Palette = "";
        public List<ManifestStripEntry> Strips = new List<ManifestStripEntry>();
        public List<ManifestOverride> Overrides = new List<ManifestOverride>();

        /// <summary>
        /// Shared floor for every strip: the image index whose opaque bottom all strips are placed
        /// against. Null = each strip anchors to its own first target slot, which is the M4
        /// behaviour and is wrong for a multi-animation manifest.
        ///
        /// Without this, two strips land on two different ground lines -- DK's imported walk sat
        /// 2-3 px below his run, so his feet stepped up as he accelerated (handoff, "cross-strip
        /// ground alignment"). Per-strip anchoring cannot see the problem because it exists
        /// *between* strips: the same wrong-level mistake as A.17, one level further out.
        ///
        /// A strip may opt out with its own `groundRef` (its index, or "self") when it legitimately
        /// sits at a different height -- swimming and rope work are not standing on the floor.
        /// </summary>
        public int? GroundRef;

        /// <summary>Parses a groundRef field: a hex image index, or "self" (-1) meaning the
        /// strip anchors to its own first slot rather than a shared floor.</summary>
        private static int? ParseGroundRef(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.Trim().Equals("self", StringComparison.OrdinalIgnoreCase)) return -1;
            return Convert.ToInt32(raw.Trim(), 16);
        }

        /// <summary>
        /// Parses a `poses` field into the explicit list of slicer pose positions that take part,
        /// in order. Comma-separated tokens, each either a single position or an inclusive
        /// `lo..hi` range: `"0..10"`, `"0,0,1,1,2"`, `"0..4,4,5..10"`.
        ///
        /// <para>Decimal, not hex, because that is how the slicer and `--slice` number poses —
        /// unlike `animation`, which is hex. Yes, that is inconsistent; `animation` refuses
        /// anything without a `0x` so the two cannot be confused silently.</para>
        ///
        /// <para><b>Repeats are allowed and are the point.</b> A sheet strip can hold fewer poses
        /// than its animation has frames, and repeating one lets N poses cover M &gt; N frames by
        /// holding each a little longer — which needs no script editing and preserves the ROM's
        /// own timing exactly. That is not always right (it is wrong when the animation's extra
        /// frames are a distinct sub-motion rather than a slower version of the same one), so it
        /// is written out pose by pose in the manifest where a reader can see and argue with
        /// it.</para>
        /// </summary>
        private static List<int>? ParsePoseList(int strip, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var positions = new List<int>();
            foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim();
                if (t.Contains(".."))
                {
                    string[] halves = t.Split("..");
                    if (halves.Length != 2
                        || !int.TryParse(halves[0].Trim(), out int lo)
                        || !int.TryParse(halves[1].Trim(), out int hi))
                        throw new ManifestException(ManifestErrorCode.BadPoseRange,
                            $"strip {strip}: 'poses' range must be \"lo..hi\", got '{t}'.");
                    if (lo < 0 || hi < lo)
                        throw new ManifestException(ManifestErrorCode.BadPoseRange,
                            $"strip {strip}: 'poses' range {lo}..{hi} is empty or negative.");
                    for (int p = lo; p <= hi; p++) positions.Add(p);
                }
                else if (int.TryParse(t, out int single) && single >= 0)
                {
                    positions.Add(single);
                }
                else
                {
                    throw new ManifestException(ManifestErrorCode.BadPoseRange,
                        $"strip {strip}: 'poses' token '{t}' is not a position or a lo..hi range.");
                }
            }

            if (positions.Count == 0)
                throw new ManifestException(ManifestErrorCode.BadPoseRange,
                    $"strip {strip}: 'poses' selected nothing.");
            return positions;
        }

        public static Manifest Load(string path)
        {
            var doc = JsonSerializer.Deserialize<ManifestJson>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"could not parse manifest '{path}'.");

            var manifest = new Manifest
            {
                Version = doc.version,
                Sheet = doc.sheet ?? "",
                Palette = doc.palette ?? "",
                GroundRef = ParseGroundRef(doc.groundRef),
            };
            foreach (var s in doc.strips ?? new List<StripJson>())
            {
                manifest.Strips.Add(new ManifestStripEntry
                {
                    Strip = s.strip,
                    Name = s.name,
                    Animation = s.animation,
                    Indices = s.indices,
                    FlatX = s.flatX,
                    GroundRef = ParseGroundRef(s.groundRef),
                    OffsetX = s.offsetX,
                    Poses = ParsePoseList(s.strip, s.poses),
                });
            }
            foreach (var o in doc.overrides ?? new List<OverrideJson>())
            {
                if (o.rect == null || o.rect.Count != 4)
                    throw new InvalidOperationException(
                        $"override for strip {o.strip} pose {o.pose} needs a 4-element [x,y,w,h] rect.");
                manifest.Overrides.Add(new ManifestOverride
                {
                    Strip = o.strip,
                    Pose = o.pose,
                    RectX = o.rect[0],
                    RectY = o.rect[1],
                    RectW = o.rect[2],
                    RectH = o.rect[3],
                });
            }
            return manifest;
        }

        /// <summary>
        /// Resolves every named strip to a flat, ordered list of (sheet rect -&gt; image index)
        /// instructions. All-or-nothing: any rule violation throws before any instruction is
        /// returned, so a bad manifest never yields a partial plan.
        /// </summary>
        public List<PlannedPose> Resolve(Rom rom, SheetSlicer.SlicedSheet sheet, SKBitmap sheetBitmap)
        {
            var overridesByTarget = new Dictionary<(int Strip, int Pose), ManifestOverride>();
            foreach (var o in Overrides)
            {
                if (sheet.Strips.All(s => s.Index != o.Strip))
                    throw new ManifestException(ManifestErrorCode.OverrideTargetsUnknownStrip,
                        $"override targets strip {o.Strip}, which the slicer did not produce " +
                        $"({sheet.Strips.Count} strips exist).");
                overridesByTarget[(o.Strip, o.Pose)] = o;
            }

            var plan = new List<PlannedPose>();

            foreach (var entry in Strips)
            {
                bool hasAnimation = !string.IsNullOrEmpty(entry.Animation);
                bool hasIndices = entry.Indices != null && entry.Indices.Count > 0;

                if (hasAnimation && hasIndices)
                    throw new ManifestException(ManifestErrorCode.BothAnimationAndIndices,
                        $"strip {entry.Strip} names both 'animation' and 'indices' -- exactly one is required.");
                if (!hasAnimation && !hasIndices)
                    throw new ManifestException(ManifestErrorCode.NeitherAnimationNorIndices,
                        $"strip {entry.Strip} names neither 'animation' nor 'indices' -- exactly one is required.");

                var stripPoses = sheet.Strips.FirstOrDefault(s => s.Index == entry.Strip)
                    ?? throw new ManifestException(ManifestErrorCode.StripNotFound,
                        $"strip {entry.Strip} does not exist in the sliced sheet ({sheet.Strips.Count} strips exist).");

                List<int> targetIndices;
                if (hasAnimation)
                {
                    // Hex, and the "0x" must be written. Every other surface in this project names
                    // animations in decimal -- `--anims-in` prints "anim 24", the specs say
                    // "anim 24" -- while this field has always parsed as hex. A bare "24" here
                    // silently means anim 36. Refuse rather than guess: it is one character to
                    // add and a wrong animation is a wrong sprite replaced.
                    string animRaw = entry.Animation!.Trim();
                    if (!animRaw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        throw new ManifestException(ManifestErrorCode.AnimationNotFound,
                            $"strip {entry.Strip}: 'animation' is hex and must be written with a 0x " +
                            $"prefix, got '{animRaw}'. Tool output and the specs name animations in " +
                            $"decimal, so decimal {animRaw} is \"0x{Convert.ToInt32(animRaw, 10):X}\" here.");
                    int animId = Convert.ToInt32(animRaw, 16);
                    if (animId < 0 || animId >= AnimationTable.Count)
                        throw new ManifestException(ManifestErrorCode.AnimationNotFound,
                            $"strip {entry.Strip}: animation 0x{animId:X} is outside the table " +
                            $"(0..0x{AnimationTable.Count - 1:X}).");
                    var script = AnimationTable.Parse(rom, animId);
                    if (!script.Ok)
                        throw new ManifestException(ManifestErrorCode.AnimationParseFailed,
                            $"strip {entry.Strip}: animation 0x{animId:X} failed to parse: {script.Error}");
                    targetIndices = script.ImageIndices;
                }
                else
                {
                    targetIndices = entry.Indices!.Select(h => Convert.ToInt32(h, 16)).ToList();
                }

                // Which of the strip's poses take part. Without `poses` this is all of them, so
                // selected[i] == i and everything below behaves exactly as it did.
                var selected = entry.Poses ?? Enumerable.Range(0, stripPoses.Poses.Count).ToList();
                foreach (int p in selected)
                    if (p >= stripPoses.Poses.Count)
                        throw new ManifestException(ManifestErrorCode.BadPoseRange,
                            $"strip {entry.Strip}: 'poses' names pose {p} but the strip has " +
                            $"{stripPoses.Poses.Count} pose(s) (0..{stripPoses.Poses.Count - 1}).");

                if (targetIndices.Count != selected.Count)
                    throw new ManifestException(ManifestErrorCode.LengthMismatch,
                        $"strip {entry.Strip} has {selected.Count} pose(s)" +
                        (entry.Poses is null ? "" : $" selected (of {stripPoses.Poses.Count})") +
                        $" but resolved to {targetIndices.Count} target index(es) " +
                        (hasAnimation ? $"(animation 0x{entry.Animation})." : "(explicit indices)."));

                // Duplicate index within one strip: two poses would target the same slot, and the
                // second write would silently discard the first. Refuse unless the two source
                // regions are pixel-identical, in which case it's a harmless re-declaration.
                var positionsByIndex = new Dictionary<int, List<int>>();
                for (int i = 0; i < targetIndices.Count; i++)
                {
                    if (!positionsByIndex.TryGetValue(targetIndices[i], out var positions))
                        positionsByIndex[targetIndices[i]] = positions = new List<int>();
                    positions.Add(i);
                }
                foreach (var kv in positionsByIndex.Where(kv => kv.Value.Count > 1))
                {
                    var first = RectFor(stripPoses, selected[kv.Value[0]], overridesByTarget);
                    for (int n = 1; n < kv.Value.Count; n++)
                    {
                        var other = RectFor(stripPoses, selected[kv.Value[n]], overridesByTarget);
                        if (!SameRegion(sheetBitmap, first, other))
                            throw new ManifestException(ManifestErrorCode.DuplicateIndexDiffers,
                                $"strip {entry.Strip}: poses {kv.Value[0]} and {kv.Value[n]} both target " +
                                $"image index 0x{kv.Key:X} but are not pixel-identical.");
                    }
                }

                for (int i = 0; i < targetIndices.Count; i++)
                {
                    // Position is the *sheet* pose position, not the zip index, so reports and
                    // overrides keep naming the pose the slicer numbered. Identical when the
                    // whole strip is selected.
                    var rect = RectFor(stripPoses, selected[i], overridesByTarget);
                    plan.Add(new PlannedPose
                    {
                        Strip = entry.Strip,
                        Position = selected[i],
                        ImageIndex = targetIndices[i],
                        RectX = rect.X,
                        RectY = rect.Y,
                        RectW = rect.W,
                        RectH = rect.H,
                        FlatX = entry.FlatX,
                        GroundRef = entry.GroundRef ?? GroundRef,
                        OffsetX = entry.OffsetX,
                    });
                }
            }

            return plan;
        }

        private static (int X, int Y, int W, int H) RectFor(SheetSlicer.Strip strip, int position,
            Dictionary<(int, int), ManifestOverride> overrides)
        {
            if (overrides.TryGetValue((strip.Index, position), out var o))
                return (o.RectX, o.RectY, o.RectW, o.RectH);

            var p = strip.Poses[position];
            return (p.MinX, p.MinY, p.Width, p.Height);
        }

        private static bool SameRegion(SKBitmap bitmap, (int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b)
        {
            if (a.W != b.W || a.H != b.H) return false;
            for (int y = 0; y < a.H; y++)
                for (int x = 0; x < a.W; x++)
                    if (bitmap.GetPixel(a.X + x, a.Y + y) != bitmap.GetPixel(b.X + x, b.Y + y))
                        return false;
            return true;
        }

        private sealed class ManifestJson
        {
            public int version { get; set; }
            public string? sheet { get; set; }
            public string? palette { get; set; }
            public List<StripJson>? strips { get; set; }
            public List<OverrideJson>? overrides { get; set; }
            public string? groundRef { get; set; }
        }

        private sealed class StripJson
        {
            public int strip { get; set; }
            public string? name { get; set; }
            public string? animation { get; set; }
            public List<string>? indices { get; set; }
            public bool? flatX { get; set; }
            public string? groundRef { get; set; }
            public int? offsetX { get; set; }
            public string? poses { get; set; }
        }

        private sealed class OverrideJson
        {
            public int strip { get; set; }
            public int pose { get; set; }
            public List<int>? rect { get; set; }
        }
    }
}
