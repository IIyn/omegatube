#include "media/webm.h"

#include <cstring>

namespace omega::media {
namespace {

// Matroska element IDs (marker bits kept, as written in the file).
constexpr uint32_t kEbml = 0x1A45DFA3;
constexpr uint32_t kSegment = 0x18538067;
constexpr uint32_t kInfo = 0x1549A966;
constexpr uint32_t kTimecodeScale = 0x2AD7B1;
constexpr uint32_t kTracks = 0x1654AE6B;
constexpr uint32_t kTrackEntry = 0xAE;
constexpr uint32_t kTrackNumber = 0xD7;
constexpr uint32_t kTrackType = 0x83;
constexpr uint32_t kCodecId = 0x86;
constexpr uint32_t kCodecPrivate = 0x63A2;
constexpr uint32_t kDefaultDuration = 0x23E383;
constexpr uint32_t kVideo = 0xE0;
constexpr uint32_t kPixelWidth = 0xB0;
constexpr uint32_t kPixelHeight = 0xBA;
constexpr uint32_t kAudio = 0xE1;
constexpr uint32_t kSamplingFrequency = 0xB5;
constexpr uint32_t kChannels = 0x9F;
constexpr uint32_t kCues = 0x1C53BB6B;
constexpr uint32_t kCuePoint = 0xBB;
constexpr uint32_t kCueTime = 0xB3;
constexpr uint32_t kCueTrackPositions = 0xB7;
constexpr uint32_t kCueTrack = 0xF7;
constexpr uint32_t kCueClusterPosition = 0xF1;
constexpr uint32_t kCluster = 0x1F43B675;
constexpr uint32_t kTimecode = 0xE7;
constexpr uint32_t kSimpleBlock = 0xA3;
constexpr uint32_t kBlockGroup = 0xA0;
constexpr uint32_t kBlock = 0xA1;
constexpr uint32_t kReferenceBlock = 0xFB;

// Bounds-checked cursor over an element payload.
struct Cursor {
    const uint8_t* p;
    const uint8_t* end;

    size_t left() const { return static_cast<size_t>(end - p); }

    // EBML variable-length integer; IDs keep their marker bits, sizes do not.
    bool vint(uint64_t& v, bool keep_marker, bool* unknown = nullptr) {
        if (p >= end || *p == 0) return false;
        int len = 1;
        while (!(*p & (0x80 >> (len - 1)))) ++len;
        if (left() < static_cast<size_t>(len)) return false;
        v = keep_marker ? *p : (*p & (0xFF >> len));
        for (int i = 1; i < len; ++i) v = v << 8 | p[i];
        if (unknown) *unknown = !keep_marker && v == (1ull << (7 * len)) - 1;  // all value bits set
        p += len;
        return true;
    }
};

uint64_t to_uint(Cursor c) {
    uint64_t v = 0;
    for (size_t i = 0; i < c.left() && i < 8; ++i) v = v << 8 | c.p[i];
    return v;
}

double to_float(Cursor c) {
    if (c.left() == 4) {
        uint32_t bits = static_cast<uint32_t>(to_uint(c));
        float f;
        memcpy(&f, &bits, 4);
        return f;
    }
    if (c.left() == 8) {
        uint64_t bits = to_uint(c);
        double d;
        memcpy(&d, &bits, 8);
        return d;
    }
    return 0;
}

// Iterates the child elements of a master element payload.
template <typename Fn>
void for_each_element(Cursor c, Fn&& fn) {
    while (c.left() >= 2) {
        uint64_t id, size;
        bool unknown = false;
        if (!c.vint(id, true) || !c.vint(size, false, &unknown) || unknown || size > c.left()) return;
        fn(static_cast<uint32_t>(id), Cursor{c.p, c.p + size});
        c.p += size;
    }
}

}  // namespace

bool WebmReader::read_vint(uint64_t& value, bool keep_marker, bool& unknown) {
    uint8_t b[8];
    if (read_(b, 1) != 1) return false;
    offset_ += 1;
    if (b[0] == 0) {
        error_ = "bad EBML vint";
        return false;
    }
    int len = 1;
    while (!(b[0] & (0x80 >> (len - 1)))) ++len;
    if (keep_marker && len > 4) {
        error_ = "bad EBML id";
        return false;
    }
    if (len > 1) {
        size_t got = read_(b + 1, static_cast<size_t>(len - 1));
        offset_ += got;
        if (got != static_cast<size_t>(len - 1)) return false;
    }
    Cursor c{b, b + len};
    return c.vint(value, keep_marker, keep_marker ? nullptr : &unknown);
}

bool WebmReader::read_element_header(uint32_t& id, uint64_t& size, bool& unknown) {
    uint64_t raw_id;
    bool ignored;
    if (!read_vint(raw_id, true, ignored)) return false;
    id = static_cast<uint32_t>(raw_id);
    return read_vint(size, false, unknown);
}

bool WebmReader::read_exact(std::vector<uint8_t>& buf, uint64_t n) {
    buf.resize(static_cast<size_t>(n));
    size_t got = n ? read_(buf.data(), buf.size()) : 0;
    offset_ += got;
    return got == n;
}

bool WebmReader::skip(uint64_t n) {
    uint8_t tmp[16 * 1024];
    while (n > 0) {
        size_t want = static_cast<size_t>(std::min<uint64_t>(n, sizeof tmp));
        size_t got = read_(tmp, want);
        offset_ += got;
        if (got != want) return false;
        n -= got;
    }
    return true;
}

bool WebmReader::read_header() {
    track_.container = Container::WebM;
    std::vector<uint8_t> buf;
    uint32_t id;
    uint64_t size;
    bool unknown;
    if (!read_element_header(id, size, unknown) || id != kEbml || unknown || !skip(size)) {
        if (error_.empty()) error_ = "not an EBML file";
        return false;
    }
    if (!read_element_header(id, size, unknown) || id != kSegment) {
        if (error_.empty()) error_ = "no Segment";
        return false;
    }
    segment_data_ = offset_;
    bool have_tracks = false;
    for (;;) {
        if (!read_element_header(id, size, unknown)) {
            if (!have_tracks && error_.empty()) error_ = "eof before Tracks";
            return have_tracks;  // header-only file
        }
        if (id == kCluster) {
            has_pending_ = true;
            pending_id_ = id;
            break;
        }
        if (unknown) {
            error_ = "unknown-size element in Segment header";
            return false;
        }
        if (id == kInfo || id == kTracks || id == kCues) {
            if (!read_exact(buf, size)) {
                error_ = "truncated element";
                return false;
            }
            if (id == kInfo && !parse_info(buf)) return false;
            if (id == kTracks) {
                if (!parse_tracks(buf)) return false;
                have_tracks = true;
            }
            if (id == kCues) parse_cues(buf);
        } else if (!skip(size)) {
            error_ = "truncated element";
            return false;
        }
    }
    if (!have_tracks) {
        error_ = "Cluster before Tracks";
        return false;
    }
    return true;
}

bool WebmReader::parse_info(const std::vector<uint8_t>& info) {
    for_each_element(Cursor{info.data(), info.data() + info.size()}, [&](uint32_t id, Cursor c) {
        if (id == kTimecodeScale) timecode_scale_ = to_uint(c);
    });
    if (timecode_scale_ == 0) {
        error_ = "bad TimecodeScale";
        return false;
    }
    track_.timescale = static_cast<uint32_t>(1000000000ull / timecode_scale_);
    return true;
}

bool WebmReader::parse_tracks(const std::vector<uint8_t>& tracks) {
    for_each_element(Cursor{tracks.data(), tracks.data() + tracks.size()}, [&](uint32_t id, Cursor entry) {
        if (id != kTrackEntry || track_.track_number) return;  // keep the first video/audio track
        uint64_t number = 0, type = 0, default_duration_ns = 0;
        std::string codec_id;
        TrackInfo t = track_;
        for_each_element(entry, [&](uint32_t eid, Cursor c) {
            if (eid == kTrackNumber) number = to_uint(c);
            else if (eid == kTrackType) type = to_uint(c);
            else if (eid == kCodecId) codec_id.assign(reinterpret_cast<const char*>(c.p), c.left());
            else if (eid == kCodecPrivate) t.codec_data.assign(c.p, c.end);
            else if (eid == kDefaultDuration) default_duration_ns = to_uint(c);
            else if (eid == kVideo) {
                for_each_element(c, [&](uint32_t vid, Cursor v) {
                    if (vid == kPixelWidth) t.width = static_cast<uint32_t>(to_uint(v));
                    else if (vid == kPixelHeight) t.height = static_cast<uint32_t>(to_uint(v));
                });
            } else if (eid == kAudio) {
                for_each_element(c, [&](uint32_t aid, Cursor a) {
                    if (aid == kSamplingFrequency) t.sample_rate = static_cast<uint32_t>(to_float(a));
                    else if (aid == kChannels) t.channels = static_cast<uint32_t>(to_uint(a));
                });
            }
        });
        if (type != 1 && type != 2) return;  // video / audio only
        while (!codec_id.empty() && codec_id.back() == '\0') codec_id.pop_back();
        if (codec_id == "V_VP9") t.codec = Codec::VP9;
        else if (codec_id == "V_AV1") t.codec = Codec::AV1;
        else if (codec_id == "V_MPEG4/ISO/AVC") t.codec = Codec::H264;
        else if (codec_id == "A_AAC") t.codec = Codec::AAC;
        t.default_duration = static_cast<uint32_t>(default_duration_ns / 1000000);
        t.track_number = number;
        track_ = std::move(t);
        if (track_.codec == Codec::Unknown) error_ = "unsupported codec " + codec_id;
    });
    if (!track_.track_number) {
        error_ = "no video/audio track";
        return false;
    }
    return track_.codec != Codec::Unknown;
}

void WebmReader::parse_cues(const std::vector<uint8_t>& cues) {
    segments_.clear();
    for_each_element(Cursor{cues.data(), cues.data() + cues.size()}, [&](uint32_t id, Cursor point) {
        if (id != kCuePoint) return;
        uint64_t time = 0, position = 0, track = 0;
        bool have_position = false;
        for_each_element(point, [&](uint32_t pid, Cursor c) {
            if (pid == kCueTime) time = to_uint(c);
            else if (pid == kCueTrackPositions && !have_position) {
                for_each_element(c, [&](uint32_t tid, Cursor t) {
                    if (tid == kCueTrack) track = to_uint(t);
                    else if (tid == kCueClusterPosition) position = to_uint(t), have_position = true;
                });
            }
        });
        if (!have_position || (track && track != track_.track_number)) return;
        int64_t ms = static_cast<int64_t>(time * timecode_scale_ / 1000000);
        segments_.push_back({segment_data_ + position, ms, 0});
    });
}

bool WebmReader::parse_block(const uint8_t* p, size_t n, bool simple, bool has_reference,
                             std::deque<Sample>& out) {
    Cursor c{p, p + n};
    uint64_t track;
    if (!c.vint(track, false) || c.left() < 3) {
        error_ = "bad block header";
        return false;
    }
    if (track != track_.track_number) return true;
    int16_t rel = static_cast<int16_t>(c.p[0] << 8 | c.p[1]);
    uint8_t flags = c.p[2];
    c.p += 3;
    if (flags & 0x06) {
        error_ = "laced blocks are not supported";
        return false;
    }
    Sample s;
    s.data.assign(c.p, c.end);
    s.pts_ms = static_cast<int64_t>((cluster_time_ + rel) * static_cast<int64_t>(timecode_scale_) / 1000000);
    s.key = simple ? (flags & 0x80) != 0 : !has_reference;
    out.push_back(std::move(s));
    return true;
}

// Blocks carry no duration: derive it from the next frame's timestamp, or the last known delta.
void WebmReader::finish_cluster(std::deque<Sample>& out, size_t first) {
    for (size_t i = first; i < out.size(); ++i) {
        if (i + 1 < out.size() && out[i + 1].pts_ms > out[i].pts_ms) {
            out[i].duration_ms = out[i + 1].pts_ms - out[i].pts_ms;
            last_delta_ms_ = out[i].duration_ms;
        } else {
            out[i].duration_ms = track_.default_duration ? track_.default_duration : last_delta_ms_;
        }
    }
}

bool WebmReader::read_fragment(std::deque<Sample>& out) {
    std::vector<uint8_t> buf;
    const size_t first = out.size();
    bool in_cluster = false;
    for (;;) {
        uint32_t id;
        uint64_t size = 0;
        bool unknown = false;
        if (has_pending_) {
            has_pending_ = false;
            id = pending_id_;
        } else if (!read_element_header(id, size, unknown)) {
            if (out.size() > first) {
                finish_cluster(out, first);
                return true;
            }
            return false;
        }
        if (id == kCluster) {
            // Descend without reading the payload: the children follow as if they were siblings.
            if (in_cluster) {
                has_pending_ = true;
                pending_id_ = id;
                finish_cluster(out, first);
                return true;
            }
            in_cluster = true;
            cluster_time_ = 0;
            continue;
        }
        if (unknown) {
            error_ = "unknown-size element";
            return false;
        }
        if (id == kTimecode || id == kSimpleBlock || id == kBlockGroup) {
            if (!read_exact(buf, size)) {
                error_ = "truncated element";
                return false;
            }
            if (id == kTimecode) {
                cluster_time_ = static_cast<int64_t>(to_uint(Cursor{buf.data(), buf.data() + buf.size()}));
            } else if (id == kSimpleBlock) {
                if (!parse_block(buf.data(), buf.size(), true, false, out)) return false;
            } else {
                const uint8_t* block = nullptr;
                size_t block_size = 0;
                bool has_reference = false;
                for_each_element(Cursor{buf.data(), buf.data() + buf.size()}, [&](uint32_t gid, Cursor c) {
                    if (gid == kBlock) block = c.p, block_size = c.left();
                    else if (gid == kReferenceBlock) has_reference = true;
                });
                if (block && !parse_block(block, block_size, false, has_reference, out)) return false;
            }
        } else if (!skip(size)) {  // Cues / Tags / Void / CRC-32 ...
            error_ = "truncated element";
            return false;
        }
    }
}

}  // namespace omega::media
