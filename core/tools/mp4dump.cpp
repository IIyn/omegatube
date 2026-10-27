// Host tool: runs the demuxer (fMP4 or WebM, sniffed) over a local file and prints track info + sample timeline stats.
#include <cstdio>
#include <cstdlib>

#include "media/demuxer.h"

int main(int argc, char** argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: mp4dump file.mp4|file.webm [seek_ms]\n");
        return 2;
    }
    FILE* f = fopen(argv[1], "rb");
    if (!f) return 1;
    auto header = omega::media::open_demuxer([f](void* dst, size_t n) { return fread(dst, 1, n, f); });
    if (!header->read_header()) {
        fprintf(stderr, "header: %s\n", header->error().c_str());
        return 1;
    }
    const auto t = header->track();
    const auto segs = header->segments();
    printf("%s codec=%s timescale=%u %ux%u ch=%u rate=%u codec_data=%zu media_time=%lld\n", t.container == omega::media::Container::WebM ? "webm" : "mp4", omega::media::codec_name(t.codec),
           t.timescale, t.width, t.height, t.channels, t.sample_rate, t.codec_data.size(),
           static_cast<long long>(t.media_time));
    printf("index: %zu segments", segs.size());
    for (size_t i = 0; i < segs.size() && i < 3; ++i)
        printf("  [%zu] @%llu %lldms", i, static_cast<unsigned long long>(segs[i].offset),
               static_cast<long long>(segs[i].start_ms));
    printf("\n");
    // Optional seek: resume at the last segment at or before seek_ms, like Player::seek does.
    std::unique_ptr<omega::media::Demuxer> resumed;
    if (argc > 2 && !segs.empty()) {
        long long target = atoll(argv[2]);
        omega::media::Segment seg = segs.front();
        for (const auto& s : segs)
            if (s.start_ms <= target) seg = s;
        fseek(f, static_cast<long>(seg.offset), SEEK_SET);
        printf("resume at segment @%llu %lldms\n", static_cast<unsigned long long>(seg.offset),
               static_cast<long long>(seg.start_ms));
        resumed = omega::media::resume_demuxer([f](void* dst, size_t n) { return fread(dst, 1, n, f); }, t, seg);
    }
    omega::media::Demuxer& reader = resumed ? *resumed : *header;
    std::deque<omega::media::Sample> samples;
    size_t fragments = 0, count = 0, keys = 0;
    long long first_pts = -1, last_pts = 0, total_dur = 0;
    while (reader.read_fragment(samples)) {
        ++fragments;
        for (auto& s : samples) {
            if (first_pts < 0) {
                first_pts = s.pts_ms;
                printf("first sample: pts=%lld dur=%lld size=%zu key=%d\n", static_cast<long long>(s.pts_ms),
                       static_cast<long long>(s.duration_ms), s.data.size(), s.key);
            }
            last_pts = s.pts_ms;
            total_dur += s.duration_ms;
            keys += s.key;
            ++count;
        }
        samples.clear();
    }
    printf("fragments=%zu samples=%zu keys=%zu first_pts=%lld last_pts=%lld sum_dur=%lld err='%s'\n", fragments, count,
           keys, first_pts, last_pts, total_dur, reader.error().c_str());
    return 0;
}
