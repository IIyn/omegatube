// Host tool, same code as the TV core:
//   ytcheck --get <url>          HTTPS sanity check (prints status + size)
//   ytcheck --sb <videoId>        SponsorBlock segments
//   ytcheck --signin              Google device-flow sign-in, then signed-in home + subscriptions
//   ytcheck --search <query>     search results + suggestions
//   ytcheck --range <url> [MB] [out]  downloads MB megabytes through RangeReader (1 MB ranges, kept-alive)
//   ytcheck <videoId> [MB]       resolves formats via InnerTube and downloads the whole audio stream and
//                                the first MB megabytes (default 100) of the 1080p H.264 stream via ranges.
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <algorithm>
#include <cstring>
#include <vector>

#include <thread>

#include "net/http.h"
#include "util/remote_config.h"
#include "yt/auth.h"
#include "yt/sponsorblock.h"
#include "yt/innertube.h"

using Clock = std::chrono::steady_clock;

static bool drain(const char* label, const omega::yt::Format& f, const omega::net::Headers& headers, uint64_t limit) {
    omega::net::RangeReader reader(f.url, headers);
    std::vector<uint8_t> buf(256 * 1024);
    uint64_t total = 0;
    auto t0 = Clock::now();
    while (total < limit) {
        size_t got = reader.read(buf.data(), buf.size());
        if (got == 0) break;
        total += got;
    }
    double secs = std::chrono::duration<double>(Clock::now() - t0).count();
    printf("%s itag %d: %llu / %lld bytes in %.1fs (%.1f Mb/s) %s%s\n", label, f.itag,
           static_cast<unsigned long long>(total), static_cast<long long>(f.content_length), secs,
           total * 8 / secs / 1e6, reader.error().empty() ? "OK" : "ERROR ", reader.error().c_str());
    return reader.error().empty();
}

int main(int argc, char** argv) {
    omega::cfg::init("");  // built-in remote-config.json defaults
    if (argc >= 3 && strcmp(argv[1], "--get") == 0) {
        omega::net::Request req;
        req.url = argv[2];
        omega::net::Response res = omega::net::fetch(req);
        printf("status %d, %zu bytes, error '%s'\n", res.status, res.body.size(), res.error.c_str());
        return res.ok() ? 0 : 1;
    }
    if (argc >= 3 && strcmp(argv[1], "--search") == 0) {
        omega::yt::Client client;
        omega::yt::Feed feed;
        std::string error;
        if (!client.search(argv[2], "", feed, error)) {
            printf("search failed: %s\n", error.c_str());
            return 1;
        }
        for (const auto& it : feed.items)
            printf("%s | %s | %s | %s | %s | %s\n", it.video_id.c_str(), it.title.c_str(), it.channel.c_str(),
                   it.duration.c_str(), it.views.c_str(), it.published.c_str());
        printf("%zu items, continuation %s\n", feed.items.size(), feed.continuation.empty() ? "none" : "yes");
        std::vector<std::string> sugg;
        if (omega::yt::suggest(argv[2], sugg, error))
            for (const auto& s : sugg) printf("suggest: %s\n", s.c_str());
        return 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--sb") == 0) {
        std::vector<omega::yt::SkipSegment> segs;
        std::string error;
        if (!omega::yt::sponsor_segments(argv[2], segs, error)) {
            printf("failed: %s\n", error.c_str());
            return 1;
        }
        for (const auto& s : segs) printf("%-15s %8.1f -> %8.1f\n", s.category.c_str(), s.start_s, s.end_s);
        printf("%zu segments\n", segs.size());
        return 0;
    }
    if (argc >= 2 && strcmp(argv[1], "--signin") == 0) {
        // Device flow end to end, token kept in ./omegatube-auth.json; then lists the signed-in home feed.
        omega::yt::Auth& auth = omega::yt::shared_auth();
        auth.init("omegatube-auth.json");
        std::string error;
        if (!auth.signed_in()) {
            omega::yt::DeviceCode code;
            if (!auth.begin(code, error)) {
                printf("begin failed: %s\n", error.c_str());
                return 1;
            }
            printf("Go to %s and enter %s\n(QR: %s)\n", code.verification_url.c_str(), code.user_code.c_str(),
                   omega::yt::prefilled_verification_url(code.verification_url, code.user_code).c_str());
            for (;;) {
                std::this_thread::sleep_for(std::chrono::seconds(code.interval_s));
                omega::yt::PollResult r = auth.poll(code, error);
                if (r == omega::yt::PollResult::Done) break;
                if (r == omega::yt::PollResult::SlowDown) code.interval_s += 5;
                if (r != omega::yt::PollResult::Pending && r != omega::yt::PollResult::SlowDown) {
                    printf("sign-in failed (%d): %s\n", static_cast<int>(r), error.c_str());
                    return 1;
                }
            }
        }
        std::string token = auth.access_token(error);
        if (token.empty()) {
            printf("no access token: %s\n", error.c_str());
            return 1;
        }
        for (const char* id : {"FEwhat_to_watch", "FEsubscriptions"}) {
            omega::yt::Client client;
            omega::yt::Feed feed;
            if (!client.browse(id, "", token, feed, error)) {
                printf("%s failed: %s\n", id, error.c_str());
                continue;
            }
            printf("== %s: %zu items, continuation %s\n", id, feed.items.size(), feed.continuation.empty() ? "none" : "yes");
            for (size_t i = 0; i < feed.items.size() && i < 8; ++i)
                printf("   %s | %s | %s | %s | %s\n", feed.items[i].video_id.c_str(), feed.items[i].title.c_str(),
                       feed.items[i].channel.c_str(), feed.items[i].duration.c_str(), feed.items[i].views.c_str());
        }
        return 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--range") == 0) {
        omega::yt::Format f;
        f.url = argv[2];
        uint64_t limit = (argc > 3 ? strtoull(argv[3], nullptr, 10) : 20) * 1024 * 1024;
        omega::net::RangeReader probe(f.url, {}, 1024 * 1024);
        // Test hook: an ".../expired..." URL is "re-resolved" to the same URL without that marker.
        omega::net::RangeReader::Hooks hooks;
        hooks.refresh_url = [&f] {
            std::string u = f.url;
            size_t at = u.find("/expired");
            if (at != std::string::npos) u.erase(at, 8);
            printf("refresh_url -> %s\n", u.c_str());
            return u;
        };
        hooks.on_recovering = [](bool on) { printf("recovering=%d\n", on); };
        probe.set_hooks(hooks);
        FILE* out = argc > 4 ? fopen(argv[4], "wb") : nullptr;
        std::vector<uint8_t> buf(256 * 1024);
        uint64_t total = 0;
        auto t0 = Clock::now();
        while (total < limit) {
            size_t got = probe.read(buf.data(), std::min<uint64_t>(buf.size(), limit - total));
            if (got == 0) break;
            if (out) fwrite(buf.data(), 1, got, out);
            total += got;
        }
        if (out) fclose(out);
        double secs = std::chrono::duration<double>(Clock::now() - t0).count();
        printf("range: %llu bytes of %lld in %.1fs (%.1f Mb/s) error '%s'\n", static_cast<unsigned long long>(total),
               static_cast<long long>(probe.total_size()), secs, total * 8 / secs / 1e6, probe.error().c_str());
        return probe.error().empty() ? 0 : 1;
    }
    if (argc < 2) {
        fprintf(stderr, "usage: ytcheck <videoId> [MB] | --get <url>\n");
        return 2;
    }
    uint64_t video_limit = (argc > 2 ? strtoull(argv[2], nullptr, 10) : 100) * 1024 * 1024;

    omega::yt::Client client;
    omega::yt::VideoInfo info;
    std::string error;
    if (!client.player(argv[1], info, error)) {
        printf("player failed: %s\n", error.c_str());
        return 1;
    }
    printf("'%s' by %s (%llds), %zu direct formats\n", info.title.c_str(), info.author.c_str(),
           static_cast<long long>(info.length_s), info.formats.size());
    const omega::yt::Format* video = omega::yt::pick_video(info, 1080);
    const omega::yt::Format* audio = omega::yt::pick_audio(info);
    if (!video || !audio) {
        printf("no H.264/AAC formats\n");
        return 1;
    }
    printf("video itag %d %dx%d@%d %s, audio itag %d %s\n", video->itag, video->width, video->height, video->fps,
           video->codecs.c_str(), audio->itag, audio->codecs.c_str());
    bool ok = drain("audio", *audio, client.stream_headers(), UINT64_MAX);
    ok &= drain("video", *video, client.stream_headers(), video_limit);
    return ok ? 0 : 1;
}
