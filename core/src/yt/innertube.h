#pragma once

#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include "net/http.h"

namespace omega::yt {

struct Format {
    int itag = 0;
    std::string url;
    std::string mime;    // e.g. video/mp4
    std::string codecs;  // e.g. avc1.64002A
    int width = 0, height = 0, fps = 0;
    int64_t bitrate = 0;
    int64_t content_length = 0;
    int audio_sample_rate = 0, audio_channels = 0;
    bool is_video() const { return mime.compare(0, 6, "video/") == 0; }
    bool is_audio() const { return mime.compare(0, 6, "audio/") == 0; }
};

// One entry of a feed (search results, channel videos...).
struct FeedItem {
    std::string video_id, title, channel, duration, views, published, thumbnail;
    bool is_live = false;
    int watched_percent = 0;  // account's progress as YouTube reports it (signed-in feeds)
};

struct Feed {
    std::vector<FeedItem> items;
    std::string continuation;  // empty when there is no next page
};

struct VideoInfo {
    std::string id, title, author;
    int64_t length_s = 0;
    std::vector<Format> formats;  // directly playable adaptive formats only
};

// InnerTube client impersonating the visionOS YouTube app: as of 2026-10 it gets direct (non-ciphered,
// non-throttled) stream URLs without PO token. See tools/yt-probe/probe.py to re-check.
class Client {
public:
    bool player(const std::string& video_id, VideoInfo& out, std::string& error);
    // HTTP headers the googlevideo stream requests must carry (same User-Agent as the player request).
    net::Headers stream_headers() const;

    // Video search (WEB client). Pass the previous Feed::continuation to get the next page.
    bool search(const std::string& query, const std::string& continuation, Feed& out, std::string& error);
    // Related videos of a watch page (WEB client "next" endpoint), for the player's suggestions row.
    bool related(const std::string& video_id, Feed& out, std::string& error);
    // Signed-in feeds through the TV client: FEwhat_to_watch (home), FEsubscriptions, FEhistory.
    bool browse(const std::string& browse_id, const std::string& continuation, const std::string& access_token,
                Feed& out, std::string& error);

private:
    bool ensure_visitor(std::string& error);
    std::mutex mutex_;  // guards the lazily fetched session data below
    std::string visitor_data_;
    std::string web_client_version_;
};

// Best H.264 video at or below max_height (prefers higher fps), and the AAC audio track.
const Format* pick_video(const VideoInfo& info, int max_height);
const Format* pick_audio(const VideoInfo& info);

// Search-as-you-type suggestions (Google suggest service, YouTube dataset).
bool suggest(const std::string& query, std::vector<std::string>& out, std::string& error);

// Shared Client used by the C API (thread-safe: requests are serialised).
Client& shared_client();

}  // namespace omega::yt
