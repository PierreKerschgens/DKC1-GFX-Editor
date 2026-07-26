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
                    int animId = Convert.ToInt32(entry.Animation, 16);
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

                if (targetIndices.Count != stripPoses.Poses.Count)
                    throw new ManifestException(ManifestErrorCode.LengthMismatch,
                        $"strip {entry.Strip} has {stripPoses.Poses.Count} pose(s) but resolved to " +
                        $"{targetIndices.Count} target index(es) " +
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
                    var first = RectFor(stripPoses, kv.Value[0], overridesByTarget);
                    for (int n = 1; n < kv.Value.Count; n++)
                    {
                        var other = RectFor(stripPoses, kv.Value[n], overridesByTarget);
                        if (!SameRegion(sheetBitmap, first, other))
                            throw new ManifestException(ManifestErrorCode.DuplicateIndexDiffers,
                                $"strip {entry.Strip}: poses {kv.Value[0]} and {kv.Value[n]} both target " +
                                $"image index 0x{kv.Key:X} but are not pixel-identical.");
                    }
                }

                for (int i = 0; i < targetIndices.Count; i++)
                {
                    var rect = RectFor(stripPoses, i, overridesByTarget);
                    plan.Add(new PlannedPose
                    {
                        Strip = entry.Strip,
                        Position = i,
                        ImageIndex = targetIndices[i],
                        RectX = rect.X,
                        RectY = rect.Y,
                        RectW = rect.W,
                        RectH = rect.H,
                        FlatX = entry.FlatX,
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
        }

        private sealed class StripJson
        {
            public int strip { get; set; }
            public string? name { get; set; }
            public string? animation { get; set; }
            public List<string>? indices { get; set; }
            public bool? flatX { get; set; }
        }

        private sealed class OverrideJson
        {
            public int strip { get; set; }
            public int pose { get; set; }
            public List<int>? rect { get; set; }
        }
    }
}
