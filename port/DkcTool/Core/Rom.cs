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

        public static int Mask(int address) =>
            address & (address > 0x7fffff ? 0x3fffff : 0xffffff);

        public int Length => _data.Length;

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

        /// <summary>Deep copy; imports mutate the copy, never the loaded original.</summary>
        public Rom Clone() => new Rom((byte[])_data.Clone());

        public void Write8(int address, byte value)
        {
            address = Mask(address);
            AssertInBounds(address, 1);
            _data[address] = value;
        }

        /// <summary>Little-endian, 3 bytes (the SNES pointer width used throughout this codebase).</summary>
        public void Write24(int address, int value)
        {
            address = Mask(address);
            AssertInBounds(address, 3);
            _data[address] = (byte)(value & 0xFF);
            _data[address + 1] = (byte)((value >> 8) & 0xFF);
            _data[address + 2] = (byte)((value >> 16) & 0xFF);
        }

        public void WriteBytes(int address, byte[] src)
        {
            address = Mask(address);
            AssertInBounds(address, src.Length);
            Array.Copy(src, 0, _data, address, src.Length);
        }

        /// <summary>Full-buffer copy, for the V2b containment diff (byte-diff against a pre-import snapshot).</summary>
        public byte[] Snapshot() => (byte[])_data.Clone();

        /// <summary>Writes the current buffer to <paramref name="path"/>. The loaded file itself is never touched.</summary>
        public void Save(string path) => File.WriteAllBytes(path, _data);

        /// <summary>Re-syncs an expanded ROM's low-bank mirror against writes made since expansion,
        /// and no-ops on an unexpanded one. See <see cref="Expansion.RefreshMirror"/> for why a
        /// snapshot mirror plus a repointing importer silently loses updates.</summary>
        public void RefreshLowBankMirror() => Expansion.RefreshMirror(_data);

        private void AssertInBounds(int maskedAddress, int length)
        {
            if (maskedAddress < 0 || maskedAddress + length > _data.Length)
                throw new ArgumentOutOfRangeException(nameof(maskedAddress),
                    $"write of {length} byte(s) at masked address 0x{maskedAddress:X} exceeds ROM buffer (0x{_data.Length:X}).");
        }

        /// <summary>The 21-char internal ROM name at 0xFFC0 (SNES header).</summary>
        public string HeaderTitle =>
            Encoding.ASCII.GetString(_data, 0xFFC0, 21);

        public bool LooksLikeDkc1 =>
            HeaderTitle.StartsWith("DONKEY KONG COUNTRY") ||
            HeaderTitle.StartsWith("DKC Hack");
    }
}
