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
        private const int CardW = 760, CardH = 330, DetailCardW = 900, DetailCardH = 470, ButtonW = 240;

        private readonly Widgets.PillButton yes, no;
        private readonly Action onYes, onNo;
        private bool onYesButton;

        public event Action Closed;

        // detail: smaller text under the question (a taller card). yesLabel / noLabel replace "Yes" / "No". onNo: run
        // when the dialog closes without a yes (No, Back).
        public ConfirmDialog(string question, Action onYes, string detail = null, string yesLabel = null,
                             string noLabel = null, Action onNo = null)
        {
            this.onYes = onYes;
            this.onNo = onNo;
            int cardW = detail == null ? CardW : DetailCardW, cardH = detail == null ? CardH : DetailCardH;
            Size = new Size(1920, 1080);
            BackgroundColor = Theme.Scrim;

            var card = Widgets.Panel(Theme.Panel, cardW, cardH, Theme.PanelRadius);
            card.Position = new Position((1920 - cardW) / 2, (1080 - cardH) / 2);
            Add(card);
            var label = Widgets.Label(question, Theme.TitleSize - 2, Theme.Text, cardW - 100, detail == null ? 120 : 90);
            label.MultiLine = true;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Position = new Position(50, 50);
            card.Add(label);
            if (detail != null)
            {
                var text = Widgets.Label(detail, Theme.BodySize, Theme.TextDim, cardW - 140, 150);
                text.MultiLine = true;
                text.HorizontalAlignment = HorizontalAlignment.Center;
                text.Position = new Position(70, 150);
                card.Add(text);
            }

            yes = new Widgets.PillButton(yesLabel ?? T("Yes"), null, ButtonW);
            yes.Position = new Position(cardW / 2 - ButtonW - 20, cardH - 76 - 50);
            card.Add(yes);
            no = new Widgets.PillButton(noLabel ?? T("No"), null, ButtonW);
            no.Position = new Position(cardW / 2 + 20, cardH - 76 - 50);
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
                    else
                    {
                        onNo?.Invoke();
                    }
                    break;
                case "XF86Back":
                case "Escape":
                    Close();
                    onNo?.Invoke();
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
