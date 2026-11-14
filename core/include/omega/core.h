#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define OMEGA_API __attribute__((visibility("default")))

// App version (repo VERSION file), e.g. "0.4.0".
OMEGA_API const char* omega_version(void);

// Call first. data_dir: app data folder (keeps the cached remote config, the account token and the log
// files omega.log / omega.log.prev for bug reports).
OMEGA_API void omega_init(const char* data_dir);
// Network: fetches the repo's remote-config.json (InnerTube clients, OAuth fallback...). {"ok"} or {"error"}.
OMEGA_API char* omega_config_refresh(void);
// InnerTube hl / gl, e.g. "fr", "FR".
OMEGA_API void omega_set_locale(const char* language, const char* region);
// Network: latest GitHub release vs. this build: {"available","version","url","tpk","notes"} or {"error"}.
OMEGA_API char* omega_update_check(void);
// 1 when remote-config.json names a log service (reports.paste): "Attach the full log" is offered.
OMEGA_API int omega_report_available(void);
// Network: posts a bug report text to the paste service of the remote config: {"url","partial"} or {"error"}
// (partial: the service kept only the beginning).
OMEGA_API char* omega_report_upload(const char* text);

// Sanity check run by the shell at startup: exercises the C++ runtime
// (std::string, exceptions, threads) and returns 0 when everything works.
OMEGA_API int omega_selftest(void);

// Mirrors every log line to UDP host:port (dev PC) in addition to the in-memory ring.
OMEGA_API void omega_log_init_udp(const char* host, int port);
// Adds a line from the shell (managed exceptions...) to the core log.
OMEGA_API void omega_log_line(const char* text);
// Copies the last `max_lines` log lines, '\n'-separated, into buf. Returns the length written.
OMEGA_API int omega_log_tail(char* buf, int buf_size, int max_lines);

typedef struct omega_player omega_player;

typedef struct {
    int32_t state;  // 0 none, 1 idle, 2 ready, 3 playing, 4 paused
    int32_t eos;
    int64_t position_ms;
    int64_t video_queued_ms;
    int64_t audio_queued_ms;
    int64_t video_submitted_ms;
    int64_t audio_submitted_ms;
    int64_t longest_stall_ms;
    uint32_t video_underruns;
    uint32_t audio_underruns;
    uint32_t stalls;
    uint32_t errors;
    double video_kbps;
    double audio_kbps;
    int64_t duration_ms;
    int32_t reconnecting;  // a stream is retrying after a network error
    int32_t buffering;     // playback is held until video and audio both have enough data (a rebuffer)
} omega_player_stats;

// Starts playing a DASH H.264 video track + AAC audio track (fragmented MP4 over http://) on the
// given Ecore_Wl2_Window. Returns NULL on failure; the reason is in the log.
OMEGA_API omega_player* omega_player_create(void* ecore_wl2_window, int width, int height, const char* video_url,
                                            const char* audio_url);
// Resolves a YouTube video id (InnerTube), picks the best video <= max_height + AAC, and starts playing.
// codecs: comma-separated preference among "avc1" (H.264), "vp9", "av01" (AV1); NULL/empty = "avc1,vp9".
// At equal resolution and fps the first listed codec wins; if the TV refuses a codec the next one is tried.
// Blocking (network): call it off the UI thread. Returns NULL on failure; the reason is in the log.
// start_ms > 0 resumes at that position. audio_track: audio track id ("fr.3") or language ("fr") for videos
// with several audio languages; NULL/empty or not offered = the default (original) track.
OMEGA_API omega_player* omega_player_create_youtube(void* ecore_wl2_window, int width, int height, const char* video_id,
                                                    int max_height, const char* codecs, int64_t start_ms,
                                                    const char* audio_track);
// Muted preview of a YouTube video (the home's "rest on it to see it" playback): video track only (no audio),
// at most max_height (0 = 720), short read-ahead, and no watch-history reporting. Blocking (network): call it
// off the UI thread. Release with omega_player_destroy. Returns NULL on failure (the reason is in the log).
OMEGA_API omega_player* omega_player_create_preview(void* ecore_wl2_window, int width, int height,
                                                    const char* video_id, int max_height, const char* codecs,
                                                    int64_t start_ms);
// Resolves a video's streams ahead of its preview (InnerTube "player" only: no decoder, no watch history) and
// keeps them (a few videos, 90 min), so omega_player_create_preview starts with the media fetch. Blocking
// (network): call it off the UI thread. Returns 1 when resolved (or already cached), 0 on failure.
OMEGA_API int omega_preview_prefetch(const char* video_id);
// Why the last omega_player_create_youtube returned NULL: {"kind","status","reason","detail","signedIn"}.
// kind: login | age | upcoming | unplayable | unavailable | rate_limited | http | network | formats | player | other;
// status: YouTube's playabilityStatus ("LOGIN_REQUIRED"...) or "HTTP_<code>"; reason: YouTube's own message
// (in the request language), may be empty; detail: technical error. Release with omega_free().
OMEGA_API char* omega_player_last_error(void);
// Video qualities of a YouTube player (best variant per height, same codec preference as at creation):
// {"current":{"width","height","fps","codec"},"available":[{...}, ...],"live":bool,"title","author"} (highest first),
// codec = "avc1" | "vp9" | "av01". Also "description", "views" (raw count), "channelId",
// "audioTracks":[{"id","name","default"}] (empty for single-track videos), "audioTrack": the playing id,
// "captions":[{"url","lang","name","auto","translatable"}], "translations":[{"code","name"}].
// Release with omega_free(). {"available":[]} for a URL player.
OMEGA_API char* omega_player_formats(omega_player* player);
OMEGA_API void omega_player_toggle_pause(omega_player* player);
// Seeks to position_ms (video decoding restarts from the preceding keyframe). Blocking for a few hundred ms.
OMEGA_API int omega_player_seek(omega_player* player, int64_t position_ms);
// Playback speed (0.5 .. 2.0), audio kept (pitch handled by the TV player).
OMEGA_API int omega_player_set_rate(omega_player* player, double rate);
OMEGA_API void omega_player_get_stats(omega_player* player, omega_player_stats* out);
OMEGA_API void omega_player_destroy(omega_player* player);

// ---- YouTube data (blocking network calls: use from a worker thread) ----
// Results are malloc'ed UTF-8 JSON strings to release with omega_free():
//   search:  {"items":[{"id","title","channel","duration","views","published","thumbnail","live"}],
//             "continuation":"..."}  or {"error":"..."}
//   suggest: {"suggestions":["..."]} or {"error":"..."}
OMEGA_API char* omega_yt_search(const char* query, const char* continuation);
OMEGA_API char* omega_yt_suggest(const char* query);
// Signed-in feed (TV client): browse_id FEwhat_to_watch | FEsubscriptions | FEhistory. Same JSON as search
// (FEsubscriptions first page adds "highlights": YouTube's "most relevant" shelf, same item format);
// FEwhat_to_watch (home) gives rows instead of items: "shelves":[{"title","items":[...],"continuation"}] (a row's
// continuation, passed back with browse_id "" , returns more items of that row).
// {"error":"signed out"} when no account is connected.
OMEGA_API char* omega_yt_browse(const char* browse_id, const char* continuation);
// Items: also "playlistId" and "kind" ("video" | "playlist" | "mix": a playlist card plays "id" within it).
// The (up to 25) videos after video_id in playlist_id (playlist or mix): same JSON as search plus "title".
OMEGA_API char* omega_yt_playlist(const char* video_id, const char* playlist_id);
// A whole playlist in order (paged): same JSON as search plus "title" (first page); items carry "playlistId".
OMEGA_API char* omega_yt_playlist_page(const char* playlist_id, const char* continuation);
// Related videos of video_id (player suggestions). Same JSON as search.
OMEGA_API char* omega_yt_related(const char* video_id);
// Watch-page details: {"channelId","channel","channelAvatar","subscribers","date","likes","dislikes",
// "likeStatus":"LIKE|DISLIKE|INDIFFERENT|" (empty: signed out),"subscribed":1|0|-1} or {"error"}.
// likes / dislikes: Return YouTube Dislike estimates, -1 when unknown.
OMEGA_API char* omega_yt_details(const char* video_id);
// As the signed-in account: rating "like" | "dislike" | "none"; (un)subscribe. {"ok":true} or {"error"}.
OMEGA_API char* omega_yt_rate(const char* video_id, const char* rating);
OMEGA_API char* omega_yt_subscribe(const char* channel_id, int subscribe);
// A channel's videos (newest first): same JSON as search plus "title","avatar","subscribers" (first page).
OMEGA_API char* omega_yt_channel(const char* channel_id, const char* continuation);
// Comments of a video, continuation NULL/empty = first page (top comments), or an item's replyToken for its replies:
// {"items":[{"id","author","avatar","text","published","likes","replies","pinned","creator","hearted","replyToken",
//  "liked","disliked" (the signed-in account's vote)}],
//  "continuation","count" (display text, first page),"sortTop","sortNewest" (continuations reloading the list
//  in that order),"createParams" (for omega_yt_post_comment),"disabled":bool} or {"error"}.
OMEGA_API char* omega_yt_comments(const char* video_id, const char* continuation);
// Downloads a video's animated preview (YouTube's moving-thumbnail WebP) and decodes it into a 24-bit BMP sprite
// sheet at out_path (the Tizen 5.5 image loader cannot animate WebP itself). url: the item's preview URL; empty =
// look it up by video_id (WEB search). Returns JSON {"cols","rows","frames","w","h","delay"} (frame grid,
// row-major; delay in ms per frame) or {"error"} (plus "none": true when the video has no preview).
OMEGA_API char* omega_preview_sheet(const char* video_id, const char* url, const char* out_path);
// As the signed-in account: {"ok":true,"comment":{...} or null} or {"error"}.
OMEGA_API char* omega_yt_post_comment(const char* create_params, const char* text);
// A reply in the thread of comment_id (a top-level comment): as omega_yt_post_comment.
OMEGA_API char* omega_yt_post_reply(const char* video_id, const char* comment_id, const char* text);
// action: like, unlike, dislike, undislike. {"ok":true} or {"error"}.
OMEGA_API char* omega_yt_comment_action(const char* video_id, const char* comment_id, const char* action);
// Subtitles: a caption track URL (from omega_player_formats), machine-translated to translate_to when not
// NULL/empty: {"cues":[{"start","end","text"}]} (ms) or {"error"}.
OMEGA_API char* omega_yt_captions(const char* url, const char* translate_to);
// SponsorBlock: {"segments":[{"category","start","end"}]} (seconds) or {"error"}.
OMEGA_API char* omega_sb_segments(const char* video_id);
OMEGA_API void omega_free(void* p);

// ---- Google account (OAuth device flow) ----
// store_path: file keeping the account store (app data folder); a legacy auth.json is migrated.
OMEGA_API void omega_auth_init(const char* store_path);
// {"signedIn":bool} (whether an active account is signed in)
OMEGA_API char* omega_auth_status(void);
// {"deviceCode","userCode","verificationUrl","qrUrl","interval","expiresIn"} or {"error"}
OMEGA_API char* omega_auth_begin(void);
// {"status":"pending|slow_down|done|denied|expired|failed","error":"..."}; on "done" a new account is added
// and becomes active.
OMEGA_API char* omega_auth_poll(const char* device_code);
// Removes the active account; the next one (if any) becomes active.
OMEGA_API void omega_auth_sign_out(void);
// Network: the active account {"name","handle","photo"} or {"error"}.
OMEGA_API char* omega_auth_account(void);
// All signed-in accounts for the picker: {"accounts":[{"id","name","handle","photo","active"}]}.
OMEGA_API char* omega_auth_accounts(void);
// Makes the account with this id active: {"ok":true} or {"error"}.
OMEGA_API char* omega_auth_switch(const char* id);

// QR code of `text` (ECC medium): {"size":N,"rows":["0101...", ...]} ("1" = dark module).
OMEGA_API char* omega_qr_encode(const char* text);

// Dev diagnostics: tries several ESPlayer configurations in sequence and logs whether each prepares.
OMEGA_API void omega_player_diagnose(void* ecore_wl2_window, int width, int height, const char* video_url,
                                     const char* audio_url);

#ifdef __cplusplus
}
#endif
