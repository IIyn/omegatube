#include "omega/core.h"

#include <chrono>
#include <cstdlib>
#include <cstring>

#include <nlohmann/json.hpp>
#include <stdexcept>
#include <string>
#include <thread>

#include "player/player.h"
#include "yt/auth.h"
#include "yt/innertube.h"
#include "yt/sponsorblock.h"

#include <qrcodegen/qrcodegen.hpp>
#include "util/log.h"
#include "util/remote_config.h"

namespace {
constexpr const char* kVersion = OMEGA_VERSION;  // from the repo's VERSION file
}

struct omega_player {
    omega::Player impl;
    explicit omega_player(omega::PlayerConfig cfg) : impl(std::move(cfg)) {}
};

const char* omega_version(void) { return kVersion; }

int omega_selftest(void) {
    int result = 1;
    std::thread worker([&result] {
        try {
            std::string s = "omega";
            s += "tube";
            if (s.size() != 9) throw std::runtime_error("string");
            result = 0;
        } catch (...) {
            result = 2;
        }
    });
    worker.join();
    return result;
}

void omega_log_init_udp(const char* host, int port) {
    omega::log::init_udp(host ? host : "", port);
    OLOG("omegacore %s: log sink %s:%d", kVersion, host ? host : "-", port);
}

int omega_log_tail(char* buf, int buf_size, int max_lines) {
    if (!buf || buf_size <= 0) return 0;
    std::string joined;
    for (const auto& line : omega::log::tail(max_lines > 0 ? static_cast<size_t>(max_lines) : 0)) {
        joined += line;
        joined += '\n';
    }
    size_t n = std::min(joined.size(), static_cast<size_t>(buf_size - 1));
    memcpy(buf, joined.data(), n);
    buf[n] = '\0';
    return static_cast<int>(n);
}

omega_player* omega_player_create(void* window, int width, int height, const char* video_url, const char* audio_url) {
    omega::PlayerConfig cfg;
    cfg.window = window;
    cfg.width = width;
    cfg.height = height;
    cfg.video_url = video_url ? video_url : "";
    cfg.audio_url = audio_url ? audio_url : "";
    cfg.app_id = "org.omegatube.app";
    cfg.app_version = kVersion;
    auto* p = new omega_player(std::move(cfg));
    if (!p->impl.start()) {
        OLOG("player start failed: %s", p->impl.error().c_str());
        delete p;
        return nullptr;
    }
    return p;
}

omega_player* omega_player_create_youtube(void* window, int width, int height, const char* video_id,
                                          int max_height) {
    omega::yt::Client client;
    omega::yt::VideoInfo info;
    std::string error;
    if (!client.player(video_id ? video_id : "", info, error)) {
        OLOG("youtube %s: %s", video_id ? video_id : "-", error.c_str());
        return nullptr;
    }
    const omega::yt::Format* video = omega::yt::pick_video(info, max_height > 0 ? max_height : 1080);
    const omega::yt::Format* audio = omega::yt::pick_audio(info);
    if (!video || !audio) {
        OLOG("youtube %s: no H.264/AAC formats", video_id);
        return nullptr;
    }
    OLOG("youtube: video itag %d %dx%d@%d %s %lld kb/s, audio itag %d %s %d Hz", video->itag, video->width,
         video->height, video->fps, video->codecs.c_str(), static_cast<long long>(video->bitrate / 1000), audio->itag,
         audio->codecs.c_str(), audio->audio_sample_rate);

    omega::PlayerConfig cfg;
    cfg.window = window;
    cfg.width = width;
    cfg.height = height;
    cfg.video_url = video->url;
    cfg.audio_url = audio->url;
    cfg.stream_headers = client.stream_headers();
    cfg.fps = video->fps > 0 ? video->fps : 30;
    cfg.max_width = video->width;
    cfg.max_height = video->height;
    cfg.duration_ms = info.length_s * 1000;
    // Expired URLs (403/410): ask InnerTube again and keep the same formats.
    std::string id = video_id;
    int video_itag = video->itag, audio_itag = audio->itag;
    cfg.refresh_urls = [id, video_itag, audio_itag](std::string& v, std::string& a) {
        omega::yt::Client fresh_client;
        omega::yt::VideoInfo fresh;
        std::string err;
        if (!fresh_client.player(id, fresh, err)) {
            OLOG("refresh %s: %s", id.c_str(), err.c_str());
            return false;
        }
        for (const auto& f : fresh.formats) {
            if (f.itag == video_itag) v = f.url;
            if (f.itag == audio_itag) a = f.url;
        }
        return !v.empty() && !a.empty();
    };
    cfg.app_id = "org.omegatube.app";
    cfg.app_version = kVersion;
    auto* p = new omega_player(std::move(cfg));
    if (!p->impl.start()) {
        OLOG("player start failed: %s", p->impl.error().c_str());
        delete p;
        return nullptr;
    }
    return p;
}

void omega_player_toggle_pause(omega_player* p) {
    if (p) p->impl.toggle_pause();
}

int omega_player_seek(omega_player* p, int64_t position_ms) { return p && p->impl.seek(position_ms) ? 0 : -1; }

void omega_player_get_stats(omega_player* p, omega_player_stats* out) {
    if (!p || !out) return;
    omega::PlayerStats s = p->impl.stats();
    out->state = s.state;
    out->eos = s.eos;
    out->position_ms = s.position_ms;
    out->video_queued_ms = s.video_queued_ms;
    out->audio_queued_ms = s.audio_queued_ms;
    out->video_submitted_ms = s.video_submitted_ms;
    out->audio_submitted_ms = s.audio_submitted_ms;
    out->longest_stall_ms = s.longest_stall_ms;
    out->video_underruns = s.video_underruns;
    out->audio_underruns = s.audio_underruns;
    out->stalls = s.stalls;
    out->errors = s.errors;
    out->video_kbps = s.video_kbps;
    out->audio_kbps = s.audio_kbps;
    out->duration_ms = s.duration_ms;
    out->reconnecting = s.reconnecting;
}

void omega_player_destroy(omega_player* p) { delete p; }

namespace {

char* to_c_json(const nlohmann::json& j) {
    std::string s = j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace);
    char* out = static_cast<char*>(malloc(s.size() + 1));
    memcpy(out, s.c_str(), s.size() + 1);
    return out;
}

nlohmann::json feed_json(const omega::yt::Feed& feed) {
    nlohmann::json items = nlohmann::json::array();
    for (const auto& it : feed.items)
        items.push_back({{"id", it.video_id},
                         {"title", it.title},
                         {"channel", it.channel},
                         {"duration", it.duration},
                         {"views", it.views},
                         {"published", it.published},
                         {"thumbnail", it.thumbnail},
                         {"live", it.is_live}});
    return {{"items", items}, {"continuation", feed.continuation}};
}

}  // namespace

char* omega_yt_search(const char* query, const char* continuation) {
    omega::yt::Feed feed;
    std::string error;
    if (!omega::yt::shared_client().search(query ? query : "", continuation ? continuation : "", feed, error))
        return to_c_json({{"error", error}});
    return to_c_json(feed_json(feed));
}

namespace {

omega::yt::DeviceCode g_device_code;  // last code from omega_auth_begin (poll only receives its id)

}  // namespace

char* omega_yt_browse(const char* browse_id, const char* continuation) {
    std::string error;
    std::string token = omega::yt::shared_auth().access_token(error);
    if (token.empty()) return to_c_json({{"error", error.empty() ? "signed out" : error}});
    omega::yt::Feed feed;
    if (!omega::yt::shared_client().browse(browse_id ? browse_id : "", continuation ? continuation : "", token, feed,
                                           error))
        return to_c_json({{"error", error}});
    return to_c_json(feed_json(feed));
}

char* omega_sb_segments(const char* video_id) {
    std::vector<omega::yt::SkipSegment> segments;
    std::string error;
    if (!omega::yt::sponsor_segments(video_id ? video_id : "", segments, error)) return to_c_json({{"error", error}});
    nlohmann::json list = nlohmann::json::array();
    for (const auto& s : segments) list.push_back({{"category", s.category}, {"start", s.start_s}, {"end", s.end_s}});
    return to_c_json({{"segments", list}});
}

void omega_init(const char* data_dir) {
    std::string dir = data_dir ? data_dir : "";
    if (!dir.empty() && dir.back() != '/') dir += '/';
    omega::cfg::init(dir + "remote-config.json");
    omega::yt::shared_auth().init(dir + "auth.json");
}

char* omega_config_refresh(void) {
    std::string error;
    if (!omega::cfg::refresh(error)) return to_c_json({{"error", error}});
    return to_c_json({{"ok", true}});
}

void omega_set_locale(const char* language, const char* region) {
    omega::cfg::set_locale(language ? language : "", region ? region : "");
}

char* omega_update_check(void) { return to_c_json(omega::cfg::check_update(kVersion)); }

void omega_auth_init(const char* token_path) { omega::yt::shared_auth().init(token_path ? token_path : ""); }

char* omega_auth_status(void) { return to_c_json({{"signedIn", omega::yt::shared_auth().signed_in()}}); }

char* omega_auth_begin(void) {
    omega::yt::DeviceCode code;
    std::string error;
    if (!omega::yt::shared_auth().begin(code, error)) return to_c_json({{"error", error}});
    g_device_code = code;
    return to_c_json({{"deviceCode", code.device_code},
                      {"userCode", code.user_code},
                      {"verificationUrl", code.verification_url},
                      {"qrUrl", omega::yt::prefilled_verification_url(code.verification_url, code.user_code)},
                      {"interval", code.interval_s},
                      {"expiresIn", code.expires_in_s}});
}

char* omega_auth_poll(const char* device_code) {
    omega::yt::DeviceCode code = g_device_code;
    if (device_code) code.device_code = device_code;
    std::string error;
    static const char* names[] = {"pending", "slow_down", "done", "denied", "expired", "failed"};
    omega::yt::PollResult r = omega::yt::shared_auth().poll(code, error);
    return to_c_json({{"status", names[static_cast<int>(r)]}, {"error", error}});
}

void omega_auth_sign_out(void) { omega::yt::shared_auth().sign_out(); }

char* omega_qr_encode(const char* text) {
    try {
        qrcodegen::QrCode qr = qrcodegen::QrCode::encodeText(text ? text : "", qrcodegen::QrCode::Ecc::MEDIUM);
        nlohmann::json rows = nlohmann::json::array();
        for (int y = 0; y < qr.getSize(); ++y) {
            std::string row;
            for (int x = 0; x < qr.getSize(); ++x) row += qr.getModule(x, y) ? '1' : '0';
            rows.push_back(row);
        }
        return to_c_json({{"size", qr.getSize()}, {"rows", rows}});
    } catch (const std::exception& e) {
        return to_c_json({{"error", e.what()}});
    }
}

char* omega_yt_suggest(const char* query) {
    std::vector<std::string> list;
    std::string error;
    if (!omega::yt::suggest(query ? query : "", list, error)) return to_c_json({{"error", error}});
    return to_c_json({{"suggestions", list}});
}

void omega_free(void* p) { free(p); }

void omega_player_diagnose(void* window, int width, int height, const char* video_url, const char* audio_url) {
    struct Variant {
        const char* name;
        bool video, audio, display;
        const char* app_type;
    };
    const Variant variants[] = {
        {"av, display, MSE", true, true, true, "MSE"},
        {"video only", true, false, true, "MSE"},
        {"audio only", false, true, true, "MSE"},
        {"av, no display", true, true, false, "MSE"},
        {"av, no app_info", true, true, true, ""},
        {"av, type HTML5", true, true, true, "HTML5"},
        {"av, type native", true, true, true, "native"},
    };
    for (const Variant& v : variants) {
        omega::PlayerConfig cfg;
        cfg.window = window;
        cfg.width = width;
        cfg.height = height;
        cfg.video_url = video_url ? video_url : "";
        cfg.audio_url = audio_url ? audio_url : "";
        cfg.app_id = "org.omegatube.app";
        cfg.app_version = kVersion;
        cfg.app_type = v.app_type;
        cfg.use_video = v.video;
        cfg.use_audio = v.audio;
        cfg.use_display = v.display;
        OLOG("=== diag: %s", v.name);
        int result = 0;
        {
            omega::Player player(cfg);
            if (player.start()) {
                for (int i = 0; i < 80 && (result = player.prepare_result()) == 0; ++i)
                    std::this_thread::sleep_for(std::chrono::milliseconds(50));
            } else {
                OLOG("start failed: %s", player.error().c_str());
            }
        }
        OLOG("=== diag result [%s]: %s", v.name, result == 1 ? "PREPARED" : (result < 0 ? "FAILED" : "TIMEOUT"));
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }
    OLOG("=== diag done");
}
