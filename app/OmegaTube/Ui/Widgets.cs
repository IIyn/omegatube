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
