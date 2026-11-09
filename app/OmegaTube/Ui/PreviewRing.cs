using System;
using System.Diagnostics;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // The video-preview countdown on the focused card's top-right corner: a transparent gradient arc (no background)
    // that sweeps clockwise to a full ring over the dwell, so the user sees exactly when the preview starts. If the
    // video is not ready at that moment the full ring spins until it is; a refusal flashes it red.
    // Tizen 5.5 has no vector arcs: the frames are pre-drawn (tools/preview-ring/make_ring.py) in one sprite sheet
    // and shown by moving PixelArea, picked from the elapsed time so it stays exact even if a tick comes late.
    internal class PreviewRing : View
    {
        public const int Diameter = 56;
        private const int Frames = 90, Cols = 10, Rows = 9;          // preview_ring.png: 10 x 9 frames of 64 px
        private const int SheetWidth = Cols * 64, SheetHeight = Rows * 64;
        private const uint FailMs = 1200;

        private readonly ImageView arc;
        private readonly Stopwatch clock = new Stopwatch();
        private Timer tick, failTimer;
        private Animation spin;
        private uint durationMs;
        private Action completed;

        public PreviewRing()
        {
            Size = new Size(Diameter, Diameter);
            PositionUsesPivotPoint = false;
            arc = new ImageView
            {
                Size = new Size(Diameter, Diameter), PivotPoint = Tizen.NUI.PivotPoint.Center,
                PositionUsesPivotPoint = false,
            };
            // Decode the whole sheet at its own size: shrunk to the 56 px view, each frame would be a few pixels.
            var map = new PropertyMap();
            map.Add((int)Visual.Property.Type, new PropertyValue((int)Visual.Type.Image));
            map.Add((int)ImageVisualProperty.URL, new PropertyValue(Theme.Image("preview_ring")));
            map.Add((int)ImageVisualProperty.DesiredWidth, new PropertyValue(SheetWidth));
            map.Add((int)ImageVisualProperty.DesiredHeight, new PropertyValue(SheetHeight));
            arc.Image = map;
            Add(arc);
            ShowFrame(0);
            Hide();
        }

        // Sweeps to a full ring over `ms`, then calls onDone (once).
        public void Countdown(uint ms, Action onDone)
        {
            Stop();
            durationMs = Math.Max(1u, ms);
            completed = onDone;
            ShowFrame(0);
            Show();
            clock.Restart();
            tick = new Timer(33);
            tick.Tick += (s, e) => UiGuard.Tick("PreviewRing.tick", Step);
            tick.Start();
        }

        private bool Step()
        {
            long t = clock.ElapsedMilliseconds;
            ShowFrame((int)Math.Min(Frames - 1, t * Frames / durationMs));
            if (t < durationMs)
            {
                return true;
            }
            tick = null;
            Action done = completed;
            completed = null;
            done?.Invoke();
            return false;
        }

        // Countdown over but the video is not showing yet: the full ring spins.
        public void Spin()
        {
            Stop();
            ShowFrame(Frames - 1);
            Show();
            spin = new Animation(1000) { Looping = true };
            spin.AnimateBy(arc, "Orientation", new Rotation(new Radian(new Degree(360f)), PositionAxis.Z));
            spin.Play();
        }

        // The preview could not start: a short red ring.
        public void Fail()
        {
            Stop();
            ShowFrame(Frames - 1);
            arc.Color = new Color(1f, 0.25f, 0.25f, 1f);
            Show();
            failTimer = new Timer(FailMs);
            failTimer.Tick += (s, e) => UiGuard.Tick("PreviewRing.fail", () =>
            {
                failTimer = null;
                Off();
                return false;
            });
            failTimer.Start();
        }

        public void Off()
        {
            Stop();
            Hide();
        }

        private void Stop()
        {
            tick?.Stop();
            tick = null;
            completed = null;
            failTimer?.Stop();
            failTimer = null;
            spin?.Stop();
            spin = null;
            arc.Orientation = new Rotation(new Radian(0f), PositionAxis.Z);
            arc.Color = Color.White;
        }

        private void ShowFrame(int f)
        {
            arc.PixelArea = new RelativeVector4((float)(f % Cols) / Cols, (float)(f / Cols) / Rows, 1f / Cols, 1f / Rows);
        }
    }
}
