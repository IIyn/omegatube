using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using OmegaTube.Ui;
using OmegaTube.Util;
using Color = Tizen.NUI.Color;

namespace OmegaTube.Plugins
{
    // An installed interface plugin: its manifest (plugin.json) and where it came from.
    public sealed class PluginInfo
    {
        public string Id, Name, Version, Author, Description, Homepage, Sdk, Update;
        public string Preview;      // full path of the screenshot inside the package, or null
        public string Dir;
        public string Incompatible; // why this app cannot use it (SDK version), or null
        public string Sha256;       // of the zip it was installed from
        public string Source;       // the URL it was installed from
    }

    // Reads an unpacked plugin (docs/plugins.md) into a ThemeSpec. Everything is data: unknown keys are ignored,
    // wrong types and out-of-range numbers are logged and clamped or left at the default, images must be files of
    // the package (or the app's own images by name). Only plugin.json can make a package unusable.
    public static class PluginPackage
    {
        public const int SdkMajor = 1, SdkMinor = 0;
        public const int MaxJsonBytes = 1 << 20;

        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9]+(\.[a-z0-9_-]+){1,6}$");
        private static readonly Regex VersionPattern = new Regex(@"^\d{1,6}(\.\d{1,6}){0,3}([-+][0-9A-Za-z.-]{1,20})?$");
        private static readonly Regex SdkPattern = new Regex(@"^(\d{1,3})\.(\d{1,3})$");
        private static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9_-]{1,32}$");
        private static readonly Regex AppImagePattern = new Regex(@"^[a-z0-9_.]{1,64}$");
        private static readonly Regex IconSuffixPattern = new Regex(@"^_[a-z0-9_]{1,20}$");
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

        // Ids the app keeps for its built-in interfaces.
        public static bool Reserved(string id) => id == "omega" || id == "glorious";

        // The manifest, or null with the reason in error.
        public static PluginInfo ReadInfo(string dir, out string error)
        {
            error = null;
            object m = ReadJson(dir, "plugin.json", null, out error);
            if (m == null)
            {
                error = error ?? "plugin.json missing";
                return null;
            }
            object strings = ReadJson(dir, "strings.json", null, out _);
            var info = new PluginInfo
            {
                Dir = dir,
                Id = Json.Str(m, "id"),
                Name = Clip(Localized(strings, Json.Str(m, "name")), 40),
                Version = Json.Str(m, "version"),
                Author = Clip(Localized(strings, Json.Str(m, "author")), 40),
                Description = Clip(Localized(strings, Json.Str(m, "description")), 300),
                Homepage = HttpsUrl(Json.Str(m, "homepage")),
                Sdk = Json.Str(m, "sdk"),
                Update = HttpsUrl(Json.Str(m, "update")),
            };
            if (info.Id.Length > 100 || !IdPattern.IsMatch(info.Id) || Reserved(info.Id))
            {
                error = "plugin.json: bad id \"" + Clip(info.Id, 40) + "\" (reverse domain name, e.g. io.github.someone.mytheme)";
                return null;
            }
            if (info.Name.Length == 0)
            {
                error = "plugin.json: name missing";
                return null;
            }
            if (!VersionPattern.IsMatch(info.Version))
            {
                error = "plugin.json: bad version \"" + Clip(info.Version, 20) + "\"";
                return null;
            }
            Match sdk = SdkPattern.Match(info.Sdk);
            if (!sdk.Success)
            {
                error = "plugin.json: bad sdk \"" + Clip(info.Sdk, 10) + "\"";
                return null;
            }
            if (int.Parse(sdk.Groups[1].Value) != SdkMajor || int.Parse(sdk.Groups[2].Value) > SdkMinor)
            {
                info.Incompatible = "sdk " + info.Sdk;
            }
            var reader = new SpecReader(dir, new List<string>());
            info.Preview = reader.Image(m, "preview", null, appImages: false);
            return info;
        }

        // The look a plugin describes, on top of Omega's defaults. Problems go to warnings (never fatal).
        public static ThemeSpec ReadTheme(string dir, List<string> warnings)
        {
            var r = new SpecReader(dir, warnings);
            ThemeSpec t = ThemeSpec.Omega();
            r.ReadThemeJson(t, ReadJson(dir, "theme.json", warnings, out _));
            r.ReadCards(t, ReadJson(dir, "cards.json", warnings, out _));
            r.ReadRail(t, ReadJson(dir, "rail.json", warnings, out _));
            r.ReadHome(t, ReadJson(dir, "home.json", warnings, out _));
            return t;
        }

        private static object ReadJson(string dir, string name, List<string> warnings, out string error)
        {
            error = null;
            string path = Path.Combine(dir, name);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                if (new FileInfo(path).Length > MaxJsonBytes)
                {
                    error = name + ": larger than 1 MB";
                }
                else
                {
                    object root = Json.Parse(File.ReadAllText(path));
                    if (root is Dictionary<string, object>)
                    {
                        return root;
                    }
                    error = name + ": not a JSON object";
                }
            }
            catch (Exception e)
            {
                error = name + ": " + e.Message;
            }
            warnings?.Add(error);
            return null;
        }

        // "@key": the text of strings.json in the app language, then English, then the key itself.
        private static string Localized(object strings, string text)
        {
            if (!text.StartsWith("@"))
            {
                return text.Trim();
            }
            string key = text.Substring(1);
            string s = Json.Str(Json.Obj(strings, I18n.Lang), key, null) ?? Json.Str(Json.Obj(strings, "en"), key, null);
            return (s ?? key).Trim();
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static string HttpsUrl(string s) => s.StartsWith("https://") && s.Length < 500 && s.IndexOfAny(new[] { ' ', '\n', '"' }) < 0 ? s : null;

        // Typed reads of one package's JSON with its rules (ranges, images, colours).
        private sealed class SpecReader
        {
            private readonly string dir;
            private readonly List<string> warnings;
            private ThemeSpec theme;   // colour tokens ("accent") refer to it

            public SpecReader(string dir, List<string> warnings)
            {
                this.dir = Path.GetFullPath(dir).TrimEnd('/') + "/";
                this.warnings = warnings;
            }

            private void Warn(string where, string what) => warnings.Add(where + ": " + what);

            private static bool TryGet(object o, string key, out object value)
            {
                value = null;
                return o is Dictionary<string, object> d && d.TryGetValue(key, out value);
            }

            private int Int(object o, string key, int current, int min, int max, string where)
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (!(v is double n) || double.IsNaN(n))
                {
                    Warn(where + "." + key, "not a number");
                    return current;
                }
                int i = (int)Math.Round(Math.Max(min, Math.Min(max, n)));
                if (n < min || n > max)
                {
                    Warn(where + "." + key, $"{n} out of {min}..{max}, using {i}");
                }
                return i;
            }

            private float Float(object o, string key, float current, float min, float max, string where)
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (!(v is double n) || double.IsNaN(n))
                {
                    Warn(where + "." + key, "not a number");
                    return current;
                }
                float f = (float)Math.Max(min, Math.Min(max, n));
                if (n < min || n > max)
                {
                    Warn(where + "." + key, $"{n} out of {min}..{max}, using {f}");
                }
                return f;
            }

            private bool Bool(object o, string key, bool current, string where)
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (v is bool b)
                {
                    return b;
                }
                Warn(where + "." + key, "not true / false");
                return current;
            }

            private string Choice(object o, string key, string current, string[] allowed, string where)
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (v is string s && Array.IndexOf(allowed, s) >= 0)
                {
                    return s;
                }
                Warn(where + "." + key, "expected one of " + string.Join(", ", allowed));
                return current;
            }

            // "#rrggbb", "#rrggbbaa" or a colour token of theme.json ("accent"). null (JSON null) where allowed: the
            // theme's colour.
            private Color ColorValue(object o, string key, Color current, string where, bool allowNull = false)
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (v == null && allowNull)
                {
                    return null;
                }
                Color c = v is string s ? ParseColor(s) : null;
                if (c == null)
                {
                    Warn(where + "." + key, "expected \"#rrggbb\", \"#rrggbbaa\" or a colour name");
                    return current;
                }
                return c;
            }

            private Color ParseColor(string s)
            {
                if (s.StartsWith("#") && (s.Length == 7 || s.Length == 9))
                {
                    uint value;
                    if (!uint.TryParse(s.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out value))
                    {
                        return null;
                    }
                    if (s.Length == 7)
                    {
                        value = (value << 8) | 0xff;
                    }
                    return new Color(((value >> 24) & 0xff) / 255f, ((value >> 16) & 0xff) / 255f,
                                     ((value >> 8) & 0xff) / 255f, (value & 0xff) / 255f);
                }
                foreach (ColorToken token in Tokens)
                {
                    if (token.Key == s)
                    {
                        Color c = token.Get(theme);
                        return new Color(c.R, c.G, c.B, c.A);
                    }
                }
                return null;
            }

            // An image: a file of the package ("images/glass9.9.png") as a full path, or one of the app's images by
            // name ("round_mask"). JSON null or false: none, where allowNone.
            public string Image(object o, string key, string current, bool allowNone = false, bool appImages = true, string where = "plugin.json")
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (allowNone && (v == null || v is bool b && !b))
                {
                    return null;
                }
                if (!(v is string s) || s.Length == 0 || s.Length > 200)
                {
                    Warn(where + "." + key, "expected an image path");
                    return current;
                }
                string ext = Path.GetExtension(s).ToLowerInvariant();
                if (Array.IndexOf(ImageExtensions, ext) < 0)
                {
                    if (appImages && AppImagePattern.IsMatch(s) && File.Exists(Theme.Image(s)))
                    {
                        return s;
                    }
                    Warn(where + "." + key, "\"" + s + "\" is not a .png, .jpg or .webp file of the package, nor an app image");
                    return current;
                }
                string full = s.StartsWith("/") || s.Contains("\\") || s.Contains("..") ? null : Path.GetFullPath(Path.Combine(dir, s));
                if (full == null || !full.StartsWith(dir) || !File.Exists(full))
                {
                    Warn(where + "." + key, "\"" + s + "\" not found in the package");
                    return current;
                }
                return full;
            }

            public void ReadThemeJson(ThemeSpec t, object root)
            {
                theme = t;
                if (root == null)
                {
                    return;
                }
                object colors = Json.Obj(root, "colors");
                foreach (ColorToken token in Tokens)
                {
                    Color c = ColorValue(colors, token.Key, null, "theme.colors");
                    if (c != null)
                    {
                        token.Set(t, c);
                    }
                }
                object type = Json.Obj(root, "type");
                t.TitleSize = Float(type, "title", t.TitleSize, 16, 80, "theme.type");
                t.BodySize = Float(type, "body", t.BodySize, 14, 60, "theme.type");
                t.SmallSize = Float(type, "small", t.SmallSize, 12, 48, "theme.type");
                object radius = Json.Obj(root, "radius");
                t.CardRadius = Int(radius, "card", t.CardRadius, 0, 60, "theme.radius");
                t.PanelRadius = Int(radius, "panel", t.PanelRadius, 0, 60, "theme.radius");
                object surfaces = Json.Obj(root, "surfaces");
                t.PanelSurface = Surface(surfaces, "panel");
                t.PanelLitSurface = Surface(surfaces, "panelLit");
                t.PillSurface = Surface(surfaces, "pill");
                t.PillLitSurface = Surface(surfaces, "pillLit");
                if (t.PanelSurface != null && (t.PanelLitSurface == null || t.PillSurface == null || t.PillLitSurface == null))
                {
                    Warn("theme.surfaces", "panel needs panelLit, pill and pillLit too: flat surfaces used");
                    t.PanelSurface = t.PanelLitSurface = t.PillSurface = t.PillLitSurface = null;
                }
            }

            // { "image": "...9.9.png" }: a nine-patch surface; anything else (a flat { "color" }): none.
            private SurfaceSpec Surface(object surfaces, string key)
            {
                object s = Json.Obj(surfaces, key);
                string image = Image(s, "image", null, allowNone: true, where: "theme.surfaces." + key);
                return image != null ? new SurfaceSpec(Theme.ImagePath(image)) : null;
            }

            public void ReadCards(ThemeSpec t, object root)
            {
                if (!(root is Dictionary<string, object> looks))
                {
                    return;
                }
                foreach (KeyValuePair<string, object> kv in looks)
                {
                    string where = "cards." + kv.Key;
                    if (!NamePattern.IsMatch(kv.Key) || !(kv.Value is Dictionary<string, object> o))
                    {
                        Warn(where, "a look is a name (letters, digits, - and _) and an object");
                        continue;
                    }
                    if (t.Cards.Count >= 16 && !t.Cards.ContainsKey(kv.Key))
                    {
                        Warn(where, "more than 16 looks");
                        continue;
                    }
                    // A built-in name starts from that look; a new one from "base" (default classic).
                    string baseName = Json.Str(o, "base", t.Cards.ContainsKey(kv.Key) ? kv.Key : "classic");
                    CardSpec c = BuiltInCard(baseName) ?? CardSpec.Classic();
                    string info = Choice(o, "info", c.Info.ToString().ToLowerInvariant(), new[] { "box", "overlay", "none" }, where);
                    c.Info = info == "overlay" ? CardInfoStyle.Overlay : info == "none" ? CardInfoStyle.None : CardInfoStyle.Box;
                    c.FocusScale = Float(o, "focusScale", c.FocusScale, 1f, 1.2f, where);
                    c.Mask = Image(o, "mask", c.Mask, where: where);
                    c.Shadow = Image(o, "shadow", c.Shadow, allowNone: true, where: where);
                    c.ShadowMargin = Int(o, "shadowMargin", c.ShadowMargin, 0, 100, where);
                    c.ShadowDrop = Int(o, "shadowDrop", c.ShadowDrop, 0, 50, where);
                    c.Outline = Image(o, "outline", c.Outline, allowNone: true, where: where);
                    c.Preview = Bool(o, "preview", c.Preview, where);
                    c.BoxHeight = Int(o, "boxHeight", c.BoxHeight, 60, 200, where);
                    c.BoxColor = ColorValue(o, "boxColor", c.BoxColor, where, allowNull: true);
                    c.BoxFocusedColor = ColorValue(o, "boxFocusedColor", c.BoxFocusedColor, where, allowNull: true);
                    c.TextFocusedColor = ColorValue(o, "textFocusedColor", c.TextFocusedColor, where, allowNull: true);
                    c.OverlayPanel = Image(o, "overlayPanel", c.OverlayPanel, where: where);
                    t.Cards[kv.Key] = c;
                }
            }

            private static CardSpec BuiltInCard(string name) =>
                name == "classic" ? CardSpec.Classic() : name == "glass" ? CardSpec.Glass() : name == "featured" ? CardSpec.Featured() : null;

            public void ReadRail(ThemeSpec t, object o)
            {
                if (o == null)
                {
                    return;
                }
                const string where = "rail";
                string mode = Choice(o, "mode", "drawer", new[] { "drawer", "overlay" }, where);
                RailSpec r = mode == "overlay" ? RailSpec.Overlay() : RailSpec.Drawer();
                r.Width = Int(o, "width", r.Width, 60, 300, where);
                r.OpenWidth = Int(o, "openWidth", r.OpenWidth, r.Width, 700, where);
                r.Shift = Int(o, "shift", r.Shift, 0, 100, where);
                r.PanelColor = ColorValue(o, "panelColor", r.PanelColor, where, allowNull: true);
                r.PanelOpenColor = ColorValue(o, "panelOpenColor", r.PanelOpenColor, where, allowNull: true);
                r.CurrentColor = ColorValue(o, "currentColor", r.CurrentColor, where, allowNull: true);
                r.FocusedColor = ColorValue(o, "focusedColor", r.FocusedColor, where, allowNull: true);
                r.Fade = Image(o, "fade", r.Fade, where: where);
                r.FadeTail = Int(o, "fadeTail", r.FadeTail, 0, 600, where);
                r.Chip = Image(o, "chip", r.Chip, where: where);
                r.IdleOpacity = Float(o, "idleOpacity", r.IdleOpacity, 0f, 1f, where);
                r.OpenOpacity = Float(o, "openOpacity", r.OpenOpacity, 0f, 1f, where);
                r.UnderlineColor = ColorValue(o, "underlineColor", r.UnderlineColor, where, allowNull: true);
                if (TryGet(o, "iconSuffix", out object suffix))
                {
                    if (suffix == null || suffix is string s && IconSuffixPattern.IsMatch(s))
                    {
                        r.IconSuffix = (string)suffix;
                    }
                    else
                    {
                        Warn(where + ".iconSuffix", "expected \"_name\" (letters, digits and _)");
                    }
                }
                if (Json.Obj(o, "icons") is Dictionary<string, object> icons)
                {
                    foreach (KeyValuePair<string, object> kv in icons)
                    {
                        string image = NamePattern.IsMatch(kv.Key) ? Image(icons, kv.Key, null, appImages: false, where: where + ".icons") : null;
                        if (image != null)
                        {
                            r.Icons[kv.Key] = image;
                        }
                    }
                }
                t.Rail = r;
            }

            public void ReadHome(ThemeSpec t, object o)
            {
                if (o == null)
                {
                    return;
                }
                const string where = "home";
                var h = new HomeSpec();
                h.Feeds = Choice(o, "feeds", "auto", new[] { "auto", "rows" }, where) == "rows" ? FeedLayout.Rows : FeedLayout.Auto;
                h.Header = Bool(o, "header", h.Header, where);

                object rows = Json.Obj(o, "rows");
                RowsSpec r = h.Rows;
                const string rw = "home.rows";
                r.Top = Int(rows, "top", r.Top, 0, 900, rw);
                r.UnderRail = Bool(rows, "underRail", r.UnderRail, rw);
                r.Inset = Int(rows, "inset", r.Inset, 0, 300, rw);
                r.Card = CardName(t, rows, "card", r.Card, rw);
                r.CardWidth = Int(rows, "cardWidth", r.CardWidth, 160, 900, rw);
                r.CardGap = Int(rows, "cardGap", r.CardGap, 0, 120, rw);
                r.RowHeight = Int(rows, "rowHeight", r.RowHeight, 100, 1000, rw);
                r.FirstCard = CardName(t, rows, "firstCard", r.FirstCard, rw);
                if (r.FirstCard != null)
                {
                    // A different first row: unset sizes follow the other rows.
                    r.FirstCardWidth = Int(rows, "firstCardWidth", r.CardWidth, 160, 900, rw);
                    r.FirstCardGap = Int(rows, "firstCardGap", r.CardGap, 0, 120, rw);
                    r.FirstRowHeight = Int(rows, "firstRowHeight", r.RowHeight, 100, 1000, rw);
                }
                r.FirstRowTop = Int(rows, "firstRowTop", r.FirstRowTop, 0, 900, rw);
                r.FlatFeatured = Int(rows, "flatFeatured", r.FlatFeatured, 0, 50, rw);
                r.GridColumns = Int(rows, "gridColumns", r.GridColumns, 1, 10, rw);
                r.GridCardWidth = Int(rows, "gridCardWidth", r.GridCardWidth, 120, 900, rw);
                r.GridGap = Int(rows, "gridGap", r.GridGap, 0, 120, rw);
                r.GridRowHeight = Int(rows, "gridRowHeight", r.GridRowHeight, 100, 1000, rw);
                r.GridTitledRowHeight = Int(rows, "gridTitledRowHeight", r.GridTitledRowHeight, 100, 1000, rw);

                object grid = Json.Obj(o, "grid");
                h.GridTop = Int(grid, "top", h.GridTop, 0, 1000, "home.grid");
                h.GridLeft = Int(grid, "left", h.GridLeft, -1, 600, "home.grid");

                object hero = Json.Obj(o, "hero");
                if (hero != null)
                {
                    const string hw = "home.hero";
                    var s = new HeroSpec();
                    s.LeftScrim = Image(hero, "leftScrim", s.LeftScrim, where: hw);
                    s.BottomScrim = Image(hero, "bottomScrim", s.BottomScrim, where: hw);
                    s.TitleWidth = Int(hero, "titleWidth", s.TitleWidth, 200, 1800, hw);
                    s.TitleSize = Float(hero, "titleSize", s.TitleSize, 20, 120, hw);
                    s.VideoPreview = Bool(hero, "videoPreview", s.VideoPreview, hw);
                    h.Hero = s;
                }
                h.TintBelowFirstRow = Bool(o, "tintBelowFirstRow", h.TintBelowFirstRow, where);
                object fades = Json.Obj(o, "fades");
                h.FadeLeft = Image(fades, "left", null, allowNone: true, where: "home.fades");
                h.FadeRight = Image(fades, "right", null, allowNone: true, where: "home.fades");
                h.FadeBottom = Image(fades, "bottom", null, allowNone: true, where: "home.fades");
                t.Home = h;
            }

            // A card look name of this theme (classic for an unknown one).
            private string CardName(ThemeSpec t, object o, string key, string current, string where)
            {
                if (!TryGet(o, key, out object v))
                {
                    return current;
                }
                if (v is string s && t.Cards.ContainsKey(s))
                {
                    return s;
                }
                Warn(where + "." + key, "unknown card look");
                return current;
            }
        }

        private sealed class ColorToken
        {
            public string Key;
            public Func<ThemeSpec, Color> Get;
            public Action<ThemeSpec, Color> Set;

            public ColorToken(string key, Func<ThemeSpec, Color> get, Action<ThemeSpec, Color> set)
            {
                Key = key;
                Get = get;
                Set = set;
            }
        }

        // theme.json "colors" keys, also usable as values elsewhere ("underlineColor": "accent").
        private static readonly ColorToken[] Tokens =
        {
            new ColorToken("background", t => t.Background, (t, c) => t.Background = c),
            new ColorToken("surface", t => t.Surface, (t, c) => t.Surface = c),
            new ColorToken("surfaceHigh", t => t.SurfaceHigh, (t, c) => t.SurfaceHigh = c),
            new ColorToken("surfaceFocused", t => t.SurfaceFocused, (t, c) => t.SurfaceFocused = c),
            new ColorToken("cardInfo", t => t.CardInfo, (t, c) => t.CardInfo = c),
            new ColorToken("cardInfoFocused", t => t.CardInfoFocused, (t, c) => t.CardInfoFocused = c),
            new ColorToken("cardTextFocused", t => t.CardTextFocused, (t, c) => t.CardTextFocused = c),
            new ColorToken("accent", t => t.Accent, (t, c) => t.Accent = c),
            new ColorToken("pageTint", t => t.PageTint, (t, c) => t.PageTint = c),
            new ColorToken("text", t => t.Text, (t, c) => t.Text = c),
            new ColorToken("textDim", t => t.TextDim, (t, c) => t.TextDim = c),
            new ColorToken("textMuted", t => t.TextMuted, (t, c) => t.TextMuted = c),
            new ColorToken("scrim", t => t.Scrim, (t, c) => t.Scrim = c),
            new ColorToken("panel", t => t.Panel, (t, c) => t.Panel = c),
            new ColorToken("panelLine", t => t.PanelLine, (t, c) => t.PanelLine = c),
            new ColorToken("success", t => t.Success, (t, c) => t.Success = c),
            new ColorToken("glassButton", t => t.GlassButton, (t, c) => t.GlassButton = c),
            new ColorToken("glassPanel", t => t.GlassPanel, (t, c) => t.GlassPanel = c),
            new ColorToken("glassHigh", t => t.GlassHigh, (t, c) => t.GlassHigh = c),
        };
    }
}
