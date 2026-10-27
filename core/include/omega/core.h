#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define OMEGA_API __attribute__((visibility("default")))

// App version (repo VERSION file), e.g. "0.4.0".
OMEGA_API const char* omega_version(void);

// Call first. data_dir: app data folder (keeps the cached remote config and the account token).
OMEGA_API void omega_init(const char* data_dir);
// Network: fetches the repo's remote-config.json (InnerTube clients, OAuth fallback...). {"ok"} or {"error"}.
OMEGA_API char* omega_config_refresh(void);
// InnerTube hl / gl, e.g. "fr", "FR".
OMEGA_API void omega_set_locale(const char* language, const char* region);
// Network: latest GitHub release vs. this build: {"available","version","url","tpk","notes"} or {"error"}.
OMEGA_API char* omega_update_check(void);

// Sanity check run by the shell at startup: exercises the C++ runtime
// (std::string, exceptions, threads) and returns 0 when everything works.
OMEGA_API int omega_selftest(void);

// Mirrors every log line to UDP host:port (dev PC) in addition to the in-memory ring.
OMEGA_API void omega_log_init_udp(const char* host, int port);
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
    int32_t reserved;
} omega_player_stats;

// Starts playing a DASH H.264 video track + AAC audio track (fragmented MP4 over http://) on the
// given Ecore_Wl2_Window. Returns NULL on failure; the reason is in the log.
OMEGA_API omega_player* omega_player_create(void* ecore_wl2_window, int width, int height, const char* video_url,
                                            const char* audio_url);
// Resolves a YouTube video id (InnerTube), picks H.264 <= max_height + AAC, and starts playing.
// Blocking (network): call it off the UI thread. Returns NULL on failure; the reason is in the log.
// start_ms > 0 resumes at that position.
OMEGA_API omega_player* omega_player_create_youtube(void* ecore_wl2_window, int width, int height, const char* video_id,
                                                    int max_height, int64_t start_ms);
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
// Signed-in feed (TV client): browse_id FEwhat_to_watch | FEsubscriptions | FEhistory. Same JSON as search;
// {"error":"signed out"} when no account is connected.
OMEGA_API char* omega_yt_browse(const char* browse_id, const char* continuation);
// Related videos of video_id (player suggestions). Same JSON as search.
OMEGA_API char* omega_yt_related(const char* video_id);
// SponsorBlock: {"segments":[{"category","start","end"}]} (seconds) or {"error"}.
OMEGA_API char* omega_sb_segments(const char* video_id);
OMEGA_API void omega_free(void* p);

// ---- Google account (OAuth device flow) ----
// token_path: file keeping the refresh token (app data folder).
OMEGA_API void omega_auth_init(const char* token_path);
// {"signedIn":bool}
OMEGA_API char* omega_auth_status(void);
// {"deviceCode","userCode","verificationUrl","qrUrl","interval","expiresIn"} or {"error"}
OMEGA_API char* omega_auth_begin(void);
// {"status":"pending|slow_down|done|denied|expired|failed","error":"..."}
OMEGA_API char* omega_auth_poll(const char* device_code);
OMEGA_API void omega_auth_sign_out(void);

// QR code of `text` (ECC medium): {"size":N,"rows":["0101...", ...]} ("1" = dark module).
OMEGA_API char* omega_qr_encode(const char* text);

// Dev diagnostics: tries several ESPlayer configurations in sequence and logs whether each prepares.
OMEGA_API void omega_player_diagnose(void* ecore_wl2_window, int width, int height, const char* video_url,
                                     const char* audio_url);

#ifdef __cplusplus
}
#endif
