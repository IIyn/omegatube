#include "player/esplayer_api.h"

#include <dlfcn.h>

#include <mutex>

#include "util/log.h"

namespace omega::esp {
namespace {

Api g_api{};
const Api* g_loaded = nullptr;
std::once_flag g_once;

template <typename T>
bool bind(void* lib, T& fn, const char* name) {
    fn = reinterpret_cast<T>(dlsym(lib, name));
    if (!fn) OLOG("esplusplayer: missing symbol %s", name);
    return fn != nullptr;
}

void load() {
    void* lib = dlopen("libesplusplayer.so", RTLD_NOW | RTLD_LOCAL);
    if (!lib) {
        OLOG("esplusplayer: dlopen failed: %s", dlerror());
        return;
    }
    bool ok = true;
    ok &= bind(lib, g_api.create, "esplusplayer_create");
    ok &= bind(lib, g_api.open, "esplusplayer_open");
    ok &= bind(lib, g_api.close, "esplusplayer_close");
    ok &= bind(lib, g_api.destroy, "esplusplayer_destroy");
    ok &= bind(lib, g_api.prepare_async, "esplusplayer_prepare_async");
    ok &= bind(lib, g_api.start, "esplusplayer_start");
    ok &= bind(lib, g_api.stop, "esplusplayer_stop");
    ok &= bind(lib, g_api.pause, "esplusplayer_pause");
    ok &= bind(lib, g_api.resume, "esplusplayer_resume");
    ok &= bind(lib, g_api.seek, "esplusplayer_seek");
    ok &= bind(lib, g_api.get_state, "esplusplayer_get_state");
    ok &= bind(lib, g_api.set_ecore_display, "esplusplayer_set_ecore_display");
    ok &= bind(lib, g_api.set_display_mode, "esplusplayer_set_display_mode");
    ok &= bind(lib, g_api.set_app_info, "esplusplayer_set_app_info");
    ok &= bind(lib, g_api.set_audio_stream_info, "esplusplayer_set_audio_stream_info");
    ok &= bind(lib, g_api.set_video_stream_info, "esplusplayer_set_video_stream_info");
    ok &= bind(lib, g_api.submit_packet, "esplusplayer_submit_packet");
    ok &= bind(lib, g_api.submit_eos_packet, "esplusplayer_submit_eos_packet");
    ok &= bind(lib, g_api.get_playing_time, "esplusplayer_get_playing_time");
    ok &= bind(lib, g_api.set_error_cb, "esplusplayer_set_error_cb");
    ok &= bind(lib, g_api.set_buffer_status_cb, "esplusplayer_set_buffer_status_cb");
    ok &= bind(lib, g_api.set_eos_cb, "esplusplayer_set_eos_cb");
    ok &= bind(lib, g_api.set_ready_to_prepare_cb, "esplusplayer_set_ready_to_prepare_cb");
    ok &= bind(lib, g_api.set_prepare_async_done_cb, "esplusplayer_set_prepare_async_done_cb");
    ok &= bind(lib, g_api.set_resource_conflicted_cb, "esplusplayer_set_resource_conflicted_cb");
    ok &= bind(lib, g_api.set_ready_to_seek_cb, "esplusplayer_set_ready_to_seek_cb");
    ok &= bind(lib, g_api.set_seek_done_cb, "esplusplayer_set_seek_done_cb");
    ok &= bind(lib, g_api.get_error_string, "esplusplayer_get_error_string");
    if (ok) g_loaded = &g_api;
}

}  // namespace

const Api* api() {
    std::call_once(g_once, load);
    return g_loaded;
}

}  // namespace omega::esp
