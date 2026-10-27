using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Video thumbnail + title + channel/meta line.
    public class Card : View
    {
        public VideoItem Item { get; }

        private readonly View frame;
        private readonly ImageView thumb;
        private bool imageLoaded;

        public Card(VideoItem item)
        {
            Item = item;
            Size = new Size(Theme.CardWidth, Theme.CardHeight);

            frame = Widgets.Box(Theme.Accent, Theme.CardWidth + 12, Theme.ThumbHeight + 12);
            frame.Position = new Position(-6, -6);
            frame.Hide();
            Add(frame);

            thumb = new ImageView { Size = new Size(Theme.CardWidth, Theme.ThumbHeight), BackgroundColor = Theme.Surface };
            Add(thumb);

            string badge = item.Live ? "EN DIRECT" : item.Duration;
            if (!string.IsNullOrEmpty(badge))
            {
                var duration = Widgets.Label(" " + badge + " ", Theme.SmallSize, Theme.Text);
                duration.BackgroundColor = item.Live ? Theme.Accent : new Color(0, 0, 0, 0.8f);
                duration.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
                duration.PivotPoint = Tizen.NUI.PivotPoint.BottomRight;
                duration.PositionUsesPivotPoint = true;
                duration.Position = new Position(Theme.CardWidth - 8, Theme.ThumbHeight - 8);
                thumb.Add(duration);
            }

            var title = Widgets.Label(item.Title, Theme.BodySize, Theme.Text, Theme.CardWidth, 64);
            title.MultiLine = true;
            title.Position = new Position(0, Theme.ThumbHeight + 10);
            Add(title);

            string meta = item.Channel;
            if (!string.IsNullOrEmpty(item.Views))
            {
                meta += " • " + item.Views;
            }
            if (!string.IsNullOrEmpty(item.Published))
            {
                meta += " • " + item.Published;
            }
            var metaLabel = Widgets.Label(meta, Theme.SmallSize, Theme.TextDim, Theme.CardWidth);
            metaLabel.Position = new Position(0, Theme.ThumbHeight + 76);
            Add(metaLabel);
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
            if (focused)
            {
                frame.Show();
                Scale = new Vector3(1.05f, 1.05f, 1f);
            }
            else
            {
                frame.Hide();
                Scale = Vector3.One;
            }
        }
    }
}
