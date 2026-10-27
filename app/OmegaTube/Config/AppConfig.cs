using System;
using System.Collections.Generic;
using System.IO;
using OmegaTube.Util;

namespace OmegaTube.Config
{
    // A home screen row: a YouTube search, or one of the signed-in feeds.
    public class HomeRow
    {
        public const string Search = "search";
        public const string Recommended = "recommended";
        public const string Subscriptions = "subscriptions";
        public const string History = "history";

        public string Type = Search;
        public string Title;
        public string Query;   // search only

        public bool NeedsAccount => Type != Search;

        public string BrowseId => Type == Recommended ? "FEwhat_to_watch"
                                : Type == Subscriptions ? "FEsubscriptions"
                                : Type == History ? "FEhistory" : null;
    }

    public class PlayerOptions
    {
        public int SeekStepSeconds = 10;    // Left/Right on the remote
        public int OsdTimeoutSeconds = 4;   // control bar auto-hide
        public bool ShowClock = true;       // wall clock in the control bar
        public bool ShowStats = false;      // technical overlay (buffer, bitrate, stalls)
        public int MaxHeight = 1080;
    }

    public enum SkipMode { Auto, Manual, Off }

    // SponsorBlock categories (https://wiki.sponsor.ajay.app/w/Segment_Categories), French labels and colours.
    public class SponsorCategory
    {
        public string Id, Label;
        public Tizen.NUI.Color Color;
        public SkipMode DefaultMode;

        public static readonly SponsorCategory[] All =
        {
            new SponsorCategory { Id = "sponsor", Label = "Sponsor", Color = new Tizen.NUI.Color(0f, 0.83f, 0f, 1f), DefaultMode = SkipMode.Auto },
            new SponsorCategory { Id = "selfpromo", Label = "Autopromo", Color = new Tizen.NUI.Color(1f, 1f, 0f, 1f), DefaultMode = SkipMode.Auto },
            new SponsorCategory { Id = "interaction", Label = "Rappel d'abonnement", Color = new Tizen.NUI.Color(0.8f, 0f, 1f, 1f), DefaultMode = SkipMode.Auto },
            new SponsorCategory { Id = "intro", Label = "Intro", Color = new Tizen.NUI.Color(0f, 1f, 1f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "outro", Label = "Fin / crédits", Color = new Tizen.NUI.Color(0.01f, 0.01f, 0.93f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "preview", Label = "Aperçu / récap", Color = new Tizen.NUI.Color(0f, 0.56f, 0.84f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "filler", Label = "Hors-sujet", Color = new Tizen.NUI.Color(0.45f, 0f, 1f, 1f), DefaultMode = SkipMode.Manual },
            new SponsorCategory { Id = "music_offtopic", Label = "Passage non musical", Color = new Tizen.NUI.Color(1f, 0.6f, 0f, 1f), DefaultMode = SkipMode.Off },
        };

        public static SponsorCategory Find(string id) => Array.Find(All, c => c.Id == id);
    }

    // User settings, persisted as JSON in the app data folder (config.json). Unknown keys are ignored,
    // missing keys take the defaults below, so the file can be edited by hand.
    public class AppConfig
    {
        public List<HomeRow> HomeRows = new List<HomeRow>();
        public PlayerOptions Player = new PlayerOptions();
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

        // First sign-in: put the account feeds on top of the home screen (once; the user can reorder/remove them).
        public void AddAccountRows()
        {
            if (HomeRows.Exists(r => r.NeedsAccount))
            {
                return;
            }
            HomeRows.Insert(0, new HomeRow { Type = HomeRow.Subscriptions, Title = "Abonnements" });
            HomeRows.Insert(0, new HomeRow { Type = HomeRow.Recommended, Title = "Recommandations" });
            Save();
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
                new HomeRow { Title = "Musique", Query = "musique" },
                new HomeRow { Title = "Jeux vidéo", Query = "jeux vidéo" },
                new HomeRow { Title = "Science & tech", Query = "science technologie" },
                new HomeRow { Title = "Documentaires", Query = "documentaire" },
                new HomeRow { Title = "Lofi", Query = "lofi hip hop" },
            };
        }

        private void FromJson(object root)
        {
            HomeRows.Clear();
            foreach (object r in Json.Arr(root, "homeRows") ?? new List<object>())
            {
                string type = Json.Str(r, "type", HomeRow.Search);
                string query = Json.Str(r, "query");
                if (type != HomeRow.Search || query.Length > 0)
                {
                    HomeRows.Add(new HomeRow { Type = type, Title = Json.Str(r, "title", query), Query = query });
                }
            }
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
            }
        }

        private Dictionary<string, object> ToJson()
        {
            var rows = new List<object>();
            foreach (HomeRow r in HomeRows)
            {
                rows.Add(new Dictionary<string, object> { ["type"] = r.Type, ["title"] = r.Title, ["query"] = r.Query ?? "" });
            }
            var cats = new Dictionary<string, object>();
            foreach (SponsorCategory c in SponsorCategory.All)
            {
                cats[c.Id] = ModeForRaw(c).ToString().ToLowerInvariant();
            }
            return new Dictionary<string, object>
            {
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
                },
            };
        }
    }
}
