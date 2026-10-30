#include "yt/watch.h"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <random>
#include <utility>
#include <vector>

#include <nlohmann/json.hpp>

#include "net/http.h"
#include "util/log.h"
#include "util/remote_config.h"
#include "yt/auth.h"

namespace omega::yt {
namespace {

using json = nlohmann::json;
using Fields = std::vector<std::pair<std::string, std::string>>;

std::string make_cpn() {
    static const char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    std::random_device rd;
    std::mt19937 rng(rd());
    std::string out;
    for (int i = 0; i < 16; ++i) out += kAlphabet[rng() & 63];
    return out;
}

// "12.345". Not printf("%.3f"): that follows the process locale and gives "12,345" on a French TV.
std::string seconds_text(double s) {
    long long ms = s < 0 ? 0 : std::llround(s * 1000);
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld.%03lld", ms / 1000, ms % 1000);
    return buf;
}

// `base` with `fields` set (replacing any of the same name) and `fexp` dropped (the stats endpoint
// refuses experiments the client did not run).
std::string stats_url(const std::string& base, const Fields& fields) {
    size_t mark = base.find('?');
    std::string out = base.substr(0, mark), query;
    if (mark != std::string::npos) {
        for (size_t at = mark + 1; at <= base.size();) {
            size_t end = base.find('&', at);
            if (end == std::string::npos) end = base.size();
            std::string pair = base.substr(at, end - at);
            std::string key = pair.substr(0, pair.find('='));
            bool drop = key.empty() || key == "fexp";
            for (const auto& f : fields) drop = drop || key == f.first;
            if (!drop) query += (query.empty() ? "" : "&") + pair;
            at = end + 1;
        }
    }
    for (const auto& f : fields) query += (query.empty() ? "" : "&") + f.first + "=" + f.second;
    return query.empty() ? out : out + "?" + query;
}

// The tracking URLs point at s.youtube.com, which only reads cookies; YouTube's TV player sends the
// pings with its OAuth token to www.youtube.com, the host that reads the Authorization header.
std::string account_host(const std::string& url) {
    const std::string stats = "https://s.youtube.com/";
    return url.compare(0, stats.size(), stats) == 0 ? "https://www.youtube.com/" + url.substr(stats.size()) : url;
}

void add_client(Fields& f, const std::string& version) {
    f.push_back({"c", cfg::str({"innertube", "browse", "clientName"})});
    f.push_back({"cver", version});
    f.push_back({"cplayer", "UNIPLAYER"});
    f.push_back({"cplatform", "TV"});
    f.push_back({"hl", "en_US"});
}

bool ping(const std::string& url, const std::string& token) {
    net::Request req;
    req.url = account_host(url);
    req.headers = {{"User-Agent", cfg::str({"innertube", "browse", "userAgent"})},
                   {"Authorization", "Bearer " + token}};
    net::Response res = net::fetch(req);
    return res.error.empty() && res.status >= 200 && res.status < 300;
}

// The account's tracking URLs: the TV client's player answer with the Bearer token (a token-less
// client's URLs are anonymous).
bool tracking_urls(const std::string& video_id, const std::string& token, std::string& playback,
                   std::string& watchtime) {
    json c = cfg::obj({"innertube", "browse"});
    json client = c.value("context", json::object());
    client["clientName"] = c.value("clientName", "");
    client["clientVersion"] = c.value("clientVersion", "");
    client["hl"] = cfg::language();
    client["gl"] = cfg::region();
    json body = {{"context", {{"client", client}}}, {"videoId", video_id}, {"contentCheckOk", true}, {"racyCheckOk", true}};
    net::Request req;
    req.method = "POST";
    req.url = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
    req.headers = {{"Content-Type", "application/json"},
                   {"User-Agent", c.value("userAgent", "")},
                   {"X-YouTube-Client-Name", std::to_string(c.value("clientNameId", 7))},
                   {"X-YouTube-Client-Version", c.value("clientVersion", "")},
                   {"Authorization", "Bearer " + token},
                   {"Origin", "https://www.youtube.com"}};
    req.body = body.dump();
    net::Response res = net::fetch(req);
    if (!res.ok()) {
        OLOG("history: tracking player http %d %s", res.status, res.error.c_str());
        return false;
    }
    json r = json::parse(res.body, nullptr, false);
    const json& t = r.is_object() ? r.value("playbackTracking", json::object()) : json::object();
    playback = t.value("videostatsPlaybackUrl", json::object()).value("baseUrl", "");
    watchtime = t.value("videostatsWatchtimeUrl", json::object()).value("baseUrl", "");
    return !playback.empty();
}

}  // namespace

WatchReporter::WatchReporter(std::string video_id, int64_t length_ms, PositionFn position)
    : video_id_(std::move(video_id)), length_ms_(length_ms), position_(std::move(position)) {
    thread_ = std::thread([this] { run(); });
}

WatchReporter::~WatchReporter() {
    stop_ = true;
    if (thread_.joinable()) thread_.join();
}

void WatchReporter::run() {
    std::string error;
    std::string token = shared_auth().access_token(error);
    if (token.empty()) return;  // signed out: nothing to report
    std::string playback, watchtime;
    if (!tracking_urls(video_id_, token, playback, watchtime)) {
        OLOG("history: no tracking URLs for %s", video_id_.c_str());
        return;
    }
    const std::string cpn = make_cpn();
    const std::string version = cfg::str({"innertube", "browse", "clientVersion"});
    // el=detailpage: without it YouTube files the view as a Short and it never shows in the history.
    Fields pf = {{"ver", "2"}, {"cpn", cpn}, {"cmt", "0"}, {"el", "detailpage"}};
    add_client(pf, version);
    pf.insert(pf.end(), {{"rt", "0.000"}, {"fs", "0"}, {"muted", "0"}, {"volume", "100"}});
    bool ok = ping(stats_url(playback, pf), token);
    OLOG("history: playback ping %s -> %s", video_id_.c_str(), ok ? "ok" : "failed");

    const auto started = std::chrono::steady_clock::now();
    int64_t span_start = -1, last = -1;
    int waited = 0;
    while (!stop_) {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
        int64_t pos = position_();
        bool final_report = stop_;
        if (pos >= 0) {
            if (span_start < 0 || pos < last || pos - last > 5000) span_start = pos;  // start or seek
            last = pos;
        }
        if (++waited < 40 && !final_report) continue;  // every 20 s
        waited = 0;
        // Nothing new since the last report; the final one is still sent (it carries the resume point).
        if (span_start < 0 || (last <= span_start && !final_report)) continue;
        token = shared_auth().access_token(error);
        if (token.empty()) return;
        double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        Fields wf = {{"ver", "2"},
                     {"cpn", cpn},
                     {"st", seconds_text(span_start / 1000.0)},
                     {"et", seconds_text(last / 1000.0)},
                     {"cmt", seconds_text(last / 1000.0)},
                     {"len", seconds_text(length_ms_ / 1000.0)},
                     {"el", "detailpage"},
                     {"state", final_report ? "paused" : "playing"}};
        // The last report of the playback (player closed): YouTube's players flag it, and it is the one
        // the account's resume point follows on other devices.
        if (final_report) wf.push_back({"final", "1"});
        add_client(wf, version);
        wf.insert(wf.end(), {{"rt", seconds_text(elapsed)}, {"muted", "0"}, {"volume", "100"}});
        bool sent = ping(stats_url(watchtime, wf), token);
        if (final_report || !sent)
            OLOG("history: %swatchtime %s %.0f-%.0f s -> %s", final_report ? "final " : "", video_id_.c_str(),
                 span_start / 1000.0, last / 1000.0, sent ? "ok" : "failed");
        span_start = last;
    }
}

}  // namespace omega::yt
