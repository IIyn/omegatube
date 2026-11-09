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
    // Video preview: resting on the item starts a 3 s countdown ring on its card. The video itself (muted,
    // 720p, no watch history) starts loading after PrefetchMs, hidden on the TV's video plane under the still,
    // and is revealed when the ring completes — or, if it is not ready by then, as soon as it plays (the full
    // ring spins meanwhile). Any move or hide puts the still back first, then releases the decoder.
    public class Hero : View
    {
        private const uint DwellMs = 3000;          // the countdown: the video shows when it completes
        private const uint PrefetchMs = 1000;       // start loading this far into it (scrolling past costs nothing)
        private const long PreviewCapMs = 120000;   // then give the decoder (and the data) back
        private const double CreateTimeoutS = 45;   // YouTube can be slow to hand over a stream
        private const double PlayTimeoutS = 15;     // created but never playing
        private const int Playing = 3;              // esp::State PLAYING

        private readonly App app;
        private readonly ImageView artBase, artHi;
        private readonly TextLabel title, meta;
        private readonly VideoPreview video = new VideoPreview();
        private readonly PreviewRing ring = new PreviewRing();   // countdown on the focused card
        private Animation fade, reveal;
        private Timer prefetchTimer, watchTimer;
        private VideoItem currentItem;
        private string currentId = "";
        private string armedId;     // its countdown is running or its preview is on
        private string doneId;      // its preview ended / failed: not retried until the focus leaves and comes back
        private bool active;        // on screen: first row of a Glorious feed, home visible
        private bool countdownDone;
        private bool revealed;      // the still has faded out over the playing video
        private long firstPlayingMs = -1;   // position at the first PLAYING sample: motion is measured from it
        private DateTime requestedAt, readyAt;
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
            video.Failed += () => UiGuard.Run("Hero.VideoFailed", () => GiveUp("refused"));
        }

        private static string Frame(string id, string variant) => "https://i.ytimg.com/vi/" + id + "/" + variant + ".jpg";

        // Sets the item's artwork + title + metadata and starts its countdown. Already the shown item: nothing is
        // restarted (keys that do not move the focus keep the countdown / preview going).
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
            StopVideo("moved");
            currentId = item.Id;
            currentItem = item;
            doneId = null;   // back on a card after leaving it: its countdown runs again
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

        // Starts the countdown (and the hidden prefetch) for the shown item, unless it is already running for it.
        private void Arm()
        {
            if (!active || currentItem == null || string.IsNullOrEmpty(currentId) || armedId == currentId
                || doneId == currentId)
            {
                return;
            }
            VideoItem item = currentItem;
            armedId = item.Id;
            countdownDone = false;
            clock.Restart();
            Log("countdown " + item.Id);
            ring.Countdown(DwellMs, CountdownDone);
            prefetchTimer = new Timer(PrefetchMs);
            prefetchTimer.Tick += (s, e) => UiGuard.Tick("Hero.prefetch", () =>
            {
                prefetchTimer = null;
                if (active && armedId == item.Id)
                {
                    StartVideo(item);
                }
                return false;
            });
            prefetchTimer.Start();
        }

        // Always from the start: a preview is a taster (OK still resumes where the user left off).
        private void StartVideo(VideoItem item)
        {
            Log("request");
            requestedAt = DateTime.Now;
            readyAt = default;
            firstPlayingMs = -1;
            video.Start(app, item.Id, 0);
            watchTimer = new Timer(150);
            watchTimer.Tick += (s, e) => UiGuard.Tick("Hero.watch", Watch);
            watchTimer.Start();
        }

        private void CountdownDone()
        {
            countdownDone = true;
            Log("countdown done");
            if (!TryReveal())
            {
                ring.Spin();   // not playing yet: shows as soon as it does
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
            readyAt = DateTime.Now;
            // The still still covers everything: making the layers under it see-through changes nothing yet.
            BackgroundColor = Color.Transparent;
            SeeThrough?.Invoke(true);
        }

        // Tracks the preview: reveals it once the countdown is over and its clock moves; ends it at the end, after
        // the cap, or when it does not come up in time.
        private bool Watch()
        {
            if (!video.Stats(out NativeCore.PlayerStats s))
            {
                if ((DateTime.Now - requestedAt).TotalSeconds > CreateTimeoutS)
                {
                    watchTimer = null;   // GiveUp -> StopVideo must not stop the timer running this tick
                    GiveUp("not created in time");
                    return false;
                }
                return true;   // still being created
            }
            if (s.State == Playing && firstPlayingMs < 0)
            {
                firstPlayingMs = s.PositionMs;
                Log("playing at " + s.PositionMs);
            }
            if (s.Eos != 0 || (revealed && firstPlayingMs >= 0 && s.PositionMs - firstPlayingMs > PreviewCapMs))
            {
                watchTimer = null;
                doneId = currentId;
                StopVideo(s.Eos != 0 ? "end" : "cap");
                return false;
            }
            if (!revealed && readyAt != default && (DateTime.Now - readyAt).TotalSeconds > PlayTimeoutS)
            {
                watchTimer = null;
                GiveUp("never played");
                return false;
            }
            if (!revealed && countdownDone)
            {
                TryReveal();
            }
            return true;
        }

        // Fades the still out over the video if it is playing (clock moved since the first PLAYING sample).
        private bool TryReveal()
        {
            if (revealed)
            {
                return true;
            }
            if (!video.Stats(out NativeCore.PlayerStats s) || s.State != Playing || firstPlayingMs < 0
                || s.PositionMs <= firstPlayingMs + 200)
            {
                return false;
            }
            Log("revealed at " + s.PositionMs);
            revealed = true;
            ring.Off();
            reveal?.Stop();
            reveal = new Animation(500);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut);
            reveal.AnimateTo(artBase, "Opacity", 0f, ease);
            reveal.AnimateTo(artHi, "Opacity", 0f, ease);
            reveal.Play();
            return true;
        }

        // The preview cannot come up: back to the still, red ring, not retried until the focus comes back.
        private void GiveUp(string why)
        {
            doneId = currentId;
            StopVideo(why);
            ring.Fail();
        }

        // Back to the still: cover the video plane first, then make the layers opaque, then free the decoder.
        private void StopVideo(string why)
        {
            if (armedId != null)
            {
                Log("stop (" + why + ")");
            }
            armedId = null;
            countdownDone = false;
            prefetchTimer?.Stop();
            prefetchTimer = null;
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
