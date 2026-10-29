using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OmegaTube.Config;
using OmegaTube.Data;
using OmegaTube.Util;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Two panes: categories on the left, the category's options as cards on the right.
    //  - categories: ▲▼ choose, ▶ / OK enter, Retour leaves;
    //  - options: ▲▼ move, ◀ ▶ change (or flip a switch), OK flips / activates, Retour back to the categories.
    //  The "Menu" category reorders the rail sections: OK picks a section up, ▲▼ move it, OK drops it.
    //  "About" shows the release notes of a newer version (startup or manual check) in a scrolling panel.
    public class SettingsScreen : Screen
    {
        private class Row
        {
            public string Title, Hint;
            public Func<string> Value;       // choice rows ("‹ value ›" when focused)
            public Func<bool> Switch;        // on/off rows
            public Action<int> Change;       // ◀ ▶ (-1 / +1); switch rows flip
            public Action Activate;          // OK
            public Color Dot;                // SponsorBlock category colour
            public HomeRow Home;             // "Menu" rows
            public string Section;           // a small heading above this row
            public View View;
            public TextLabel TitleLabel, HintLabel, ValueLabel;
            public View SwitchView;
        }

        private class Category
        {
            public string Title, Icon, Description;
            public Action<List<Row>> Build;
            public bool CaptionPreview;
            public View View;
            public ImageView IconView;
            public TextLabel Label;
        }

        private const int PaneWidth = 600;
        private const int ListX = 660, ListY = 220, ListW = 1180, ListH = 770;
        private const int RowH = 100, RowGap = 14, SectionH = 64;

        private static readonly int[] SeekSteps = { 5, 10, 15, 20, 30, 60 };
        private static readonly int[] Heights = { 720, 1080, 1440, 2160 };
        private static readonly string[] CodecSets = { "avc1,vp9", "vp9,avc1", "avc1,vp9,av01" };
        private static readonly string[] CodecSetLabels = { "H.264, VP9 above 1080p", "VP9 first", "H.264 / VP9 / AV1 (experimental)" };
        private static readonly string[] Locales = { "fr-FR", "fr-BE", "fr-CA", "fr-CH", "en-US", "en-GB", "de-DE", "es-ES", "it-IT" };

        private readonly List<Category> categories = new List<Category>();
        private readonly List<Row> rows = new List<Row>();
        private readonly TextLabel paneTitle, paneDescription, help;
        private readonly View list, listClip;
        private readonly View preview;
        private readonly TextLabel previewCaption;
        private int category, index;
        private bool inRows;
        private HomeRow moving;            // "Menu": section being moved
        private Animation scroll;
        private string updateStatus = "";
        private int aboutCategory;
        private Layer notesLayer;          // release notes panel, while open
        private TextLabel notesText;
        private float notesScroll;
        private bool relabel;              // the app language changed: every text has to be rebuilt

        // startCategory / startRow: where to come back after a rebuild (app language change).
        public SettingsScreen(App app, int startCategory = 0, int startRow = -1) : base(app)
        {
            Root.BackgroundColor = Theme.Background;

            var pane = Widgets.Box(Theme.Panel, PaneWidth, 1080);
            Root.Add(pane);
            var title = Widgets.Label(T("Settings"), Theme.TitleSize + 12, Theme.Text, 480, 70);
            title.Position = new Position(80, 64);
            Root.Add(title);
            var version = Widgets.Label("OmegaTube " + NativeCore.Version, Theme.SmallSize, Theme.TextMuted, 480);
            version.Position = new Position(82, 134);
            Root.Add(version);

            paneTitle = Widgets.Label("", Theme.TitleSize + 4, Theme.Text, ListW, 60);
            paneTitle.Position = new Position(ListX, 70);
            Root.Add(paneTitle);
            paneDescription = Widgets.Label("", Theme.BodySize, Theme.TextDim, ListW, 40);
            paneDescription.Position = new Position(ListX, 136);
            Root.Add(paneDescription);

            listClip = new View { Position = new Position(ListX - 20, ListY - 20), Size = new Size(ListW + 40, ListH + 20),
                                  ClippingMode = ClippingModeType.ClipChildren };
            Root.Add(listClip);
            list = new View { Position = new Position(20, 20), Size = new Size(ListW, 4000) };
            listClip.Add(list);

            // Subtitle style sample (Sous-titres et audio).
            preview = Widgets.Rounded(Theme.Surface, ListW, 200, Theme.PanelRadius);
            preview.ClippingMode = ClippingModeType.ClipChildren;
            var sky = Widgets.Box(new Color(0.20f, 0.30f, 0.42f, 1f), ListW, 120);
            preview.Add(sky);
            var ground = Widgets.Box(new Color(0.22f, 0.32f, 0.20f, 1f), ListW, 80);
            ground.PositionY = 120;
            preview.Add(ground);
            previewCaption = Widgets.Label("", 40, Color.White);
            previewCaption.MultiLine = true;
            previewCaption.HorizontalAlignment = HorizontalAlignment.Center;
            previewCaption.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            previewCaption.PivotPoint = Tizen.NUI.PivotPoint.BottomCenter;
            previewCaption.PositionUsesPivotPoint = true;
            previewCaption.Position = new Position(ListW / 2, 180);
            preview.Add(previewCaption);
            preview.Hide();
            list.Add(preview);

            help = Widgets.Label("", Theme.SmallSize + 1, Theme.TextDim, ListW, 40);
            help.Position = new Position(ListX, 1012);
            Root.Add(help);

            AddCategory(T("Player"), "play", T("Controls and behaviour during playback"), BuildPlayer);
            AddCategory(T("Video quality"), "quality", T("Resolution and codecs requested from YouTube"), BuildQuality);
            AddCategory(T("Subtitles and audio"), "subtitles", T("Languages and appearance of subtitles"), BuildCaptions, true);
            AddCategory("SponsorBlock", "shield", T("Skipping sponsors and other segments reported by the community"), BuildSponsorBlock);
            AddCategory(T("Menu"), "view_list", T("Sidebar sections (add some from Search)"), BuildMenu);
            AddCategory(T("General"), "language", T("App language, content language and updates"), BuildGeneral);
            aboutCategory = categories.Count;
            AddCategory(T("About"), "info_outline", T("Version, license and credits"), BuildAbout);
            ShowCategory(startCategory);
            if (startRow >= 0 && startRow < rows.Count)
            {
                inRows = true;
                index = startRow;
                Refresh();
            }
        }

        private PlayerOptions P => App.Config.Player;

        // ---------------------------------------------------------------- categories

        private void AddCategory(string title, string icon, string description, Action<List<Row>> build, bool captionPreview = false)
        {
            int i = categories.Count;
            var view = Widgets.Rounded(Color.Transparent, PaneWidth - 100, 80, 40);
            view.Position = new Position(50, 220 + i * 92);
            var img = Widgets.Icon(icon, 40);
            img.Position = new Position(28, 20);
            view.Add(img);
            var label = Widgets.Label(title, Theme.BodySize + 4, Theme.Text, 380, 80);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Position = new Position(90, 0);
            view.Add(label);
            Root.Add(view);
            categories.Add(new Category { Title = title, Icon = icon, Description = description, Build = build,
                                          CaptionPreview = captionPreview, View = view, IconView = img, Label = label });
        }

        private void ShowCategory(int i)
        {
            category = i;
            index = 0;
            moving = null;
            BuildRows();
            list.PositionY = 20;
            scroll?.Stop();
            Refresh();
        }

        private void BuildRows()
        {
            foreach (View v in new List<View>(list.Children))
            {
                if (v != preview)
                {
                    list.Remove(v);
                    v.Dispose();
                }
            }
            rows.Clear();
            Category c = categories[category];
            c.Build(rows);
            paneTitle.Text = c.Title;
            paneDescription.Text = c.Description;

            int y = 0;
            foreach (Row r in rows)
            {
                if (r.Section != null)
                {
                    var heading = Widgets.Label(r.Section, Theme.BodySize, Theme.Accent, ListW, SectionH);
                    heading.VerticalAlignment = VerticalAlignment.Center;
                    heading.Position = new Position(8, y);
                    list.Add(heading);
                    y += SectionH;
                }
                MakeRowView(r);
                r.View.PositionY = y;
                list.Add(r.View);
                y += RowH + RowGap;
            }
            Widgets.SetVisible(preview, c.CaptionPreview);
            if (c.CaptionPreview)
            {
                preview.PositionY = y + 10;
            }
        }

        private void MakeRowView(Row r)
        {
            r.View = Widgets.Rounded(Theme.Panel, ListW, RowH, 18);
            int x = 36;
            if (r.Dot != null)
            {
                var dot = Widgets.Rounded(r.Dot, 18, 18, 9);
                dot.Position = new Position(36, RowH / 2 - 9);
                r.View.Add(dot);
                x = 72;
            }
            bool hint = !string.IsNullOrEmpty(r.Hint);
            r.TitleLabel = Widgets.Label(r.Title, Theme.BodySize + 4, Theme.Text, 640, hint ? 44 : RowH);
            r.TitleLabel.VerticalAlignment = hint ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            r.TitleLabel.Position = new Position(x, hint ? 10 : 0);
            r.View.Add(r.TitleLabel);
            if (hint)
            {
                r.HintLabel = Widgets.Label(r.Hint, Theme.SmallSize, Theme.TextDim, 680, 34);
                r.HintLabel.Position = new Position(x, 56);
                r.View.Add(r.HintLabel);
            }
            if (r.Switch != null)
            {
                r.SwitchView = Widgets.Switch(r.Switch());
                r.SwitchView.Position = new Position(ListW - 112, (RowH - 40) / 2);
                r.View.Add(r.SwitchView);
            }
            if (r.Value != null)
            {
                int right = r.Switch != null ? ListW - 140 : ListW - 36;
                r.ValueLabel = Widgets.Label("", Theme.BodySize + 2, Theme.TextDim, 460, RowH);
                r.ValueLabel.HorizontalAlignment = HorizontalAlignment.End;
                r.ValueLabel.VerticalAlignment = VerticalAlignment.Center;
                r.ValueLabel.Position = new Position(right - 460, 0);
                r.View.Add(r.ValueLabel);
            }
        }

        // ---------------------------------------------------------------- option lists

        private void BuildPlayer(List<Row> l)
        {
            l.Add(Toggle(T("Autoplay"), T("Plays the suggested video when the current one ends"),
                         () => P.Autoplay, v => P.Autoplay = v));
            l.Add(Choice(T("Skip with ◀ ▶"), T("Fast forward / rewind with the remote"),
                         () => $"{P.SeekStepSeconds} s", d => P.SeekStepSeconds = Cycle(SeekSteps, P.SeekStepSeconds, d)));
            l.Add(Choice(T("Hide controls after"), T("Delay before the playback bar disappears"),
                         () => $"{P.OsdTimeoutSeconds} s", d => P.OsdTimeoutSeconds = Math.Max(2, Math.Min(15, P.OsdTimeoutSeconds + d))));
            l.Add(Toggle(T("Clock"), T("Time shown with the controls"), () => P.ShowClock, v => P.ShowClock = v));
            l.Add(Toggle(T("Stats for nerds"), T("Bitrate, buffer and stalls overlay"),
                         () => P.ShowStats, v => P.ShowStats = v));
        }

        private void BuildQuality(List<Row> l)
        {
            l.Add(Choice(T("Maximum quality"), T("The best available quality up to this limit"),
                         () => HeightLabel(P.MaxHeight), d => P.MaxHeight = Cycle(Heights, P.MaxHeight, d)));
            l.Add(Choice(T("Video codecs"), T("Above 1080p, YouTube only offers VP9 or AV1"),
                         () => T(CodecSetLabels[Math.Max(0, Array.IndexOf(CodecSets, P.Codecs))]),
                         d =>
                         {
                             int i = Array.IndexOf(CodecSets, P.Codecs);
                             P.Codecs = CodecSets[i < 0 ? 0 : (i + d + CodecSets.Length) % CodecSets.Length];
                         }));
        }

        private void BuildCaptions(List<Row> l)
        {
            l.Add(Choice(T("Subtitles"), T("Turned on automatically in this language when the video has them"),
                         () => P.CaptionLang.Length == 0 ? T("Off") : Languages.Name(P.CaptionLang),
                         d => P.CaptionLang = CycleLang(P.CaptionLang, d)));
            l.Add(Toggle(T("Auto-translate"), T("Otherwise, translate subtitles from another language"),
                         () => P.CaptionTranslate, v => P.CaptionTranslate = v));
            l.Add(Choice(T("Text size"), null, () => T(PlayerOptions.CaptionSizeLabels[P.CaptionSize]),
                         d => P.CaptionSize = Clamp(P.CaptionSize + d, PlayerOptions.CaptionSizes.Length)));
            l.Add(Choice(T("Subtitle background"), null, () => T(PlayerOptions.CaptionBackgroundLabels[P.CaptionBackground]),
                         d => P.CaptionBackground = Clamp(P.CaptionBackground + d, PlayerOptions.CaptionBackgrounds.Length)));
            l.Add(Choice(T("Audio track"), T("For videos dubbed in several languages"),
                         () => P.AudioLang.Length == 0 ? T("Original") : Languages.Name(P.AudioLang),
                         d => P.AudioLang = CycleLang(P.AudioLang, d)));
        }

        private void BuildSponsorBlock(List<Row> l)
        {
            l.Add(Toggle("SponsorBlock", T("Segments reported by the community (sponsor.ajay.app)"),
                         () => App.Config.SponsorBlockEnabled, v => App.Config.SponsorBlockEnabled = v));
            bool first = true;
            foreach (SponsorCategory cat in SponsorCategory.All)
            {
                SponsorCategory c = cat;
                Row r = Choice(T(c.Label), null, () => App.Config.SponsorBlockEnabled ? ModeLabel(App.Config.ModeForRaw(c)) : "—",
                               d =>
                               {
                                   int next = ((int)App.Config.ModeForRaw(c) + d + 3) % 3;
                                   App.Config.SkipModes[c.Id] = (SkipMode)next;
                               });
                r.Dot = c.Color;
                r.Section = first ? T("Categories") : null;
                first = false;
                l.Add(r);
            }
        }

        private void BuildMenu(List<Row> l)
        {
            foreach (HomeRow row in App.Config.HomeRows)
            {
                HomeRow h = row;
                l.Add(new Row
                {
                    Home = h,
                    Title = h.Title,
                    Hint = h.Title != h.Query ? T("Search: {0}", h.Query) : null,
                    Switch = () => h.Visible,
                    Change = d => h.Visible = !h.Visible,
                    Activate = () => moving = moving == h ? null : h,
                });
            }
        }

        private void BuildGeneral(List<Row> l)
        {
            l.Add(Choice(T("App language"), T("Language of the app's texts"),
                         () => I18n.Names[Math.Max(0, Array.IndexOf(I18n.Codes, App.Config.UiLanguage))],
                         d =>
                         {
                             int i = Array.IndexOf(I18n.Codes, App.Config.UiLanguage);
                             i = (Math.Max(0, i) + d + I18n.Codes.Length) % I18n.Codes.Length;
                             App.Config.UiLanguage = I18n.Codes[i];
                             I18n.Lang = App.Config.UiLanguage;
                             App.Config.RelabelDefaultRows();
                             relabel = true;
                         }));
            l.Add(Choice(T("YouTube language and region"), T("Language of titles, searches and trends"),
                         () => $"{App.Config.Language}-{App.Config.Region}",
                         d =>
                         {
                             int i = (Array.IndexOf(Locales, App.Config.Language + "-" + App.Config.Region) + d + Locales.Length) % Locales.Length;
                             string[] parts = Locales[i].Split('-');
                             App.Config.Language = parts[0];
                             App.Config.Region = parts[1];
                             NativeCore.SetLocale(parts[0], parts[1]);
                         }));
            l.Add(Toggle(T("Updates"), T("Report a new version at startup"),
                         () => App.Config.CheckUpdates, v => App.Config.CheckUpdates = v));
        }

        private void BuildAbout(List<Row> l)
        {
            l.Add(new Row { Title = T("Version"), Value = () => NativeCore.Version });
            l.Add(new Row
            {
                Title = T("Check for updates"),
                Hint = T("Compares with the latest version published on GitHub"),
                Value = () => updateStatus,
                Activate = () => _ = CheckUpdate(),
            });
            if (App.NewRelease != null)
            {
                l.Add(new Row
                {
                    Title = T("What's new in {0}", Json.Str(App.NewRelease, "version")),
                    Hint = T("Release notes of the new version"),
                    Value = () => "",
                    Activate = OpenNotes,
                });
            }
            l.Add(new Row { Title = T("License"), Hint = T("Free software, no ads and no data collection"), Value = () => "GPL-3.0" });
            l.Add(new Row { Title = T("Credits"), Hint = T("Interface inspired by SmartTube · SponsorBlock segments · SmartTube and Material icons"),
                            Value = () => "" });
            l.Add(new Row { Title = T("YouTube is a trademark of Google LLC"), Hint = T("OmegaTube is an independent project, not affiliated with Google"),
                            Value = () => "" });
        }

        private async Task CheckUpdate()
        {
            updateStatus = T("Checking…");
            Refresh();
            object update = await Task.Run(() => YouTube.Call(NativeCore.UpdateCheck()));
            if (Json.Str(update, "error", null) != null)
            {
                updateStatus = T("Could not check");
            }
            else
            {
                bool available = Json.Bool(update, "available");
                App.NewRelease = available ? update : null;
                updateStatus = available ? T("Version {0} available", Json.Str(update, "version")) : T("Up to date");
                if (available)
                {
                    App.Home.ShowUpdate(Json.Str(update, "version"), Json.Str(update, "url"));
                }
                if (category == aboutCategory)
                {
                    BuildRows();   // adds / removes the release notes row
                    index = Clamp(index, rows.Count);
                }
            }
            Refresh();
        }

        // ---------------------------------------------------------------- release notes

        // In a layer of its own above the window: drawn over the settings whatever their clipping (the list's
        // clipped rows would otherwise be drawn over a panel added to Root).
        private void OpenNotes()
        {
            notesLayer = new Layer();
            Window.Instance.AddLayer(notesLayer);
            var scrim = Widgets.Box(Theme.Scrim, 1920, 1080);
            notesLayer.Add(scrim);
            var card = Widgets.Rounded(Theme.Panel, 1200, 900, Theme.PanelRadius);
            card.Position = new Position(360, 90);
            scrim.Add(card);
            var title = Widgets.Label(T("What's new in {0}", Json.Str(App.NewRelease, "version")), Theme.TitleSize, Theme.Text, 1080, 60);
            title.Position = new Position(60, 40);
            card.Add(title);
            var install = Widgets.Label(T("To install it, run the install script again or reinstall by following the README."),
                                        Theme.SmallSize, Theme.Accent, 1080, 40);
            install.Position = new Position(60, 104);
            card.Add(install);
            var clip = new View { Position = new Position(60, 170), Size = new Size(1080, 640), ClippingMode = ClippingModeType.ClipChildren };
            card.Add(clip);
            string notes = PlainNotes(Json.Str(App.NewRelease, "notes"));
            notesText = Widgets.Label(notes.Length > 0 ? notes : T("No release notes."), Theme.BodySize, Theme.Text, 1080, 20000);
            notesText.MultiLine = true;
            notesText.Ellipsis = false;
            notesText.VerticalAlignment = VerticalAlignment.Top;
            clip.Add(notesText);
            var hint = Widgets.Label(T("▲▼ scroll    Back close"), Theme.SmallSize, Theme.TextDim, 1080, 36);
            hint.Position = new Position(60, 840);
            card.Add(hint);
            notesScroll = 0;
        }

        private void NotesKey(string key)
        {
            switch (key)
            {
                case "Up":
                case "Down":
                    float height = PlayerScreen.EstimateTextHeight(notesText.Text, 1080, Theme.BodySize);
                    float min = Math.Min(0, 620 - height);
                    notesScroll = Math.Max(min, Math.Min(0, notesScroll + (key == "Up" ? 300 : -300)));
                    notesText.PositionY = notesScroll;
                    break;
                case "Return":
                case "XF86Back":
                case "Escape":
                    CloseNotes();
                    break;
            }
        }

        private void CloseNotes()
        {
            Window.Instance.RemoveLayer(notesLayer);
            notesLayer.Dispose();
            notesLayer = null;
        }

        public override void OnHide()
        {
            if (notesLayer != null)
            {
                CloseNotes();
            }
            base.OnHide();
        }

        // GitHub release body (Markdown) as plain text: headings, emphasis and links simplified, "•" bullets.
        private static string PlainNotes(string markdown)
        {
            var sb = new StringBuilder();
            foreach (string raw in markdown.Replace("\r", "").Split('\n'))
            {
                string t = raw.Trim();
                string indent = new string(' ', raw.Length - raw.TrimStart().Length);
                string line = t.StartsWith("#") ? t.TrimStart('#').Trim()
                            : t.StartsWith("- ") || t.StartsWith("* ") ? indent + "•  " + t.Substring(2)
                            : indent + t;
                line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
                sb.Append(line.Replace("**", "").Replace("__", "").Replace("`", "")).Append('\n');
            }
            return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
        }

        private static Row Toggle(string title, string hint, Func<bool> get, Action<bool> set)
        {
            return new Row { Title = title, Hint = hint, Switch = get, Change = d => set(!get()), Activate = () => set(!get()) };
        }

        private static Row Choice(string title, string hint, Func<string> value, Action<int> change)
        {
            return new Row { Title = title, Hint = hint, Value = value, Change = change };
        }

        // ---------------------------------------------------------------- display

        private void Refresh()
        {
            for (int i = 0; i < categories.Count; i++)
            {
                Category c = categories[i];
                bool selected = i == category;
                bool focused = selected && !inRows;
                c.View.BackgroundColor = focused ? Theme.CardInfoFocused : selected ? Theme.SurfaceHigh : Color.Transparent;
                c.Label.TextColor = focused ? Theme.CardTextFocused : selected ? Theme.Text : Theme.TextDim;
                c.IconView.Color = focused ? new Color(0.16f, 0.16f, 0.16f, 1f) : selected ? Color.White : new Color(0.67f, 0.67f, 0.67f, 1f);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                bool f = inRows && i == index;
                bool lifted = r.Home != null && r.Home == moving;
                r.View.BackgroundColor = lifted ? Theme.Accent : f ? Theme.CardInfoFocused : Theme.Panel;
                r.View.Scale = f ? new Vector3(1.015f, 1.06f, 1f) : Vector3.One;
                Color text = f && !lifted ? Theme.CardTextFocused : Theme.Text;
                r.TitleLabel.TextColor = text;
                if (r.HintLabel != null)
                {
                    r.HintLabel.TextColor = f && !lifted ? new Color(0.33f, 0.33f, 0.33f, 1f) : Theme.TextDim;
                }
                if (r.SwitchView != null)
                {
                    Widgets.SetSwitch(r.SwitchView, r.Switch());
                }
                if (r.ValueLabel != null)
                {
                    string v = r.Value();
                    r.ValueLabel.Text = f && r.Change != null && v.Length > 0 ? "‹   " + v + "   ›" : v;
                    r.ValueLabel.TextColor = f && !lifted ? Theme.CardTextFocused : Theme.TextDim;
                }
            }
            if (categories[category].CaptionPreview)
            {
                float alpha = PlayerOptions.CaptionBackgrounds[P.CaptionBackground];
                previewCaption.PixelSize = PlayerOptions.CaptionSizes[P.CaptionSize];
                previewCaption.Text = " " + T("This is what subtitles will look like") + " \n" +
                                      (P.CaptionLang.Length == 0 ? " " + T("(off by default)") + " " : " " + Languages.Name(P.CaptionLang) + " ");
                previewCaption.BackgroundColor = new Color(0.03f, 0.03f, 0.03f, alpha);
                PlayerScreen.StyleCaption(previewCaption, alpha);
            }
            if (inRows && index < rows.Count)
            {
                float y = rows[index].View.PositionY;
                float target = Math.Min(20, ListH / 2f - y - RowH / 2f);
                scroll = Widgets.Slide(list, 20, target, scroll);
            }
            else if (!inRows)
            {
                scroll = Widgets.Slide(list, 20, 20, scroll);
            }
            help.Text = !inRows ? T("▲▼ categories    ▶ / OK open    Back leave")
                      : moving != null ? T("▲▼ move the section    OK drop")
                      : rows.Count > 0 && rows[index].Home != null ? T("◀ ▶ show / hide    OK move    Back categories")
                      : rows.Count > 0 && rows[index].Change != null ? T("◀ ▶ change    OK confirm    Back categories")
                      : rows.Count > 0 && rows[index].Activate != null ? T("OK run    Back categories")
                      : T("Back categories");
        }

        // ---------------------------------------------------------------- keys

        public override bool OnKey(string key)
        {
            if (notesLayer != null)
            {
                NotesKey(key);
                return true;
            }
            bool back = key == "XF86Back" || key == "Escape";
            if (!inRows)
            {
                switch (key)
                {
                    case "Up":
                        if (category > 0)
                        {
                            ShowCategory(category - 1);
                        }
                        break;
                    case "Down":
                        if (category < categories.Count - 1)
                        {
                            ShowCategory(category + 1);
                        }
                        break;
                    case "Right":
                    case "Return":
                        if (rows.Count > 0)
                        {
                            inRows = true;
                            index = 0;
                        }
                        break;
                    default:
                        if (back || key == "Left")
                        {
                            return false;   // back to the home rail
                        }
                        break;
                }
                Refresh();
                return true;
            }

            Row row = rows[index];
            if (moving != null)
            {
                if (key == "Up" || key == "Down")
                {
                    MoveRow(moving, key == "Up" ? -1 : 1);
                }
                else if (key == "Return" || back)
                {
                    moving = null;
                }
                Refresh();
                return true;
            }
            switch (key)
            {
                case "Up":
                    index = Math.Max(0, index - 1);
                    break;
                case "Down":
                    index = Math.Min(rows.Count - 1, index + 1);
                    break;
                case "Left":
                case "Right":
                    if (row.Change != null)
                    {
                        row.Change(key == "Left" ? -1 : 1);
                        Save();
                        if (relabel)
                        {
                            App.ReplaceScreen(this, new SettingsScreen(App, category, index));
                            return true;
                        }
                    }
                    else if (key == "Left")
                    {
                        inRows = false;
                    }
                    break;
                case "Return":
                    if (row.Activate != null)
                    {
                        row.Activate();
                        Save();
                    }
                    break;
                default:
                    if (back)
                    {
                        inRows = false;
                        moving = null;
                    }
                    break;
            }
            Refresh();
            return true;
        }

        private static string ModeLabel(SkipMode m) =>
            m == SkipMode.Auto ? T("Skip automatically") : m == SkipMode.Manual ? T("“Skip” button") : T("Do nothing");

        private static string HeightLabel(int h) => h == 2160 ? "2160p (4K)" : h == 1440 ? "1440p (2K)" : h + "p";

        private static int Cycle(int[] values, int current, int d)
        {
            int i = Array.IndexOf(values, current);
            i = i < 0 ? 0 : (i + d + values.Length) % values.Length;
            return values[i];
        }

        private static int Clamp(int i, int count) => Math.Max(0, Math.Min(count - 1, i));

        // "" (off / original) then each known language.
        private static string CycleLang(string current, int d)
        {
            int n = Languages.Codes.Length + 1;
            int i = current.Length == 0 ? 0 : Array.IndexOf(Languages.Codes, Languages.Base(current)) + 1;
            i = (i + d + n) % n;
            return i == 0 ? "" : Languages.Codes[i - 1];
        }

        // "Menu": swaps the section with its neighbour; the focus follows it.
        private void MoveRow(HomeRow r, int d)
        {
            List<HomeRow> homeRows = App.Config.HomeRows;
            int i = homeRows.IndexOf(r), j = i + d;
            if (i < 0 || j < 0 || j >= homeRows.Count)
            {
                return;
            }
            homeRows[i] = homeRows[j];
            homeRows[j] = r;
            Save();
            BuildRows();
            index = j;
        }

        private void Save()
        {
            App.Config.Save();
            App.Home.InvalidateRows();
        }
    }
}
