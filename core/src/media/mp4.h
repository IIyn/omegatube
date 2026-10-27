#pragma once

#include <cstdint>
#include <deque>
#include <functional>
#include <string>
#include <vector>

namespace omega::media {

enum class Codec { Unknown, H264, AAC };

struct TrackInfo {
    Codec codec = Codec::Unknown;
    uint32_t timescale = 0;
    int64_t media_time = 0;  // edit list shift, in track timescale
    std::vector<uint8_t> codec_data;  // avcC payload / AudioSpecificConfig
    uint32_t width = 0, height = 0;
    uint32_t channels = 0, sample_rate = 0;
    uint32_t default_duration = 0, default_size = 0, default_flags = 0;  // from trex
};

// One entry of the sidx index: a self-contained fragment starting with a keyframe.
struct Segment {
    uint64_t offset;      // absolute byte offset of the segment (its moof)
    int64_t start_ms;     // presentation time of its first sample
    uint64_t start_dts;   // decode time of its first sample, in track timescale
};

struct Sample {
    std::vector<uint8_t> data;
    int64_t pts_ms = 0;
    int64_t duration_ms = 0;
    bool key = false;
};

// Sequential reader for single-track fragmented MP4 (the layout YouTube serves for DASH):
// ftyp, moov, [sidx], then moof+mdat pairs. Works on a forward-only byte source.
class Fmp4Reader {
public:
    using ReadFn = std::function<size_t(void*, size_t)>;

    explicit Fmp4Reader(ReadFn read) : read_(std::move(read)) {}
    // Resumes at a segment boundary (after a seek) of a stream whose header was already parsed.
    // YouTube fragments carry no tfdt, so the segment's decode time comes from the sidx.
    Fmp4Reader(ReadFn read, const TrackInfo& track, const Segment& segment)
        : read_(std::move(read)), offset_(segment.offset), track_(track), next_dts_(segment.start_dts) {}

    // Reads up to and including moov (and the sidx index that follows it, when present).
    bool read_header();
    const std::vector<Segment>& segments() const { return segments_; }
    // Appends the samples of the next fragment. Returns false on EOF or error.
    bool read_fragment(std::deque<Sample>& out);

    const TrackInfo& track() const { return track_; }
    const std::string& error() const { return error_; }

private:
    struct Run {
        uint64_t data_start = 0;  // absolute offset of first sample
        std::vector<uint32_t> sizes, durations, flags;
        std::vector<int32_t> ctos;
    };

    bool read_box_header(uint32_t& type, uint64_t& payload);
    bool read_exact(std::vector<uint8_t>& buf, uint64_t n);
    bool parse_moov(const std::vector<uint8_t>& moov);
    bool parse_moof(const std::vector<uint8_t>& moof, uint64_t moof_start);
    void parse_sidx(const std::vector<uint8_t>& sidx, uint64_t sidx_end);

    ReadFn read_;
    uint64_t offset_ = 0;
    TrackInfo track_;
    std::string error_;

    uint64_t next_dts_ = 0;
    std::vector<Run> runs_;
    uint64_t base_dts_ = 0;
    std::vector<Segment> segments_;

    // A box read ahead by read_header() (the first moof when there is no sidx).
    bool has_pending_ = false;
    uint32_t pending_type_ = 0;
    uint64_t pending_start_ = 0, pending_payload_start_ = 0;
    std::vector<uint8_t> pending_;
};

}  // namespace omega::media
