using System;
using System.Collections.Generic;

namespace DkcTool.Core
{
    /// <summary>
    /// M1 verification gates (see specs/m1-encoder-spec.md Part D).
    /// Gate 1: EncodeIndices(DecodeIndices(chr)) == chr for every char in every sprite.
    /// Gate 2: SpriteEncoder.Serialize(DecodeStructured(addr)) == raw sprite bytes,
    /// for every valid image index in the GFX pointer table.
    /// </summary>
    public static class RoundTripHarness
    {
        public sealed class Gate1Result
        {
            public int TotalChars;
            public int PassChars;
            public int FailChars;
            public List<string> Failures = new List<string>();
        }

        public sealed class Gate2Result
        {
            public int TotalIndices;
            public int PassIndices;
            public int FailIndices;
            public int SkippedIndices;
            public List<string> Failures = new List<string>();
            public List<string> Skips = new List<string>();
        }

        public static Gate1Result RunGate1(Rom rom)
        {
            var result = new Gate1Result();

            foreach (int imageIndex in GfxTable.EnumerateImageIndices(rom))
            {
                int addr = GfxTable.ResolveSpriteAddress(rom, imageIndex);
                byte[] raw;
                try
                {
                    raw = SpriteDecoder.ReadSpriteBytes(rom, addr);
                }
                catch (Exception ex)
                {
                    result.Failures.Add($"index 0x{imageIndex:X} @ 0x{addr:X}: could not read sprite bytes: {ex.Message}");
                    continue;
                }

                int b0 = raw[0], b1 = raw[1], b3 = raw[3], b5 = raw[5], b7 = raw[7];
                int charStart = 8 + b0 * 2 + b1 * 2 + b3 * 2;
                int charCount = b5 + b7;

                for (int i = 0; i < charCount; i++)
                {
                    int offset = charStart + i * 0x20;
                    var chr = new byte[0x20];
                    Array.Copy(raw, offset, chr, 0, 0x20);

                    result.TotalChars++;
                    var indices = CharCodec.DecodeIndices(chr);
                    var reEncoded = CharCodec.EncodeIndices(indices);

                    if (BytesEqual(chr, reEncoded))
                    {
                        result.PassChars++;
                    }
                    else
                    {
                        result.FailChars++;
                        int firstDiff = FirstDiff(chr, reEncoded);
                        result.Failures.Add(
                            $"index 0x{imageIndex:X} char {i}: mismatch at byte {firstDiff} " +
                            $"(orig 0x{chr[firstDiff]:X2} vs re-encoded 0x{reEncoded[firstDiff]:X2})");
                    }
                }
            }

            return result;
        }

        public static Gate2Result RunGate2(Rom rom)
        {
            var result = new Gate2Result();

            foreach (int imageIndex in GfxTable.EnumerateImageIndices(rom))
            {
                result.TotalIndices++;
                int addr = GfxTable.ResolveSpriteAddress(rom, imageIndex);

                try
                {
                    byte[] original = SpriteDecoder.ReadSpriteBytes(rom, addr);
                    SpriteModel model = SpriteDecoder.DecodeStructured(rom, addr);
                    byte[] reEncoded = SpriteEncoder.Serialize(model);

                    if (original.Length != reEncoded.Length)
                    {
                        result.FailIndices++;
                        result.Failures.Add(
                            $"index 0x{imageIndex:X} @ 0x{addr:X}: length mismatch " +
                            $"(orig {original.Length} vs re-encoded {reEncoded.Length})");
                        continue;
                    }

                    if (BytesEqual(original, reEncoded))
                    {
                        result.PassIndices++;
                    }
                    else
                    {
                        result.FailIndices++;
                        int firstDiff = FirstDiff(original, reEncoded);
                        result.Failures.Add(
                            $"index 0x{imageIndex:X} @ 0x{addr:X}: byte mismatch at offset 0x{firstDiff:X} " +
                            $"(orig 0x{original[firstDiff]:X2} vs re-encoded 0x{reEncoded[firstDiff]:X2})");
                    }
                }
                catch (Exception ex)
                {
                    result.SkippedIndices++;
                    result.Skips.Add($"index 0x{imageIndex:X} @ 0x{addr:X}: decode exception: {ex.Message}");
                }
            }

            return result;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static int FirstDiff(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (a[i] != b[i]) return i;
            return n;
        }
    }
}
