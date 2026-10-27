#include "player/player.h"

#include <chrono>
#include <cstring>
#include <memory>
#include <vector>

#include "net/http.h"
#include "util/log.h"

namespace omega {
namespace {

using Clock = std::chrono::steady_clock;

const char* stream_name(esp::StreamType t) { return t == esp::STREAM_VIDEO ? "video" : "audio"; }

int64_t ms_since(Clock::time_point t) {
    return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - t).count();
}

void sleep_ms(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

}  // namespace

Player::Player(PlayerConfig cfg) : cfg_(std::move(cfg)) {
    video_.type = esp::STREAM_VIDEO;
    video_.url = cfg_.video_url;
    audio_.type = esp::STREAM_AUDIO;
    audio_.url = cfg_.audio_url;
}

Player::~Player() {
    running_ = false;
    for (Track* t : {&video_, &audio_}) t->cv.notify_all();
    if (control_thread_.joinable()) control_thread_.join();
    for (Track* t : {&video_, &audio_}) {
        if (t->feed_thread.joinable()) t->feed_thread.join();
        if (t->demux_thread.joinable()) t->demux_thread.join();
    }
    if (handle_) {
        api_->stop(handle_);
        api_->close(handle_);
        api_->destroy(handle_);
        OLOG("player: destroyed");
    }
}

bool Player::start() {
    api_ = esp::api();
    if (!api_) {
        error_ = "libesplusplayer.so unavailable";
        return false;
    }

    // Open the HTTP streams and parse their init segments first: ESPlayer needs codec info before prepare.
    struct Source {
        Track* track;
        std::unique_ptr<net::RangeReader> http;
        std::unique_ptr<media::Fmp4Reader> reader;
    };
    std::vector<Source> sources;
    if (cfg_.use_video) sources.push_back({&video_, nullptr, nullptr});
    if (cfg_.use_audio) sources.push_back({&audio_, nullptr, nullptr});
    for (Source& src : sources) {
        Track& t = *src.track;
        src.http = std::make_unique<net::RangeReader>(t.url, cfg_.stream_headers);
        net::RangeReader* http = src.http.get();
        src.reader = std::make_unique<media::Fmp4Reader>([http, &t](void* dst, size_t n) {
            size_t got = http->read(dst, n);
            t.bytes += got;
            return got;
        });
        if (!src.reader->read_header()) {
            error_ = std::string(stream_name(t.type)) + " mp4: " + src.reader->error() +
                     (http->error().empty() ? "" : " / http: " + http->error());
            return false;
        }
        t.info = src.reader->track();
        t.segments = src.reader->segments();
        OLOG("%s: codec=%d timescale=%u %ux%u ch=%u rate=%u codec_data=%zu bytes, media_time=%lld",
             stream_name(t.type), static_cast<int>(t.info.codec), t.info.timescale, t.info.width, t.info.height,
             t.info.channels, t.info.sample_rate, t.info.codec_data.size(), static_cast<long long>(t.info.media_time));
    }
    if ((cfg_.use_video && video_.info.codec != media::Codec::H264) ||
        (cfg_.use_audio && audio_.info.codec != media::Codec::AAC)) {
        error_ = "expected H.264 video + AAC audio";
        return false;
    }

    handle_ = api_->create();
    if (!handle_) {
        error_ = "esplusplayer_create failed";
        return false;
    }
    api_->set_error_cb(handle_, &Player::on_error, this);
    api_->set_buffer_status_cb(handle_, &Player::on_buffer_status, this);
    api_->set_eos_cb(handle_, &Player::on_eos, this);
    api_->set_ready_to_prepare_cb(handle_, &Player::on_ready_to_prepare, this);
    api_->set_prepare_async_done_cb(handle_, &Player::on_prepare_done, this);
    api_->set_resource_conflicted_cb(handle_, &Player::on_resource_conflicted, this);
    api_->set_ready_to_seek_cb(handle_, &Player::on_ready_to_seek, this);
    api_->set_seek_done_cb(handle_, &Player::on_seek_done, this);

    int rc = api_->open(handle_);
    OLOG("esp open -> %d", rc);
    if (rc != 0) {
        error_ = "esplusplayer_open failed: " + std::to_string(rc);
        return false;
    }
    if (!cfg_.app_type.empty()) {
        esp::AppInfo app{const_cast<char*>(cfg_.app_id.c_str()), const_cast<char*>(cfg_.app_version.c_str()),
                         const_cast<char*>(cfg_.app_type.c_str())};
        rc = api_->set_app_info(handle_, &app);
        OLOG("esp set_app_info(%s %s %s) -> %d", app.id, app.version, app.type, rc);
    }
    if (cfg_.use_display) {
        rc = api_->set_ecore_display(handle_, esp::DISPLAY_OVERLAY, cfg_.window, 0, 0, cfg_.width, cfg_.height);
        OLOG("esp set_ecore_display(%p, %dx%d) -> %d", cfg_.window, cfg_.width, cfg_.height, rc);
        rc = api_->set_display_mode(handle_, esp::DISPLAY_MODE_LETTER_BOX);
        OLOG("esp set_display_mode -> %d", rc);
    }

    if (cfg_.use_audio) {
        esp::AudioStreamInfo ai{};
        ai.codec_data = reinterpret_cast<char*>(audio_.info.codec_data.data());
        ai.codec_data_length = static_cast<uint32_t>(audio_.info.codec_data.size());
        ai.mime_type = esp::AUDIO_AAC;
        ai.bitrate = 128000;
        ai.channels = audio_.info.channels;
        ai.sample_rate = audio_.info.sample_rate;
        rc = api_->set_audio_stream_info(handle_, &ai);
        OLOG("esp set_audio_stream_info -> %d", rc);
    }
    if (cfg_.use_video) {
        esp::VideoStreamInfo vi{};
        vi.codec_data = reinterpret_cast<char*>(video_.info.codec_data.data());
        vi.codec_data_length = static_cast<uint32_t>(video_.info.codec_data.size());
        vi.mime_type = esp::VIDEO_H264;
        vi.width = video_.info.width;
        vi.height = video_.info.height;
        vi.max_width = static_cast<uint32_t>(cfg_.max_width);
        vi.max_height = static_cast<uint32_t>(cfg_.max_height);
        vi.framerate_num = static_cast<uint32_t>(cfg_.fps);
        vi.framerate_den = 1;
        rc = api_->set_video_stream_info(handle_, &vi);
        OLOG("esp set_video_stream_info -> %d", rc);
    }

    duration_ms_ = cfg_.duration_ms;
    if (duration_ms_ == 0 && !video_.segments.empty()) duration_ms_ = video_.segments.back().start_ms;

    running_ = true;
    if (cfg_.start_ms > 0 && !video_.segments.empty()) {
        // Start mid-stream: same as a seek, but issued in IDLE before prepare (allowed by ESPlayer).
        int64_t target = std::min<int64_t>(cfg_.start_ms, duration_ms_ > 0 ? duration_ms_ - 1000 : cfg_.start_ms);
        rc = api_->seek(handle_, static_cast<uint64_t>(target));
        OLOG("esp start position %lld ms -> %d", static_cast<long long>(target), rc);
        feed_from_ms_ = target;
        position_ms_ = target;
        for (Source& src : sources) {
            Track& t = *src.track;
            media::Segment seg = t.segments.empty() ? media::Segment{0, 0, 0} : t.segments.front();
            for (const auto& candidate : t.segments)
                if (candidate.start_ms <= target) seg = candidate;
            std::string url = src.http->url();
            t.url = url;
            src.http = std::make_unique<net::RangeReader>(url, cfg_.stream_headers, 4 * 1024 * 1024, seg.offset);
            net::RangeReader* http = src.http.get();
            src.reader = std::make_unique<media::Fmp4Reader>(
                [http, &t](void* dst, size_t n) {
                    size_t got = http->read(dst, n);
                    t.bytes += got;
                    return got;
                },
                t.info, seg);
        }
    }
    for (Source& src : sources) start_track(*src.track, src.reader.release(), src.http.release());

    rc = api_->prepare_async(handle_);
    OLOG("esp prepare_async -> %d", rc);
    if (rc != 0) {
        error_ = "prepare_async failed: " + std::to_string(rc);
        return false;
    }
    control_thread_ = std::thread([this] { control_loop(); });
    return true;
}

void Player::attach_hooks(Track& t, net::RangeReader& source) {
    net::RangeReader::Hooks hooks;
    hooks.aborted = [this, &t] { return !running_ || !t.active; };
    hooks.refresh_url = [this, &t] { return fresh_url(t); };
    hooks.on_recovering = [&t](bool on) { t.reconnecting = on; };
    source.set_hooks(std::move(hooks));
}

// Both tracks hit the expiry together: the first caller re-resolves, the second reuses the result.
std::string Player::fresh_url(Track& t) {
    std::lock_guard<std::mutex> lock(refresh_mutex_);
    if (!cfg_.refresh_urls) return "";
    if (last_refresh_ == Clock::time_point{} || Clock::now() - last_refresh_ > std::chrono::seconds(30)) {
        std::string v, a;
        if (!cfg_.refresh_urls(v, a)) {
            OLOG("refresh: stream URLs could not be re-resolved");
            return "";
        }
        last_refresh_ = Clock::now();
        std::lock_guard<std::mutex> lv(video_.mutex), la(audio_.mutex);
        video_.url = v;
        audio_.url = a;
        OLOG("refresh: new stream URLs");
    }
    std::lock_guard<std::mutex> lt(t.mutex);
    return t.url;
}

void Player::start_track(Track& t, media::Fmp4Reader* reader, net::RangeReader* source) {
    attach_hooks(t, *source);
    t.active = true;
    t.demux_thread = std::thread([this, &t, reader, source] { demux_loop(t, reader, source); });
    t.feed_thread = std::thread([this, &t] { feed_loop(t); });
}

void Player::stop_track(Track& t) {
    t.active = false;
    t.cv.notify_all();
    if (t.feed_thread.joinable()) t.feed_thread.join();
    if (t.demux_thread.joinable()) t.demux_thread.join();
    std::lock_guard<std::mutex> lock(t.mutex);
    t.queue.clear();
    t.queued_ms = 0;
    t.demux_done = false;
}

bool Player::seek(int64_t ms) {
    std::lock_guard<std::mutex> lock(seek_mutex_);
    if (!prepared_ || video_.segments.empty()) return false;
    ms = std::max<int64_t>(0, std::min<int64_t>(ms, duration_ms_ > 0 ? duration_ms_ - 1000 : ms));
    // Exact seek: ESPlayer is told the exact target; video is fed from the keyframe segment at or before it
    // (the decoder needs it) and audio from the target. Precision matters for SponsorBlock skips.
    int64_t target = ms;
    auto t0 = Clock::now();

    Track* tracks[] = {&video_, &audio_};
    bool use[] = {cfg_.use_video, cfg_.use_audio};
    for (int i = 0; i < 2; ++i)
        if (use[i]) stop_track(*tracks[i]);

    for (Track* t : tracks) t->ready_to_feed = false;
    feed_from_ms_ = target;
    eos_ = false;
    seek_pending_ = true;
    position_ms_ = target;
    stall_grace_until_ms_ = ms_since(started_at_) + 3000;
    int rc = api_->seek(handle_, static_cast<uint64_t>(target));

    for (int i = 0; i < 2; ++i) {
        if (!use[i]) continue;
        Track& t = *tracks[i];
        media::Segment seg = t.segments.empty() ? media::Segment{0, 0, 0} : t.segments.front();
        for (const auto& candidate : t.segments)
            if (candidate.start_ms <= target) seg = candidate;
        std::string url;
        {
            std::lock_guard<std::mutex> lt(t.mutex);
            url = t.url;
        }
        auto* http = new net::RangeReader(url, cfg_.stream_headers, 4 * 1024 * 1024, seg.offset);
        auto* reader = new media::Fmp4Reader(
            [http, &t](void* dst, size_t n) {
                size_t got = http->read(dst, n);
                t.bytes += got;
                return got;
            },
            t.info, seg);
        start_track(t, reader, http);
    }
    OLOG("seek %lld -> %lld ms (esp %d), threads restarted in %lld ms", static_cast<long long>(ms),
         static_cast<long long>(target), rc, static_cast<long long>(ms_since(t0)));
    return rc == 0;
}

void Player::demux_loop(Track& t, media::Fmp4Reader* reader_raw, net::RangeReader* source_raw) {
    std::unique_ptr<media::Fmp4Reader> reader(reader_raw);
    std::unique_ptr<net::RangeReader> source(source_raw);
    std::deque<media::Sample> fragment;
    while (running_) {
        {
            std::unique_lock<std::mutex> lock(t.mutex);
            t.cv.wait(lock, [&] { return !running_ || !t.active || t.queued_ms < cfg_.max_queue_ms; });
        }
        if (!running_ || !t.active) break;
        fragment.clear();
        if (!reader->read_fragment(fragment)) {
            OLOG("%s demux: end at byte %llu/%lld (%s%s%s)", stream_name(t.type),
                 static_cast<unsigned long long>(source->position()), static_cast<long long>(source->total_size()),
                 reader->error().empty() ? "eof" : reader->error().c_str(), source->error().empty() ? "" : ", http: ",
                 source->error().c_str());
            break;
        }
        int64_t ms = 0;
        for (const auto& s : fragment) ms += s.duration_ms;
        std::lock_guard<std::mutex> lock(t.mutex);
        for (auto& s : fragment) t.queue.push_back(std::move(s));
        t.queued_ms += ms;
        t.cv.notify_all();
    }
    std::lock_guard<std::mutex> lock(t.mutex);
    t.url = source->url();  // keep the redirected edge URL for later seeks
    t.demux_done = true;
    t.cv.notify_all();
}

void Player::feed_loop(Track& t) {
    while (running_ && t.active && !t.ready_to_feed) sleep_ms(5);
    OLOG("%s feed: start", stream_name(t.type));
    while (running_ && t.active) {
        media::Sample s;
        {
            std::unique_lock<std::mutex> lock(t.mutex);
            t.cv.wait(lock, [&] { return !running_ || !t.active || !t.queue.empty() || t.demux_done; });
            if (!running_ || !t.active) break;
            if (t.queue.empty()) {
                lock.unlock();
                api_->submit_eos_packet(handle_, t.type);
                OLOG("%s feed: eos submitted", stream_name(t.type));
                break;
            }
            s = std::move(t.queue.front());
            t.queue.pop_front();
            t.queued_ms -= s.duration_ms;
            t.cv.notify_all();
        }

        // Pace submission on the playback clock so ESPlayer never holds more than feed_ahead_ms
        // (keeps memory bounded and makes seeks / quality switches cheap). Paused => clock frozen => we wait.
        while (running_ && t.active && s.pts_ms > position_ms_ + cfg_.feed_ahead_ms) sleep_ms(20);
        if (!running_ || !t.active) break;
        // After a seek the audio segment starts a bit before the (video keyframe) target.
        if (t.type == esp::STREAM_AUDIO && s.pts_ms < feed_from_ms_) continue;

        esp::EsPacket pkt{};
        pkt.type = t.type;
        pkt.buffer = reinterpret_cast<char*>(s.data.data());
        pkt.buffer_size = static_cast<uint32_t>(s.data.size());
        pkt.pts = static_cast<uint64_t>(s.pts_ms);
        pkt.duration = static_cast<uint64_t>(s.duration_ms);
        for (;;) {
            int st = api_->submit_packet(handle_, &pkt);
            if (st == esp::SUBMIT_SUCCESS) break;
            if (st == esp::SUBMIT_FULL || st == esp::SUBMIT_NOT_PREPARED) {
                sleep_ms(10);
                if (!running_ || !t.active) return;
                continue;
            }
            OLOG("%s feed: submit status %d at pts=%lld size=%u", stream_name(t.type), st,
                 static_cast<long long>(s.pts_ms), pkt.buffer_size);
            break;
        }
        t.submitted_ms = s.pts_ms;
    }
}

void Player::control_loop() {
    while (running_ && !prepared_ && !prepare_failed_) sleep_ms(10);
    if (!running_ || prepare_failed_) return;
    int rc = api_->start(handle_);
    started_at_ = Clock::now();
    OLOG("esp start -> %d", rc);

    // Stall detector: the playback clock must advance with wall time while PLAYING.
    uint64_t last_pos = 0;
    auto last_advance = Clock::now();
    bool in_stall = false;
    while (running_) {
        sleep_ms(50);
        uint64_t pos = 0;
        api_->get_playing_time(handle_, &pos);
        // Until seek_done the player still reports the old position: keep the target so feeding
        // (paced on position_ms_) is not throttled against a stale clock.
        if (!seek_pending_) position_ms_ = static_cast<int64_t>(pos);
        if (paused_ || eos_ || ms_since(started_at_) < stall_grace_until_ms_) {
            last_advance = Clock::now();
            continue;
        }
        if (pos != last_pos) {
            if (in_stall) {
                int64_t dur = ms_since(last_advance);
                if (dur > longest_stall_ms_) longest_stall_ms_ = dur;
                OLOG("stall ended after %lld ms at %llu ms", static_cast<long long>(dur),
                     static_cast<unsigned long long>(pos));
                in_stall = false;
            }
            last_pos = pos;
            last_advance = Clock::now();
        } else if (!in_stall && ms_since(last_advance) > 150 && ms_since(started_at_) > 1000) {
            in_stall = true;
            ++stalls_;
            OLOG("stall started at %llu ms (vq=%lld aq=%lld)", static_cast<unsigned long long>(pos),
                 static_cast<long long>(video_.queued_ms), static_cast<long long>(audio_.queued_ms));
        }
    }
}

void Player::toggle_pause() {
    if (!handle_ || !prepared_) return;
    paused_ = !paused_;
    int rc = paused_ ? api_->pause(handle_) : api_->resume(handle_);
    OLOG("esp %s -> %d", paused_ ? "pause" : "resume", rc);
}

bool Player::set_rate(double rate) {
    if (!handle_ || !prepared_) return false;
    int rc = api_->set_playback_rate(handle_, rate, false);
    OLOG("esp set_playback_rate(%.2f) -> %d", rate, rc);
    return rc == 0;
}

PlayerStats Player::stats() {
    PlayerStats s{};
    s.state = handle_ ? api_->get_state(handle_) : esp::STATE_NONE;
    s.position_ms = position_ms_;
    {
        std::lock_guard<std::mutex> lock(video_.mutex);
        s.video_queued_ms = video_.queued_ms;
    }
    {
        std::lock_guard<std::mutex> lock(audio_.mutex);
        s.audio_queued_ms = audio_.queued_ms;
    }
    s.video_submitted_ms = video_.submitted_ms;
    s.audio_submitted_ms = audio_.submitted_ms;
    s.video_underruns = video_.underruns;
    s.audio_underruns = audio_.underruns;
    s.stalls = stalls_;
    s.longest_stall_ms = longest_stall_ms_;
    double secs = prepared_ ? std::max<int64_t>(ms_since(started_at_), 1) / 1000.0 : 1.0;
    s.video_kbps = video_.bytes * 8.0 / 1000.0 / secs;
    s.audio_kbps = audio_.bytes * 8.0 / 1000.0 / secs;
    s.errors = errors_;
    s.eos = eos_ ? 1 : 0;
    s.duration_ms = duration_ms_;
    s.reconnecting = (video_.reconnecting || audio_.reconnecting) ? 1 : 0;
    return s;
}

void Player::on_error(int err, void* self) {
    auto* p = static_cast<Player*>(self);
    ++p->errors_;
    OLOG("esp error 0x%x (%s)", err, p->api_->get_error_string(err));
}

void Player::on_buffer_status(esp::StreamType type, esp::BufferStatus status, void* self) {
    auto* p = static_cast<Player*>(self);
    // Underruns are expected while filling up before prepare; only count them during playback.
    if (status == esp::BUFFER_UNDERRUN && p->prepared_) {
        Track& t = type == esp::STREAM_VIDEO ? p->video_ : p->audio_;
        ++t.underruns;
        int64_t queued;
        {
            std::lock_guard<std::mutex> lock(t.mutex);
            queued = t.queued_ms;
        }
        OLOG("%s buffer underrun: position=%lld submitted=%lld (lead %lld ms) demux queue=%lld ms", stream_name(type),
             static_cast<long long>(p->position_ms_.load()), static_cast<long long>(t.submitted_ms.load()),
             static_cast<long long>(t.submitted_ms - p->position_ms_), static_cast<long long>(queued));
    }
}

void Player::on_eos(void* self) {
    static_cast<Player*>(self)->eos_ = true;
    OLOG("esp eos");
}

void Player::on_ready_to_prepare(esp::StreamType type, void* self) {
    auto* p = static_cast<Player*>(self);
    OLOG("esp ready_to_prepare(%s)", stream_name(type));
    (type == esp::STREAM_VIDEO ? p->video_ : p->audio_).ready_to_feed = true;
}

void Player::on_prepare_done(bool ok, void* self) {
    auto* p = static_cast<Player*>(self);
    OLOG("esp prepare_done ok=%d", ok ? 1 : 0);
    (ok ? p->prepared_ : p->prepare_failed_) = true;
}

void Player::on_ready_to_seek(esp::StreamType type, uint64_t time_ms, void* self) {
    auto* p = static_cast<Player*>(self);
    OLOG("esp ready_to_seek(%s, %llu)", stream_name(type), static_cast<unsigned long long>(time_ms));
    (type == esp::STREAM_VIDEO ? p->video_ : p->audio_).ready_to_feed = true;
}

void Player::on_seek_done(void* self) {
    static_cast<Player*>(self)->seek_pending_ = false;
    OLOG("esp seek_done");
}

void Player::on_resource_conflicted(void* self) {
    ++static_cast<Player*>(self)->errors_;
    OLOG("esp resource conflicted");
}

}  // namespace omega
