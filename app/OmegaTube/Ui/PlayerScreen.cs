using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Config;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Full-screen video (ESPlayer plane under a transparent UI) with SmartTube-style controls
    // (layout as in SmartTube / ytps5's player chrome):
    //  - hidden while playing; OK pauses and shows them, ◀ ▶ seek, any other key shows them;
    //  - title + channel, a full-width seek bar with the SponsorBlock segments, and a row of round
    //    buttons: restart, -N s, play/pause, +N s, next | skip segment, speed, quality, stats;
    //  - Down from the buttons slides the block up and reveals the suggestions row.
    public class PlayerScreen : Screen
    {
        // Player teardown joins native threads: serialise create/destroy so two players never coexist
        // (the TV has one hardware decoder; a second player would get "resource conflicted").
        private static Task lastTeardown = Task.CompletedTask;

        private enum Focus { Hidden, Buttons, Suggestions, Panel }
        private enum Action { Restart, Rewind, PlayPause, Forward, Next, Skip, Speed, Quality, Stats }

        private class Button
        {
            public Action Action;
            public View View;
            public ImageView Icon;
            public TextLabel Label;   // the skip button is a pill with text
        }

        private static readonly double[] Speeds = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        private static readonly int[] DefaultHeights = { 2160, 1440, 1080, 720, 480, 360 };  // until the core answers
        private const int ProgressWidth = 1760, ProgressX = 80;
        private const int ChromeLift = 330;   // how far the block slides up for the suggestions

        private readonly VideoItem item;
        private readonly View chrome;          // everything at the bottom; slides up for suggestions
        private readonly TextLabel titleLabel, timeLabel, durationLabel, clockLabel, stateLabel, statsLabel, toast;
        private readonly View backdrop;        // opaque while loading: the TV picture must not show through
        private readonly View spinner;
        private readonly Animation spin;
        private readonly View track, progressFill, playhead;
        private readonly List<Button> buttons = new List<Button>();
        private readonly Button skipButton, playButton;
        private readonly View panel;
        private readonly List<TextLabel> panelLabels = new List<TextLabel>();
        private FeedRow suggestions;
        private Focus focus = Focus.Hidden;
        private int buttonIndex;
        private Action panelKind;
        private int panelIndex;
        private Animation slide;

        private IntPtr player = IntPtr.Zero;
        private bool closing;
        private Timer tick;
        private DateTime chromeUntil;
        private DateTime toastUntil;
        private long pendingSeekMs = -1;   // accumulated target while the user holds Left/Right
        private DateTime pendingSeekAt;
        private NativeCore.PlayerStats stats;
        private List<SkipSegment> segments = new List<SkipSegment>();
        private SkipSegment offered;       // manual segment currently offered with the skip button
        private bool marksDrawn;
        private DateTime noSkipUntil;      // let a just-issued seek settle before checking segments
        private long resumeMs;             // position to start at
        private int maxHeight;
        private List<VideoQuality> qualities = new List<VideoQuality>();  // what this video offers (from the core)
        private VideoQuality playing;      // the format being played
        private bool live;                 // broadcast: no timeline, no seeking
        private double speed = 1.0;
        private DateTime lastSave;

        public PlayerScreen(App app, VideoItem item, long startMs = -1, int maxHeight = 0) : base(app)
        {
            this.item = item;
            this.maxHeight = maxHeight > 0 ? maxHeight : app.Config.Player.MaxHeight;
            Root.BackgroundColor = Color.Transparent;
            if (startMs >= 0)
            {
                resumeMs = startMs;
            }
            else
            {
                long saved = LocalStore.ResumePosition(item.Id);
                resumeMs = saved > 30000 && LocalStore.Progress(item.Id) < 0.99 ? saved : 0;
            }

            // Loading backdrop: black + the video's thumbnail, dimmed. Hidden once frames are on screen
            // (the video plane is under this transparent UI).
            backdrop = new View { Size = new Size(1920, 1080), BackgroundColor = Color.Black };
            var poster = new ImageView
            {
                Size = new Size(1920, 1080),
                ResourceUrl = "https://i.ytimg.com/vi/" + item.Id + "/hq720.jpg",
                Opacity = 0.35f,
            };
            backdrop.Add(poster);
            Root.Add(backdrop);

            spinner = Widgets.Spinner(96, Theme.Text, out spin);
            spinner.Position = new Position(960, 470);
            Root.Add(spinner);
            stateLabel = Widgets.Label("Chargement…", Theme.BodySize + 2, Theme.Text, 1920, 80);
            stateLabel.HorizontalAlignment = HorizontalAlignment.Center;
            stateLabel.Position = new Position(0, 540);
            Root.Add(stateLabel);

            chrome = new View { Size = new Size(1920, 1080 + ChromeLift) };
            Root.Add(chrome);
            // Soft bottom gradient (three bands).
            int[] bandY = { 640, 760, 860 };
            float[] bandA = { 0.35f, 0.6f, 0.82f };
            for (int i = 0; i < bandY.Length; i++)
            {
                var band = Widgets.Box(new Color(0, 0, 0, bandA[i]), 1920, 1080 + ChromeLift - bandY[i]);
                band.Position = new Position(0, bandY[i]);
                chrome.Add(band);
            }

            titleLabel = Widgets.Label(item.Title, Theme.TitleSize + 4, Theme.Text, 1500);
            titleLabel.Position = new Position(ProgressX, 760);
            chrome.Add(titleLabel);
            var channel = Widgets.Label(item.Channel, Theme.BodySize, Theme.TextDim, 1200);
            channel.Position = new Position(ProgressX, 820);
            chrome.Add(channel);
            clockLabel = Widgets.Label("", Theme.TitleSize, Theme.Text, 200);
            clockLabel.Position = new Position(1640, 60);
            clockLabel.HorizontalAlignment = HorizontalAlignment.End;
            chrome.Add(clockLabel);

            track = Widgets.Box(new Color(1, 1, 1, 0.3f), ProgressWidth, 6);
            track.Position = new Position(ProgressX, 880);
            chrome.Add(track);
            progressFill = Widgets.Box(Theme.Accent, 0, 6);
            track.Add(progressFill);
            playhead = Widgets.Box(Theme.Accent, 18, 18);
            playhead.CornerRadius = 9;
            playhead.Position = new Position(ProgressX - 9, 874);
            chrome.Add(playhead);
            timeLabel = Widgets.Label("", Theme.SmallSize + 2, Theme.Text, 300);
            timeLabel.Position = new Position(ProgressX, 900);
            chrome.Add(timeLabel);
            durationLabel = Widgets.Label("", Theme.SmallSize + 2, Theme.Text, 300);
            durationLabel.Position = new Position(ProgressX + ProgressWidth - 300, 900);
            durationLabel.HorizontalAlignment = HorizontalAlignment.End;
            chrome.Add(durationLabel);

            int x = ProgressX;
            AddButton(Action.Restart, "restart", ref x);
            AddButton(Action.Rewind, "fast_rewind", ref x);
            playButton = AddButton(Action.PlayPause, "pause", ref x);
            AddButton(Action.Forward, "fast_forward", ref x);
            AddButton(Action.Next, "skip_next", ref x);
            int right = ProgressX + ProgressWidth;
            AddRightButton(Action.Stats, "stats", ref right);
            AddRightButton(Action.Quality, "quality", ref right);
            AddRightButton(Action.Speed, "speed", ref right);
            skipButton = AddSkipButton(ref right);

            panel = new View { Position = new Position(1460, 380), Size = new Size(400, 420), BackgroundColor = Theme.Surface, CornerRadius = 12 };
            panel.Hide();
            Root.Add(panel);

            statsLabel = Widgets.Label("", Theme.SmallSize, Theme.Text, 760, 260);
            statsLabel.MultiLine = true;
            statsLabel.BackgroundColor = Theme.Scrim;
            statsLabel.Position = new Position(60, 60);
            Widgets.SetVisible(statsLabel, app.Config.Player.ShowStats);
            Root.Add(statsLabel);

            toast = Widgets.Label("", Theme.TitleSize, Theme.Text, 0, 0);
            toast.BackgroundColor = Theme.Scrim;
            toast.CornerRadius = 8;
            toast.Position = new Position(80, 660);
            toast.Hide();
            Root.Add(toast);
        }

        private Button AddButton(Action action, string icon, ref int x)
        {
            var b = MakeButton(action, icon, x);
            x += 96;
            return b;
        }

        private void AddRightButton(Action action, string icon, ref int right)
        {
            right -= 72;
            MakeButton(action, icon, right);
            right -= 24;
        }

        private Button MakeButton(Action action, string icon, int x)
        {
            var view = new View { Position = new Position(x, 950), Size = new Size(72, 72), CornerRadius = 36,
                                  BackgroundColor = new Color(1, 1, 1, 0.14f), PivotPoint = Tizen.NUI.PivotPoint.Center,
                                  PositionUsesPivotPoint = false };
            var img = new ImageView { ResourceUrl = Theme.Icon(icon), Size = new Size(44, 44), Position = new Position(14, 14) };
            view.Add(img);
            chrome.Add(view);
            var b = new Button { Action = action, View = view, Icon = img };
            buttons.Add(b);
            return b;
        }

        // "Skip <segment>" pill, only present while a manual SponsorBlock segment is offered.
        private Button AddSkipButton(ref int right)
        {
            right -= 300;
            var view = new View { Position = new Position(right, 950), Size = new Size(300, 72), CornerRadius = 36,
                                  BackgroundColor = new Color(1, 1, 1, 0.14f) };
            var img = new ImageView { ResourceUrl = Theme.Icon("skip_segment"), Size = new Size(40, 40), Position = new Position(18, 16) };
            view.Add(img);
            var label = Widgets.Label("Passer", Theme.BodySize, Theme.Text, 220, 72);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Position = new Position(68, 0);
            view.Add(label);
            view.Hide();
            chrome.Add(view);
            var b = new Button { Action = Action.Skip, View = view, Icon = img, Label = label };
            buttons.Add(b);
            return b;
        }

        // ---------------------------------------------------------------- lifecycle

        public override void OnShow()
        {
            ShowChrome(Focus.Hidden);
            tick = new Timer(250);
            tick.Tick += (s, e) => { OnTick(); return true; };
            tick.Start();
            _ = Start();
            _ = LoadSuggestions();
        }

        private async Task Start()
        {
            IntPtr window = App.EcoreWindow;
            int w = (int)Window.Instance.WindowSize.Width, h = (int)Window.Instance.WindowSize.Height;
            if (App.Config.SponsorBlockEnabled && segments.Count == 0)
            {
                _ = LoadSegments();
            }
            long start = resumeMs;
            resumeMs = 0;
            if (start > 0)
            {
                Toast("Reprise à " + Format(start));
                noSkipUntil = DateTime.Now.AddSeconds(3);
            }
            int height = maxHeight;
            string codecs = App.Config.Player.Codecs;
            ShowLoading("Chargement…", opaque: true);
            await lastTeardown;
            IntPtr created = await Task.Run(() => NativeCore.PlayerCreateYouTube(window, w, h, item.Id, height, codecs, start));
            if (closing)
            {
                Release(created);
                return;
            }
            player = created;
            if (player != IntPtr.Zero)
            {
                qualities = YouTube.Qualities(player, out playing, out live);
            }
            if (player == IntPtr.Zero)
            {
                ShowLoading("Lecture impossible\n" + LastError(), opaque: true);
                spin.Stop();
                spinner.Hide();
                stateLabel.MultiLine = true;
                stateLabel.Size = new Size(1920, 300);
            }
            else if (speed != 1.0)
            {
                IntPtr p = player;
                double s = speed;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);  // the rate applies once prepared
                    NativeCore.PlayerSetRate(p, s);
                });
            }
        }

        private void ShowLoading(string text, bool opaque)
        {
            stateLabel.Text = text;
            stateLabel.Show();
            if (!spinner.Visibility)
            {
                spinner.Show();
                spin.Play();
            }
            if (opaque)
            {
                backdrop.Show();
            }
        }

        private void HideLoading()
        {
            if (!stateLabel.Visibility && !backdrop.Visibility)
            {
                return;
            }
            stateLabel.Hide();
            spinner.Hide();
            spin.Stop();
            backdrop.Hide();
        }

        private async Task LoadSuggestions()
        {
            suggestions = new FeedRow("Vidéos suggérées", c => YouTube.Related(item.Id), 1920 - 40)
            {
                Position = new Position(40, 1080 + 30),
            };
            chrome.Add(suggestions);
            await suggestions.Load();
        }

        private async Task LoadSegments()
        {
            List<SkipSegment> list = await YouTube.SponsorSegments(item.Id);
            list.RemoveAll(s => App.Config.ModeFor(s.Category) == SkipMode.Off);
            segments = list;
        }

        public override void OnHide()
        {
            Close();
        }

        public override void OnPause()
        {
            // Leaving the app (Home button...): give the decoder back, return to the previous screen.
            App.Pop();
        }

        private void Close()
        {
            if (closing)
            {
                return;
            }
            closing = true;
            tick?.Stop();
            spin.Stop();
            SavePosition();
            Release(player);
            player = IntPtr.Zero;
        }

        private void SavePosition()
        {
            if (player != IntPtr.Zero && stats.DurationMs > 0)
            {
                LocalStore.SavePosition(item.Id, stats.Eos != 0 ? stats.DurationMs : stats.PositionMs, stats.DurationMs);
            }
        }

        private static void Release(IntPtr p)
        {
            if (p != IntPtr.Zero)
            {
                Task previous = lastTeardown;
                lastTeardown = Task.Run(async () =>
                {
                    await previous;
                    NativeCore.PlayerDestroy(p);
                });
            }
        }

        // Quality change: same video, new player at the current position.
        private void Restart(int height)
        {
            SavePosition();
            long pos = stats.PositionMs;
            Release(player);
            player = IntPtr.Zero;
            maxHeight = height;
            resumeMs = pos;
            marksDrawn = false;
            _ = Start();
        }

        // ---------------------------------------------------------------- per tick

        private void OnTick()
        {
            DateTime now = DateTime.Now;
            if (player != IntPtr.Zero)
            {
                NativeCore.PlayerGetStats(player, out stats);
                if (stats.Reconnecting != 0)
                {
                    ShowLoading("Reconnexion…", opaque: false);   // over the last frame
                }
                else if (stats.State == 3 || stats.State == 4)
                {
                    HideLoading();
                }
                if (stats.State == 3 && (now - lastSave).TotalSeconds > 10)
                {
                    lastSave = now;
                    LocalStore.SavePosition(item.Id, stats.PositionMs, stats.DurationMs);
                }
            }
            if (pendingSeekMs >= 0 && (now - pendingSeekAt).TotalMilliseconds > 600)
            {
                CommitSeek();
            }
            CheckSegments(now);
            DrawMarks();
            // Controls hide on their own while playing (not while paused, nor in a panel / the suggestions).
            if (focus != Focus.Hidden && focus != Focus.Panel && focus != Focus.Suggestions && stats.State == 3 &&
                now > chromeUntil && pendingSeekMs < 0)
            {
                HideChrome();
            }
            UpdateChrome(now);
            if (statsLabel.Visibility && player != IntPtr.Zero)
            {
                statsLabel.Text =
                    $"buffer  vidéo {stats.VideoQueuedMs / 1000.0:F1}s  audio {stats.AudioQueuedMs / 1000.0:F1}s\n" +
                    $"débit  vidéo {stats.VideoKbps:F0} kb/s  audio {stats.AudioKbps:F0} kb/s\n" +
                    $"saccades {stats.Stalls} (max {stats.LongestStallMs} ms)  underruns v{stats.VideoUnderruns}/a{stats.AudioUnderruns}\n" +
                    $"erreurs {stats.Errors}   vitesse {speed:0.##}x   max {maxHeight}p" +
                    (playing != null ? "   lecture " + QualityLabel(playing) : "") + "\n" + NativeCore.RecentLog(3);
            }
            if (toast.Visibility && now > toastUntil)
            {
                toast.Hide();
            }
            if (stats.Eos != 0 && !closing)
            {
                App.Pop();
            }
        }

        private void UpdateChrome(DateTime now)
        {
            if (!chrome.Visibility)
            {
                return;
            }
            Widgets.SetVisible(clockLabel, App.Config.Player.ShowClock);
            clockLabel.Text = now.ToString("HH:mm");
            playButton.Icon.ResourceUrl = Theme.Icon(stats.State == 4 ? "play" : "pause");
            if (live)
            {
                progressFill.SizeWidth = ProgressWidth;
                playhead.PositionX = ProgressX + ProgressWidth - 9;
                timeLabel.Text = "EN DIRECT";
                durationLabel.Text = "";
                return;
            }
            long pos = pendingSeekMs >= 0 ? pendingSeekMs : stats.PositionMs;
            long dur = Math.Max(stats.DurationMs, 1);
            float fill = (float)(ProgressWidth * Math.Min(1.0, pos / (double)dur));
            progressFill.SizeWidth = fill;
            playhead.PositionX = ProgressX + fill - 9;
            timeLabel.Text = Format(pos);
            durationLabel.Text = Format(stats.DurationMs);
        }

        // ---------------------------------------------------------------- SponsorBlock

        // Auto-skips, or offers (skip button) the segment under the playback position.
        private void CheckSegments(DateTime now)
        {
            if (segments.Count == 0 || stats.State != 3 || pendingSeekMs >= 0 || now < noSkipUntil)
            {
                return;
            }
            long pos = stats.PositionMs;
            SkipSegment current = null;
            foreach (SkipSegment s in segments)
            {
                if (pos >= s.EndMs - 300)
                {
                    s.Done |= pos >= s.EndMs;
                }
                else if (!s.Done && pos >= s.StartMs)
                {
                    current = s;
                    break;
                }
            }
            if (current == null)
            {
                if (offered != null)
                {
                    offered = null;
                    skipButton.View.Hide();
                    if (focus == Focus.Buttons && buttons[buttonIndex] == skipButton)
                    {
                        buttonIndex = buttons.IndexOf(playButton);
                        RefreshButtons();
                    }
                }
                return;
            }
            SponsorCategory cat = SponsorCategory.Find(current.Category);
            string label = cat != null ? cat.Label : current.Category;
            if (App.Config.ModeFor(current.Category) == SkipMode.Auto)
            {
                SkipTo(current);
                Toast(label + " passé");
            }
            else if (offered != current)
            {
                // SmartTube shows the controls with the focus on "Skip".
                offered = current;
                skipButton.Label.Text = "Passer : " + label;
                skipButton.View.Show();
                ShowChrome(Focus.Buttons);
                buttonIndex = buttons.IndexOf(skipButton);
                RefreshButtons();
            }
        }

        private void SkipTo(SkipSegment s)
        {
            s.Done = true;
            offered = null;
            skipButton.View.Hide();
            noSkipUntil = DateTime.Now.AddSeconds(1.5);
            long target = Math.Min(s.EndMs, Math.Max(0, stats.DurationMs - 1000));
            Seek(target);
        }

        // Coloured marks for the SponsorBlock segments on the seek bar.
        private void DrawMarks()
        {
            if (marksDrawn || stats.DurationMs <= 0 || segments.Count == 0)
            {
                return;
            }
            marksDrawn = true;
            foreach (SkipSegment s in segments)
            {
                SponsorCategory cat = SponsorCategory.Find(s.Category);
                float x = (float)(ProgressWidth * s.StartMs / (double)stats.DurationMs);
                float w = Math.Max(4f, (float)(ProgressWidth * (s.EndMs - s.StartMs) / (double)stats.DurationMs));
                track.Add(new View { BackgroundColor = cat?.Color ?? Theme.Text, Position = new Position(x, 0), Size = new Size(w, 6) });
            }
            progressFill.RaiseToTop();
        }

        // ---------------------------------------------------------------- chrome & focus

        private void ShowChrome(Focus target)
        {
            chrome.Show();
            chromeUntil = DateTime.Now.AddSeconds(App.Config.Player.OsdTimeoutSeconds);
            if (target != Focus.Hidden)
            {
                focus = target;
            }
            slide = Widgets.Slide(chrome, 0, focus == Focus.Suggestions ? -ChromeLift : 0, slide, 220);
            RefreshButtons();
            suggestions?.SetFocused(focus == Focus.Suggestions);
            UpdateChrome(DateTime.Now);
        }

        private void HideChrome()
        {
            focus = Focus.Hidden;
            chrome.Hide();
            chrome.PositionY = 0;
            suggestions?.SetFocused(false);
        }

        private void RefreshButtons()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                bool f = focus == Focus.Buttons && i == buttonIndex;
                buttons[i].View.BackgroundColor = f ? Theme.Accent : new Color(1, 1, 1, 0.14f);
                buttons[i].View.Scale = f ? new Vector3(1.12f, 1.12f, 1f) : Vector3.One;
            }
        }

        // Next / previous visible button in screen order (left group, then right group right-to-left reversed).
        private void MoveButton(int delta)
        {
            List<int> order = VisibleOrder();
            int at = order.IndexOf(buttonIndex);
            at = Math.Max(0, Math.Min(order.Count - 1, (at < 0 ? 0 : at) + delta));
            buttonIndex = order[at];
            RefreshButtons();
        }

        private List<int> VisibleOrder()
        {
            var order = new List<int>();
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].View.Visibility)
                {
                    order.Add(i);
                }
            }
            order.Sort((a, b) => buttons[a].View.PositionX.CompareTo(buttons[b].View.PositionX));
            return order;
        }

        public override bool OnKey(string key)
        {
            bool back = key == "XF86Back" || key == "Escape";
            switch (focus)
            {
                case Focus.Hidden:
                    if (back)
                    {
                        return false;
                    }
                    if (key == "Return" || key == "XF86AudioPlay" || key == "XF86AudioPause")
                    {
                        TogglePause();
                        buttonIndex = buttons.IndexOf(playButton);
                        ShowChrome(Focus.Buttons);
                    }
                    else if (key == "Left" || key == "Right")
                    {
                        SeekBy(key == "Left" ? -1 : 1);
                        buttonIndex = buttons.IndexOf(playButton);
                        ShowChrome(Focus.Buttons);
                    }
                    else if (key == "Down")
                    {
                        ShowChrome(Focus.Suggestions);
                    }
                    else
                    {
                        buttonIndex = buttons.IndexOf(playButton);
                        ShowChrome(Focus.Buttons);
                    }
                    return true;

                case Focus.Buttons:
                    switch (key)
                    {
                        case "Left":
                            MoveButton(-1);
                            break;
                        case "Right":
                            MoveButton(1);
                            break;
                        case "Return":
                            Activate(buttons[buttonIndex].Action);
                            break;
                        case "Down":
                            if (suggestions != null && suggestions.Count > 0)
                            {
                                focus = Focus.Suggestions;
                            }
                            break;
                        case "Up":
                        case "XF86Back":
                        case "Escape":
                            HideChrome();
                            return true;
                    }
                    ShowChrome(Focus.Hidden);
                    return true;

                case Focus.Suggestions:
                    switch (key)
                    {
                        case "Left":
                            suggestions.Move(-1);
                            break;
                        case "Right":
                            suggestions.Move(1);
                            break;
                        case "Return":
                            if (suggestions.FocusedItem != null)
                            {
                                App.Replace(new PlayerScreen(App, suggestions.FocusedItem));
                            }
                            return true;
                        case "Up":
                        case "XF86Back":
                        case "Escape":
                            focus = Focus.Buttons;
                            break;
                    }
                    ShowChrome(Focus.Hidden);
                    return true;

                case Focus.Panel:
                    PanelKey(key);
                    return true;
            }
            return true;
        }

        private void Activate(Action action)
        {
            switch (action)
            {
                case Action.Restart:
                    Seek(0);
                    break;
                case Action.Rewind:
                    SeekBy(-1);
                    break;
                case Action.Forward:
                    SeekBy(1);
                    break;
                case Action.PlayPause:
                    TogglePause();
                    break;
                case Action.Next:
                    if (suggestions != null && suggestions.Count > 0)
                    {
                        App.Replace(new PlayerScreen(App, suggestions.FocusedItem));
                    }
                    break;
                case Action.Skip:
                    if (offered != null)
                    {
                        SkipTo(offered);
                        buttonIndex = buttons.IndexOf(playButton);
                    }
                    break;
                case Action.Speed:
                case Action.Quality:
                    OpenPanel(action);
                    break;
                case Action.Stats:
                    Widgets.SetVisible(statsLabel, !statsLabel.Visibility);
                    break;
            }
        }

        // ---------------------------------------------------------------- speed / quality panel

        private void OpenPanel(Action kind)
        {
            panelKind = kind;
            foreach (TextLabel l in panelLabels)
            {
                panel.Remove(l);
                l.Dispose();
            }
            panelLabels.Clear();
            var title = Widgets.Label(kind == Action.Speed ? "Vitesse" : "Qualité", Theme.BodySize, Theme.TextDim, 360, 60);
            title.Position = new Position(24, 10);
            title.VerticalAlignment = VerticalAlignment.Center;
            panel.Add(title);
            panelLabels.Add(title);
            List<VideoQuality> options = PanelQualities();
            int count = kind == Action.Speed ? Speeds.Length : options.Count;
            int width = kind == Action.Speed ? 400 : 640;
            panel.SizeWidth = width;
            panel.PositionX = 1860 - width;
            panelIndex = 0;
            for (int i = 0; i < count; i++)
            {
                bool current = kind == Action.Speed ? Speeds[i] == speed
                                                    : options[i].Height == (playing != null ? playing.Height : maxHeight);
                if (current)
                {
                    panelIndex = i;
                }
                string text = kind == Action.Speed ? (Speeds[i] == 1.0 ? "Normale" : $"{Speeds[i]:0.##}x") : QualityLabel(options[i]);
                var l = Widgets.Label("  " + text + (current ? "  (actuelle)" : ""), Theme.BodySize + 2, Theme.Text, width - 48, 52);
                l.VerticalAlignment = VerticalAlignment.Center;
                l.CornerRadius = 8;
                l.Position = new Position(24, 76 + i * 54);
                panel.Add(l);
                panelLabels.Add(l);
            }
            panel.SizeHeight = 96 + count * 54;
            panel.Show();
            focus = Focus.Panel;
            RefreshPanel();
        }

        private void RefreshPanel()
        {
            for (int i = 1; i < panelLabels.Count; i++)
            {
                panelLabels[i].BackgroundColor = i - 1 == panelIndex ? Theme.Accent : Color.Transparent;
            }
        }

        private void PanelKey(string key)
        {
            int count = panelLabels.Count - 1;
            switch (key)
            {
                case "Up":
                    panelIndex = Math.Max(0, panelIndex - 1);
                    break;
                case "Down":
                    panelIndex = Math.Min(count - 1, panelIndex + 1);
                    break;
                case "Return":
                    if (panelKind == Action.Speed)
                    {
                        speed = Speeds[panelIndex];
                        if (player != IntPtr.Zero)
                        {
                            NativeCore.PlayerSetRate(player, speed);
                        }
                        Toast(speed == 1.0 ? "Vitesse normale" : $"Vitesse {speed:0.##}x");
                    }
                    else
                    {
                        VideoQuality q = PanelQualities()[panelIndex];
                        if (playing == null || q.Height != playing.Height)
                        {
                            Toast("Qualité " + QualityLabel(q));
                            Restart(q.Height);
                        }
                    }
                    ClosePanel();
                    return;
                case "XF86Back":
                case "Escape":
                    ClosePanel();
                    return;
            }
            RefreshPanel();
        }

        private void ClosePanel()
        {
            panel.Hide();
            focus = Focus.Buttons;
            ShowChrome(Focus.Hidden);
        }

        // ---------------------------------------------------------------- playback helpers

        private void TogglePause()
        {
            if (player != IntPtr.Zero)
            {
                NativeCore.PlayerTogglePause(player);
            }
        }

        // Repeated presses accumulate; the seek is issued once the user stops.
        private void SeekBy(int direction)
        {
            if (live)
            {
                Toast("Direct : pas d'avance ni de retour");
                return;
            }
            int step = App.Config.Player.SeekStepSeconds * 1000 * direction;
            long basePos = pendingSeekMs >= 0 ? pendingSeekMs : stats.PositionMs;
            pendingSeekMs = Math.Max(0, Math.Min(basePos + step, Math.Max(0, stats.DurationMs - 1000)));
            pendingSeekAt = DateTime.Now;
            UpdateChrome(DateTime.Now);
        }

        private void CommitSeek()
        {
            long target = pendingSeekMs;
            pendingSeekMs = -1;
            Seek(target);
        }

        private void Seek(long target)
        {
            if (live)
            {
                return;
            }
            noSkipUntil = DateTime.Now.AddSeconds(1.5);
            IntPtr p = player;
            if (p != IntPtr.Zero)
            {
                Task.Run(() => NativeCore.PlayerSeek(p, target));
            }
        }

        private void Toast(string text)
        {
            toast.Text = "  " + text + "  ";
            toast.Show();
            toastUntil = DateTime.Now.AddSeconds(1.5);
        }

        private List<VideoQuality> PanelQualities()
        {
            if (qualities.Count > 0)
            {
                return qualities;
            }
            var list = new List<VideoQuality>();
            foreach (int h in DefaultHeights)
            {
                list.Add(new VideoQuality { Height = h });
            }
            return list;
        }

        // "2160p (4K) · 60 fps · VP9"; fps and codec only once the core has listed the formats.
        private static string QualityLabel(VideoQuality q)
        {
            string text = q.Height == 2160 ? "2160p (4K)" : q.Height == 1440 ? "1440p (2K)" : $"{q.Height}p";
            if (q.Fps > 0)
            {
                text += $" · {q.Fps} fps";
            }
            string codec = q.Codec == "avc1" ? "H.264" : q.Codec == "vp9" ? "VP9" : q.Codec == "av01" ? "AV1" : "";
            return codec.Length > 0 ? text + " · " + codec : text;
        }

        private static string Format(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private static string LastError()
        {
            string[] lines = NativeCore.RecentLog(3).Trim().Split('\n');
            return lines.Length > 0 ? lines[lines.Length - 1] : "";
        }
    }
}
