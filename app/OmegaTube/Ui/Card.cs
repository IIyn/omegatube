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
    public class Card : View
    {
        public VideoItem Item { get; private set; }
        public bool Filled { get; private set; }

        private readonly int width, thumbH;
        private readonly ImageView thumb;
        private readonly View info, infoTopPatch;
        private readonly View progress;
        private TextLabel title, meta;
        private bool focused;
        private Animation zoom;

        public Card(VideoItem item, int width = Theme.CardWidth)
        {
            Item = item;
            this.width = width;
            thumbH = Theme.ThumbHeight(width);
            Size = new Size(width, Theme.CardHeight(width));
            // Scale around the centre so neighbours are overlapped evenly.
            PivotPoint = Tizen.NUI.PivotPoint.Center;
            PositionUsesPivotPoint = false;  // positions stay top-left based

            // TizenFX 8 only has a uniform corner radius. The "glued" look (rounded outer corners only) is
            // made by overlap: the thumbnail extends one radius under the info box (which hides its rounded
            // bottom), and a square patch covers the info box's rounded top corners.
            thumb = new ImageView
            {
                Size = new Size(width, thumbH + Theme.CardRadius),
                BackgroundColor = Theme.Surface,
            };
            Widgets.Round(thumb, Theme.CardRadius);
            Add(thumb);

            info = new View
            {
                Position = new Position(0, thumbH),
                Size = new Size(width, Theme.CardInfoHeight),
                BackgroundColor = Theme.CardInfo,
            };
            Widgets.Round(info, Theme.CardRadius);
            Add(info);
            infoTopPatch = Widgets.Box(Theme.CardInfo, width, Theme.CardRadius);
            info.Add(infoTopPatch);

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

            title = Widgets.Label(Item.Title, Theme.BodySize, Theme.Text, width - 24, 62);
            title.MultiLine = true;
            title.Position = new Position(12, 8);
            info.Add(title);

            meta = Widgets.Label(MetaLine(Item), Theme.SmallSize, Theme.TextDim, width - 24);
            meta.Position = new Position(12, 70);
            info.Add(meta);
            ApplyFocusColors();

            if (!string.IsNullOrEmpty(Item.Thumbnail))
            {
                thumb.ResourceUrl = Item.Thumbnail;
            }
        }

        // The same video in a fresher page (network after cache): keeps the card and its loaded thumbnail.
        public void Rebind(VideoItem item)
        {
            Item = item;
            if (Filled)
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
            info.BackgroundColor = focused ? Theme.CardInfoFocused : Theme.CardInfo;
            infoTopPatch.BackgroundColor = info.BackgroundColor;
            if (Filled)
            {
                title.TextColor = focused ? Theme.CardTextFocused : Theme.Text;
                meta.TextColor = focused ? Theme.CardTextFocused : Theme.TextDim;
            }
        }
    }
}
