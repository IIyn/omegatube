using System;
using System.Collections.Generic;
using System.IO;
using OmegaTube.Util;

namespace OmegaTube.Data
{
    // Small JSON files in the app data folder: first page of each home row (instant start-up) and the
    // playback position of recently watched videos (resume + progress bars). Failures are non-fatal.
    public static class LocalStore
    {
        private const int MaxPositions = 300;

        private static string dir;
        private static Dictionary<string, object> positions;

        public static void Init(string dataDir)
        {
            dir = dataDir;
            positions = ReadJson("positions.json") as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        // ---- feed cache ----

        public static FeedPage LoadFeed(string key)
        {
            object root = ReadJson(FeedFile(key));
            if (root == null)
            {
                return null;
            }
            var page = new FeedPage { Items = YouTube.ToItems(root, "items"), Highlights = YouTube.ToItems(root, "highlights") };
            foreach (object s in Json.Arr(root, "shelves") ?? new List<object>())
            {
                page.Shelves.Add(new Shelf { Title = Json.Str(s, "title"), Items = YouTube.ToItems(s, "items") });
            }
            return page.Items.Count > 0 || page.Shelves.Count > 0 ? page : null;
        }

        public static void SaveFeed(string key, FeedPage page)
        {
            var shelves = new List<object>();
            foreach (Shelf s in page.Shelves)
            {
                shelves.Add(new Dictionary<string, object> { ["title"] = s.Title, ["items"] = ItemsJson(s.Items) });
            }
            WriteJson(FeedFile(key), new Dictionary<string, object>
            {
                ["items"] = ItemsJson(page.Items), ["highlights"] = ItemsJson(page.Highlights), ["shelves"] = shelves,
            });
        }

        private static List<object> ItemsJson(List<VideoItem> list)
        {
            var items = new List<object>();
            foreach (VideoItem v in list)
            {
                items.Add(new Dictionary<string, object>
                {
                    ["id"] = v.Id, ["title"] = v.Title, ["channel"] = v.Channel, ["duration"] = v.Duration,
                    ["views"] = v.Views, ["published"] = v.Published, ["thumbnail"] = v.Thumbnail, ["live"] = v.Live,
                    ["watched"] = v.WatchedPercent, ["playlistId"] = v.PlaylistId, ["kind"] = v.Kind,
                });
            }
            return items;
        }

        private static string FeedFile(string key)
        {
            // Stable short file name for any row key (query text may contain anything).
            uint h = 2166136261;
            foreach (char c in key)
            {
                h = (h ^ c) * 16777619;
            }
            return "feed-" + h.ToString("x8") + ".json";
        }

        // ---- playback positions ----

        // Fraction watched (0..1), or 0 when unknown.
        public static double Progress(string videoId)
        {
            object e = positions != null && positions.TryGetValue(videoId, out object v) ? v : null;
            double dur = Json.Num(e, "dur");
            return dur > 0 ? Math.Min(1, Json.Num(e, "pos") / dur) : 0;
        }

        public static long ResumePosition(string videoId)
        {
            object e = positions != null && positions.TryGetValue(videoId, out object v) ? v : null;
            return (long)Json.Num(e, "pos");
        }

        public static void SavePosition(string videoId, long posMs, long durMs)
        {
            if (positions == null || string.IsNullOrEmpty(videoId) || durMs <= 0)
            {
                return;
            }
            // Nearly finished (or barely started) counts as "start from the beginning" next time,
            // but finished videos keep a full bar.
            positions[videoId] = new Dictionary<string, object>
            {
                ["pos"] = (double)(posMs > durMs - 30000 ? durMs : posMs),
                ["dur"] = (double)durMs,
                ["at"] = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (positions.Count > MaxPositions)
            {
                string oldest = null;
                double oldestAt = double.MaxValue;
                foreach (var kv in positions)
                {
                    double at = Json.Num(kv.Value, "at");
                    if (at < oldestAt)
                    {
                        oldestAt = at;
                        oldest = kv.Key;
                    }
                }
                positions.Remove(oldest);
            }
            WriteJson("positions.json", positions);
        }

        private static object ReadJson(string name)
        {
            try
            {
                string path = Path.Combine(dir, name);
                return File.Exists(path) ? Json.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteJson(string name, object value)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, name), Json.Write(value));
            }
            catch (Exception e)
            {
                Tizen.Log.Error("OmegaTube", name + ": " + e.Message);
            }
        }
    }
}
