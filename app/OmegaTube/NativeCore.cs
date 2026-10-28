using System;
using System.Runtime.InteropServices;

namespace OmegaTube
{
    // P/Invoke bindings to libomegacore.so (see core/include/omega/core.h).
    public static class NativeCore
    {
        private const string Lib = "omegacore";

        [StructLayout(LayoutKind.Sequential)]
        public struct PlayerStats
        {
            public int State;
            public int Eos;
            public long PositionMs;
            public long VideoQueuedMs;
            public long AudioQueuedMs;
            public long VideoSubmittedMs;
            public long AudioSubmittedMs;
            public long LongestStallMs;
            public uint VideoUnderruns;
            public uint AudioUnderruns;
            public uint Stalls;
            public uint Errors;
            public double VideoKbps;
            public double AudioKbps;
            public long DurationMs;
            public int Reconnecting;
            public int Reserved;
        }

        [DllImport(Lib, EntryPoint = "omega_version")]
        private static extern IntPtr VersionPtr();

        [DllImport(Lib, EntryPoint = "omega_selftest")]
        public static extern int SelfTest();

        [DllImport(Lib, EntryPoint = "omega_log_init_udp")]
        public static extern void LogInitUdp(string host, int port);

        [DllImport(Lib, EntryPoint = "omega_log_tail")]
        private static extern int LogTail(byte[] buf, int bufSize, int maxLines);

        [DllImport(Lib, EntryPoint = "omega_player_create")]
        public static extern IntPtr PlayerCreate(IntPtr ecoreWindow, int width, int height, string videoUrl, string audioUrl);

        [DllImport(Lib, EntryPoint = "omega_player_create_youtube")]
        public static extern IntPtr PlayerCreateYouTube(IntPtr ecoreWindow, int width, int height, string videoId, int maxHeight,
                                                        string codecs, long startMs, string audioTrack);

        [DllImport(Lib, EntryPoint = "omega_player_last_error")]
        public static extern IntPtr PlayerLastError();

        [DllImport(Lib, EntryPoint = "omega_player_toggle_pause")]
        public static extern void PlayerTogglePause(IntPtr player);

        [DllImport(Lib, EntryPoint = "omega_player_seek")]
        public static extern int PlayerSeek(IntPtr player, long positionMs);

        [DllImport(Lib, EntryPoint = "omega_player_get_stats")]
        public static extern void PlayerGetStats(IntPtr player, out PlayerStats stats);

        [DllImport(Lib, EntryPoint = "omega_player_destroy")]
        public static extern void PlayerDestroy(IntPtr player);

        [DllImport(Lib, EntryPoint = "omega_player_diagnose")]
        public static extern void PlayerDiagnose(IntPtr ecoreWindow, int width, int height, string videoUrl, string audioUrl);

        [DllImport(Lib, EntryPoint = "omega_yt_search")]
        private static extern IntPtr YtSearchRaw(byte[] query, byte[] continuation);

        [DllImport(Lib, EntryPoint = "omega_yt_suggest")]
        private static extern IntPtr YtSuggestRaw(byte[] query);

        public static IntPtr YtSearch(string query, string continuation) => YtSearchRaw(Utf8z(query), Utf8z(continuation));

        public static IntPtr YtSuggest(string query) => YtSuggestRaw(Utf8z(query));

        // Managed string -> NUL-terminated UTF-8 (the default ANSI marshalling would mangle non-ASCII).
        private static byte[] Utf8z(string s)
        {
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            Array.Resize(ref raw, raw.Length + 1);
            return raw;
        }

        [DllImport(Lib, EntryPoint = "omega_yt_browse")]
        private static extern IntPtr YtBrowseRaw(byte[] browseId, byte[] continuation);

        public static IntPtr YtBrowse(string browseId, string continuation) => YtBrowseRaw(Utf8z(browseId), Utf8z(continuation));

        [DllImport(Lib, EntryPoint = "omega_init")]
        private static extern void InitRaw(byte[] dataDir);

        public static void Init(string dataDir) => InitRaw(Utf8z(dataDir));

        [DllImport(Lib, EntryPoint = "omega_config_refresh")]
        public static extern IntPtr ConfigRefresh();

        [DllImport(Lib, EntryPoint = "omega_set_locale")]
        private static extern void SetLocaleRaw(byte[] language, byte[] region);

        public static void SetLocale(string language, string region) => SetLocaleRaw(Utf8z(language), Utf8z(region));

        [DllImport(Lib, EntryPoint = "omega_update_check")]
        public static extern IntPtr UpdateCheck();

        [DllImport(Lib, EntryPoint = "omega_auth_status")]
        public static extern IntPtr AuthStatus();

        [DllImport(Lib, EntryPoint = "omega_auth_begin")]
        public static extern IntPtr AuthBegin();

        [DllImport(Lib, EntryPoint = "omega_auth_poll")]
        private static extern IntPtr AuthPollRaw(byte[] deviceCode);

        public static IntPtr AuthPoll(string deviceCode) => AuthPollRaw(Utf8z(deviceCode));

        [DllImport(Lib, EntryPoint = "omega_auth_sign_out")]
        public static extern void AuthSignOut();

        [DllImport(Lib, EntryPoint = "omega_qr_encode")]
        private static extern IntPtr QrEncodeRaw(byte[] text);

        public static IntPtr QrEncode(string text) => QrEncodeRaw(Utf8z(text));

        [DllImport(Lib, EntryPoint = "omega_yt_playlist")]
        private static extern IntPtr YtPlaylistRaw(byte[] videoId, byte[] playlistId);

        public static IntPtr YtPlaylist(string videoId, string playlistId) => YtPlaylistRaw(Utf8z(videoId), Utf8z(playlistId));

        [DllImport(Lib, EntryPoint = "omega_yt_playlist_page")]
        private static extern IntPtr YtPlaylistPageRaw(byte[] playlistId, byte[] continuation);

        public static IntPtr YtPlaylistPage(string playlistId, string continuation) => YtPlaylistPageRaw(Utf8z(playlistId), Utf8z(continuation));

        [DllImport(Lib, EntryPoint = "omega_yt_related")]
        private static extern IntPtr YtRelatedRaw(byte[] videoId);

        public static IntPtr YtRelated(string videoId) => YtRelatedRaw(Utf8z(videoId));

        [DllImport(Lib, EntryPoint = "omega_player_formats")]
        public static extern IntPtr PlayerFormats(IntPtr player);

        [DllImport(Lib, EntryPoint = "omega_player_set_rate")]
        public static extern int PlayerSetRate(IntPtr player, double rate);

        [DllImport(Lib, EntryPoint = "omega_yt_captions")]
        private static extern IntPtr YtCaptionsRaw(byte[] url, byte[] translateTo);

        public static IntPtr YtCaptions(string url, string translateTo) => YtCaptionsRaw(Utf8z(url), Utf8z(translateTo));

        [DllImport(Lib, EntryPoint = "omega_yt_details")]
        private static extern IntPtr YtDetailsRaw(byte[] videoId);

        public static IntPtr YtDetails(string videoId) => YtDetailsRaw(Utf8z(videoId));

        [DllImport(Lib, EntryPoint = "omega_yt_rate")]
        private static extern IntPtr YtRateRaw(byte[] videoId, byte[] rating);

        public static IntPtr YtRate(string videoId, string rating) => YtRateRaw(Utf8z(videoId), Utf8z(rating));

        [DllImport(Lib, EntryPoint = "omega_yt_subscribe")]
        private static extern IntPtr YtSubscribeRaw(byte[] channelId, int subscribe);

        public static IntPtr YtSubscribe(string channelId, bool subscribe) => YtSubscribeRaw(Utf8z(channelId), subscribe ? 1 : 0);

        [DllImport(Lib, EntryPoint = "omega_yt_channel")]
        private static extern IntPtr YtChannelRaw(byte[] channelId, byte[] continuation);

        public static IntPtr YtChannel(string channelId, string continuation) => YtChannelRaw(Utf8z(channelId), Utf8z(continuation));

        [DllImport(Lib, EntryPoint = "omega_auth_account")]
        public static extern IntPtr AuthAccount();

        [DllImport(Lib, EntryPoint = "omega_sb_segments")]
        private static extern IntPtr SbSegmentsRaw(byte[] videoId);

        public static IntPtr SbSegments(string videoId) => SbSegmentsRaw(Utf8z(videoId));

        [DllImport(Lib, EntryPoint = "omega_free")]
        public static extern void Free(IntPtr p);

        // Native UTF-8 C string -> managed string.
        public static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero)
            {
                return "";
            }
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0)
            {
                len++;
            }
            var bytes = new byte[len];
            Marshal.Copy(p, bytes, 0, len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public static string Version => Marshal.PtrToStringAnsi(VersionPtr());

        public static string RecentLog(int maxLines)
        {
            var buf = new byte[8192];
            int n = LogTail(buf, buf.Length, maxLines);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
    }
}
