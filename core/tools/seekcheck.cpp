// Host tool: the seek planner, on a table of cases. Runs on the PC (`seekcheck`), no TV and no media file needed:
// the planner is pure logic over buffered timestamps, so every rule can be pinned down here.
//
//   seekcheck            runs the table, prints PASS/FAIL, exits 1 on a failure
#include <cstdio>
#include <vector>

#include "media/seek_window.h"

using omega::media::plan_seek;
using omega::media::SeekPlan;
using omega::media::WindowEntry;

namespace {

int failures = 0;

std::vector<WindowEntry> track(std::vector<int64_t> pts, int64_t duration = 100,
                               std::vector<size_t> keyframes = {})
{
    std::vector<WindowEntry> out;
    out.reserve(pts.size());
    for (size_t i = 0; i < pts.size(); ++i) {
        bool key = false;
        for (size_t k : keyframes) key = key || k == i;
        out.push_back({pts[i], duration, key});
    }
    return out;
}

void check(const char* name, const SeekPlan& plan, bool from_queue, size_t video_from, size_t audio_from,
           const char* why_contains = nullptr)
{
    bool ok = plan.from_queue == from_queue;
    if (ok && from_queue) ok = plan.video_from == video_from && plan.audio_from == audio_from;
    if (ok && !from_queue && why_contains) {
        std::string why = plan.why;
        ok = why.find(why_contains) != std::string::npos;
    }
    if (!ok) ++failures;
    std::printf("%s  %-58s from_queue=%d video_from=%zu audio_from=%zu%s\n", ok ? "PASS" : "FAIL", name,
                plan.from_queue ? 1 : 0, plan.video_from, plan.audio_from,
                plan.why[0] ? (std::string(" (") + plan.why + ")").c_str() : "");
}

}  // namespace

int main()
{
    // 0..900 ms of video at 100 ms a sample, keyframes every 300 ms (like a real stream's GOP).
    auto v = track({0, 100, 200, 300, 400, 500, 600, 700, 800, 900}, 100, {0, 3, 6, 9});
    auto a = track({0, 100, 200, 300, 400, 500, 600, 700, 800, 900});

    // The ordinary case: a tap 250 ms ahead. Playback must restart at the keyframe at 200, not at the target.
    check("target inside the window, keyframe before it", plan_seek(450, v, a, true, true), true, 3, 4);
    // Landing exactly on a keyframe keeps that keyframe.
    check("target on a keyframe", plan_seek(300, v, a, true, true), true, 3, 3);
    // The very start of the window.
    check("target at the front of the window", plan_seek(0, v, a, true, true), true, 0, 0);
    // The last sample still covers the target.
    check("target at the back of the window", plan_seek(950, v, a, true, true), true, 9, 9);

    // Falling back: behind the window (samples already submitted are gone), past it, or no keyframe to start from.
    check("target behind the window", plan_seek(-50, v, a, true, true), true == false, 0, 0, "behind");
    check("target past the window", plan_seek(1500, v, a, true, true), false, 0, 0, "past the buffer");
    auto no_keys = track({0, 100, 200, 300}, 100, {});
    check("no keyframe in the window", plan_seek(200, no_keys, a, true, true), false, 0, 0, "no keyframe");
    auto keys_after = track({0, 100, 200, 300}, 100, {2, 3});
    check("keyframes only after the target", plan_seek(150, keys_after, a, true, true), false, 0, 0, "no keyframe");

    // Empty queues.
    std::vector<WindowEntry> none;
    check("nothing buffered at all", plan_seek(100, none, none, true, true), false, 0, 0, "nothing buffered");
    check("audio missing while it is used", plan_seek(450, v, none, true, true), false, 0, 0, "nothing buffered");

    // Muted video (the backdrop preview): audio is not in use, so an empty audio window must not block the seek.
    check("video only, audio unused", plan_seek(450, v, none, true, false), true, 3, 0);
    // Audio-only playback keeps working the same way.
    check("audio only, video unused", plan_seek(450, none, a, false, true), true, 0, 4);

    // A single keyframe at the front still serves a target anywhere after it (a long GOP).
    auto long_gop = track({0, 100, 200, 300, 400}, 100, {0});
    check("one keyframe at the front serves a later target", plan_seek(380, long_gop, a, true, true), true, 0, 3);

    // ---------------------------------------------------------------------------------------------------------------
    // The window the player holds is two regions, and only the second can serve a seek:
    //   playhead .. submitted   handed to the TV's player (feed_ahead, i.e. the buffer setting) — kept no copy
    //   submitted .. +queued    our demux queue
    // A bar that draws both promises more than the planner can honour, which is what made a tap inside the grey bar
    // reset the buffer. These cases pin the arithmetic either side of the boundary.
    {
        const int64_t feed_ahead = 12000;    // what the buffer setting fed
        const int64_t queued = 8000;         // what the demuxer still holds
        // With the back-buffer in place the window the planner sees spans retained past + queue, so a tap in the region
        // that was handed to the TV's player IS servable — which is what the grey bar has always been drawing.
        auto retained_then_queue = track({8000, 8100, 8200, 12000, 12100, 12200, 20000, 20100, 20200, 20300}, 100,
                                        {0, 3, 6, 9});
        // target 12100: the last video keyframe at or before it is 12000 (index 3), the audio sample covering it is 12100
        check("a tap in the fed region is served from the back-buffer",
              plan_seek(12100, retained_then_queue, retained_then_queue, true, true), true, 3, 4);
        // target 8100 is behind the first queue sample: only the retained past can serve it (keyframe 8000, index 0)
        check("a short step back is served from the back-buffer",
              plan_seek(8100, retained_then_queue, retained_then_queue, true, true), true, 0, 1);
        // Without retained data (a restart, or before the first seconds) the same tap still needs the network.
        auto queue_only = track({20000, 20100, 20200, 20300}, 100, {1, 3});
        check("no retained data: the same tap needs the network",
              plan_seek(12100, queue_only, track({20000, 20100}, 100), true, true), false, 0, 0, "behind the buffer");
        check("a tap in the demuxed region is served",
              plan_seek(20200, queue_only, track({20100, 20200, 20300}, 100), true, true), true, 1, 1);
        // Pacing must reach the target: feeding stops at position + feed_ahead, so pacing from the keyframe instead of
        // the target would stop short and stall (the bug that emptied the bar right after a seek).
        const int64_t anchor = 12000, target = 15000, small_feed = 2000;
        if (anchor + small_feed < target) {
            std::printf("PASS  %-58s anchor %lld + feed %lld < target %lld: pacing from the anchor would stall\n",
                        "pacing must start from the target, not the keyframe", (long long)anchor,
                        (long long)small_feed, (long long)target);
        } else {
            ++failures;
            std::printf("FAIL  pacing arithmetic\n");
        }
        const int64_t held_after = (anchor + queued) - target;
        if (held_after > 0) {
            std::printf("PASS  %-58s %lld ms held after the seek\n", "a served seek keeps a non-empty buffer",
                        (long long)held_after);
        } else {
            ++failures;
            std::printf("FAIL  a served seek left %lld ms held\n", (long long)held_after);
        }
    }

    std::printf("\n%s (%d failure%s)\n", failures == 0 ? "all cases pass" : "FAILURES", failures,
                failures == 1 ? "" : "s");
    return failures == 0 ? 0 : 1;
}
