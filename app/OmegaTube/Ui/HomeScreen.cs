using System.Collections.Generic;
using OmegaTube.Config;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Sidebar (Home / Search / Settings) + vertically scrolling rows of feeds from the config.
    public class HomeScreen : Screen
    {
        private static readonly string[] MenuItems = { "Accueil", "Recherche", "Compte", "Réglages" };

        private readonly List<TextLabel> menuLabels = new List<TextLabel>();
        private readonly View rowsContainer;
        private readonly List<FeedRow> rows = new List<FeedRow>();
        private readonly TextLabel emptyHint;
        private bool inMenu;
        private int menuIndex;
        private int rowIndex;
        private Animation scroll;
        private bool rowsDirty = true;

        private TextLabel updateBanner;

        public HomeScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;

            var sidebar = Widgets.Box(Theme.Surface, Theme.SidebarWidth, 1080);
            Root.Add(sidebar);
            var logo = Widgets.Label("OmegaTube", Theme.TitleSize, Theme.Accent, Theme.SidebarWidth - 40);
            logo.Position = new Position(30, 50);
            sidebar.Add(logo);
            for (int i = 0; i < MenuItems.Length; i++)
            {
                var label = Widgets.Label(MenuItems[i], Theme.BodySize + 4, Theme.TextDim, Theme.SidebarWidth - 40, 56);
                label.Position = new Position(30, 180 + i * 70);
                label.VerticalAlignment = VerticalAlignment.Center;
                sidebar.Add(label);
                menuLabels.Add(label);
            }

            var clip = new View
            {
                Position = new Position(Theme.SidebarWidth + 30, 0),
                Size = new Size(1920 - Theme.SidebarWidth - 30, 1080),
                ClippingMode = ClippingModeType.ClipChildren,
            };
            Root.Add(clip);
            rowsContainer = new View { Position = new Position(0, 60), Size = new Size(1920, 10000) };
            clip.Add(rowsContainer);

            emptyHint = Widgets.Label("Aucune rangée : ajoute des recherches à l'accueil depuis l'écran Recherche.",
                                      Theme.BodySize, Theme.TextDim, 1400);
            emptyHint.Position = new Position(Theme.SidebarWidth + 60, 120);
            Root.Add(emptyHint);
        }

        // A newer GitHub release exists: the app cannot install itself, so point to the release page.
        public void ShowUpdate(string version, string url)
        {
            if (updateBanner == null)
            {
                updateBanner = Widgets.Label("", Theme.SmallSize, Theme.Text, 0, 0);
                updateBanner.BackgroundColor = Theme.Accent;
                updateBanner.Position = new Position(Theme.SidebarWidth + 60, 12);
                Root.Add(updateBanner);
            }
            updateBanner.Text = $"  Nouvelle version {version} disponible : {url.Replace("https://", "")}  ";
        }

        // The config changed (rows added/removed/moved): rebuild on next show.
        public void InvalidateRows()
        {
            rowsDirty = true;
        }

        public override void OnShow()
        {
            if (rowsDirty)
            {
                BuildRows();
            }
            UpdateFocus();
        }

        private void BuildRows()
        {
            rowsDirty = false;
            foreach (FeedRow r in rows)
            {
                rowsContainer.Remove(r);
                r.Dispose();
            }
            rows.Clear();
            rowIndex = 0;
            int width = 1920 - Theme.SidebarWidth - 30;
            bool signedIn = Data.YouTube.SignedIn();
            foreach (HomeRow cfg in App.Config.HomeRows)
            {
                if (cfg.NeedsAccount && !signedIn)
                {
                    continue;  // account feeds appear once signed in
                }
                string browseId = cfg.BrowseId;
                FeedRow row = browseId != null
                    ? new FeedRow(cfg.Title, c => Data.YouTube.Browse(browseId, c), width)
                    : new FeedRow(cfg.Title, cfg.Query, width);
                row.Position = new Position(0, rows.Count * Theme.RowHeight);
                rows.Add(row);
                rowsContainer.Add(row);
                _ = row.Load();
            }
            rowsContainer.PositionY = 60;
            Widgets.SetVisible(emptyHint, rows.Count == 0);
            inMenu = rows.Count == 0;
        }

        private void UpdateFocus()
        {
            for (int i = 0; i < menuLabels.Count; i++)
            {
                bool active = inMenu && i == menuIndex;
                menuLabels[i].TextColor = active ? Theme.Text : (i == 0 ? Theme.Accent : Theme.TextDim);
                menuLabels[i].BackgroundColor = active ? Theme.SurfaceFocused : Color.Transparent;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].SetFocused(!inMenu && i == rowIndex);
            }
        }

        public override bool OnKey(string key)
        {
            if (inMenu)
            {
                switch (key)
                {
                    case "Up":
                        menuIndex = System.Math.Max(0, menuIndex - 1);
                        break;
                    case "Down":
                        menuIndex = System.Math.Min(MenuItems.Length - 1, menuIndex + 1);
                        break;
                    case "Right":
                        if (rows.Count > 0)
                        {
                            inMenu = false;
                        }
                        break;
                    case "Return":
                        ActivateMenu();
                        break;
                    case "XF86Back":
                    case "Escape":
                        return false;
                    default:
                        return true;
                }
                UpdateFocus();
                return true;
            }

            FeedRow row = rows[rowIndex];
            switch (key)
            {
                case "Left":
                    if (!row.Move(-1))
                    {
                        inMenu = true;
                        menuIndex = 0;
                    }
                    break;
                case "Right":
                    row.Move(1);
                    break;
                case "Up":
                    if (rowIndex > 0)
                    {
                        rowIndex--;
                        scroll = Widgets.Slide(rowsContainer, 0, 60 - rowIndex * Theme.RowHeight, scroll);
                    }
                    break;
                case "Down":
                    if (rowIndex < rows.Count - 1)
                    {
                        rowIndex++;
                        scroll = Widgets.Slide(rowsContainer, 0, 60 - rowIndex * Theme.RowHeight, scroll);
                    }
                    break;
                case "Return":
                    if (row.FocusedItem != null)
                    {
                        App.Play(row.FocusedItem);
                    }
                    break;
                case "XF86Back":
                case "Escape":
                    // Back from the content goes to the menu first, then exits.
                    inMenu = true;
                    menuIndex = 0;
                    break;
                default:
                    return true;
            }
            UpdateFocus();
            return true;
        }

        private void ActivateMenu()
        {
            switch (menuIndex)
            {
                case 0:
                    if (rows.Count > 0)
                    {
                        inMenu = false;
                    }
                    break;
                case 1:
                    App.Push(new SearchScreen(App));
                    break;
                case 2:
                    App.Push(new AccountScreen(App));
                    break;
                case 3:
                    App.Push(new SettingsScreen(App));
                    break;
            }
        }
    }
}
