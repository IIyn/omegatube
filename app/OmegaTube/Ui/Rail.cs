using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // SmartTube-style navigation rail: icons only while the content has focus, icons + labels
    // (drawn over the content) while the rail itself has focus.
    public class Rail : View
    {
        public struct Entry
        {
            public string Icon, Label;
        }

        private const int ItemHeight = 76;
        private const int Top = 140;
        private const int VisibleItems = 12;   // beyond this the list scrolls with the focus

        private readonly List<View> items = new List<View>();
        private readonly List<TextLabel> labels = new List<TextLabel>();
        private readonly View panel;
        private readonly View list;
        private Animation scroll;
        private int index;
        private bool focused;
        private Animation widen;

        public int Index => index;
        public int Count => items.Count;

        public Rail(IList<Entry> entries)
        {
            Size = new Size(Theme.RailExpandedWidth, 1080);
            panel = new View { Size = new Size(Theme.RailWidth, 1080), BackgroundColor = Theme.Background };
            Add(panel);

            var brand = new ImageView { ResourceUrl = Theme.Icon("omegatube"), Size = new Size(64, 64), Position = new Position(23, 44) };
            Add(brand);
            var clip = new View { Position = new Position(0, Top), Size = new Size(Theme.RailExpandedWidth, 1080 - Top),
                                  ClippingMode = ClippingModeType.ClipChildren };
            Add(clip);
            list = new View { Size = new Size(Theme.RailExpandedWidth, 2000) };
            clip.Add(list);

            for (int i = 0; i < entries.Count; i++)
            {
                var item = new View
                {
                    Position = new Position(14, i * ItemHeight),
                    Size = new Size(Theme.RailWidth - 28, ItemHeight - 12),
                    CornerRadius = (ItemHeight - 12) / 2,
                };
                var icon = new ImageView { ResourceUrl = Theme.Icon(entries[i].Icon), Size = new Size(40, 40), Position = new Position(21, 12) };
                item.Add(icon);
                var label = Widgets.Label(entries[i].Label, Theme.BodySize + 2, Theme.Text, 200, ItemHeight - 12);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Position = new Position(80, 0);
                label.Hide();
                item.Add(label);
                list.Add(item);
                items.Add(item);
                labels.Add(label);
            }
            Refresh();
        }

        public void SetFocused(bool value)
        {
            focused = value;
            widen?.Stop();
            widen = new Animation(160);
            float w = value ? Theme.RailExpandedWidth : Theme.RailWidth;
            widen.AnimateTo(panel, "SizeWidth", w, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            widen.Play();
            foreach (View item in items)
            {
                item.SizeWidth = (value ? Theme.RailExpandedWidth : Theme.RailWidth) - 28;
            }
            foreach (TextLabel l in labels)
            {
                Widgets.SetVisible(l, value);
            }
            panel.BackgroundColor = value ? Theme.Surface : Theme.Background;
            Refresh();
        }

        public void Move(int delta)
        {
            index = System.Math.Max(0, System.Math.Min(items.Count - 1, index + delta));
            Refresh();
        }

        public void Select(int i)
        {
            index = i;
            Refresh();
        }

        private void Refresh()
        {
            int first = System.Math.Max(0, index - VisibleItems + 2);
            scroll = Widgets.Slide(list, 0, -first * ItemHeight, scroll);
            for (int i = 0; i < items.Count; i++)
            {
                bool current = i == index;
                items[i].BackgroundColor = focused && current ? Theme.Accent
                                         : current ? Theme.SurfaceHigh : Color.Transparent;
            }
        }
    }
}
