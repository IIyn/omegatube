#include "media/webp_anim.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <vector>

#include <webp/demux.h>

namespace omega::media {

namespace {

constexpr int kMaxTexture = 4096;   // safe GL texture size on the TVs
constexpr int kSheetWidth = 1920;   // target sheet width (320 px frames -> 6 columns)
constexpr int kMinDelayMs = 40;

void put16(std::vector<uint8_t>& b, size_t at, uint32_t v) {
    b[at] = v & 0xff;
    b[at + 1] = (v >> 8) & 0xff;
}

void put32(std::vector<uint8_t>& b, size_t at, uint32_t v) {
    put16(b, at, v);
    put16(b, at + 2, v >> 16);
}

// libwebp's DSP set-up is lazily initialised global state: decode one preview at a time.
std::mutex decode_mutex;

struct Decoder {
    WebPAnimDecoder* dec = nullptr;
    ~Decoder() {
        if (dec) WebPAnimDecoderDelete(dec);
    }
};

}  // namespace

bool webp_to_sheet(const std::string& webp, const std::string& path, int max_frames, SpriteSheet& out,
                   std::string& error) {
    std::lock_guard<std::mutex> lock(decode_mutex);
    WebPData data;
    data.bytes = reinterpret_cast<const uint8_t*>(webp.data());
    data.size = webp.size();
    WebPAnimDecoderOptions opt;
    if (!WebPAnimDecoderOptionsInit(&opt)) {
        error = "webp: incompatible libwebp";
        return false;
    }
    opt.color_mode = MODE_RGBA;
    opt.use_threads = 0;
    Decoder d;
    d.dec = WebPAnimDecoderNew(&data, &opt);
    if (!d.dec) {
        error = "webp: not a WebP image";
        return false;
    }
    WebPAnimInfo info;
    if (!WebPAnimDecoderGetInfo(d.dec, &info) || info.frame_count == 0 || info.canvas_width == 0 ||
        info.canvas_height == 0) {
        error = "webp: no frames";
        return false;
    }
    const int fw = static_cast<int>(info.canvas_width), fh = static_cast<int>(info.canvas_height);
    const int total = static_cast<int>(info.frame_count);
    if (fw > kMaxTexture || fh > kMaxTexture) {
        error = "webp: frames too large";
        return false;
    }

    // Grid: about kSheetWidth wide, never beyond the texture limit in either direction.
    int n = std::max(1, std::min(total, max_frames));
    const int cols = std::max(1, std::min(n, std::max(1, kSheetWidth / fw)));
    const int max_rows = std::max(1, kMaxTexture / fh);
    n = std::min(n, cols * max_rows);
    const int rows = (n + cols - 1) / cols;
    const int W = cols * fw, H = rows * fh;
    const size_t stride = (static_cast<size_t>(W) * 3 + 3) & ~static_cast<size_t>(3);

    // 24-bit BMP, bottom-up rows; unused cells stay black.
    std::vector<uint8_t> bmp(54 + stride * H, 0);
    bmp[0] = 'B';
    bmp[1] = 'M';
    put32(bmp, 2, static_cast<uint32_t>(bmp.size()));
    put32(bmp, 10, 54);
    put32(bmp, 14, 40);
    put32(bmp, 18, static_cast<uint32_t>(W));
    put32(bmp, 22, static_cast<uint32_t>(H));
    put16(bmp, 26, 1);
    put16(bmp, 28, 24);
    put32(bmp, 34, static_cast<uint32_t>(stride * H));

    // Keep frames floor(k * total / n) for k = 0 .. n-1 (evenly spaced over the whole clip).
    int kept = 0, index = 0, end_ms = 0;
    while (WebPAnimDecoderHasMoreFrames(d.dec)) {
        uint8_t* rgba = nullptr;
        int timestamp = 0;
        if (!WebPAnimDecoderGetNext(d.dec, &rgba, &timestamp)) break;
        end_ms = timestamp;
        if (kept < n && index == static_cast<int>(static_cast<long long>(kept) * total / n)) {
            const int x0 = (kept % cols) * fw, y0 = (kept / cols) * fh;
            for (int y = 0; y < fh; ++y) {
                const uint8_t* src = rgba + static_cast<size_t>(y) * fw * 4;
                uint8_t* dst = bmp.data() + 54 + static_cast<size_t>(H - 1 - (y0 + y)) * stride + x0 * 3;
                for (int x = 0; x < fw; ++x, src += 4, dst += 3) {
                    const unsigned a = src[3];  // composite over black (previews are opaque in practice)
                    dst[0] = static_cast<uint8_t>(src[2] * a / 255);
                    dst[1] = static_cast<uint8_t>(src[1] * a / 255);
                    dst[2] = static_cast<uint8_t>(src[0] * a / 255);
                }
            }
            ++kept;
        }
        ++index;
    }
    if (kept == 0) {
        error = "webp: decode failed";
        return false;
    }

    // Write beside the target, then rename: the app never sees a half-written sheet.
    const std::string tmp = path + ".tmp";
    FILE* f = std::fopen(tmp.c_str(), "wb");
    if (!f) {
        error = "webp: cannot write " + tmp;
        return false;
    }
    const bool written = std::fwrite(bmp.data(), 1, bmp.size(), f) == bmp.size();
    std::fclose(f);
    if (!written || std::rename(tmp.c_str(), path.c_str()) != 0) {
        std::remove(tmp.c_str());
        error = "webp: cannot write " + path;
        return false;
    }

    out.cols = cols;
    out.rows = rows;
    out.frames = kept;
    out.frame_w = fw;
    out.frame_h = fh;
    out.delay_ms = std::max(kMinDelayMs, end_ms > 0 ? end_ms / kept : 100);
    return true;
}

}  // namespace omega::media
