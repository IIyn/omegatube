using System;
using System.Collections.Generic;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Full-screen backdrop for the "Glorious" home style: the focused item's thumbnail fills the screen and
    // fades to black on the left (under the nav / behind the text) and along the bottom (under the rows), with
    // the item's title and a metadata line over it. Hero.Display(item) is called by HomeScreen as focus moves.
    //
    // The feed only carries the small mqdefault thumbnail (320×180), which is why a stretched backdrop looked
    // blurry. For the backdrop we fetch the full-resolution frames from the id instead: a reliable hqdefault
    // base layer with the sharp maxresdefault on top of it, so when maxres is missing (not every video has one)
    // the hqdefault still shows rather than a blank or a stale frame.
    //
    // The darkening is two full-screen gradients (not cropped rectangles), so there is no visible band/seam
    // where a gradient rectangle used to end; multiple stops keep the ramp short enough to limit TV banding.
    //
    // Video preview: after resting DwellMs on the item, the video itself plays (muted, 720p, no watch history)
    // on the TV's video plane under the UI. The still stays on top until the video is actually PLAYING, then
    // fades out to reveal it (the gradients and texts stay). Any move or hide puts the still back first, then
    // releases the decoder, so there is no black flash either way.
    public class Hero : View
    {
        private const uint DwellMs = 3000;          // rest this long on a first-row video before it plays
        private const long PreviewCapMs = 120000;   // then give the decoder (and the data) back
        private const double StartTimeoutS = 20;    // from the request: never playing by then -> stay on the still
        private const int Playing = 3;              // esp::State PLAYING

        private readonly App app;
        private readonly ImageView artBase, artHi;
        private readonly TextLabel title, meta;
        private readonly VideoPreview video = new VideoPreview();
        private Animation fade, reveal;
        private Timer dwellTimer, watchTimer;
        private VideoItem currentItem;
        private string currentId = "";
        private string doneId;      // its preview ended (end, cap, never started): not replayed until it changes
        private bool active;        // on screen: first row of a Glorious feed, home visible
        private bool revealed;      // the still has faded out over the playing video
        private long firstPlayingMs = -1;   // position at the first PLAYING sample: motion is measured from it
        private DateTime requestedAt;       // when the preview was asked for (end of the dwell)
        private readonly PreviewRing ring = new PreviewRing();   // countdown / loading on the focused card
        private readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();   // timing log

        // The layers under the hero (window, home root) must turn see-through while the video plane shows.
        public Action<bool> SeeThrough;

        public Hero(App app)
        {
            this.app = app;
            Size = new Size(1920, 1080);
            BackgroundColor = Theme.Background;

            artBase = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artBase);
            artHi = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artHi);

            // Left fade: solid black at the left edge -> clear by the centre (full-screen, so no vertical seam).
            Add(Widgets.Gradient(new Vector2(-0.5f, 0f), new Vector2(0.5f, 0f),
                new[] { 0f, 0.28f, 0.5f },
                new[] { new Vector4(0, 0, 0, 1f), new Vector4(0, 0, 0, 0.55f), new Vector4(0, 0, 0, 0f) },
                1920, 1080, 0, 0));
            // Bottom fade: clear in the upper screen -> dark across the lower band under the rows.
            Add(Widgets.Gradient(new Vector2(0f, 0.5f), new Vector2(0f, -0.5f),
                new[] { 0f, 0.5f, 1f },
                new[] { new Vector4(0, 0, 0, 0.92f), new Vector4(0, 0, 0, 0.15f), new Vector4(0, 0, 0, 0f) },
                1920, 1080, 0, 0));

            title = Widgets.Label("", Theme.TitleSize + 8, Theme.Text, 760, 118);
            title.Position = new Position(Theme.GloriousInset, 196);
            title.MultiLine = true;
            Widgets.Bold(title);
            Add(title);

            meta = Widgets.Label("", Theme.BodySize + 2, Theme.TextDim, 760, 44);
            meta.Position = new Position(Theme.GloriousInset, 322);
            Add(meta);

            video.Ready += () => UiGuard.Run("Hero.VideoReady", OnVideoReady);
        }

        private static string Frame(string id, string variant) => "https://i.ytimg.com/vi/" + id + "/" + variant + ".jpg";

        // Sets the item's artwork + title + metadata. Already the shown item: only (re)arms its preview.
        public void Display(VideoItem item)
        {
            if (item == null)
            {
                return;
            }
            if (item.Id == currentId)
            {
                Arm();
                return;
            }
            StopVideo();
            currentId = item.Id;
            currentItem = item;
            doneId = null;
            if (!string.IsNullOrEmpty(item.Id))
            {
                artBase.ResourceUrl = Frame(item.Id, "hqdefault");
                artHi.ResourceUrl = Frame(item.Id, "maxresdefault");
            }
            else
            {
                artBase.ResourceUrl = item.Thumbnail ?? "";
                artHi.ResourceUrl = "";
            }
            title.Text = item.Title ?? "";
            meta.Text = MetaLine(item);
            fade?.Stop();
            artBase.Opacity = 0.5f;
            fade = new Animation(260);
            fade.AnimateTo(artBase, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            fade.Play();
            Arm();
        }

        // The hero is (not) on screen. Off: any preview stops right away.
        public void SetActive(bool on)
        {
            active = on;
            if (on)
            {
                Arm();
            }
            else
            {
                StopVideo("hidden");
            }
        }

        // OK on the shown item: where the full player should start, -1 for its usual resume point. A preview that
        // was playing continues from its position, unless the user had already watched further (resume point).
        // The preview stops, so the decoder is on its way back before the player asks for it.
        public long TakeVideoPosition(string id)
        {
            long position = -1;
            if (video.VideoId == id && video.Stats(out NativeCore.PlayerStats s) && s.State == Playing)
            {
                position = s.PositionMs;
                if (currentItem != null)
                {
                    position = Math.Max(position, PlayerScreen.ResumePoint(currentItem));
                }
            }
            StopVideo("play");
            return position;
        }

        private void Log(string what) => NativeCore.Log("preview: " + what + " +" + clock.ElapsedMilliseconds + "ms");

        // Starts the dwell countdown for the shown item, unless its preview is already starting or playing.
        private void Arm()
        {
            if (!active || currentItem == null || string.IsNullOrEmpty(currentId) || dwellTimer != null
                || video.VideoId == currentId || doneId == currentId)
            {
                return;
            }
            VideoItem item = currentItem;
            ring.Countdown(DwellMs);
            dwellTimer = new Timer(DwellMs);
            dwellTimer.Tick += (s, e) => UiGuard.Tick("Hero.dwell", () =>
            {
                dwellTimer = null;
                if (active && item.Id == currentId)
                {
                    // Always from the start: a preview is a taster (OK still resumes where the user left off).
                    clock.Restart();
                    Log("start " + item.Id);
                    ring.Loading();
                    requestedAt = DateTime.Now;
                    firstPlayingMs = -1;
                    video.Start(app, item.Id, 0);
                    // Watched from the request on, so a preview that never comes up also ends (and the ring stops).
                    watchTimer = new Timer(150);
                    watchTimer.Tick += (s2, e2) => UiGuard.Tick("Hero.watch", Watch);
                    watchTimer.Start();
                }
                else
                {
                    ring.Off();
                }
                return false;
            });
            dwellTimer.Start();
        }

        // Puts the countdown ring on the top-right corner of the focused card (it then moves and zooms with it).
        public void AttachRing(View card)
        {
            if (card == null || ring.GetParent() == card)
            {
                return;
            }
            try
            {
                ring.GetParent()?.Remove(ring);
                ring.Position = new Position(card.SizeWidth - PreviewRing.Diameter - 10, 10);
                card.Add(ring);
            }
            catch (Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "preview ring: " + e.Message);
            }
        }

        private void OnVideoReady()
        {
            if (!active || video.VideoId != currentId)
            {
                StopVideo("stale");
                return;
            }
            Log("player ready");
            // The still still covers everything: making the layers under it see-through changes nothing yet.
            BackgroundColor = Color.Transparent;
            SeeThrough?.Invoke(true);
        }

        // Reveals the video once its clock moves (measured from the first PLAYING sample, so wherever it started);
        // ends the preview at the end, after the cap, or if it never starts.
        private bool Watch()
        {
            bool tooLate = !revealed && (DateTime.Now - requestedAt).TotalSeconds > StartTimeoutS;
            if (!video.Stats(out NativeCore.PlayerStats s))
            {
                if (tooLate || video.VideoId == null)
                {
                    watchTimer = null;   // never came up (or stopped): StopVideo must not stop this running timer
                    doneId = currentId;
                    StopVideo("never started");
                    return false;
                }
                return true;   // still being created
            }
            if (s.State == Playing && firstPlayingMs < 0)
            {
                firstPlayingMs = s.PositionMs;
                Log("playing at " + s.PositionMs);
            }
            bool capped = revealed && firstPlayingMs >= 0 && s.PositionMs - firstPlayingMs > PreviewCapMs;
            if (s.Eos != 0 || tooLate || capped)
            {
                watchTimer = null;   // StopVideo must not stop the timer that is running this tick
                doneId = currentId;
                StopVideo(s.Eos != 0 ? "end" : tooLate ? "never played" : "cap");
                return false;
            }
            if (!revealed && s.State == Playing && firstPlayingMs >= 0 && s.PositionMs > firstPlayingMs + 200)
            {
                Log("revealed at " + s.PositionMs);
                ring.Off();
                revealed = true;
                reveal?.Stop();
                reveal = new Animation(500);
                var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut);
                reveal.AnimateTo(artBase, "Opacity", 0f, ease);
                reveal.AnimateTo(artHi, "Opacity", 0f, ease);
                reveal.Play();
            }
            return true;
        }

        // Back to the still: cover the video plane first, then make the layers opaque, then free the decoder.
        private void StopVideo(string why = "moved")
        {
            if (video.VideoId != null)
            {
                Log("stop (" + why + ")");
            }
            dwellTimer?.Stop();
            dwellTimer = null;
            watchTimer?.Stop();
            watchTimer = null;
            ring.Off();
            reveal?.Stop();
            artBase.Opacity = 1f;
            artHi.Opacity = 1f;
            revealed = false;
            BackgroundColor = Theme.Background;
            SeeThrough?.Invoke(false);
            video.Stop();
        }

        // "Channel · 1.2M views · 3 days ago", skipping the parts YouTube did not give.
        private static string MetaLine(VideoItem item)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(item.Channel)) parts.Add(item.Channel);
            if (!string.IsNullOrEmpty(item.Views)) parts.Add(item.Views);
            if (!string.IsNullOrEmpty(item.Published)) parts.Add(item.Published);
            return string.Join("   ·   ", parts);
        }
    }
}
