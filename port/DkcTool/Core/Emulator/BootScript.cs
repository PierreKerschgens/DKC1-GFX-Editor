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
        /// <summary>
        /// Cranky Kong's cabin in the attract-mode demo, reached with **no input at all**.
        /// This is the preferred capture point.
        ///
        /// Scripted input turned out to be the wrong foundation for a cross-core gate: button
        /// presses land on whatever the game is doing at that instant, and cores drift apart in
        /// timing as a run goes on (snes9x vs bsnes-mercury agree to ~11% of pixels at frame 900
        /// but 82% by frame 1,800). A tap that lands mid-transition on one core lands after it on
        /// another, so "the same frame number" stops meaning "the same game state" -- which is
        /// what made bsnes-mercury look like it had a rendering bug when it does not.
        ///
        /// With no input there is nothing to mistime: every core runs the same attract sequence.
        /// </summary>
        public const int AttractFrame = 900;

        /// <summary>Intro -> "SELECT A GAME".</summary>
        public const int FileSelectFrame = 1500;

        /// <summary>In-game, Jungle Hijinxs, DK standing at the hut with the level faded in.
        /// The default capture point: a Kong sprite is on screen and the scene is static.</summary>
        public const int InGameFrame = 2700;

        /// <summary>A Start tap every <see cref="TapInterval"/> frames walks intro -> title ->
        /// file select -> level without needing to know each screen's exact timing.</summary>
        public const int TapInterval = 300;

        /// <summary>
        /// Tapping stops after this many frames, so a capture taken well after level entry is not
        /// disturbed by a Start press (which pauses, in-game).
        ///
        /// Tuned by bisection, and the window is narrow: the tap at ~2,192 is what enters the
        /// level, and the tap at ~2,492 *pauses* it. At 2,100 snes9x never reaches the level at
        /// all (black at 2,700); at 2,600 it reaches the level and is then paused -- the scene is
        /// frozen (settle 90 and 240 give an identical frame), input is ignored (300 frames of
        /// Right moves nothing), and no sprite is re-DMA'd. At 2,300 the game is live: DK walks
        /// and the screen scrolls.
        ///
        /// This is also what made bsnes-mercury look broken. DKC1's pause overlay dims the scene
        /// with a dither pattern, which at 256px reads as garbled vertical striping -- mercury's
        /// timing had it paused where snes9x's did not.
        ///
        /// **The value is snes9x-specific.** Any other core needs its own window, which is the
        /// reason the gate prefers a save state over this script.
        /// </summary>
        public const int TapWindow = 2300;

        /// <summary>Runs <paramref name="frames"/> frames from power-on. With
        /// <paramref name="pressStart"/> false (the default for the gate) no input is sent at
        /// all, which is what keeps different cores in the same state -- see
        /// <see cref="AttractFrame"/>.</summary>
        public static void RunTo(LibretroCore core, int frames, bool pressStart, int tapWindow = TapWindow)
        {
            if (!pressStart)
            {
                core.RunFrames(frames);
                return;
            }

            RunToWithStartTaps(core, frames, tapWindow);
        }

        /// <summary>Start-tap script that walks intro -> title -> file select -> level. Needed to
        /// reach gameplay, but see <see cref="AttractFrame"/> for why the gate avoids it.</summary>
        public static void RunToWithStartTaps(LibretroCore core, int frames, int tapWindow = TapWindow)
        {
            for (int f = 0; f < frames; f += TapInterval)
            {
                int chunk = System.Math.Min(TapInterval, frames - f);

                // Past the tap window the level is loaded; further Start presses would pause it.
                if (f >= tapWindow || chunk <= 8)
                {
                    core.RunFrames(chunk);
                    continue;
                }

                core.RunFrames(chunk - 8);
                core.HoldFor(4, Joypad.Start);
                core.RunFrames(4);
            }
        }

        /// <summary>Boots <paramref name="romBytes"/> and runs the script to
        /// <paramref name="frames"/>, returning the final frame.</summary>
        public static SkiaSharp.SKBitmap CaptureAt(string corePath, byte[] romBytes, int frames,
                                                  bool pressStart = false, int tapWindow = TapWindow)
        {
            using var core = new LibretroCore(corePath);
            core.LoadGame(romBytes);
            RunTo(core, frames, pressStart, tapWindow);
            return FrameCapture.ToBitmap(core);
        }
    }
}
