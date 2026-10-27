#pragma once

#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace omega::media {

enum class Codec { Unknown, H264, AAC, VP9, AV1 };
enum class Container { Mp4, WebM };

const char* codec_name(Codec c);

struct TrackInfo {
    Container container = Container::Mp4;
    Codec codec = Codec::Unknown;
    uint64_t track_number = 0;  // WebM: blocks of other tracks are ignored
    uint32_t timescale = 0;  // ticks per second (WebM: 1e9 / TimecodeScale)
    int64_t media_time = 0;  // edit list shift, in track timescale
    std::vector<uint8_t> codec_data;  // avcC / av1C / vpcC payload, AudioSpecificConfig, or WebM CodecPrivate
    uint32_t width = 0, height = 0;
    uint32_t channels = 0, sample_rate = 0;
    uint32_t default_duration = 0, default_size = 0, default_flags = 0;  // from trex (WebM: DefaultDuration in ms)
};

// One entry of the stream index (sidx / Cues): a self-contained fragment starting with a keyframe.
struct Segment {
    uint64_t offset;      // absolute byte offset of the segment (moof / Cluster)
    int64_t start_ms;     // presentation time of its first sample
    uint64_t start_dts;   // decode time of its first sample, in track timescale (MP4 only)
};

struct Sample {
    std::vector<uint8_t> data;
    int64_t pts_ms = 0;
    int64_t duration_ms = 0;
    bool key = false;
};

// Sequential reader for a single-track DASH stream on a forward-only byte source.
class Demuxer {
public:
    using ReadFn = std::function<size_t(void*, size_t)>;
    virtual ~Demuxer() = default;

    // Reads the init segment and the stream index that follows it, when present.
    virtual bool read_header() = 0;
    const std::vector<Segment>& segments() const { return segments_; }
    // Appends the samples of the next fragment. Returns false on EOF or error.
    virtual bool read_fragment(std::deque<Sample>& out) = 0;

    const TrackInfo& track() const { return track_; }
    const std::string& error() const { return error_; }

protected:
    TrackInfo track_;
    std::string error_;
    std::vector<Segment> segments_;
};

// Sniffs the container (MP4 or WebM) from the first bytes and returns the matching reader.
std::unique_ptr<Demuxer> open_demuxer(Demuxer::ReadFn read);
// Resumes at a segment boundary (after a seek) of a stream whose header was already parsed.
std::unique_ptr<Demuxer> resume_demuxer(Demuxer::ReadFn read, const TrackInfo& track, const Segment& segment);

}  // namespace omega::media
