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
        private const double StartTimeoutS = 15;    // never reached PLAYING: stay on the still
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
        private long videoStartMs;
        private DateTime videoReadyAt;

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
                StopVideo();
            }
        }

        // OK on the shown item: where its preview is (for the full player to continue from), -1 if it is not
        // playing. The preview stops, so the decoder is on its way back before the player asks for it.
        public long TakeVideoPosition(string id)
        {
            long position = -1;
            if (video.VideoId == id && video.Stats(out NativeCore.PlayerStats s) && s.State == Playing)
            {
                position = s.PositionMs;
            }
            StopVideo();
            return position;
        }

        // Starts the dwell countdown for the shown item, unless its preview is already starting or playing.
        private void Arm()
        {
            if (!active || currentItem == null || string.IsNullOrEmpty(currentId) || dwellTimer != null
                || video.VideoId == currentId || doneId == currentId)
            {
                return;
            }
            VideoItem item = currentItem;
            dwellTimer = new Timer(DwellMs);
            dwellTimer.Tick += (s, e) => UiGuard.Tick("Hero.dwell", () =>
            {
                dwellTimer = null;
                if (active && item.Id == currentId)
                {
                    videoStartMs = PlayerScreen.ResumePoint(item);
                    video.Start(app, item.Id, videoStartMs);
                }
                return false;
            });
            dwellTimer.Start();
        }

        private void OnVideoReady()
        {
            if (!active || video.VideoId != currentId)
            {
                StopVideo();
                return;
            }
            // The still still covers everything: making the layers under it see-through changes nothing yet.
            BackgroundColor = Color.Transparent;
            SeeThrough?.Invoke(true);
            videoReadyAt = DateTime.Now;
            watchTimer = new Timer(150);
            watchTimer.Tick += (s, e) => UiGuard.Tick("Hero.watch", Watch);
            watchTimer.Start();
        }

        // Reveals the video once frames flow; ends the preview at the end, after the cap, or if it never starts.
        private bool Watch()
        {
            if (!video.Stats(out NativeCore.PlayerStats s))
            {
                watchTimer = null;
                return false;
            }
            bool tooLate = !revealed && (DateTime.Now - videoReadyAt).TotalSeconds > StartTimeoutS;
            if (s.Eos != 0 || tooLate || (revealed && s.PositionMs - videoStartMs > PreviewCapMs))
            {
                watchTimer = null;   // StopVideo must not stop the timer that is running this tick
                doneId = currentId;
                StopVideo();
                return false;
            }
            if (!revealed && s.State == Playing && s.PositionMs > videoStartMs + 200)
            {
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
        private void StopVideo()
        {
            dwellTimer?.Stop();
            dwellTimer = null;
            watchTimer?.Stop();
            watchTimer = null;
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
