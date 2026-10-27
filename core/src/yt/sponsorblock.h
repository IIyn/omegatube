#pragma once

#include <string>
#include <vector>

namespace omega::yt {

struct SkipSegment {
    std::string category;  // sponsor, selfpromo, interaction, intro, outro, preview, music_offtopic, filler
    double start_s = 0, end_s = 0;
};

// SponsorBlock (sponsor.ajay.app). Privacy-preserving lookup: only the first 4 hex chars of the
// video id's SHA-256 are sent; matching videos are filtered locally.
bool sponsor_segments(const std::string& video_id, std::vector<SkipSegment>& out, std::string& error);

}  // namespace omega::yt
