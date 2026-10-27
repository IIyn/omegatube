using System;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // SmartTube-style card: rounded 16:9 thumbnail, info box under it that lights up when focused,
    // duration badge bottom right, red progress bar for started videos.
    public class Card : View
    {
        public VideoItem Item { get; }

        private readonly int width;
        private readonly ImageView thumb;
        private readonly View info, infoTopPatch;
        private readonly TextLabel title, meta;
        private readonly View progress;
        private bool imageLoaded;
        private Animation zoom;

        public Card(VideoItem item, int width = Theme.CardWidth)
        {
            Item = item;
            this.width = width;
            int thumbH = Theme.ThumbHeight(width);
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
                CornerRadius = Theme.CardRadius,
            };
            Add(thumb);

            string badge = item.Live ? "EN DIRECT" : item.Duration;
            if (!string.IsNullOrEmpty(badge))
            {
                var duration = Widgets.Label(" " + badge + " ", Theme.SmallSize - 2, Theme.Text);
                duration.BackgroundColor = item.Live ? Theme.Accent : new Color(0, 0, 0, 0.82f);
                duration.CornerRadius = 4;
                duration.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
                duration.PivotPoint = Tizen.NUI.PivotPoint.BottomRight;
                duration.PositionUsesPivotPoint = true;
                duration.Position = new Position(width - 8, thumbH - 10);
                thumb.Add(duration);
            }

            info = new View
            {
                Position = new Position(0, thumbH),
                Size = new Size(width, Theme.CardInfoHeight),
                BackgroundColor = Theme.CardInfo,
                CornerRadius = Theme.CardRadius,
            };
            Add(info);
            infoTopPatch = Widgets.Box(Theme.CardInfo, width, Theme.CardRadius);
            info.Add(infoTopPatch);

            // On top of the info box's edge: the watched-fraction bar sits on the seam.
            progress = Widgets.Box(Theme.Accent, 0, 5);
            progress.Position = new Position(0, thumbH - 5);
            Add(progress);
            UpdateProgress();

            title = Widgets.Label(item.Title, Theme.BodySize, Theme.Text, width - 24, 62);
            title.MultiLine = true;
            title.Position = new Position(12, 8);
            info.Add(title);

            string line = item.Channel;
            if (!string.IsNullOrEmpty(item.Views))
            {
                line += " • " + item.Views;
            }
            if (!string.IsNullOrEmpty(item.Published))
            {
                line += " • " + item.Published;
            }
            meta = Widgets.Label(line, Theme.SmallSize, Theme.TextDim, width - 24);
            meta.Position = new Position(12, 70);
            info.Add(meta);
        }

        public void UpdateProgress()
        {
            // Local position (watched here) or YouTube's (watched on another device), whichever is further.
            double p = Math.Max(LocalStore.Progress(Item.Id), Item.WatchedPercent / 100.0);
            progress.SizeWidth = (float)(width * p);
            Widgets.SetVisible(progress, p > 0.01);
        }

        // Thumbnails are fetched only when the card is about to be visible.
        public void LoadImage()
        {
            if (!imageLoaded && !string.IsNullOrEmpty(Item.Thumbnail))
            {
                imageLoaded = true;
                thumb.ResourceUrl = Item.Thumbnail;
            }
        }

        public void SetFocused(bool focused)
        {
            info.BackgroundColor = focused ? Theme.CardInfoFocused : Theme.CardInfo;
            infoTopPatch.BackgroundColor = info.BackgroundColor;
            title.TextColor = focused ? Theme.CardTextFocused : Theme.Text;
            meta.TextColor = focused ? Theme.CardTextFocused : Theme.TextDim;
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
    }
}
