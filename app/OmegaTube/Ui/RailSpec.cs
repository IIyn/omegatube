using System.Collections.Generic;
using Tizen.NUI;

namespace OmegaTube.Ui
{
    // How the navigation rail behaves. Both are native; a theme picks one and sets its parameters. A new kind of rail
    // is a host change.
    public enum RailMode
    {
        Drawer,    // Omega: a panel that shows icons only, and widens with the labels while the rail has the focus
        Overlay,   // folded into one pill with the current section; opens over the content when focused
    }

    // The rail as data (docs/plugins.md: rail.json). Images are a resource name or a full path (Theme.ImagePath);
    // colours left null follow the theme.
    public sealed class RailSpec
    {
        public RailMode Mode = RailMode.Drawer;
        public int Width = 110;       // collapsed: icons only
        public int OpenWidth = 330;   // focused: icons + labels
        public int Shift;             // the whole nav sits this far in from the screen edge (overlay)

        // Drawer: the panel resting / open, and the current entry resting / focused.
        public Color PanelColor, PanelOpenColor, CurrentColor, FocusedColor;

        // Overlay
        public string Fade = "nav_fade";            // dark fade behind the open list, from the screen edge
        public int FadeTail = 150;                  // the fade reaches this far past the open labels
        public string Chip = "glass_dark9.9";       // the folded pill's background
        public float IdleOpacity = 0.25f;           // folded: entries other than the current one
        public float OpenOpacity = 0.7f;            // open: entries other than the current one
        public Color UnderlineColor;                // folded: the thick underline under the current entry

        // Icon variant tried first (e.g. "_outline", when the app has one), and per-entry replacements (icon key -> image).
        public string IconSuffix;
        public Dictionary<string, string> Icons = new Dictionary<string, string>();

        // The icon to draw for an entry key ("home", "search"...): a replacement, else the variant, else the app's.
        public string IconPath(string key)
        {
            if (Icons.TryGetValue(key, out string image))
            {
                return Theme.ImagePath(image);
            }
            if (IconSuffix != null && System.IO.File.Exists(Theme.Icon(key + IconSuffix)))
            {
                return Theme.Icon(key + IconSuffix);
            }
            return Theme.Icon(key);
        }

        public static RailSpec Drawer() => new RailSpec();

        // Icons floating over the artwork, round avatar on top; idle, the current entry is marked with a
        // thick accent underline; focused, a frosted fade and a glass pill on the focused entry.
        public static RailSpec Overlay() => new RailSpec
        {
            Mode = RailMode.Overlay,
            Shift = 25,
        };
    }
}
