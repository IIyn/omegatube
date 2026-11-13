using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // A full-screen page managed by App's screen stack. Screens do their own focus handling:
    // the remote only sends arrows / Return / Back, so a simple index model beats NUI's FocusManager.
    public abstract class Screen
    {
        public View Root { get; } = new View { WidthResizePolicy = Tizen.NUI.ResizePolicyType.FillToParent,
                                               HeightResizePolicy = Tizen.NUI.ResizePolicyType.FillToParent };

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
