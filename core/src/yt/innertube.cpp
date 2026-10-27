#include "yt/innertube.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cstring>
#include <mutex>

#include "util/log.h"
#include "util/remote_config.h"

namespace omega::yt {
namespace {

using json = nlohmann::json;

// One InnerTube client as described by remote-config.json ("player", "browse" or "search").
struct ClientDef {
    json client;  // context.client
    std::string user_agent, version, name_id;
};

ClientDef client_def(const char* role, const std::string& visitor, const std::string& version_override = "") {
    json c = cfg::obj({"innertube", role});
    ClientDef d;
    d.user_agent = c.value("userAgent", "");
    d.version = version_override.empty() ? c.value("clientVersion", "") : version_override;
    d.name_id = std::to_string(c.value("clientNameId", 0));
    d.client = c.value("context", json::object());
    d.client["clientName"] = c.value("clientName", "");
    d.client["clientVersion"] = d.version;
    d.client["hl"] = cfg::language();
    d.client["gl"] = cfg::region();
    if (!visitor.empty()) d.client["visitorData"] = visitor;
    return d;
}

net::Headers client_headers(const ClientDef& d, const std::string& visitor) {
    net::Headers h = {{"Content-Type", "application/json"},
                      {"User-Agent", d.user_agent},
                      {"X-YouTube-Client-Name", d.name_id},
                      {"X-YouTube-Client-Version", d.version},
                      {"Origin", "https://www.youtube.com"}};
    if (!visitor.empty()) h.emplace_back("X-Goog-Visitor-Id", visitor);
    return h;
}

std::string text_of(const json& j) {
    if (!j.is_object()) return "";
    if (j.contains("simpleText")) return j["simpleText"].get<std::string>();
    std::string out;
    for (const json& run : j.value("runs", json::array())) out += run.value("text", "");
    if (out.empty() && j.contains("content")) out = j["content"].get<std::string>();  // view-model texts
    return out;
}

std::string last_thumbnail(const json& thumbs) {
    const json& list = thumbs.value("thumbnails", json::array());
    return list.empty() ? "" : list.back().value("url", "");
}

// The account's progress on a video: resume overlay (videoRenderer / tile) or progress bar (lockup).
int watched_percent(const json& j) {
    std::string d = j.dump();
    for (const char* key : {"\"percentDurationWatched\":", "\"startPercent\":"}) {
        size_t at = d.find(key);
        if (at != std::string::npos) return std::max(0, std::min(100, atoi(d.c_str() + at + strlen(key))));
    }
    return 0;
}

void parse_video_renderer(const json& v, Feed& out) {
    FeedItem item;
    item.video_id = v.value("videoId", "");
    if (item.video_id.empty()) return;
    item.title = text_of(v.value("title", json::object()));
    item.channel = text_of(v.value("ownerText", v.value("longBylineText", json::object())));
    item.duration = text_of(v.value("lengthText", json::object()));
    item.views = text_of(v.value("shortViewCountText", v.value("viewCountText", json::object())));
    item.published = text_of(v.value("publishedTimeText", json::object()));
    item.thumbnail = "https://i.ytimg.com/vi/" + item.video_id + "/mqdefault.jpg";
    for (const json& badge : v.value("badges", json::array()))
        if (badge.dump().find("LIVE") != std::string::npos) item.is_live = true;
    item.watched_percent = watched_percent(v.value("thumbnailOverlays", json::array()));
    out.items.push_back(std::move(item));
}

// TV client layout (as in SmartTube's TileItem / ytps5): id in onSelectCommand.watchEndpoint, title in
// metadata.tileMetadataRenderer, thumbnail and duration in header.tileHeaderRenderer.
void parse_tile(const json& t, Feed& out) {
    const json& cmd = t.value("onSelectCommand", json::object());
    if (!cmd.contains("watchEndpoint")) return;  // Shorts (reelWatchEndpoint), channels, playlists
    FeedItem item;
    item.video_id = cmd["watchEndpoint"].value("videoId", "");
    if (item.video_id.empty()) return;
    for (const FeedItem& existing : out.items)
        if (existing.video_id == item.video_id) return;
    const json& meta = t.value("metadata", json::object()).value("tileMetadataRenderer", json::object());
    const json& header = t.value("header", json::object()).value("tileHeaderRenderer", json::object());
    item.title = text_of(meta.value("title", json::object()));
    if (item.title.empty()) return;
    // lines[0]: channel • views • age (sometimes split over two lines); "•" separators are skipped.
    std::vector<std::string> parts;
    for (const json& line : meta.value("lines", json::array()))
        for (const json& li : line.value("lineRenderer", json::object()).value("items", json::array())) {
            std::string text = text_of(li.value("lineItemRenderer", json::object()).value("text", json::object()));
            if (!text.empty() && text != "\xE2\x80\xA2" && text != " \xE2\x80\xA2 ") parts.push_back(text);
        }
    if (!parts.empty()) item.channel = parts[0];
    if (parts.size() > 1) item.views = parts[1];
    if (parts.size() > 2) item.published = parts[2];
    item.thumbnail = "https://i.ytimg.com/vi/" + item.video_id + "/mqdefault.jpg";
    for (const json& o : header.value("thumbnailOverlays", json::array())) {
        const json& ts = o.value("thumbnailOverlayTimeStatusRenderer", json::object());
        if (ts.empty()) continue;
        item.duration = text_of(ts.value("text", json::object()));
        if (ts.value("style", "") == "LIVE") item.is_live = true;
    }
    item.watched_percent = watched_percent(header.value("thumbnailOverlays", json::array()));
    out.items.push_back(std::move(item));
}

// 2025+ layout: lockupViewModel (contentId, lockupMetadataViewModel; duration in a thumbnail badge).
void parse_lockup(const json& l, Feed& out) {
    if (l.value("contentType", "") != "LOCKUP_CONTENT_TYPE_VIDEO") return;
    FeedItem item;
    item.video_id = l.value("contentId", "");
    if (item.video_id.size() != 11) return;
    for (const FeedItem& existing : out.items)
        if (existing.video_id == item.video_id) return;
    const json& meta = l.value("metadata", json::object()).value("lockupMetadataViewModel", json::object());
    item.title = text_of(meta.value("title", json::object()));
    if (item.title.empty()) return;
    // metadataRows: [channel], [views, age]
    std::vector<std::string> parts;
    const json& rows = meta.value("metadata", json::object())
                           .value("contentMetadataViewModel", json::object())
                           .value("metadataRows", json::array());
    for (const json& row : rows)
        for (const json& part : row.value("metadataParts", json::array())) {
            std::string text = text_of(part.value("text", json::object()));
            if (!text.empty()) parts.push_back(text);
        }
    if (!parts.empty()) item.channel = parts[0];
    if (parts.size() > 1) item.views = parts[1];
    if (parts.size() > 2) item.published = parts[2];
    item.thumbnail = "https://i.ytimg.com/vi/" + item.video_id + "/mqdefault.jpg";
    std::string image = l.value("contentImage", json::object()).dump();
    for (const char* key : {"\"text\":\""}) {
        // The duration / LIVE badge: first thumbnailBadgeViewModel text.
        size_t badge = image.find("thumbnailBadgeViewModel");
        size_t at = badge == std::string::npos ? badge : image.find(key, badge);
        if (at != std::string::npos) {
            at += strlen(key);
            item.duration = image.substr(at, image.find('"', at) - at);
            if (item.duration == "LIVE" || item.duration == "EN DIRECT") item.is_live = true;
        }
    }
    item.watched_percent = watched_percent(l.value("contentImage", json::object()));
    out.items.push_back(std::move(item));
}

// Walks the whole response: YouTube moves renderers around between layouts, so search by key.
void collect(const json& j, Feed& out) {
    if (j.is_object()) {
        for (auto it = j.begin(); it != j.end(); ++it) {
            if (it.key() == "videoRenderer" || it.key() == "compactVideoRenderer") {
                parse_video_renderer(it.value(), out);
            } else if (it.key() == "lockupViewModel") {
                parse_lockup(it.value(), out);
            } else if (it.key() == "tileRenderer") {
                parse_tile(it.value(), out);
            } else if (it.key() == "nextContinuationData" && out.continuation.empty()) {
                out.continuation = it.value().value("continuation", "");
            } else if (it.key() == "reelShelfRenderer" || it.key() == "shortsLockupViewModel") {
                continue;  // Shorts: not playable in a TV layout
            } else if (it.key() == "continuationCommand" && out.continuation.empty()) {
                out.continuation = it.value().value("token", "");
            } else {
                collect(it.value(), out);
            }
        }
    } else if (j.is_array()) {
        for (const json& e : j) collect(e, out);
    }
}

// "video/mp4; codecs=\"avc1.64002A\"" -> mime + codecs
void split_mime(const std::string& full, std::string& mime, std::string& codecs) {
    size_t semi = full.find(';');
    mime = full.substr(0, semi);
    size_t q1 = full.find('"', semi == std::string::npos ? 0 : semi);
    size_t q2 = q1 == std::string::npos ? std::string::npos : full.find('"', q1 + 1);
    codecs = (q1 != std::string::npos && q2 != std::string::npos) ? full.substr(q1 + 1, q2 - q1 - 1) : "";
}

int64_t to_int(const json& j, const char* key) {
    auto it = j.find(key);
    if (it == j.end()) return 0;
    if (it->is_number()) return it->get<int64_t>();
    if (it->is_string()) return std::stoll(it->get<std::string>());
    return 0;
}

}  // namespace

bool Client::ensure_visitor(std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!visitor_data_.empty()) return true;
    net::Request req;
    req.url = "https://www.youtube.com/";
    req.headers = {{"User-Agent", cfg::str({"innertube", "search", "userAgent"})}, {"Accept-Language", "en-US"}};
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "youtube.com: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    const std::string key = "\"VISITOR_DATA\":\"";
    size_t at = res.body.find(key);
    if (at == std::string::npos) {
        error = "VISITOR_DATA not found";
        return false;
    }
    at += key.size();
    visitor_data_ = res.body.substr(at, res.body.find('"', at) - at);
    const std::string ver_key = "\"INNERTUBE_CLIENT_VERSION\":\"";
    size_t v = res.body.find(ver_key);
    if (v != std::string::npos) {
        v += ver_key.size();
        web_client_version_ = res.body.substr(v, res.body.find('"', v) - v);
    }
    if (web_client_version_.empty()) web_client_version_ = cfg::str({"innertube", "search", "clientVersion"});
    OLOG("yt: visitorData %zu chars, web client %s", visitor_data_.size(), web_client_version_.c_str());
    return true;
}

net::Headers Client::stream_headers() const { return {{"User-Agent", cfg::str({"innertube", "player", "userAgent"})}}; }

bool Client::player(const std::string& video_id, VideoInfo& out, std::string& error) {
    if (!ensure_visitor(error)) return false;

    ClientDef def = client_def("player", visitor_data_);
    json body = {
        {"context", {{"client", def.client}}},
        {"videoId", video_id},
        {"contentCheckOk", true},
        {"racyCheckOk", true},
    };
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "player: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }

    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "player: invalid json";
        return false;
    }
    const json& ps = r.value("playabilityStatus", json::object());
    std::string status = ps.value("status", "");
    if (status != "OK") {
        error = "playability " + status + ": " + ps.value("reason", "");
        return false;
    }
    const json& details = r.value("videoDetails", json::object());
    out.id = video_id;
    out.title = details.value("title", "");
    out.author = details.value("author", "");
    out.length_s = to_int(details, "lengthSeconds");
    out.formats.clear();
    for (const json& f : r["streamingData"].value("adaptiveFormats", json::array())) {
        if (!f.contains("url")) continue;  // ciphered formats are not supported with this client
        Format fmt;
        fmt.itag = static_cast<int>(to_int(f, "itag"));
        fmt.url = f.value("url", "");
        split_mime(f.value("mimeType", ""), fmt.mime, fmt.codecs);
        fmt.width = static_cast<int>(to_int(f, "width"));
        fmt.height = static_cast<int>(to_int(f, "height"));
        fmt.fps = static_cast<int>(to_int(f, "fps"));
        fmt.bitrate = to_int(f, "bitrate");
        fmt.content_length = to_int(f, "contentLength");
        fmt.audio_sample_rate = static_cast<int>(to_int(f, "audioSampleRate"));
        fmt.audio_channels = static_cast<int>(to_int(f, "audioChannels"));
        out.formats.push_back(std::move(fmt));
    }
    OLOG("yt: '%s' by %s, %llds, %zu direct formats", out.title.c_str(), out.author.c_str(),
         static_cast<long long>(out.length_s), out.formats.size());
    return true;
}

bool Client::search(const std::string& query, const std::string& continuation, Feed& out, std::string& error) {
    if (!ensure_visitor(error)) return false;
    ClientDef def = client_def("search", visitor_data_, web_client_version_);
    json body = {{"context", {{"client", def.client}}}};
    if (continuation.empty()) {
        body["query"] = query;
        body["params"] = "EgIQAQ%3D%3D";  // filter: videos only
    } else {
        body["continuation"] = continuation;
    }
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "search: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "search: invalid json";
        return false;
    }
    out = Feed{};
    collect(r, out);
    OLOG("yt: search '%s'%s -> %zu items", query.c_str(), continuation.empty() ? "" : " (next page)", out.items.size());
    return true;
}

bool Client::related(const std::string& video_id, Feed& out, std::string& error) {
    if (!ensure_visitor(error)) return false;
    ClientDef def = client_def("search", visitor_data_, web_client_version_);
    json body = {{"context", {{"client", def.client}}}, {"videoId", video_id}};
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "next: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "next: invalid json";
        return false;
    }
    out = Feed{};
    // Only the secondary results column: the primary one repeats the current video.
    const json* secondary = nullptr;
    try {
        secondary = &r.at("contents").at("twoColumnWatchNextResults").at("secondaryResults");
    } catch (const json::exception&) {
    }
    collect(secondary ? *secondary : r, out);
    out.items.erase(std::remove_if(out.items.begin(), out.items.end(),
                                   [&](const FeedItem& i) { return i.video_id == video_id; }),
                    out.items.end());
    OLOG("yt: related %s -> %zu items", video_id.c_str(), out.items.size());
    return true;
}

bool Client::browse(const std::string& browse_id, const std::string& continuation, const std::string& access_token,
                    Feed& out, std::string& error) {
    ClientDef def = client_def("browse", "");
    json body = {{"context", {{"client", def.client}}}};
    if (continuation.empty()) body["browseId"] = browse_id;
    else body["continuation"] = continuation;
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";
    req.headers = client_headers(def, "");
    req.headers.emplace_back("Authorization", "Bearer " + access_token);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "browse " + browse_id + ": " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "browse: invalid json";
        return false;
    }
    out = Feed{};
    collect(r, out);
    OLOG("yt: browse %s%s -> %zu items", browse_id.c_str(), continuation.empty() ? "" : " (next page)",
         out.items.size());
    return true;
}

bool suggest(const std::string& query, std::vector<std::string>& out, std::string& error) {
    std::string q;
    for (unsigned char c : query) {
        if (isalnum(c) || c == '-' || c == '_' || c == '.') {
            q += static_cast<char>(c);
        } else {
            char hex[4];
            snprintf(hex, sizeof(hex), "%%%02X", c);
            q += hex;
        }
    }
    net::Request req;
    req.url = "https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&ie=utf-8&oe=utf-8&hl=" + cfg::language() + "&q=" + q;
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "suggest: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);  // ["query", ["s1", "s2", ...]]
    out.clear();
    if (r.is_array() && r.size() > 1 && r[1].is_array())
        for (const json& s : r[1])
            if (s.is_string()) out.push_back(s.get<std::string>());
    return true;
}

Client& shared_client() {
    static Client client;
    return client;
}

std::string Format::video_codec() const {
    if (!is_video()) return "";
    if (mime == "video/mp4" && codecs.compare(0, 4, "avc1") == 0) return "avc1";
    // VP9 profile 0 only: YouTube labels it "vp9"; "vp09.02..." is 10-bit HDR.
    if (mime == "video/webm" && (codecs == "vp9" || codecs.compare(0, 8, "vp09.00.") == 0)) return "vp9";
    // av01.<profile>.<level><tier>.<bitdepth>[...]: main profile, 8-bit (the 10-bit ones are HDR).
    if (mime == "video/mp4" && codecs.compare(0, 7, "av01.0.") == 0) {
        size_t dot = codecs.find('.', 7);
        if (dot != std::string::npos && codecs.compare(dot + 1, 2, "08") == 0) return "av01";
    }
    return "";
}

std::vector<const Format*> rank_videos(const VideoInfo& info, int max_height, const std::vector<std::string>& codecs) {
    auto pref = [&codecs](const Format* f) {
        return std::find(codecs.begin(), codecs.end(), f->video_codec()) - codecs.begin();
    };
    std::vector<const Format*> out;
    for (const Format& f : info.formats)
        if (f.height <= max_height && pref(&f) < static_cast<long>(codecs.size())) out.push_back(&f);
    std::stable_sort(out.begin(), out.end(), [&](const Format* a, const Format* b) {
        if (a->height != b->height) return a->height > b->height;
        if (a->fps != b->fps) return a->fps > b->fps;
        return pref(a) < pref(b);
    });
    return out;
}

std::vector<std::string> parse_codec_list(const std::string& list) {
    std::vector<std::string> out;
    size_t start = 0;
    while (start <= list.size()) {
        size_t comma = list.find(',', start);
        if (comma == std::string::npos) comma = list.size();
        std::string item = list.substr(start, comma - start);
        item.erase(0, item.find_first_not_of(' '));
        item.erase(item.find_last_not_of(' ') + 1);
        if (!item.empty()) out.push_back(item);
        start = comma + 1;
    }
    return out;
}

const Format* pick_audio(const VideoInfo& info) {
    const Format* best = nullptr;
    for (const Format& f : info.formats) {
        if (!f.is_audio() || f.mime != "audio/mp4" || f.codecs.compare(0, 4, "mp4a") != 0) continue;
        if (!best || f.bitrate > best->bitrate) best = &f;
    }
    return best;
}

}  // namespace omega::yt
