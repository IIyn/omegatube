#include "media/webp_anim.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <exception>
#include <mutex>
#include <vector>

#include <webp/decode.h>
#include <webp/demux.h>

namespace omega::media {

namespace {

constexpr int kMaxTexture = 4096;   // safe GL texture size on the TVs
constexpr int kSheetWidth = 1920;   // target sheet width (320 px frames -> 6 columns)
constexpr int kMinDelayMs = 40;
constexpr size_t kMaxSheetBytes = 32u << 20;   // a 320x180 x 36 frame sheet is 6.2 MB; never more than this

void put16(std::vector<uint8_t>& b, size_t at, uint32_t v) {
    b[at] = v & 0xff;
    b[at + 1] = (v >> 8) & 0xff;
}

void put32(std::vector<uint8_t>& b, size_t at, uint32_t v) {
    put16(b, at, v);
    put16(b, at + 2, v >> 16);
}

// libwebp's DSP set-up is lazily initialised global state: decode one preview at a time. Only the decoding is
// serialised; the file write happens outside the lock.
std::mutex decode_mutex;

struct Decoder {
    WebPAnimDecoder* dec = nullptr;
    ~Decoder() {
        if (dec) WebPAnimDecoderDelete(dec);
    }
};

// Frame -> sheet cell: composite over black (previews are opaque in practice, so most pixels take the fast copy)
// and swap RGBA -> BGR for the BMP.
void blit_cell(const uint8_t* rgba, int fw, int fh, uint8_t* bmp_pixels, size_t stride, int H, int x0, int y0) {
    for (int y = 0; y < fh; ++y) {
        const uint8_t* src = rgba + static_cast<size_t>(y) * fw * 4;
        uint8_t* dst = bmp_pixels + static_cast<size_t>(H - 1 - (y0 + y)) * stride + static_cast<size_t>(x0) * 3;
        for (int x = 0; x < fw; ++x, src += 4, dst += 3) {
            const unsigned a = src[3];
            if (a == 255) {
                dst[0] = src[2];
                dst[1] = src[1];
                dst[2] = src[0];
            } else {
                dst[0] = static_cast<uint8_t>((src[2] * a + 127) / 255);
                dst[1] = static_cast<uint8_t>((src[1] * a + 127) / 255);
                dst[2] = static_cast<uint8_t>((src[0] * a + 127) / 255);
            }
        }
    }
}

// Decodes under the lock into `bmp` (a complete BMP file image). False with `error` on failure.
bool decode_sheet(const std::string& webp, int max_frames, std::vector<uint8_t>& bmp, SpriteSheet& out,
                  std::string& error) {
    WebPData data;
    data.bytes = reinterpret_cast<const uint8_t*>(webp.data());
    data.size = webp.size();

    // Header only: refuse oversized canvases before libwebp allocates two canvas-sized buffers for them.
    WebPBitstreamFeatures features;
    if (WebPGetFeatures(data.bytes, data.size, &features) != VP8_STATUS_OK) {
        error = "webp: not a WebP image";
        return false;
    }
    if (features.width <= 0 || features.height <= 0 || features.width > kMaxFrameW || features.height > kMaxFrameH) {
        error = "webp: frames too large";
        return false;
    }
    if (!features.has_animation) {
        out.none = true;
        error = "webp: not animated";
        return false;
    }

    std::lock_guard<std::mutex> lock(decode_mutex);
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
    if (total < 2) {
        out.none = true;
        error = "webp: not animated";
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
    const size_t bytes = 54 + stride * static_cast<size_t>(H);
    if (bytes > kMaxSheetBytes) {
        error = "webp: sheet too large";
        return false;
    }

    // 24-bit BMP, bottom-up rows; unused cells stay black.
    bmp.assign(bytes, 0);
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

    // Keep frames floor(k * total / n) for k = 0 .. n-1 (evenly spaced over the whole clip). Every frame must be
    // decoded (each is composited over the previous), only the kept ones are copied out.
    int kept = 0, index = 0, end_ms = 0;
    while (WebPAnimDecoderHasMoreFrames(d.dec)) {
        uint8_t* rgba = nullptr;
        int timestamp = 0;
        if (!WebPAnimDecoderGetNext(d.dec, &rgba, &timestamp)) break;
        end_ms = timestamp;
        if (kept < n && index == static_cast<int>(static_cast<long long>(kept) * total / n)) {
            blit_cell(rgba, fw, fh, bmp.data() + 54, stride, H, (kept % cols) * fw, (kept / cols) * fh);
            ++kept;
        }
        ++index;
    }
    if (kept == 0 || index < total) {   // nothing decoded, or the stream broke off part-way: no clipped clip
        error = "webp: decode failed";
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

// Write beside the target, then rename: the app never sees a half-written sheet.
bool write_atomically(const std::vector<uint8_t>& bmp, const std::string& path, std::string& error) {
    const std::string tmp = path + ".tmp";
    FILE* f = std::fopen(tmp.c_str(), "wb");
    if (!f) {
        error = "webp: cannot write " + tmp;
        return false;
    }
    const bool written = std::fwrite(bmp.data(), 1, bmp.size(), f) == bmp.size();
    const bool closed = std::fclose(f) == 0;   // buffered write errors (disk full) surface here
    if (!written || !closed || std::rename(tmp.c_str(), path.c_str()) != 0) {
        std::remove(tmp.c_str());
        error = "webp: cannot write " + path;
        return false;
    }
    return true;
}

}  // namespace

bool webp_to_sheet(const std::string& webp, const std::string& path, int max_frames, SpriteSheet& out,
                   std::string& error) {
    out = SpriteSheet{};
    if (path.empty()) {
        error = "webp: no output path";
        return false;
    }
    try {
        std::vector<uint8_t> bmp;
        if (!decode_sheet(webp, max_frames, bmp, out, error)) return false;
        return write_atomically(bmp, path, error);
    } catch (const std::exception& e) {   // bad_alloc on a 32-bit TV, mostly: never let it cross the C ABI
        error = std::string("webp: ") + e.what();
        return false;
    }
}

}  // namespace omega::media
