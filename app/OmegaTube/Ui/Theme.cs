using Tizen.NUI;

namespace OmegaTube.Ui
{
    // SmartTube's "Dark Grey" look (as followed by ytps5): black page, grey panels, YouTube red accent.
    // 1920x1080 layout, sizes in pixels.
    public static class Theme
    {
        public static readonly Color Background = Rgb(0x000000);
        public static readonly Color Surface = Rgb(0x262626);
        public static readonly Color SurfaceHigh = Rgb(0x3a3a3a);
        public static readonly Color SurfaceFocused = Rgb(0x3a3a3a);
        public static readonly Color CardInfo = Rgb(0x1f1f1f);
        public static readonly Color CardInfoFocused = Rgb(0xe3e3e3);
        public static readonly Color CardTextFocused = Rgb(0x2a2a2a);
        public static readonly Color Accent = Rgb(0xe52d27);
        public static readonly Color Text = Rgb(0xf1f1f1);
        public static readonly Color TextDim = Rgb(0xaaaaaa);
        public static readonly Color Scrim = new Color(0f, 0f, 0f, 0.65f);

        public const float TitleSize = 34f;
        public const float BodySize = 24f;
        public const float SmallSize = 19f;

        public const int RailWidth = 110;          // collapsed: icons only
        public const int RailExpandedWidth = 330;  // focused: icons + labels
        public const int CardWidth = 352;
        public const int CardGap = 30;
        public const int CardInfoHeight = 104;     // title (2 lines) + channel line
        public const int CardRadius = 8;
        public const float FocusScale = 1.1f;
        public const int FocusMargin = 30;         // room for the focused card's scale-up
        public const int RowHeight = 470;

        public static int ThumbHeight(int cardWidth) => cardWidth * 9 / 16;
        public static int CardHeight(int cardWidth) => ThumbHeight(cardWidth) + CardInfoHeight;

        public static string Icon(string name) =>
            System.IO.Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Resource, "icons", name + ".png");

        private static Color Rgb(int rgb) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
    }
}
