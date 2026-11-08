using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OmegaTube
{
    // Bindings to libomegacore.so (see core/include/omega/core.h).
    //
    // Loading the core under Samsung UEP (GitHub issue #15)
    // -----------------------------------------------------
    // On Tizen 6.0+ the dynamic linker maps app/OmegaTube/lib/libomegacore.so by path and all is well. On
    // Tizen 5.5 and older, UEP refuses to make an unsigned library's segments executable: a file/fd-backed
    // PROT_EXEC mmap is rejected (confirmed on-device: "failed to map segment"), so both dlopen-by-path and the
    // memfd route fail. UEP does, however, allow anonymous executable memory (also confirmed on-device). So
    // there are three routes, tried in order, and the one that worked is reported in bug reports (LoadRoute):
    //   1. "file path" - dlopen(path)                      (Tizen 6.0+, the fast path, unchanged)
    //   2. "memfd"     - dlopen("/proc/self/fd/<n>")       (a file-free dlopen; works where only path lookup is gated)
    //   3. "custom-elf"- ElfLoader maps the segments into anonymous memory and relocates them by hand
    // Because the custom route has no OS library handle, every native function is called through a function
    // pointer resolved either from the dlopen handle (dlsym) or from the custom image, so all three routes share
    // one set of bindings below.
    public static class NativeCore
    {
        private const string Lib = "omegacore";

        private const int RtldNow = 2;
        private const uint MfdCloexec = 1;
        private const int ProtRead = 1, ProtWrite = 2, ProtExec = 4, MapPrivate = 2, MapAnon = 0x20;

        // glibc only grew a memfd_create() wrapper in 2.27; Tizen 5.5 ships glibc 2.24, so a direct
        // [DllImport("libc", "memfd_create")] would throw EntryPointNotFoundException there. The kernel syscall
        // exists since Linux 3.17, so go through libc's syscall(). Every argument is IntPtr (native word), not
        // long: glibc's `long int syscall(long int, ...)` is 32-bit on armv7, so a 64-bit C# long would occupy
        // two registers and shift the later args into the wrong slots (seen as EINVAL on the TV, issue #15).
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern IntPtr syscall(IntPtr number, byte[] name, IntPtr flags);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr write(int fd, IntPtr buf, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
        private static extern IntPtr Mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

        [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
        private static extern int Mprotect(IntPtr addr, UIntPtr len, int prot);

        [DllImport("libc", EntryPoint = "munmap")]
        private static extern int Munmap(IntPtr addr, UIntPtr len);

        [DllImport("libdl.so.2", EntryPoint = "dlopen", SetLastError = true)]
        private static extern IntPtr DlOpen(string file, int mode);

        [DllImport("libdl.so.2", EntryPoint = "dlsym", SetLastError = true)]
        private static extern IntPtr DlSym(IntPtr handle, string name);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        private static extern IntPtr DlError();

        private static int MemfdCreate(string name, uint flags)
        {
            int nr;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.Arm: nr = 385; break;    // armv7 EABI (the TV)
                case Architecture.Arm64: nr = 279; break;
                case Architecture.X64: nr = 319; break;
                case Architecture.X86: nr = 356; break;
                default: throw new PlatformNotSupportedException("memfd_create: unknown arch " + RuntimeInformation.ProcessArchitecture);
            }
            byte[] raw = System.Text.Encoding.ASCII.GetBytes(name ?? "");
            Array.Resize(ref raw, raw.Length + 1);  // NUL-terminated
            return (int)(long)syscall((IntPtr)nr, raw, (IntPtr)flags);
        }

        // "file path", "memfd", "custom-elf", or the failure detail - shown in bug reports so a Tizen 5.5 owner
        // can confirm which route the fix used (issue #15).
        public static string LoadRoute { get; private set; } = "not attempted";

        // Resolves an exported symbol of the loaded core, whatever route loaded it.
        private static Func<string, IntPtr> sym;
        private static readonly object loadLock = new object();
        private static bool loaded;

        // Kept for the call site in OnCreate; loads the core (see EnsureLoaded).
        public static void RegisterResolver() => EnsureLoaded();

        // Loads the core once, trying each route in turn, then binds the function pointers. Throws
        // DllNotFoundException (with the per-route detail in LoadRoute) if every route fails.
        public static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            lock (loadLock)
            {
                if (loaded)
                {
                    return;
                }
                string path = CorePath();

                IntPtr handle = DlOpen(path, RtldNow);
                if (handle != IntPtr.Zero)
                {
                    sym = n => DlSym(handle, n);
                    LoadRoute = "file path";
                    Bind();
                    loaded = true;
                    return;
                }
                string fileError = DlErrorString();

                string memfdError;
                try
                {
                    IntPtr m = LoadFromMemory(path);
                    if (m != IntPtr.Zero)
                    {
                        sym = n => DlSym(m, n);
                        LoadRoute = "memfd";
                        Bind();
                        loaded = true;
                        return;
                    }
                    memfdError = "returned null";
                }
                catch (Exception e)
                {
                    memfdError = e.Message;
                }

                // UEP blocks fd-backed executable mappings but allows anonymous ones: map the core ourselves.
                try
                {
                    ElfImage img = ElfLoader.Load(File.ReadAllBytes(path));
                    sym = img.GetSymbol;
                    LoadRoute = "custom-elf";
                    Bind();
                    loaded = true;
                    return;
                }
                catch (Exception e)
                {
                    LoadRoute = "all routes failed: file(" + fileError + ") memfd(" + memfdError + ") custom(" + e.Message + "); " + ExecProbe();
                    Tizen.Log.Error("OmegaTube", LoadRoute);
                    throw new DllNotFoundException(LoadRoute);
                }
            }
        }

        private static T Fn<T>(string name) where T : Delegate
        {
            IntPtr p = sym(name);
            if (p == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException("omegacore: " + name);
            }
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        // dlopen an in-memory copy of the library so no on-disk file is mapped (route 2).
        private static IntPtr LoadFromMemory(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);  // reading the file is allowed; only mapping it is blocked
            int fd = MemfdCreate("omegacore", MfdCloexec);
            if (fd < 0)
            {
                throw new InvalidOperationException("memfd_create errno " + Marshal.GetLastWin32Error());
            }
            try
            {
                WriteAll(fd, bytes);
                IntPtr h = DlOpen("/proc/self/fd/" + fd, RtldNow);
                if (h == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "dlopen(memfd) null (bytes=" + bytes.Length + " fd=" + fd + " errno=" + Marshal.GetLastWin32Error()
                        + " dlerror=" + DlErrorString() + ")");
                }
                return h;
            }
            finally
            {
                close(fd);
            }
        }

        private static void WriteAll(int fd, byte[] bytes)
        {
            GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = pin.AddrOfPinnedObject();
                long remaining = bytes.Length;
                long offset = 0;
                while (remaining > 0)
                {
                    IntPtr n = write(fd, basePtr + (int)offset, (IntPtr)remaining);
                    long written = n.ToInt64();
                    if (written <= 0)
                    {
                        throw new IOException("write to memfd failed, errno " + Marshal.GetLastWin32Error());
                    }
                    offset += written;
                    remaining -= written;
                }
            }
            finally
            {
                pin.Free();
            }
        }

        // Whether this device lets us run unsigned code at all (part of the bug report if every route fails).
        private static string ExecProbe()
        {
            IntPtr fail = (IntPtr)(-1);
            var sb = new System.Text.StringBuilder("exec-probe ");
            IntPtr p = Mmap(IntPtr.Zero, (UIntPtr)4096, ProtRead | ProtWrite | ProtExec, MapPrivate | MapAnon, -1, IntPtr.Zero);
            if (p == fail) { sb.Append("anon-rwx=DENIED(errno " + Marshal.GetLastWin32Error() + ")"); }
            else { sb.Append("anon-rwx=OK"); Munmap(p, (UIntPtr)4096); }
            IntPtr q = Mmap(IntPtr.Zero, (UIntPtr)4096, ProtRead | ProtWrite, MapPrivate | MapAnon, -1, IntPtr.Zero);
            if (q == fail) { sb.Append(" rw->rx=rw-mmap-failed(errno " + Marshal.GetLastWin32Error() + ")"); }
            else { int r = Mprotect(q, (UIntPtr)4096, ProtRead | ProtExec); sb.Append(r == 0 ? " rw->rx=OK" : " rw->rx=DENIED(errno " + Marshal.GetLastWin32Error() + ")"); Munmap(q, (UIntPtr)4096); }
            return sb.ToString();
        }

        private static string DlErrorString()
        {
            IntPtr p = DlError();
            return p == IntPtr.Zero ? "unknown error" : (Marshal.PtrToStringAnsi(p) ?? "unknown error");
        }

        // app/OmegaTube/lib/libomegacore.so at runtime: a sibling of the resource folder.
        private static string CorePath()
        {
            string res = Tizen.Applications.Application.Current.DirectoryInfo.Resource;
            return Path.GetFullPath(Path.Combine(res, "..", "lib", "libomegacore.so"));
        }

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

        // --- Delegate types for the core's C API (cdecl; ANSI for the few char* taken as string) --------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Ip_v();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I_v();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void V_v();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Ip_b(byte[] a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Ip_bb(byte[] a, byte[] b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Ip_bbb(byte[] a, byte[] b, byte[] c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Ip_bi(byte[] a, int b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void V_b(byte[] a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void V_bb(byte[] a, byte[] b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I_buf(byte[] buf, int size, int maxLines);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void V_ip(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Ip_ip(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I_ipl(IntPtr p, long a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I_ipd(IntPtr p, double a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void V_ipStats(IntPtr p, out PlayerStats stats);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void LogInitFn(string host, int port);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void LogLineFn(string text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr CreateFn(IntPtr win, int w, int h, string videoUrl, string audioUrl);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr CreateYtFn(IntPtr win, int w, int h, string videoId, int maxHeight, string codecs, long startMs, string audioTrack);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void DiagnoseFn(IntPtr win, int w, int h, string videoUrl, string audioUrl);

        // --- Bound function pointers -------------------------------------------------------------------------
        private static Ip_v _version, _playerLastError, _configRefresh, _updateCheck, _authStatus, _authBegin, _authAccount;
        private static I_v _selfTest, _reportAvailable;
        private static V_v _authSignOut;
        private static I_buf _logTail;
        private static LogInitFn _logInitUdp;
        private static LogLineFn _logLine;
        private static CreateFn _playerCreate;
        private static CreateYtFn _playerCreateYouTube;
        private static DiagnoseFn _playerDiagnose;
        private static V_ip _playerTogglePause, _playerDestroy, _free;
        private static Ip_ip _playerFormats;
        private static I_ipl _playerSeek;
        private static I_ipd _playerSetRate;
        private static V_ipStats _playerGetStats;
        private static Ip_b _ytSuggest, _reportUpload, _authPoll, _qrEncode, _ytRelated, _ytDetails, _sbSegments;
        private static Ip_bb _ytSearch, _ytBrowse, _ytPlaylist, _ytPlaylistPage, _ytCaptions, _ytRate, _ytChannel, _ytComments, _ytPostComment;
        private static Ip_bbb _ytPostReply, _ytCommentAction;
        private static Ip_bi _ytSubscribe;
        private static V_b _init;
        private static V_bb _setLocale;

        private static void Bind()
        {
            _version = Fn<Ip_v>("omega_version");
            _selfTest = Fn<I_v>("omega_selftest");
            _logInitUdp = Fn<LogInitFn>("omega_log_init_udp");
            _logLine = Fn<LogLineFn>("omega_log_line");
            _logTail = Fn<I_buf>("omega_log_tail");
            _playerCreate = Fn<CreateFn>("omega_player_create");
            _playerCreateYouTube = Fn<CreateYtFn>("omega_player_create_youtube");
            _playerLastError = Fn<Ip_v>("omega_player_last_error");
            _playerTogglePause = Fn<V_ip>("omega_player_toggle_pause");
            _playerSeek = Fn<I_ipl>("omega_player_seek");
            _playerGetStats = Fn<V_ipStats>("omega_player_get_stats");
            _playerDestroy = Fn<V_ip>("omega_player_destroy");
            _playerDiagnose = Fn<DiagnoseFn>("omega_player_diagnose");
            _ytSearch = Fn<Ip_bb>("omega_yt_search");
            _ytSuggest = Fn<Ip_b>("omega_yt_suggest");
            _ytBrowse = Fn<Ip_bb>("omega_yt_browse");
            _init = Fn<V_b>("omega_init");
            _configRefresh = Fn<Ip_v>("omega_config_refresh");
            _setLocale = Fn<V_bb>("omega_set_locale");
            _updateCheck = Fn<Ip_v>("omega_update_check");
            _reportAvailable = Fn<I_v>("omega_report_available");
            _reportUpload = Fn<Ip_b>("omega_report_upload");
            _authStatus = Fn<Ip_v>("omega_auth_status");
            _authBegin = Fn<Ip_v>("omega_auth_begin");
            _authPoll = Fn<Ip_b>("omega_auth_poll");
            _authSignOut = Fn<V_v>("omega_auth_sign_out");
            _qrEncode = Fn<Ip_b>("omega_qr_encode");
            _ytPlaylist = Fn<Ip_bb>("omega_yt_playlist");
            _ytPlaylistPage = Fn<Ip_bb>("omega_yt_playlist_page");
            _ytRelated = Fn<Ip_b>("omega_yt_related");
            _playerFormats = Fn<Ip_ip>("omega_player_formats");
            _playerSetRate = Fn<I_ipd>("omega_player_set_rate");
            _ytCaptions = Fn<Ip_bb>("omega_yt_captions");
            _ytDetails = Fn<Ip_b>("omega_yt_details");
            _ytRate = Fn<Ip_bb>("omega_yt_rate");
            _ytSubscribe = Fn<Ip_bi>("omega_yt_subscribe");
            _ytChannel = Fn<Ip_bb>("omega_yt_channel");
            _ytComments = Fn<Ip_bb>("omega_yt_comments");
            _ytPostComment = Fn<Ip_bb>("omega_yt_post_comment");
            _ytPostReply = Fn<Ip_bbb>("omega_yt_post_reply");
            _ytCommentAction = Fn<Ip_bbb>("omega_yt_comment_action");
            _authAccount = Fn<Ip_v>("omega_auth_account");
            _sbSegments = Fn<Ip_b>("omega_sb_segments");
            _free = Fn<V_ip>("omega_free");
        }

        // --- Public API (unchanged signatures; call sites are untouched) --------------------------------------
        public static int SelfTest() => _selfTest();
        public static void LogInitUdp(string host, int port) => _logInitUdp(host, port);
        public static void Log(string text) => _logLine(text);
        public static IntPtr PlayerCreate(IntPtr ecoreWindow, int width, int height, string videoUrl, string audioUrl) => _playerCreate(ecoreWindow, width, height, videoUrl, audioUrl);
        public static IntPtr PlayerCreateYouTube(IntPtr ecoreWindow, int width, int height, string videoId, int maxHeight, string codecs, long startMs, string audioTrack) => _playerCreateYouTube(ecoreWindow, width, height, videoId, maxHeight, codecs, startMs, audioTrack);
        public static IntPtr PlayerLastError() => _playerLastError();
        public static void PlayerTogglePause(IntPtr player) => _playerTogglePause(player);
        public static int PlayerSeek(IntPtr player, long positionMs) => _playerSeek(player, positionMs);
        public static void PlayerGetStats(IntPtr player, out PlayerStats stats) => _playerGetStats(player, out stats);
        public static void PlayerDestroy(IntPtr player) => _playerDestroy(player);
        public static void PlayerDiagnose(IntPtr ecoreWindow, int width, int height, string videoUrl, string audioUrl) => _playerDiagnose(ecoreWindow, width, height, videoUrl, audioUrl);
        public static IntPtr PlayerFormats(IntPtr player) => _playerFormats(player);
        public static int PlayerSetRate(IntPtr player, double rate) => _playerSetRate(player, rate);

        public static IntPtr YtSearch(string query, string continuation) => _ytSearch(Utf8z(query), Utf8z(continuation));
        public static IntPtr YtSuggest(string query) => _ytSuggest(Utf8z(query));
        public static IntPtr YtBrowse(string browseId, string continuation) => _ytBrowse(Utf8z(browseId), Utf8z(continuation));
        public static void Init(string dataDir) { EnsureLoaded(); _init(Utf8z(dataDir)); }
        public static IntPtr ConfigRefresh() => _configRefresh();
        public static void SetLocale(string language, string region) => _setLocale(Utf8z(language), Utf8z(region));
        public static IntPtr UpdateCheck() => _updateCheck();
        public static int ReportAvailable() => _reportAvailable();
        public static IntPtr ReportUpload(string text) => _reportUpload(Utf8z(text));
        public static IntPtr AuthStatus() => _authStatus();
        public static IntPtr AuthBegin() => _authBegin();
        public static IntPtr AuthPoll(string deviceCode) => _authPoll(Utf8z(deviceCode));
        public static void AuthSignOut() => _authSignOut();
        public static IntPtr QrEncode(string text) => _qrEncode(Utf8z(text));
        public static IntPtr YtPlaylist(string videoId, string playlistId) => _ytPlaylist(Utf8z(videoId), Utf8z(playlistId));
        public static IntPtr YtPlaylistPage(string playlistId, string continuation) => _ytPlaylistPage(Utf8z(playlistId), Utf8z(continuation));
        public static IntPtr YtRelated(string videoId) => _ytRelated(Utf8z(videoId));
        public static IntPtr YtCaptions(string url, string translateTo) => _ytCaptions(Utf8z(url), Utf8z(translateTo));
        public static IntPtr YtDetails(string videoId) => _ytDetails(Utf8z(videoId));
        public static IntPtr YtRate(string videoId, string rating) => _ytRate(Utf8z(videoId), Utf8z(rating));
        public static IntPtr YtSubscribe(string channelId, bool subscribe) => _ytSubscribe(Utf8z(channelId), subscribe ? 1 : 0);
        public static IntPtr YtComments(string videoId, string continuation) => _ytComments(Utf8z(videoId), Utf8z(continuation));
        public static IntPtr YtPostComment(string createParams, string text) => _ytPostComment(Utf8z(createParams), Utf8z(text));
        public static IntPtr YtPostReply(string videoId, string commentId, string text) => _ytPostReply(Utf8z(videoId), Utf8z(commentId), Utf8z(text));
        public static IntPtr YtCommentAction(string videoId, string commentId, string action) => _ytCommentAction(Utf8z(videoId), Utf8z(commentId), Utf8z(action));
        public static IntPtr YtChannel(string channelId, string continuation) => _ytChannel(Utf8z(channelId), Utf8z(continuation));
        public static IntPtr AuthAccount() => _authAccount();
        public static IntPtr SbSegments(string videoId) => _sbSegments(Utf8z(videoId));
        public static void Free(IntPtr p) => _free(p);

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

        [DllImport(Lib, EntryPoint = "omega_report_available")]
        public static extern int ReportAvailable();

        [DllImport(Lib, EntryPoint = "omega_report_upload")]
        private static extern IntPtr ReportUploadRaw(byte[] text);

        public static IntPtr ReportUpload(string text) => ReportUploadRaw(Utf8z(text));

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

        [DllImport(Lib, EntryPoint = "omega_auth_accounts")]
        public static extern IntPtr AuthAccounts();

        [DllImport(Lib, EntryPoint = "omega_auth_switch")]
        private static extern IntPtr AuthSwitchRaw(byte[] id);

        public static IntPtr AuthSwitch(string id) => AuthSwitchRaw(Utf8z(id));

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

        public static string Version => Marshal.PtrToStringAnsi(_version());

        public static string RecentLog(int maxLines)
        {
            var buf = new byte[8192];
            int n = _logTail(buf, buf.Length, maxLines);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
    }
}
