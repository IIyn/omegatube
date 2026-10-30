using System.Threading.Tasks;
using OmegaTube.Data;
using OmegaTube.Util;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Bug report: a QR code that opens a pre-filled GitHub issue (see BugReport). Three cases:
    //  - Settings > About: the end of this session's log;
    //  - at launch after a crash: the saved exception;
    //  - fatal: the app could not start (often libomegacore.so not loading on an untested TV); Back quits.
    // ◀ ▶ choose a button, OK activates. "Attach the full log" posts it to the paste service and adds the
    // link to the issue (only with the core loaded: the upload goes through its HTTPS stack).
    public class ReportScreen : Screen
    {
        private const int QrSize = 660;
        private const int TextX = 120, TextW = 940;

        private readonly string crash;
        private readonly bool fatal;
        private readonly View qrHost;
        private readonly TextLabel status;
        private readonly Widgets.PillButton close;
        private Widgets.PillButton attach;   // replaced by a green "attached" badge once done
        private bool onClose;
        private string fullLogUrl;
        private bool uploading, closed;

        public ReportScreen(App app, string crash, bool fatal) : base(app)
        {
            this.crash = crash;
            this.fatal = fatal;
            Root.BackgroundColor = Theme.Background;

            string title = fatal ? T("OmegaTube cannot start on this TV")
                         : crash != null ? T("OmegaTube stopped because of an error")
                         : T("Report a problem");
            string body = fatal ? T("Scan the QR code with your phone: it opens a GitHub issue already filled in with this TV's model and the error. Sending it helps make OmegaTube work on your TV.")
                        : crash != null ? T("Scan the QR code with your phone to report it: the GitHub issue is already filled in with this TV's model and the error. Add what you were doing, then send it.")
                        : T("Scan the QR code with your phone: it opens a GitHub issue already filled in with this TV's model and the end of the log. Describe the problem, then send it.");

            var titleLabel = Widgets.Label(title, Theme.TitleSize + 8, Theme.Text, TextW, 70);
            titleLabel.Position = new Position(TextX, 130);
            Root.Add(titleLabel);
            var bodyLabel = Widgets.Label(body, Theme.BodySize + 2, Theme.Text, TextW, 200);
            bodyLabel.MultiLine = true;
            bodyLabel.Ellipsis = false;
            bodyLabel.Position = new Position(TextX, 240);
            Root.Add(bodyLabel);
            var device = Widgets.Label(BugReport.DeviceInfo(), Theme.SmallSize, Theme.TextDim, TextW, 260);
            device.MultiLine = true;
            device.Position = new Position(TextX, 480);
            Root.Add(device);

            // The upload goes through the core's HTTPS stack, to the service named by the remote config.
            bool canUpload = BugReport.CoreLoaded && NativeCore.ReportAvailable() != 0;
            int x = TextX;
            if (canUpload)
            {
                attach = new Widgets.PillButton(T("Attach the full log"), null, 460);
                attach.Position = new Position(x, 790);
                Root.Add(attach);
                x += 460 + 40;
            }
            else
            {
                onClose = true;
            }
            close = new Widgets.PillButton(fatal ? T("Quit") : T("Close"), null, 260);
            close.Position = new Position(x, 790);
            Root.Add(close);

            status = Widgets.Label(T("A free GitHub account is needed to send the issue.")
                                   + (canUpload ? "\n" + T("The full log is sent to OmegaTube's server and kept 90 days, after removing account data, addresses, titles and searches. Anyone with the link can read it.") : ""),
                                   Theme.SmallSize, Theme.TextDim, TextW, 110);
            status.MultiLine = true;
            status.Ellipsis = false;
            status.Position = new Position(TextX, 900);
            Root.Add(status);

            qrHost = Widgets.Rounded(Color.White, QrSize, QrSize, 18);
            qrHost.Position = new Position(1920 - 120 - QrSize, 150);
            Root.Add(qrHost);
            var caption = Widgets.Label(T("Scan with your phone's camera"), Theme.BodySize, Theme.TextDim, QrSize, 40);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Position = new Position(1920 - 120 - QrSize, 150 + QrSize + 24);
            Root.Add(caption);

            DrawQr();
            Refresh();
        }

        private void DrawQr()
        {
            Widgets.DrawQr(qrHost, QrSize, Qr.Encode(BugReport.IssueUrl(crash, fullLogUrl)));
        }

        private void Refresh()
        {
            if (fullLogUrl == null)
            {
                attach?.SetFocused(!onClose);
            }
            close.SetFocused(onClose);
        }

        private async Task Upload()
        {
            if (uploading || fullLogUrl != null)
            {
                return;
            }
            uploading = true;
            status.Text = T("Uploading the log…");
            string text = BugReport.FullText(crash);
            object res = await Task.Run(() => YouTube.Call(NativeCore.ReportUpload(text)));
            uploading = false;
            if (closed)
            {
                return;
            }
            string url = Json.Str(res, "url", null);
            if (url == null)
            {
                status.Text = T("Upload failed: {0}", Json.Str(res, "error")) + "\n"
                              + T("The log could not be sent: try again later. The QR code also works without the full log.");
                return;
            }
            fullLogUrl = url;
            var done = new Widgets.PillButton(T("Full log attached"), "check", 460);
            done.Position = new Position(TextX, 790);
            done.BackgroundColor = Theme.Success;
            Root.Remove(attach);
            attach.Dispose();
            attach = done;
            Root.Add(attach);
            onClose = true;
            Refresh();
            status.Text = Json.Bool(res, "partial")
                ? T("The log was too long: only its beginning is attached. The QR code now includes the link.")
                : T("The QR code now includes the link to the full log.");
            DrawQr();
        }

        public override void OnHide()
        {
            closed = true;
        }

        public override void OnShow()
        {
            closed = false;
        }

        public override bool OnKey(string key)
        {
            switch (key)
            {
                case "Left":
                case "Right":
                    if (attach != null && fullLogUrl == null)
                    {
                        onClose = key == "Right";
                        Refresh();
                    }
                    return true;
                case "Return":
                    if (!onClose)
                    {
                        _ = Upload();
                    }
                    else if (fatal)
                    {
                        App.Exit();
                    }
                    else
                    {
                        App.Pop();
                    }
                    return true;
                case "XF86Back":
                case "Escape":
                    if (fatal)
                    {
                        App.Exit();
                        return true;
                    }
                    return false;
            }
            return false;
        }
    }
}
