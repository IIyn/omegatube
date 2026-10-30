using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Config;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // SmartTube-style main screen: the rail (Home, Search, Subscriptions, History, the user's sections, then
    // Account / Settings) and, beside it, the selected entry. Moving in the rail opens the entry under the focus
    // after a short pause; Right / OK enters it, Left from its left edge or Back comes back to the rail.
    // Home shows YouTube's rows by topic, feeds a grid; Search, Account and Settings are whole screens hosted
    // beside the rail (scaled down a little, so their 1920-wide layouts fit).
    // Switching entries must not stall the remote: each entry keeps its own view once built (switching is a
    // show / hide; Home and Subscriptions refresh in the background each time, other feeds once older than
    // StaleAfter), and while the remote rests the entries not opened yet are built ahead (Prewarm).
    public class HomeScreen : Screen
    {
        private class Section
        {
            public string Key, Title, Icon, CacheKey, EmptyText;
            public Func<string, Task<FeedPage>> Loader;   // null: nothing to show (signed out) or a hosted screen
            public bool Shelves;                          // rows by topic (Home) instead of a grid
            public bool RefreshOnOpen;                    // new content each time it is opened (recommendations)
            public Func<Screen> Embed;                    // a screen shown beside the rail
        }

        private const int ContentX = Theme.RailWidth + 20;
        private const int GridTop = 110;
        private const int OpenDelayMs = 350;   // pause on a rail entry before it opens
        private const float EmbedScale = (1920f - Theme.RailWidth) / 1920f;
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private const int PrewarmIdleMs = 1500;   // remote untouched that long: build one entry ahead

        private readonly List<Section> sections = new List<Section>();
        private readonly Dictionary<string, Screen> embedded = new Dictionary<string, Screen>();
        private readonly TextLabel header;
        private readonly Dictionary<string, View> feeds = new Dictionary<string, View>();   // FeedGrid / ShelfList
        private readonly Dictionary<string, DateTime> loadedAt = new Dictionary<string, DateTime>();
        private readonly View feedHost;
        private View feedView;     // feed on display, if any
        private readonly View embedHost;
        private Rail rail;
        private TextLabel updateBanner;
        private Screen shown;      // hosted screen on display, if any
        private Timer openTimer, prewarmTimer;
        private string signature;  // sections as last built: rebuild the rail only when they change
        private int current;       // section shown beside the rail
        private bool inRail;
        private bool dirty = true;

        public HomeScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;
            header = Widgets.Label("", Theme.TitleSize + 2, Theme.Text, 1200);
            header.Position = new Position(ContentX + Theme.FocusMargin, 44);
            Root.Add(header);
            feedHost = new View { Size = new Size(1920, 1080) };   // under the rail, which is added last
            Root.Add(feedHost);
            embedHost = new View { Position = new Position(Theme.RailWidth, 0), Size = new Size(1920 - Theme.RailWidth, 1080),
                                   ClippingMode = ClippingModeType.ClipChildren };
            embedHost.Hide();
            Root.Add(embedHost);
        }

        // A newer GitHub release exists: the app cannot install itself, so point to the release page.
        public void ShowUpdate(string version, string url)
        {
            if (updateBanner == null)
            {
                updateBanner = Widgets.Label("", Theme.SmallSize, Theme.Text, 0, 0);
                updateBanner.BackgroundColor = Theme.Accent;
                Widgets.Round(updateBanner, 6);
                updateBanner.Position = new Position(1000, 50);
                Root.Add(updateBanner);
            }
            updateBanner.Text = "  " + T("New version {0} available: {1}", version, url.Replace("https://", "")) + "  ";
            // A Settings built ahead lacks the "What's new" row: built again when next opened.
            if (embedded.TryGetValue("settings", out Screen settings) && settings != shown)
            {
                DropEmbedded("settings");
            }
        }

        private IFeedView Feed => feedView as IFeedView;

        // Something to look at: rows of the open feed, or a hosted screen (the launch screen waits for this).
        public bool HasContent => Feed == null || Feed.Count > 0;

        private void DropEmbedded(string key)
        {
            embedHost.Remove(embedded[key].Root);
            embedded[key].Root.Dispose();
            embedded.Remove(key);
        }

        // Sections or the account changed: rebuild the rail and reload at the next safe point.
        public void InvalidateRows()
        {
            dirty = true;
        }

        // A hosted screen replacing itself (Settings after an app language change).
        public void ReplaceEmbedded(Screen old, Screen replacement)
        {
            foreach (KeyValuePair<string, Screen> e in embedded)
            {
                if (e.Value != old)
                {
                    continue;
                }
                old.OnHide();
                embedHost.Remove(old.Root);
                old.Root.Dispose();
                embedded[e.Key] = replacement;
                Host(replacement);
                if (shown == old)
                {
                    shown = replacement;
                    replacement.OnShow();
                }
                else
                {
                    replacement.Root.Hide();
                }
                return;
            }
        }

        public override void OnShow()
        {
            if (dirty)
            {
                ApplyPending(reloadFeed: true);
            }
            else
            {
                // Back from another screen (often a video just watched): History has changed.
                loadedAt.Remove("history");
                if (sections[current].Key == "history")
                {
                    ShowSection(current);
                }
                Feed?.RefreshProgress();
                shown?.OnShow();
            }
            UpdateFocus();
            StartPrewarm();
        }

        public override void OnHide()
        {
            openTimer?.Stop();
            prewarmTimer?.Stop();
            prewarmTimer = null;
            shown?.OnHide();
        }

        public override void OnPause()
        {
            shown?.OnPause();
        }

        // Rebuilds what InvalidateRows asked for. reloadFeed: also refresh the feed on display.
        private void ApplyPending(bool reloadFeed)
        {
            if (!dirty)
            {
                return;
            }
            dirty = false;
            if (!Rebuild() && reloadFeed)
            {
                // Same entries, but their content changed (subscription, account): every feed is outdated.
                loadedAt.Clear();
                if (sections[current].Embed == null)
                {
                    ShowSection(current);
                }
            }
        }

        private List<Section> BuildSections()
        {
            bool signedIn = YouTube.SignedIn();
            var list = new List<Section>
            {
                new Section
                {
                    Key = "home", Title = T("Home"), Icon = "home", CacheKey = "home-shelves", Shelves = true, RefreshOnOpen = true,
                    Loader = signedIn ? (Func<string, Task<FeedPage>>)(c => YouTube.Browse("FEwhat_to_watch", c)) : null,
                    EmptyText = T("Sign in to your account (Account menu) to see your recommendations, or use Search and the menu sections."),
                },
                new Section { Key = "search", Title = T("Search"), Icon = "search", Embed = () => new SearchScreen(App) },
                new Section
                {
                    Key = "subscriptions", Title = T("Subscriptions"), Icon = "subscriptions", CacheKey = "subscriptions",
                    RefreshOnOpen = true,
                    Loader = signedIn ? (Func<string, Task<FeedPage>>)(c => YouTube.Browse("FEsubscriptions", c)) : null,
                    EmptyText = T("Sign in to your account (Account menu) to see the videos of your subscriptions."),
                },
                new Section
                {
                    Key = "history", Title = T("History"), Icon = "history", CacheKey = "history",
                    Loader = signedIn ? (Func<string, Task<FeedPage>>)(c => YouTube.Browse("FEhistory", c)) : null,
                    EmptyText = T("Sign in to your account (Account menu) to see your history."),
                },
            };
            foreach (HomeRow row in App.Config.HomeRows)
            {
                if (!row.Visible)
                {
                    continue;
                }
                string query = row.Query;
                list.Add(new Section
                {
                    Key = "search:" + query, Title = row.Title, Icon = row.Icon, CacheKey = "search:" + query,
                    Loader = c => YouTube.Search(query, c),
                });
            }
            list.Add(new Section { Key = "account", Title = T("Account"), Icon = "set_account", Embed = () => new AccountScreen(App) });
            list.Add(new Section { Key = "settings", Title = T("Settings"), Icon = "settings", Embed = () => new SettingsScreen(App) });
            return list;
        }

        // Returns false when the sections are unchanged (nothing rebuilt).
        private bool Rebuild()
        {
            List<Section> next = BuildSections();
            var sig = new System.Text.StringBuilder();
            foreach (Section s in next)
            {
                sig.Append(s.Key).Append('|').Append(s.Title).Append('|').Append(s.Icon).Append('|').Append(s.Loader != null).Append('\n');
            }
            if (rail != null && sig.ToString() == signature)
            {
                return false;
            }
            signature = sig.ToString();
            string currentKey = rail != null && current < sections.Count ? sections[current].Key : null;
            sections.Clear();
            sections.AddRange(next);

            // Hosted screens carry texts of the previous language / account: rebuilt when next opened.
            foreach (string key in new List<string>(embedded.Keys))
            {
                if (embedded[key] != shown)
                {
                    DropEmbedded(key);
                }
            }
            // Feeds too (signed in / out, language, section list): built again when opened.
            foreach (View v in feeds.Values)
            {
                feedHost.Remove(v);
                v.Dispose();
            }
            feeds.Clear();
            loadedAt.Clear();
            feedView = null;

            if (rail != null)
            {
                Root.Remove(rail);
                rail.Dispose();
            }
            var entries = new List<Rail.Entry>();
            foreach (Section s in sections)
            {
                entries.Add(new Rail.Entry { Icon = s.Icon, Label = s.Title });
            }
            rail = new Rail(entries);
            Root.Add(rail);  // last: the expanded rail draws over the content

            int index = currentKey != null ? sections.FindIndex(s => s.Key == currentKey) : -1;
            if (index < 0)
            {
                // Signed out, Home has nothing: start on the first feed section instead.
                bool signedIn = sections[0].Loader != null;
                index = signedIn ? 0 : sections.FindIndex(s => s.Loader != null);
                index = Math.Max(0, index);
                inRail = !signedIn;
            }
            ShowSection(index);
            return true;
        }

        private void Host(Screen screen)
        {
            View r = screen.Root;
            r.WidthResizePolicy = ResizePolicyType.Fixed;
            r.HeightResizePolicy = ResizePolicyType.Fixed;
            r.Size = new Size(1920, 1080);
            r.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            r.PivotPoint = Tizen.NUI.PivotPoint.TopLeft;
            r.PositionUsesPivotPoint = true;
            r.Scale = new Vector3(EmbedScale, EmbedScale, 1f);
            r.Position = new Position(0, (1080 - 1080 * EmbedScale) / 2);
            embedHost.Add(r);
        }

        private void ShowSection(int index)
        {
            openTimer?.Stop();
            current = index;
            Section s = sections[index];
            rail.Select(index);
            if (s.Embed != null)
            {
                Screen screen = EmbeddedFor(s);
                header.Hide();
                ShowFeed(null);
                if (shown != screen)
                {
                    HideEmbedded();
                    shown = screen;
                    screen.Root.Show();
                    screen.OnShow();
                }
                embedHost.Show();
                return;
            }
            HideEmbedded();
            embedHost.Hide();
            header.Show();
            header.Text = s.Title;
            bool built = feeds.ContainsKey(s.Key);
            View v = FeedFor(s);
            ShowFeed(v);
            if (built)
            {
                Feed.RefreshProgress();
                if (s.RefreshOnOpen || !loadedAt.TryGetValue(s.Key, out DateTime at) || DateTime.Now - at > StaleAfter)
                {
                    loadedAt[s.Key] = DateTime.Now;
                    Feed.Refresh();
                }
            }
        }

        private void ShowFeed(View v)
        {
            if (feedView == v)
            {
                return;
            }
            if (feedView != null)
            {
                Feed.SetFocused(false);
                feedView.Hide();
            }
            feedView = v;
            if (v != null)
            {
                v.Show();
                Feed.Wake();
            }
        }

        // The entry's feed view, built (hidden) and loading on first use.
        private View FeedFor(Section s)
        {
            if (feeds.TryGetValue(s.Key, out View v))
            {
                return v;
            }
            if (s.Shelves && s.Loader != null)
            {
                var list = new ShelfList(1920 - ContentX, 1080 - GridTop + 20) { Position = new Position(ContentX, GridTop - 20) };
                list.Show(s.Loader, s.CacheKey);
                v = list;
            }
            else
            {
                var grid = new FeedGrid(1920 - ContentX, 1080 - GridTop) { Position = new Position(ContentX, GridTop) };
                grid.Show(s.Loader, s.CacheKey, s.EmptyText);
                v = grid;
            }
            v.Hide();
            feedHost.Add(v);
            feeds[s.Key] = v;
            loadedAt[s.Key] = DateTime.Now;
            return v;
        }

        private Screen EmbeddedFor(Section s)
        {
            if (!embedded.TryGetValue(s.Key, out Screen screen))
            {
                screen = s.Embed();
                embedded[s.Key] = screen;
                Host(screen);
                screen.Root.Hide();
            }
            return screen;
        }

        // While the remote rests, builds one entry not opened yet per tick: the hosted screens (whose
        // construction is the heavy part of their first opening), then the account feeds. Search sections
        // are left to be opened (each would cost a search request).
        private void StartPrewarm()
        {
            if (prewarmTimer != null)
            {
                return;
            }
            prewarmTimer = new Timer(500);
            prewarmTimer.Tick += (sender, e) =>
            {
                if (!UiWork.IdleFor(PrewarmIdleMs))
                {
                    return true;
                }
                foreach (Section s in sections)
                {
                    if (s.Embed != null && !embedded.ContainsKey(s.Key))
                    {
                        EmbeddedFor(s);
                        return true;
                    }
                }
                foreach (Section s in sections)
                {
                    if (s.Embed == null && s.Loader != null && !s.Key.StartsWith("search:") && !feeds.ContainsKey(s.Key))
                    {
                        FeedFor(s);
                        return true;
                    }
                }
                prewarmTimer = null;
                return false;
            };
            prewarmTimer.Start();
        }

        private void HideEmbedded()
        {
            if (shown != null)
            {
                shown.OnHide();
                shown.Root.Hide();
                shown = null;
            }
        }

        // The rail's entry opens once the focus rests on it.
        private void ScheduleOpen()
        {
            openTimer?.Stop();
            openTimer = new Timer(OpenDelayMs);
            openTimer.Tick += (s, e) =>
            {
                if (inRail && rail.Index != current)
                {
                    ShowSection(rail.Index);
                    UpdateFocus();
                }
                return false;
            };
            openTimer.Start();
        }

        // Something to move to beside the rail.
        private bool ContentFocusable()
        {
            Section s = sections[current];
            if (s.Embed != null)
            {
                return true;
            }
            return Feed != null && Feed.Count > 0;
        }

        private void UpdateFocus()
        {
            rail.SetFocused(inRail);
            Feed?.SetFocused(!inRail && shown == null);
        }

        public override bool OnKey(string key)
        {
            bool back = key == "XF86Back" || key == "Escape";
            if (inRail)
            {
                switch (key)
                {
                    case "Up":
                    case "Down":
                        rail.Move(key == "Up" ? -1 : 1);
                        ScheduleOpen();
                        break;
                    case "Right":
                    case "Return":
                        if (rail.Index != current)
                        {
                            ShowSection(rail.Index);
                        }
                        if (ContentFocusable())
                        {
                            inRail = false;
                        }
                        break;
                    default:
                        if (back)
                        {
                            return false;   // asks to quit the app
                        }
                        return true;
                }
                UpdateFocus();
                return true;
            }

            if (shown != null)
            {
                // A hosted screen: Back / Left it does not use bring the focus back to the rail.
                if (!shown.OnKey(key) && (back || key == "Left"))
                {
                    inRail = true;
                }
                ApplyPending(reloadFeed: false);
            }
            else if (back)
            {
                inRail = true;
            }
            else if (key == "Return")
            {
                VideoItem item = Feed?.FocusedItem;
                if (item != null)
                {
                    App.Play(item);
                }
                return true;
            }
            else if (Feed != null && !Feed.Move(key) && key == "Left")
            {
                inRail = true;
            }
            UpdateFocus();
            return true;
        }
    }
}
