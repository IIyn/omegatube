using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Player option list (subtitles, audio, speed, quality): a title, one line per choice with a check mark on
    // the current one, the focused line lit. Long lists scroll with the focus.
    public class ListPanel : View
    {
        public class Item
        {
            public string Text, Detail;
            public bool Checked;
        }

        private const int LineH = 66, Top = 84, MaxVisible = 8, Pad = 20;

        private readonly TextLabel title;
        private readonly View clip, lines;
        private readonly List<View> rows = new List<View>();
        private List<Item> items = new List<Item>();
        private Animation scroll;

        public int Index { get; private set; }

        public ListPanel()
        {
            BackgroundColor = new Color(0.09f, 0.09f, 0.09f, 0.96f);
            CornerRadius = Theme.PanelRadius;
            title = Widgets.Label("", Theme.BodySize + 2, Theme.Text, 600, 60);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Position = new Position(32, 14);
            Add(title);
            clip = new View { Position = new Position(0, Top), ClippingMode = ClippingModeType.ClipChildren };
            Add(clip);
            lines = new View();
            clip.Add(lines);
            Hide();
        }

        // Shows the list with its bottom-right corner at (right, bottom).
        public void Open(string heading, List<Item> list, int right, int bottom, int width = 560)
        {
            foreach (View r in rows)
            {
                lines.Remove(r);
                r.Dispose();
            }
            rows.Clear();
            items = list;
            title.Text = heading;
            title.SizeWidth = width - 64;
            Index = Math.Max(0, list.FindIndex(i => i.Checked));
            for (int i = 0; i < list.Count; i++)
            {
                var row = Widgets.Rounded(Color.Transparent, width - 2 * Pad, LineH - 6, 14);
                row.Position = new Position(Pad, i * LineH);
                var check = Widgets.Icon("check", 34);
                check.Position = new Position(18, (LineH - 6 - 34) / 2);
                Widgets.SetVisible(check, list[i].Checked);
                row.Add(check);
                bool detail = !string.IsNullOrEmpty(list[i].Detail);
                var text = Widgets.Label(list[i].Text, Theme.BodySize + 2, Theme.Text, width - 2 * Pad - 90, detail ? 36 : LineH - 6);
                text.VerticalAlignment = detail ? VerticalAlignment.Bottom : VerticalAlignment.Center;
                text.Position = new Position(68, detail ? 2 : 0);
                row.Add(text);
                if (detail)
                {
                    var d = Widgets.Label(list[i].Detail, Theme.SmallSize - 1, Theme.TextDim, width - 2 * Pad - 90, 24);
                    d.Position = new Position(68, 36);
                    row.Add(d);
                }
                lines.Add(row);
                rows.Add(row);
            }
            int visible = Math.Min(MaxVisible, list.Count);
            Size = new Size(width, Top + visible * LineH + 14);
            clip.Size = new Size(width, visible * LineH);
            Position = new Position(right - width, bottom - SizeHeight);
            lines.PositionY = 0;
            Show();
            Refresh(false);
        }

        public void Move(int delta)
        {
            Index = Math.Max(0, Math.Min(items.Count - 1, Index + delta));
            Refresh(true);
        }

        private void Refresh(bool animate)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool f = i == Index;
                rows[i].BackgroundColor = f ? Theme.CardInfoFocused : Color.Transparent;
                var check = (ImageView)rows[i].GetChildAt(0);
                check.Color = f ? new Color(0.16f, 0.16f, 0.16f, 1f) : Color.White;
                ((TextLabel)rows[i].GetChildAt(1)).TextColor = f ? Theme.CardTextFocused : Theme.Text;
                if (rows[i].ChildCount > 2)
                {
                    ((TextLabel)rows[i].GetChildAt(2)).TextColor = f ? new Color(0.33f, 0.33f, 0.33f, 1f) : Theme.TextDim;
                }
            }
            // Keep the focused line inside the window, one line of context when possible.
            int first = Math.Max(0, Math.Min(Index - MaxVisible + 2, items.Count - MaxVisible));
            float y = -first * LineH;
            if (animate)
            {
                scroll = Widgets.Slide(lines, 0, y, scroll, 140);
            }
            else
            {
                lines.PositionY = y;
            }
        }
    }
}
