#include "media/mp4.h"

#include <cstring>

namespace omega::media {
namespace {

constexpr uint32_t fourcc(const char (&s)[5]) {
    return (uint32_t(uint8_t(s[0])) << 24) | (uint32_t(uint8_t(s[1])) << 16) | (uint32_t(uint8_t(s[2])) << 8) |
           uint32_t(uint8_t(s[3]));
}

// Bounds-checked big-endian cursor over a box payload.
struct Cursor {
    const uint8_t* p;
    const uint8_t* end;

    size_t left() const { return static_cast<size_t>(end - p); }
    bool has(size_t n) const { return left() >= n; }
    void skip(size_t n) { p += std::min(n, left()); }
    uint8_t u8() { return has(1) ? *p++ : 0; }
    uint16_t u16() { uint16_t v = 0; for (int i = 0; i < 2 && has(1); ++i) v = uint16_t(v << 8 | *p++); return v; }
    uint32_t u24() { uint32_t v = 0; for (int i = 0; i < 3 && has(1); ++i) v = v << 8 | *p++; return v; }
    uint32_t u32() { uint32_t v = 0; for (int i = 0; i < 4 && has(1); ++i) v = v << 8 | *p++; return v; }
    uint64_t u64() { uint64_t hi = u32(); return hi << 32 | u32(); }
};

// Iterates child boxes of a container payload.
template <typename Fn>
void for_each_box(Cursor c, Fn&& fn) {
    while (c.has(8)) {
        const uint8_t* start = c.p;
        uint64_t size = c.u32();
        uint32_t type = c.u32();
        size_t header = 8;
        if (size == 1) {
            size = c.u64();
            header = 16;
        } else if (size == 0) {
            size = static_cast<uint64_t>(c.end - start);
        }
        if (size < header || size > static_cast<uint64_t>(c.end - start)) return;
        fn(type, Cursor{start + header, start + size});
        c.p = start + size;
    }
}

// MPEG-4 descriptor length (1-4 bytes, 7 bits each).
uint32_t descriptor_len(Cursor& c) {
    uint32_t len = 0;
    for (int i = 0; i < 4; ++i) {
        uint8_t b = c.u8();
        len = len << 7 | (b & 0x7f);
        if (!(b & 0x80)) break;
    }
    return len;
}

std::vector<uint8_t> parse_esds_asc(Cursor c) {
    c.skip(4);  // version + flags
    while (c.has(2)) {
        uint8_t tag = c.u8();
        uint32_t len = descriptor_len(c);
        if (tag == 0x03) {  // ES_Descriptor
            c.skip(2);
            uint8_t flags = c.u8();
            if (flags & 0x80) c.skip(2);
            if (flags & 0x40) c.skip(c.u8());
            if (flags & 0x20) c.skip(2);
        } else if (tag == 0x04) {  // DecoderConfigDescriptor
            c.skip(13);
        } else if (tag == 0x05) {  // DecoderSpecificInfo = AudioSpecificConfig
            len = std::min<uint32_t>(len, static_cast<uint32_t>(c.left()));
            return {c.p, c.p + len};
        } else {
            c.skip(len);
        }
    }
    return {};
}

}  // namespace

bool Fmp4Reader::read_exact(std::vector<uint8_t>& buf, uint64_t n) {
    buf.resize(static_cast<size_t>(n));
    size_t got = n ? read_(buf.data(), buf.size()) : 0;
    offset_ += got;
    return got == n;
}

bool Fmp4Reader::read_box_header(uint32_t& type, uint64_t& payload) {
    uint8_t h[16];
    size_t got = read_(h, 8);
    offset_ += got;
    if (got != 8) return false;
    uint64_t size = (uint32_t(h[0]) << 24) | (uint32_t(h[1]) << 16) | (uint32_t(h[2]) << 8) | h[3];
    type = (uint32_t(h[4]) << 24) | (uint32_t(h[5]) << 16) | (uint32_t(h[6]) << 8) | h[7];
    uint64_t header = 8;
    if (size == 1) {
        if (read_(h + 8, 8) != 8) return false;
        offset_ += 8;
        size = 0;
        for (int i = 8; i < 16; ++i) size = size << 8 | h[i];
        header = 16;
    }
    if (size < header) {
        error_ = "bad box size";
        return false;
    }
    payload = size - header;
    return true;
}

bool Fmp4Reader::read_header() {
    std::vector<uint8_t> buf;
    for (;;) {
        uint32_t type;
        uint64_t payload;
        if (!read_box_header(type, payload)) {
            if (error_.empty()) error_ = "eof before moov";
            return false;
        }
        if (!read_exact(buf, payload)) {
            error_ = "truncated box";
            return false;
        }
        if (type == fourcc("moov")) {
            if (!parse_moov(buf)) return false;
            break;
        }
    }
    // Peek at the next box: YouTube (and ffmpeg's global_sidx) put the segment index right here.
    uint64_t start = offset_;
    uint32_t type;
    uint64_t payload;
    if (!read_box_header(type, payload)) return true;  // header-only file
    uint64_t payload_start = offset_;
    if (!read_exact(buf, payload)) return true;
    if (type == fourcc("sidx")) {
        parse_sidx(buf, offset_);
    } else {
        has_pending_ = true;
        pending_type_ = type;
        pending_start_ = start;
        pending_payload_start_ = payload_start;
        pending_.swap(buf);
    }
    return true;
}

void Fmp4Reader::parse_sidx(const std::vector<uint8_t>& sidx, uint64_t sidx_end) {
    Cursor c{sidx.data(), sidx.data() + sidx.size()};
    uint8_t version = c.u8();
    c.skip(3 + 4);  // flags, reference_ID
    uint32_t timescale = c.u32();
    uint64_t earliest = version == 0 ? c.u32() : c.u64();
    uint64_t first_offset = version == 0 ? c.u32() : c.u64();
    c.skip(2);
    uint16_t count = c.u16();
    if (timescale == 0) return;
    uint64_t offset = sidx_end + first_offset;
    uint64_t time = earliest;
    for (uint16_t i = 0; i < count && c.has(12); ++i) {
        uint32_t size = c.u32() & 0x7fffffff;
        uint32_t duration = c.u32();
        c.skip(4);  // SAP info
        int64_t start_ticks = static_cast<int64_t>(time) - track_.media_time * static_cast<int64_t>(timescale) /
                                                                static_cast<int64_t>(track_.timescale);
        uint64_t dts = time * track_.timescale / timescale;
        segments_.push_back({offset, std::max<int64_t>(start_ticks, 0) * 1000 / timescale, dts});
        offset += size;
        time += duration;
    }
}

bool Fmp4Reader::parse_moov(const std::vector<uint8_t>& moov) {
    Cursor root{moov.data(), moov.data() + moov.size()};
    for_each_box(root, [&](uint32_t type, Cursor c) {
        if (type == fourcc("mvex")) {
            for_each_box(c, [&](uint32_t t, Cursor e) {
                if (t != fourcc("trex")) return;
                e.skip(4 + 4 + 4);  // version/flags, track_ID, default_sample_description_index
                track_.default_duration = e.u32();
                track_.default_size = e.u32();
                track_.default_flags = e.u32();
            });
        } else if (type == fourcc("trak")) {
            for_each_box(c, [&](uint32_t t, Cursor trak) {
                if (t == fourcc("edts")) {
                    for_each_box(trak, [&](uint32_t et, Cursor e) {
                        if (et != fourcc("elst")) return;
                        uint8_t version = e.u8();
                        e.skip(3);
                        uint32_t count = e.u32();
                        for (uint32_t i = 0; i < count; ++i) {
                            int64_t media_time;
                            if (version == 1) {
                                e.skip(8);
                                media_time = static_cast<int64_t>(e.u64());
                            } else {
                                e.skip(4);
                                media_time = static_cast<int32_t>(e.u32());
                            }
                            e.skip(4);
                            if (media_time >= 0) {
                                track_.media_time = media_time;
                                break;
                            }
                        }
                    });
                } else if (t == fourcc("mdia")) {
                    for_each_box(trak, [&](uint32_t mt, Cursor mdia) {
                        if (mt == fourcc("mdhd")) {
                            uint8_t version = mdia.u8();
                            mdia.skip(3 + (version == 1 ? 16 : 8));
                            track_.timescale = mdia.u32();
                        } else if (mt == fourcc("minf")) {
                            for_each_box(mdia, [&](uint32_t it, Cursor minf) {
                                if (it != fourcc("stbl")) return;
                                for_each_box(minf, [&](uint32_t st, Cursor stbl) {
                                    if (st != fourcc("stsd")) return;
                                    stbl.skip(8);  // version/flags + entry_count
                                    for_each_box(stbl, [&](uint32_t entry, Cursor se) {
                                        if (entry == fourcc("avc1") || entry == fourcc("avc3")) {
                                            track_.codec = Codec::H264;
                                            se.skip(24);
                                            track_.width = se.u16();
                                            track_.height = se.u16();
                                            se.skip(50);
                                            for_each_box(se, [&](uint32_t ct, Cursor cc) {
                                                if (ct == fourcc("avcC")) track_.codec_data.assign(cc.p, cc.end);
                                            });
                                        } else if (entry == fourcc("mp4a")) {
                                            track_.codec = Codec::AAC;
                                            se.skip(16);
                                            track_.channels = se.u16();
                                            se.skip(6);
                                            track_.sample_rate = se.u32() >> 16;
                                            for_each_box(se, [&](uint32_t ct, Cursor cc) {
                                                if (ct == fourcc("esds")) track_.codec_data = parse_esds_asc(cc);
                                            });
                                        }
                                    });
                                });
                            });
                        }
                    });
                }
            });
        }
    });
    if (track_.codec == Codec::Unknown || track_.timescale == 0 || track_.codec_data.empty()) {
        error_ = "unsupported or incomplete moov";
        return false;
    }
    return true;
}

bool Fmp4Reader::parse_moof(const std::vector<uint8_t>& moof, uint64_t moof_start) {
    runs_.clear();
    Cursor root{moof.data(), moof.data() + moof.size()};
    for_each_box(root, [&](uint32_t type, Cursor traf) {
        if (type != fourcc("traf")) return;
        uint64_t base = moof_start;
        uint32_t def_duration = track_.default_duration, def_size = track_.default_size, def_flags = track_.default_flags;
        for_each_box(traf, [&](uint32_t t, Cursor c) {
            if (t == fourcc("tfhd")) {
                uint32_t flags = c.u32() & 0xffffff;
                c.skip(4);  // track_ID
                if (flags & 0x1) base = c.u64();
                if (flags & 0x2) c.skip(4);
                if (flags & 0x8) def_duration = c.u32();
                if (flags & 0x10) def_size = c.u32();
                if (flags & 0x20) def_flags = c.u32();
            } else if (t == fourcc("tfdt")) {
                uint8_t version = c.u8();
                c.skip(3);
                base_dts_ = version == 1 ? c.u64() : c.u32();
                next_dts_ = base_dts_;
            } else if (t == fourcc("trun")) {
                uint32_t vf = c.u32();
                uint8_t version = uint8_t(vf >> 24);
                uint32_t flags = vf & 0xffffff;
                uint32_t count = c.u32();
                Run run;
                int32_t data_offset = (flags & 0x1) ? static_cast<int32_t>(c.u32()) : 0;
                run.data_start = base + static_cast<uint64_t>(static_cast<int64_t>(data_offset));
                uint32_t first_flags = (flags & 0x4) ? c.u32() : def_flags;
                for (uint32_t i = 0; i < count && c.has(1); ++i) {
                    run.durations.push_back((flags & 0x100) ? c.u32() : def_duration);
                    run.sizes.push_back((flags & 0x200) ? c.u32() : def_size);
                    uint32_t sf = (flags & 0x400) ? c.u32() : (i == 0 && (flags & 0x4) ? first_flags : def_flags);
                    run.flags.push_back(sf);
                    uint32_t cto = (flags & 0x800) ? c.u32() : 0;
                    run.ctos.push_back(version == 0 ? static_cast<int32_t>(std::min<uint32_t>(cto, INT32_MAX))
                                                    : static_cast<int32_t>(cto));
                }
                runs_.push_back(std::move(run));
            }
        });
    });
    return !runs_.empty();
}

bool Fmp4Reader::read_fragment(std::deque<Sample>& out) {
    std::vector<uint8_t> buf;
    for (;;) {
        uint64_t box_start, payload_start;
        uint32_t type;
        if (has_pending_) {
            has_pending_ = false;
            type = pending_type_;
            box_start = pending_start_;
            payload_start = pending_payload_start_;
            buf.swap(pending_);
        } else {
            box_start = offset_;
            uint64_t payload;
            if (!read_box_header(type, payload)) return false;
            payload_start = offset_;
            if (!read_exact(buf, payload)) {
                error_ = "truncated box";
                return false;
            }
        }
        if (type == fourcc("moof")) {
            if (!parse_moof(buf, box_start)) {
                error_ = "moof without trun";
                return false;
            }
        } else if (type == fourcc("mdat") && !runs_.empty()) {
            const double to_ms = 1000.0 / track_.timescale;
            for (const Run& run : runs_) {
                uint64_t at = run.data_start;
                for (size_t i = 0; i < run.sizes.size(); ++i) {
                    if (at < payload_start || at + run.sizes[i] > payload_start + buf.size()) {
                        error_ = "sample outside mdat";
                        return false;
                    }
                    Sample s;
                    const uint8_t* src = buf.data() + (at - payload_start);
                    s.data.assign(src, src + run.sizes[i]);
                    int64_t pts = static_cast<int64_t>(next_dts_) + run.ctos[i] - track_.media_time;
                    s.pts_ms = static_cast<int64_t>(std::max<int64_t>(pts, 0) * to_ms);
                    s.duration_ms = static_cast<int64_t>(run.durations[i] * to_ms + 0.5);
                    s.key = !(run.flags[i] & 0x10000);
                    out.push_back(std::move(s));
                    next_dts_ += run.durations[i];
                    at += run.sizes[i];
                }
            }
            runs_.clear();
            return true;
        }
    }
}

}  // namespace omega::media
