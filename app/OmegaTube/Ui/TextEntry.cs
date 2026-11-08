using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // Full-screen text input over the current screen (the player keeps playing under it): the search screen's
    // keyboard layouts, a growing text box, and Cancel / Send keys. The owner forwards keys to Key() and
    // removes the view when Done is called.
    public class TextEntry : View
    {
        private enum KeyAction { Page, Space, Delete, Cancel, Send }
        private const int CharRows = 4, ActionRow = 4, ActionKeys = 5;
        private const int KeyW = 104, KeyH = 76, KeyGap = 10;
        private const int PanelW = 1240, BoxW = PanelW - 120, BoxLines = 5;
        private const float BoxSize = Theme.BodySize + 4;

        // (text) on Send, null on Cancel / Back.
        public Action<string> Done;

        private readonly TextLabel box;
        private readonly TextLabel counter;
        private readonly List<List<TextLabel>> keys = new List<List<TextLabel>>();
        private readonly string placeholder;
        private readonly int maxLength;
        private string text;
        private int row = 1, col;
        private bool symbols;
        private bool busy;

        private string[][] KeyLayout => symbols ? SearchScreen.Symbols
                                   : Lang == "fr" ? SearchScreen.Azerty : Lang == "es" ? SearchScreen.QwertySpanish : SearchScreen.Qwerty;

        public string Text => text;

        public TextEntry(string title, string placeholder, string initial, int maxLength)
        {
            this.placeholder = placeholder;
            this.maxLength = maxLength;
            text = initial ?? "";
            Size = new Size(1920, 1080);
            BackgroundColor = new Color(0, 0, 0, 0.6f);

            int panelH = 110 + (int)(BoxLines * BoxSize * 1.35f) + 70 + 5 * (KeyH + KeyGap) + 40;
            var panel = Widgets.Rounded(new Color(0.09f, 0.09f, 0.09f, 0.98f), PanelW, panelH, Theme.PanelRadius);
            panel.Position = new Position((1920 - PanelW) / 2, (1080 - panelH) / 2);
            Add(panel);

            var heading = Widgets.Label(title, Theme.TitleSize, Theme.Text, PanelW - 120);
            heading.Position = new Position(60, 36);
            Widgets.Bold(heading);
            panel.Add(heading);

            int boxH = (int)(BoxLines * BoxSize * 1.35f) + 20;
            var frame = Widgets.Rounded(Theme.Surface, BoxW, boxH, 12);
            frame.Position = new Position(60, 100);
            panel.Add(frame);
            box = Widgets.Label("", BoxSize, Theme.Text, BoxW - 40, boxH - 20);
            box.MultiLine = true;
            box.Ellipsis = false;
            box.VerticalAlignment = VerticalAlignment.Top;
            box.Position = new Position(20, 10);
            frame.Add(box);
            counter = Widgets.Label("", Theme.SmallSize, Theme.TextDim, 300);
            counter.HorizontalAlignment = HorizontalAlignment.End;
            counter.Position = new Position(60 + BoxW - 300, 100 + boxH + 6);
            panel.Add(counter);

            int kbX = (PanelW - (10 * (KeyW + KeyGap) - KeyGap)) / 2;
            int kbY = 100 + boxH + 50;
            for (int r = 0; r <= ActionRow; r++)
            {
                var line = new List<TextLabel>();
                int x = kbX;
                int count = r == ActionRow ? ActionKeys : 10;
                for (int c = 0; c < count; c++)
                {
                    int w = r == ActionRow ? (10 * (KeyW + KeyGap) - KeyGap - (ActionKeys - 1) * KeyGap) / ActionKeys : KeyW;
                    var key = Widgets.Label("", r == ActionRow ? Theme.BodySize : Theme.BodySize + 4, Theme.Text, w, KeyH);
                    key.HorizontalAlignment = HorizontalAlignment.Center;
                    key.VerticalAlignment = VerticalAlignment.Center;
                    Widgets.Round(key, 10);
                    key.Position = new Position(x, kbY + r * (KeyH + KeyGap));
                    panel.Add(key);
                    line.Add(key);
                    x += w + KeyGap;
                }
                keys.Add(line);
            }
            LabelKeys();
            Refresh();
        }

        // While sending: keys ignored, the Send key says so.
        public void SetBusy(bool value)
        {
            busy = value;
            keys[ActionRow][(int)KeyAction.Send].Text = value ? T("Sending…") : T("Send");
        }

        private void LabelKeys()
        {
            string[][] layout = KeyLayout;
            for (int r = 0; r < CharRows; r++)
            {
                for (int c = 0; c < 10; c++)
                {
                    keys[r][c].Text = layout[r][c];
                }
            }
            List<TextLabel> actions = keys[ActionRow];
            actions[(int)KeyAction.Page].Text = symbols ? "abc" : "àç?#";
            actions[(int)KeyAction.Space].Text = T("Space");
            actions[(int)KeyAction.Delete].Text = "← " + T("Delete");
            actions[(int)KeyAction.Cancel].Text = T("Cancel");
            actions[(int)KeyAction.Send].Text = busy ? T("Sending…") : T("Send");
        }

        private void Refresh()
        {
            int perLine = Math.Max(10, (int)((BoxW - 40) / (BoxSize * 0.55f)));
            if (text.Length == 0)
            {
                box.Text = placeholder;
                box.TextColor = Theme.TextDim;
            }
            else
            {
                // Last lines only, so the cursor stays in view.
                string[] lines = Widgets.Wrap(text, perLine).Split('\n');
                int from = Math.Max(0, lines.Length - BoxLines);
                box.Text = string.Join("\n", lines, from, lines.Length - from) + "_";
                box.TextColor = Theme.Text;
            }
            counter.Text = text.Length + " / " + maxLength;
            for (int r = 0; r < keys.Count; r++)
            {
                for (int c = 0; c < keys[r].Count; c++)
                {
                    bool f = r == row && c == col;
                    bool send = r == ActionRow && c == (int)KeyAction.Send;
                    keys[r][c].BackgroundColor = f ? Theme.Accent : send ? Theme.SurfaceHigh : Theme.Surface;
                }
            }
        }

        public void Key(string key)
        {
            if (busy)
            {
                return;
            }
            switch (key)
            {
                case "Up":
                    if (row > 0)
                    {
                        col = row == ActionRow ? Math.Min(9, col * 10 / ActionKeys) : col;
                        row--;
                    }
                    break;
                case "Down":
                    if (row < ActionRow)
                    {
                        row++;
                        col = row == ActionRow ? Math.Min(ActionKeys - 1, col * ActionKeys / 10) : col;
                    }
                    break;
                case "Left":
                    col = Math.Max(0, col - 1);
                    break;
                case "Right":
                    col = Math.Min(keys[row].Count - 1, col + 1);
                    break;
                case "Return":
                    if (row == ActionRow)
                    {
                        Press((KeyAction)col);
                        return;
                    }
                    Type(KeyLayout[row][col]);
                    break;
                case "XF86Back":
                case "Escape":
                    Done?.Invoke(null);
                    return;
                default:
                    // Number keys of the remote.
                    if (key.Length == 1 && char.IsDigit(key[0]))
                    {
                        Type(key);
                    }
                    break;
            }
            Refresh();
        }

        private void Type(string s)
        {
            if (text.Length + s.Length <= maxLength)
            {
                text += s;
            }
        }

        private void Press(KeyAction action)
        {
            switch (action)
            {
                case KeyAction.Page:
                    symbols = !symbols;
                    LabelKeys();
                    break;
                case KeyAction.Space:
                    if (text.Length > 0 && !text.EndsWith(" "))
                    {
                        Type(" ");
                    }
                    break;
                case KeyAction.Delete:
                    if (text.Length > 0)
                    {
                        text = text.Substring(0, text.Length - 1);
                    }
                    break;
                case KeyAction.Cancel:
                    Done?.Invoke(null);
                    return;
                case KeyAction.Send:
                    if (text.Trim().Length > 0)
                    {
                        Done?.Invoke(text.Trim());
                        return;
                    }
                    break;
            }
            Refresh();
        }
    }
}
