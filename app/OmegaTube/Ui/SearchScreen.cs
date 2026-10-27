using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Config;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // QWERTY on-screen keyboard + live suggestions + one row of results.
    public class SearchScreen : Screen
    {
        private static readonly string[][] Keys =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "'" },
            new[] { "z", "x", "c", "v", "b", "n", "m", "-", ".", "é" },
            new[] { "ESPACE", "← SUPPR.", "EFFACER", "RECHERCHER" },
        };
        private const int KeyW = 86, KeyH = 72, KeyGap = 8;
        private const int KbX = 80, KbY = 190;

        private enum Zone { Keyboard, Suggestions, AddButton, Results }

        private readonly TextLabel queryLabel;
        private readonly TextLabel addButton;
        private readonly List<List<TextLabel>> keyLabels = new List<List<TextLabel>>();
        private readonly List<TextLabel> suggestionLabels = new List<TextLabel>();
        private readonly View resultsHost;
        private List<string> suggestions = new List<string>();
        private FeedRow results;
        private string query = "";
        private Zone zone = Zone.Keyboard;
        private int keyRow = 1, keyCol;
        private int suggestionIndex;
        private int suggestGeneration;

        public SearchScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;

            var box = Widgets.Box(Theme.Surface, 900, 80);
            box.Position = new Position(KbX, 70);
            Root.Add(box);
            queryLabel = Widgets.Label("", Theme.TitleSize, Theme.Text, 860, 80);
            queryLabel.Position = new Position(20, 0);
            queryLabel.VerticalAlignment = VerticalAlignment.Center;
            box.Add(queryLabel);

            addButton = Widgets.Label("  + Ajouter au menu  ", Theme.BodySize, Theme.Text, 0, 0);
            addButton.Position = new Position(KbX + 930, 86);
            addButton.BackgroundColor = Theme.Surface;
            addButton.Hide();
            Root.Add(addButton);

            for (int r = 0; r < Keys.Length; r++)
            {
                var line = new List<TextLabel>();
                int x = KbX;
                for (int c = 0; c < Keys[r].Length; c++)
                {
                    // The last row has 4 wide keys spanning the 10 columns.
                    int w = r == Keys.Length - 1 ? (10 * (KeyW + KeyGap) - KeyGap - 3 * KeyGap) / 4 : KeyW;
                    var key = Widgets.Label(Keys[r][c].ToUpperInvariant() == "ESPACE" ? "Espace" : Keys[r][c], Theme.BodySize + 4,
                                            Theme.Text, w, KeyH);
                    key.HorizontalAlignment = HorizontalAlignment.Center;
                    key.VerticalAlignment = VerticalAlignment.Center;
                    key.BackgroundColor = Theme.Surface;
                    key.Position = new Position(x, KbY + r * (KeyH + KeyGap));
                    Root.Add(key);
                    line.Add(key);
                    x += w + KeyGap;
                }
                keyLabels.Add(line);
            }

            var sugTitle = Widgets.Label("Suggestions", Theme.BodySize, Theme.TextDim, 700);
            sugTitle.Position = new Position(1060, 190);
            Root.Add(sugTitle);
            for (int i = 0; i < 7; i++)
            {
                var s = Widgets.Label("", Theme.BodySize + 2, Theme.Text, 780, 50);
                s.Position = new Position(1060, 240 + i * 54);
                s.VerticalAlignment = VerticalAlignment.Center;
                Root.Add(s);
                suggestionLabels.Add(s);
            }

            resultsHost = new View { Position = new Position(KbX - Theme.FocusMargin, 610), Size = new Size(1920 - KbX + Theme.FocusMargin, 470),
                                     ClippingMode = ClippingModeType.ClipChildren };
            Root.Add(resultsHost);
            UpdateFocus();
        }

        private int SuggestionCount => System.Math.Min(suggestions.Count, suggestionLabels.Count);

        private void UpdateFocus()
        {
            queryLabel.Text = query.Length > 0 ? query + "_" : "Rechercher sur YouTube…";
            queryLabel.TextColor = query.Length > 0 ? Theme.Text : Theme.TextDim;
            for (int r = 0; r < keyLabels.Count; r++)
            {
                for (int c = 0; c < keyLabels[r].Count; c++)
                {
                    bool f = zone == Zone.Keyboard && r == keyRow && c == keyCol;
                    keyLabels[r][c].BackgroundColor = f ? Theme.Accent : Theme.Surface;
                }
            }
            for (int i = 0; i < suggestionLabels.Count; i++)
            {
                suggestionLabels[i].Text = i < suggestions.Count ? "  " + suggestions[i] : "";
                suggestionLabels[i].BackgroundColor = zone == Zone.Suggestions && i == suggestionIndex
                    ? Theme.SurfaceFocused : Color.Transparent;
            }
            addButton.BackgroundColor = zone == Zone.AddButton ? Theme.Accent : Theme.Surface;
            results?.SetFocused(zone == Zone.Results);
        }

        public override bool OnKey(string key)
        {
            switch (zone)
            {
                case Zone.Keyboard:
                    if (!KeyboardKey(key))
                    {
                        return false;
                    }
                    break;
                case Zone.Suggestions:
                    if (key == "Up")
                    {
                        suggestionIndex--;
                        if (suggestionIndex < 0)
                        {
                            suggestionIndex = 0;
                        }
                    }
                    else if (key == "Down")
                    {
                        suggestionIndex = System.Math.Min(SuggestionCount - 1, suggestionIndex + 1);
                    }
                    else if (key == "Left")
                    {
                        zone = Zone.Keyboard;
                    }
                    else if (key == "Return" && suggestionIndex < suggestions.Count)
                    {
                        query = suggestions[suggestionIndex];
                        RunSearch();
                    }
                    else if (key == "XF86Back" || key == "Escape")
                    {
                        zone = Zone.Keyboard;
                    }
                    break;
                case Zone.AddButton:
                    if (key == "Down" || key == "XF86Back" || key == "Escape")
                    {
                        zone = Zone.Keyboard;
                    }
                    else if (key == "Return")
                    {
                        AddToHome();
                    }
                    break;
                case Zone.Results:
                    if (key == "Up" || key == "XF86Back" || key == "Escape")
                    {
                        zone = Zone.Keyboard;
                        keyRow = Keys.Length - 1;
                    }
                    else if (key == "Left")
                    {
                        results.Move(-1);
                    }
                    else if (key == "Right")
                    {
                        results.Move(1);
                    }
                    else if (key == "Return" && results.FocusedItem != null)
                    {
                        App.Play(results.FocusedItem);
                    }
                    break;
            }
            UpdateFocus();
            return true;
        }

        private bool KeyboardKey(string key)
        {
            switch (key)
            {
                case "Up":
                    if (keyRow == 0)
                    {
                        if (results != null)
                        {
                            zone = Zone.AddButton;
                        }
                    }
                    else
                    {
                        keyRow--;
                        keyCol = System.Math.Min(keyCol, Keys[keyRow].Length - 1);
                    }
                    break;
                case "Down":
                    if (keyRow == Keys.Length - 1)
                    {
                        if (results != null && results.Count > 0)
                        {
                            zone = Zone.Results;
                        }
                    }
                    else
                    {
                        keyRow++;
                        // Map the 10 columns onto the 4 wide keys of the last row.
                        keyCol = keyRow == Keys.Length - 1 ? System.Math.Min(3, keyCol * 4 / 10) : keyCol;
                    }
                    break;
                case "Left":
                    keyCol = System.Math.Max(0, keyCol - 1);
                    break;
                case "Right":
                    if (keyCol == Keys[keyRow].Length - 1)
                    {
                        if (SuggestionCount > 0)
                        {
                            zone = Zone.Suggestions;
                            suggestionIndex = 0;
                        }
                    }
                    else
                    {
                        keyCol++;
                    }
                    break;
                case "Return":
                    PressKey(Keys[keyRow][keyCol]);
                    break;
                case "XF86Back":
                case "Escape":
                    return false;
            }
            return true;
        }

        private void PressKey(string k)
        {
            switch (k)
            {
                case "ESPACE":
                    query += " ";
                    break;
                case "← SUPPR.":
                    if (query.Length > 0)
                    {
                        query = query.Substring(0, query.Length - 1);
                    }
                    break;
                case "EFFACER":
                    query = "";
                    break;
                case "RECHERCHER":
                    RunSearch();
                    return;
                default:
                    query += k;
                    break;
            }
            _ = RefreshSuggestions();
        }

        // Debounced: only the latest keystroke's request updates the list.
        private async Task RefreshSuggestions()
        {
            int generation = ++suggestGeneration;
            await Task.Delay(250);
            if (generation != suggestGeneration)
            {
                return;
            }
            List<string> list = query.Trim().Length == 0 ? new List<string>() : await YouTubeSuggest(query);
            if (generation != suggestGeneration)
            {
                return;
            }
            suggestions = list;
            if (zone == Zone.Suggestions && SuggestionCount == 0)
            {
                zone = Zone.Keyboard;
            }
            UpdateFocus();
        }

        private static Task<List<string>> YouTubeSuggest(string q) => Data.YouTube.Suggest(q);

        private void RunSearch()
        {
            string q = query.Trim();
            if (q.Length == 0)
            {
                return;
            }
            if (results != null)
            {
                resultsHost.Remove(results);
                results.Dispose();
            }
            results = new FeedRow("Résultats pour « " + q + " »", q, 1920 - KbX);
            resultsHost.Add(results);
            addButton.Show();
            zone = Zone.Keyboard;
            _ = LoadResults();
        }

        private async Task LoadResults()
        {
            FeedRow row = results;
            await row.Load();
            if (row == results && row.Count > 0 && zone == Zone.Keyboard)
            {
                zone = Zone.Results;
                UpdateFocus();
            }
        }

        private void AddToHome()
        {
            string q = results?.Query;
            if (string.IsNullOrEmpty(q))
            {
                return;
            }
            App.Config.HomeRows.Add(new HomeRow { Title = q, Query = q, Icon = HomeRow.GuessIcon(q) });
            App.Config.Save();
            App.Home.InvalidateRows();
            addButton.Text = "  ✓ Ajouté au menu  ";
        }
    }
}
