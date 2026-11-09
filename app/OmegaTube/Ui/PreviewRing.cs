using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // The video-preview indicator on the focused card's top-right corner: a ring of dots that fills clockwise over
    // the dwell (so the user sees when the preview will start), then spins while the video loads, and goes away
    // once the video shows. Dots are circle images (CornerRadius does not exist on Tizen 5.5).
    internal class PreviewRing : View
    {
        public const int Diameter = 44;
        private const int Dots = 12;
        private const int DotSize = 7;
        private const float Radius = 14.5f;
        private const float DimDot = 0.22f;

        private readonly View spinner;          // the dots, rotated as one while loading
        private readonly ImageView[] dots = new ImageView[Dots];
        private Animation fill, spin;

        public PreviewRing()
        {
            Size = new Size(Diameter, Diameter);
            // Rotate around the centre while keeping Position as the top-left corner.
            PositionUsesPivotPoint = false;
            Add(new ImageView
            {
                ResourceUrl = Theme.Image("circle_mask"), Size = new Size(Diameter, Diameter),
                Color = new Color(0f, 0f, 0f, 0.5f),
            });
            spinner = new View
            {
                Size = new Size(Diameter, Diameter), PivotPoint = Tizen.NUI.PivotPoint.Center,
                PositionUsesPivotPoint = false,
            };
            Add(spinner);
            float c = Diameter / 2f;
            for (int i = 0; i < Dots; i++)
            {
                double a = -Math.PI / 2 + 2 * Math.PI * i / Dots;   // clockwise from 12 o'clock
                dots[i] = new ImageView
                {
                    ResourceUrl = Theme.Image("circle_mask"), Size = new Size(DotSize, DotSize), Color = Color.White,
                    Position = new Position(c + Radius * (float)Math.Cos(a) - DotSize / 2f,
                                            c + Radius * (float)Math.Sin(a) - DotSize / 2f),
                };
                spinner.Add(dots[i]);
            }
            Hide();
        }

        // Dots light up one by one over `ms`: full ring = the preview starts.
        public void Countdown(uint ms)
        {
            Stop();
            foreach (ImageView d in dots)
            {
                d.Opacity = DimDot;
            }
            fill = new Animation((int)ms);
            for (int i = 0; i < Dots; i++)
            {
                int from = (int)(ms * i / Dots), to = (int)(ms * (i + 1) / Dots);
                fill.AnimateTo(dots[i], "Opacity", 1f, from, to);
            }
            Show();
            fill.Play();
        }

        // The preview is loading: a spinning tail.
        public void Loading()
        {
            Stop();
            for (int i = 0; i < Dots; i++)
            {
                dots[i].Opacity = DimDot + (1f - DimDot) * i / (Dots - 1);
            }
            spin = new Animation(900) { Looping = true };
            spin.AnimateBy(spinner, "Orientation", new Rotation(new Radian(new Degree(360f)), PositionAxis.Z));
            Show();
            spin.Play();
        }

        public void Off()
        {
            Stop();
            Hide();
        }

        private void Stop()
        {
            fill?.Stop();
            fill = null;
            spin?.Stop();
            spin = null;
            spinner.Orientation = new Rotation(new Radian(0f), PositionAxis.Z);
        }
    }
}
