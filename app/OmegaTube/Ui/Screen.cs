using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // A full-screen page managed by App's screen stack. Screens do their own focus handling:
    // the remote only sends arrows / Return / Back, so a simple index model beats NUI's FocusManager.
    public abstract class Screen
    {
        public View Root { get; } = new View { WidthResizePolicy = Tizen.NUI.ResizePolicyType.FillToParent,
                                               HeightResizePolicy = Tizen.NUI.ResizePolicyType.FillToParent };

        // The whole interface is laid out at 1920x1080 (Theme.cs), so a window of another logical size is fitted by
        // scaling the page rather than re-flowing it: every hardcoded coordinate stays correct, and the same picture
        // lands on a 720p panel, a 1080p one or a 1440p one. Scale is the smaller of the two ratios, so nothing is
        // cropped; the remainder is letterboxed.
        public float FitScale { get; private set; } = 1f;

        public void FitToWindow()
        {
            Size2D size = Window.Instance.WindowSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }
            FitScale = Math.Min(size.Width / Theme.DesignWidth, size.Height / Theme.DesignHeight);
            if (Math.Abs(FitScale - 1f) < 0.001f)
            {
                Root.Scale = new Vector3(1f, 1f, 1f);
                return;
            }
            Root.PivotPoint = PivotPoint.TopLeft;
            Root.PositionUsesPivotPoint = false;
            Root.Scale = new Vector3(FitScale, FitScale, 1f);
            Tizen.Log.Info("OmegaTube", $"window {size.Width}x{size.Height}, layout {Theme.DesignWidth}x{Theme.DesignHeight} at {FitScale:0.###}");
        }

        protected App App { get; }

        protected Screen(App app)
        {
            App = app;
        }

        public virtual void OnShow()
        {
        }

        public virtual void OnHide()
        {
        }

        // Called when the app goes to background (Home button, other app, another HDMI source): release scarce resources.
        public virtual void OnPause()
        {
        }

        // The app is back in the foreground and this screen is the one on display again.
        public virtual void OnResume()
        {
        }

        // Returns true when the key was consumed. Unconsumed Back pops the screen.
        public abstract bool OnKey(string key);
    }
}
