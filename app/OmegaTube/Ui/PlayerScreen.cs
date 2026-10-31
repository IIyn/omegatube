using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using OmegaTube.Config;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Full-screen video (ESPlayer plane under a transparent UI) with SmartTube-style controls
    // (layout as in SmartTube / ytps5's player chrome):
    //  - hidden while playing; OK pauses and shows them, ◀ ▶ seek (focus on the seek bar), any other key shows them;
    //  - title, views + date, a row of actions (channel, subscribe, like / dislike with Return YouTube Dislike
    //    counts), a full-width seek bar with the SponsorBlock segments, and a row of round buttons:
    //    restart, -N s, play/pause, +N s, next | skip segment, subtitles, audio track, speed, quality, loop,
    //    description, stats (the focused one is named in a tooltip above it);
    //  - focus rows, top to bottom: actions / seek bar (◀ ▶ seek) / buttons / suggestions (Up and Down move between them);
    //    the suggestions row slides the whole block up;
    //  - subtitles are drawn by the app (YouTube json3 tracks), above the controls while they are shown;
    //  - at the end: next in the playlist, (autoplay) the first suggestion after a short countdown, or the
    //    controls stay up on the finished video.
    public class PlayerScreen : Screen
    {
        // Player teardown joins native threads: serialise create/destroy so two players never coexist
        // (the TV has one hardware decoder; a second player would get "resource conflicted").
        private static Task lastTeardown = Task.CompletedTask;
        // Kept across videos for the session, like YouTube.
        private static double sessionSpeed = 1.0;
        private static string loopVideoId;   // loop survives the player restart that replays the video

        private enum Focus { Hidden, Actions, Timeline, Buttons, Suggestions, Panel, Info, UpNext }
        private enum Social { Channel, Subscribe, Like, Dislike }

        // A pill of the actions row: icon or avatar, then text; sized from its text.
        private class Chip
        {
            public Social Kind;
            public View View;
            public ImageView Image;
            public TextLabel Label;
            public bool Active;      // subscribed / liked / disliked
        }
        private enum Action { Restart, Rewind, PlayPause, Forward, Next, Skip, Captions, Audio, Speed, Quality, Loop, Info, Stats }

        private class Button
        {
            public Action Action;
            public View View;
            public ImageView Icon;
            public TextLabel Label;   // the skip button is a pill with text
            public View Indicator;    // lit under toggles that are on (subtitles, loop, stats); none on the skip pill
            public bool Available = true;
        }

        private class CaptionChoice
        {
            public CaptionTrack Track;
            public string TranslateTo = "";
        }

        private static readonly double[] Speeds = { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
        private static readonly int[] DefaultHeights = { 2160, 1440, 1080, 720, 480, 360 };  // until the core answers
        private const int ProgressWidth = 1760, ProgressX = 80;
        private const int TitleY = 590, MetaY = 654, ActionsY = 706, ChipH = 64;
        private const int TrackY = 812, ButtonsY = 890, ButtonSize = 72;
        private const int SuggestionsY = 1080 + 30;   // the suggestions row, below the screen until revealed
        private const int ChromeLift = 30 + Theme.RowHeight + 40;   // how far the block slides up to show it whole
        private const int CaptionBottomFree = 1010, CaptionBottomChrome = 570;
        private const int UpNextSeconds = 10;

        private readonly VideoItem item;
        private readonly View chrome;          // everything at the bottom; slides up for suggestions
        private readonly TextLabel titleLabel, metaLabel, timeLabel, durationLabel, clockLabel, stateLabel, statsLabel, toast;
        private readonly TextLabel tooltip;
        private readonly View backdrop;        // opaque while loading: the TV picture must not show through
        private readonly View barA, barB;      // black bars where the video does not cover the screen (4:3, 21:9)
        private readonly View spinner;
        private readonly Animation spin;
        private readonly View track, progressFill, playhead;
        private readonly List<Button> buttons = new List<Button>();
        private readonly List<Button> rightButtons = new List<Button>();  // laid out from the right edge
        private readonly Button skipButton, playButton;
        private readonly ListPanel panel;
        private readonly TextLabel captionLabel;
        private readonly List<Chip> chips = new List<Chip>();
        private int chipIndex;
        private VideoDetails details;
        private bool signedIn;
        private bool socialBusy;
        private FeedRow suggestions;
        private Focus focus = Focus.Hidden;
        private int buttonIndex;
        private Action panelKind;
        private Animation slide;

        // Description panel
        private View infoPanel;
        private TextLabel infoText;
        private float infoScroll;

        // End of video: "up next" card
        private View upNext;
        private TextLabel upNextCountdown;
        private Widgets.PillButton upNextPlay, upNextCancel;
        private int upNextFocus;
        private DateTime upNextAt;
        private VideoItem upNextItem;

        private IntPtr player = IntPtr.Zero;
        private bool closing;
        private bool ended;
        private Timer tick, captionTick;
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
        private string audioTrack;         // requested audio track id / language ("" = original)
        private PlaybackInfo info = new PlaybackInfo();
        private bool live;                 // broadcast: no timeline, no seeking
        private bool InList => !string.IsNullOrEmpty(item.PlaylistId);
        private bool Loop => loopVideoId == item.Id;
        private DateTime lastSave;
        private string lastPlayIcon;

        private CaptionChoice captions;    // null: off
        private List<CaptionCue> cues;
        private int captionGeneration;
        private string captionShown = "";
        private bool captionsAutoPicked;

        public PlayerScreen(App app, VideoItem item, long startMs = -1, int maxHeight = 0) : base(app)
        {
            this.item = item;
            this.maxHeight = maxHeight > 0 ? maxHeight : app.Config.Player.MaxHeight;
            audioTrack = app.Config.Player.AudioLang;
            if (loopVideoId != item.Id)
            {
                loopVideoId = null;
            }
            Root.BackgroundColor = Color.Transparent;
            if (startMs >= 0)
            {
                resumeMs = startMs;
            }
            else
            {
                resumeMs = ResumePoint(item);
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
            barA = new View { BackgroundColor = Color.Black };
            barB = new View { BackgroundColor = Color.Black };
            barA.Hide();
            barB.Hide();
            Root.Add(barA);
            Root.Add(barB);

            spinner = Widgets.Spinner(96, Theme.Text, out spin);
            spinner.Position = new Position(960, 470);
            Root.Add(spinner);
            stateLabel = Widgets.Label(T("Loading…"), Theme.BodySize + 2, Theme.Text, 1920, 80);
            stateLabel.HorizontalAlignment = HorizontalAlignment.Center;
            stateLabel.Position = new Position(0, 540);
            Root.Add(stateLabel);

            // Subtitles: under the controls in z-order, sized to the text (wrapped by hand) and bottom-centred.
            captionLabel = Widgets.Label("", 40, Color.White);
            captionLabel.MultiLine = true;
            captionLabel.HorizontalAlignment = HorizontalAlignment.Center;
            captionLabel.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            captionLabel.PivotPoint = Tizen.NUI.PivotPoint.BottomCenter;
            captionLabel.PositionUsesPivotPoint = true;
            captionLabel.Position = new Position(960, CaptionBottomFree);
            captionLabel.Hide();
            Root.Add(captionLabel);

            chrome = new View { Size = new Size(1920, 1080 + ChromeLift) };
            Root.Add(chrome);
            // Light gradients (top for the clock, bottom for the controls) instead of opaque bands; below the
            // screen, under the suggestions row, the bottom one's darkest shade continues.
            chrome.Add(new ImageView { ResourceUrl = Theme.Image("scrim_top"), Size = new Size(1920, 200) });
            chrome.Add(new ImageView { ResourceUrl = Theme.Image("scrim_bottom"), Size = new Size(1920, 620), Position = new Position(0, 460) });
            var under = Widgets.Box(new Color(0, 0, 0, 0.8f), 1920, ChromeLift);
            under.PositionY = 1080;
            chrome.Add(under);

            titleLabel = Widgets.Label(item.Title, Theme.TitleSize + 8, Theme.Text, 1600, 62);
            titleLabel.Position = new Position(ProgressX, TitleY);
            chrome.Add(titleLabel);
            metaLabel = Widgets.Label(MetaLine(), Theme.BodySize + 1, new Color(0.85f, 0.85f, 0.85f, 1f), 1600, 40);
            metaLabel.Position = new Position(ProgressX, MetaY);
            chrome.Add(metaLabel);
            signedIn = YouTube.SignedIn();
            AddChip(Social.Channel, null, string.IsNullOrEmpty(item.Channel) ? T("Channel") : item.Channel);
            AddChip(Social.Subscribe, null, T("Subscribe")).View.Hide();
            AddChip(Social.Like, "thumb_up", T("Like")).View.Hide();
            AddChip(Social.Dislike, "thumb_down", T("Dislike")).View.Hide();
            LayoutChips();
            clockLabel = Widgets.Label("", Theme.TitleSize, Theme.Text, 200);
            clockLabel.Position = new Position(1640, 44);
            clockLabel.HorizontalAlignment = HorizontalAlignment.End;
            chrome.Add(clockLabel);

            track = Widgets.Rounded(new Color(1, 1, 1, 0.3f), ProgressWidth, 6, 3);
            track.Position = new Position(ProgressX, TrackY);
            chrome.Add(track);
            progressFill = Widgets.Rounded(Theme.Accent, 0, 6, 3);
            track.Add(progressFill);
            playhead = Widgets.Rounded(Theme.Accent, 18, 18, 9);
            playhead.Position = new Position(ProgressX - 9, TrackY - 6);
            chrome.Add(playhead);
            timeLabel = Widgets.Label("", Theme.SmallSize + 2, Theme.Text, 400);
            timeLabel.Position = new Position(ProgressX, TrackY + 22);
            chrome.Add(timeLabel);
            durationLabel = Widgets.Label("", Theme.SmallSize + 2, Theme.Text, 300);
            durationLabel.Position = new Position(ProgressX + ProgressWidth - 300, TrackY + 22);
            durationLabel.HorizontalAlignment = HorizontalAlignment.End;
            chrome.Add(durationLabel);

            int x = ProgressX;
            AddButton(Action.Restart, "restart", ref x);
            AddButton(Action.Rewind, "fast_rewind", ref x);
            playButton = AddButton(Action.PlayPause, "pause", ref x);
            AddButton(Action.Forward, "fast_forward", ref x);
            AddButton(Action.Next, "skip_next", ref x);
            // Right group, right to left.
            AddRightButton(Action.Stats, "stats");
            AddRightButton(Action.Info, "info");
            AddRightButton(Action.Loop, "repeat");
            AddRightButton(Action.Quality, "quality");
            AddRightButton(Action.Speed, "speed");
            AddRightButton(Action.Audio, "audio_track").Available = false;      // until the video has several
            AddRightButton(Action.Captions, "subtitles").Available = false;     // until the video has some
            skipButton = AddSkipButton();
            LayoutRightButtons();

            tooltip = Widgets.Label("", Theme.SmallSize + 1, Theme.CardTextFocused);
            tooltip.BackgroundColor = Theme.CardInfoFocused;
            Widgets.Round(tooltip, 8);
            tooltip.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            tooltip.PivotPoint = Tizen.NUI.PivotPoint.TopCenter;
            tooltip.PositionUsesPivotPoint = true;
            tooltip.Hide();
            chrome.Add(tooltip);

            panel = new ListPanel();
            Root.Add(panel);

            statsLabel = Widgets.Label("", Theme.SmallSize, Theme.Text, 820, 270);
            statsLabel.MultiLine = true;
            statsLabel.BackgroundColor = Theme.Scrim;
            Widgets.Round(statsLabel, 10);
            statsLabel.Position = new Position(60, 60);
            Widgets.SetVisible(statsLabel, app.Config.Player.ShowStats);
            Root.Add(statsLabel);

            toast = Widgets.Label("", Theme.TitleSize - 2, Theme.Text, 0, 0);
            toast.BackgroundColor = new Color(0.09f, 0.09f, 0.09f, 0.9f);
            Widgets.Round(toast, 12);
            toast.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            toast.PivotPoint = Tizen.NUI.PivotPoint.TopCenter;
            toast.PositionUsesPivotPoint = true;
            toast.Position = new Position(960, 70);
            toast.Hide();
            Root.Add(toast);
        }

        private Button AddButton(Action action, string icon, ref int x)
        {
            var b = MakeButton(action, icon, x);
            x += 96;
            return b;
        }

        private Button AddRightButton(Action action, string icon)
        {
            Button b = MakeButton(action, icon, 0);
            rightButtons.Add(b);
            return b;
        }

        private Button MakeButton(Action action, string icon, int x)
        {
            var view = new View { Position = new Position(x, ButtonsY), Size = new Size(ButtonSize, ButtonSize),
                                  BackgroundColor = Theme.GlassButton, PivotPoint = Tizen.NUI.PivotPoint.Center,
                                  PositionUsesPivotPoint = false };
            Widgets.Round(view, ButtonSize / 2);
            var img = new ImageView { ResourceUrl = Theme.Icon(icon), Size = new Size(44, 44), Position = new Position(14, 14) };
            view.Add(img);
            chrome.Add(view);
            var indicator = Widgets.Rounded(Theme.Accent, 28, 5, 2.5f);
            indicator.Position = new Position(x + (ButtonSize - 28) / 2, ButtonsY + ButtonSize + 7);
            indicator.Hide();
            chrome.Add(indicator);
            var b = new Button { Action = action, View = view, Icon = img, Indicator = indicator };
            buttons.Add(b);
            return b;
        }

        // "Skip <segment>" pill, only present while a manual SponsorBlock segment is offered.
        private Button AddSkipButton()
        {
            var view = new View { Position = new Position(0, ButtonsY), Size = new Size(320, ButtonSize),
                                  BackgroundColor = Theme.GlassButton, PivotPoint = Tizen.NUI.PivotPoint.Center,
                                  PositionUsesPivotPoint = false };
            Widgets.Round(view, ButtonSize / 2);
            var img = new ImageView { ResourceUrl = Theme.Icon("skip_segment"), Size = new Size(40, 40), Position = new Position(20, 16) };
            view.Add(img);
            var label = Widgets.Label(T("Skip"), Theme.BodySize, Theme.Text, 236, ButtonSize);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Position = new Position(72, 0);
            view.Add(label);
            chrome.Add(view);
            var b = new Button { Action = Action.Skip, View = view, Icon = img, Label = label, Available = false };
            buttons.Add(b);
            rightButtons.Add(b);
            return b;
        }

        // Right group packed against the right edge; unavailable buttons leave no gap.
        private void LayoutRightButtons()
        {
            int right = ProgressX + ProgressWidth;
            foreach (Button b in rightButtons)
            {
                Widgets.SetVisible(b.View, b.Available);
                if (!b.Available)
                {
                    b.Indicator?.Hide();
                    continue;
                }
                right -= (int)b.View.SizeWidth;
                b.View.PositionX = right;
                if (b.Indicator != null)
                {
                    b.Indicator.PositionX = right + (b.View.SizeWidth - 28) / 2;
                }
                right -= 24;
            }
            UpdateIndicators();
        }

        // ---------------------------------------------------------------- lifecycle

        public override void OnShow()
        {
            ShowChrome(Focus.Hidden);
            tick = new Timer(250);
            tick.Tick += (s, e) => { OnTick(); return true; };
            tick.Start();
            captionTick = new Timer(100);
            captionTick.Tick += (s, e) => { UpdateCaption(); return true; };
            captionTick.Start();
            _ = Start();
            _ = LoadSuggestions();
            _ = LoadDetails();
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
                Toast(T("Resuming at {0}", Format(start)));
                noSkipUntil = DateTime.Now.AddSeconds(3);
            }
            int height = maxHeight;
            string codecs = App.Config.Player.Codecs;
            string audio = audioTrack;
            ShowLoading(T("Loading…"), opaque: true);
            await lastTeardown;
            IntPtr created = await Task.Run(() => NativeCore.PlayerCreateYouTube(window, w, h, item.Id, height, codecs, start, audio));
            if (closing)
            {
                Release(created);
                return;
            }
            player = created;
            if (player == IntPtr.Zero)
            {
                ShowError(YouTube.LastPlayError());
                return;
            }
            info = YouTube.Playback(player);
            live = info.Live;
            if (item.IsList && info.Title.Length > 0)
            {
                titleLabel.Text = info.Title;   // the card showed the list's name
            }
            metaLabel.Text = MetaLine();
            if (details != null && details.ChannelId.Length == 0 && info.ChannelId.Length > 0)
            {
                details.ChannelId = info.ChannelId;   // the details answered first, without it
                ChipOf(Social.Subscribe).View.Show();
                UpdateSocial();
            }
            LayoutBars();
            rightButtons.Find(b => b.Action == Action.Audio).Available = info.AudioTracks.Count > 1;
            rightButtons.Find(b => b.Action == Action.Captions).Available = info.Captions.Count > 0;
            rightButtons.Find(b => b.Action == Action.Speed).Available = !live;
            rightButtons.Find(b => b.Action == Action.Loop).Available = !live;
            LayoutRightButtons();
            if (!captionsAutoPicked)
            {
                captionsAutoPicked = true;
                AutoPickCaptions();
            }
            if (sessionSpeed != 1.0 && !live)
            {
                IntPtr p = player;
                double s = sessionSpeed;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);  // the rate applies once prepared
                    NativeCore.PlayerSetRate(p, s);
                });
            }
        }

        private void ShowError(PlayError e)
        {
            ShowLoading("", opaque: true);
            spin.Stop();
            spinner.Hide();
            stateLabel.MultiLine = true;
            stateLabel.Size = new Size(1600, 440);
            stateLabel.Position = new Position(160, 340);
            var (title, hint) = ErrorTexts(e);
            // YouTube's own message says which case it is (private, members only, bot check...); else the technical one.
            string why = e.Reason.Length > 0 ? e.Reason : e.Kind == "player" || e.Kind == "other" || e.Kind == "formats" ? e.Detail : "";
            var lines = new List<string> { title };
            if (why.Length > 0)
            {
                lines.Add(why);
            }
            if (hint.Length > 0)
            {
                lines.Add(hint);
            }
            lines.Add(T("Back to return"));
            stateLabel.Text = string.Join("\n\n", lines);
        }

        private static (string Title, string Hint) ErrorTexts(PlayError e)
        {
            switch (e.Kind)
            {
                case "login":
                    return e.SignedIn
                        ? (T("YouTube refused this video, even with your account"),
                           T("YouTube may be flagging your network as a bot. Wait a while, or restart your router to get a new IP address."))
                        : (T("YouTube asks you to sign in to watch this video"), T("Sign in from the Account screen, then try again."));
                case "age":
                    return (T("This video is age-restricted"),
                            e.SignedIn ? T("Your YouTube account must be age-verified to watch it.")
                                       : T("Sign in with an age-verified account, then try again."));
                case "upcoming":
                    return (T("This live has not started yet"), "");
                case "unplayable":
                    return (T("YouTube does not allow this video to be played here"), "");
                case "unavailable":
                    return (T("This video is unavailable"), "");
                case "rate_limited":
                    return (T("YouTube is receiving too many requests from your network"), T("Wait a few minutes, then try again."));
                case "http":
                    return (T("YouTube answered with an error ({0})", e.Status.Replace("HTTP_", "")), T("Try again later."));
                case "network":
                    return (T("Cannot reach YouTube"), T("Check the TV's internet connection."));
                case "formats":
                    return (T("No video format this TV can play"), T("Try other video codecs in Settings > Player."));
                case "player":
                    return (T("The TV's player refused this video"), "");
                default:
                    return (T("Cannot play this video"), "");
            }
        }

        // "1,2 M de vues  •  12 mars 2023" (the card's texts until the player / details answer).
        private string MetaLine()
        {
            if (live || item.Live)
            {
                return "●  " + T("LIVE") + (string.IsNullOrEmpty(item.Views) ? "" : "  •  " + item.Views);
            }
            string views = FormatViews(info.Views);
            if (views.Length == 0)
            {
                views = item.Views ?? "";
            }
            string date = details != null && details.Date.Length > 0 ? details.Date : item.Published ?? "";
            return views.Length > 0 && date.Length > 0 ? views + "  •  " + date : views + date;
        }

        // "Channel  •  1,2 M de vues  •  12 mars 2023" (description panel).
        private string ChannelLine()
        {
            string channel = details != null && details.Channel.Length > 0 ? details.Channel
                           : !string.IsNullOrEmpty(item.Channel) ? item.Channel : info.Author;
            string meta = MetaLine();
            return meta.Length > 0 ? channel + "  •  " + meta : channel;
        }

        // Where to start: the furthest of our own saved position and the account's progress as YouTube reports
        // it on the card (watched on another device); nearly finished or barely started videos start over.
        private static long ResumePoint(VideoItem v)
        {
            long local = LocalStore.ResumePosition(v.Id);
            if (LocalStore.Progress(v.Id) >= 0.99)
            {
                local = 0;
            }
            long duration = ParseDuration(v.Duration);
            long remote = v.WatchedPercent > 0 && v.WatchedPercent < 95 && duration > 0 ? duration * v.WatchedPercent / 100 : 0;
            long start = Math.Max(local, remote);
            return start > 30000 && (duration <= 0 || start < duration - 30000) ? start : 0;
        }

        // "1:02:03" / "12:34" -> ms; 0 when not a duration ("EN DIRECT", "MIX"...).
        private static long ParseDuration(string text)
        {
            long ms = 0;
            foreach (string part in (text ?? "").Split(':'))
            {
                if (!int.TryParse(part, out int n))
                {
                    return 0;
                }
                ms = ms * 60 + n;
            }
            return ms * 1000;
        }

        // ---------------------------------------------------------------- actions row (channel, subscribe, like)

        private Chip AddChip(Social kind, string icon, string text)
        {
            var view = new View { Size = new Size(200, ChipH), Position = new Position(0, ActionsY),
                                  PivotPoint = Tizen.NUI.PivotPoint.Center, PositionUsesPivotPoint = false };
            Widgets.Round(view, ChipH / 2f);
            ImageView image = null;
            if (kind == Social.Channel)
            {
                image = Widgets.Round(new ImageView { Size = new Size(48, 48), Position = new Position(8, 8), BackgroundColor = Theme.SurfaceHigh }, 24);
            }
            else if (icon != null)
            {
                image = new ImageView { ResourceUrl = Theme.Icon(icon), Size = new Size(36, 36), Position = new Position(22, 14) };
            }
            if (image != null)
            {
                view.Add(image);
            }
            var label = Widgets.Label(text, Theme.BodySize + 1, Theme.Text, 600, ChipH);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Position = new Position(image != null ? (kind == Social.Channel ? 70 : 68) : 30, 0);
            view.Add(label);
            chrome.Add(view);
            var chip = new Chip { Kind = kind, View = view, Image = image, Label = label };
            chips.Add(chip);
            return chip;
        }

        // Widths from the text length (a label's natural size is not known before layout), left to right.
        private void LayoutChips()
        {
            int x = ProgressX;
            foreach (Chip c in chips)
            {
                if (!c.View.Visibility)
                {
                    continue;
                }
                string text = c.Label.Text ?? "";
                int width = (int)c.Label.PositionX + Math.Max(40, (int)(text.Length * (Theme.BodySize + 1) * 0.53f)) + 30;
                width = Math.Min(c.Kind == Social.Channel ? 640 : 360, width);
                c.View.SizeWidth = width;
                c.Label.SizeWidth = width - c.Label.PositionX - 24;
                c.View.PositionX = x;
                x += width + 18;
            }
            RefreshChips();
        }

        private void RefreshChips()
        {
            for (int i = 0; i < chips.Count; i++)
            {
                Chip c = chips[i];
                bool f = focus == Focus.Actions && i == chipIndex;
                // Unfocused: glass pills, red "S'abonner" as on YouTube; liked / disliked thumbs turn red.
                c.View.BackgroundColor = f ? Theme.CardInfoFocused
                                       : c.Kind == Social.Subscribe && !c.Active ? Theme.Accent : Theme.GlassButton;
                c.Label.TextColor = f ? Theme.CardTextFocused : Theme.Text;
                if (c.Image != null && c.Kind != Social.Channel)
                {
                    c.Image.Color = c.Active ? Theme.Accent : f ? new Color(0.16f, 0.16f, 0.16f, 1f) : Color.White;
                }
                c.View.Scale = f ? new Vector3(1.06f, 1.06f, 1f) : Vector3.One;
            }
        }

        private Chip ChipOf(Social kind) => chips.Find(c => c.Kind == kind);

        private void MoveChip(int delta)
        {
            int i = chipIndex;
            do
            {
                i += delta;
            }
            while (i >= 0 && i < chips.Count && !chips[i].View.Visibility);
            if (i >= 0 && i < chips.Count)
            {
                chipIndex = i;
            }
            RefreshChips();
        }

        private async Task LoadDetails()
        {
            VideoDetails d = await YouTube.Details(item.Id);
            if (closing || d.Error != null)
            {
                return;
            }
            details = d;
            if (d.ChannelId.Length == 0)
            {
                d.ChannelId = info.ChannelId ?? "";   // videoDetails.channelId from the player answer
            }
            metaLabel.Text = MetaLine();
            Chip channel = ChipOf(Social.Channel);
            if (d.Channel.Length > 0)
            {
                channel.Label.Text = d.Subscribers.Length > 0 ? d.Channel + "  ·  " + d.Subscribers : d.Channel;
            }
            if (d.ChannelAvatar.Length > 0)
            {
                channel.Image.ResourceUrl = d.ChannelAvatar;
            }
            Chip sub = ChipOf(Social.Subscribe);
            sub.Active = d.Subscribed == 1;
            Widgets.SetVisible(sub.View, d.ChannelId.Length > 0);
            Chip like = ChipOf(Social.Like), dislike = ChipOf(Social.Dislike);
            like.View.Show();
            dislike.View.Show();
            UpdateSocial();
        }

        // Texts and states of the subscribe / like / dislike chips from `details`.
        private void UpdateSocial()
        {
            Chip sub = ChipOf(Social.Subscribe), like = ChipOf(Social.Like), dislike = ChipOf(Social.Dislike);
            sub.Label.Text = sub.Active ? T("Subscribed") : T("Subscribe");
            like.Active = details.LikeStatus == "LIKE";
            dislike.Active = details.LikeStatus == "DISLIKE";
            like.Label.Text = details.Likes >= 0 ? FormatCount(details.Likes) : T("Like");
            dislike.Label.Text = details.Dislikes >= 0 ? FormatCount(details.Dislikes) : T("Dislike");
            LayoutChips();
        }

        private void ActivateChip(Chip c)
        {
            if (c.Kind == Social.Channel)
            {
                string id = details != null && details.ChannelId.Length > 0 ? details.ChannelId : info.ChannelId;
                if (string.IsNullOrEmpty(id))
                {
                    Toast(T("Channel unavailable"));
                    return;
                }
                string name = details != null && details.Channel.Length > 0 ? details.Channel : item.Channel;
                App.Replace(new ChannelScreen(App, id, name, details?.ChannelAvatar, details?.Subscribed ?? -1));
                return;
            }
            if (!signedIn)
            {
                Toast(c.Kind == Social.Subscribe ? T("Sign in (Account menu) to subscribe") : T("Sign in (Account menu) to rate videos"));
                return;
            }
            if (details == null || socialBusy)
            {
                return;
            }
            _ = c.Kind == Social.Subscribe ? ToggleSubscribe(c) : Rate(c.Kind == Social.Like ? "LIKE" : "DISLIKE");
        }

        private async Task ToggleSubscribe(Chip c)
        {
            socialBusy = true;
            bool subscribe = !c.Active;
            c.Active = subscribe;   // optimistic
            UpdateSocial();
            string error = await YouTube.Subscribe(details.ChannelId, subscribe);
            socialBusy = false;
            if (closing)
            {
                return;
            }
            if (error != null)
            {
                c.Active = !subscribe;
                UpdateSocial();
                Toast(T("Subscription failed"));
                return;
            }
            details.Subscribed = subscribe ? 1 : 0;
            App.Home.InvalidateRows();
            Toast(subscribe ? T("Subscription added") : T("Subscription removed"));
        }

        // Pressing the current rating again removes it; counts follow (they are estimates anyway).
        private async Task Rate(string wanted)
        {
            socialBusy = true;
            string before = details.LikeStatus;
            long likes = details.Likes, dislikes = details.Dislikes;
            string next = before == wanted ? "INDIFFERENT" : wanted;
            Adjust(before, -1);
            Adjust(next, +1);
            details.LikeStatus = next;
            UpdateSocial();
            string error = await YouTube.Rate(item.Id, next == "LIKE" ? "like" : next == "DISLIKE" ? "dislike" : "none");
            socialBusy = false;
            if (closing)
            {
                return;
            }
            if (error != null)
            {
                details.LikeStatus = before;
                details.Likes = likes;
                details.Dislikes = dislikes;
                UpdateSocial();
                Toast(T("Failed: {0}", error));
            }
        }

        private void Adjust(string status, int delta)
        {
            if (status == "LIKE" && details.Likes >= 0)
            {
                details.Likes = Math.Max(0, details.Likes + delta);
            }
            else if (status == "DISLIKE" && details.Dislikes >= 0)
            {
                details.Dislikes = Math.Max(0, details.Dislikes + delta);
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
            // In a playlist / mix the row is what comes next in it (and Next / the end of the video plays its first).
            suggestions = InList
                ? new FeedRow(item.Kind == "mix" || item.PlaylistId.StartsWith("RD") ? T("Up next in the mix") : T("Up next in the playlist"),
                              c => YouTube.Playlist(item.Id, item.PlaylistId), 1920 - 40)
                : new FeedRow(T("Suggested videos"), c => YouTube.Related(item.Id), 1920 - 40)
            {
                Position = new Position(40, SuggestionsY),
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
            captionTick?.Stop();
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

        // Quality / audio track change: same video, new player at the current position.
        private void Restart()
        {
            SavePosition();
            long pos = stats.PositionMs;
            Release(player);
            player = IntPtr.Zero;
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
                    ShowLoading(T("Reconnecting…"), opaque: false);   // over the last frame
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
            if ((focus == Focus.Actions || focus == Focus.Timeline || focus == Focus.Buttons) && stats.State == 3 && now > chromeUntil && pendingSeekMs < 0)
            {
                HideChrome();
            }
            UpdateChrome(now);
            if (statsLabel.Visibility && player != IntPtr.Zero)
            {
                statsLabel.Text =
                    $"  buffer  video {stats.VideoQueuedMs / 1000.0:F1}s  audio {stats.AudioQueuedMs / 1000.0:F1}s\n" +
                    $"  bitrate  video {stats.VideoKbps:F0} kb/s  audio {stats.AudioKbps:F0} kb/s\n" +
                    $"  stalls {stats.Stalls} (max {stats.LongestStallMs} ms)  underruns v{stats.VideoUnderruns}/a{stats.AudioUnderruns}\n" +
                    $"  errors {stats.Errors}   speed {sessionSpeed:0.##}x   max {maxHeight}p" +
                    (info.Current != null ? "   playing " + QualityLabel(info.Current) : "") +
                    (info.AudioTrack.Length > 0 ? "   audio " + info.AudioTrack : "") + "\n" + NativeCore.RecentLog(3);
            }
            if (toast.Visibility && now > toastUntil)
            {
                toast.Hide();
            }
            if (focus == Focus.UpNext)
            {
                int left = (int)Math.Ceiling((upNextAt - now).TotalSeconds);
                upNextCountdown.Text = T("Playing in {0} s", Math.Max(0, left));
                if (left <= 0)
                {
                    App.Replace(new PlayerScreen(App, upNextItem));
                }
            }
            if (stats.Eos != 0 && !closing && !ended)
            {
                ended = true;
                OnEnded();
            }
        }

        private void OnEnded()
        {
            captionLabel.Hide();
            if (Loop)
            {
                App.Replace(new PlayerScreen(App, item, 0, maxHeight));
            }
            else if (InList && suggestions?.FirstItem != null)
            {
                App.Replace(new PlayerScreen(App, suggestions.FirstItem));
            }
            else if (!live && App.Config.Player.Autoplay && suggestions?.FirstItem != null)
            {
                ShowUpNext(suggestions.FirstItem);
            }
            else
            {
                // Stays on the finished video with its info and controls (replay, suggestions); Back leaves.
                buttonIndex = buttons.IndexOf(playButton);
                ShowChrome(Focus.Buttons);
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
            string icon = stats.State == 4 || ended ? "play" : "pause";
            if (icon != lastPlayIcon)
            {
                lastPlayIcon = icon;
                playButton.Icon.ResourceUrl = Theme.Icon(icon);
            }
            if (live)
            {
                progressFill.SizeWidth = ProgressWidth;
                playhead.PositionX = ProgressX + ProgressWidth - 9;
                timeLabel.Text = "● EN DIRECT";
                durationLabel.Text = "";
                return;
            }
            long pos = pendingSeekMs >= 0 ? pendingSeekMs : stats.PositionMs;
            long dur = Math.Max(stats.DurationMs, 1);
            float fill = (float)(ProgressWidth * Math.Min(1.0, pos / (double)dur));
            progressFill.SizeWidth = fill;
            playhead.PositionX = ProgressX + fill - 9;
            timeLabel.Text = pendingSeekMs >= 0 ? $"{Format(pos)}   ({SignedSeconds(pendingSeekMs - stats.PositionMs)})" : Format(pos);
            durationLabel.Text = Format(stats.DurationMs);
            if (focus == Focus.Buttons)
            {
                UpdateTooltip();
            }
        }

        // ---------------------------------------------------------------- subtitles

        // Turns on the subtitles of the preferred language (Settings), translating another track if allowed.
        private void AutoPickCaptions()
        {
            string lang = App.Config.Player.CaptionLang;
            if (lang.Length == 0 || info.Captions.Count == 0)
            {
                return;
            }
            CaptionTrack exact = info.Captions.Find(c => !c.Auto && Languages.Base(c.Lang) == lang)
                                 ?? info.Captions.Find(c => Languages.Base(c.Lang) == lang);
            if (exact != null)
            {
                SetCaptions(new CaptionChoice { Track = exact }, false);
                return;
            }
            CaptionChoice translated = TranslationChoice(lang);
            if (App.Config.Player.CaptionTranslate && translated != null)
            {
                SetCaptions(translated, false);
            }
        }

        // A track YouTube can machine-translate to `lang` (a human-made one when possible).
        private CaptionChoice TranslationChoice(string lang)
        {
            string code = null;
            foreach (string c in info.Translations.Keys)
            {
                if (c == lang || (code == null && Languages.Base(c) == lang))
                {
                    code = c;
                }
            }
            CaptionTrack source = info.Captions.Find(c => c.Translatable && !c.Auto) ?? info.Captions.Find(c => c.Translatable);
            return code != null && source != null ? new CaptionChoice { Track = source, TranslateTo = code } : null;
        }

        private void SetCaptions(CaptionChoice choice, bool announce)
        {
            captions = choice;
            cues = null;
            captionShown = "";
            captionLabel.Hide();
            int gen = ++captionGeneration;
            UpdateIndicators();
            if (choice == null)
            {
                if (announce)
                {
                    Toast(T("Subtitles off"));
                }
                return;
            }
            if (announce)
            {
                Toast(T("Subtitles: {0}", CaptionName(choice)));
            }
            _ = LoadCues(choice, gen);
        }

        private async Task LoadCues(CaptionChoice choice, int gen)
        {
            List<CaptionCue> list = await YouTube.Captions(choice.Track.Url, choice.TranslateTo);
            if (closing || gen != captionGeneration)
            {
                return;
            }
            if ((list == null || list.Count == 0) && choice.TranslateTo.Length > 0)
            {
                // YouTube often refuses translations (HTTP 429): show the source track instead.
                list = await YouTube.Captions(choice.Track.Url, "");
                if (closing || gen != captionGeneration)
                {
                    return;
                }
                if (list != null && list.Count > 0)
                {
                    captions = new CaptionChoice { Track = choice.Track };
                    Toast(T("Translation unavailable: subtitles in {0}", choice.Track.Name));
                }
            }
            if (list == null || list.Count == 0)
            {
                Toast(T("Subtitles unavailable"));
                captions = null;
                UpdateIndicators();
                return;
            }
            cues = list;
        }

        private string CaptionName(CaptionChoice c)
        {
            if (c.TranslateTo.Length == 0)
            {
                return c.Track.Name;
            }
            return (info.Translations.TryGetValue(c.TranslateTo, out string name) && name.Length > 0 ? name : Languages.Name(c.TranslateTo))
                   + " " + T("(auto-translated)");
        }

        // The lines active at the playback position (speech-recognition tracks overlap: keep the last two).
        private void UpdateCaption()
        {
            if (cues == null || player == IntPtr.Zero || ended || focus == Focus.Info)
            {
                if (captionLabel.Visibility)
                {
                    captionLabel.Hide();
                    captionShown = "";
                }
                return;
            }
            NativeCore.PlayerGetStats(player, out NativeCore.PlayerStats now);
            long pos = now.PositionMs;
            var active = new List<string>();
            foreach (CaptionCue c in cues)
            {
                if (c.StartMs > pos)
                {
                    break;
                }
                if (pos < c.EndMs)
                {
                    active.Add(c.Text);
                }
            }
            if (active.Count > 2)
            {
                active.RemoveRange(0, active.Count - 2);
            }
            string text = active.Count == 0 ? "" : Widgets.Wrap(string.Join("\n", active), 46);
            if (text != captionShown)
            {
                captionShown = text;
                if (text.Length == 0)
                {
                    captionLabel.Hide();
                }
                else
                {
                    // One space of padding on each line, inside the background.
                    captionLabel.Text = " " + text.Replace("\n", " \n ") + " ";
                    ApplyCaptionStyle();
                    captionLabel.Show();
                }
            }
            captionLabel.PositionY = chrome.Visibility && focus != Focus.Hidden && focus != Focus.Suggestions
                ? CaptionBottomChrome : CaptionBottomFree;
        }

        private void ApplyCaptionStyle()
        {
            PlayerOptions p = App.Config.Player;
            float alpha = PlayerOptions.CaptionBackgrounds[p.CaptionBackground];
            captionLabel.PixelSize = PlayerOptions.CaptionSizes[p.CaptionSize];
            captionLabel.BackgroundColor = new Color(0.03f, 0.03f, 0.03f, alpha);
            StyleCaption(captionLabel, alpha);
        }

        // Without a background the text needs an outline to stay readable on bright pictures.
        public static void StyleCaption(TextLabel label, float backgroundAlpha)
        {
            var outline = new PropertyMap();
            outline.Add("color", new PropertyValue(Color.Black));
            outline.Add("width", new PropertyValue(backgroundAlpha < 0.3f ? 3f : 0f));
            label.Outline = outline;
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
                    skipButton.Available = false;
                    LayoutRightButtons();
                    if (focus == Focus.Buttons && buttons[buttonIndex] == skipButton)
                    {
                        buttonIndex = buttons.IndexOf(playButton);
                        RefreshButtons();
                    }
                }
                return;
            }
            SponsorCategory cat = SponsorCategory.Find(current.Category);
            string label = cat != null ? T(cat.Label) : current.Category;
            if (App.Config.ModeFor(current.Category) == SkipMode.Auto)
            {
                SkipTo(current);
                Toast(T("{0} skipped", label));
            }
            else if (offered != current && focus != Focus.Panel && focus != Focus.Info)
            {
                // SmartTube shows the controls with the focus on "Skip".
                offered = current;
                skipButton.Label.Text = T("Skip: {0}", label);
                skipButton.Available = true;
                LayoutRightButtons();
                ShowChrome(Focus.Buttons);
                buttonIndex = buttons.IndexOf(skipButton);
                RefreshButtons();
            }
        }

        private void SkipTo(SkipSegment s)
        {
            s.Done = true;
            offered = null;
            skipButton.Available = false;
            LayoutRightButtons();
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
            // Focused seek bar: bigger playhead and a thicker track.
            RefreshChips();
            bool onTimeline = focus == Focus.Timeline;
            playhead.Scale = onTimeline ? new Vector3(1.6f, 1.6f, 1f) : Vector3.One;
            track.SizeHeight = onTimeline ? 10 : 6;
            progressFill.SizeHeight = track.SizeHeight;
            UpdateChrome(DateTime.Now);
        }

        private void HideChrome()
        {
            focus = Focus.Hidden;
            chrome.Hide();
            chrome.PositionY = 0;
            tooltip.Hide();
            suggestions?.SetFocused(false);
        }

        private void RefreshButtons()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                bool f = focus == Focus.Buttons && i == buttonIndex;
                buttons[i].View.BackgroundColor = f ? Theme.Accent : Theme.GlassButton;
                buttons[i].View.Scale = f ? new Vector3(1.12f, 1.12f, 1f) : Vector3.One;
            }
            UpdateTooltip();
        }

        private void UpdateIndicators()
        {
            foreach (Button b in buttons)
            {
                if (b.Indicator == null)
                {
                    continue;   // the skip pill
                }
                bool on = b.Action == Action.Captions ? captions != null
                        : b.Action == Action.Loop ? Loop
                        : b.Action == Action.Stats && statsLabel != null && statsLabel.Visibility;
                Widgets.SetVisible(b.Indicator, on && b.Available);
            }
        }

        // Name (and state) of the focused button, above it.
        private void UpdateTooltip()
        {
            if (focus != Focus.Buttons || buttonIndex >= buttons.Count || buttons[buttonIndex] == skipButton)
            {
                tooltip.Hide();
                return;
            }
            Button b = buttons[buttonIndex];
            string step = App.Config.Player.SeekStepSeconds + " s";
            string text = b.Action switch
            {
                Action.Restart => T("Restart"),
                Action.Rewind => T("Back {0}", step),
                Action.PlayPause => stats.State == 4 || ended ? T("Play") : T("Pause"),
                Action.Forward => T("Forward {0}", step),
                Action.Next => InList ? T("Next in the list") : T("Next video"),
                Action.Captions => T("Subtitles: {0}", captions != null ? CaptionName(captions) : T("off")),
                Action.Audio => T("Audio track: {0}", AudioName(info.AudioTrack)),
                Action.Speed => T("Speed: {0}", SpeedLabel(sessionSpeed)),
                Action.Quality => T("Quality: {0}", info.Current != null ? QualityLabel(info.Current) : maxHeight + "p"),
                Action.Loop => Loop ? T("Loop: on") : T("Loop: off"),
                Action.Info => T("Description"),
                Action.Stats => T("Stats for nerds"),
                _ => "",
            };
            if (tooltip.Text != "  " + text + "  ")
            {
                tooltip.Text = "  " + text + "  ";
            }
            // Centred above the button, kept on screen.
            float center = b.View.PositionX + b.View.SizeWidth / 2;
            float half = Math.Min(400, text.Length * 6.5f + 30);
            tooltip.Position = new Position(Math.Max(ProgressX + half, Math.Min(ProgressX + ProgressWidth - half, center)), ButtonsY + ButtonSize + 22);
            tooltip.Show();
        }

        // Next / previous visible button in screen order.
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
            if (key.StartsWith("XF86AudioRaise") || key.StartsWith("XF86AudioLower") || key.StartsWith("XF86AudioMute"))
            {
                return true;   // volume: the TV handles it, the controls stay as they are
            }
            // Remote play/pause (Samsung sends XF86PlayBack) works whatever has the focus.
            if (key == "XF86PlayBack" || key == "XF86AudioPlay" || key == "XF86AudioPause")
            {
                TogglePause();
                if (focus == Focus.Hidden)
                {
                    buttonIndex = buttons.IndexOf(playButton);
                    ShowChrome(Focus.Buttons);
                }
                else if (focus != Focus.Panel && focus != Focus.Info && focus != Focus.UpNext)
                {
                    ShowChrome(Focus.Hidden);
                }
                return true;
            }
            switch (focus)
            {
                case Focus.Hidden:
                    if (back)
                    {
                        return false;
                    }
                    if (key == "Return")
                    {
                        TogglePause();
                        buttonIndex = buttons.IndexOf(playButton);
                        ShowChrome(Focus.Buttons);
                    }
                    else if ((key == "Left" || key == "Right") && !live)
                    {
                        SeekBy(key == "Left" ? -1 : 1);
                        ShowChrome(Focus.Timeline);
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

                case Focus.Timeline:
                    switch (key)
                    {
                        case "Left":
                        case "Right":
                            SeekBy(key == "Left" ? -1 : 1);
                            break;
                        case "Return":
                            if (pendingSeekMs >= 0)
                            {
                                CommitSeek();
                            }
                            else
                            {
                                TogglePause();
                            }
                            break;
                        case "Down":
                            buttonIndex = buttons.IndexOf(playButton);
                            focus = Focus.Buttons;
                            break;
                        case "Up":
                            focus = Focus.Actions;
                            break;
                        case "XF86Back":
                        case "Escape":
                            HideChrome();
                            return true;
                    }
                    ShowChrome(Focus.Hidden);
                    return true;

                case Focus.Actions:
                    switch (key)
                    {
                        case "Left":
                            MoveChip(-1);
                            break;
                        case "Right":
                            MoveChip(1);
                            break;
                        case "Return":
                            ActivateChip(chips[chipIndex]);
                            if (closing)
                            {
                                return true;
                            }
                            break;
                        case "Down":
                            if (live)
                            {
                                buttonIndex = buttons.IndexOf(playButton);
                                focus = Focus.Buttons;
                            }
                            else
                            {
                                focus = Focus.Timeline;
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
                            if (closing || focus != Focus.Buttons)
                            {
                                return true;   // replaced by another video, or a panel took the focus
                            }
                            break;
                        case "Down":
                            if (suggestions != null && suggestions.Count > 0)
                            {
                                focus = Focus.Suggestions;
                            }
                            break;
                        case "Up":
                            focus = live ? Focus.Actions : Focus.Timeline;
                            break;
                        case "XF86Back":
                        case "Escape":
                            if (ended)
                            {
                                return false;
                            }
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

                case Focus.Info:
                    InfoKey(key);
                    return true;

                case Focus.UpNext:
                    return UpNextKey(key);
            }
            return true;
        }

        private void Activate(Action action)
        {
            switch (action)
            {
                case Action.Restart:
                    if (ended)
                    {
                        App.Replace(new PlayerScreen(App, item, 0, maxHeight));   // the stream is over: replay it
                    }
                    else
                    {
                        Seek(0);
                    }
                    break;
                case Action.Rewind:
                    SeekBy(-1);
                    break;
                case Action.Forward:
                    SeekBy(1);
                    break;
                case Action.PlayPause:
                    if (ended)
                    {
                        App.Replace(new PlayerScreen(App, item, 0, maxHeight));
                    }
                    else
                    {
                        TogglePause();
                    }
                    break;
                case Action.Next:
                    if (suggestions != null && suggestions.Count > 0)
                    {
                        App.Replace(new PlayerScreen(App, InList ? suggestions.FirstItem : suggestions.FocusedItem));
                    }
                    break;
                case Action.Skip:
                    if (offered != null)
                    {
                        SkipTo(offered);
                        buttonIndex = buttons.IndexOf(playButton);
                    }
                    break;
                case Action.Captions:
                case Action.Audio:
                case Action.Speed:
                case Action.Quality:
                    OpenPanel(action);
                    break;
                case Action.Loop:
                    loopVideoId = Loop ? null : item.Id;
                    Toast(Loop ? T("Loop on") : T("Loop off"));
                    UpdateIndicators();
                    break;
                case Action.Info:
                    OpenInfo();
                    break;
                case Action.Stats:
                    Widgets.SetVisible(statsLabel, !statsLabel.Visibility);
                    UpdateIndicators();
                    break;
            }
        }

        // ---------------------------------------------------------------- option panels

        private void OpenPanel(Action kind)
        {
            panelKind = kind;
            var items = new List<ListPanel.Item>();
            string title = "";
            int width = 560;
            switch (kind)
            {
                case Action.Captions:
                    title = T("Subtitles");
                    width = 680;
                    items.Add(new ListPanel.Item { Text = T("Off"), Checked = captions == null });
                    foreach (CaptionTrack c in info.Captions)
                    {
                        items.Add(new ListPanel.Item
                        {
                            Text = c.Name,
                            Checked = captions != null && captions.Track == c && captions.TranslateTo.Length == 0,
                        });
                    }
                    CaptionChoice tr = PanelTranslation();
                    if (tr != null)
                    {
                        items.Add(new ListPanel.Item
                        {
                            Text = CaptionName(tr),
                            Detail = T("Translated from: {0}", tr.Track.Name),
                            Checked = captions != null && captions.TranslateTo == tr.TranslateTo,
                        });
                    }
                    break;
                case Action.Audio:
                    title = T("Audio track");
                    width = 680;
                    foreach (AudioTrack a in info.AudioTracks)
                    {
                        items.Add(new ListPanel.Item
                        {
                            Text = AudioName(a.Id),
                            Detail = a.Default ? T("Original track") : null,
                            Checked = a.Id == info.AudioTrack,
                        });
                    }
                    break;
                case Action.Speed:
                    title = T("Playback speed");
                    foreach (double s in Speeds)
                    {
                        items.Add(new ListPanel.Item { Text = SpeedLabel(s), Checked = s == sessionSpeed });
                    }
                    break;
                case Action.Quality:
                    title = T("Quality");
                    width = 640;
                    foreach (VideoQuality q in PanelQualities())
                    {
                        items.Add(new ListPanel.Item
                        {
                            Text = QualityLabel(q),
                            Checked = q.Height == (info.Current != null ? info.Current.Height : maxHeight),
                        });
                    }
                    break;
            }
            if (items.Count == 0)
            {
                return;
            }
            tooltip.Hide();
            panel.Open(title, items, ProgressX + ProgressWidth, ButtonsY - 30, width);
            focus = Focus.Panel;
        }

        // The machine-translated entry: to the preferred subtitle language, or the YouTube language, when the
        // video has no track in it.
        private CaptionChoice PanelTranslation()
        {
            string lang = App.Config.Player.CaptionLang.Length > 0 ? App.Config.Player.CaptionLang : App.Config.Language;
            if (info.Captions.Exists(c => Languages.Base(c.Lang) == lang))
            {
                return null;
            }
            return TranslationChoice(lang);
        }

        private void PanelKey(string key)
        {
            switch (key)
            {
                case "Up":
                    panel.Move(-1);
                    return;
                case "Down":
                    panel.Move(1);
                    return;
                case "Return":
                    PanelChoose(panel.Index);
                    ClosePanel();
                    return;
                case "XF86Back":
                case "Escape":
                    ClosePanel();
                    return;
            }
        }

        private void PanelChoose(int i)
        {
            PlayerOptions p = App.Config.Player;
            switch (panelKind)
            {
                case Action.Captions:
                    if (i == 0)
                    {
                        SetCaptions(null, true);
                        p.CaptionLang = "";
                    }
                    else if (i <= info.Captions.Count)
                    {
                        CaptionTrack c = info.Captions[i - 1];
                        SetCaptions(new CaptionChoice { Track = c }, true);
                        p.CaptionLang = Languages.Base(c.Lang);
                    }
                    else
                    {
                        CaptionChoice tr = PanelTranslation();
                        if (tr != null)
                        {
                            SetCaptions(tr, true);
                            p.CaptionLang = Languages.Base(tr.TranslateTo);
                        }
                    }
                    App.Config.Save();   // the choice becomes the default for the next videos
                    break;
                case Action.Audio:
                    AudioTrack a = info.AudioTracks[i];
                    if (a.Id != info.AudioTrack)
                    {
                        audioTrack = a.Id;
                        Toast(T("Audio track: {0}", AudioName(a.Id)));
                        Restart();
                    }
                    break;
                case Action.Speed:
                    sessionSpeed = Speeds[i];
                    if (player != IntPtr.Zero && NativeCore.PlayerSetRate(player, sessionSpeed) != 0 && sessionSpeed != 1.0)
                    {
                        Toast(T("Speed not supported right now"));
                    }
                    else
                    {
                        Toast(sessionSpeed == 1.0 ? T("Normal speed") : T("Speed {0}", SpeedLabel(sessionSpeed)));
                    }
                    break;
                case Action.Quality:
                    VideoQuality q = PanelQualities()[i];
                    if (info.Current == null || q.Height != info.Current.Height)
                    {
                        Toast(T("Quality {0}", QualityLabel(q)));
                        maxHeight = q.Height;
                        Restart();
                    }
                    break;
            }
        }

        private void ClosePanel()
        {
            panel.Hide();
            focus = Focus.Buttons;
            ShowChrome(Focus.Hidden);
        }

        // ---------------------------------------------------------------- description

        private void OpenInfo()
        {
            if (infoPanel == null)
            {
                infoPanel = Widgets.Rounded(new Color(0.07f, 0.07f, 0.07f, 0.96f), 1200, 900, Theme.PanelRadius);
                infoPanel.Position = new Position(360, 90);
                var clip = new View { Position = new Position(60, 50), Size = new Size(1080, 760), ClippingMode = ClippingModeType.ClipChildren };
                infoPanel.Add(clip);
                infoText = Widgets.Label("", Theme.BodySize, Theme.Text, 1080, 10000);
                infoText.MultiLine = true;
                infoText.Ellipsis = false;
                infoText.VerticalAlignment = VerticalAlignment.Top;
                clip.Add(infoText);
                var hint = Widgets.Label(T("▲▼ scroll    Back close"), Theme.SmallSize, Theme.TextDim, 1080, 36);
                hint.Position = new Position(60, 840);
                infoPanel.Add(hint);
                Root.Add(infoPanel);
            }
            string title = titleLabel.Text;
            string description = info.Description.Length > 0 ? info.Description : T("No description.");
            infoText.Text = title + "\n" + ChannelLine() + "\n\n" + description;
            infoScroll = 0;
            infoText.PositionY = 0;
            infoPanel.Show();
            tooltip.Hide();
            chrome.Hide();
            focus = Focus.Info;
        }

        private void InfoKey(string key)
        {
            switch (key)
            {
                case "Up":
                case "Down":
                    // NaturalSize is costly to keep: approximate the text height from its line count.
                    float height = EstimateTextHeight(infoText.Text, 1080, Theme.BodySize);
                    float min = Math.Min(0, 740 - height);
                    infoScroll = Math.Max(min, Math.Min(0, infoScroll + (key == "Up" ? 300 : -300)));
                    infoText.PositionY = infoScroll;
                    break;
                case "Return":
                case "XF86Back":
                case "Escape":
                    infoPanel.Hide();
                    focus = Focus.Buttons;
                    ShowChrome(Focus.Hidden);
                    break;
            }
        }

        public static float EstimateTextHeight(string text, int width, float pixelSize)
        {
            int perLine = Math.Max(1, (int)(width / (pixelSize * 0.52f)));
            int lines = 0;
            foreach (string l in text.Split('\n'))
            {
                lines += Math.Max(1, (l.Length + perLine - 1) / perLine);
            }
            return lines * pixelSize * 1.35f;
        }

        // ---------------------------------------------------------------- up next

        private void ShowUpNext(VideoItem next)
        {
            upNextItem = next;
            if (upNext == null)
            {
                upNext = Widgets.Rounded(new Color(0.07f, 0.07f, 0.07f, 0.95f), 1180, 400, Theme.PanelRadius + 4);
                upNext.Position = new Position(370, 300);
                Root.Add(upNext);
            }
            foreach (View v in new List<View>(upNext.Children))
            {
                upNext.Remove(v);
                v.Dispose();
            }
            var thumb = Widgets.Round(new ImageView { Size = new Size(480, 270), Position = new Position(40, 40),
                                                      BackgroundColor = Theme.Surface, ResourceUrl = next.Thumbnail }, 14);
            upNext.Add(thumb);
            var heading = Widgets.Label(T("Up next"), Theme.BodySize, Theme.Accent, 600);
            heading.Position = new Position(560, 44);
            upNext.Add(heading);
            var title = Widgets.Label(next.Title, Theme.TitleSize, Theme.Text, 580, 100);
            title.MultiLine = true;
            title.Position = new Position(560, 84);
            upNext.Add(title);
            var channel = Widgets.Label(next.Channel, Theme.BodySize, Theme.TextDim, 580);
            channel.Position = new Position(560, 196);
            upNext.Add(channel);
            upNextCountdown = Widgets.Label("", Theme.BodySize, Theme.Text, 580);
            upNextCountdown.Position = new Position(560, 250);
            upNext.Add(upNextCountdown);
            upNextPlay = new Widgets.PillButton(T("Play now"), "play", 330);
            upNextPlay.Position = new Position(560, 296);
            upNext.Add(upNextPlay);
            upNextCancel = new Widgets.PillButton(T("Cancel"), null, 220);
            upNextCancel.Position = new Position(910, 296);
            upNext.Add(upNextCancel);
            upNextFocus = 0;
            upNextPlay.SetFocused(true);
            upNextAt = DateTime.Now.AddSeconds(UpNextSeconds);
            upNextCountdown.Text = T("Playing in {0} s", UpNextSeconds);
            HideChrome();
            panel.Hide();
            infoPanel?.Hide();
            upNext.Show();
            focus = Focus.UpNext;
        }

        private bool UpNextKey(string key)
        {
            switch (key)
            {
                case "Left":
                case "Right":
                    upNextFocus = key == "Left" ? 0 : 1;
                    upNextPlay.SetFocused(upNextFocus == 0);
                    upNextCancel.SetFocused(upNextFocus == 1);
                    return true;
                case "Return":
                    if (upNextFocus == 0)
                    {
                        App.Replace(new PlayerScreen(App, upNextItem));
                    }
                    else
                    {
                        CancelUpNext();
                    }
                    return true;
                case "XF86Back":
                case "Escape":
                    CancelUpNext();
                    return true;
            }
            return true;
        }

        // Stays on the finished video with the controls (replay, suggestions); Retour leaves.
        private void CancelUpNext()
        {
            upNext.Hide();
            buttonIndex = buttons.IndexOf(playButton);
            ShowChrome(Focus.Buttons);
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
                Toast(T("Live: no seeking"));
                return;
            }
            if (ended)
            {
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
            toast.Text = "   " + text + "   ";
            toast.Show();
            toastUntil = DateTime.Now.AddSeconds(2);
        }

        // The video plane is letterboxed and leaves the uncovered area empty: cover it in black.
        private void LayoutBars()
        {
            barA.Hide();
            barB.Hide();
            VideoQuality playing = info.Current;
            if (playing == null || playing.Width <= 0 || playing.Height <= 0)
            {
                return;
            }
            double aspect = playing.Width / (double)playing.Height;
            if (Math.Abs(aspect - 16.0 / 9.0) < 0.01)
            {
                return;
            }
            if (aspect < 16.0 / 9.0)
            {
                int side = (int)Math.Ceiling((1920 - 1080 * aspect) / 2);   // pillarbox: left / right
                barA.Size = new Size(side, 1080);
                barA.Position = new Position(0, 0);
                barB.Size = new Size(side, 1080);
                barB.Position = new Position(1920 - side, 0);
            }
            else
            {
                int band = (int)Math.Ceiling((1080 - 1920 / aspect) / 2);   // letterbox: top / bottom
                barA.Size = new Size(1920, band);
                barA.Position = new Position(0, 0);
                barB.Size = new Size(1920, band);
                barB.Position = new Position(0, 1080 - band);
            }
            barA.Show();
            barB.Show();
        }

        private List<VideoQuality> PanelQualities()
        {
            if (info.Qualities.Count > 0)
            {
                return info.Qualities;
            }
            var list = new List<VideoQuality>();
            foreach (int h in DefaultHeights)
            {
                list.Add(new VideoQuality { Height = h });
            }
            return list;
        }

        private string AudioName(string id)
        {
            AudioTrack a = info.AudioTracks.Find(t => t.Id == id);
            if (a != null && !string.IsNullOrEmpty(a.Name))
            {
                return a.Name;
            }
            return string.IsNullOrEmpty(id) ? T("default") : Languages.Name(id);
        }

        private static string SpeedLabel(double s) => s == 1.0 ? T("Normal") : Decimal(s.ToString("0.##", CultureInfo.InvariantCulture)) + "x";

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

        // Raw view count -> "1.2M views" / "845 views".
        private static string FormatViews(string raw)
        {
            if (!long.TryParse(raw, out long n) || n <= 0)
            {
                return "";
            }
            return n >= 1_000 ? T("{0} views", FormatCount(n)) : n > 1 ? T("{0} views", n) : T("{0} view", n);
        }

        // 54321 -> "54K" / "54 k", 1234567 -> "1.2M" / "1,2 M" (suffixes and decimal mark from the app language).
        private static string FormatCount(long n)
        {
            string Short(double v) => Decimal(v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture));
            return n >= 1_000_000_000 ? T("{0}B", Short(n / 1e9)) : n >= 1_000_000 ? T("{0}M", Short(n / 1e6))
                 : n >= 1_000 ? T("{0}K", Short(n / 1e3)) : n.ToString();
        }

        private static string Format(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private static string SignedSeconds(long ms) => (ms >= 0 ? "+" : "−") + Format(Math.Abs(ms));
    }
}
