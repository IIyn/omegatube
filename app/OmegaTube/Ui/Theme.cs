using Tizen.NUI;

namespace OmegaTube.Ui
{
    // Visual constants (1920x1080 layout, sizes in pixels).
    public static class Theme
    {
        public static readonly Color Background = new Color(0.06f, 0.06f, 0.07f, 1f);
        public static readonly Color Surface = new Color(0.12f, 0.12f, 0.14f, 1f);
        public static readonly Color SurfaceFocused = new Color(0.22f, 0.22f, 0.26f, 1f);
        public static readonly Color Accent = new Color(0.90f, 0.13f, 0.09f, 1f);
        public static readonly Color Text = Color.White;
        public static readonly Color TextDim = new Color(0.67f, 0.67f, 0.70f, 1f);
        public static readonly Color Scrim = new Color(0f, 0f, 0f, 0.65f);

        public const float TitleSize = 34f;
        public const float BodySize = 24f;
        public const float SmallSize = 20f;

        public const int SidebarWidth = 260;
        public const int CardWidth = 352;
        public const int ThumbHeight = 198;
        public const int CardHeight = 300;
        public const int CardGap = 28;
        public const int FocusMargin = 30;  // room for the focused card's frame and scale-up
        public const int RowHeight = 440;
    }
}
