#pragma once

#include <string>

namespace omega::media {

// A grid of animation frames packed into one image (row-major, frame 0 top-left).
struct SpriteSheet {
    int cols = 0, rows = 0, frames = 0;
    int frame_w = 0, frame_h = 0;
    int delay_ms = 0;   // per kept frame
    bool none = false;  // not an animation (a still): nothing to play, nothing written
};

// Decodes an animated WebP and writes up to max_frames evenly spaced frames as one 24-bit BMP sprite sheet at
// `path`. The Tizen 5.5 image loader shows animated WebP as a single still, but loads BMP natively, so the app
// plays the animation itself by moving the visible area over this sheet.
// Frames larger than kMaxFrameW x kMaxFrameH are refused (a preview is 320x180): the check reads the header only,
// before libwebp allocates its canvases. Returns false with `error` set on any failure; `out.none` is set for a
// still image. Never throws.
constexpr int kMaxFrameW = 640, kMaxFrameH = 360;

bool webp_to_sheet(const std::string& webp, const std::string& path, int max_frames, SpriteSheet& out,
                   std::string& error);

}  // namespace omega::media
