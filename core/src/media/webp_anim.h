#pragma once

#include <string>

namespace omega::media {

// A grid of animation frames packed into one image (row-major, frame 0 top-left).
struct SpriteSheet {
    int cols = 0, rows = 0, frames = 0;
    int frame_w = 0, frame_h = 0;
    int delay_ms = 0;  // per kept frame
};

// Decodes an animated (or still) WebP and writes up to max_frames evenly spaced frames as one 24-bit BMP sprite
// sheet at `path`. The Tizen 5.5 image loader shows animated WebP as a single still, but loads BMP natively, so
// the app plays the animation itself by moving the visible area over this sheet.
bool webp_to_sheet(const std::string& webp, const std::string& path, int max_frames, SpriteSheet& out,
                   std::string& error);

}  // namespace omega::media
