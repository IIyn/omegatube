#pragma once

#include <cstdint>
#include <mutex>
#include <string>
#include <utility>
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
    // Videos with several audio languages (dubbed): the same itags once per track.
    std::string audio_track_id;    // e.g. "fr.3", "en.4" (empty: single-track video)
    std::string audio_track_name;  // e.g. "Français (doublage)", "English (United States) original"
    bool audio_default = false;
    bool is_video() const { return mime.compare(0, 6, "video/") == 0; }
    bool is_audio() const { return mime.compare(0, 6, "audio/") == 0; }
    // "avc1", "vp9" or "av01" for the video codecs we can play (8-bit SDR only), else "".
    std::string video_codec() const;
};

// One entry of a feed (search results, channel videos...).
struct FeedItem {
    std::string video_id, title, channel, duration, views, published, thumbnail;
    // Playlists and mixes: playlist_id + the first video to play (video_id). Videos inside a playlist
    // (Client::playlist) keep kind "video" with the playlist_id they belong to.
    std::string playlist_id;
    std::string kind = "video";  // "video" | "playlist" | "mix"
    bool is_live = false;
    int watched_percent = 0;  // account's progress as YouTube reports it (signed-in feeds)
};

struct Feed {
    std::vector<FeedItem> items;
    // FEsubscriptions: YouTube's picked shelf ("Les plus pertinentes") kept apart from the chronological feed.
    std::vector<FeedItem> highlights;
    std::string continuation;  // empty when there is no next page
};

// One subtitle track of a video (playerCaptionsTracklistRenderer).
struct CaptionTrack {
    std::string url;   // timedtext base URL (fetch_captions adds the format / translation)
    std::string lang;  // e.g. "fr", "en-US"
    std::string name;  // e.g. "Français", "Anglais (générés automatiquement)"
    bool automatic = false;   // speech recognition ("asr")
    bool translatable = false;
};

struct CaptionCue {
    int64_t start_ms = 0, end_ms = 0;
    std::string text;
};

struct VideoInfo {
    std::string id, title, author;
    std::string description, views;  // views: raw count
    std::string channel_id;
    int64_t length_s = 0;
    bool is_live = false;  // broadcasting now: formats are numbered-segment streams (net::LiveReader)
    std::string user_agent;  // of the client that resolved the formats (stream requests must send it)
    std::vector<Format> formats;  // directly playable adaptive formats only
    std::vector<CaptionTrack> captions;
    // Languages a translatable caption track can be machine-translated to: (code, name).
    std::vector<std::pair<std::string, std::string>> translations;
};

struct VideoDetails {
    std::string channel_id, channel, channel_avatar, subscribers;  // subscribers: display text ("1,2 M d'abonnés")
    std::string date;           // display text ("12 mars 2023")
    std::string like_status;    // "LIKE" | "DISLIKE" | "INDIFFERENT" | "" (signed out / unknown)
    int subscribed = -1;        // 1 / 0, -1 unknown
};

struct ChannelInfo {
    std::string title, avatar, subscribers;
};

// InnerTube client impersonating the visionOS YouTube app: as of 2026-10 it gets direct (non-ciphered,
// non-throttled) stream URLs without PO token. See tools/yt-probe/probe.py to re-check.
class Client {
public:
    // Tries the primary client, then (remote-config "playerFallback") the TV client with deciphering.
    bool player(const std::string& video_id, VideoInfo& out, std::string& error);
    bool player_fallback(const std::string& video_id, VideoInfo& out, std::string& error);
    // HTTP headers the googlevideo stream requests must carry (same User-Agent as the player request).
    net::Headers stream_headers() const;

    // Video search (WEB client). Pass the previous Feed::continuation to get the next page.
    bool search(const std::string& query, const std::string& continuation, Feed& out, std::string& error);
    // The videos after video_id in a playlist or mix (WEB "next" endpoint with playlistId). A mix is endless:
    // asking again from a later video continues it.
    bool playlist(const std::string& video_id, const std::string& playlist_id, Feed& out, std::string& title,
                  std::string& error);
    // Related videos of a watch page (WEB client "next" endpoint), for the player's suggestions row.
    bool related(const std::string& video_id, Feed& out, std::string& error);
    // Watch-page details (WEB "next"): date, channel; plus, with an access token, the account's like and
    // subscription state (TV "next").
    bool details(const std::string& video_id, const std::string& access_token, struct VideoDetails& out,
                 std::string& error);
    // A channel's videos tab (WEB browse), newest first; the first page also fills the channel header.
    bool channel(const std::string& channel_id, const std::string& continuation, Feed& out, struct ChannelInfo& header,
                 std::string& error);
    // Signed-in feeds through the TV client: FEwhat_to_watch (home), FEsubscriptions, FEhistory.
    bool browse(const std::string& browse_id, const std::string& continuation, const std::string& access_token,
                Feed& out, std::string& error);

    // Raw JSON of the last player/browse response (dev tooling: ytcheck --dump).
    const std::string& last_response() const { return last_response_; }

private:
    bool player_with(const char* role, const std::string& video_id, VideoInfo& out, std::string& error);
    bool ensure_visitor(std::string& error);
    std::string last_response_;
    std::mutex mutex_;  // guards the lazily fetched session data below
    std::string visitor_data_;
    std::string web_client_version_;
};

// Playable video formats at or below max_height, best first: highest resolution, then fps, then the codec
// order of `codecs` (e.g. {"avc1", "vp9", "av01"}; codecs not listed are excluded).
std::vector<const Format*> rank_videos(const VideoInfo& info, int max_height, const std::vector<std::string>& codecs);
// "avc1,vp9" -> {"avc1", "vp9"}
std::vector<std::string> parse_codec_list(const std::string& list);
// The AAC format with the highest bitrate of the preferred audio track: `track` is a track id ("fr.3") or a
// language ("fr"); empty, or not offered, picks the default (original) track.
const Format* pick_audio(const VideoInfo& info, const std::string& track = "");

// Downloads a caption track (json3), optionally machine-translated to `translate_to`, as timed cues.
bool fetch_captions(const std::string& url, const std::string& translate_to, std::vector<CaptionCue>& out,
                    std::string& error);

// Like / dislike / neutral ("like", "dislike", "none") and (un)subscribe, as the signed-in account (TV client).
bool rate_video(const std::string& video_id, const std::string& rating, const std::string& access_token,
                std::string& error);
bool set_subscribed(const std::string& channel_id, bool subscribe, const std::string& access_token, std::string& error);

// Return YouTube Dislike estimates (returnyoutubedislikeapi.com): likes, dislikes; -1 when unknown.
bool vote_counts(const std::string& video_id, int64_t& likes, int64_t& dislikes, std::string& error);

// Name and avatar of the signed-in account (TV client accounts_list).
struct AccountInfo {
    std::string name, handle, photo;
};
bool account_info(const std::string& access_token, AccountInfo& out, std::string& error);

// Search-as-you-type suggestions (Google suggest service, YouTube dataset).
bool suggest(const std::string& query, std::vector<std::string>& out, std::string& error);

// Shared Client used by the C API (thread-safe: requests are serialised).
Client& shared_client();

}  // namespace omega::yt
