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
    public class Hero : View
    {
        private const int PreviewDelayMs = 600;   // dwell before the animated preview starts (avoids one per scroll tick)
        // Tizen 5.5's NUI image decoder does not animate WebP (YouTube's only moving-thumbnail format), so a
        // preview would just replace the sharp still with a low-res first frame. The core still provides
        // item.Preview, so this can be switched on for a firmware that can animate it.
        private const bool AnimatedPreviews = false;

        private readonly ImageView artBase, artHi, preview;
        private readonly TextLabel title, meta;
        private Animation fade, previewFade;
        private Timer previewTimer;
        private string currentId = "";

        public Hero()
        {
            Size = new Size(1920, 1080);
            BackgroundColor = Theme.Background;

            artBase = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artBase);
            artHi = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artHi);
            // Animated-preview layer (richThumbnail WebP), faded in over the stills after a short dwell. NUI plays
            // animated images loaded by URL; if the firmware can't animate the WebP it just shows its first frame.
            preview = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill, Opacity = 0f };
            Add(preview);

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
        }

        private static string Frame(string id, string variant) => "https://i.ytimg.com/vi/" + id + "/" + variant + ".jpg";

        // Sets the item's artwork + title + metadata. No-op when it is already the shown item.
        public void Display(VideoItem item)
        {
            if (item == null || item.Id == currentId)
            {
                return;
            }
            currentId = item.Id;
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
            SchedulePreview(item);
        }

        // Clears any running preview, then (if this item has one) starts it after a short dwell.
        private void SchedulePreview(VideoItem item)
        {
            previewTimer?.Stop();
            previewTimer = null;
            previewFade?.Stop();
            preview.Opacity = 0f;
            preview.Image = new PropertyMap();   // drop the previous preview
            if (!AnimatedPreviews || string.IsNullOrEmpty(item.Preview))
            {
                return;
            }
            string url = item.Preview, id = item.Id;
            previewTimer = new Timer(PreviewDelayMs);
            previewTimer.Tick += (s, e) => UiGuard.Tick("Hero.preview", () =>
            {
                if (id == currentId)
                {
                    // Force the animated-image visual: YouTube's moving thumbnail is an animated WebP whose URL
                    // carries a query string, so NUI's extension-based auto-detection would otherwise load it as a
                    // single still frame.
                    var map = new PropertyMap();
                    map.Add((int)Visual.Property.Type, new PropertyValue((int)Visual.Type.AnimatedImage));
                    map.Add((int)ImageVisualProperty.URL, new PropertyValue(url));
                    preview.Image = map;
                    previewFade?.Stop();
                    previewFade = new Animation(300);
                    previewFade.AnimateTo(preview, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                    previewFade.Play();
                }
                return false;   // one-shot
            });
            previewTimer.Start();
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
