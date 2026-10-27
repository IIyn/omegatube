using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OmegaTube.Util;

namespace OmegaTube.Data
{
    public class VideoItem
    {
        public string Id, Title, Channel, Duration, Views, Published, Thumbnail;
        public bool Live;
        public int WatchedPercent;   // account progress reported by YouTube (0 when unknown)
        // Playlist / mix card: plays Id within PlaylistId. Videos of a playlist keep Kind "video" + PlaylistId.
        public string PlaylistId = "";
        public string Kind = "video";   // video | playlist | mix
        public bool IsList => Kind == "playlist" || Kind == "mix";
    }

    public class SkipSegment
    {
        public string Category;
        public long StartMs, EndMs;
        public bool Done;   // skipped once (or passed): never re-triggered
    }

    // One entry of the quality panel: the format a height cap selects.
    public class VideoQuality
    {
        public int Width, Height, Fps;
        public string Codec;   // avc1 | vp9 | av01
    }

    public class FeedPage
    {
        public List<VideoItem> Items = new List<VideoItem>();
        public List<VideoItem> Highlights = new List<VideoItem>();  // subscriptions: YouTube's "most relevant" shelf
        public string Continuation = "";
        public string Title = "";   // playlists
        public string Error;
    }

    // Async wrappers over the native core's YouTube data calls (network runs on the thread pool,
    // continuations come back on the UI thread through the Tizen synchronization context).
    public static class YouTube
    {
        public static Task<FeedPage> Search(string query, string continuation = "")
        {
            return Task.Run(() => ToPage(Call(NativeCore.YtSearch(query, continuation ?? ""))));
        }

        // The videos after videoId in a playlist / mix (page.Title: the list's name).
        public static Task<FeedPage> Playlist(string videoId, string playlistId)
        {
            return Task.Run(() => ToPage(Call(NativeCore.YtPlaylist(videoId, playlistId))));
        }

        public static Task<FeedPage> Related(string videoId)
        {
            return Task.Run(() => ToPage(Call(NativeCore.YtRelated(videoId))));
        }

        // Signed-in feeds: FEwhat_to_watch (recommended), FEsubscriptions, FEhistory.
        public static Task<FeedPage> Browse(string browseId, string continuation = "")
        {
            return Task.Run(() => ToPage(Call(NativeCore.YtBrowse(browseId, continuation ?? ""))));
        }

        private static FeedPage ToPage(object root)
        {
            {
                var page = new FeedPage();
                page.Error = Json.Str(root, "error", null);
                page.Items = ToItems(root, "items");
                page.Highlights = ToItems(root, "highlights");
                page.Continuation = Json.Str(root, "continuation");
                page.Title = Json.Str(root, "title");
                return page;
            }
        }

        // Feed items as the core's JSON (and the feed cache) writes them.
        public static List<VideoItem> ToItems(object root, string key)
        {
            var items = new List<VideoItem>();
            foreach (object o in Json.Arr(root, key) ?? new List<object>())
            {
                items.Add(new VideoItem
                {
                    Id = Json.Str(o, "id"),
                    Title = Json.Str(o, "title"),
                    Channel = Json.Str(o, "channel"),
                    Duration = Json.Str(o, "duration"),
                    Views = Json.Str(o, "views"),
                    Published = Json.Str(o, "published"),
                    Thumbnail = Json.Str(o, "thumbnail"),
                    Live = Json.Bool(o, "live"),
                    WatchedPercent = (int)Json.Num(o, "watched"),
                    PlaylistId = Json.Str(o, "playlistId"),
                    Kind = Json.Str(o, "kind", "video"),
                });
            }
            return items;
        }

        public static Task<List<string>> Suggest(string query)
        {
            return Task.Run(() =>
            {
                var list = new List<string>();
                foreach (object o in Json.Arr(Call(NativeCore.YtSuggest(query)), "suggestions") ?? new List<object>())
                {
                    if (o is string s)
                    {
                        list.Add(s);
                    }
                }
                return list;
            });
        }

        public static Task<List<SkipSegment>> SponsorSegments(string videoId)
        {
            return Task.Run(() =>
            {
                var list = new List<SkipSegment>();
                foreach (object o in Json.Arr(Call(NativeCore.SbSegments(videoId)), "segments") ?? new List<object>())
                {
                    list.Add(new SkipSegment
                    {
                        Category = Json.Str(o, "category"),
                        StartMs = (long)(Json.Num(o, "start") * 1000),
                        EndMs = (long)(Json.Num(o, "end") * 1000),
                    });
                }
                list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
                return list;
            });
        }

        public static bool SignedIn() => Json.Bool(Call(NativeCore.AuthStatus()), "signedIn");

        // {deviceCode, userCode, verificationUrl, qrUrl, interval, expiresIn} or {error}
        public static Task<object> BeginSignIn() => Task.Run(() => Call(NativeCore.AuthBegin()));

        // "pending" | "slow_down" | "done" | "denied" | "expired" | "failed"
        public static Task<string> PollSignIn(string deviceCode) =>
            Task.Run(() => Json.Str(Call(NativeCore.AuthPoll(deviceCode)), "status", "failed"));

        public static void SignOut() => NativeCore.AuthSignOut();

        // QR modules as rows of '0'/'1'.
        public static List<string> Qr(string text)
        {
            var rows = new List<string>();
            foreach (object o in Json.Arr(Call(NativeCore.QrEncode(text)), "rows") ?? new List<object>())
            {
                rows.Add(o as string ?? "");
            }
            return rows;
        }

        // Qualities the core can play for this player (highest first) and the one playing. No network call.
        public static List<VideoQuality> Qualities(IntPtr player, out VideoQuality current, out bool live, out string title)
        {
            object root = Call(NativeCore.PlayerFormats(player));
            live = Json.Bool(root, "live", false);
            title = Json.Str(root, "title");
            object cur = Json.Obj(root, "current");
            current = cur != null ? ToQuality(cur) : null;
            var list = new List<VideoQuality>();
            foreach (object o in Json.Arr(root, "available") ?? new List<object>())
            {
                list.Add(ToQuality(o));
            }
            return list;
        }

        private static VideoQuality ToQuality(object o)
        {
            return new VideoQuality { Width = (int)Json.Num(o, "width"), Height = (int)Json.Num(o, "height"), Fps = (int)Json.Num(o, "fps"), Codec = Json.Str(o, "codec") };
        }

        public static object Call(IntPtr json)
        {
            try
            {
                return Json.Parse(NativeCore.Utf8(json));
            }
            catch (Exception e)
            {
                return new Dictionary<string, object> { ["error"] = e.Message };
            }
            finally
            {
                NativeCore.Free(json);
            }
        }
    }
}
