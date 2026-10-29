using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using OmegaTube.Util;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Google account. Signed out: device flow (URL + code, or the QR code), polled until confirmed on another
    // device. Signed in: the profile (name, handle, avatar) and sign-out (OK twice).
    public class AccountScreen : Screen
    {
        private const int CardX = 120, CardY = 230, CardW = 1060, CardH = 640;
        private const int SideX = 1260, SideW = 540;
        private const int QrSize = 460;

        private readonly View signInView, profileView;
        // Signed out
        private readonly TextLabel urlLabel, codeLabel, statusLabel, expiryLabel, qrCaption;
        private readonly View qrHost, statusSpinner;
        private readonly Animation statusSpin;
        private readonly Widgets.PillButton retryButton;
        // Signed in
        private readonly ImageView avatar;
        private readonly TextLabel nameLabel, handleLabel;
        private readonly Widgets.PillButton signOutButton;
        private readonly TextLabel help;

        private bool closed;
        private bool confirmSignOut;
        private int generation;

        public AccountScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;
            var title = Widgets.Label(T("Account"), Theme.TitleSize + 12, Theme.Text, 1200, 70);
            title.Position = new Position(CardX, 64);
            Root.Add(title);
            var subtitle = Widgets.Label(T("Your YouTube account: recommendations, subscriptions and synced history."),
                                         Theme.BodySize + 2, Theme.TextDim, 1600);
            subtitle.Position = new Position(CardX, 140);
            Root.Add(subtitle);
            help = Widgets.Label("", Theme.SmallSize + 1, Theme.TextDim, 1600, 40);
            help.Position = new Position(CardX, 1000);
            Root.Add(help);

            // ---- signed out: steps card + QR card
            signInView = new View { Size = new Size(1920, 1080) };
            Root.Add(signInView);
            var card = Widgets.Rounded(Theme.Panel, CardW, CardH, Theme.PanelRadius + 4);
            card.Position = new Position(CardX, CardY);
            signInView.Add(card);

            card.Add(StepBadge("1", 48, 48));
            var step1 = Widgets.Label(T("On your phone or computer, open"), Theme.BodySize + 2, Theme.TextDim, 860);
            step1.Position = new Position(124, 50);
            card.Add(step1);
            urlLabel = Widgets.Label("", Theme.TitleSize + 8, Theme.Text, 860, 64);
            urlLabel.Position = new Position(124, 92);
            card.Add(urlLabel);

            card.Add(StepBadge("2", 48, 200));
            var step2 = Widgets.Label(T("Enter this code"), Theme.BodySize + 2, Theme.TextDim, 860);
            step2.Position = new Position(124, 202);
            card.Add(step2);
            var codeBox = Widgets.Rounded(Theme.Surface, 860, 150, 18);
            codeBox.Position = new Position(124, 252);
            card.Add(codeBox);
            codeLabel = Widgets.Label("", 84, Theme.Text, 860, 150);
            codeLabel.HorizontalAlignment = HorizontalAlignment.Center;
            codeLabel.VerticalAlignment = VerticalAlignment.Center;
            codeBox.Add(codeLabel);

            var line = Widgets.Box(Theme.PanelLine, CardW - 96, 2);
            line.Position = new Position(48, 446);
            card.Add(line);
            statusSpinner = Widgets.Spinner(40, Theme.TextDim, out statusSpin);
            statusSpinner.Position = new Position(68, 500);
            card.Add(statusSpinner);
            statusLabel = Widgets.Label("", Theme.BodySize + 2, Theme.Text, 860, 44);
            statusLabel.Position = new Position(124, 478);
            card.Add(statusLabel);
            expiryLabel = Widgets.Label("", Theme.SmallSize, Theme.TextDim, 860, 34);
            expiryLabel.Position = new Position(124, 524);
            card.Add(expiryLabel);
            retryButton = new Widgets.PillButton(T("New code"), "restart", 340);
            retryButton.Position = new Position(CardW - 48 - 340, 530);
            retryButton.Hide();
            card.Add(retryButton);

            var qrCard = Widgets.Rounded(Theme.Panel, SideW, CardH, Theme.PanelRadius + 4);
            qrCard.Position = new Position(SideX, CardY);
            signInView.Add(qrCard);
            qrHost = Widgets.Rounded(Color.White, QrSize, QrSize, 18);
            qrHost.Position = new Position((SideW - QrSize) / 2, 40);
            qrHost.Hide();
            qrCard.Add(qrHost);
            qrCaption = Widgets.Label(T("Or scan this QR code:\nthe code is already filled in"), Theme.BodySize, Theme.TextDim, SideW - 80, 90);
            qrCaption.MultiLine = true;
            qrCaption.HorizontalAlignment = HorizontalAlignment.Center;
            qrCaption.Position = new Position(40, 530);
            qrCard.Add(qrCaption);

            // ---- signed in: profile card, what the account brings, sign-out
            profileView = new View { Size = new Size(1920, 1080) };
            profileView.Hide();
            Root.Add(profileView);
            var profile = Widgets.Rounded(Theme.Panel, CardW, 290, Theme.PanelRadius + 4);
            profile.Position = new Position(CardX, CardY);
            profileView.Add(profile);
            var avatarBack = Widgets.Rounded(Theme.Accent, 190, 190, 95);
            avatarBack.Position = new Position(50, 50);
            profile.Add(avatarBack);
            var person = Widgets.Icon("person", 120);
            person.Position = new Position(35, 35);
            avatarBack.Add(person);
            avatar = new ImageView { Size = new Size(190, 190), CornerRadius = 95, Position = new Position(50, 50) };
            profile.Add(avatar);
            nameLabel = Widgets.Label("", Theme.TitleSize + 8, Theme.Text, 740, 64);
            nameLabel.Position = new Position(280, 62);
            profile.Add(nameLabel);
            handleLabel = Widgets.Label("", Theme.BodySize + 2, Theme.TextDim, 740, 40);
            handleLabel.Position = new Position(280, 130);
            profile.Add(handleLabel);
            var badge = Widgets.Label("  ●  " + T("Signed in") + "  ", Theme.SmallSize + 1, Theme.Text, 0, 0);
            badge.BackgroundColor = Theme.Success;
            badge.CornerRadius = 18;
            badge.Position = new Position(280, 190);
            profile.Add(badge);

            var features = Widgets.Rounded(Theme.Panel, CardW, 330, Theme.PanelRadius + 4);
            features.Position = new Position(CardX, CardY + 310);
            profileView.Add(features);
            Feature(features, 0, "home", T("Personalised home"), T("The recommendations of your YouTube account"));
            Feature(features, 1, "subscriptions", T("Subscriptions"), T("The latest videos of your channels"));
            Feature(features, 2, "history", T("Synced history"), T("What you watch here resumes wherever you left off"));

            signOutButton = new Widgets.PillButton(T("Sign out"), "logout", SideW, 84);
            signOutButton.Position = new Position(SideX, CardY);
            profileView.Add(signOutButton);
            var note = Widgets.Label(T("Your settings and menu sections stay on this TV."), Theme.SmallSize + 1, Theme.TextDim, SideW, 80);
            note.MultiLine = true;
            note.Position = new Position(SideX + 10, CardY + 110);
            profileView.Add(note);
        }

        private static View StepBadge(string n, int x, int y)
        {
            var b = Widgets.Rounded(Theme.Accent, 52, 52, 26);
            b.Position = new Position(x, y);
            var l = Widgets.Label(n, Theme.BodySize + 2, Theme.Text, 52, 52);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.VerticalAlignment = VerticalAlignment.Center;
            b.Add(l);
            return b;
        }

        private static void Feature(View card, int i, string icon, string title, string text)
        {
            int y = 34 + i * 96;
            var img = Widgets.Icon(icon, 48);
            img.Position = new Position(56, y + 12);
            card.Add(img);
            var t = Widgets.Label(title, Theme.BodySize + 4, Theme.Text, 860, 40);
            t.Position = new Position(140, y);
            card.Add(t);
            var d = Widgets.Label(text, Theme.SmallSize + 1, Theme.TextDim, 860, 34);
            d.Position = new Position(140, y + 42);
            card.Add(d);
        }

        public override void OnShow()
        {
            closed = false;
            if (YouTube.SignedIn())
            {
                ShowProfile();
            }
            else
            {
                _ = SignIn();
            }
        }

        // ---------------------------------------------------------------- signed in

        private void ShowProfile()
        {
            generation++;
            statusSpin.Stop();
            signInView.Hide();
            profileView.Show();
            confirmSignOut = false;
            signOutButton.Text = T("Sign out");
            signOutButton.SetFocused(true);
            help.Text = T("OK sign out    Back leave");
            if (!profileLoaded)
            {
                nameLabel.Text = T("YouTube account");   // until the profile arrives (kept for later openings)
                handleLabel.Text = "";
            }
            _ = LoadProfile(generation);
        }

        private bool profileLoaded;

        private async Task LoadProfile(int gen)
        {
            AccountInfo info = await YouTube.Account();
            if (closed || gen != generation || info.Error != null)
            {
                return;
            }
            profileLoaded = true;
            if (!string.IsNullOrEmpty(info.Name))
            {
                nameLabel.Text = info.Name;
            }
            handleLabel.Text = info.Handle ?? "";
            if (!string.IsNullOrEmpty(info.Photo))
            {
                avatar.ResourceUrl = info.Photo;
            }
        }

        // ---------------------------------------------------------------- signed out

        private void ShowSignInStatus(string status, bool waiting, bool retry, string retryText = null)
        {
            statusLabel.Text = status;
            Widgets.SetVisible(statusSpinner, waiting);
            if (waiting)
            {
                statusSpin.Play();
            }
            else
            {
                statusSpin.Stop();
            }
            retryText = retryText ?? T("New code");
            retryButton.Text = retryText;
            Widgets.SetVisible(retryButton, retry);
            retryButton.SetFocused(retry);
            help.Text = retry ? T("OK {0}    Back leave", retryText.ToLowerInvariant()) : T("Back leave");
        }

        private async Task SignIn()
        {
            int gen = ++generation;
            profileView.Hide();
            signInView.Show();
            urlLabel.Text = "";
            codeLabel.Text = "";
            expiryLabel.Text = "";
            qrHost.Hide();
            ShowSignInStatus(T("Preparing sign-in…"), true, false);
            object res = await YouTube.BeginSignIn();
            if (closed || gen != generation)
            {
                return;
            }
            string error = Json.Str(res, "error", null);
            if (error != null)
            {
                ShowSignInStatus(T("Could not start the sign-in: {0}", error), false, true, T("Retry"));
                return;
            }
            string deviceCode = Json.Str(res, "deviceCode");
            urlLabel.Text = Json.Str(res, "verificationUrl").Replace("https://", "").Replace("http://", "");
            codeLabel.Text = SpacedCode(Json.Str(res, "userCode"));
            DrawQr(Json.Str(res, "qrUrl"));
            ShowSignInStatus(T("Waiting for confirmation on your other device…"), true, false);

            int interval = Math.Max(2, (int)Json.Num(res, "interval", 5));
            DateTime expires = DateTime.Now.AddSeconds(Json.Num(res, "expiresIn", 1800));
            while (!closed && gen == generation && DateTime.Now < expires)
            {
                int minutes = (int)Math.Ceiling((expires - DateTime.Now).TotalMinutes);
                expiryLabel.Text = minutes > 1 ? T("Code valid for {0} more minutes", minutes) : T("The code expires in less than a minute");
                await Task.Delay(interval * 1000);
                if (closed || gen != generation)
                {
                    return;
                }
                string result = await YouTube.PollSignIn(deviceCode);
                if (closed || gen != generation)
                {
                    return;
                }
                switch (result)
                {
                    case "pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "done":
                        App.Home.InvalidateRows();
                        ShowProfile();
                        return;
                    case "denied":
                        ShowSignInStatus(T("Sign-in refused on the other device."), false, true);
                        break;
                    default:
                        ShowSignInStatus(T("Sign-in failed ({0}).", result), false, true);
                        break;
                }
                expiryLabel.Text = "";
                return;
            }
            if (!closed && gen == generation)
            {
                expiryLabel.Text = "";
                ShowSignInStatus(T("The code has expired."), false, true);
            }
        }

        // "ABCD-EFGH" -> "A B C D – E F G H": easier to read from the sofa.
        private static string SpacedCode(string code)
        {
            var parts = new List<string>();
            foreach (char c in code)
            {
                parts.Add(c == '-' ? "–" : c.ToString());
            }
            return string.Join(" ", parts);
        }

        // QR modules drawn as horizontal runs of black views on the white panel (few hundred views).
        private void DrawQr(string text)
        {
            foreach (View child in new List<View>(qrHost.Children))
            {
                qrHost.Remove(child);
                child.Dispose();
            }
            List<string> qrRows = YouTube.Qr(text);
            if (qrRows.Count == 0)
            {
                qrHost.Hide();
                return;
            }
            int quiet = 3;
            int module = QrSize / (qrRows.Count + 2 * quiet);
            int offset = (QrSize - module * qrRows.Count) / 2;
            for (int y = 0; y < qrRows.Count; y++)
            {
                string row = qrRows[y];
                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x] != '1')
                    {
                        continue;
                    }
                    int run = 1;
                    while (x + run < row.Length && row[x + run] == '1')
                    {
                        run++;
                    }
                    var dark = Widgets.Box(Color.Black, run * module, module);
                    dark.Position = new Position(offset + x * module, offset + y * module);
                    qrHost.Add(dark);
                    x += run - 1;
                }
            }
            qrHost.Show();
        }

        public override bool OnKey(string key)
        {
            if (key == "XF86Back" || key == "Escape")
            {
                if (confirmSignOut)
                {
                    confirmSignOut = false;
                    signOutButton.Text = T("Sign out");
                    help.Text = T("OK sign out    Back leave");
                    return true;
                }
                return false;
            }
            if (key == "Left")
            {
                return false;   // back to the home rail
            }
            if (key != "Return")
            {
                return true;
            }
            if (profileView.Visibility)
            {
                if (!confirmSignOut)
                {
                    confirmSignOut = true;
                    signOutButton.Text = T("Confirm?");
                    help.Text = T("OK confirm sign-out    Back cancel");
                    return true;
                }
                YouTube.SignOut();
                App.Home.InvalidateRows();
                avatar.ResourceUrl = "";
                _ = SignIn();
            }
            else if (retryButton.Visibility)
            {
                _ = SignIn();
            }
            return true;
        }

        public override void OnHide()
        {
            closed = true;
            statusSpin.Stop();
        }
    }
}
