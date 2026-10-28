using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // A playlist: its name and a "Play all" button above a grid of its videos, in order. The picked video
    // plays within the playlist (Next and the end of the video continue with the following ones).
    // Up from the grid's first row reaches the button.
    public class PlaylistScreen : Screen
    {
        private const int GridTop = 210;

        private readonly VideoItem list;
        private readonly TextLabel titleLabel, countLabel;
        private readonly Widgets.PillButton playAll;
        private readonly FeedGrid grid;
        private int count;
        private bool onButton = true;   // until the grid has videos

        public PlaylistScreen(App app, VideoItem list) : base(app)
        {
            this.list = list;
            Root.BackgroundColor = Theme.Background;

            var icon = Widgets.Icon("view_list", 64);
            icon.Position = new Position(80, 78);
            Root.Add(icon);
            titleLabel = Widgets.Label(list.Title ?? "", Theme.TitleSize + 10, Theme.Text, 1300, 64);
            titleLabel.Position = new Position(170, 58);
            Root.Add(titleLabel);
            countLabel = Widgets.Label(list.Channel ?? "", Theme.BodySize + 2, Theme.TextDim, 1300, 40);
            countLabel.Position = new Position(172, 126);
            Root.Add(countLabel);

            playAll = new Widgets.PillButton(T("Play all"), "play", 300, 76);
            playAll.Position = new Position(1540, 72);
            Root.Add(playAll);

            grid = new FeedGrid(1920 - 60, 1080 - GridTop) { Position = new Position(30, GridTop) };
            Root.Add(grid);
            grid.Show(LoadPage, null, T("No videos"));
            RefreshFocus();
        }

        private async Task<FeedPage> LoadPage(string continuation)
        {
            FeedPage page = await YouTube.PlaylistPage(list.PlaylistId, continuation);
            if (page.Error != null)
            {
                return page;
            }
            if (string.IsNullOrEmpty(continuation) && page.Title.Length > 0)
            {
                titleLabel.Text = page.Title;
            }
            count += page.Items.Count;
            countLabel.Text = (string.IsNullOrEmpty(list.Channel) ? "" : list.Channel + "  •  ") +
                              (count == 1 ? T("{0} video", count) : T("{0} videos", count)) +
                              (string.IsNullOrEmpty(page.Continuation) ? "" : "+");
            if (onButton && count > 0 && string.IsNullOrEmpty(continuation))
            {
                onButton = false;
                RefreshFocus();
            }
            return page;
        }

        private void RefreshFocus()
        {
            playAll.SetFocused(onButton);
            grid.SetFocused(!onButton);
        }

        public override void OnShow()
        {
            grid.RefreshProgress();
        }

        public override bool OnKey(string key)
        {
            if (key == "XF86Back" || key == "Escape")
            {
                return false;
            }
            if (onButton)
            {
                if (key == "Return")
                {
                    // From the start: the card's first video, within the playlist.
                    App.Push(new PlayerScreen(App, new VideoItem
                    {
                        Id = list.Id, Title = list.Title, Thumbnail = list.Thumbnail, PlaylistId = list.PlaylistId,
                    }));
                }
                else if (key == "Down" && grid.Count > 0)
                {
                    onButton = false;
                    RefreshFocus();
                }
                return true;
            }
            if (key == "Return")
            {
                if (grid.FocusedItem != null)
                {
                    App.Play(grid.FocusedItem);   // a video of the list: Kind "video" with its PlaylistId
                }
                return true;
            }
            if (!grid.Move(key) && key == "Up")
            {
                onButton = true;
                RefreshFocus();
            }
            return true;
        }
    }
}
