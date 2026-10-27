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
    }

    public class SkipSegment
    {
        public string Category;
        public long StartMs, EndMs;
        public bool Done;   // skipped once (or passed): never re-triggered
    }

    public class FeedPage
    {
        public List<VideoItem> Items = new List<VideoItem>();
        public string Continuation = "";
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
                foreach (object o in Json.Arr(root, "items") ?? new List<object>())
                {
                    page.Items.Add(new VideoItem
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
                    });
                }
                page.Continuation = Json.Str(root, "continuation");
                return page;
            }
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
