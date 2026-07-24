using System;
using System.Collections.Generic;
using SkiaSharp;

namespace DkcTool.Core
{
    /// <summary>
    /// M2a gate, real-sprite corpus: for every valid image index in the real ROM's
    /// GFX pointer table, decode the game's own sprite to a pixel-index canvas
    /// (identity palette), re-tile that exact pose through <see cref="SpriteTiler"/>,
    /// re-serialize, re-decode, and pixel-compare against the original decode.
    ///
    /// This is the corpus the M2 spec's V2 gate actually calls for: 2,714 real,
    /// game-authored occupied-cell shapes -- the kind of irregular pattern hand-made
    /// synthetic test poses don't reliably produce -- rather than a handful of
    /// synthetic cases exercising the tiler's branches in isolation.
    /// </summary>
    public static class M2aRomHarness
    {
        public sealed class Result
        {
            public int TotalIndices;
            public int PassIndices;
            public int FailIndices;
            public int EmptySkipped;
            public int ExceedsCharBudgetCount;
            public int ExceedsOamBudgetCount;
            public List<string> Failures = new List<string>();
        }

        public static Result Run(Rom rom)
        {
            var result = new Result();

            foreach (int imageIndex in GfxTable.EnumerateImageIndices(rom))
            {
                result.TotalIndices++;
                int addr = GfxTable.ResolveSpriteAddress(rom, imageIndex);

                int[,] original;
                try
                {
                    original = TilerHarness.DecodeIndexCanvas(rom, addr);
                }
                catch (Exception ex)
                {
                    result.FailIndices++;
                    result.Failures.Add($"index 0x{imageIndex:X} @ 0x{addr:X}: decode failed: {ex.Message}");
                    continue;
                }

                int height = original.GetLength(0), width = original.GetLength(1);
                int minX = width, minY = height, maxX = -1, maxY = -1;
                for (int r = 0; r < height; r++)
                {
                    for (int c = 0; c < width; c++)
                    {
                        if (original[r, c] == 0) continue;
                        if (c < minX) minX = c;
                        if (c > maxX) maxX = c;
                        if (r < minY) minY = r;
                        if (r > maxY) maxY = r;
                    }
                }

                if (maxX < 0)
                {
                    result.EmptySkipped++; // fully transparent sprite -- nothing to tile
                    continue;
                }

                int w = maxX - minX + 1, h = maxY - minY + 1;
                var pose = new int[h, w];
                for (int r = 0; r < h; r++)
                    for (int c = 0; c < w; c++)
                        pose[r, c] = original[minY + r, minX + c];

                var tilerResult = SpriteTiler.Build(pose, minX, minY);
                if (tilerResult.ExceedsCharBudget) result.ExceedsCharBudgetCount++;
                if (tilerResult.ExceedsOamBudget) result.ExceedsOamBudgetCount++;

                byte[] serialized = SpriteEncoder.Serialize(tilerResult.Model);
                var synthRom = new Rom(serialized);

                int[,] reDecoded;
                try
                {
                    reDecoded = TilerHarness.DecodeIndexCanvas(synthRom, 0);
                }
                catch (Exception ex)
                {
                    result.FailIndices++;
                    result.Failures.Add($"index 0x{imageIndex:X} @ 0x{addr:X}: re-decode failed: {ex.Message}");
                    continue;
                }

                string? mismatch = null;
                for (int r = 0; r < height && mismatch == null; r++)
                {
                    for (int c = 0; c < width; c++)
                    {
                        if (original[r, c] != reDecoded[r, c])
                        {
                            mismatch = $"pixel ({r},{c}): expected {original[r, c]}, got {reDecoded[r, c]}";
                            break;
                        }
                    }
                }

                if (mismatch == null)
                {
                    result.PassIndices++;
                }
                else
                {
                    result.FailIndices++;
                    result.Failures.Add($"index 0x{imageIndex:X} @ 0x{addr:X}: {mismatch}");
                }
            }

            return result;
        }
    }
}
