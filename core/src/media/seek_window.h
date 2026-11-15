#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace omega::media {

// What one buffered sample looks like to the seek planner: no payload, just where it is and whether it can start a
// decode. The player builds these from its queues (Track::queue) without copying any bytes.
struct WindowEntry {
    int64_t pts_ms;
    int64_t duration_ms;
    bool key;
};

struct SeekPlan {
    bool from_queue = false;   // the target can be served from what is already demuxed: no network round-trip
    size_t video_from = 0;     // first video sample to keep: the keyframe at or before the target
    size_t audio_from = 0;     // first audio sample to keep: the one covering the target
    const char* why = "";      // why it fell back, for the log
};

// Can this seek be served from the buffered window?
//
// The window is what the demuxer has queued *ahead* of the playhead (samples behind it were submitted and dropped),
// so this is inherently a forward seek. For it to work:
//   - every track in use must still have the target inside its window, and
//   - the video must keep a keyframe at or before the target, because the decoder cannot start anywhere else.
// Taking the keyframe means playback resumes a fraction of a second early and decodes up to the target, which is
// exactly what the network path does too (it starts at the segment holding that keyframe).
inline SeekPlan plan_seek(int64_t target, const std::vector<WindowEntry>& video,
                          const std::vector<WindowEntry>& audio, bool use_video, bool use_audio)
{
    SeekPlan plan;

    if (use_video) {
        if (video.empty()) {
            plan.why = "nothing buffered for video";
            return plan;
        }
        if (video.front().pts_ms > target) {
            plan.why = "target is behind the buffer (forward-only)";
            return plan;
        }
        const WindowEntry& last = video.back();
        if (last.pts_ms + last.duration_ms < target) {
            plan.why = "target is past the buffer";
            return plan;
        }
        size_t anchor = video.size();
        for (size_t i = 0; i < video.size(); ++i) {
            if (video[i].key && video[i].pts_ms <= target) {
                anchor = i;
            }
        }
        if (anchor == video.size()) {
            plan.why = "no keyframe at or before the target";
            return plan;
        }
        plan.video_from = anchor;
    }

    if (use_audio) {
        if (audio.empty()) {
            plan.why = "nothing buffered for audio";
            return plan;
        }
        size_t at = audio.size();
        for (size_t i = 0; i < audio.size(); ++i) {
            if (audio[i].pts_ms + audio[i].duration_ms > target) {
                at = i;
                break;
            }
        }
        if (at == audio.size()) {
            plan.why = "target is past the audio buffer";
            return plan;
        }
        plan.audio_from = at;
    }

    plan.from_queue = true;
    return plan;
}

}  // namespace omega::media
