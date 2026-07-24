using System.Collections.Generic;
using System.Linq;

namespace DkcTool.Core
{
    /// <summary>
    /// M2a: turns a palette-index pixel grid into a <see cref="SpriteModel"/> (no ROM
    /// write). Implements the canonical single-DMA-group scheme from
    /// specs/m2-importer-spec.md Part C: baseline all-1x1, with a 2x2 refinement to
    /// save OAM entries when the occupied-cell count exceeds the OAM budget.
    ///
    /// Safety note (verified empirically, see M2 implementation notes): the spec's
    /// general "b2 = (R&lt;&lt;4)|(2*(m%8))" formula collides with 2x2 BL/BR chars
    /// when the last VRAM row-pair is only partially filled and the 1x1 walk needs
    /// to wrap past it. This tiler sidesteps that entirely by only ever converting
    /// 2x2 blocks in whole groups of 8 (whole row-pairs) -- so 1x1 always starts on
    /// a completely fresh row (b2 = 4*m). Any qualifying blocks beyond the last
    /// multiple of 8 are left as plain 1x1 cells; this costs at most 7 blocks' worth
    /// of OAM savings and is always collision-free.
    /// </summary>
    public static class SpriteTiler
    {
        /// <summary>Hard cap on distinct chars (b5+b7), evidence-based from --stats over the real ROM.</summary>
        public const int MaxChars = 88;

        /// <summary>Soft cap on hardware OAM entries (b0+b1+b3); only matters in-game.</summary>
        public const int MaxOamEntries = 37;

        public sealed class Result
        {
            public SpriteModel Model = null!;
            public int CharCount;
            public int OamEntries;
            public bool ExceedsCharBudget;
            public bool ExceedsOamBudget;
        }

        /// <summary>
        /// Builds a SpriteModel for a palette-index pixel grid (0 = transparent).
        /// `originX/originY` is the canvas top-left the tiles are placed relative to
        /// (see M2 spec Part C, Q4: normally the replaced slot's original bbox top-left).
        /// </summary>
        public static Result Build(int[,] pixels, int originX, int originY)
        {
            int h = pixels.GetLength(0);
            int w = pixels.GetLength(1);
            int cellRows = (h + 7) / 8;
            int cellCols = (w + 7) / 8;

            var cells = new int[cellRows, cellCols][,];
            var occupied = new bool[cellRows, cellCols];
            for (int cr = 0; cr < cellRows; cr++)
            {
                for (int cc = 0; cc < cellCols; cc++)
                {
                    var block = ExtractCell(pixels, cr, cc, h, w);
                    cells[cr, cc] = block;
                    occupied[cr, cc] = AnyNonZero(block);
                }
            }

            int totalOccupied = 0;
            for (int cr = 0; cr < cellRows; cr++)
                for (int cc = 0; cc < cellCols; cc++)
                    if (occupied[cr, cc]) totalOccupied++;

            // Qualifying 2x2 blocks: aligned (even row, even col) cell pairs whose
            // four 8x8 sub-cells are all occupied. Scanned row-major for determinism.
            var qualifying = new List<(int cr, int cc)>();
            for (int cr = 0; cr + 1 < cellRows; cr += 2)
            {
                for (int cc = 0; cc + 1 < cellCols; cc += 2)
                {
                    if (occupied[cr, cc] && occupied[cr, cc + 1] &&
                        occupied[cr + 1, cc] && occupied[cr + 1, cc + 1])
                    {
                        qualifying.Add((cr, cc));
                    }
                }
            }

            List<(int cr, int cc)> chosen2x2 = new List<(int, int)>();
            if (totalOccupied > MaxOamEntries)
            {
                int usable = (qualifying.Count / 8) * 8; // whole row-pairs only (see class doc)
                chosen2x2 = qualifying.Take(usable).ToList();
            }

            var consumed = new bool[cellRows, cellCols];
            foreach (var (cr, cc) in chosen2x2)
            {
                consumed[cr, cc] = consumed[cr, cc + 1] = consumed[cr + 1, cc] = consumed[cr + 1, cc + 1] = true;
            }

            var model = new SpriteModel();

            foreach (var (cr, cc) in chosen2x2)
            {
                int x = originX + cc * 8, y = originY + cr * 8;
                model.Placements.Add(new Placement(x, y, TileType.TwoByTwo));
            }

            // The VRAM row-pair layout interleaves entries, not chars-per-entry: for
            // a full group of 8 (one row-pair), row R holds every entry's TL,TR (in
            // entry order) and row R+1 holds every entry's BL,BR (in entry order) --
            // NOT [TL0,TR0,BL0,BR0, TL1,TR1,BL1,BR1, ...]. `chosen2x2.Count` is always
            // a multiple of 8 by construction (see comment above), so this grouping
            // is exact with no remainder to handle.
            for (int g = 0; g < chosen2x2.Count; g += 8)
            {
                for (int n = g; n < g + 8; n++)
                {
                    var (cr, cc) = chosen2x2[n];
                    model.Chars.Add(cells[cr, cc]);     // TL
                    model.Chars.Add(cells[cr, cc + 1]); // TR
                }
                for (int n = g; n < g + 8; n++)
                {
                    var (cr, cc) = chosen2x2[n];
                    model.Chars.Add(cells[cr + 1, cc]);     // BL
                    model.Chars.Add(cells[cr + 1, cc + 1]); // BR
                }
            }

            int oneByOneCount = 0;
            for (int cr = 0; cr < cellRows; cr++)
            {
                for (int cc = 0; cc < cellCols; cc++)
                {
                    if (!occupied[cr, cc] || consumed[cr, cc]) continue;
                    oneByOneCount++;
                    int x = originX + cc * 8, y = originY + cr * 8;
                    model.Placements.Add(new Placement(x, y, TileType.OneByOne));
                    model.Chars.Add(cells[cr, cc]);
                }
            }

            int b0 = chosen2x2.Count;
            int b1 = oneByOneCount;
            int b2 = 4 * b0; // safe: always a fresh row since b0 is a multiple of 8
            int b5 = 4 * b0 + b1;

            model.Header = new byte[]
            {
                (byte)b0, (byte)b1, (byte)b2, 0, 0, (byte)b5, 0, 0,
            };

            int oam = b0 + b1;
            return new Result
            {
                Model = model,
                CharCount = b5,
                OamEntries = oam,
                ExceedsCharBudget = b5 > MaxChars,
                ExceedsOamBudget = oam > MaxOamEntries,
            };
        }

        private static int[,] ExtractCell(int[,] pixels, int cr, int cc, int h, int w)
        {
            var block = new int[8, 8];
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int pr = cr * 8 + r, pc = cc * 8 + c;
                    block[r, c] = (pr < h && pc < w) ? pixels[pr, pc] : 0;
                }
            }
            return block;
        }

        private static bool AnyNonZero(int[,] block)
        {
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if (block[r, c] != 0) return true;
            return false;
        }
    }
}
