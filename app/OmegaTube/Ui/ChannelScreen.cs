using System;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // A channel: header (avatar, name, subscribers, subscribe button) above a grid of its videos, newest first.
    // Up from the grid's first row reaches the subscribe button.
    public class ChannelScreen : Screen
    {
        private const int GridTop = 210;

        private readonly string channelId;
        private readonly ImageView avatar;
        private readonly TextLabel nameLabel, subsLabel;
        private readonly Widgets.PillButton subscribeButton;
        private readonly FeedGrid grid;
        private readonly bool signedIn;
        private int subscribed;    // 1 / 0 / -1 unknown
        private bool onButton;
        private bool busy;
        private bool loaded;

        public ChannelScreen(App app, string channelId, string name, string avatarUrl, int subscribed) : base(app)
        {
            this.channelId = channelId;
            this.subscribed = subscribed;
            signedIn = YouTube.SignedIn();
            Root.BackgroundColor = Theme.Background;

            var avatarBack = Widgets.Rounded(Theme.Surface, 120, 120, 60);
            avatarBack.Position = new Position(80, 50);
            Root.Add(avatarBack);
            avatar = new ImageView { Size = new Size(120, 120), CornerRadius = 60, Position = new Position(80, 50) };
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                avatar.ResourceUrl = avatarUrl;
            }
            Root.Add(avatar);
            nameLabel = Widgets.Label(name ?? "", Theme.TitleSize + 10, Theme.Text, 1100, 64);
            nameLabel.Position = new Position(230, 58);
            Root.Add(nameLabel);
            subsLabel = Widgets.Label("", Theme.BodySize + 2, Theme.TextDim, 1100, 40);
            subsLabel.Position = new Position(232, 126);
            Root.Add(subsLabel);

            subscribeButton = new Widgets.PillButton("", null, 300, 76);
            subscribeButton.Position = new Position(1540, 72);
            Widgets.SetVisible(subscribeButton, signedIn);
            Root.Add(subscribeButton);

            grid = new FeedGrid(1920 - 60, 1080 - GridTop) { Position = new Position(30, GridTop) };
            Root.Add(grid);
            grid.Show(LoadPage, null, T("No videos"));
            RefreshButton();
        }

        // The first page also carries the channel header.
        private async Task<FeedPage> LoadPage(string continuation)
        {
            FeedPage page = await YouTube.Channel(channelId, continuation);
            if (page.Error == null && string.IsNullOrEmpty(continuation))
            {
                if (page.Title.Length > 0)
                {
                    nameLabel.Text = page.Title;
                }
                if (page.Avatar.Length > 0 && string.IsNullOrEmpty(avatar.ResourceUrl))
                {
                    avatar.ResourceUrl = page.Avatar;
                }
                subsLabel.Text = page.Subscribers;
                if (!loaded)
                {
                    loaded = true;
                    grid.SetFocused(!onButton);
                }
            }
            return page;
        }

        private void RefreshButton()
        {
            subscribeButton.Text = subscribed == 1 ? T("Subscribed") : T("Subscribe");
            subscribeButton.SetFocused(onButton);
            if (!onButton)
            {
                // Unfocused: YouTube's red "Subscribe", grey once subscribed.
                subscribeButton.BackgroundColor = subscribed == 1 ? Theme.SurfaceHigh : Theme.Accent;
            }
            grid.SetFocused(!onButton);
        }

        private async Task ToggleSubscription()
        {
            if (busy)
            {
                return;
            }
            busy = true;
            bool subscribe = subscribed != 1;
            string error = await YouTube.Subscribe(channelId, subscribe);
            busy = false;
            if (error == null)
            {
                subscribed = subscribe ? 1 : 0;
                App.Home.InvalidateRows();   // the Subscriptions feed changed
            }
            else
            {
                subsLabel.Text = T("Failed: {0}", error);
            }
            RefreshButton();
        }

        public override void OnShow()
        {
            grid.RefreshProgress();
        }

        public override bool OnKey(string key)
        {
            if (key == "XF86Back" || key == "Escape")
            {
                if (onButton)
                {
                    onButton = false;
                    RefreshButton();
                    return true;
                }
                return false;
            }
            if (onButton)
            {
                if (key == "Return")
                {
                    _ = ToggleSubscription();
                }
                else if (key == "Down" && grid.Count > 0)
                {
                    onButton = false;
                }
                RefreshButton();
                return true;
            }
            if (key == "Return")
            {
                if (grid.FocusedItem != null)
                {
                    App.Play(grid.FocusedItem);
                }
                return true;
            }
            if (!grid.Move(key) && key == "Up" && signedIn)
            {
                onButton = true;
                RefreshButton();
            }
            return true;
        }
    }
}
