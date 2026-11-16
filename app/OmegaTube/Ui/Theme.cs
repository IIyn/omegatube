using Tizen.NUI;

namespace OmegaTube.Ui
{
    // The interface's look. Colours, type sizes, radii and surfaces come from Current (a ThemeSpec: Omega by default,
    // see Apply); the layout geometry below is still fixed. 1920x1080 layout, sizes in pixels.
    public static class Theme
    {
        public static ThemeSpec Current { get; private set; } = ThemeSpec.Omega();

        // Selects the interface (config "uiStyle"): at start-up and when the user changes it. Screens read the theme
        // when they are built, so the caller rebuilds what is on screen.
        public static void Apply(string uiStyle)
        {
            Glorious = uiStyle == "glorious";
            Current = Glorious ? ThemeSpec.Glorious() : ThemeSpec.Omega();
        }

        public static Color Background => Current.Background;
        public static Color Surface => Current.Surface;
        public static Color SurfaceHigh => Current.SurfaceHigh;
        public static Color SurfaceFocused => Current.SurfaceFocused;
        public static Color CardInfo => Current.CardInfo;
        public static Color CardInfoFocused => Current.CardInfoFocused;
        public static Color CardTextFocused => Current.CardTextFocused;
        public static Color Accent => Current.Accent;
        public static Color PageTint => Current.PageTint;
        public static Color Text => Current.Text;
        public static Color TextDim => Current.TextDim;
        public static Color Scrim => Current.Scrim;
        public static Color Panel => Current.Panel;
        public static Color PanelLine => Current.PanelLine;
        public static Color TextMuted => Current.TextMuted;
        public static Color Success => Current.Success;
        public static Color GlassButton => Current.GlassButton;
        public static Color GlassPanel => Current.GlassPanel;
        public static Color GlassHigh => Current.GlassHigh;

        // True while the Glorious interface is selected (set by Apply): its home layout (hero, featured row, overlay
        // nav). Its look (glass surfaces) is in Current.
        public static bool Glorious { get; private set; }

        public static float TitleSize => Current.TitleSize;
        public static float BodySize => Current.BodySize;
        public static float SmallSize => Current.SmallSize;
        public static int CardRadius => Current.CardRadius;
        public static RailSpec Rail => Current.Rail;
        public static int PanelRadius => Current.PanelRadius;

        public const int CardWidth = 352;
        public const int CardGap = 30;
        public const int FocusMargin = 30;         // room for the focused card's scale-up
        public const int RowHeight = 470;

        // Glorious home geometry.
        public const int GloriousInset = 75;    // rows start here, clearing the nav; earlier cards slide under it
        public const int GloriousRowsTop = 120;    // the rows' list starts here on screen; the hero fills the band above
        public const int HeroFirstRowTop = 500;    // first row's top inside that list: below the hero's text block
        public const int BigCardWidth = 317;       // featured first row (thumbnail-only, small to show more backdrop)
        public const int BigCardGap = 34;          // more spacing between featured cards
        public const int GloriousRowHeight = RowHeight - 96;   // rows 2+: half the empty space under the thumbnails
        // Glorious grid rows (the videos after the featured first row of a category): five columns, loaded downward.
        public const int GridColumns = 5;
        public const int GridCardWidth = 330;      // 5 cards + 4 gaps fit between the left inset and the right margin
        public const int GridGap = 30;
        public const int GridRowHeight = 300;      // thumbnail + the line under it + room for the focus zoom
        public const int GridTitledRowHeight = 360;   // the same with a heading above
        public const int BigRowHeight = 340;       // tall so the next row's heading clears the featured thumbnails

        public static int ThumbHeight(int cardWidth) => cardWidth * 9 / 16;

        // Resource paths, resolved once per name (a card asks for several, hundreds of cards a page).
        private static string resources;
        private static readonly System.Collections.Generic.Dictionary<string, string> images = new System.Collections.Generic.Dictionary<string, string>();
        private static readonly System.Collections.Generic.Dictionary<string, string> icons = new System.Collections.Generic.Dictionary<string, string>();

        private static string Resolve(System.Collections.Generic.Dictionary<string, string> cache, string folder, string name)
        {
            if (!cache.TryGetValue(name, out string path))
            {
                resources = resources ?? Tizen.Applications.Application.Current.DirectoryInfo.Resource;
                path = System.IO.Path.Combine(resources, folder, name + ".png");
                cache[name] = path;
            }
            return path;
        }

        public static string Image(string name) => Resolve(images, "images", name);

        // A theme image: a full path (a plugin's file) as is, otherwise a name in res/images.
        public static string ImagePath(string image) => System.IO.Path.IsPathRooted(image) ? image : Image(image);

        // The card look of that name in the current theme (classic when it has none).
        public static CardSpec Card(string name) =>
            name != null && Current.Cards.TryGetValue(name, out CardSpec spec) ? spec : Current.Cards["classic"];

        public static string Icon(string name) => Resolve(icons, "icons", name);
    }
}
