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
    // The picture (Backdrop) is the video's sharp maxresdefault frame, fetched by id and cross-faded in once it is
    // loaded, so the screen never shows a blurry or stretched half-loaded stage; a video with no such frame gets the
    // small 16:9 one.
    //
    // The darkening is two full-screen pre-rendered scrims (not cropped rectangles), so there is no visible band
    // or seam where a rectangle used to end, and no banding (tools/hero-scrims).
    //
    // Video preview: resting on the item starts a 3 s countdown ring on its card. The video itself (muted,
    // 720p, no watch history) starts loading after PrefetchMs, hidden on the TV's video plane under the still,
    // and is revealed when the ring completes — or, if it is not ready by then, as soon as it plays (the full
    // ring spins meanwhile). Any move or hide puts the still back first, then releases the decoder.
    public class Hero : View
    {
        private const uint DwellMs = 3000;          // the countdown: the video shows when it completes
        private const uint PrefetchMs = 1000;       // start loading this far into it (scrolling past costs nothing)
        private const uint ResolveMs = 400;         // resolve this one and the next ones' streams after this long
        private const int ResolveAhead = 2;         // how many of the next cards
        private const long PreviewCapMs = 120000;   // then give the decoder (and the data) back
        private const double CreateTimeoutS = 45;   // YouTube can be slow to hand over a stream
        private const double PlayTimeoutS = 15;     // created but never playing
        private const int Playing = 3;              // esp::State PLAYING

        // Text block geometry (bottom aligned, just above the first row's heading).
        // The block ends just above the first row's heading; the title is 40% of the screen wide, up to two lines of 59 px,
        // then an ellipsis.
        private const int TextBottom = Theme.GloriousRowsTop + Theme.HeroFirstRowTop - 16;
        private const int MetaH = 44, TitleGap = 26, TitleH = 150, TitleW = 768;
        private const int TitleTop = TextBottom - MetaH - TitleGap - TitleH;

        private readonly App app;
        private readonly Backdrop art;
        private readonly TextLabel title, meta;   // title, then channel · views · age
        private readonly VideoPreview video = new VideoPreview();
        private readonly PreviewRing ring = new PreviewRing();   // countdown on the focused card
        private Animation reveal;
        private Timer resolveTimer, prefetchTimer, watchTimer;
        private List<VideoItem> upcoming = new List<VideoItem>();   // the cards after the focused one
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

            art = new Backdrop();
            Add(art);
            // Left fade: solid black at the left edge -> clear by the centre. Bottom fade: clear in the upper screen
            // -> dark across the lower band under the rows. Both full-screen (so no seam), pre-rendered PNGs: the TV's
            // gradient visuals band and cost a shader pass each (tools/hero-scrims).
            Add(new ImageView { ResourceUrl = Theme.Image("hero_left"), Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill });
            Add(new ImageView { ResourceUrl = Theme.Image("hero_bottom"), Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill });

            // The text block is bottom-aligned just above the first row: the channel, views and age on the last line
            // and the title (one or two lines, growing upward) above it.
            title = Widgets.Label("", 59f, Theme.Text, TitleW, TitleH);   // regular weight; wraps to a second line
            title.Position = new Position(Theme.GloriousInset, TitleTop);
            title.MultiLine = true;
            title.VerticalAlignment = VerticalAlignment.Bottom;
            Add(title);

            meta = Widgets.Label("", Theme.BodySize + 2, Theme.Text, 760, MetaH);
            meta.Position = new Position(Theme.GloriousInset, TextBottom - MetaH);
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
            title.Text = item.Title ?? "";
            meta.Text = MetaLine(item);
            // The sharp 1280x720 picture, shown once it is loaded (never a blurry stage first); the small 16:9 one for a
            // video that has none.
            if (!string.IsNullOrEmpty(item.Id))
            {
                art.Show(item.Id, Frame(item.Id, "maxresdefault"), Frame(item.Id, "mqdefault"));
            }
            else
            {
                art.Show(null, item.Thumbnail ?? "", null);
            }
            Arm();
        }

        // Another category opened: its first picture fades in from nothing instead of over the old category's.
        public void ClearArt()
        {
            art.Clear();
            currentId = "";
            currentItem = null;
        }

        // The hero is (not) on screen. Off: any preview stops right away.
        public void SetActive(bool on)
        {
            if (!on && !active && armedId == null && !revealed && !video.Exists)
            {
                return;   // already idle: nothing to stop (this is asked on keys that never involved the hero)
            }
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

        // The cards after the focused one, whose streams are resolved ahead (see Arm).
        public void SetUpcoming(List<VideoItem> next)
        {
            upcoming = next ?? new List<VideoItem>();
        }

        // Puts the countdown ring on the top-right corner of the focused card (it then moves and zooms with it).
        public void AttachRing(View card)
        {
            if (card == null)
            {
                return;
            }
            try
            {
                if (ring.GetParent() == card)
                {
                    return;
                }
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

        // Timing trace of a preview for the core log; compiled out unless built with OMEGA_TRACE (it runs on key presses).
        [System.Diagnostics.Conditional("OMEGA_TRACE")]
        private void Log(string what) => NativeCore.Log("preview: " + what + " +" + clock.ElapsedMilliseconds + "ms");

        // Starts the countdown (and the hidden prefetch) for the shown item, unless it is already running for it.
        private void Arm()
        {
            if (!active || currentItem == null || string.IsNullOrEmpty(currentId) || armedId == currentId
                || doneId == currentId || !app.Config.HeroPreview)
            {
                return;
            }
            VideoItem item = currentItem;
            armedId = item.Id;
            countdownDone = false;
            clock.Restart();
            Log("countdown " + item.Id);
            ring.Countdown(DwellMs, CountdownDone);
            // Resolve this video's streams (and the next cards') ahead: no decoder needed, so it runs while the
            // ring fills and the player creation at PrefetchMs only has to fetch media. Not while scrolling past.
            resolveTimer = new Timer(ResolveMs);
            resolveTimer.Tick += (s, e) => UiGuard.Tick("Hero.resolve", () =>
            {
                resolveTimer = null;
                if (active && armedId == item.Id)
                {
                    var ids = new List<string> { item.Id };
                    for (int i = 0; i < upcoming.Count && i < ResolveAhead; i++)
                    {
                        ids.Add(upcoming[i].Id);
                    }
                    PreviewPrefetcher.Want(ids);
                }
                return false;
            });
            resolveTimer.Start();
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
            var ease = Widgets.EaseInOut;
            art.AnimateOut(reveal, ease);
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
            resolveTimer?.Stop();
            resolveTimer = null;
            PreviewPrefetcher.Clear();   // the next focus (if any) asks for its own neighbours
            prefetchTimer?.Stop();
            prefetchTimer = null;
            watchTimer?.Stop();
            watchTimer = null;
            ring.Off();
            reveal?.Stop();
            art.RestoreStill();
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
