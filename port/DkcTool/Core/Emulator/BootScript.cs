namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// Deterministic input scripts that drive a freshly booted ROM to a known screen
    /// (specs/v3-emulator-spec.md). The emulator is deterministic, so replaying the same
    /// button cadence from power-on lands on the same frame every time -- that reproducibility
    /// is what makes a frame comparison between two ROMs meaningful.
    ///
    /// Frame numbers are for the USA Rev 2 ROM and were found with <c>--emu-explore</c>.
    /// </summary>
    public static class BootScript
    {
        /// <summary>Intro -> "SELECT A GAME".</summary>
        public const int FileSelectFrame = 1500;

        /// <summary>In-game, Jungle Hijinxs, DK standing at the hut with the level faded in.
        /// The default capture point: a Kong sprite is on screen and the scene is static.</summary>
        public const int InGameFrame = 2700;

        /// <summary>A Start tap every <see cref="TapInterval"/> frames walks intro -> title ->
        /// file select -> level without needing to know each screen's exact timing.</summary>
        public const int TapInterval = 300;

        /// <summary>Runs the standard power-on script for <paramref name="frames"/> frames.
        /// The core must already have a game loaded.</summary>
        public static void RunTo(LibretroCore core, int frames)
        {
            for (int f = 0; f < frames; f += TapInterval)
            {
                int chunk = System.Math.Min(TapInterval, frames - f);
                if (chunk <= 8)
                {
                    core.RunFrames(chunk);
                    break;
                }

                core.RunFrames(chunk - 8);
                core.HoldFor(4, Joypad.Start);
                core.RunFrames(4);
            }
        }

        /// <summary>Boots <paramref name="romBytes"/> and runs the script to
        /// <paramref name="frames"/>, returning the final frame.</summary>
        public static SkiaSharp.SKBitmap CaptureAt(string corePath, byte[] romBytes, int frames)
        {
            using var core = new LibretroCore(corePath);
            core.LoadGame(romBytes);
            RunTo(core, frames);
            return FrameCapture.ToBitmap(core);
        }
    }
}
