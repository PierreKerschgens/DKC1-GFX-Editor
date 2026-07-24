using System.IO;

namespace DkcTool.Core
{
    /// <summary>
    /// Exact inverse of <see cref="SpriteDecoder.DecodeStructured"/>: given a
    /// <see cref="SpriteModel"/>, re-emits the header, placement table, and char
    /// data in file order. Byte-exactness with the source bytes is the M1 gate.
    /// </summary>
    public static class SpriteEncoder
    {
        public static byte[] Serialize(SpriteModel model)
        {
            using var ms = new MemoryStream();
            ms.Write(model.Header, 0, 8);

            foreach (var pl in model.Placements)
            {
                ms.WriteByte((byte)pl.X);
                ms.WriteByte((byte)pl.Y);
            }

            foreach (var chr in model.Chars)
            {
                byte[] bytes = CharCodec.EncodeIndices(chr);
                ms.Write(bytes, 0, bytes.Length);
            }

            return ms.ToArray();
        }
    }
}
