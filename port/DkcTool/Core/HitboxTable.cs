namespace DkcTool.Core
{
    /// <summary>
    /// Read-only port of Form1.HitboxAndZoom.cs's LoadHitbox. The collision box lives in a
    /// table separate from the sprite/GFX one: a 2-byte pointer at <see cref="PointerTableBase"/>
    /// + imageIndex/2 (integer division, used directly as a byte address), resolving to an
    /// 8-byte x,y,w,h record (four 16-bit fields) at <see cref="RecordBase"/> + pointer.
    ///
    /// M2b does not write this table (specs/m2b-writer-spec.md Part G) -- a re-posed frame
    /// keeps the old pose's collision box. This exists only so the drift report can show the
    /// current hitbox next to the new pose's bbox, making the staleness visible.
    /// </summary>
    public static class HitboxTable
    {
        public const int PointerTableBase = 0xbb8000;
        public const int RecordBase = 0xbb0000;

        public struct Record
        {
            public int PointerAddress;
            public int Pointer;
            public int RecordAddress;
            public int X, Y, Width, Height;

            /// <summary>X/Y are signed 16-bit offsets from sprite center (ROM.cs ConvertToSNESInt).</summary>
            public int SignedX => ToSigned16(X);
            public int SignedY => ToSigned16(Y);

            private static int ToSigned16(int v) => v >= 0x8000 ? (0x10000 - v) * -1 : v;
        }

        public static Record Read(Rom rom, int imageIndex)
        {
            int pointerAddress = PointerTableBase + imageIndex / 2;
            int pointer = rom.Read16(pointerAddress);
            int recordAddress = RecordBase + pointer;

            return new Record
            {
                PointerAddress = pointerAddress,
                Pointer = pointer,
                RecordAddress = recordAddress,
                X = rom.Read16(recordAddress + 0),
                Y = rom.Read16(recordAddress + 2),
                Width = rom.Read16(recordAddress + 4),
                Height = rom.Read16(recordAddress + 6),
            };
        }
    }
}
