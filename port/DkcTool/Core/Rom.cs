using System;
using System.IO;
using System.Text;

namespace DkcTool.Core
{
    /// <summary>
    /// Cross-platform port of the byte-access core from the original ROM.cs.
    /// Reads are non-mutating (no ref-address bookkeeping) and every access goes
    /// through <see cref="Mask"/>, which reproduces the original SNES bank -> file
    /// offset convention:  address &amp; (address > 0x7fffff ? 0x3fffff : 0xffffff).
    /// </summary>
    public sealed class Rom
    {
        private readonly byte[] _data;

        public Rom(byte[] data) => _data = data;

        public static Rom Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // Strip the 0x200-byte copier header if present (matches original: length 0x400200).
            if (bytes.Length == 0x400200)
                bytes = bytes[0x200..];
            return new Rom(bytes);
        }

        private static int Mask(int address) =>
            address & (address > 0x7fffff ? 0x3fffff : 0xffffff);

        public byte Read8(int address) => _data[Mask(address)];

        public int Read16(int address)
        {
            address = Mask(address);
            return _data[address] | (_data[address + 1] << 8);
        }

        public int Read24(int address)
        {
            address = Mask(address);
            return _data[address]
                 | (_data[address + 1] << 8)
                 | (_data[address + 2] << 16);
        }

        public byte[] ReadBytes(int address, int count)
        {
            address = Mask(address);
            var result = new byte[count];
            Array.Copy(_data, address, result, 0, count);
            return result;
        }

        /// <summary>The 21-char internal ROM name at 0xFFC0 (SNES header).</summary>
        public string HeaderTitle =>
            Encoding.ASCII.GetString(_data, 0xFFC0, 21);

        public bool LooksLikeDkc1 =>
            HeaderTitle.StartsWith("DONKEY KONG COUNTRY") ||
            HeaderTitle.StartsWith("DKC Hack");
    }
}
