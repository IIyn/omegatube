using Tizen.NUI;

namespace OmegaTube.Ui
{
    // The interface's look. Colours, type sizes, radii and surfaces come from Current (a ThemeSpec: Omega by default,
    // see Apply); the constants below are the defaults the specs start from. 1920x1080 layout, sizes in pixels.
    public static class Theme
    {
        public static ThemeSpec Current { get; private set; } = ThemeSpec.Omega();

        // The interface in use: "omega", "glorious" or an installed plugin's id (Omega when that one could not load).
        public static string Active { get; private set; } = "omega";

        // Changes at each Apply: what was built for the previous theme must be built again.
        public static int Generation { get; private set; }

        // Selects the interface (config "uiStyle"): at start-up and when the user changes it. Screens read the theme
        // when they are built, so the caller rebuilds what is on screen. A plugin that cannot be read gives Omega.
        public static void Apply(string uiStyle)
        {
            ThemeSpec spec = null;
            if (uiStyle == "glorious")
            {
                spec = ThemeSpec.Glorious();
            }
            else if (uiStyle != "omega")
            {
                spec = Plugins.PluginStore.Load(uiStyle);
            }
            Current = spec ?? ThemeSpec.Omega();
            Active = spec != null ? uiStyle : "omega";
            Generation++;
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

        public static float TitleSize => Current.TitleSize;
        public static float BodySize => Current.BodySize;
        public static float SmallSize => Current.SmallSize;
        public static int CardRadius => Current.CardRadius;
        public static RailSpec Rail => Current.Rail;
        public static HomeSpec Home => Current.Home;
        public static int PanelRadius => Current.PanelRadius;

        public const int CardWidth = 352;
        public const int CardGap = 30;
        public const int FocusMargin = 30;         // room for the focused card's scale-up
        public const int RowHeight = 470;


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
