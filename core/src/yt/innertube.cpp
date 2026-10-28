#include "yt/innertube.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <functional>
#include <mutex>

#include "util/log.h"
#include "util/remote_config.h"
#include "yt/auth.h"
#include "yt/js_solver.h"

namespace omega::yt {
namespace {

using json = nlohmann::json;

// One InnerTube client as described by remote-config.json ("player", "browse" or "search").
struct ClientDef {
    json client;  // context.client
    std::string user_agent, version, name_id;
};

ClientDef client_def_from(const json& c, const std::string& visitor, const std::string& version_override = "") {
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

ClientDef client_def(const char* role, const std::string& visitor, const std::string& version_override = "") {
    return client_def_from(cfg::obj({"innertube", role}), visitor, version_override);
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

// First value of `key` anywhere under j (depth-first), or nullptr.
const json* find_key(const json& j, const char* key) {
    if (j.is_object()) {
        auto it = j.find(key);
        if (it != j.end()) return &*it;
        for (const json& v : j)
            if (const json* f = find_key(v, key)) return f;
    } else if (j.is_array()) {
        for (const json& v : j)
            if (const json* f = find_key(v, key)) return f;
    }
    return nullptr;
}

// A playlist card and a video card of the same first video are different entries.
bool already_listed(const Feed& out, const FeedItem& item) {
    for (const FeedItem& existing : out.items)
        if (existing.video_id == item.video_id && existing.playlist_id == item.playlist_id) return true;
    return false;
}

std::string playlist_kind(const std::string& playlist_id) { return playlist_id.compare(0, 2, "RD") == 0 ? "mix" : "playlist"; }

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
    // Broadcasts have no length and count viewers ("18 k spectateurs" / "1.2K watching") instead of views.
    if (item.duration.empty() && (item.views.find("spectateur") != std::string::npos ||
                                  item.views.find("watching") != std::string::npos))
        item.is_live = true;
    item.watched_percent = watched_percent(v.value("thumbnailOverlays", json::array()));
    out.items.push_back(std::move(item));
}

// A row of a playlist page (WEB browse VL<id>): no channel avatar, "views • age" in videoInfo.
void parse_playlist_video(const json& v, Feed& out) {
    if (!v.value("isPlayable", true)) return;  // deleted / private entries
    FeedItem item;
    item.video_id = v.value("videoId", "");
    if (item.video_id.empty()) return;
    item.title = text_of(v.value("title", json::object()));
    item.channel = text_of(v.value("shortBylineText", json::object()));
    item.duration = text_of(v.value("lengthText", json::object()));
    const json& runs = v.value("videoInfo", json::object()).value("runs", json::array());
    if (!runs.empty()) item.views = runs[0].value("text", "");
    if (runs.size() >= 3) item.published = runs[2].value("text", "");
    item.thumbnail = "https://i.ytimg.com/vi/" + item.video_id + "/mqdefault.jpg";
    item.watched_percent = watched_percent(v.value("thumbnailOverlays", json::array()));
    out.items.push_back(std::move(item));
}

// TV client layout (as in SmartTube's TileItem / ytps5): id in onSelectCommand.watchEndpoint, title in
// metadata.tileMetadataRenderer, thumbnail and duration in header.tileHeaderRenderer.
void parse_tile(const json& t, Feed& out) {
    const json& cmd = t.value("onSelectCommand", json::object());
    if (!cmd.contains("watchEndpoint")) return;  // Shorts (reelWatchEndpoint), channels
    FeedItem item;
    item.video_id = cmd["watchEndpoint"].value("videoId", "");
    if (item.video_id.empty()) return;
    if (t.value("contentType", "") == "TILE_CONTENT_TYPE_PLAYLIST") {
        item.playlist_id = cmd["watchEndpoint"].value("playlistId", "");
        if (item.playlist_id.empty()) return;
        item.kind = playlist_kind(item.playlist_id);
    }
    if (already_listed(out, item)) return;
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
    std::string type = l.value("contentType", "");
    FeedItem item;
    if (type == "LOCKUP_CONTENT_TYPE_PLAYLIST") {
        // Playlist / mix: contentId is the list; play it from its first video (the card's watchEndpoint).
        item.playlist_id = l.value("contentId", "");
        const json* watch = find_key(l, "watchEndpoint");
        if (item.playlist_id.empty() || !watch || !watch->is_object()) return;
        item.video_id = watch->value("videoId", "");
        item.kind = playlist_kind(item.playlist_id);
    } else if (type == "LOCKUP_CONTENT_TYPE_VIDEO") {
        item.video_id = l.value("contentId", "");
    } else {
        return;
    }
    if (item.video_id.size() != 11) return;
    if (already_listed(out, item)) return;
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
    if (item.kind == "mix") item.duration = "MIX";  // its badge is an icon
    if (item.views == "Playlist") item.views.clear();  // the type label, already shown by the badge
    item.watched_percent = watched_percent(l.value("contentImage", json::object()));
    out.items.push_back(std::move(item));
}

// Walks the whole response: YouTube moves renderers around between layouts, so search by key.
// highlights: where titled TV shelves go (subscriptions), instead of the main list.
void collect(const json& j, Feed& out, Feed* highlights = nullptr) {
    if (j.is_object()) {
        for (auto it = j.begin(); it != j.end(); ++it) {
            if (it.key() == "shelfRenderer" && it.value().is_object()) {
                const json& shelf = it.value();
                if (shelf.value("tvhtml5ShelfRendererType", "") == "TVHTML5_SHELF_RENDERER_TYPE_SHORTS") continue;
                if (highlights && shelf.contains("headerRenderer")) collect(shelf, *highlights);
                else collect(shelf, out, highlights);
            } else if (it.key() == "videoRenderer" || it.key() == "compactVideoRenderer") {
                parse_video_renderer(it.value(), out);
            } else if (it.key() == "lockupViewModel") {
                parse_lockup(it.value(), out);
            } else if (it.key() == "playlistVideoRenderer") {
                parse_playlist_video(it.value(), out);
            } else if (it.key() == "tileRenderer") {
                parse_tile(it.value(), out);
            } else if (it.key() == "nextContinuationData" && out.continuation.empty()) {
                out.continuation = it.value().value("continuation", "");
            } else if (it.key() == "reelShelfRenderer" || it.key() == "shortsLockupViewModel") {
                continue;  // Shorts: not playable in a TV layout
            } else if (it.key() == "continuationCommand" && out.continuation.empty() && it.value().contains("token")) {
                out.continuation = it.value().value("token", "");
            } else {  // continuationItemViewModel: the token sits in a nested continuationCommand
                collect(it.value(), out, highlights);
            }
        }
    } else if (j.is_array()) {
        for (const json& e : j) collect(e, out, highlights);
    }
}

// The TV home: one Shelf per titled shelfRenderer (Shorts and ads left out); the page continuation is the one
// outside the shelves (theirs only extend their own row).
void collect_shelves(const json& j, Feed& out) {
    if (j.is_object()) {
        for (auto it = j.begin(); it != j.end(); ++it) {
            if (it.key() == "shelfRenderer" && it.value().is_object()) {
                const json& s = it.value();
                if (s.value("tvhtml5ShelfRendererType", "") == "TVHTML5_SHELF_RENDERER_TYPE_SHORTS") continue;
                Shelf shelf;
                if (s.contains("headerRenderer"))
                    if (const json* title = find_key(s["headerRenderer"], "title")) shelf.title = text_of(*title);
                Feed row;
                collect(s.value("content", json::object()), row);
                if (row.items.empty()) continue;
                shelf.items = std::move(row.items);
                shelf.continuation = row.continuation;
                out.shelves.push_back(std::move(shelf));
            } else if (it.key() == "nextContinuationData" && out.continuation.empty()) {
                out.continuation = it.value().value("continuation", "");
            } else if (it.key() != "adSlotRenderer") {
                collect_shelves(it.value(), out);
            }
        }
    } else if (j.is_array()) {
        for (const json& e : j) collect_shelves(e, out);
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

// Subtitle tracks and the machine-translation targets (captions.playerCaptionsTracklistRenderer).
void parse_captions(const json& captions, VideoInfo& out) {
    out.captions.clear();
    out.translations.clear();
    const json& list = captions.value("playerCaptionsTracklistRenderer", json::object());
    for (const json& t : list.value("captionTracks", json::array())) {
        CaptionTrack track;
        track.url = t.value("baseUrl", "");
        if (track.url.empty()) continue;
        if (track.url[0] == '/') track.url = "https://www.youtube.com" + track.url;
        track.lang = t.value("languageCode", "");
        track.name = text_of(t.value("name", json::object()));
        track.automatic = t.value("kind", "") == "asr";
        track.translatable = t.value("isTranslatable", false);
        if (track.name.empty()) track.name = track.lang;
        out.captions.push_back(std::move(track));
    }
    for (const json& l : list.value("translationLanguages", json::array())) {
        std::string code = l.value("languageCode", "");
        if (!code.empty()) out.translations.emplace_back(code, text_of(l.value("languageName", json::object())));
    }
}

// `url` without its `name` query parameter.
std::string drop_param(const std::string& url, const std::string& name) {
    size_t mark = url.find('?');
    if (mark == std::string::npos) return url;
    std::string out = url.substr(0, mark + 1);
    bool first = true;
    for (size_t at = mark + 1; at <= url.size();) {
        size_t end = url.find('&', at);
        if (end == std::string::npos) end = url.size();
        std::string pair = url.substr(at, end - at);
        if (!pair.empty() && pair.substr(0, pair.find('=')) != name) {
            out += (first ? "" : "&") + pair;
            first = false;
        }
        at = end + 1;
    }
    return out;
}

// POST to an InnerTube endpoint as the signed-in account (TV client + Bearer), as YouTube's TV app does.
net::Response post_as_account(const std::string& endpoint, json body, const std::string& access_token) {
    ClientDef def = client_def("browse", "");
    body["context"] = {{"client", def.client}};
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/" + endpoint + "?prettyPrint=false";
    req.headers = client_headers(def, "");
    req.headers.emplace_back("Authorization", "Bearer " + access_token);
    req.body = body.dump();
    return net::fetch(req);
}

std::string http_error(const char* what, const net::Response& res) {
    return std::string(what) + ": " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
}

// Every text of `key` objects under j (e.g. all "metadataParts" texts of a header).
void texts_under(const json& j, const char* key, std::vector<std::string>& out) {
    if (j.is_object()) {
        for (auto it = j.begin(); it != j.end(); ++it) {
            if (it.key() == key && it.value().is_array()) {
                for (const json& part : it.value()) out.push_back(text_of(part.value("text", json::object())));
            } else {
                texts_under(it.value(), key, out);
            }
        }
    } else if (j.is_array()) {
        for (const json& e : j) texts_under(e, key, out);
    }
}

bool contains_any(const std::string& s, std::initializer_list<const char*> words) {
    for (const char* w : words)
        if (s.find(w) != std::string::npos) return true;
    return false;
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

namespace {

// The player clients to try, in order (remote-config innertube.playerClients, as yt-dlp's client list).
// Older configs only have "player" (direct URLs) and "playerFallback" (TV client, deciphered, account retry).
// include_disabled: also the entries with "enabled": false (dev tooling picking one by name).
std::vector<json> player_clients(bool include_disabled) {
    json all = cfg::snapshot();
    const json* list = nullptr;
    if (all.contains("innertube") && all["innertube"].contains("playerClients")) list = &all["innertube"]["playerClients"];
    std::vector<json> out;
    if (list && list->is_array()) {
        for (const json& c : *list)
            if (c.is_object() && !c.value("clientName", "").empty() && (include_disabled || c.value("enabled", true)))
                out.push_back(c);
        if (!out.empty()) return out;
    }
    json primary = cfg::obj({"innertube", "player"});
    if (!primary.value("clientName", "").empty()) out.push_back(primary);
    json fallback = cfg::obj({"innertube", "playerFallback"});
    if (!fallback.value("clientName", "").empty()) {
        fallback["decipher"] = true;
        fallback["auth"] = "retry";
        out.push_back(fallback);
    }
    return out;
}

std::atomic<int> g_last_good_client{-1};  // index of the client that last worked: tried first

}  // namespace

bool Client::player(const std::string& video_id, VideoInfo& out, std::string& error, const std::string& only_client) {
    refusal_ = {};
    if (!ensure_visitor(error)) {
        refusal_ = {"NETWORK", error};
        return false;
    }
    std::vector<json> clients = player_clients(!only_client.empty());
    std::vector<size_t> order;
    int good = g_last_good_client.load();
    if (good >= 0 && static_cast<size_t>(good) < clients.size()) order.push_back(static_cast<size_t>(good));
    for (size_t i = 0; i < clients.size(); i++)
        if (static_cast<int>(i) != good) order.push_back(i);

    std::string errors;
    for (size_t i : order) {
        const json& c = clients[i];
        std::string name = c.value("clientName", "");
        if (!only_client.empty() && name != only_client) continue;
        std::string err;
        if (try_client(c, video_id, out, err)) {
            if (static_cast<int>(i) != good) OLOG("yt: %s played through %s", video_id.c_str(), name.c_str());
            g_last_good_client = static_cast<int>(i);
            return true;
        }
        OLOG("yt: %s via %s: %s", video_id.c_str(), name.c_str(), err.c_str());
        errors += (errors.empty() ? "" : "; ") + name + ": " + err;
    }
    error = errors.empty() ? "no player client" : errors;
    return false;
}

// One client: deciphering when its URLs need the player JS, and (auth "retry") once more as the signed-in
// account when YouTube wants a login (bot check, age); auth "always" sends the account right away.
bool Client::try_client(const json& c, const std::string& video_id, VideoInfo& out, std::string& error) {
    bool decipher = c.value("decipher", false);
    if (decipher && !shared_decipher().ensure(error)) return false;
    std::string auth = c.value("auth", "never");
    std::string token;
    if (auth != "never" && shared_auth().signed_in()) {
        std::string token_error;
        token = shared_auth().access_token(token_error);
    }
    if (auth == "always" && !token.empty()) return player_with(c, video_id, out, error, token) && !out.formats.empty();
    if (player_with(c, video_id, out, error) && !out.formats.empty()) return true;
    bool account_may_help = refusal_.status == "LOGIN_REQUIRED" || refusal_.status.rfind("AGE_", 0) == 0;
    if (auth == "retry" && account_may_help && !token.empty()) {
        error.clear();
        if (player_with(c, video_id, out, error, token) && !out.formats.empty()) {
            OLOG("yt: %s played as the signed-in account", video_id.c_str());
            return true;
        }
    }
    if (error.empty()) error = decipher ? "no playable formats" : "no direct formats";
    return false;
}

bool Client::player_with(const json& c, const std::string& video_id, VideoInfo& out, std::string& error,
                         const std::string& access_token) {
    bool decipher = c.value("decipher", false);
    ClientDef def = client_def_from(c, visitor_data_);
    json body = {
        {"context", {{"client", def.client}}},
        {"videoId", video_id},
        {"contentCheckOk", true},
        {"racyCheckOk", true},
    };
    if (decipher)
        body["playbackContext"] = {{"contentPlaybackContext",
                                    {{"html5Preference", "HTML5_PREF_WANTS"},
                                     {"signatureTimestamp", shared_decipher().signature_timestamp()}}}};
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    if (!access_token.empty()) req.headers.emplace_back("Authorization", "Bearer " + access_token);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "player: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        refusal_ = res.status > 0 ? Refusal{"HTTP_" + std::to_string(res.status), ""} : Refusal{"NETWORK", res.error};
        return false;
    }

    last_response_ = res.body;
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "player: invalid json";
        return false;
    }
    const json& ps = r.value("playabilityStatus", json::object());
    std::string status = ps.value("status", "");
    if (status != "OK") {
        error = "playability " + status + ": " + ps.value("reason", "");
        refusal_ = {status, ps.value("reason", "")};
        return false;
    }
    const json& details = r.value("videoDetails", json::object());
    out.id = video_id;
    out.title = details.value("title", "");
    out.author = details.value("author", "");
    out.length_s = to_int(details, "lengthSeconds");
    out.is_live = details.value("isLive", false);
    out.description = details.value("shortDescription", "");
    out.channel_id = details.value("channelId", "");
    out.views = details.value("viewCount", "");
    parse_captions(r.value("captions", json::object()), out);
    out.formats.clear();
    out.user_agent = def.user_agent;
    size_t failed = 0;
    for (const json& f : r["streamingData"].value("adaptiveFormats", json::array())) {
        Format fmt;
        if (decipher) {
            std::string err;
            if (!shared_decipher().stream_url(f.value("url", ""), f.value("signatureCipher", ""), fmt.url, err)) {
                if (failed++ == 0) OLOG("yt: decipher itag %lld: %s", static_cast<long long>(to_int(f, "itag")), err.c_str());
                continue;
            }
        } else {
            if (!f.contains("url")) continue;  // ciphered formats: only through a deciphering client
            fmt.url = f.value("url", "");
        }
        fmt.itag = static_cast<int>(to_int(f, "itag"));
        split_mime(f.value("mimeType", ""), fmt.mime, fmt.codecs);
        fmt.width = static_cast<int>(to_int(f, "width"));
        fmt.height = static_cast<int>(to_int(f, "height"));
        fmt.fps = static_cast<int>(to_int(f, "fps"));
        fmt.bitrate = to_int(f, "bitrate");
        fmt.content_length = to_int(f, "contentLength");
        fmt.audio_sample_rate = static_cast<int>(to_int(f, "audioSampleRate"));
        fmt.audio_channels = static_cast<int>(to_int(f, "audioChannels"));
        if (f.value("isDrc", false)) continue;  // loudness-compressed duplicate of an audio format
        const json& track = f.value("audioTrack", json::object());
        fmt.audio_track_id = track.value("id", "");
        fmt.audio_track_name = track.value("displayName", "");
        fmt.audio_default = track.value("audioIsDefault", false);
        out.formats.push_back(std::move(fmt));
    }
    OLOG("yt: '%s' by %s, %llds, %zu formats (%s client%s)", out.title.c_str(), out.author.c_str(),
         static_cast<long long>(out.length_s), out.formats.size(), def.client.value("clientName", "").c_str(),
         failed ? (", " + std::to_string(failed) + " not deciphered").c_str() : "");
    return true;
}

bool Client::search(const std::string& query, const std::string& continuation, Feed& out, std::string& error) {
    if (!ensure_visitor(error)) return false;
    ClientDef def = client_def("search", visitor_data_, web_client_version_);
    json body = {{"context", {{"client", def.client}}}};
    if (continuation.empty()) {
        body["query"] = query;
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
    last_response_ = res.body;
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

bool Client::playlist(const std::string& video_id, const std::string& playlist_id, Feed& out, std::string& title,
                      std::string& error) {
    if (!ensure_visitor(error)) return false;
    ClientDef def = client_def("search", visitor_data_, web_client_version_);
    json body = {{"context", {{"client", def.client}}}, {"videoId", video_id}, {"playlistId", playlist_id}};
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "playlist: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    const json* panel = r.is_discarded() ? nullptr : find_key(r, "playlist");
    if (panel && panel->is_object() && panel->contains("playlist")) panel = &(*panel)["playlist"];
    if (!panel || !panel->is_object() || !panel->contains("contents")) {
        error = "playlist: no playlist panel";
        return false;
    }
    const json& t = panel->value("title", json());
    title = t.is_string() ? t.get<std::string>() : text_of(t);
    out = Feed{};
    // The panel is a window of the list around the current video: keep what comes after it.
    std::vector<FeedItem> all;
    size_t after = 0;
    for (const json& c : (*panel)["contents"]) {
        const json& v = c.value("playlistPanelVideoRenderer", json::object());
        FeedItem item;
        item.video_id = v.value("videoId", "");
        if (item.video_id.empty()) continue;  // unavailable entries
        item.title = text_of(v.value("title", json::object()));
        item.channel = text_of(v.value("shortBylineText", json::object()));
        item.duration = text_of(v.value("lengthText", json::object()));
        item.thumbnail = "https://i.ytimg.com/vi/" + item.video_id + "/mqdefault.jpg";
        item.playlist_id = playlist_id;
        if (v.value("selected", false) || item.video_id == video_id) after = all.size() + 1;
        all.push_back(std::move(item));
    }
    // A window of what follows (each new video asks again, so it slides along the list): a row of hundreds of
    // cards reaches coordinates the TV's GPU does not render reliably.
    constexpr size_t kWindow = 25;
    size_t first = std::min(after, all.size());
    out.items.assign(all.begin() + static_cast<long>(first),
                     all.begin() + static_cast<long>(std::min(all.size(), first + kWindow)));
    OLOG("yt: playlist %s '%s' from %s -> %zu next items", playlist_id.c_str(), title.c_str(), video_id.c_str(),
         out.items.size());
    return true;
}

bool Client::playlist_page(const std::string& playlist_id, const std::string& continuation, Feed& out,
                           std::string& title, std::string& error) {
    if (!ensure_visitor(error)) return false;
    ClientDef def = client_def("search", visitor_data_, web_client_version_);
    json body = {{"context", {{"client", def.client}}}};
    if (continuation.empty()) body["browseId"] = "VL" + playlist_id;
    else body["continuation"] = continuation;
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = http_error("playlist", res);
        return false;
    }
    last_response_ = res.body;
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "playlist: invalid json";
        return false;
    }
    out = Feed{};
    collect(r, out);
    for (FeedItem& item : out.items) item.playlist_id = playlist_id;
    if (const json* meta = find_key(r, "playlistMetadataRenderer")) title = meta->value("title", "");
    OLOG("yt: playlist page %s%s '%s' -> %zu items", playlist_id.c_str(), continuation.empty() ? "" : " (next page)",
         title.c_str(), out.items.size());
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

bool Client::details(const std::string& video_id, const std::string& access_token, VideoDetails& out,
                     std::string& error) {
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
        error = http_error("next", res);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "next: invalid json";
        return false;
    }
    out = VideoDetails{};
    if (const json* primary = find_key(r, "videoPrimaryInfoRenderer"))
        out.date = text_of(primary->value("dateText", json::object()));
    if (const json* owner = find_key(r, "videoOwnerRenderer")) {
        out.channel = text_of(owner->value("title", json::object()));
        out.channel_avatar = last_thumbnail(owner->value("thumbnail", json::object()));
        out.subscribers = text_of(owner->value("subscriberCountText", json::object()));
        // The channel link moved from navigationEndpoint into the title runs: take any browseId under the owner.
        if (const json* browse = find_key(*owner, "browseId"))
            if (browse->is_string()) out.channel_id = browse->get<std::string>();
    }
    if (out.channel_id.empty())
        if (const json* sub = find_key(r, "subscribeButtonRenderer"))
            if (sub->is_object()) out.channel_id = sub->value("channelId", "");
    if (out.channel_avatar.compare(0, 2, "//") == 0) out.channel_avatar = "https:" + out.channel_avatar;

    // The account's state: only the TV "next" answer (with the Bearer token) knows it.
    if (!access_token.empty()) {
        net::Response tv = post_as_account("next", {{"videoId", video_id}}, access_token);
        json t = tv.ok() ? json::parse(tv.body, nullptr, false) : json();
        if (t.is_object()) {
            if (const json* like = find_key(t, "likeStatus"))
                if (like->is_string()) out.like_status = like->get<std::string>();
            if (const json* sub = find_key(t, "subscribeButtonRenderer"))
                if (sub->is_object()) {
                    if (sub->contains("subscribed")) out.subscribed = sub->value("subscribed", false) ? 1 : 0;
                    if (out.channel_id.empty()) out.channel_id = sub->value("channelId", "");
                }
        } else {
            OLOG("yt: details %s: account state %s", video_id.c_str(), http_error("tv next", tv).c_str());
        }
    }
    OLOG("yt: details %s: '%s' %s, %s, like %s, subscribed %d", video_id.c_str(), out.channel.c_str(),
         out.subscribers.c_str(), out.date.c_str(), out.like_status.c_str(), out.subscribed);
    return true;
}

bool Client::channel(const std::string& channel_id, const std::string& continuation, Feed& out, ChannelInfo& header,
                     std::string& error) {
    if (!ensure_visitor(error)) return false;
    ClientDef def = client_def("search", visitor_data_, web_client_version_);
    json body = {{"context", {{"client", def.client}}}};
    if (continuation.empty()) {
        body["browseId"] = channel_id;
        body["params"] = "EgZ2aWRlb3PyBgQKAjoA";  // "Vidéos" tab
    } else {
        body["continuation"] = continuation;
    }
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";
    req.headers = client_headers(def, visitor_data_);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = http_error("channel", res);
        return false;
    }
    last_response_ = res.body;
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "channel: invalid json";
        return false;
    }
    out = Feed{};
    collect(r, out);
    if (const json* meta = find_key(r, "channelMetadataRenderer")) {
        header.title = meta->value("title", "");
        header.avatar = last_thumbnail(meta->value("avatar", json::object()));
    }
    if (const json* page = find_key(r, "pageHeaderViewModel")) {
        std::vector<std::string> parts;
        texts_under(*page, "metadataParts", parts);
        for (const std::string& p : parts)
            if (contains_any(p, {"abonn", "subscriber"})) header.subscribers = p;
    }
    // Videos of a channel page carry no channel name: lockups then list [views, age] where parse_lockup
    // expects [channel, views, age].
    for (FeedItem& item : out.items) {
        if (contains_any(item.channel, {"vue", "view"})) {
            item.published = item.views;
            item.views = item.channel;
            item.channel.clear();
        }
        if (item.channel.empty()) item.channel = header.title;
    }
    OLOG("yt: channel %s%s -> %zu items", channel_id.c_str(), continuation.empty() ? "" : " (next page)",
         out.items.size());
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
    last_response_ = res.body;
    json r = json::parse(res.body, nullptr, false);
    if (r.is_discarded()) {
        error = "browse: invalid json";
        return false;
    }
    out = Feed{};
    if (browse_id == "FEwhat_to_watch") {
        // Home: rows by topic (next pages bring more rows).
        collect_shelves(r, out);
    } else {
        // Subscriptions "Tous" tab: a titled "most relevant" shelf, then untitled shelves in date order.
        Feed picked;
        collect(r, out, browse_id == "FEsubscriptions" && continuation.empty() ? &picked : nullptr);
        out.highlights = std::move(picked.items);
    }
    OLOG("yt: browse %s%s -> %zu items, %zu highlights, %zu shelves", browse_id.c_str(),
         continuation.empty() ? "" : " (next page)", out.items.size(), out.highlights.size(), out.shelves.size());
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

const Format* pick_audio(const VideoInfo& info, const std::string& track) {
    auto aac = [](const Format& f) {
        return f.is_audio() && f.mime == "audio/mp4" && f.codecs.compare(0, 4, "mp4a") == 0;
    };
    // The track to play: exact id, else same language ("fr" matches "fr.3" and "fr-FR.3"), else the default.
    std::string wanted;
    if (!track.empty()) {
        for (const Format& f : info.formats)
            if (aac(f) && f.audio_track_id == track) wanted = track;
        std::string lang = track.substr(0, track.find_first_of(".-"));
        for (const Format& f : info.formats) {
            if (!wanted.empty() || !aac(f) || f.audio_track_id.empty()) continue;
            if (f.audio_track_id.substr(0, f.audio_track_id.find_first_of(".-")) == lang) wanted = f.audio_track_id;
        }
    }
    if (wanted.empty())
        for (const Format& f : info.formats)
            if (aac(f) && f.audio_default) wanted = f.audio_track_id;
    const Format* best = nullptr;
    for (const Format& f : info.formats) {
        if (!aac(f) || (!wanted.empty() && f.audio_track_id != wanted)) continue;
        if (!best || f.bitrate > best->bitrate) best = &f;
    }
    return best;
}

bool fetch_captions(const std::string& url, const std::string& translate_to, std::vector<CaptionCue>& out,
                    std::string& error) {
    net::Request req;
    req.url = drop_param(drop_param(url, "fmt"), "tlang") + "&fmt=json3";
    if (!translate_to.empty()) req.url += "&tlang=" + translate_to;
    req.headers = {{"User-Agent", cfg::str({"innertube", "search", "userAgent"})}};
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "captions: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    if (!r.is_object()) {
        error = res.body.empty() ? "captions: empty answer" : "captions: invalid json";
        return false;
    }
    out.clear();
    // json3: events {tStartMs, dDurationMs, segs:[{utf8}]}; speech-recognition tracks split lines into
    // word segments and add "\n"-only events, which carry nothing to show.
    for (const json& e : r.value("events", json::array())) {
        if (!e.contains("segs")) continue;
        CaptionCue cue;
        for (const json& seg : e["segs"]) cue.text += seg.value("utf8", "");
        size_t first = cue.text.find_first_not_of(" \n");
        if (first == std::string::npos) continue;
        cue.text = cue.text.substr(first, cue.text.find_last_not_of(" \n") + 1 - first);
        cue.start_ms = to_int(e, "tStartMs");
        cue.end_ms = cue.start_ms + to_int(e, "dDurationMs");
        out.push_back(std::move(cue));
    }
    OLOG("yt: captions %s -> %zu cues", translate_to.empty() ? "" : ("-> " + translate_to).c_str(), out.size());
    return true;
}

bool rate_video(const std::string& video_id, const std::string& rating, const std::string& access_token,
                std::string& error) {
    std::string endpoint = rating == "like" ? "like/like" : rating == "dislike" ? "like/dislike" : "like/removelike";
    net::Response res = post_as_account(endpoint, {{"target", {{"videoId", video_id}}}}, access_token);
    if (!res.ok()) {
        error = http_error(endpoint.c_str(), res);
        return false;
    }
    OLOG("yt: %s %s ok", endpoint.c_str(), video_id.c_str());
    return true;
}

bool set_subscribed(const std::string& channel_id, bool subscribe, const std::string& access_token, std::string& error) {
    std::string endpoint = subscribe ? "subscription/subscribe" : "subscription/unsubscribe";
    net::Response res = post_as_account(endpoint, {{"channelIds", json::array({channel_id})}}, access_token);
    if (!res.ok()) {
        error = http_error(endpoint.c_str(), res);
        return false;
    }
    OLOG("yt: %s %s ok", endpoint.c_str(), channel_id.c_str());
    return true;
}

bool vote_counts(const std::string& video_id, int64_t& likes, int64_t& dislikes, std::string& error) {
    likes = dislikes = -1;
    std::string api = cfg::str({"returnYouTubeDislike", "api"});
    if (api.empty()) api = "https://returnyoutubedislikeapi.com";
    net::Request req;
    req.url = api + "/votes?videoId=" + video_id;
    req.headers = {{"User-Agent", "OmegaTube"}};
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = http_error("ryd", res);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    if (!r.is_object()) {
        error = "ryd: invalid json";
        return false;
    }
    likes = to_int(r, "likes");
    dislikes = to_int(r, "dislikes");
    return true;
}

bool account_info(const std::string& access_token, AccountInfo& out, std::string& error) {
    ClientDef def = client_def("browse", "");
    json body = {{"context", {{"client", def.client}}}};
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/account/accounts_list?prettyPrint=false";
    req.headers = client_headers(def, "");
    req.headers.emplace_back("Authorization", "Bearer " + access_token);
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        error = "accounts_list: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    // accountItem {accountName, accountPhoto, channelHandle, isSelected}: the selected one, else the first.
    std::vector<const json*> items;
    std::function<void(const json&)> walk = [&](const json& j) {
        if (j.is_object()) {
            for (auto it = j.begin(); it != j.end(); ++it) {
                if ((it.key() == "accountItem" || it.key() == "accountItemRenderer") && it.value().is_object())
                    items.push_back(&it.value());
                else
                    walk(it.value());
            }
        } else if (j.is_array()) {
            for (const json& e : j) walk(e);
        }
    };
    if (!r.is_discarded()) walk(r);
    const json* item = nullptr;
    for (const json* i : items)
        if (!item || i->value("isSelected", false)) item = i;
    if (!item) {
        error = "accounts_list: no account";
        return false;
    }
    out.name = text_of(item->value("accountName", json::object()));
    out.handle = text_of(item->value("channelHandle", json::object()));
    out.photo = last_thumbnail(item->value("accountPhoto", json::object()));
    if (out.photo.compare(0, 2, "//") == 0) out.photo = "https:" + out.photo;
    return true;
}

}  // namespace omega::yt
