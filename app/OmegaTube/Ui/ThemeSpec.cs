using System.Collections.Generic;
using Tizen.NUI;

namespace OmegaTube.Ui
{
    // A background drawn from a nine-patch image (the "glass" panels and pills). Null where a theme uses flat
    // rounded fills instead. Path is a full file path: today an image in res/, later one inside a plugin package.
    public sealed class SurfaceSpec
    {
        public string Path;

        public SurfaceSpec(string path)
        {
            Path = path;
        }
    }

    // The look of the interface as data: colours, type sizes, radii and surfaces. Theme.Current holds the one in use
    // and Theme's static members read it. Omega() is the built-in default; Glorious() is the immersive look until it
    // becomes a plugin (docs/plugins.md: theme.json, cards.json). Layout geometry (rows, rail, hero) is not here yet.
    public sealed class ThemeSpec
    {
        // SmartTube's "Dark Grey" look (as followed by ytps5): black page, grey panels, YouTube red accent.
        public Color Background = Rgb(0x000000);
        public Color Surface = Rgb(0x262626);
        public Color SurfaceHigh = Rgb(0x3a3a3a);
        public Color SurfaceFocused = Rgb(0x3a3a3a);
        public Color CardInfo = Rgb(0x1f1f1f);
        public Color CardInfoFocused = Rgb(0xe3e3e3);
        public Color CardTextFocused = Rgb(0x2a2a2a);
        public Color Accent = Rgb(0xe52d27);
        // The page behind the rows from the second row down when a hero backdrop covers the first one.
        public Color PageTint = Rgb(0x0F121C);   // a dark blue-slate: #2D3452 at 49% brightness
        public Color Text = Rgb(0xf1f1f1);
        public Color TextDim = Rgb(0xaaaaaa);
        public Color Scrim = new Color(0f, 0f, 0f, 0.65f);
        public Color Panel = Rgb(0x181818);         // settings / account cards, player panels
        public Color PanelLine = Rgb(0x2c2c2c);     // separators
        public Color TextMuted = Rgb(0x6f6f6f);
        public Color Success = Rgb(0x2ba640);
        // Grey rather than a white tint: as light as before on dark scenes, but it darkens bright ones under the icons.
        public Color GlassButton = new Color(0.24f, 0.24f, 0.24f, 0.6f);
        // Plain tinted panels over artwork: GlassPanel darkens a card over the picture, GlassHigh is a light
        // translucent disc. (The TV's NUI has no backdrop blur, so "glass" is translucent fills.)
        public Color GlassPanel = new Color(0.10f, 0.11f, 0.14f, 0.34f);
        public Color GlassHigh = new Color(1f, 1f, 1f, 0.16f);

        public float TitleSize = 34f;
        public float BodySize = 24f;
        public float SmallSize = 19f;

        public int CardRadius = 14;    // soft corners (no sharp edges)
        public int PanelRadius = 20;

        // Panels (settings / account cards, dialogs) and pills (buttons, selected entries), resting and lit.
        // All null: flat rounded fills in the theme's colours.
        public SurfaceSpec PanelSurface, PanelLitSurface, PillSurface, PillLitSurface;

        // Card looks by name. A layout asks for "classic" (default), "glass" or "featured"; Theme.Card falls back to
        // classic for a name the theme does not define.
        public Dictionary<string, CardSpec> Cards = new Dictionary<string, CardSpec>
        {
            ["classic"] = CardSpec.Classic(),
            ["glass"] = CardSpec.Glass(),
            ["featured"] = CardSpec.Featured(),
        };

        // A theme with image surfaces gets the "glass" variant of the hosted screens (Settings, Account, dialogs):
        // transparent page, surfaces instead of flat panels.
        public bool GlassScreens => PanelSurface != null;

        public static ThemeSpec Omega() => new ThemeSpec();

        // Liquid glass: the baked nine-patches from tools/glass-panel.
        public static ThemeSpec Glorious()
        {
            var t = new ThemeSpec();
            t.PanelSurface = new SurfaceSpec(Theme.Image("glass9.9"));
            t.PanelLitSurface = new SurfaceSpec(Theme.Image("glass_lit9.9"));
            t.PillSurface = new SurfaceSpec(Theme.Image("glass_pill9.9"));
            t.PillLitSurface = new SurfaceSpec(Theme.Image("glass_pill_lit9.9"));
            return t;
        }

        private static Color Rgb(int rgb) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
    }
}
