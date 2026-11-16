namespace OmegaTube.Ui
{
    // How the home sections show a feed.
    public enum FeedLayout
    {
        Auto,   // Omega: Home (topic shelves) as rows, the other feeds as a grid
        Rows,   // every feed as rows (a flat feed becomes a featured row, then grid rows)
    }

    // The rows of a home feed (ShelfList), in pixels on the 1920x1080 page.
    public sealed class RowsSpec
    {
        public int Top = 90;             // the list's top on screen
        public bool UnderRail;           // full width from the screen edge (cards slide under the nav); else beside the rail
        public int Inset = Theme.FocusMargin;   // x of the focused card inside the list

        public string Card = "classic";  // card look of the rows (Theme.Card)
        public int CardWidth = Theme.CardWidth, CardGap = Theme.CardGap, RowHeight = Theme.RowHeight;

        // The first row's own look (null: like the others); FirstRowTop holds it lower while focused (hero text above).
        public string FirstCard;
        public int FirstCardWidth, FirstCardGap, FirstRowHeight, FirstRowTop;

        // Grid rows (a flat feed shown as rows): videos placed GridColumns at a time after the first FlatFeatured.
        public int FlatFeatured = 10;
        public int GridColumns = 5, GridCardWidth = 330, GridGap = 30;
        public int GridRowHeight = 300;         // thumbnail + the line under it + room for the focus zoom
        public int GridTitledRowHeight = 360;   // the same with a heading above
    }

    // The full-screen backdrop behind the first row: the focused video's picture, its title and details, and a
    // muted video preview after a rest (Hero, Backdrop, VideoPreview).
    public sealed class HeroSpec
    {
        public string LeftScrim = "hero_left", BottomScrim = "hero_bottom";   // full-screen pre-rendered fades
        public int TitleWidth = 768;   // 40% of the screen; up to two lines, then an ellipsis
        public float TitleSize = 59f;
        public bool VideoPreview = true;   // offered (the user can still switch it off in Settings)
    }

    // The home screen as data (docs/plugins.md: home.json). The home keeps what it built (hero, fades) while the
    // theme's HomeSpec is the same instance, so the built-in ones are shared.
    public sealed class HomeSpec
    {
        public FeedLayout Feeds = FeedLayout.Auto;
        public bool Header = true;   // the section's name above its feed
        public RowsSpec Rows = new RowsSpec();

        // Grid feeds (FeedGrid): top on screen, and x (-1: beside the rail).
        public int GridTop = 110, GridLeft = -1;

        public HeroSpec Hero;          // null: no backdrop
        public bool TintBelowFirstRow; // the page turns Theme.PageTint once the focus leaves the first row (hero only)

        // Fades over the rows band while the hero shows (null: none): left edge, right edge, under the first row.
        public string FadeLeft, FadeRight, FadeBottom;

        public static readonly HomeSpec Omega = new HomeSpec();
    }
}
