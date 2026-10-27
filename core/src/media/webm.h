#pragma once

#include "media/demuxer.h"

namespace omega::media {

// Sequential reader for single-track WebM (the layout YouTube serves for VP9 DASH):
// EBML header, Segment { SeekHead, Info, Tracks, Cues, Cluster... }. Works on a forward-only byte source.
// One fragment = one Cluster. Clusters of unknown size are supported (they end at the next Cluster).
class WebmReader : public Demuxer {
public:
    explicit WebmReader(ReadFn read) : read_(std::move(read)) {}
    // Resumes at a Cluster (after a seek) of a stream whose header was already parsed.
    WebmReader(ReadFn read, const TrackInfo& track, const Segment& segment)
        : read_(std::move(read)), offset_(segment.offset) {
        track_ = track;
        if (track.timescale) timecode_scale_ = 1000000000ull / track.timescale;
    }

    // Reads up to the first Cluster, parsing Info, Tracks and the Cues index.
    bool read_header() override;
    bool read_fragment(std::deque<Sample>& out) override;

private:
    bool read_vint(uint64_t& value, bool keep_marker, bool& unknown);
    bool read_element_header(uint32_t& id, uint64_t& size, bool& unknown);
    bool read_exact(std::vector<uint8_t>& buf, uint64_t n);
    bool skip(uint64_t n);
    bool parse_info(const std::vector<uint8_t>& info);
    bool parse_tracks(const std::vector<uint8_t>& tracks);
    void parse_cues(const std::vector<uint8_t>& cues);
    bool parse_block(const uint8_t* p, size_t n, bool simple, bool has_reference, std::deque<Sample>& out);
    void finish_cluster(std::deque<Sample>& out, size_t first);

    ReadFn read_;
    uint64_t offset_ = 0;
    uint64_t segment_data_ = 0;   // absolute offset of the Segment payload (Cues positions are relative to it)
    uint64_t timecode_scale_ = 1000000;  // ns per tick
    int64_t cluster_time_ = 0;    // ticks
    int64_t last_delta_ms_ = 0;   // last frame duration seen (for the last frame of a cluster)

    // An element header read ahead (the first Cluster after the header, or the next Cluster ending a fragment).
    bool has_pending_ = false;
    uint32_t pending_id_ = 0;
};

}  // namespace omega::media
