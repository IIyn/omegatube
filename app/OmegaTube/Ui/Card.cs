using System;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // SmartTube-style card: rounded 16:9 thumbnail, info box under it that lights up when focused,
    // duration badge bottom right, red progress bar for started videos.
    // Built in two steps for smooth scrolling: the constructor only makes the cheap frame (grey thumbnail and
    // info box); Fill() adds the texts and starts the thumbnail, when the card is about to be seen (UiWork).
    //
    // thumbOnly: a bare rounded thumbnail with no info box (Glorious's featured first row, where the title and
    // metadata already live on the hero backdrop) — so the cards are smaller and show more of the background.
    public class Card : View
    {
        public VideoItem Item { get; private set; }
        public bool Filled { get; private set; }

        private readonly int width, thumbH;
        private readonly bool thumbOnly;
        private readonly ImageView thumb;
        private readonly ImageView info;
        private readonly View infoTopPatch;
        private readonly View progress;
        private TextLabel title, meta;
        private bool focused;
        private Animation zoom;

        public Card(VideoItem item, int width = Theme.CardWidth, bool thumbOnly = false)
        {
            Item = item;
            this.width = width;
            this.thumbOnly = thumbOnly;
            thumbH = Theme.ThumbHeight(width);
            Size = new Size(width, thumbOnly ? thumbH : Theme.CardHeight(width));
            // Scale around the centre so neighbours are overlapped evenly.
            PivotPoint = Tizen.NUI.PivotPoint.Center;
            PositionUsesPivotPoint = false;  // positions stay top-left based

            // Rounding on Tizen 5.5: the firmware's NUI has no CornerRadius, so we round with image techniques
            // that it does support. The thumbnail is clipped by a rounded-rect alpha mask; the info box is a
            // white rounded-rect nine-patch tinted to the panel colour (fixed corners at any width).
            // Without an info box the thumbnail rounds all four corners; with one it extends a radius under the
            // info box (the "glued" look) and a square patch hides the info box's rounded top corners.
            thumb = new ImageView
            {
                Size = new Size(width, thumbOnly ? thumbH : thumbH + Theme.CardRadius),
            };
            Add(thumb);

            if (!thumbOnly)
            {
                info = new ImageView
                {
                    Position = new Position(0, thumbH),
                    Size = new Size(width, Theme.CardInfoHeight),
                    ResourceUrl = Theme.Image("round9.9"),
                    Color = Theme.CardInfo,
                };
                Add(info);
                infoTopPatch = Widgets.Box(Theme.CardInfo, width, Theme.CardRadius);
                info.Add(infoTopPatch);
            }

            // On top of the info box's edge: the watched-fraction bar sits on the seam.
            progress = Widgets.Box(Theme.Accent, 0, 5);
            progress.Position = new Position(0, thumbH - 5);
            Add(progress);
            UpdateProgress();
        }

        // Texts, duration badge and thumbnail. Text labels are what costs (shaping on the UI thread).
        public void Fill()
        {
            if (Filled)
            {
                return;
            }
            Filled = true;
            string badge = Item.Live ? T("LIVE") : Item.Duration;
            if (!string.IsNullOrEmpty(badge))
            {
                var duration = Widgets.Label(" " + badge + " ", Theme.SmallSize - 2, Theme.Text);
                duration.BackgroundColor = Item.Live ? Theme.Accent : new Color(0, 0, 0, 0.82f);
                Widgets.Round(duration, 4);
                duration.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
                duration.PivotPoint = Tizen.NUI.PivotPoint.BottomRight;
                duration.PositionUsesPivotPoint = true;
                duration.Position = new Position(width - 8, thumbH - 10);
                thumb.Add(duration);
            }

            if (!thumbOnly)
            {
                title = Widgets.Label(Item.Title, Theme.BodySize, Theme.Text, width - 24, 62);
                title.MultiLine = true;
                title.Position = new Position(12, 8);
                info.Add(title);

                meta = Widgets.Label(MetaLine(Item), Theme.SmallSize, Theme.TextDim, width - 24);
                meta.Position = new Position(12, 70);
                info.Add(meta);
                ApplyFocusColors();
            }

            if (!string.IsNullOrEmpty(Item.Thumbnail))
            {
                Widgets.MaskedImage(thumb, Item.Thumbnail, Theme.Image("round_mask"));
            }
        }

        // The same video in a fresher page (network after cache): keeps the card and its loaded thumbnail.
        public void Rebind(VideoItem item)
        {
            Item = item;
            if (Filled && !thumbOnly)
            {
                title.Text = item.Title;
                meta.Text = MetaLine(item);
            }
            UpdateProgress();
        }

        private static string MetaLine(VideoItem item)
        {
            string line = item.Channel;
            if (!string.IsNullOrEmpty(item.Views))
            {
                line += " • " + item.Views;
            }
            if (!string.IsNullOrEmpty(item.Published))
            {
                line += " • " + item.Published;
            }
            return line;
        }

        public void UpdateProgress()
        {
            // Local position (watched here) or YouTube's (watched on another device), whichever is further.
            double p = Math.Max(LocalStore.Progress(Item.Id), Item.WatchedPercent / 100.0);
            progress.SizeWidth = (float)(width * p);
            Widgets.SetVisible(progress, p > 0.01);
        }

        public void SetFocused(bool focused)
        {
            if (focused == this.focused)
            {
                return;   // grids refresh every card's state after a page: no animation for unchanged ones
            }
            this.focused = focused;
            ApplyFocusColors();
            zoom?.Stop();
            zoom = new Animation(140);
            float s = focused ? Theme.FocusScale : 1f;
            zoom.AnimateTo(this, "Scale", new Vector3(s, s, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            zoom.Play();
            if (focused)
            {
                RaiseToTop();
            }
        }

        private void ApplyFocusColors()
        {
            if (thumbOnly)
            {
                return;   // no info box to recolour
            }
            Color infoColor = focused ? Theme.CardInfoFocused : Theme.CardInfo;
            info.Color = infoColor;   // tints the white nine-patch
            infoTopPatch.BackgroundColor = infoColor;
            if (Filled)
            {
                title.TextColor = focused ? Theme.CardTextFocused : Theme.Text;
                meta.TextColor = focused ? Theme.CardTextFocused : Theme.TextDim;
            }
        }
    }
}
