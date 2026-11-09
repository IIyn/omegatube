using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Navigation rail. Two styles:
    //  - Omega (default): a panel that shows icons only while the content has focus, icons + labels while the
    //    rail has focus.
    //  - Glorious (overlay): a thin column of outline icons floating over the hero artwork, with a round profile
    //    avatar on top. Idle (content focused) it is dimmed with a thick accent underline marking the current
    //    section; focused it slides out a frosted "liquid glass" panel with the labels and a glass pill on the
    //    focused entry. No opaque drawer.
    public class Rail : View
    {
        public struct Entry
        {
            public string Icon, Label;
        }

        private const int ItemHeight = 84;
        private const int Top = 150;
        private const int VisibleItems = 11;       // beyond this the list scrolls with the focus
        private const int IconX = 40;              // icon left edge inside the rail (clear of the screen edge)
        private const int IconSize = 40;
        private const float IdleOpacity = 0.25f;   // overlay, content focused: the whole rail fades back

        private readonly bool overlay;
        private readonly List<View> items = new List<View>();
        private readonly List<ImageView> icons = new List<ImageView>();
        private readonly List<TextLabel> labels = new List<TextLabel>();
        private readonly List<View> underlines = new List<View>();
        private readonly View panel;
        private readonly View list;
        private View glassPill;      // overlay only: frosted highlight behind the focused entry
        private ImageView avatar;    // active account photo, top-left; focusable to switch profile
        private View avatarRing;     // focus highlight around the avatar
        private Animation scroll;
        private int index;
        private bool focused;
        private Animation widen;

        public int Index => index;
        public int Count => items.Count;

        public Rail(IList<Entry> entries, bool overlay = false)
        {
            this.overlay = overlay;
            Size = new Size(overlay ? Theme.RailWidth : Theme.RailExpandedWidth, 1080);

            if (overlay)
            {
                // Frosted panel, shown (widened) only while the rail is focused.
                panel = Widgets.Glass(Theme.GlassPanel, Theme.RailWidth, 1080, 0);
                panel.Opacity = 0f;
                Add(panel);
                // The pill that lifts the focused entry; parked until the rail is focused.
                glassPill = Widgets.Glass(Theme.GlassHigh, Theme.RailExpandedWidth - 24, ItemHeight - 14, Theme.GlassRadius);
                glassPill.Hide();
                Add(glassPill);

                avatarRing = new ImageView { ResourceUrl = Theme.Image("circle_mask"), Color = Theme.Accent,
                                             Size = new Size(78, 78), Position = new Position(IconX - 11, 42) };
                avatarRing.Hide();
                Add(avatarRing);
                avatar = new ImageView { Size = new Size(60, 60), Position = new Position(IconX - 2, 51) };
                Add(avatar);
                Widgets.MaskedImage(avatar, Theme.Icon("person"), Theme.Image("circle_mask"));
            }
            else
            {
                panel = new View { Size = new Size(Theme.RailWidth, 1080), BackgroundColor = Theme.Background };
                Add(panel);
                // Omega: the top-left profile avatar (falls back to a person icon when signed out / no photo).
                // Focusable: the home screen moves focus here from the top rail entry, and OK cycles accounts.
                avatarRing = new ImageView { ResourceUrl = Theme.Image("circle_mask"), Color = Theme.Accent,
                                             Size = new Size(76, 76), Position = new Position(17, 38) };
                avatarRing.Hide();
                Add(avatarRing);
                avatar = new ImageView { Size = new Size(64, 64), Position = new Position(23, 44) };
                Add(avatar);
                Widgets.MaskedImage(avatar, Theme.Icon("person"), Theme.Image("circle_mask"));
            }

            var clip = new View { Position = new Position(0, Top), Size = new Size(Theme.RailExpandedWidth, 1080 - Top),
                                  ClippingMode = ClippingModeType.ClipChildren };
            Add(clip);
            list = new View { Size = new Size(Theme.RailExpandedWidth, 2000) };
            clip.Add(list);

            for (int i = 0; i < entries.Count; i++)
            {
                var item = new View
                {
                    Position = new Position(overlay ? 0 : 14, i * ItemHeight),
                    Size = new Size(overlay ? Theme.RailExpandedWidth : Theme.RailWidth - 28, ItemHeight - 12),
                };
                if (!overlay)
                {
                    Widgets.Round(item, (ItemHeight - 12) / 2);
                }
                // Overlay uses the outline variant when it exists, else falls back to the filled icon.
                string iconName = entries[i].Icon;
                if (overlay && System.IO.File.Exists(Theme.Icon(iconName + "_outline")))
                {
                    iconName += "_outline";
                }
                var icon = new ImageView { ResourceUrl = Theme.Icon(iconName), Size = new Size(IconSize, IconSize),
                                           Position = new Position(overlay ? IconX : 21, (ItemHeight - 12 - IconSize) / 2) };
                item.Add(icon);
                icons.Add(icon);
                if (overlay)
                {
                    // Thick accent underline the width of the icon, marking the current section.
                    var underline = Widgets.Rounded(Theme.Accent, IconSize, 6, 3);
                    underline.Position = new Position(IconX, (ItemHeight - 12 + IconSize) / 2 + 2);
                    underline.Hide();
                    item.Add(underline);
                    underlines.Add(underline);
                }
                var label = Widgets.Label(entries[i].Label, Theme.BodySize + 2, Theme.Text, 220, ItemHeight - 12);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Position = new Position(overlay ? IconX + IconSize + 22 : 80, 0);
                label.Hide();
                item.Add(label);
                list.Add(item);
                items.Add(item);
                labels.Add(label);
            }
            Refresh();
        }

        // The active account's avatar (empty -> default person icon).
        public void SetAvatar(string photo)
        {
            if (avatar != null)
            {
                string url = string.IsNullOrEmpty(photo) ? Theme.Icon("person") : photo;
                Widgets.MaskedImage(avatar, url, Theme.Image("circle_mask"));   // clip to a circle (no CornerRadius on 5.5)
            }
        }

        public void SetAvatarFocused(bool value)
        {
            if (avatarRing != null)
            {
                Widgets.SetVisible(avatarRing, value);
            }
        }

        public void SetFocused(bool value)
        {
            focused = value;
            if (overlay)
            {
                widen?.Stop();
                widen = new Animation(180);
                var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
                widen.AnimateTo(panel, "SizeWidth", (float)(value ? Theme.RailExpandedWidth : Theme.RailWidth), ease);
                widen.AnimateTo(panel, "Opacity", value ? 1f : 0f, ease);
                widen.Play();
                foreach (TextLabel l in labels)
                {
                    Widgets.SetVisible(l, value);
                }
                Refresh();
                return;
            }
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
                if (overlay)
                {
                    // Focused: every entry is legible, the current one lit. Idle: unselected icons recede to
                    // 25%, the current section stays bright with its thick accent underline.
                    items[i].Opacity = focused ? (current ? 1f : 0.7f) : (current ? 1f : IdleOpacity);
                    Widgets.SetVisible(underlines[i], current && !focused);
                }
                else
                {
                    items[i].BackgroundColor = focused && current ? Theme.Accent
                                             : current ? Theme.SurfaceHigh : Color.Transparent;
                }
            }
            if (overlay && glassPill != null)
            {
                if (focused)
                {
                    glassPill.Position = new Position(12, Top + (index - first) * ItemHeight + 1);
                    glassPill.Show();
                }
                else
                {
                    glassPill.Hide();
                }
            }
        }
    }
}
