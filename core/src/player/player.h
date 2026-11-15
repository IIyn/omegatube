#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

#include "media/demuxer.h"
#include "net/http.h"
#include "player/esplayer_api.h"

namespace omega {

struct PlayerConfig {
    void* window = nullptr;  // Ecore_Wl2_Window*
    int width = 1920, height = 1080;
    std::string video_url, audio_url;
    net::Headers stream_headers;      // sent with every media request (e.g. YouTube User-Agent)
    int fps = 30;
    int max_width = 1920, max_height = 1080;
    int64_t duration_ms = 0;          // 0: derive from the stream index
    int64_t start_ms = 0;             // resume position (set before prepare: no flash of the beginning)
    // Live broadcast: the URLs are numbered-segment streams (net::LiveReader); no index, no seek.
    bool live = false;
    int live_behind_segments = 3;     // start this many segments (~5 s each) behind the live edge
    // Re-resolves expired stream URLs (YouTube URLs live ~6 h). Optional.
    std::function<bool(std::string& video_url, std::string& audio_url)> refresh_urls;
    std::string app_id, app_version;  // reported to the TV resource manager
    std::string app_type = "MSE";     // empty: skip esplusplayer_set_app_info
    bool use_video = true, use_audio = true, use_display = true;
    int64_t max_queue_ms = 30000;   // demuxed read-ahead per stream (network side)
    size_t max_queue_bytes = 48u << 20;  // ...and its memory cap (4K VP9 runs at 20-40 Mb/s)
    int64_t feed_ahead_ms = 15000;  // how far ahead of the playback clock we submit to ESPlayer
};

struct PlayerStats {
    int state;                   // esp::State
    int64_t position_ms;
    int64_t video_queued_ms, audio_queued_ms;
    int64_t video_submitted_ms, audio_submitted_ms;
    uint32_t video_underruns, audio_underruns;
    uint32_t stalls;             // playback clock stopped advancing while PLAYING
    int64_t longest_stall_ms;
    double video_kbps, audio_kbps;  // download rate
    uint32_t errors;
    int eos;
    int64_t duration_ms;
    int reconnecting;  // a stream is retrying after a network error
    int buffering;     // playback is held until video and audio have enough data again (see update_rebuffering)
};

// Plays one DASH video track + one audio track (fragmented MP4 or WebM over HTTP) with ESPlayer.
class Player {
public:
    using Clock = std::chrono::steady_clock;
    explicit Player(PlayerConfig cfg);
    ~Player();

    bool start();
    void toggle_pause();
    // Jumps to the keyframe segment at or before `ms`. Returns false if not seekable (yet).
    bool seek(int64_t ms);
    bool set_rate(double rate);
    // Read-ahead, live: how far ahead of the playhead we feed the TV, plus the demuxer caps that must hold it.
    void set_buffer(int64_t ahead_ms, int64_t queue_ms, int64_t queue_bytes);
    int64_t buffer_ahead_ms() const { return feed_ahead_.load(); }
    PlayerStats stats();
    // 0 while preparing, 1 prepared, -1 prepare failed.
    int prepare_result() const { return prepared_ ? 1 : (prepare_failed_ ? -1 : 0); }
    const std::string& error() const { return error_; }
    // start() failed because YouTube kept refusing a stream (403/410), not a network or codec issue.
    bool stream_refused() const { return stream_refused_; }

private:
    struct Track {
        esp::StreamType type;
        std::string url;
        media::TrackInfo info;
        std::mutex mutex;
        std::condition_variable cv;
        std::deque<media::Sample> queue;
        int64_t queued_ms = 0;
        size_t queued_bytes = 0;
        // What has already been fed to the TV's player, kept so a seek inside that region (the larger half of the grey
        // bar) can start here again instead of reopening the stream. Bounded by time and by the buffer memory setting.
        std::deque<media::Sample> history;
        size_t history_bytes = 0;
        bool demux_done = false;
        std::atomic<bool> ready_to_feed{false};
        std::atomic<bool> active{true};  // cleared to stop this track's threads (seek)
        std::atomic<bool> reconnecting{false};
        std::vector<media::Segment> segments;
        std::atomic<int64_t> submitted_ms{0};
        std::atomic<uint32_t> underruns{0};
        std::atomic<uint64_t> bytes{0};
        std::thread demux_thread, feed_thread;
    };

    void demux_loop(Track& t, media::Demuxer* reader, net::StreamSource* source);
    void start_track(Track& t, media::Demuxer* reader, net::StreamSource* source);
    void stop_track(Track& t);
    void attach_hooks(Track& t, net::StreamSource& source);
    void push_samples(Track& t, std::deque<media::Sample>& samples);
    std::string fresh_url(Track& t);
    void feed_loop(Track& t);
    void control_loop();
    void update_rebuffering();
    // Serves a forward seek from the demuxed window when it can (see media/seek_window.h): no network round-trip.
    bool seek_from_queue(int64_t target);
    // The retained back-buffer's limits: as far back as we feed ahead, and half the buffer memory each way.
    int64_t keep_behind_ms() const { return keep_behind_.load(); }
    int64_t history_byte_cap() const { return queue_bytes_cap_.load(); }   // the retained past is the seekable side
    int64_t lead_ms(Track& t);        // how far ahead of the playhead the TV's player was fed with this track
    bool track_finished(Track& t);    // nothing more will come from this track (end of the stream)

    static void on_error(int err, void* self);
    static void on_buffer_status(esp::StreamType type, esp::BufferStatus status, void* self);
    static void on_eos(void* self);
    static void on_ready_to_prepare(esp::StreamType type, void* self);
    static void on_prepare_done(bool ok, void* self);
    static void on_resource_conflicted(void* self);
    static void on_ready_to_seek(esp::StreamType type, uint64_t time_ms, void* self);
    static void on_seek_done(void* self);

    PlayerConfig cfg_;
    const esp::Api* api_ = nullptr;
    esp::Handle handle_ = nullptr;
    std::string error_;
    bool stream_refused_ = false;

    Track video_, audio_;
    std::atomic<bool> running_{false};
    std::atomic<bool> prepared_{false}, prepare_failed_{false}, paused_{false}, eos_{false};
    // How far ahead of the playhead we feed the TV (see feed_loop). Live-changeable: the buffer setting is a share
    // of the video's length, so it differs per video and the user can change it from the player's menu mid-video.
    std::atomic<int64_t> feed_ahead_{15000}, queue_cap_{30000}, queue_bytes_cap_{48u << 20};
    // Bumped on every seek: a sample the feed loop already popped belongs to the old position and is dropped.
    std::atomic<uint32_t> seek_gen_{0};
    // How much lead the resume gate waits for. A seek served from the buffer already holds the data, so it resumes
    // on a fraction of the lead a network seek needs (which also starts the decode from the keyframe sooner).
    std::atomic<int64_t> resume_lead_ms_{2000};
    // How far back the retained copy reaches: the buffer setting, so a seek anywhere in the grey bar is ours.
    std::atomic<int64_t> keep_behind_{15000};
    // Where "enough to resume" is measured from. A buffer-served seek feeds from a keyframe behind the target, so
    // waiting for 400 ms past the target means waiting for the feed to crawl through the whole GOP first; 0 means
    // "use the position" (the normal case).
    std::atomic<int64_t> lead_base_ms_{0};
    std::atomic<bool> rebuffering_{false};   // held by us (not the user) because a track ran out of data
    Clock::time_point rebuffer_since_{}, rebuffer_grace_until_{};
    std::atomic<uint32_t> errors_{0}, stalls_{0};
    std::atomic<int64_t> longest_stall_ms_{0}, position_ms_{0};
    std::thread control_thread_;
    std::mutex seek_mutex_;
    std::atomic<int64_t> feed_from_ms_{0};  // audio samples before this are dropped after a seek
    std::atomic<int64_t> duration_ms_{0};
    int64_t live_base_ms_ = 0;  // live: absolute broadcast time of our playback position 0
    std::atomic<int64_t> stall_grace_until_ms_{0};
    std::atomic<bool> seek_pending_{false};
    std::mutex refresh_mutex_;
    Clock::time_point last_refresh_{};
    Clock::time_point started_at_;
};

}  // namespace omega
