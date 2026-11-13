using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Navigation rail. Two styles:
    //  - Omega (default): a panel that shows icons only while the content has focus, icons + labels while the
    //    rail has focus.
    //  - Glorious (overlay): while the content has the focus the nav is folded into one dark glass pill with the
    //    current section's icon and name; focused (or with the profile avatar focused) it opens into the column of
    //    outline icons with their labels over a dark fade, a glass pill on the focused entry and the round profile
    //    avatar on top. No opaque drawer.
    public class Rail : View
    {
        public struct Entry
        {
            public string Icon, Label;
        }

        private const int ItemHeight = 84;
        private const int Top = 150;
        private const int VisibleItems = 11;       // beyond this the list scrolls with the focus
        private const int IconX = 56;              // icon left edge inside the rail (breathing space from the screen edge)
        private const int IconSize = 40;
        private const float IdleOpacity = 0.25f;   // overlay: entries other than the current one, while the nav is not open
        private const int ChipHeight = 60, ChipTextX = 96, ChipPadRight = 28;   // the folded pill: icon + name of the current entry

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
        private View railClip;       // overlay: the entry list (hidden while the nav is folded)
        private View chip;           // overlay, folded: a single pill with the current section
        private View chipGlass;
        private ImageView chipIcon;
        private TextLabel chipLabel;
        private readonly List<string> entryLabels = new List<string>();
        private bool avatarOn;       // the profile avatar has the focus (the nav is open then too)
        private bool opened;         // overlay: the list is open (not folded into the pill)
        private int drawnIndex = -1, chipIndex = -1;   // what Refresh / UpdateChip last drew: skipped when unchanged
        private bool drawnFocused, drawnOpen;

        public int Index => index;
        public int Count => items.Count;

        public Rail(IList<Entry> entries, bool overlay = false)
        {
            this.overlay = overlay;
            Size = new Size(overlay ? Theme.RailWidth : Theme.RailExpandedWidth, 1080);

            if (overlay)
            {
                // Frosted panel, shown (widened) only while the rail is focused.
                // The whole nav sits NavShift in from the screen edge; its backdrop is a dark fade that starts at the
                // screen edge and eases out to the right (so labels stay legible without a visible panel edge).
                Position = new Position(Theme.NavShift, 0);
                panel = new ImageView { ResourceUrl = Theme.Image("nav_fade"), FittingMode = FittingModeType.ScaleToFill,
                                        Size = new Size(FadeWidth(false), 1080), Position = new Position(-Theme.NavShift, 0),
                                        Opacity = 0f };
                Add(panel);
                // The pill that lifts the focused entry; parked until the rail is focused.
                glassPill = Widgets.GlassCard(true, Theme.RailExpandedWidth - 24, ItemHeight - 14, true);
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
            railClip = clip;
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
                entryLabels.Add(entries[i].Label);
            }
            if (overlay)
            {
                // Folded pill (the nav's resting state): dark smoked glass so it reads on any artwork.
                chip = new View { Position = new Position(0, 38), Size = new Size(240, ChipHeight) };
                chipGlass = new ImageView { ResourceUrl = Theme.Image("glass_dark9.9"), Size = new Size(240, ChipHeight) };
                chip.Add(chipGlass);
                var chevron = Widgets.Label("‹", Theme.BodySize + 8, Theme.TextDim, 30, ChipHeight);
                chevron.VerticalAlignment = VerticalAlignment.Center;
                chevron.Position = new Position(18, -2);
                chip.Add(chevron);
                chipIcon = new ImageView { Size = new Size(34, 34), Position = new Position(52, (ChipHeight - 34) / 2) };
                chip.Add(chipIcon);
                chipLabel = Widgets.Bold(Widgets.Label("", Theme.BodySize + 2, Theme.Text, 0, ChipHeight));
                chipLabel.WidthResizePolicy = ResizePolicyType.UseNaturalSize;   // exactly as wide as the text
                chipLabel.HeightResizePolicy = ResizePolicyType.Fixed;
                chipLabel.SizeHeight = ChipHeight;
                chipLabel.VerticalAlignment = VerticalAlignment.Center;
                chipLabel.Position = new Position(ChipTextX, 0);
                chip.Add(chipLabel);
                Add(chip);
                // Starts folded: the list and avatar are faded out until the nav gets the focus.
                railClip.Opacity = 0f;
                avatar.Opacity = 0f;
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
            if (avatarOn == value)
            {
                return;
            }
            avatarOn = value;
            if (overlay)
            {
                ApplyOpen();
            }
        }

        public void SetFocused(bool value)
        {
            if (focused == value)
            {
                return;   // asked on every key press: only a change needs animating
            }
            focused = value;
            if (overlay)
            {
                ApplyOpen();
                return;
            }
            widen?.Stop();
            widen = new Animation(160);
            float w = value ? Theme.RailExpandedWidth : Theme.RailWidth;
            widen.AnimateTo(panel, "SizeWidth", w, Widgets.EaseOut);
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

        // Overlay: the nav is folded into a single pill with the current section while the content has the focus,
        // and opens into the full list (with the fade behind it) while the nav or the profile avatar has it.
        private void ApplyOpen()
        {
            bool open = focused || avatarOn;
            if (open == opened)
            {
                Refresh();   // still open or still folded (only the focused entry may differ)
                return;
            }
            opened = open;
            widen?.Stop();
            widen = new Animation(180);
            var ease = Widgets.EaseOut;
            widen.AnimateTo(panel, "SizeWidth", (float)FadeWidth(open), ease);
            widen.AnimateTo(panel, "Opacity", open ? 1f : 0f, ease);
            widen.AnimateTo(railClip, "Opacity", open ? 1f : 0f, ease);
            widen.AnimateTo(avatar, "Opacity", open ? 1f : 0f, ease);
            widen.AnimateTo(chip, "Opacity", open ? 0f : 1f, ease);
            widen.Play();
            foreach (TextLabel l in labels)
            {
                Widgets.SetVisible(l, open);
            }
            Refresh();
        }

        // The folded pill shows the current entry: its icon and name, sized to the text.
        private void UpdateChip()
        {
            if (chip == null || index < 0 || index >= entryLabels.Count)
            {
                return;
            }
            if (index == chipIndex)
            {
                return;
            }
            chipIndex = index;
            string text = entryLabels[index];
            chipLabel.Text = text;
            chipIcon.ResourceUrl = icons[index].ResourceUrl;
            // Fit the pill to the text: ask the label for its natural width (falls back to an estimate if the
            // toolkit has not measured it yet).
            int textW = 0;
            try
            {
                textW = (int)System.Math.Ceiling((double)chipLabel.NaturalSize2D.Width);
            }
            catch (System.Exception)
            {
            }
            if (textW <= 0)
            {
                textW = (int)(text.Length * 15f);
            }
            textW = System.Math.Min(textW, 300);
            int w = ChipTextX + textW + ChipPadRight;
            chip.Size = new Size(w, ChipHeight);
            chipGlass.Size = new Size(w, ChipHeight);
        }

        // Width of the overlay's fade, from the screen's left edge: icons only, or past the labels when expanded.
        private static int FadeWidth(bool expanded) =>
            Theme.NavShift + (expanded ? Theme.RailExpandedWidth + Theme.NavFadeTail : Theme.RailWidth);

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
            bool open = focused || avatarOn;
            if (index == drawnIndex && focused == drawnFocused && open == drawnOpen)
            {
                return;   // nothing it draws has changed
            }
            drawnIndex = index;
            drawnFocused = focused;
            drawnOpen = open;
            int first = System.Math.Max(0, index - VisibleItems + 2);
            scroll = Widgets.Slide(list, 0, -first * ItemHeight, scroll);
            for (int i = 0; i < items.Count; i++)
            {
                bool current = i == index;
                if (overlay)
                {
                    // Focused: every entry is legible, the current one lit. Idle: unselected icons recede to
                    // 25%, the current section stays bright with its thick accent underline.
                    items[i].Opacity = open ? (current ? 1f : 0.7f) : (current ? 1f : IdleOpacity);
                    Widgets.SetVisible(underlines[i], current && !open);
                }
                else
                {
                    items[i].BackgroundColor = focused && current ? Theme.Accent
                                             : current ? Theme.SurfaceHigh : Color.Transparent;
                }
            }
            UpdateChip();
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
