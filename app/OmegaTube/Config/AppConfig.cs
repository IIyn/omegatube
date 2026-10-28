using System;
using System.Collections.Generic;
using System.IO;
using OmegaTube.Util;
using static OmegaTube.I18n;

namespace OmegaTube.Config
{
    // A section of the rail (Musique, Jeux vidéo...): a YouTube search shown as a grid. Home, Subscriptions
    // and History are fixed rail entries backed by the account.
    public class HomeRow
    {
        public string Title;
        public string Query;
        public string Icon = "trending";   // res/icons/<name>.png
        public bool Visible = true;

        // Picks a SmartTube icon from the section name when none was chosen.
        public static string GuessIcon(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            if (t.Contains("musi") || t.Contains("músi") || t.Contains("lofi")) return "music";
            if (t.Contains("jeu") || t.Contains("gam") || t.Contains("jueg")) return "gaming";
            if (t.Contains("sport") || t.Contains("foot") || t.Contains("deport") || t.Contains("fútbol")) return "sports";
            if (t.Contains("actu") || t.Contains("info") || t.Contains("news") || t.Contains("notic") || t.Contains("docu")) return "news";
            if (t.Contains("direct") || t.Contains("live")) return "live";
            if (t.Contains("enfant") || t.Contains("kid") || t.Contains("niñ")) return "kids";
            return "trending";
        }
    }

    public class PlayerOptions
    {
        public int SeekStepSeconds = 10;    // Left/Right on the remote
        public int OsdTimeoutSeconds = 4;   // control bar auto-hide
        public bool ShowClock = true;       // wall clock in the control bar
        public bool ShowStats = false;      // technical overlay (buffer, bitrate, stalls)
        public int MaxHeight = 1080;
        // Video codec preference for the core ("avc1" H.264, "vp9", "av01" AV1). Above 1080p YouTube has no H.264.
        public string Codecs = "avc1,vp9";
        public bool Autoplay = true;        // at the end of a video, play the first suggestion (after a countdown)
        // Subtitles: language turned on automatically ("" off). Choosing a track in the player updates it.
        public string CaptionLang = "";
        public bool CaptionTranslate = true;   // no track in that language: machine-translate one
        public int CaptionSize = 1;            // index in CaptionSizes
        public int CaptionBackground = 2;      // index in CaptionBackgrounds
        // Audio language for dubbed videos ("" = the original track).
        public string AudioLang = "";

        public static readonly float[] CaptionSizes = { 32f, 40f, 50f, 62f };
        public static readonly string[] CaptionSizeLabels = { "Small", "Medium", "Large", "Extra large" };
        public static readonly float[] CaptionBackgrounds = { 0f, 0.45f, 0.75f, 1f };
        public static readonly string[] CaptionBackgroundLabels = { "None (outline)", "Light", "Dark", "Opaque" };
    }

    public enum SkipMode { Auto, Manual, Off }

    // Languages offered for subtitles / dubbed audio (French names), by ISO 639-1 code.
    public static class Languages
    {
        public static readonly string[] Codes = { "fr", "en", "es", "de", "it", "pt", "nl", "ar", "ja", "ko", "zh", "ru", "tr", "pl" };
        private static readonly string[] Names =
        {
            "French", "English", "Spanish", "German", "Italian", "Portuguese", "Dutch", "Arabic", "Japanese",
            "Korean", "Chinese", "Russian", "Turkish", "Polish",
        };

        public static string Name(string code)
        {
            int i = Array.IndexOf(Codes, Base(code));
            return i >= 0 ? T(Names[i]) : code;
        }

        // "fr-FR" / "fr.3" -> "fr"
        public static string Base(string code)
        {
            code = code ?? "";
            int cut = code.IndexOfAny(new[] { '-', '.', '_' });
            return (cut >= 0 ? code.Substring(0, cut) : code).ToLowerInvariant();
        }
    }

    // SponsorBlock categories (https://wiki.sponsor.ajay.app/w/Segment_Categories), French labels and colours.
    public class SponsorCategory
    {
        public string Id, Label;
        public Tizen.NUI.Color Color;
        public SkipMode DefaultMode;

        public static readonly SponsorCategory[] All =
        {
            new SponsorCategory { Id = "sponsor", Label = "Sponsor", Color = new Tizen.NUI.Color(0f, 0.83f, 0f, 1f), DefaultMode = SkipMode.Auto },
            new SponsorCategory { Id = "selfpromo", Label = "Self-promotion", Color = new Tizen.NUI.Color(1f, 1f, 0f, 1f), DefaultMode = SkipMode.Auto },
            new SponsorCategory { Id = "interaction", Label = "Interaction reminder", Color = new Tizen.NUI.Color(0.8f, 0f, 1f, 1f), DefaultMode = SkipMode.Auto },
            new SponsorCategory { Id = "intro", Label = "Intro", Color = new Tizen.NUI.Color(0f, 1f, 1f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "outro", Label = "Endcards / credits", Color = new Tizen.NUI.Color(0.01f, 0.01f, 0.93f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "preview", Label = "Preview / recap", Color = new Tizen.NUI.Color(0f, 0.56f, 0.84f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "filler", Label = "Filler / tangent", Color = new Tizen.NUI.Color(0.45f, 0f, 1f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "music_offtopic", Label = "Non-music section", Color = new Tizen.NUI.Color(1f, 0.6f, 0f, 1f), DefaultMode = SkipMode.Off },
        };

        public static SponsorCategory Find(string id) => Array.Find(All, c => c.Id == id);
    }

    // User settings, persisted as JSON in the app data folder (config.json). Unknown keys are ignored,
    // missing keys take the defaults below, so the file can be edited by hand.
    public class AppConfig
    {
        public List<HomeRow> HomeRows = new List<HomeRow>();
        public PlayerOptions Player = new PlayerOptions();
        public string UiLanguage = "en"; // app texts (I18n): en | fr | es
        public string Language = "fr";   // InnerTube hl
        public string Region = "FR";     // InnerTube gl
        public bool CheckUpdates = true;
        public bool SponsorBlockEnabled = true;
        public Dictionary<string, SkipMode> SkipModes = new Dictionary<string, SkipMode>();

        public SkipMode ModeFor(string category)
        {
            if (!SponsorBlockEnabled)
            {
                return SkipMode.Off;
            }
            if (SkipModes.TryGetValue(category, out SkipMode m))
            {
                return m;
            }
            SponsorCategory c = SponsorCategory.Find(category);
            return c != null ? c.DefaultMode : SkipMode.Off;
        }

        private string path;

        public static AppConfig Load(string dataDir)
        {
            var cfg = new AppConfig { path = Path.Combine(dataDir, "config.json") };
            try
            {
                if (File.Exists(cfg.path))
                {
                    cfg.FromJson(Json.Parse(File.ReadAllText(cfg.path)));
                    if (cfg.HomeRows.Count > 0)
                    {
                        return cfg;
                    }
                }
            }
            catch (Exception e)
            {
                Tizen.Log.Error("OmegaTube", "config.json ignored: " + e.Message);
            }
            cfg.HomeRows = DefaultRows();
            return cfg;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(path, Json.Write(ToJson()));
            }
            catch (Exception e)
            {
                Tizen.Log.Error("OmegaTube", "config.json save failed: " + e.Message);
            }
        }

        public SkipMode ModeForRaw(SponsorCategory c) => SkipModes.TryGetValue(c.Id, out SkipMode m) ? m : c.DefaultMode;

        private static List<HomeRow> DefaultRows()
        {
            return new List<HomeRow>
            {
                new HomeRow { Title = T("Music"), Query = T("music"), Icon = "music" },
                new HomeRow { Title = T("Gaming"), Query = T("video games"), Icon = "gaming" },
                new HomeRow { Title = T("Science & tech"), Query = T("science technology"), Icon = "trending" },
                new HomeRow { Title = T("Documentaries"), Query = T("documentary"), Icon = "news" },
                new HomeRow { Title = "Lofi", Query = "lofi hip hop", Icon = "music" },
            };
        }

        private void FromJson(object root)
        {
            HomeRows.Clear();
            foreach (object r in Json.Arr(root, "homeRows") ?? new List<object>())
            {
                // Older configs also listed account feeds here: they are fixed rail entries now.
                string type = Json.Str(r, "type", "search");
                string query = Json.Str(r, "query");
                if (type != "search" || query.Length == 0)
                {
                    continue;
                }
                string title = Json.Str(r, "title", query);
                HomeRows.Add(new HomeRow
                {
                    Title = title,
                    Query = query,
                    Icon = Json.Str(r, "icon", HomeRow.GuessIcon(title + " " + query)),
                    Visible = Json.Bool(r, "visible", true),
                });
            }
            UiLanguage = Json.Str(root, "uiLanguage", UiLanguage);
            if (Array.IndexOf(I18n.Codes, UiLanguage) < 0)
            {
                UiLanguage = "en";
            }
            I18n.Lang = UiLanguage;
            Language = Json.Str(root, "language", Language);
            Region = Json.Str(root, "region", Region);
            CheckUpdates = Json.Bool(root, "checkUpdates", CheckUpdates);
            object sb = Json.Obj(root, "sponsorBlock");
            if (sb != null)
            {
                SponsorBlockEnabled = Json.Bool(sb, "enabled", true);
                foreach (SponsorCategory c in SponsorCategory.All)
                {
                    string m = Json.Str(Json.Obj(sb, "categories"), c.Id, null);
                    if (m != null && Enum.TryParse(m, true, out SkipMode mode))
                    {
                        SkipModes[c.Id] = mode;
                    }
                }
            }
            object p = Json.Obj(root, "player");
            if (p != null)
            {
                Player.SeekStepSeconds = Math.Max(1, (int)Json.Num(p, "seekStepSeconds", Player.SeekStepSeconds));
                Player.OsdTimeoutSeconds = Math.Max(1, (int)Json.Num(p, "osdTimeoutSeconds", Player.OsdTimeoutSeconds));
                Player.ShowClock = Json.Bool(p, "showClock", Player.ShowClock);
                Player.ShowStats = Json.Bool(p, "showStats", Player.ShowStats);
                Player.MaxHeight = (int)Json.Num(p, "maxHeight", Player.MaxHeight);
                Player.Codecs = Json.Str(p, "codecs", Player.Codecs);
                Player.Autoplay = Json.Bool(p, "autoplay", Player.Autoplay);
                Player.CaptionLang = Json.Str(p, "captionLang", Player.CaptionLang);
                Player.CaptionTranslate = Json.Bool(p, "captionTranslate", Player.CaptionTranslate);
                Player.CaptionSize = Math.Max(0, Math.Min(PlayerOptions.CaptionSizes.Length - 1,
                                                          (int)Json.Num(p, "captionSize", Player.CaptionSize)));
                Player.CaptionBackground = Math.Max(0, Math.Min(PlayerOptions.CaptionBackgrounds.Length - 1,
                                                                (int)Json.Num(p, "captionBackground", Player.CaptionBackground)));
                Player.AudioLang = Json.Str(p, "audioLang", Player.AudioLang);
            }
        }

        private Dictionary<string, object> ToJson()
        {
            var rows = new List<object>();
            foreach (HomeRow r in HomeRows)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["type"] = "search", ["title"] = r.Title, ["query"] = r.Query, ["icon"] = r.Icon, ["visible"] = r.Visible,
                });
            }
            var cats = new Dictionary<string, object>();
            foreach (SponsorCategory c in SponsorCategory.All)
            {
                cats[c.Id] = ModeForRaw(c).ToString().ToLowerInvariant();
            }
            return new Dictionary<string, object>
            {
                ["uiLanguage"] = UiLanguage,
                ["language"] = Language,
                ["region"] = Region,
                ["checkUpdates"] = CheckUpdates,
                ["homeRows"] = rows,
                ["sponsorBlock"] = new Dictionary<string, object> { ["enabled"] = SponsorBlockEnabled, ["categories"] = cats },
                ["player"] = new Dictionary<string, object>
                {
                    ["seekStepSeconds"] = Player.SeekStepSeconds,
                    ["osdTimeoutSeconds"] = Player.OsdTimeoutSeconds,
                    ["showClock"] = Player.ShowClock,
                    ["showStats"] = Player.ShowStats,
                    ["maxHeight"] = Player.MaxHeight,
                    ["codecs"] = Player.Codecs,
                    ["autoplay"] = Player.Autoplay,
                    ["captionLang"] = Player.CaptionLang,
                    ["captionTranslate"] = Player.CaptionTranslate,
                    ["captionSize"] = Player.CaptionSize,
                    ["captionBackground"] = Player.CaptionBackground,
                    ["audioLang"] = Player.AudioLang,
                },
            };
        }
    }
}
