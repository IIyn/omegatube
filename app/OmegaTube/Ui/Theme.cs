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
        // Glorious: the page behind the rows from the second row down (the first row has the artwork backdrop).
        public static readonly Color PageTint = Rgb(0x0F121C);   // a dark blue-slate: #2D3452 at 49% brightness
        public static readonly Color Text = Rgb(0xf1f1f1);
        public static readonly Color TextDim = Rgb(0xaaaaaa);
        public static readonly Color Scrim = new Color(0f, 0f, 0f, 0.65f);
        public static readonly Color Panel = Rgb(0x181818);         // settings / account cards, player panels
        public static readonly Color PanelLine = Rgb(0x2c2c2c);     // separators
        public static readonly Color TextMuted = Rgb(0x6f6f6f);
        public static readonly Color Success = Rgb(0x2ba640);
        // Grey rather than a white tint: as light as before on dark scenes, but it darkens bright ones under the icons.
        public static readonly Color GlassButton = new Color(0.24f, 0.24f, 0.24f, 0.6f);

        // "Liquid glass" for the Glorious theme. The TV's NUI has no backdrop blur, so the glass is translucent fills
        // (mostly the baked nine-patches glass*9.9.png, see tools/glass-panel); these two tints serve the few plain
        // tinted panels: GlassPanel darkens a card over the artwork, GlassHigh is a light translucent disc.
        public static readonly Color GlassPanel = new Color(0.10f, 0.11f, 0.14f, 0.34f);
        public static readonly Color GlassHigh = new Color(1f, 1f, 1f, 0.16f);

        // Tube: the light pill YouTube lights its current nav entry with (dark text and icon on it).
        public static readonly Color TubePill = Rgb(0xe8e8e8);

        // Tube: the channel line under a card. YouTube runs three lines there (title, channel, views/age) with the
        // channel brighter than the views line, so the card needs a shade between Text and TextDim.
        public static readonly Color TubeSub = Rgb(0xc9c9c9);

        // True while the Glorious interface is selected (set from the config at startup and when it changes):
        // shared widgets (panels, buttons) draw as glass instead of flat grey.
        public static bool Glorious;

        // True while the Tube interface is selected (the YouTube TV look): three big cards a row, with the title and
        // the channel line under the thumbnail on the page colour. Set from the config like Glorious.
        public static bool Tube;

        // The layout is designed at this size and everything below is in its pixels (Screen.FitToWindow scales the
        // finished page when a window reports another logical size).
        public const int DesignWidth = 1920, DesignHeight = 1080;

        public const float TitleSize = 34f;
        public const float BodySize = 24f;
        public const float SmallSize = 19f;

        public const int RailWidth = 110;          // collapsed: icons only
        public const int RailExpandedWidth = 330;  // focused: icons + labels
        public const int CardWidth = 352;
        public const int CardGap = 30;
        public const int CardInfoHeight = 104;     // title (2 lines) + channel line
        public const int CardRadius = 14;          // soft corners (no sharp edges)
        public const float FocusScale = 1.1f;
        public const int FocusMargin = 30;         // room for the focused card's scale-up
        public const int RowHeight = 470;
        public const int PanelRadius = 20;

        // Glorious home geometry.
        public const int NavShift = 25;          // the Glorious nav sits this far in from the screen edge
        public const int NavFadeTail = 150;        // the nav's fade reaches this far past the expanded labels
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

        // Tube (YouTube TV) geometry, matched to what the TV's own YouTube app shows on the same panel: two cards
        // and the near half of a third, so the cards (and the type under them) are big. YouTube measures ~27.5% of
        // the width per card while its nav is open, which is the 2.5-up look this reproduces.
        public const int TubeColumns = 3;            // grid feeds (search, history) stay 3-up: no peek there
        public const int TubeCardWidth = 660;
        public const int TubeCardGap = 34;
        public const int TubeCardInfoHeight = 212;   // title (2 lines, 112) + channel + views/age + padding
        public const int TubeRowHeight = 700;        // heading + cards from StripY 64 + 583 + ~53 before the next
        public const int TubeRailWidth = 144;        // icons only, YouTube's collapsed nav
        public const int TubeRailExpandedWidth = 420;   // open: icon + label rows, YouTube's pill is ~420
        public const int TubeCardInfoSmall = 152;     // the player's cards (400 wide): the same three lines, smaller type
        public const int TubePlayerCardWidth = 400;    // the player's row has to fit 570..1080 with StripY 64
        public const int TubePlayerRowHeight = 441;    // StripY 64 + the card's 377

        public static int TubeInfoFor(int cardWidth) => cardWidth >= 600 ? TubeCardInfoHeight : TubeCardInfoSmall;
        public static int TubeCardHeight(int cardWidth) => ThumbHeight(cardWidth) + TubeInfoFor(cardWidth);

        // The rail's width in the current style (expanded: with the labels out).
        public static int RailFor(bool expanded) => Tube
            ? (expanded ? TubeRailExpandedWidth : TubeRailWidth)
            : (expanded ? RailExpandedWidth : RailWidth);

        public static int ThumbHeight(int cardWidth) => cardWidth * 9 / 16;
        public static int CardHeight(int cardWidth) => ThumbHeight(cardWidth) + CardInfoHeight;

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

        public static string Icon(string name) => Resolve(icons, "icons", name);

        private static Color Rgb(int rgb) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
    }
}
