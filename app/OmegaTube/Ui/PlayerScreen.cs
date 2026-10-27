using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Config;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Full-screen video (ESPlayer plane under a transparent UI) + auto-hiding control bar.
    public class PlayerScreen : Screen
    {
        // Player teardown joins native threads: serialise create/destroy so two players never coexist
        // (the TV has one hardware decoder; a second player would get "resource conflicted").
        private static Task lastTeardown = Task.CompletedTask;

        private readonly VideoItem item;
        private readonly View osd;
        private readonly TextLabel titleLabel, timeLabel, clockLabel, stateLabel, statsLabel, toast;
        private readonly View progressFill;
        private const int ProgressWidth = 1760;
        private IntPtr player = IntPtr.Zero;
        private bool closing;
        private Timer tick;
        private DateTime osdUntil;
        private DateTime toastUntil;
        private long pendingSeekMs = -1;   // accumulated target while the user holds Left/Right
        private DateTime pendingSeekAt;
        private NativeCore.PlayerStats stats;
        private List<SkipSegment> segments = new List<SkipSegment>();
        private SkipSegment offered;            // manual segment currently offered with "▼ Passer"
        private readonly TextLabel skipPrompt;
        private View track;
        private bool marksDrawn;
        private DateTime noSkipUntil;           // let a just-issued seek settle before checking segments

        public PlayerScreen(App app, VideoItem item) : base(app)
        {
            this.item = item;
            Root.BackgroundColor = Color.Transparent;

            stateLabel = Widgets.Label("Chargement…", Theme.TitleSize, Theme.Text, 1920, 80);
            stateLabel.HorizontalAlignment = HorizontalAlignment.Center;
            stateLabel.Position = new Position(0, 500);
            Root.Add(stateLabel);

            osd = new View { Position = new Position(0, 780), Size = new Size(1920, 300), BackgroundColor = Theme.Scrim };
            Root.Add(osd);
            titleLabel = Widgets.Label(item.Title, Theme.TitleSize, Theme.Text, 1500);
            titleLabel.Position = new Position(80, 40);
            osd.Add(titleLabel);
            var channel = Widgets.Label(item.Channel, Theme.BodySize, Theme.TextDim, 1200);
            channel.Position = new Position(80, 100);
            osd.Add(channel);
            clockLabel = Widgets.Label("", Theme.TitleSize, Theme.Text, 200);
            clockLabel.Position = new Position(1640, 40);
            clockLabel.HorizontalAlignment = HorizontalAlignment.End;
            osd.Add(clockLabel);

            track = Widgets.Box(new Color(1, 1, 1, 0.25f), ProgressWidth, 8);
            track.Position = new Position(80, 180);
            osd.Add(track);
            progressFill = Widgets.Box(Theme.Accent, 0, 8);
            track.Add(progressFill);
            timeLabel = Widgets.Label("", Theme.BodySize, Theme.Text, 800);
            timeLabel.Position = new Position(80, 205);
            osd.Add(timeLabel);
            var hint = Widgets.Label($"OK pause   ◀ ▶ ±{app.Config.Player.SeekStepSeconds} s   ▲ stats   Retour quitter",
                                     Theme.SmallSize, Theme.TextDim, 1000);
            hint.Position = new Position(840, 210);
            hint.HorizontalAlignment = HorizontalAlignment.End;
            osd.Add(hint);

            statsLabel = Widgets.Label("", Theme.SmallSize, Theme.Text, 760, 260);
            statsLabel.MultiLine = true;
            statsLabel.BackgroundColor = Theme.Scrim;
            statsLabel.Position = new Position(60, 60);
            Widgets.SetVisible(statsLabel, app.Config.Player.ShowStats);
            Root.Add(statsLabel);

            skipPrompt = Widgets.Label("", Theme.TitleSize, Theme.Text, 0, 0);
            skipPrompt.BackgroundColor = Theme.Accent;
            skipPrompt.Position = new Position(1300, 680);
            skipPrompt.Hide();
            Root.Add(skipPrompt);

            toast = Widgets.Label("", Theme.TitleSize, Theme.Text, 0, 0);
            toast.BackgroundColor = Theme.Scrim;
            toast.Position = new Position(80, 680);
            toast.Hide();
            Root.Add(toast);
        }

        public override void OnShow()
        {
            ShowOsd();
            tick = new Timer(250);
            tick.Tick += (s, e) => { OnTick(); return true; };
            tick.Start();
            _ = Start();
        }

        private async Task Start()
        {
            IntPtr window = App.EcoreWindow;
            int maxHeight = App.Config.Player.MaxHeight;
            int w = (int)Window.Instance.WindowSize.Width, h = (int)Window.Instance.WindowSize.Height;
            if (App.Config.SponsorBlockEnabled)
            {
                _ = LoadSegments();
            }
            await lastTeardown;
            IntPtr created = await Task.Run(() => NativeCore.PlayerCreateYouTube(window, w, h, item.Id, maxHeight));
            if (closing)
            {
                Release(created);
                return;
            }
            player = created;
            if (player == IntPtr.Zero)
            {
                stateLabel.Text = "Lecture impossible\n" + LastError();
                stateLabel.MultiLine = true;
                stateLabel.Size = new Size(1920, 300);
            }
        }

        private async Task LoadSegments()
        {
            List<SkipSegment> list = await YouTube.SponsorSegments(item.Id);
            list.RemoveAll(s => App.Config.ModeFor(s.Category) == SkipMode.Off);
            segments = list;
        }

        // SponsorBlock: auto-skip or offer the segment under the playback position.
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
                offered = null;
                skipPrompt.Hide();
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
                offered = current;
                skipPrompt.Text = "  ▼ Passer : " + label + "  ";
                skipPrompt.Show();
            }
        }

        private void SkipTo(SkipSegment s)
        {
            s.Done = true;
            offered = null;
            skipPrompt.Hide();
            noSkipUntil = DateTime.Now.AddSeconds(1.5);
            IntPtr p = player;
            long target = Math.Min(s.EndMs, Math.Max(0, stats.DurationMs - 1000));
            if (p != IntPtr.Zero)
            {
                Task.Run(() => NativeCore.PlayerSeek(p, target));
            }
        }

        // Coloured marks for the SponsorBlock segments on the progress track.
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
                var mark = new View { BackgroundColor = cat?.Color ?? Theme.Text, Position = new Position(x, 0), Size = new Size(w, 8) };
                track.Add(mark);
            }
            progressFill.RaiseToTop();
        }

        private static string LastError()
        {
            string[] lines = NativeCore.RecentLog(3).Trim().Split('\n');
            return lines.Length > 0 ? lines[lines.Length - 1] : "";
        }

        private void OnTick()
        {
            DateTime now = DateTime.Now;
            if (player != IntPtr.Zero)
            {
                NativeCore.PlayerGetStats(player, out stats);
                if (stats.Reconnecting != 0)
                {
                    stateLabel.Text = "Reconnexion…";
                    stateLabel.Show();
                }
                else if (stats.State == 3 || stats.State == 4)
                {
                    stateLabel.Hide();
                }
                if (stats.State == 4)
                {
                    osdUntil = now.AddSeconds(1);  // keep the bar visible while paused
                }
            }
            if (pendingSeekMs >= 0 && (now - pendingSeekAt).TotalMilliseconds > 600)
            {
                CommitSeek();
            }
            CheckSegments(now);
            DrawMarks();
            UpdateOsd(now);
            if (statsLabel.Visibility && player != IntPtr.Zero)
            {
                statsLabel.Text =
                    $"buffer  vidéo {stats.VideoQueuedMs / 1000.0:F1}s  audio {stats.AudioQueuedMs / 1000.0:F1}s\n" +
                    $"débit  vidéo {stats.VideoKbps:F0} kb/s  audio {stats.AudioKbps:F0} kb/s\n" +
                    $"saccades {stats.Stalls} (max {stats.LongestStallMs} ms)  underruns v{stats.VideoUnderruns}/a{stats.AudioUnderruns}\n" +
                    $"erreurs {stats.Errors}\n" + NativeCore.RecentLog(3);
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

        private void UpdateOsd(DateTime now)
        {
            if (now > osdUntil)
            {
                osd.Hide();
                return;
            }
            osd.Show();
            Widgets.SetVisible(clockLabel, App.Config.Player.ShowClock);
            clockLabel.Text = now.ToString("HH:mm");
            long pos = pendingSeekMs >= 0 ? pendingSeekMs : stats.PositionMs;
            long dur = Math.Max(stats.DurationMs, 1);
            progressFill.SizeWidth = (float)(ProgressWidth * Math.Min(1.0, pos / (double)dur));
            timeLabel.Text = Format(pos) + " / " + Format(stats.DurationMs);
        }

        private static string Format(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private void ShowOsd()
        {
            osdUntil = DateTime.Now.AddSeconds(App.Config.Player.OsdTimeoutSeconds);
            UpdateOsd(DateTime.Now);
        }

        private void Toast(string text)
        {
            toast.Text = "  " + text + "  ";
            toast.Show();
            toastUntil = DateTime.Now.AddSeconds(1.5);
        }

        public override bool OnKey(string key)
        {
            switch (key)
            {
                case "XF86Back":
                case "Escape":
                    return false;
                case "Return":
                case "XF86AudioPlay":
                case "XF86AudioPause":
                    if (player != IntPtr.Zero)
                    {
                        NativeCore.PlayerTogglePause(player);
                        Toast(stats.State == 4 ? "▶" : "❚❚");
                    }
                    break;
                case "Left":
                case "Right":
                case "XF86AudioRewind":
                case "XF86AudioNext":
                    // Repeated presses accumulate; the seek is issued once the user stops.
                    int step = App.Config.Player.SeekStepSeconds * 1000 * (key == "Left" || key == "XF86AudioRewind" ? -1 : 1);
                    long basePos = pendingSeekMs >= 0 ? pendingSeekMs : stats.PositionMs;
                    pendingSeekMs = Math.Max(0, Math.Min(basePos + step, Math.Max(0, stats.DurationMs - 1000)));
                    pendingSeekAt = DateTime.Now;
                    Toast((step > 0 ? "» " : "« ") + Format(pendingSeekMs));
                    break;
                case "Up":
                    Widgets.SetVisible(statsLabel, !statsLabel.Visibility);
                    break;
                case "Down":
                    if (offered != null)
                    {
                        SkipTo(offered);
                        return true;
                    }
                    break;
            }
            ShowOsd();
            return true;
        }

        private void CommitSeek()
        {
            long target = pendingSeekMs;
            pendingSeekMs = -1;
            noSkipUntil = DateTime.Now.AddSeconds(1.5);
            IntPtr p = player;
            if (p != IntPtr.Zero)
            {
                Task.Run(() => NativeCore.PlayerSeek(p, target));
            }
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
            Release(player);
            player = IntPtr.Zero;
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
    }
}
