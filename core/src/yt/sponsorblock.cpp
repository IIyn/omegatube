#include "yt/sponsorblock.h"

#include <mbedtls/sha256.h>

#include <nlohmann/json.hpp>

#include "net/http.h"
#include "util/log.h"
#include "util/remote_config.h"

namespace omega::yt {
namespace {

constexpr const char* kCategories =
    "%5B%22sponsor%22,%22selfpromo%22,%22interaction%22,%22intro%22,%22outro%22,%22preview%22,"
    "%22music_offtopic%22,%22filler%22%5D";

std::string sha256_hex(const std::string& s) {
    unsigned char digest[32];
    mbedtls_sha256(reinterpret_cast<const unsigned char*>(s.data()), s.size(), digest, 0);
    static const char* hex = "0123456789abcdef";
    std::string out;
    for (unsigned char b : digest) {
        out += hex[b >> 4];
        out += hex[b & 15];
    }
    return out;
}

}  // namespace

bool sponsor_segments(const std::string& video_id, std::vector<SkipSegment>& out, std::string& error) {
    out.clear();
    net::Request req;
    req.url = cfg::str({"sponsorblock", "api"}) + "/api/skipSegments/" + sha256_hex(video_id).substr(0, 4) +
              "?categories=" + kCategories + "&actionType=skip";
    req.headers = {{"User-Agent", "OmegaTube"}};
    net::Response res = net::fetch(req);
    if (res.status == 404) return true;  // no segments for any video of this prefix
    if (!res.ok()) {
        error = "sponsorblock: " + (res.error.empty() ? "http " + std::to_string(res.status) : res.error);
        return false;
    }
    nlohmann::json r = nlohmann::json::parse(res.body, nullptr, false);
    if (!r.is_array()) {
        error = "sponsorblock: invalid json";
        return false;
    }
    for (const auto& video : r) {
        if (video.value("videoID", "") != video_id) continue;
        for (const auto& seg : video.value("segments", nlohmann::json::array())) {
            const auto& range = seg.value("segment", nlohmann::json::array());
            if (range.size() != 2 || !range[0].is_number() || !range[1].is_number()) continue;
            out.push_back({seg.value("category", ""), range[0].get<double>(), range[1].get<double>()});
        }
    }
    OLOG("sponsorblock %s: %zu segments", video_id.c_str(), out.size());
    return true;
}

}  // namespace omega::yt
