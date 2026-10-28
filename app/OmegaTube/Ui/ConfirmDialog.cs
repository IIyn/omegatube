using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Modal yes / no question over the current screen (scrim + centred card). App routes the keys to it while
    // it is open; "No" has the focus first so a second Back press never confirms by accident.
    public class ConfirmDialog : View
    {
        private const int CardW = 760, CardH = 330, ButtonW = 240;

        private readonly Widgets.PillButton yes, no;
        private readonly Action onYes;
        private bool onYesButton;

        public event Action Closed;

        public ConfirmDialog(string question, Action onYes)
        {
            this.onYes = onYes;
            Size = new Size(1920, 1080);
            BackgroundColor = Theme.Scrim;

            var card = Widgets.Rounded(Theme.Panel, CardW, CardH, Theme.PanelRadius);
            card.Position = new Position((1920 - CardW) / 2, (1080 - CardH) / 2);
            Add(card);
            var label = Widgets.Label(question, Theme.TitleSize - 2, Theme.Text, CardW - 100, 120);
            label.MultiLine = true;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Position = new Position(50, 50);
            card.Add(label);

            yes = new Widgets.PillButton(T("Yes"), null, ButtonW);
            yes.Position = new Position(CardW / 2 - ButtonW - 20, CardH - 76 - 50);
            card.Add(yes);
            no = new Widgets.PillButton(T("No"), null, ButtonW);
            no.Position = new Position(CardW / 2 + 20, CardH - 76 - 50);
            card.Add(no);
            Refresh();
        }

        private void Refresh()
        {
            yes.SetFocused(onYesButton);
            no.SetFocused(!onYesButton);
        }

        // Every key is consumed while the dialog is open.
        public bool OnKey(string key)
        {
            switch (key)
            {
                case "Left":
                    onYesButton = true;
                    Refresh();
                    break;
                case "Right":
                    onYesButton = false;
                    Refresh();
                    break;
                case "Return":
                    Close();
                    if (onYesButton)
                    {
                        onYes();
                    }
                    break;
                case "XF86Back":
                case "Escape":
                    Close();
                    break;
            }
            return true;
        }

        private void Close()
        {
            Unparent();
            Closed?.Invoke();
            Dispose();
        }
    }
}
