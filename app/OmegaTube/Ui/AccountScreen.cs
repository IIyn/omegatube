using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using OmegaTube.Util;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Google sign-in (device flow): code + QR code, polled until the user confirms on another device.
    public class AccountScreen : Screen
    {
        private const int QrPanel = 520;

        private readonly TextLabel title, info, code, status, button;
        private readonly View qrHost;
        private bool closed;
        private int generation;

        public AccountScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;
            title = Widgets.Label("Compte Google", Theme.TitleSize + 8, Theme.Text, 1200);
            title.Position = new Position(120, 80);
            Root.Add(title);

            info = Widgets.Label("", Theme.BodySize + 2, Theme.TextDim, 1000, 200);
            info.MultiLine = true;
            info.Position = new Position(120, 200);
            Root.Add(info);

            code = Widgets.Label("", 96, Theme.Text, 1000, 140);
            code.Position = new Position(120, 420);
            Root.Add(code);

            status = Widgets.Label("", Theme.BodySize, Theme.TextDim, 1000, 100);
            status.MultiLine = true;
            status.Position = new Position(120, 600);
            Root.Add(status);

            button = Widgets.Label("", Theme.BodySize + 2, Theme.Text, 0, 0);
            button.BackgroundColor = Theme.Accent;
            button.Position = new Position(120, 760);
            button.Hide();
            Root.Add(button);

            qrHost = new View { Position = new Position(1240, 220), Size = new Size(QrPanel, QrPanel), BackgroundColor = Color.White };
            qrHost.Hide();
            Root.Add(qrHost);
        }

        public override void OnShow()
        {
            if (YouTube.SignedIn())
            {
                ShowSignedIn();
            }
            else
            {
                _ = SignIn();
            }
        }

        private void ShowSignedIn()
        {
            qrHost.Hide();
            code.Text = "";
            info.Text = "Ton compte est connecté : recommandations, abonnements et historique sont disponibles sur l'accueil.";
            status.Text = "";
            button.Text = "  Se déconnecter  ";
            button.Show();
        }

        private async Task SignIn()
        {
            int gen = ++generation;
            button.Hide();
            info.Text = "Connexion en cours…";
            object res = await YouTube.BeginSignIn();
            if (closed || gen != generation)
            {
                return;
            }
            string error = Json.Str(res, "error", null);
            if (error != null)
            {
                info.Text = "Impossible de démarrer la connexion : " + error;
                button.Text = "  Réessayer  ";
                button.Show();
                return;
            }
            string deviceCode = Json.Str(res, "deviceCode");
            info.Text = "Sur ton téléphone ou ton ordinateur, va sur\n" + Json.Str(res, "verificationUrl") +
                        "\net saisis ce code, ou scanne le QR code :";
            code.Text = Json.Str(res, "userCode");
            DrawQr(Json.Str(res, "qrUrl"));
            status.Text = "En attente de validation…";

            int interval = Math.Max(2, (int)Json.Num(res, "interval", 5));
            DateTime expires = DateTime.Now.AddSeconds(Json.Num(res, "expiresIn", 1800));
            while (!closed && gen == generation && DateTime.Now < expires)
            {
                await Task.Delay(interval * 1000);
                if (closed || gen != generation)
                {
                    return;
                }
                string result = await YouTube.PollSignIn(deviceCode);
                switch (result)
                {
                    case "pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "done":
                        App.Home.InvalidateRows();
                        ShowSignedIn();
                        status.Text = "Connecté !";
                        return;
                    case "denied":
                        status.Text = "Connexion refusée.";
                        break;
                    default:
                        status.Text = "La connexion a échoué (" + result + ").";
                        break;
                }
                break;
            }
            if (!closed && gen == generation && !YouTube.SignedIn())
            {
                if (DateTime.Now >= expires)
                {
                    status.Text = "Le code a expiré.";
                }
                button.Text = "  Nouveau code  ";
                button.Show();
            }
        }

        // QR modules drawn as horizontal runs of black views on the white panel (few hundred views).
        private void DrawQr(string text)
        {
            foreach (View child in new List<View>(qrHost.Children))
            {
                qrHost.Remove(child);
                child.Dispose();
            }
            List<string> rows = YouTube.Qr(text);
            if (rows.Count == 0)
            {
                qrHost.Hide();
                return;
            }
            int quiet = 4;
            int module = QrPanel / (rows.Count + 2 * quiet);
            int offset = (QrPanel - module * rows.Count) / 2;
            for (int y = 0; y < rows.Count; y++)
            {
                string row = rows[y];
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
                return false;
            }
            if (key == "Return" && button.Visibility)
            {
                if (YouTube.SignedIn())
                {
                    YouTube.SignOut();
                    App.Home.InvalidateRows();
                    info.Text = "Déconnecté.";
                    button.Text = "  Se connecter  ";
                }
                else
                {
                    _ = SignIn();
                }
            }
            return true;
        }

        public override void OnHide()
        {
            closed = true;
        }
    }
}
