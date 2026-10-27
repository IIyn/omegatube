using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Config;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // SmartTube-style main screen: the rail (Home, Subscriptions, History, the user's sections, then
    // Search / Account / Settings) and a grid showing the selected entry. Left from the grid's first
    // column moves into the rail; OK on a rail entry switches the grid to it.
    public class HomeScreen : Screen
    {
        private class Section
        {
            public string Title, Icon, CacheKey, EmptyText;
            public Func<string, Task<FeedPage>> Loader;   // null: a screen (Push) or nothing to show
            public Func<Screen> Open;
        }

        private const int ContentX = Theme.RailWidth + 20;
        private const int GridTop = 110;

        private readonly List<Section> sections = new List<Section>();
        private readonly TextLabel header;
        private readonly FeedGrid grid;
        private Rail rail;
        private TextLabel updateBanner;
        private int current;     // section shown in the grid
        private bool inRail;
        private bool dirty = true;

        public HomeScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;
            header = Widgets.Label("", Theme.TitleSize + 2, Theme.Text, 1200);
            header.Position = new Position(ContentX + Theme.FocusMargin, 44);
            Root.Add(header);
            grid = new FeedGrid(1920 - ContentX, 1080 - GridTop) { Position = new Position(ContentX, GridTop) };
            Root.Add(grid);
        }

        // A newer GitHub release exists: the app cannot install itself, so point to the release page.
        public void ShowUpdate(string version, string url)
        {
            if (updateBanner == null)
            {
                updateBanner = Widgets.Label("", Theme.SmallSize, Theme.Text, 0, 0);
                updateBanner.BackgroundColor = Theme.Accent;
                updateBanner.CornerRadius = 6;
                updateBanner.Position = new Position(1000, 50);
                Root.Add(updateBanner);
            }
            updateBanner.Text = $"  Nouvelle version {version} disponible : {url.Replace("https://", "")}  ";
        }

        // Sections or the account changed: rebuild the rail and reload on next show.
        public void InvalidateRows()
        {
            dirty = true;
        }

        public override void OnShow()
        {
            if (dirty)
            {
                Rebuild();
            }
            else
            {
                grid.RefreshProgress();
            }
            UpdateFocus();
        }

        private void Rebuild()
        {
            dirty = false;
            bool signedIn = YouTube.SignedIn();
            const string signIn = "Connecte ton compte (menu Compte) pour voir ";
            sections.Clear();
            sections.Add(new Section
            {
                Title = "Accueil",
                Icon = "home",
                CacheKey = "home",
                Loader = signedIn ? (Func<string, Task<FeedPage>>)(c => YouTube.Browse("FEwhat_to_watch", c)) : null,
                EmptyText = signIn + "tes recommandations, ou utilise la Recherche et les sections du menu.",
            });
            sections.Add(new Section { Title = "Recherche", Icon = "search", Open = () => new SearchScreen(App) });
            sections.Add(new Section
            {
                Title = "Abonnements", Icon = "subscriptions", CacheKey = "subscriptions",
                Loader = signedIn ? (Func<string, Task<FeedPage>>)(c => YouTube.Browse("FEsubscriptions", c)) : null,
                EmptyText = signIn + "les vidéos de tes abonnements.",
            });
            sections.Add(new Section
            {
                Title = "Historique", Icon = "history", CacheKey = "history",
                Loader = signedIn ? (Func<string, Task<FeedPage>>)(c => YouTube.Browse("FEhistory", c)) : null,
                EmptyText = signIn + "ton historique.",
            });
            foreach (HomeRow row in App.Config.HomeRows)
            {
                if (!row.Visible)
                {
                    continue;
                }
                string query = row.Query;
                sections.Add(new Section
                {
                    Title = row.Title, Icon = row.Icon, CacheKey = "search:" + query,
                    Loader = c => YouTube.Search(query, c),
                });
            }
            sections.Add(new Section { Title = "Compte", Icon = "set_account", Open = () => new AccountScreen(App) });
            sections.Add(new Section { Title = "Réglages", Icon = "settings", Open = () => new SettingsScreen(App) });

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
            Root.Add(rail);  // last: the expanded rail draws over the grid

            // Signed out, Home has nothing: start on the first section instead.
            current = signedIn ? 0 : Math.Min(3, sections.Count - 1);
            if (sections[current].Loader == null)
            {
                current = 0;
            }
            ShowSection(current);
        }

        private void ShowSection(int index)
        {
            current = index;
            Section s = sections[index];
            header.Text = s.Title;
            grid.Show(s.Loader, s.CacheKey, s.EmptyText);
            rail.Select(index);
            inRail = s.Loader == null;   // nothing to browse: stay in the rail
        }

        private void UpdateFocus()
        {
            rail.SetFocused(inRail);
            grid.SetFocused(!inRail);
        }

        public override bool OnKey(string key)
        {
            bool back = key == "XF86Back" || key == "Escape";
            if (inRail)
            {
                switch (key)
                {
                    case "Up":
                        rail.Move(-1);
                        break;
                    case "Down":
                        rail.Move(1);
                        break;
                    case "Right":
                        if (grid.Count > 0)
                        {
                            inRail = false;
                            rail.Select(current);
                        }
                        break;
                    case "Return":
                        Section s = sections[rail.Index];
                        if (s.Open != null)
                        {
                            App.Push(s.Open());
                            return true;
                        }
                        ShowSection(rail.Index);
                        inRail = s.Loader == null;
                        break;
                    default:
                        if (back)
                        {
                            return false;   // exits the app
                        }
                        return true;
                }
                UpdateFocus();
                return true;
            }

            if (back)
            {
                inRail = true;
            }
            else if (key == "Return")
            {
                if (grid.FocusedItem != null)
                {
                    App.Play(grid.FocusedItem);
                }
                return true;
            }
            else if (!grid.Move(key) && key == "Left")
            {
                inRail = true;
            }
            UpdateFocus();
            return true;
        }
    }
}
