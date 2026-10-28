// Host tool, same code as the TV core:
//   ytcheck --get <url>          HTTPS sanity check (prints status + size)
//   ytcheck --sb <videoId>        SponsorBlock segments
//   ytcheck --signin              Google device-flow sign-in, then signed-in home + subscriptions
//   ytcheck --search <query>     search results + suggestions
//   ytcheck --captions <videoId> [lang] [tlang]  caption tracks, then the first cues of the `lang` track
//                                (default: the first one), machine-translated to tlang if given
//   ytcheck --account            name / handle / photo of the account of a previous --signin
//   ytcheck --details <videoId>  date, channel, like / subscription state (with a --signin token), RYD votes
//   ytcheck --channel <channelId>  channel header + first videos
//   ytcheck --range <url> [MB] [out]  downloads MB megabytes through RangeReader (1 MB ranges, kept-alive)
//   ytcheck <videoId> [MB]       resolves formats via InnerTube and downloads the whole audio stream and
//                                the first MB megabytes (default 100) of the 1080p H.264 stream via ranges.
//                                Env AUDIO=<track id or language> picks the audio language.
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <algorithm>
#include <cstring>
#include <vector>

#include <thread>

#include "media/demuxer.h"
#include "net/http.h"
#include "util/remote_config.h"
#include "yt/auth.h"
#include "yt/sponsorblock.h"
#include "yt/innertube.h"
#include "yt/js_solver.h"

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
            printf("%s | %s | %s | %s | %s | %s%s%s\n", it.video_id.c_str(), it.title.c_str(), it.channel.c_str(),
                   it.duration.c_str(), it.views.c_str(), it.published.c_str(),
                   it.playlist_id.empty() ? "" : (" | " + it.kind + " ").c_str(), it.playlist_id.c_str());
        printf("%zu items, continuation %s\n", feed.items.size(), feed.continuation.empty() ? "none" : "yes");
        std::vector<std::string> sugg;
        if (omega::yt::suggest(argv[2], sugg, error))
            for (const auto& s : sugg) printf("suggest: %s\n", s.c_str());
        return 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--next") == 0) {
        omega::yt::Client client;
        omega::yt::Feed feed;
        std::string error;
        if (!client.related(argv[2], feed, error)) {
            printf("next failed: %s\n", error.c_str());
            return 1;
        }
        for (const auto& it : feed.items)
            printf("%s | %s | %s | %s | %s | %s%s%s\n", it.video_id.c_str(), it.title.c_str(), it.channel.c_str(),
                   it.duration.c_str(), it.views.c_str(), it.published.c_str(),
                   it.playlist_id.empty() ? "" : (" | " + it.kind + " ").c_str(), it.playlist_id.c_str());
        printf("%zu related\n", feed.items.size());
        return 0;
    }
    if (argc >= 4 && strcmp(argv[1], "--playlist") == 0) {
        omega::yt::Client client;
        omega::yt::Feed feed;
        std::string title, error;
        if (!client.playlist(argv[2], argv[3], feed, title, error)) {
            printf("playlist failed: %s\n", error.c_str());
            return 1;
        }
        printf("'%s': %zu next items\n", title.c_str(), feed.items.size());
        for (const auto& it : feed.items)
            printf("  %s | %s | %s | %s\n", it.video_id.c_str(), it.title.c_str(), it.channel.c_str(), it.duration.c_str());
        return 0;
    }
    if (argc >= 2 && strcmp(argv[1], "--home") == 0) {
        // Home rows: first page, the next page of rows, then more items of the first row.
        omega::yt::Client client;
        omega::yt::Auth& auth = omega::yt::shared_auth();
        auth.init("omegatube-auth.json");
        std::string error, token = auth.access_token(error);
        omega::yt::Feed first, next, row;
        if (token.empty() || !client.browse("FEwhat_to_watch", "", token, first, error)) {
            printf("home failed: %s\n", error.c_str());
            return 1;
        }
        auto print = [](const omega::yt::Feed& f) {
            for (const auto& sh : f.shelves) printf("  '%s' %zu items\n", sh.title.c_str(), sh.items.size());
        };
        print(first);
        if (!first.continuation.empty() && client.browse("FEwhat_to_watch", first.continuation, token, next, error)) {
            printf("next page: %zu shelves, %zu items\n", next.shelves.size(), next.items.size());
            print(next);
        }
        if (!first.shelves.empty() && client.browse("", first.shelves[0].continuation, token, row, error))
            printf("row '%s' continued: %zu items, continuation %s\n", first.shelves[0].title.c_str(), row.items.size(),
                   row.continuation.empty() ? "none" : "yes");
        return 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--playlist-page") == 0) {
        // A whole playlist page by page: --playlist-page <listId> [pages]
        omega::yt::Client client;
        std::string title, error, cont;
        int pages = argc > 3 ? atoi(argv[3]) : 1;
        for (int p = 0; p < pages; p++) {
            omega::yt::Feed feed;
            if (!client.playlist_page(argv[2], cont, feed, title, error)) {
                printf("playlist page failed: %s\n", error.c_str());
                return 1;
            }
            if (const char* dump = getenv("DUMP")) {  // raw response of the last page
                if (FILE* f = fopen(dump, "wb")) {
                    fwrite(client.last_response().data(), 1, client.last_response().size(), f);
                    fclose(f);
                }
            }
            printf("'%s' page %d: %zu items, continuation %s\n", title.c_str(), p + 1, feed.items.size(),
                   feed.continuation.empty() ? "none" : "yes");
            for (const auto& it : feed.items)
                printf("  %s | %s | %s | %s | %s | %s\n", it.video_id.c_str(), it.title.c_str(), it.channel.c_str(),
                       it.duration.c_str(), it.views.c_str(), it.published.c_str());
            cont = feed.continuation;
            if (cont.empty()) break;
        }
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
    if (argc >= 4 && (strcmp(argv[1], "--dump-player") == 0 || strcmp(argv[1], "--dump-browse") == 0 ||
                      strcmp(argv[1], "--dump-search") == 0)) {
        // Saves the raw InnerTube response, to study a layout (live player response, feed shelves).
        // --dump-browse needs the token of a previous --signin (./omegatube-auth.json).
        omega::yt::Client client;
        std::string error;
        bool ok;
        if (argv[1][7] == 's') {
            omega::yt::Feed feed;
            ok = client.search(argv[2], "", feed, error);
        } else if (argv[1][7] == 'p') {
            omega::yt::VideoInfo info;
            ok = client.player(argv[2], info, error);
        } else {
            omega::yt::Auth& auth = omega::yt::shared_auth();
            auth.init("omegatube-auth.json");
            std::string token = auth.access_token(error);
            omega::yt::Feed feed;
            ok = !token.empty() && client.browse(argv[2], "", token, feed, error);
            if (ok) printf("%zu items parsed\n", feed.items.size());
            for (const auto& sh : feed.shelves)
                printf("  shelf '%s': %zu items, continuation %s\n", sh.title.c_str(), sh.items.size(),
                       sh.continuation.empty() ? "none" : "yes");
            if (ok && !feed.shelves.empty()) printf("  page continuation %s\n", feed.continuation.empty() ? "none" : "yes");
        }
        if (!client.last_response().empty()) {
            FILE* out = fopen(argv[3], "wb");
            if (out) {
                fwrite(client.last_response().data(), 1, client.last_response().size(), out);
                fclose(out);
            }
            printf("%zu bytes -> %s\n", client.last_response().size(), argv[3]);
        }
        if (!ok) printf("failed: %s\n", error.c_str());
        return ok ? 0 : 1;
    }
    if (argc >= 3 && strcmp(argv[1], "--live-url") == 0) {
        // Reads a live format URL (from --dump-player) segment by segment like the TV: LiveReader + demuxer.
        omega::net::Headers headers = omega::yt::Client().stream_headers();
        std::string error;
        int64_t head = omega::net::LiveReader::head_sequence(argv[2], headers, error);
        if (head < 0) {
            printf("head: %s\n", error.c_str());
            return 1;
        }
        int count = argc > 3 ? atoi(argv[3]) : 5;
        omega::net::LiveReader live(argv[2], headers, head - 2);
        auto reader = omega::media::open_demuxer([&live](void* dst, size_t n) { return live.read(dst, n); });
        if (!reader->read_header()) {
            printf("header: %s\n", reader->error().c_str());
            return 1;
        }
        printf("head %lld, %s %ux%u\n", static_cast<long long>(head), omega::media::codec_name(reader->track().codec),
               reader->track().width, reader->track().height);
        auto t0 = Clock::now();
        for (int i = 0; i < count; ++i) {
            std::deque<omega::media::Sample> samples;
            if (!reader->read_fragment(samples)) {
                printf("end: %s / %s\n", reader->error().c_str(), live.error().c_str());
                break;
            }
            printf("segment %lld at %.1fs: %zu samples, pts %lld..%lld, key=%d\n", static_cast<long long>(live.sequence()),
                   std::chrono::duration<double>(Clock::now() - t0).count(), samples.size(),
                   static_cast<long long>(samples.front().pts_ms), static_cast<long long>(samples.back().pts_ms),
                   samples.front().key);
        }
        return 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--captions") == 0) {
        omega::yt::Client client;
        omega::yt::VideoInfo info;
        std::string error;
        if (!client.player(argv[2], info, error)) {
            printf("player failed: %s\n", error.c_str());
            return 1;
        }
        const omega::yt::CaptionTrack* pick = nullptr;
        for (const auto& c : info.captions) {
            printf("  %-8s %-40s %s%s\n", c.lang.c_str(), c.name.c_str(), c.automatic ? "auto " : "",
                   c.translatable ? "translatable" : "");
            if (!pick && (argc < 4 || c.lang == argv[3])) pick = &c;
        }
        printf("%zu tracks, %zu translation languages\n", info.captions.size(), info.translations.size());
        if (!pick) return 1;
        std::vector<omega::yt::CaptionCue> cues;
        if (!omega::yt::fetch_captions(pick->url, argc > 4 ? argv[4] : "", cues, error)) {
            printf("captions failed: %s\n", error.c_str());
            return 1;
        }
        for (size_t i = 0; i < cues.size() && i < 12; ++i)
            printf("  %7lld-%7lld %s\n", static_cast<long long>(cues[i].start_ms), static_cast<long long>(cues[i].end_ms),
                   cues[i].text.c_str());
        printf("%zu cues\n", cues.size());
        return cues.empty() ? 1 : 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--details") == 0) {
        omega::yt::Auth& auth = omega::yt::shared_auth();
        auth.init("omegatube-auth.json");
        std::string error;
        std::string token = auth.signed_in() ? auth.access_token(error) : "";
        omega::yt::Client client;
        omega::yt::VideoDetails d;
        if (!client.details(argv[2], token, d, error)) {
            printf("details failed: %s\n", error.c_str());
            return 1;
        }
        int64_t likes = -1, dislikes = -1;
        if (!omega::yt::vote_counts(argv[2], likes, dislikes, error)) printf("ryd failed: %s\n", error.c_str());
        printf("channel '%s' (%s) %s\navatar %s\ndate '%s'\nlikes %lld dislikes %lld\nlikeStatus '%s' subscribed %d%s\n",
               d.channel.c_str(), d.channel_id.c_str(), d.subscribers.c_str(), d.channel_avatar.c_str(), d.date.c_str(),
               static_cast<long long>(likes), static_cast<long long>(dislikes), d.like_status.c_str(), d.subscribed,
               token.empty() ? "  (signed out: run --signin for the account state)" : "");
        return 0;
    }
    if (argc >= 3 && strcmp(argv[1], "--channel") == 0) {
        omega::yt::Client client;
        omega::yt::Feed feed;
        omega::yt::ChannelInfo header;
        std::string error;
        if (!client.channel(argv[2], "", feed, header, error)) {
            printf("channel failed: %s\n", error.c_str());
            return 1;
        }
        printf("'%s' %s\navatar %s\n%zu items, continuation %s\n", header.title.c_str(), header.subscribers.c_str(),
               header.avatar.c_str(), feed.items.size(), feed.continuation.empty() ? "none" : "yes");
        for (size_t i = 0; i < feed.items.size() && i < 8; ++i)
            printf("   %s | %s | %s | %s | %s | %s\n", feed.items[i].video_id.c_str(), feed.items[i].title.c_str(),
                   feed.items[i].channel.c_str(), feed.items[i].duration.c_str(), feed.items[i].views.c_str(),
                   feed.items[i].published.c_str());
        return feed.items.empty() ? 1 : 0;
    }
    if (argc >= 2 && strcmp(argv[1], "--account") == 0) {
        omega::yt::Auth& auth = omega::yt::shared_auth();
        auth.init("omegatube-auth.json");
        std::string error;
        std::string token = auth.access_token(error);
        omega::yt::AccountInfo account;
        if (token.empty() || !omega::yt::account_info(token, account, error)) {
            printf("account failed: %s\n", token.empty() ? "signed out (run --signin)" : error.c_str());
            return 1;
        }
        printf("name '%s' handle '%s'\nphoto %s\n", account.name.c_str(), account.handle.c_str(), account.photo.c_str());
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
            for (size_t i = 0; i < feed.highlights.size() && i < 4; ++i)
                printf(" * %s | %s | %s\n", feed.highlights[i].video_id.c_str(), feed.highlights[i].title.c_str(),
                       feed.highlights[i].channel.c_str());
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
    // CLIENT=<clientName>: only that player client (e.g. CLIENT=TVHTML5), to see which ones YouTube accepts.
    omega::yt::shared_decipher().set_cache_dir(".");
    omega::yt::shared_auth().init("omegatube-auth.json");  // signed-in retry on LOGIN_REQUIRED, as on the TV
    const char* only = getenv("CLIENT");
    if (!client.player(argv[1], info, error, only ? only : "")) {
        printf("player failed: %s\n", error.c_str());
        printf("refusal: status '%s' reason '%s'\n", client.refusal().status.c_str(), client.refusal().reason.c_str());
        return 1;
    }
    printf("'%s' by %s (%llds), %zu direct formats\n", info.title.c_str(), info.author.c_str(),
           static_cast<long long>(info.length_s), info.formats.size());
    for (const auto& f : info.formats)
        if (f.is_video())
            printf("  itag %3d %-10s %-34s %4dx%-4d @%d %6lld kb/s %s\n", f.itag, f.mime.c_str(), f.codecs.c_str(), f.width,
                   f.height, f.fps, static_cast<long long>(f.bitrate / 1000),
                   f.video_codec().empty() ? "(skipped)" : f.video_codec().c_str());
    for (const auto& f : info.formats)
        if (f.is_audio() && !f.audio_track_id.empty())
            printf("  itag %3d %-10s %-10s track %-8s %s%s\n", f.itag, f.mime.c_str(), f.codecs.c_str(),
                   f.audio_track_id.c_str(), f.audio_track_name.c_str(), f.audio_default ? " (default)" : "");
    printf("%zu caption tracks\n", info.captions.size());
    // Same choice as the TV with "Qualité max" / "Codecs" from the environment (default 2160, avc1,vp9).
    const char* max_env = getenv("MAX_HEIGHT");
    const char* codecs_env = getenv("CODECS");
    auto videos = omega::yt::rank_videos(info, max_env ? atoi(max_env) : 2160,
                                         omega::yt::parse_codec_list(codecs_env ? codecs_env : "avc1,vp9"));
    const omega::yt::Format* video = videos.empty() ? nullptr : videos.front();
    const char* audio_env = getenv("AUDIO");
    const omega::yt::Format* audio = omega::yt::pick_audio(info, audio_env ? audio_env : "");
    if (!video || !audio) {
        printf("no playable video / AAC formats\n");
        return 1;
    }
    printf("video itag %d %dx%d@%d %s, audio itag %d %s %s\n", video->itag, video->width, video->height, video->fps,
           video->codecs.c_str(), audio->itag, audio->codecs.c_str(), audio->audio_track_id.c_str());
    omega::net::Headers headers = {{"User-Agent", info.user_agent}};
    bool ok = drain("audio", *audio, headers, UINT64_MAX);
    ok &= drain("video", *video, headers, video_limit);
    return ok ? 0 : 1;
}
