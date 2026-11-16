using Tizen.NUI;

namespace OmegaTube.Ui
{
    // Where a card shows the video's texts. Each is a native layout (tuned for hundreds of cards and for Tizen 5.5,
    // which has no CornerRadius); a theme picks one and sets its parameters. A new kind is a host change.
    public enum CardInfoStyle
    {
        Box,       // Omega: a box glued under the thumbnail (title on two lines, then the meta line), lit when focused
        Overlay,   // Glorious: a dark panel with the title fades in on the thumbnail when focused, the meta line under it
        None,      // Glorious featured row: the thumbnail alone (the title lives elsewhere, on the hero)
    }

    // One card look, as data (docs/plugins.md: cards.json). Images are a resource name in res/images or a full path
    // (Theme.ImagePath). Colours left null follow the theme (Theme.CardInfo...).
    public sealed class CardSpec
    {
        public CardInfoStyle Info = CardInfoStyle.Box;
        public float FocusScale = 1.1f;
        public string Mask = "round_mask";   // alpha mask that rounds the thumbnail (no CornerRadius on 5.5)

        // Pre-blurred shadow nine-patch under the thumbnail (null: none). Its blur margin and drop are baked into the
        // image, so they travel with it.
        public string Shadow;
        public int ShadowMargin = 36, ShadowDrop = 10;

        public string Outline;   // nine-patch ring drawn round the focused card (null: none)
        public bool Preview;     // the animated preview may play over the thumbnail (Overlay cards)

        // Box
        public int BoxHeight = 104;   // title (2 lines) + channel line
        public Color BoxColor, BoxFocusedColor, TextFocusedColor;

        // Overlay
        public string OverlayPanel = "card_info9.9";

        // The card's height at a width: the 16:9 thumbnail plus what its info style adds under it.
        public int Height(int width)
        {
            int thumbH = Theme.ThumbHeight(width);
            switch (Info)
            {
                case CardInfoStyle.Overlay: return thumbH + Card.MetaRowH;
                case CardInfoStyle.None: return thumbH;
                default: return thumbH + BoxHeight;
            }
        }

        public static CardSpec Classic() => new CardSpec();

        // Glass: a bare rounded thumbnail with a soft shadow; focused, a dark title panel on it, the meta line under
        // it, a hairline outline, and the animated preview.
        public static CardSpec Glass() => new CardSpec
        {
            Info = CardInfoStyle.Overlay,
            Shadow = "card_shadow9.9",
            Outline = "card_ring9.9",
            Preview = true,
        };

        // Featured: the glass card without any text.
        public static CardSpec Featured() => new CardSpec
        {
            Info = CardInfoStyle.None,
            Shadow = "card_shadow9.9",
            Outline = "card_ring9.9",
        };
    }
}
