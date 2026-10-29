using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Launch screen over the home screen: the Ω on black, the play triangle popping into it, then a fade once
    // the home rows are on screen (or after MaxMs). It starts exactly like shared/res/splash.png, which the
    // platform shows while the app process starts, so the hand-over is invisible.
    public class Splash : View
    {
        private const int LogoSize = 280;
        private const int MinMs = 900;        // never just flash
        private const int MaxMs = 6000;       // give up waiting for the feed
        private const int SpinnerAfterMs = 1500;

        private readonly Func<bool> ready;
        private readonly ImageView play;
        private readonly long start = Environment.TickCount;
        private View spinner;
        private Animation pop, spin, pulse, fade;   // fields: a collected Animation never finishes
        private Timer poll;

        public event Action Closed;

        public Splash(Func<bool> ready)
        {
            this.ready = ready;
            Size = new Size(1920, 1080);
            BackgroundColor = Color.Black;

            var logo = new ImageView
            {
                ResourceUrl = Theme.Image("splash_omega"),
                Size = new Size(LogoSize, LogoSize),
                Position = new Position((1920 - LogoSize) / 2, (1080 - LogoSize) / 2),
            };
            Add(logo);
            play = new ImageView
            {
                ResourceUrl = Theme.Image("splash_play"),
                Size = new Size(LogoSize, LogoSize),
                PivotPoint = new Position(0.506f, 0.477f, 0.5f),   // the triangle's centre in the logo
                PositionUsesPivotPoint = false,
                Scale = new Vector3(0f, 0f, 1f),
            };
            logo.Add(play);

            pop = new Animation(450);
            pop.AnimateTo(play, "Scale", new Vector3(1f, 1f, 1f), 150, 450, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOutBack));
            pop.Finished += (s, e) => Pulse();
            pop.Play();

            poll = new Timer(100);
            poll.Tick += (s, e) => Check();
            poll.Start();
        }

        // A slow heartbeat on the triangle while waiting.
        private void Pulse()
        {
            if (poll == null)
            {
                return;
            }
            pulse = new Animation(1200) { Looping = true };
            pulse.AnimateTo(play, "Scale", new Vector3(0.88f, 0.88f, 1f), 0, 600, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
            pulse.AnimateTo(play, "Scale", new Vector3(1f, 1f, 1f), 600, 1200, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut));
            pulse.Play();
        }

        private bool Check()
        {
            long elapsed = Environment.TickCount - start;
            if (elapsed >= MaxMs || (elapsed >= MinMs && ready()))
            {
                poll = null;
                Close();
                return false;
            }
            if (spinner == null && elapsed >= SpinnerAfterMs)
            {
                spinner = Widgets.Spinner(44, Theme.TextDim, out spin);
                spinner.Position = new Position(960, (1080 + LogoSize) / 2 + 70);
                Add(spinner);
            }
            return true;
        }

        // The remote is handed back as the fade starts, not when it ends.
        private void Close()
        {
            Closed?.Invoke();
            fade = new Animation(350);
            fade.AnimateTo(this, "Opacity", 0f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            fade.Finished += (s, e) =>
            {
                pulse?.Stop();
                spin?.Stop();
                Unparent();
            };
            fade.Play();
        }
    }
}
