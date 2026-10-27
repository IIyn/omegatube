#include "media/demuxer.h"

#include <cstring>

#include "media/mp4.h"
#include "media/webm.h"

namespace omega::media {

const char* codec_name(Codec c) {
    switch (c) {
        case Codec::H264: return "H.264";
        case Codec::AAC: return "AAC";
        case Codec::VP9: return "VP9";
        case Codec::AV1: return "AV1";
        default: return "unknown";
    }
}

std::unique_ptr<Demuxer> open_demuxer(Demuxer::ReadFn read) {
    // Peek at the first 4 bytes (EBML magic vs an MP4 box size), then replay them to the reader.
    auto head = std::make_shared<std::vector<uint8_t>>(4);
    head->resize(read(head->data(), head->size()));
    bool webm = head->size() == 4 && (*head)[0] == 0x1A && (*head)[1] == 0x45 && (*head)[2] == 0xDF &&
                (*head)[3] == 0xA3;
    auto replay = [head, pos = size_t(0), read = std::move(read)](void* dst, size_t n) mutable {
        size_t from_head = std::min(n, head->size() - pos);
        memcpy(dst, head->data() + pos, from_head);
        pos += from_head;
        return from_head == n ? n : from_head + read(static_cast<uint8_t*>(dst) + from_head, n - from_head);
    };
    if (webm) return std::make_unique<WebmReader>(std::move(replay));
    return std::make_unique<Fmp4Reader>(std::move(replay));
}

std::unique_ptr<Demuxer> resume_demuxer(Demuxer::ReadFn read, const TrackInfo& track, const Segment& segment) {
    if (track.container == Container::WebM) return std::make_unique<WebmReader>(std::move(read), track, segment);
    return std::make_unique<Fmp4Reader>(std::move(read), track, segment);
}

}  // namespace omega::media
