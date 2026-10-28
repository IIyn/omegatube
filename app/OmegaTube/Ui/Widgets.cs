using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    public static class Widgets
    {
        public static TextLabel Label(string text, float pixelSize, Color color, int width = 0, int height = 0)
        {
            var label = new TextLabel(text)
            {
                TextColor = color,
                PixelSize = pixelSize,
                Ellipsis = true,
            };
            if (width > 0)
            {
                label.Size = new Size(width, height > 0 ? height : pixelSize * 1.5f);
            }
            return label;
        }

        public static View Box(Color color, int width, int height)
        {
            return new View { BackgroundColor = color, Size = new Size(width, height) };
        }

        public static void SetVisible(View view, bool visible)
        {
            if (visible)
            {
                view.Show();
            }
            else
            {
                view.Hide();
            }
        }

        // Loading spinner: a ring of dots with a fading tail, rotating forever. Returns the ring (centre
        // it with Position) and its animation (Stop/Play with the ring's visibility).
        public static View Spinner(int size, Color color, out Animation spin)
        {
            var ring = new View { Size = new Size(size, size), PivotPoint = Tizen.NUI.PivotPoint.Center, PositionUsesPivotPoint = true };
            const int Dots = 10;
            int dot = size / 7;
            float r = size / 2f - dot;
            for (int i = 0; i < Dots; i++)
            {
                double a = 2 * System.Math.PI * i / Dots;
                var d = new View
                {
                    Size = new Size(dot, dot),
                    CornerRadius = dot / 2f,
                    BackgroundColor = new Color(color.R, color.G, color.B, 0.15f + 0.85f * i / (Dots - 1)),
                    Position = new Position(size / 2f + r * (float)System.Math.Cos(a) - dot / 2f,
                                            size / 2f + r * (float)System.Math.Sin(a) - dot / 2f),
                    PositionUsesPivotPoint = false,
                };
                ring.Add(d);
            }
            spin = new Animation(900) { Looping = true };
            spin.AnimateBy(ring, "Orientation", new Rotation(new Radian(new Degree(360f)), PositionAxis.Z));
            spin.Play();
            return ring;
        }

        public static ImageView Icon(string name, int size)
        {
            return new ImageView { ResourceUrl = Theme.Icon(name), Size = new Size(size, size) };
        }

        public static View Rounded(Color color, int width, int height, float radius)
        {
            return new View { BackgroundColor = color, Size = new Size(width, height), CornerRadius = radius };
        }

        // On/off switch (settings rows). Update with SetSwitch.
        public static View Switch(bool on)
        {
            var track = Rounded(Theme.SurfaceHigh, 72, 40, 20);
            var knob = Rounded(Theme.Text, 30, 30, 15);
            knob.Position = new Position(5, 5);
            track.Add(knob);
            SetSwitch(track, on);
            return track;
        }

        public static void SetSwitch(View track, bool on)
        {
            track.BackgroundColor = on ? Theme.Accent : new Color(0.45f, 0.45f, 0.45f, 1f);
            track.GetChildAt(0).PositionX = on ? 37 : 5;
        }

        // Pill button (icon + text) with a focused look, as used by the account screen and player overlays.
        public class PillButton : View
        {
            private readonly TextLabel label;
            private readonly ImageView icon;

            public PillButton(string text, string iconName, int width, int height = 76)
            {
                Size = new Size(width, height);
                CornerRadius = height / 2f;
                int x = 34;
                if (iconName != null)
                {
                    icon = Icon(iconName, 40);
                    icon.Position = new Position(28, (height - 40) / 2);
                    Add(icon);
                    x = 80;
                }
                label = Label(text, Theme.BodySize + 2, Theme.Text, width - x - 24, height);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Position = new Position(x, 0);
                Add(label);
                SetFocused(false);
            }

            public string Text
            {
                get => label.Text;
                set => label.Text = value;
            }

            public void SetFocused(bool focused)
            {
                BackgroundColor = focused ? Theme.CardInfoFocused : Theme.SurfaceHigh;
                label.TextColor = focused ? Theme.CardTextFocused : Theme.Text;
                if (icon != null)
                {
                    // White icons on the light focused pill: darken them through the mix colour.
                    icon.Color = focused ? new Color(0.16f, 0.16f, 0.16f, 1f) : Color.White;
                }
                Scale = focused ? new Vector3(1.04f, 1.04f, 1f) : Vector3.One;
            }
        }

        // Greedy word wrap to lines of at most `max` characters (subtitles: the label sizes to its text).
        public static string Wrap(string text, int max)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string paragraph in text.Split('\n'))
            {
                int line = 0;
                foreach (string word in paragraph.Split(' '))
                {
                    if (word.Length == 0)
                    {
                        continue;
                    }
                    if (line > 0 && line + 1 + word.Length > max)
                    {
                        sb.Append('\n');
                        line = 0;
                    }
                    else if (line > 0)
                    {
                        sb.Append(' ');
                        line++;
                    }
                    sb.Append(word);
                    line += word.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // Smoothly moves a view; returns the animation so callers can stop a running one.
        public static Animation Slide(View view, float x, float y, Animation previous, int ms = 180)
        {
            previous?.Stop();
            var anim = new Animation(ms);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            anim.AnimateTo(view, "PositionX", x, ease);
            anim.AnimateTo(view, "PositionY", y, ease);
            anim.Play();
            return anim;
        }
    }
}
