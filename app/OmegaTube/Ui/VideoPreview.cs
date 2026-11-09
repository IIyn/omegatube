using System;
using System.Threading;
using Tizen.NUI;

namespace OmegaTube.Ui
{
    // Muted, video-only playback of a home item behind the Glorious hero once the user rests on it. A preview is
    // not a watch: the core plays no audio and attaches no history reporter (omega_player_create_preview).
    // The TV has one video decoder, so creation and teardown run on PlayerScreen's decoder chain: the full player
    // (or the next preview) never starts while this one still holds it, even if it is still being created.
    internal class VideoPreview
    {
        private const int MaxHeight = 720;   // starts faster, and is plenty behind the hero's gradients

        private readonly object sync = new object();
        private int generation;   // bumped by every Start / Stop: a creation from an older one is discarded
        private IntPtr pending;   // created on the decoder chain, not yet adopted by the UI thread
        private IntPtr player;    // adopted (UI thread)

        public string VideoId { get; private set; }
        public bool Exists => player != IntPtr.Zero;

        // The player exists (on the UI thread). Frames follow once it reaches PLAYING: see Stats.
        public event Action Ready;
        // The core could not start it (refused / no format); the reason is in the core log. UI thread.
        public event Action Failed;

        public void Start(App app, string videoId, long startMs)
        {
            Stop();
            SynchronizationContext ui = SynchronizationContext.Current;
            int gen;
            lock (sync)
            {
                gen = ++generation;
            }
            VideoId = videoId;
            IntPtr window = app.EcoreWindow;
            int w = (int)Window.Instance.WindowSize.Width, h = (int)Window.Instance.WindowSize.Height;
            string codecs = app.Config.Player.Codecs;
            PlayerScreen.Decoder(() =>
            {
                lock (sync)
                {
                    if (gen != generation)
                    {
                        return;   // stopped while waiting for the decoder
                    }
                }
                IntPtr p = NativeCore.PlayerCreatePreview(window, w, h, videoId, MaxHeight, codecs, startMs);
                if (p == IntPtr.Zero)
                {
                    ui?.Post(_ => UiGuard.Run("VideoPreview.Failed", () =>
                    {
                        bool current;
                        lock (sync)
                        {
                            current = gen == generation;
                        }
                        if (current)
                        {
                            Failed?.Invoke();
                        }
                    }), null);
                    return;
                }
                bool keep;
                lock (sync)
                {
                    keep = gen == generation;
                    if (keep)
                    {
                        pending = p;
                    }
                }
                if (!keep)
                {
                    NativeCore.PlayerDestroy(p);   // stopped during creation: released before the chain moves on
                    return;
                }
                ui?.Post(_ => UiGuard.Run("VideoPreview.Adopt", () => Adopt(gen, p)), null);
            });
        }

        private void Adopt(int gen, IntPtr p)
        {
            lock (sync)
            {
                if (gen != generation || pending != p)
                {
                    return;   // Stop() took it meanwhile (and released it)
                }
                pending = IntPtr.Zero;
                player = p;
            }
            Ready?.Invoke();
        }

        // State and position of the running preview; false when there is none.
        public bool Stats(out NativeCore.PlayerStats stats)
        {
            stats = default;
            if (player == IntPtr.Zero)
            {
                return false;
            }
            NativeCore.PlayerGetStats(player, out stats);
            return true;
        }

        // Stops (or cancels) the preview; the decoder is given back through the chain.
        public void Stop()
        {
            IntPtr p;
            lock (sync)
            {
                generation++;
                p = player != IntPtr.Zero ? player : pending;
                player = pending = IntPtr.Zero;
            }
            VideoId = null;
            PlayerScreen.Release(p);
        }
    }
}
