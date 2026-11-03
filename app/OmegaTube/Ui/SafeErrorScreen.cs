using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Last-resort error page, used when the ReportScreen itself cannot be built (e.g. a NUI API missing on
    // this TV). Only the oldest NUI members (View, TextLabel, ImageView, Position, Size, colours) and no
    // Widgets/Theme helpers beyond plain values, so it works on any TizenFX. The QR code is tried, and
    // skipped if it fails too.
    public class SafeErrorScreen : Screen
    {
        private const string IssuesUrl = "github.com/IIyn/omegatube/issues";

        private readonly bool fatal;

        public SafeErrorScreen(App app, string error, bool fatal) : base(app)
        {
            this.fatal = fatal;
            Root.BackgroundColor = new Color(0.07f, 0.07f, 0.07f, 1f);

            Add(fatal ? T("OmegaTube cannot start on this TV") : T("OmegaTube ran into an error"), 46, Color.White, 100, 2);
            Add(T("Please report it on GitHub so it can be fixed:") + " " + IssuesUrl
                + "\n" + T("Include the details below (a photo of the screen is fine)."), 28, Color.White, 190, 3);
            Add(SafeDeviceInfo(), 22, new Color(0.7f, 0.7f, 0.7f, 1f), 330, 8);
            Add(Trim(BugReport.Redact(error ?? ""), 14), 20, new Color(1f, 0.6f, 0.6f, 1f), 560, 14);
            Add(fatal ? T("Back: quit") : T("Back or OK: close"), 26, new Color(0.7f, 0.7f, 0.7f, 1f), 990, 1);

            try
            {
                const int qr = 520;
                var host = new View { BackgroundColor = Color.White, Size = new Size(qr, qr), Position = new Position(1920 - 100 - qr, 100) };
                Root.Add(host);
                Widgets.DrawQr(host, qr, Util.Qr.Encode(BugReport.IssueUrl(error, null)));
            }
            catch (Exception e)
            {
                Tizen.Log.Error("OmegaTube", "safe error QR: " + e.Message);
            }
        }

        private void Add(string text, float pixelSize, Color color, int y, int lines)
        {
            var label = new TextLabel(text)
            {
                TextColor = color,
                PixelSize = pixelSize,
                MultiLine = lines > 1,
                Position = new Position(100, y),
                Size = new Size(1100, pixelSize * 1.35f * lines),
            };
            Root.Add(label);
        }

        private static string SafeDeviceInfo()
        {
            try
            {
                return BugReport.DeviceInfo();
            }
            catch (Exception)
            {
                return ".NET: " + Environment.Version;
            }
        }

        private static string Trim(string text, int maxLines)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            return lines.Length <= maxLines ? text : string.Join("\n", lines, 0, maxLines) + "\n…";
        }

        public override bool OnKey(string key)
        {
            if (key == "XF86Back" || key == "Escape" || key == "Return")
            {
                if (fatal)
                {
                    App.Exit();
                }
                else
                {
                    App.Pop();
                }
                return true;
            }
            return true;
        }
    }
}
