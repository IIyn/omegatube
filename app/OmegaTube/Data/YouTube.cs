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

    public class AudioTrack
    {
        public string Id, Name;
        public bool Default;
    }

    public class CaptionTrack
    {
        public string Url, Lang, Name;
        public bool Auto, Translatable;
    }

    public class CaptionCue
    {
        public long StartMs, EndMs;
        public string Text;
    }

    // What the core resolved for the playing video: formats, audio languages, subtitles, details.
    public class PlaybackInfo
    {
        public List<VideoQuality> Qualities = new List<VideoQuality>();   // highest first
        public VideoQuality Current;
        public bool Live;
        public string Title = "", Author = "", Description = "", Views = "", ChannelId = "";
        public List<AudioTrack> AudioTracks = new List<AudioTrack>();     // empty: single-language video
        public string AudioTrack = "";                                    // playing track id
        public List<CaptionTrack> Captions = new List<CaptionTrack>();
        public Dictionary<string, string> Translations = new Dictionary<string, string>();  // code -> name
    }

    // Watch-page details and the account's state for the playing video.
    public class VideoDetails
    {
        public string ChannelId = "", Channel = "", ChannelAvatar = "", Subscribers = "", Date = "";
        public long Likes = -1, Dislikes = -1;      // Return YouTube Dislike estimates
        public string LikeStatus = "";             // LIKE | DISLIKE | INDIFFERENT | "" (signed out)
        public int Subscribed = -1;                // 1 / 0 / -1 unknown
        public string Error;
    }

    public class AccountInfo
    {
        public string Name, Handle, Photo, Error;
    }

    public class FeedPage
    {
        public List<VideoItem> Items = new List<VideoItem>();
        public List<VideoItem> Highlights = new List<VideoItem>();  // subscriptions: YouTube's "most relevant" shelf
        public string Continuation = "";
        public string Title = "";   // playlists, channels
        public string Avatar = "", Subscribers = "";   // channels
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

        public static Task<FeedPage> Channel(string channelId, string continuation = "")
        {
            return Task.Run(() => ToPage(Call(NativeCore.YtChannel(channelId, continuation ?? ""))));
        }

        public static Task<VideoDetails> Details(string videoId)
        {
            return Task.Run(() =>
            {
                object root = Call(NativeCore.YtDetails(videoId));
                return new VideoDetails
                {
                    ChannelId = Json.Str(root, "channelId"), Channel = Json.Str(root, "channel"),
                    ChannelAvatar = Json.Str(root, "channelAvatar"), Subscribers = Json.Str(root, "subscribers"),
                    Date = Json.Str(root, "date"), Likes = (long)Json.Num(root, "likes", -1),
                    Dislikes = (long)Json.Num(root, "dislikes", -1), LikeStatus = Json.Str(root, "likeStatus"),
                    Subscribed = (int)Json.Num(root, "subscribed", -1), Error = Json.Str(root, "error", null),
                };
            });
        }

        // "like" | "dislike" | "none". Returns the error, or null.
        public static Task<string> Rate(string videoId, string rating) =>
            Task.Run(() => Json.Str(Call(NativeCore.YtRate(videoId, rating)), "error", null));

        public static Task<string> Subscribe(string channelId, bool subscribe) =>
            Task.Run(() => Json.Str(Call(NativeCore.YtSubscribe(channelId, subscribe)), "error", null));

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
                page.Avatar = Json.Str(root, "avatar");
                page.Subscribers = Json.Str(root, "subscribers");
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

        // What the player resolved (qualities, audio tracks, captions...). No network call.
        public static PlaybackInfo Playback(IntPtr player)
        {
            object root = Call(NativeCore.PlayerFormats(player));
            var info = new PlaybackInfo
            {
                Live = Json.Bool(root, "live", false),
                Title = Json.Str(root, "title"),
                Author = Json.Str(root, "author"),
                Description = Json.Str(root, "description"),
                Views = Json.Str(root, "views"),
                ChannelId = Json.Str(root, "channelId"),
                AudioTrack = Json.Str(root, "audioTrack"),
            };
            object cur = Json.Obj(root, "current");
            info.Current = cur != null ? ToQuality(cur) : null;
            foreach (object o in Json.Arr(root, "available") ?? new List<object>())
            {
                info.Qualities.Add(ToQuality(o));
            }
            foreach (object o in Json.Arr(root, "audioTracks") ?? new List<object>())
            {
                info.AudioTracks.Add(new AudioTrack { Id = Json.Str(o, "id"), Name = Json.Str(o, "name"), Default = Json.Bool(o, "default") });
            }
            foreach (object o in Json.Arr(root, "captions") ?? new List<object>())
            {
                info.Captions.Add(new CaptionTrack
                {
                    Url = Json.Str(o, "url"), Lang = Json.Str(o, "lang"), Name = Json.Str(o, "name"),
                    Auto = Json.Bool(o, "auto"), Translatable = Json.Bool(o, "translatable"),
                });
            }
            foreach (object o in Json.Arr(root, "translations") ?? new List<object>())
            {
                info.Translations[Json.Str(o, "code")] = Json.Str(o, "name");
            }
            return info;
        }

        // Timed subtitle lines of a caption track, machine-translated when translateTo is set. Null on error.
        public static Task<List<CaptionCue>> Captions(string url, string translateTo = "")
        {
            return Task.Run(() =>
            {
                object root = Call(NativeCore.YtCaptions(url, translateTo ?? ""));
                if (Json.Str(root, "error", null) != null)
                {
                    return null;
                }
                var list = new List<CaptionCue>();
                foreach (object o in Json.Arr(root, "cues") ?? new List<object>())
                {
                    list.Add(new CaptionCue { StartMs = (long)Json.Num(o, "start"), EndMs = (long)Json.Num(o, "end"), Text = Json.Str(o, "text") });
                }
                list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
                return list;
            });
        }

        public static Task<AccountInfo> Account()
        {
            return Task.Run(() =>
            {
                object root = Call(NativeCore.AuthAccount());
                return new AccountInfo
                {
                    Name = Json.Str(root, "name"), Handle = Json.Str(root, "handle"), Photo = Json.Str(root, "photo"),
                    Error = Json.Str(root, "error", null),
                };
            });
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
