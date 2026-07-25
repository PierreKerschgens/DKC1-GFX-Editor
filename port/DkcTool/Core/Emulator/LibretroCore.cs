using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// Minimal headless libretro host (specs/v3-emulator-spec.md). Loads a core .dylib/.so,
    /// wires the six callbacks the API requires, boots a ROM, and runs frames -- no window, no
    /// GL context, no audio device. Software-rendered SNES cores (snes9x, bsnes) hand their
    /// framebuffer straight to the video_refresh callback, which is all the harness needs.
    ///
    /// Only the slice of the libretro API this harness uses is bound. Anything a core asks for
    /// via the environment callback that we don't implement returns false, which cores are
    /// required to tolerate.
    /// </summary>
    public sealed class LibretroCore : IDisposable
    {
        // --- libretro ABI ---------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool EnvironmentDelegate(uint cmd, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VideoRefreshDelegate(IntPtr data, uint width, uint height, UIntPtr pitch);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void AudioSampleDelegate(short left, short right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr AudioSampleBatchDelegate(IntPtr data, UIntPtr frames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InputPollDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate short InputStateDelegate(uint port, uint device, uint index, uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint UintFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetCallbackFn(IntPtr cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool LoadGameFn(ref RetroGameInfo game);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetAvInfoFn(out RetroSystemAvInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetSystemInfoFn(out RetroSystemInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr SerializeSizeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SerializeFn(IntPtr data, UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool UnserializeFn(IntPtr data, UIntPtr size);

        [StructLayout(LayoutKind.Sequential)]
        private struct RetroGameInfo
        {
            public IntPtr Path;
            public IntPtr Data;
            public UIntPtr Size;
            public IntPtr Meta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RetroSystemInfo
        {
            public IntPtr LibraryName;
            public IntPtr LibraryVersion;
            public IntPtr ValidExtensions;
            [MarshalAs(UnmanagedType.U1)] public bool NeedFullPath;
            [MarshalAs(UnmanagedType.U1)] public bool BlockExtract;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RetroGameGeometry
        {
            public uint BaseWidth, BaseHeight, MaxWidth, MaxHeight;
            public float AspectRatio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RetroSystemTiming
        {
            public double Fps, SampleRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RetroSystemAvInfo
        {
            public RetroGameGeometry Geometry;
            public RetroSystemTiming Timing;
        }

        // Environment commands this host answers. Everything else returns false.
        private const uint EnvGetOverscan = 2;
        private const uint EnvGetCanDupe = 3;
        private const uint EnvSetPixelFormat = 10;
        private const uint EnvGetSystemDirectory = 9;
        private const uint EnvGetVariable = 15;
        private const uint EnvSetVariables = 16;
        private const uint EnvGetVariableUpdate = 17;
        private const uint EnvGetLibretroPath = 19;
        private const uint EnvGetCoreAssetsDirectory = 30;
        private const uint EnvGetSaveDirectory = 31;
        private const uint EnvSetGeometry = 37;

        public enum PixelFormat { ZeroRgb1555 = 0, Xrgb8888 = 1, Rgb565 = 2 }

        // --- state ----------------------------------------------------------------------

        private readonly IntPtr _handle;
        private readonly string _systemDirectory;
        private IntPtr _systemDirPtr, _corePathPtr;

        // Delegates must be rooted: the core keeps raw function pointers to them, and a
        // collected delegate is a crash the GC schedules for you at an arbitrary later frame.
        private readonly List<Delegate> _rooted = new List<Delegate>();

        /// <summary>Native copies of option values handed to the core; they must stay alive for
        /// as long as the core might read them, so they are freed only on Dispose.</summary>
        private readonly Dictionary<string, IntPtr> _optionStrings = new Dictionary<string, IntPtr>();

        private readonly VoidFn _retroInit, _retroDeinit, _retroRun;
        private readonly LoadGameFn _retroLoadGame;
        private readonly VoidFn _retroUnloadGame;
        private readonly GetAvInfoFn _retroGetAvInfo;
        private readonly SerializeSizeFn _retroSerializeSize;
        private readonly SerializeFn _retroSerialize;
        private readonly UnserializeFn _retroUnserialize;

        private bool _gameLoaded, _disposed;
        private string? _tempRomPath;

        /// <summary>Core identity from retro_get_system_info, e.g. "bsnes-mercury Balanced 0.94".</summary>
        public string LibraryName { get; }
        public string LibraryVersion { get; }

        /// <summary>Some cores (notably the modern bsnes) will not accept a ROM handed over in
        /// memory and need a real file on disk. Loading such a core from memory "succeeds" and
        /// then renders a black screen forever, so this is honoured rather than assumed away.</summary>
        public bool NeedsFullPath { get; }

        /// <summary>Most recent frame handed to video_refresh, as 32-bit BGRA (Skia's layout).</summary>
        public byte[]? FrameBuffer { get; private set; }
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public PixelFormat CorePixelFormat { get; private set; } = PixelFormat.ZeroRgb1555;

        /// <summary>Frames the core has produced (a duped frame still counts).</summary>
        public long FramesRun { get; private set; }

        /// <summary>Row stride in bytes from the last video_refresh, for diagnosing geometry
        /// mismatches (a pitch wider than width * bytes-per-pixel means the core is rendering
        /// into a larger buffer than it reports).</summary>
        public int LastPitch { get; private set; }

        /// <summary>Every distinct (width, height, pitch) the core has reported, in first-seen
        /// order. A core that changes geometry mid-run (hi-res intro -> normal gameplay) is the
        /// case most likely to break framebuffer conversion, so it is recorded rather than guessed.</summary>
        public List<(int Width, int Height, int Pitch)> GeometryLog { get; } =
            new List<(int, int, int)>();

        /// <summary>Environment commands this host answered "false" to, with hit counts. A core
        /// asking repeatedly for something unimplemented is the first place to look when it boots
        /// but renders wrongly.</summary>
        public Dictionary<uint, int> UnhandledEnvironment { get; } = new Dictionary<uint, int>();

        /// <summary>Core options declared via SET_VARIABLES, mapped to their default (first) value.
        /// A frontend that answers GET_VARIABLE with nothing leaves some cores running on
        /// uninitialised option state, so the declared defaults are served back.</summary>
        public Dictionary<string, string> CoreOptions { get; } = new Dictionary<string, string>();

        /// <summary>Buttons held for the next <see cref="RunFrame"/>, keyed by RETRO_DEVICE_ID_JOYPAD_*.</summary>
        public HashSet<int> HeldButtons { get; } = new HashSet<int>();

        public LibretroCore(string corePath)
        {
            if (!File.Exists(corePath))
                throw new FileNotFoundException(
                    $"libretro core not found: {corePath}. Run port/emu/fetch-cores.sh first.", corePath);

            _handle = NativeLibrary.Load(Path.GetFullPath(corePath));
            _systemDirectory = Path.GetDirectoryName(Path.GetFullPath(corePath)) ?? ".";
            _systemDirPtr = Marshal.StringToHGlobalAnsi(_systemDirectory);
            _corePathPtr = Marshal.StringToHGlobalAnsi(Path.GetFullPath(corePath));

            T Bind<T>(string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_handle, name));

            uint apiVersion = Bind<UintFn>("retro_api_version")();
            if (apiVersion != 1)
                throw new InvalidOperationException($"unsupported libretro API version {apiVersion} (expected 1).");

            _retroInit = Bind<VoidFn>("retro_init");
            _retroDeinit = Bind<VoidFn>("retro_deinit");
            _retroRun = Bind<VoidFn>("retro_run");
            _retroLoadGame = Bind<LoadGameFn>("retro_load_game");
            _retroUnloadGame = Bind<VoidFn>("retro_unload_game");
            _retroGetAvInfo = Bind<GetAvInfoFn>("retro_get_system_av_info");
            _retroSerializeSize = Bind<SerializeSizeFn>("retro_serialize_size");
            _retroSerialize = Bind<SerializeFn>("retro_serialize");
            _retroUnserialize = Bind<UnserializeFn>("retro_unserialize");

            Bind<GetSystemInfoFn>("retro_get_system_info")(out var sysInfo);
            LibraryName = Marshal.PtrToStringAnsi(sysInfo.LibraryName) ?? "?";
            LibraryVersion = Marshal.PtrToStringAnsi(sysInfo.LibraryVersion) ?? "?";
            NeedsFullPath = sysInfo.NeedFullPath;

            // Callbacks must be registered before retro_init: cores read the environment
            // callback during init to negotiate pixel format and options.
            Register<EnvironmentDelegate>("retro_set_environment", OnEnvironment);
            Register<VideoRefreshDelegate>("retro_set_video_refresh", OnVideoRefresh);
            Register<AudioSampleDelegate>("retro_set_audio_sample", (l, r) => { });
            Register<AudioSampleBatchDelegate>("retro_set_audio_sample_batch", (data, frames) => frames);
            Register<InputPollDelegate>("retro_set_input_poll", () => { });
            Register<InputStateDelegate>("retro_set_input_state", OnInputState);

            _retroInit();
        }

        private void Register<T>(string setter, T callback) where T : Delegate
        {
            _rooted.Add(callback);
            Marshal.GetDelegateForFunctionPointer<SetCallbackFn>(NativeLibrary.GetExport(_handle, setter))(
                Marshal.GetFunctionPointerForDelegate(callback));
        }

        private bool OnEnvironment(uint cmd, IntPtr data)
        {
            switch (cmd)
            {
                case EnvGetCanDupe:
                    // "Yes, a null frame means 'same as last'." Required by most cores.
                    if (data != IntPtr.Zero) Marshal.WriteByte(data, 1);
                    return true;

                case EnvGetOverscan:
                    if (data != IntPtr.Zero) Marshal.WriteByte(data, 0);
                    return true;

                case EnvSetPixelFormat:
                    if (data == IntPtr.Zero) return false;
                    var requested = (PixelFormat)Marshal.ReadInt32(data);
                    if (requested != PixelFormat.Rgb565 && requested != PixelFormat.Xrgb8888 &&
                        requested != PixelFormat.ZeroRgb1555)
                        return false;
                    CorePixelFormat = requested;
                    return true;

                case EnvGetSystemDirectory:
                case EnvGetCoreAssetsDirectory:
                case EnvGetSaveDirectory:
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _systemDirPtr);
                    return true;

                case EnvGetLibretroPath:
                    if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _corePathPtr);
                    return true;

                case EnvSetVariables:
                {
                    // Array of { const char *key; const char *value; } terminated by a null key.
                    // `value` is "Description; default|other|other" -- the first option is the
                    // default, which is what a frontend is expected to serve back.
                    if (data == IntPtr.Zero) return false;
                    int entry = 0;
                    while (true)
                    {
                        IntPtr keyPtr = Marshal.ReadIntPtr(data, entry * IntPtr.Size * 2);
                        if (keyPtr == IntPtr.Zero) break;
                        IntPtr valPtr = Marshal.ReadIntPtr(data, entry * IntPtr.Size * 2 + IntPtr.Size);
                        string? key = Marshal.PtrToStringAnsi(keyPtr);
                        string? spec = Marshal.PtrToStringAnsi(valPtr);
                        if (key != null && spec != null)
                        {
                            int semi = spec.IndexOf(';');
                            string values = semi >= 0 ? spec[(semi + 1)..].Trim() : spec;
                            int bar = values.IndexOf('|');
                            CoreOptions[key] = (bar >= 0 ? values[..bar] : values).Trim();
                        }
                        entry++;
                        if (entry > 512) break; // malformed list guard
                    }
                    return true;
                }

                case EnvGetVariable:
                {
                    // struct retro_variable { const char *key; const char *value; }
                    if (data == IntPtr.Zero) return false;
                    string? key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(data));
                    if (key == null || !CoreOptions.TryGetValue(key, out string? value)) return false;

                    if (!_optionStrings.TryGetValue(key, out IntPtr valuePtr))
                    {
                        valuePtr = Marshal.StringToHGlobalAnsi(value);
                        _optionStrings[key] = valuePtr; // must outlive the call
                    }
                    Marshal.WriteIntPtr(data, IntPtr.Size, valuePtr);
                    return true;
                }

                case EnvGetVariableUpdate:
                    if (data != IntPtr.Zero) Marshal.WriteByte(data, 0);
                    return true;

                case EnvSetGeometry:
                    return true;

                default:
                    UnhandledEnvironment.TryGetValue(cmd, out int hits);
                    UnhandledEnvironment[cmd] = hits + 1;
                    return false;
            }
        }

        private void OnVideoRefresh(IntPtr data, uint width, uint height, UIntPtr pitch)
        {
            FramesRun++;
            if (data == IntPtr.Zero) return; // duped frame: keep the previous buffer

            int w = (int)width, h = (int)height, stride = (int)pitch;
            LastPitch = stride;
            if (!GeometryLog.Contains((w, h, stride))) GeometryLog.Add((w, h, stride));
            var target = new byte[w * h * 4];

            for (int y = 0; y < h; y++)
            {
                IntPtr row = data + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte r, g, b;
                    switch (CorePixelFormat)
                    {
                        case PixelFormat.Xrgb8888:
                        {
                            int px = Marshal.ReadInt32(row, x * 4);
                            r = (byte)((px >> 16) & 0xFF);
                            g = (byte)((px >> 8) & 0xFF);
                            b = (byte)(px & 0xFF);
                            break;
                        }
                        case PixelFormat.Rgb565:
                        {
                            ushort px = (ushort)Marshal.ReadInt16(row, x * 2);
                            // Replicate high bits into the low ones so 0x1F -> 0xFF exactly.
                            r = (byte)(((px >> 11) & 0x1F) * 255 / 31);
                            g = (byte)(((px >> 5) & 0x3F) * 255 / 63);
                            b = (byte)((px & 0x1F) * 255 / 31);
                            break;
                        }
                        default: // 0RGB1555
                        {
                            ushort px = (ushort)Marshal.ReadInt16(row, x * 2);
                            r = (byte)(((px >> 10) & 0x1F) * 255 / 31);
                            g = (byte)(((px >> 5) & 0x1F) * 255 / 31);
                            b = (byte)((px & 0x1F) * 255 / 31);
                            break;
                        }
                    }

                    int o = (y * w + x) * 4; // BGRA8888, matching SKColorType.Bgra8888
                    target[o + 0] = b;
                    target[o + 1] = g;
                    target[o + 2] = r;
                    target[o + 3] = 0xFF;
                }
            }

            FrameBuffer = target;
            FrameWidth = w;
            FrameHeight = h;
        }

        private short OnInputState(uint port, uint device, uint index, uint id) =>
            port == 0 && HeldButtons.Contains((int)id) ? (short)1 : (short)0;

        /// <summary>Boots a ROM from memory -- no temp file, and the bytes are exactly what the
        /// importer produced.</summary>
        public void LoadGame(byte[] romBytes, string? path = null)
        {
            // Always hand the core a real path, even when it also takes the ROM in memory.
            // bsnes cores derive their save-RAM path from it, and a core loaded without one
            // behaves differently enough to land the game on a different screen -- which cost a
            // long debugging detour when state capture (no path) and --emu-boot (path) diverged
            // despite both cores being provably deterministic.
            if (NeedsFullPath || path == null)
            {
                _tempRomPath = Path.Combine(Path.GetTempPath(),
                    $"dkctool-{Guid.NewGuid():N}{Path.GetExtension(path) ?? ".sfc"}");
                File.WriteAllBytes(_tempRomPath, romBytes);
                path = _tempRomPath;
            }

            IntPtr buffer = Marshal.AllocHGlobal(romBytes.Length);
            IntPtr pathPtr = path == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(path);
            try
            {
                Marshal.Copy(romBytes, 0, buffer, romBytes.Length);
                var info = new RetroGameInfo
                {
                    Path = pathPtr,
                    Data = NeedsFullPath ? IntPtr.Zero : buffer,
                    Size = NeedsFullPath ? UIntPtr.Zero : (UIntPtr)romBytes.Length,
                    Meta = IntPtr.Zero,
                };
                if (!_retroLoadGame(ref info))
                    throw new InvalidOperationException("retro_load_game failed -- core rejected the ROM.");
                _gameLoaded = true;

                // The libretro spec requires the frontend to call retro_get_system_av_info after
                // retro_load_game, and bsnes cores rely on it: skipping it left the game running
                // but rendering a garbled scene. Calling it here means every code path gets it,
                // rather than only the ones that happened to want the AV info.
                _retroGetAvInfo(out _);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer); // cores copy the ROM during load
                if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
            }
        }

        public (int Width, int Height, double Fps) GetAvInfo()
        {
            _retroGetAvInfo(out var info);
            return ((int)info.Geometry.BaseWidth, (int)info.Geometry.BaseHeight, info.Timing.Fps);
        }

        public void RunFrame() => _retroRun();

        public void RunFrames(int count)
        {
            for (int i = 0; i < count; i++) _retroRun();
        }

        /// <summary>Holds <paramref name="buttons"/> for <paramref name="frames"/> frames, then
        /// releases them. Scripted input is how the harness reaches a known screen deterministically.</summary>
        public void HoldFor(int frames, params int[] buttons)
        {
            foreach (int b in buttons) HeldButtons.Add(b);
            RunFrames(frames);
            foreach (int b in buttons) HeldButtons.Remove(b);
        }

        /// <summary>Core save state, used to pin a capture point without replaying input.</summary>
        public byte[] Serialize()
        {
            int size = (int)_retroSerializeSize();
            if (size <= 0) throw new InvalidOperationException("core reports no save-state support.");
            var managed = new byte[size];
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!_retroSerialize(buffer, (UIntPtr)size))
                    throw new InvalidOperationException("retro_serialize failed.");
                Marshal.Copy(buffer, managed, 0, size);
                return managed;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>
        /// Restores a core save state. The state carries CPU/PPU/WRAM/**VRAM**, not the ROM, so a
        /// state captured on one ROM can be restored while running a slightly different one --
        /// which is exactly what the V3 gate needs (capture once on the baseline, replay on each
        /// control ROM from an identical starting state, with no input script and no timing drift).
        ///
        /// The VRAM caveat is real and must be respected by callers: right after a restore, video
        /// memory still holds the tiles the *original* ROM had DMA'd. An imported sprite only
        /// appears once the game re-DMAs that frame, so run some frames before capturing.
        ///
        /// Returns false if the core rejects the state (some cores checksum the ROM).
        /// </summary>
        public bool LoadState(byte[] state)
        {
            IntPtr buffer = Marshal.AllocHGlobal(state.Length);
            try
            {
                Marshal.Copy(state, 0, buffer, state.Length);
                return _retroUnserialize(buffer, (UIntPtr)state.Length);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_gameLoaded) { _retroUnloadGame(); _gameLoaded = false; }
            _retroDeinit();

            if (_tempRomPath != null)
            {
                try { File.Delete(_tempRomPath); } catch { /* best effort */ }
                _tempRomPath = null;
            }

            foreach (IntPtr p in _optionStrings.Values) Marshal.FreeHGlobal(p);
            _optionStrings.Clear();

            if (_systemDirPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_systemDirPtr); _systemDirPtr = IntPtr.Zero; }
            if (_corePathPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_corePathPtr); _corePathPtr = IntPtr.Zero; }

            NativeLibrary.Free(_handle);
            _rooted.Clear();
        }
    }

    /// <summary>RETRO_DEVICE_ID_JOYPAD_* -- the SNES pad as libretro numbers it.</summary>
    public static class Joypad
    {
        public const int B = 0, Y = 1, Select = 2, Start = 3;
        public const int Up = 4, Down = 5, Left = 6, Right = 7;
        public const int A = 8, X = 9, L = 10, R = 11;
    }
}
