#include "omega/core.h"

#include <algorithm>
#include <chrono>
#include <climits>
#include <cstdlib>
#include <cstring>
#include <mutex>

#include <nlohmann/json.hpp>
#include <stdexcept>
#include <string>
#include <thread>

#include "player/player.h"
#include "yt/auth.h"
#include "yt/innertube.h"
#include "yt/js_solver.h"
#include "yt/sponsorblock.h"
#include "yt/watch.h"

#include <memory>

#include <qrcodegen/qrcodegen.hpp>
#include "util/log.h"
#include "util/remote_config.h"

namespace {
constexpr const char* kVersion = OMEGA_VERSION;  // from the repo's VERSION file
}

struct omega_player {
    omega::Player impl;
    std::unique_ptr<omega::yt::WatchReporter> history;  // signed-in YouTube playback only
    nlohmann::json formats = {{"available", nlohmann::json::array()}};  // see omega_player_formats
    explicit omega_player(omega::PlayerConfig cfg) : impl(std::move(cfg)) {}
    ~omega_player() { history.reset(); }  // stop pinging before the player goes away
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

namespace {

omega_player* start_youtube(void* window, int width, int height, omega::yt::Client& client,
                            const omega::yt::VideoInfo& info, const omega::yt::Format* video,
                            const omega::yt::Format* audio, int64_t start_ms) {
    OLOG("youtube: video itag %d %dx%d@%d %s %lld kb/s, audio itag %d %s %d Hz", video->itag, video->width,
         video->height, video->fps, video->codecs.c_str(), static_cast<long long>(video->bitrate / 1000), audio->itag,
         audio->codecs.c_str(), audio->audio_sample_rate);

    omega::PlayerConfig cfg;
    cfg.window = window;
    cfg.width = width;
    cfg.height = height;
    cfg.video_url = video->url;
    cfg.audio_url = audio->url;
    // Stream requests carry the User-Agent of the client that resolved them (primary or fallback).
    cfg.stream_headers = info.user_agent.empty() ? client.stream_headers()
                                                 : omega::net::Headers{{"User-Agent", info.user_agent}};
    cfg.fps = video->fps > 0 ? video->fps : 30;
    cfg.max_width = video->width;
    cfg.max_height = video->height;
    cfg.duration_ms = info.length_s * 1000;
    cfg.live = info.is_live;
    cfg.start_ms = info.is_live ? 0 : start_ms;
    // Expired URLs (403/410): ask InnerTube again and keep the same formats (and audio language).
    std::string id = info.id, audio_track = audio->audio_track_id;
    int video_itag = video->itag, audio_itag = audio->itag;
    cfg.refresh_urls = [id, video_itag, audio_itag, audio_track](std::string& v, std::string& a) {
        omega::yt::Client fresh_client;
        omega::yt::VideoInfo fresh;
        std::string err;
        if (!fresh_client.player(id, fresh, err)) {
            OLOG("refresh %s: %s", id.c_str(), err.c_str());
            return false;
        }
        for (const auto& f : fresh.formats) {
            if (f.itag == video_itag) v = f.url;
            if (f.itag == audio_itag && f.audio_track_id == audio_track) a = f.url;
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
    if (omega::yt::shared_auth().signed_in() && !info.is_live) {
        omega::Player* player = &p->impl;
        p->history = std::make_unique<omega::yt::WatchReporter>(id, info.length_s * 1000, [player] {
            omega::PlayerStats s = player->stats();
            return s.state == 3 ? s.position_ms : int64_t(-1);  // only while PLAYING
        });
    }
    return p;
}

}  // namespace

namespace {

// Why the last omega_player_create_youtube failed (omega_player_last_error).
std::mutex g_play_error_mutex;
nlohmann::json g_play_error = nlohmann::json::object();

void set_play_error(const std::string& kind, const std::string& status, const std::string& reason,
                    const std::string& detail) {
    std::lock_guard<std::mutex> lock(g_play_error_mutex);
    g_play_error = {{"kind", kind},
                    {"status", status},
                    {"reason", reason},
                    {"detail", detail},
                    {"signedIn", omega::yt::shared_auth().signed_in()}};
}

// YouTube's playabilityStatus (or HTTP refusal) -> the app's error kinds.
std::string refusal_kind(const std::string& status) {
    if (status == "LOGIN_REQUIRED") return "login";
    if (status.rfind("AGE_", 0) == 0 || status == "CONTENT_CHECK_REQUIRED") return "age";
    if (status == "LIVE_STREAM_OFFLINE") return "upcoming";
    if (status == "UNPLAYABLE") return "unplayable";
    if (status == "ERROR") return "unavailable";
    if (status == "HTTP_429") return "rate_limited";
    if (status.rfind("HTTP_", 0) == 0) return "http";
    if (status == "NETWORK") return "network";
    return "other";
}

// Audio languages of a multi-track video, in YouTube's order (one entry per track id).
nlohmann::json audio_tracks_json(const omega::yt::VideoInfo& info) {
    nlohmann::json tracks = nlohmann::json::array();
    std::vector<std::string> seen;
    for (const auto& f : info.formats) {
        if (!f.is_audio() || f.audio_track_id.empty() ||
            std::find(seen.begin(), seen.end(), f.audio_track_id) != seen.end())
            continue;
        seen.push_back(f.audio_track_id);
        tracks.push_back({{"id", f.audio_track_id}, {"name", f.audio_track_name}, {"default", f.audio_default}});
    }
    return tracks;
}

}  // namespace

omega_player* omega_player_create_youtube(void* window, int width, int height, const char* video_id,
                                          int max_height, const char* codecs, int64_t start_ms,
                                          const char* audio_track) {
    omega::yt::Client client;
    omega::yt::VideoInfo info;
    std::string error;
    if (!client.player(video_id ? video_id : "", info, error)) {
        OLOG("youtube %s: %s", video_id ? video_id : "-", error.c_str());
        const omega::yt::Client::Refusal& r = client.refusal();
        set_play_error(refusal_kind(r.status), r.status, r.reason, error);
        return nullptr;
    }
    std::vector<std::string> prefs = omega::yt::parse_codec_list(codecs && *codecs ? codecs : "avc1,vp9");
    std::vector<const omega::yt::Format*> videos =
        omega::yt::rank_videos(info, max_height > 0 ? max_height : 1080, prefs);
    const omega::yt::Format* audio = omega::yt::pick_audio(info, audio_track ? audio_track : "");
    if (videos.empty() || !audio) {
        OLOG("youtube %s: no playable video (%s) / AAC audio format", video_id, codecs ? codecs : "-");
        set_play_error("formats", "", "", videos.empty() ? "no video format for " + std::string(codecs ? codecs : "-")
                                                          : "no AAC audio format");
        return nullptr;
    }
    // A codec the TV refuses (ESPlayer rejects its stream info) is dropped and the next best format tried.
    std::vector<std::string> refused;
    for (const omega::yt::Format* video : videos) {
        if (std::find(refused.begin(), refused.end(), video->video_codec()) != refused.end()) continue;
        if (omega_player* p = start_youtube(window, width, height, client, info, video, audio, start_ms)) {
            // What the quality panel offers: the format each height cap would pick (codecs the TV refused aside).
            auto entry = [](const omega::yt::Format* f) {
                return nlohmann::json{
                    {"width", f->width}, {"height", f->height}, {"fps", f->fps}, {"codec", f->video_codec()}};
            };
            nlohmann::json available = nlohmann::json::array();
            int last_height = -1;
            for (const omega::yt::Format* f : omega::yt::rank_videos(info, INT32_MAX, prefs)) {
                if (f->height == last_height ||
                    std::find(refused.begin(), refused.end(), f->video_codec()) != refused.end())
                    continue;
                last_height = f->height;
                available.push_back(entry(f));
            }
            p->formats = {{"current", entry(video)},  {"available", available}, {"live", info.is_live},
                          {"title", info.title}, {"author", info.author}, {"description", info.description},
                          {"views", info.views}, {"channelId", info.channel_id}, {"audioTrack", audio->audio_track_id}};
            p->formats["audioTracks"] = audio_tracks_json(info);
            nlohmann::json captions = nlohmann::json::array(), translations = nlohmann::json::array();
            for (const auto& c : info.captions)
                captions.push_back({{"url", c.url}, {"lang", c.lang}, {"name", c.name}, {"auto", c.automatic},
                                    {"translatable", c.translatable}});
            for (const auto& t : info.translations) translations.push_back({{"code", t.first}, {"name", t.second}});
            p->formats["captions"] = captions;
            p->formats["translations"] = translations;
            return p;
        }
        refused.push_back(video->video_codec());
    }
    std::vector<std::string> last = omega::log::tail(1);  // "player start failed: ..."
    set_play_error("player", "", "", last.empty() ? "" : last.back());
    return nullptr;
}

void omega_player_toggle_pause(omega_player* p) {
    if (p) p->impl.toggle_pause();
}

int omega_player_seek(omega_player* p, int64_t position_ms) { return p && p->impl.seek(position_ms) ? 0 : -1; }

int omega_player_set_rate(omega_player* p, double rate) { return p && p->impl.set_rate(rate) ? 0 : -1; }

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
char* to_c_json(const nlohmann::json& j);
}

char* omega_player_last_error(void) {
    std::lock_guard<std::mutex> lock(g_play_error_mutex);
    return to_c_json(g_play_error);
}

char* omega_player_formats(omega_player* p) {
    return to_c_json(p ? p->formats : nlohmann::json{{"available", nlohmann::json::array()}});
}

namespace {

char* to_c_json(const nlohmann::json& j) {
    std::string s = j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace);
    char* out = static_cast<char*>(malloc(s.size() + 1));
    memcpy(out, s.c_str(), s.size() + 1);
    return out;
}

nlohmann::json items_json(const std::vector<omega::yt::FeedItem>& list) {
    nlohmann::json items = nlohmann::json::array();
    for (const auto& it : list)
        items.push_back({{"id", it.video_id},
                         {"title", it.title},
                         {"channel", it.channel},
                         {"duration", it.duration},
                         {"views", it.views},
                         {"published", it.published},
                         {"thumbnail", it.thumbnail},
                         {"live", it.is_live},
                         {"watched", it.watched_percent},
                         {"playlistId", it.playlist_id},
                         {"kind", it.kind}});
    return items;
}

nlohmann::json feed_json(const omega::yt::Feed& feed) {
    nlohmann::json j = {{"items", items_json(feed.items)}, {"continuation", feed.continuation}};
    if (!feed.highlights.empty()) j["highlights"] = items_json(feed.highlights);
    if (!feed.shelves.empty()) {
        nlohmann::json shelves = nlohmann::json::array();
        for (const auto& s : feed.shelves)
            shelves.push_back({{"title", s.title}, {"items", items_json(s.items)}, {"continuation", s.continuation}});
        j["shelves"] = shelves;
    }
    return j;
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

char* omega_yt_playlist(const char* video_id, const char* playlist_id) {
    omega::yt::Feed feed;
    std::string title, error;
    if (!omega::yt::shared_client().playlist(video_id ? video_id : "", playlist_id ? playlist_id : "", feed, title,
                                             error))
        return to_c_json({{"error", error}});
    nlohmann::json j = feed_json(feed);
    j["title"] = title;
    return to_c_json(j);
}

char* omega_yt_playlist_page(const char* playlist_id, const char* continuation) {
    omega::yt::Feed feed;
    std::string title, error;
    if (!omega::yt::shared_client().playlist_page(playlist_id ? playlist_id : "", continuation ? continuation : "",
                                                  feed, title, error))
        return to_c_json({{"error", error}});
    nlohmann::json j = feed_json(feed);
    j["title"] = title;
    return to_c_json(j);
}

char* omega_yt_related(const char* video_id) {
    omega::yt::Feed feed;
    std::string error;
    std::string token = omega::yt::shared_auth().signed_in() ? omega::yt::shared_auth().access_token(error) : "";
    error.clear();
    if (!omega::yt::shared_client().related(video_id ? video_id : "", token, feed, error))
        return to_c_json({{"error", error}});
    return to_c_json(feed_json(feed));
}

char* omega_yt_details(const char* video_id) {
    std::string id = video_id ? video_id : "", error;
    std::string token = omega::yt::shared_auth().signed_in() ? omega::yt::shared_auth().access_token(error) : "";
    omega::yt::VideoDetails d;
    if (!omega::yt::shared_client().details(id, token, d, error)) return to_c_json({{"error", error}});
    int64_t likes = -1, dislikes = -1;
    std::string vote_error;
    if (!omega::yt::vote_counts(id, likes, dislikes, vote_error)) OLOG("details %s: %s", id.c_str(), vote_error.c_str());
    return to_c_json({{"channelId", d.channel_id},
                      {"channel", d.channel},
                      {"channelAvatar", d.channel_avatar},
                      {"subscribers", d.subscribers},
                      {"date", d.date},
                      {"likes", likes},
                      {"dislikes", dislikes},
                      {"likeStatus", d.like_status},
                      {"subscribed", d.subscribed}});
}

namespace {

// Runs an account action with a fresh access token: {"ok":true} or {"error"}.
template <typename Fn>
char* account_action(Fn action) {
    std::string error;
    std::string token = omega::yt::shared_auth().access_token(error);
    if (token.empty()) return to_c_json({{"error", error.empty() ? "signed out" : error}});
    if (!action(token, error)) return to_c_json({{"error", error}});
    return to_c_json({{"ok", true}});
}

}  // namespace

char* omega_yt_rate(const char* video_id, const char* rating) {
    std::string id = video_id ? video_id : "", r = rating ? rating : "none";
    return account_action([&](const std::string& token, std::string& error) {
        return omega::yt::rate_video(id, r, token, error);
    });
}

char* omega_yt_subscribe(const char* channel_id, int subscribe) {
    std::string id = channel_id ? channel_id : "";
    return account_action([&](const std::string& token, std::string& error) {
        return omega::yt::set_subscribed(id, subscribe != 0, token, error);
    });
}

char* omega_yt_channel(const char* channel_id, const char* continuation) {
    omega::yt::Feed feed;
    omega::yt::ChannelInfo header;
    std::string error;
    if (!omega::yt::shared_client().channel(channel_id ? channel_id : "", continuation ? continuation : "", feed, header,
                                            error))
        return to_c_json({{"error", error}});
    nlohmann::json j = feed_json(feed);
    j["title"] = header.title;
    j["avatar"] = header.avatar;
    j["subscribers"] = header.subscribers;
    return to_c_json(j);
}

char* omega_yt_captions(const char* url, const char* translate_to) {
    std::vector<omega::yt::CaptionCue> cues;
    std::string error;
    if (!omega::yt::fetch_captions(url ? url : "", translate_to ? translate_to : "", cues, error))
        return to_c_json({{"error", error}});
    nlohmann::json list = nlohmann::json::array();
    for (const auto& c : cues) list.push_back({{"start", c.start_ms}, {"end", c.end_ms}, {"text", c.text}});
    return to_c_json({{"cues", list}});
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
    if (!dir.empty()) omega::yt::shared_decipher().set_cache_dir(dir.substr(0, dir.size() - 1));
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

char* omega_auth_account(void) {
    std::string error;
    std::string token = omega::yt::shared_auth().access_token(error);
    if (token.empty()) return to_c_json({{"error", error.empty() ? "signed out" : error}});
    omega::yt::AccountInfo account;
    if (!omega::yt::account_info(token, account, error)) return to_c_json({{"error", error}});
    return to_c_json({{"name", account.name}, {"handle", account.handle}, {"photo", account.photo}});
}

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
