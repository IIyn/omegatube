using System;
using System.Collections.Generic;
using OmegaTube.Config;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Player options (◀ ▶ to change) and home rows (◀ ▶ to move, OK twice to remove).
    public class SettingsScreen : Screen
    {
        private class Entry
        {
            public Func<string> Text;
            public Action<int> Change;   // -1 / +1
            public Action Activate;
            public HomeRow Row;
        }

        private static readonly int[] SeekSteps = { 5, 10, 15, 30, 60 };
        private static readonly int[] Heights = { 720, 1080 };
        private static readonly string[] Locales = { "fr-FR", "fr-BE", "fr-CA", "en-US", "en-GB", "de-DE", "es-ES", "it-IT" };

        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<TextLabel> labels = new List<TextLabel>();       // every line (headers + entries)
        private readonly List<TextLabel> entryLabels = new List<TextLabel>();  // parallel to `entries`
        private int nextY;
        private readonly View list;
        private readonly TextLabel help;
        private int index;
        private Animation scroll;

        public SettingsScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;
            var title = Widgets.Label("Réglages", Theme.TitleSize + 8, Theme.Text, 1200);
            title.Position = new Position(120, 60);
            Root.Add(title);
            help = Widgets.Label("", Theme.BodySize, Theme.TextDim, 1700);
            help.Position = new Position(120, 990);
            Root.Add(help);
            var clip = new View { Position = new Position(120, 150), Size = new Size(1700, 820),
                                  ClippingMode = ClippingModeType.ClipChildren };
            Root.Add(clip);
            list = new View { Size = new Size(1700, 5000) };
            clip.Add(list);
            Build();
        }

        private PlayerOptions P => App.Config.Player;

        private void Build()
        {
            foreach (TextLabel l in labels)
            {
                list.Remove(l);
                l.Dispose();
            }
            labels.Clear();
            entries.Clear();
            entryLabels.Clear();
            nextY = 0;

            Header("Lecteur");
            AddEntry(new Entry
            {
                Text = () => $"Saut ◀ ▶ : {P.SeekStepSeconds} s",
                Change = d => P.SeekStepSeconds = Cycle(SeekSteps, P.SeekStepSeconds, d),
            });
            AddEntry(new Entry
            {
                Text = () => $"Masquage de la barre : {P.OsdTimeoutSeconds} s",
                Change = d => P.OsdTimeoutSeconds = Math.Max(2, Math.Min(15, P.OsdTimeoutSeconds + d)),
            });
            AddEntry(new Entry
            {
                Text = () => "Horloge dans la barre : " + (P.ShowClock ? "oui" : "non"),
                Change = d => P.ShowClock = !P.ShowClock,
            });
            AddEntry(new Entry
            {
                Text = () => "Statistiques techniques : " + (P.ShowStats ? "oui" : "non"),
                Change = d => P.ShowStats = !P.ShowStats,
            });
            AddEntry(new Entry
            {
                Text = () => $"Qualité maximale : {P.MaxHeight}p",
                Change = d => P.MaxHeight = Cycle(Heights, P.MaxHeight, d),
            });

            Header("Général");
            AddEntry(new Entry
            {
                Text = () => $"Langue / région YouTube : {App.Config.Language}-{App.Config.Region}",
                Change = d =>
                {
                    int i = (Array.IndexOf(Locales, App.Config.Language + "-" + App.Config.Region) + d + Locales.Length) % Locales.Length;
                    string[] parts = Locales[i].Split('-');
                    App.Config.Language = parts[0];
                    App.Config.Region = parts[1];
                    NativeCore.SetLocale(parts[0], parts[1]);
                },
            });
            AddEntry(new Entry
            {
                Text = () => "Vérifier les mises à jour au démarrage : " + (App.Config.CheckUpdates ? "oui" : "non"),
                Change = d => App.Config.CheckUpdates = !App.Config.CheckUpdates,
            });

            Header("SponsorBlock");
            AddEntry(new Entry
            {
                Text = () => "SponsorBlock : " + (App.Config.SponsorBlockEnabled ? "activé" : "désactivé"),
                Change = d => App.Config.SponsorBlockEnabled = !App.Config.SponsorBlockEnabled,
            });
            foreach (SponsorCategory cat in SponsorCategory.All)
            {
                SponsorCategory c = cat;
                AddEntry(new Entry
                {
                    Text = () => $"{c.Label} : " + ModeLabel(App.Config.ModeForRaw(c)),
                    Change = d =>
                    {
                        int next = ((int)App.Config.ModeForRaw(c) + d + 3) % 3;
                        App.Config.SkipModes[c.Id] = (SkipMode)next;
                    },
                });
            }

            Header("Sections du menu (ajoute-en depuis la Recherche)");
            foreach (HomeRow row in App.Config.HomeRows)
            {
                HomeRow r = row;
                AddEntry(new Entry
                {
                    Row = r,
                    Text = () => r.Title + (r.Title != r.Query ? $"  ({r.Query})" : "") + (r.Visible ? "  : affichée" : "  : masquée"),
                    Change = d => MoveRow(r, d),
                    Activate = () =>
                    {
                        r.Visible = !r.Visible;
                        Save();
                    },
                });
            }
            Refresh();
        }

        private void Header(string text)
        {
            var l = Widgets.Label(text, Theme.BodySize, Theme.Accent, 1600, 60);
            l.VerticalAlignment = VerticalAlignment.Bottom;
            l.Position = new Position(0, nextY + 20);
            nextY += 90;
            list.Add(l);
            labels.Add(l);
        }

        private void AddEntry(Entry entry)
        {
            var l = Widgets.Label("", Theme.BodySize + 4, Theme.Text, 1600, 64);
            l.VerticalAlignment = VerticalAlignment.Center;
            l.Position = new Position(0, nextY);
            nextY += 70;
            list.Add(l);
            labels.Add(l);
            entries.Add(entry);
            entryLabels.Add(l);
        }

        private void Refresh()
        {
            List<TextLabel> el = entryLabels;
            for (int i = 0; i < entries.Count && i < el.Count; i++)
            {
                el[i].Text = "  " + entries[i].Text();
                el[i].BackgroundColor = i == index ? Theme.SurfaceFocused : Color.Transparent;
            }
            if (index < el.Count)
            {
                float y = el[index].PositionY;
                scroll = Widgets.Slide(list, 0, Math.Min(0, 600 - y), scroll);
            }
            help.Text = entries.Count > 0 && entries[index].Row != null
                ? "◀ ▶ déplacer la section   OK afficher / masquer   Retour quitter"
                : "◀ ▶ modifier   Retour quitter";
        }

        public override bool OnKey(string key)
        {
            if (entries.Count == 0)
            {
                return key != "XF86Back" && key != "Escape";
            }
            Entry entry = entries[index];
            switch (key)
            {
                case "Up":
                    index = Math.Max(0, index - 1);
                    break;
                case "Down":
                    index = Math.Min(entries.Count - 1, index + 1);
                    break;
                case "Left":
                case "Right":
                    entry.Change?.Invoke(key == "Left" ? -1 : 1);
                    Save();
                    if (entry.Row != null)
                    {
                        Build();
                    }
                    break;
                case "Return":
                    entry.Activate?.Invoke();
                    break;
                case "XF86Back":
                case "Escape":
                    return false;
            }
            Refresh();
            return true;
        }

        private static string ModeLabel(SkipMode m) =>
            m == SkipMode.Auto ? "passer automatiquement" : m == SkipMode.Manual ? "bouton « Passer »" : "ne rien faire";

        private static int Cycle(int[] values, int current, int d)
        {
            int i = Array.IndexOf(values, current);
            i = i < 0 ? 0 : (i + d + values.Length) % values.Length;
            return values[i];
        }

        private void MoveRow(HomeRow r, int d)
        {
            List<HomeRow> rows = App.Config.HomeRows;
            int i = rows.IndexOf(r), j = i + d;
            if (i < 0 || j < 0 || j >= rows.Count)
            {
                return;
            }
            rows[i] = rows[j];
            rows[j] = r;
            index += d;
        }

        private void Save()
        {
            App.Config.Save();
            App.Home.InvalidateRows();
        }
    }
}
