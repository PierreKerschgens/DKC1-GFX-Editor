using System;
using System.IO;
using SkiaSharp;

namespace DkcTool.Core.Emulator
{
    /// <summary>
    /// Save-state capture points (specs/v3-emulator-spec.md). Replaces "boot and replay an input
    /// script to frame N" with "restore a state captured once at a verified scene".
    ///
    /// Why: a frame number is not a portable capture point. Cores drift apart in timing even with
    /// no input (snes9x vs bsnes-mercury: 11% of pixels differ at frame 900, 82% by frame 1,800),
    /// and scripted input makes it worse, since a button press lands on whatever is on screen at
    /// that instant. A state pins the *state*, which is what the gate actually cares about.
    ///
    /// The state carries CPU/PPU/WRAM/VRAM but **not the ROM**, so one captured on the baseline
    /// ROM can be restored while running a modified one -- the control ROMs then start from a
    /// byte-identical machine state with no script to mistime.
    /// </summary>
    public static class StateCapture
    {
        /// <summary>Gitignored: a save state is derived from a copyrighted ROM, same rule as the
        /// ROM and the golden frames.</summary>
        public const string StateDirectory = "port/emu/states";

        /// <summary>
        /// Frames to run after restoring before capturing. A restored state's VRAM still holds
        /// the tiles the *original* ROM DMA'd, so an imported sprite is invisible until the game
        /// re-uploads that frame. DKC re-DMAs per animation frame, so a short settle is enough --
        /// but this is the whole reason the gate cannot capture immediately after a restore.
        /// </summary>
        public const int SettleFrames = 90;

        public static string PathFor(string corePath, string name) =>
            Path.Combine(StateDirectory,
                $"{Path.GetFileNameWithoutExtension(corePath)}-{name}.state");

        /// <summary>Drives a fresh boot to <paramref name="frames"/> with the tap script, then
        /// serializes. Used once per core, and the resulting frame must be eyeballed.</summary>
        public static (byte[] State, SKBitmap Frame) Create(string corePath, byte[] romBytes, int frames,
                                                            int tapWindow = BootScript.TapWindow,
                                                            int holdRight = 0)
        {
            using var core = new LibretroCore(corePath);
            core.LoadGame(romBytes);
            BootScript.RunTo(core, frames, pressStart: true, tapWindow);

            // Walking before serializing serves two purposes: it clears whatever screen the last
            // Start tap left up (on bsnes-mercury the game otherwise sits on an overlay that never
            // times out), and it leaves the Kong mid-animation, so the state resumes into a scene
            // that is actively re-DMAing sprite frames.
            if (holdRight > 0) core.HoldFor(holdRight, Joypad.Right);

            return (core.Serialize(), FrameCapture.ToBitmap(core));
        }

        /// <summary>
        /// Restores <paramref name="state"/> on a core running <paramref name="romBytes"/>, runs
        /// <see cref="SettleFrames"/> frames so the game re-DMAs its graphics, and captures.
        /// Throws if the core rejects the state (some cores checksum the ROM).
        /// </summary>
        public static SKBitmap CaptureFromState(string corePath, byte[] romBytes, byte[] state,
                                                int settleFrames = SettleFrames, bool walk = false)
        {
            using var core = new LibretroCore(corePath);
            core.LoadGame(romBytes);

            // Some cores need a frame before their state buffers are valid.
            int warmup = int.TryParse(Environment.GetEnvironmentVariable("DKC_WARMUP"), out int wv) ? wv : 1;
            core.RunFrames(warmup);

            if (!core.LoadState(state))
                throw new InvalidOperationException(
                    "core rejected the save state -- it may checksum the ROM, in which case the " +
                    "state must be recaptured per ROM rather than shared from the baseline.");

            if (walk)
            {
                // Standing still, DKC never re-DMAs the idle frame, so a restored state keeps
                // showing the ROM's original tiles no matter how long it settles. Walking forces
                // new animation frames, and each one is a fresh DMA from the (possibly modified)
                // GFX table. Deterministic: both control ROMs start from an identical state.
                core.HoldFor(settleFrames, Joypad.Right);
            }
            else
            {
                core.RunFrames(settleFrames);
            }

            return FrameCapture.ToBitmap(core);
        }
    }
}
