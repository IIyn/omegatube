#pragma once

#include "media/demuxer.h"

namespace omega::media {

// Sequential reader for single-track fragmented MP4 (the layout YouTube serves for DASH):
// ftyp, moov, [sidx], then moof+mdat pairs. Works on a forward-only byte source.
class Fmp4Reader : public Demuxer {
public:
    explicit Fmp4Reader(ReadFn read) : read_(std::move(read)) {}
    // Resumes at a segment boundary (after a seek) of a stream whose header was already parsed.
    // YouTube fragments carry no tfdt, so the segment's decode time comes from the sidx.
    Fmp4Reader(ReadFn read, const TrackInfo& track, const Segment& segment)
        : read_(std::move(read)), offset_(segment.offset), next_dts_(segment.start_dts) {
        track_ = track;
    }

    // Reads up to and including moov (and the sidx index that follows it, when present).
    bool read_header() override;
    bool read_fragment(std::deque<Sample>& out) override;

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

    uint64_t next_dts_ = 0;
    std::vector<Run> runs_;
    uint64_t base_dts_ = 0;

    // A box read ahead by read_header() (the first moof when there is no sidx).
    bool has_pending_ = false;
    uint32_t pending_type_ = 0;
    uint64_t pending_start_ = 0, pending_payload_start_ = 0;
    std::vector<uint8_t> pending_;
};

}  // namespace omega::media
