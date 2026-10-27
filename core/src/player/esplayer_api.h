#pragma once

// Our own declarations of the subset of Samsung's esplusplayer C API we use, resolved at runtime
// from the TV's /usr/lib/libesplusplayer.so (we do not ship or link against Samsung headers/libs).

#include <cstdint>

namespace omega::esp {

using Handle = void*;

enum StreamType : int { STREAM_AUDIO = 0, STREAM_VIDEO = 1 };
enum DisplayType : int { DISPLAY_NONE = 0, DISPLAY_OVERLAY = 1 };
enum DisplayMode : int { DISPLAY_MODE_LETTER_BOX = 0, DISPLAY_MODE_FULL_SCREEN = 2 };
enum AudioMime : int { AUDIO_AAC = 1 };
enum VideoMime : int { VIDEO_H264 = 2 };
enum BufferStatus : int { BUFFER_UNDERRUN = 0, BUFFER_OVERRUN = 1 };
enum State : int { STATE_NONE, STATE_IDLE, STATE_READY, STATE_PLAYING, STATE_PAUSED };
enum SubmitStatus : int {
    SUBMIT_NOT_PREPARED = 0,
    SUBMIT_INVALID_PACKET = 1,
    SUBMIT_OUT_OF_MEMORY = 2,
    SUBMIT_FULL = 3,
    SUBMIT_SUCCESS = 4,
};

struct EsPacket {
    StreamType type;
    char* buffer;
    uint32_t buffer_size;
    uint64_t pts;       // ms
    uint64_t duration;  // ms
    void* matroska_color_info;
    uint32_t hdr10p_metadata_size;
    char* hdr10p_metadata;
    // Headroom in case the TV build's struct is larger than the public one; always zeroed.
    uint8_t reserved[64];
};

struct AudioStreamInfo {
    char* codec_data;
    uint32_t codec_data_length;
    AudioMime mime_type;
    uint32_t bitrate;
    uint32_t channels;
    uint32_t sample_rate;
    uint8_t reserved[64];
};

struct VideoStreamInfo {
    char* codec_data;
    uint32_t codec_data_length;
    VideoMime mime_type;
    uint32_t width;
    uint32_t height;
    uint32_t max_width;
    uint32_t max_height;
    uint32_t framerate_num;
    uint32_t framerate_den;
    uint8_t reserved[64];
};

// TV-only. The TV build reports sizeof == 12 (get_size_of_esplusplayer_app_info).
struct AppInfo {
    char* id;
    char* version;
    char* type;
};

using ErrorCb = void (*)(int error, void* user);
using BufferStatusCb = void (*)(StreamType, BufferStatus, void* user);
using EosCb = void (*)(void* user);
using ReadyToPrepareCb = void (*)(StreamType, void* user);
using PrepareDoneCb = void (*)(bool ok, void* user);
using ResourceConflictedCb = void (*)(void* user);
using ReadyToSeekCb = void (*)(StreamType, uint64_t time_ms, void* user);
using SeekDoneCb = void (*)(void* user);

struct Api {
    Handle (*create)();
    int (*open)(Handle);
    int (*close)(Handle);
    int (*destroy)(Handle);
    int (*prepare_async)(Handle);
    int (*start)(Handle);
    int (*stop)(Handle);
    int (*pause)(Handle);
    int (*resume)(Handle);
    int (*seek)(Handle, uint64_t time_ms);
    int (*set_playback_rate)(Handle, double rate, bool audio_mute);
    int (*get_state)(Handle);
    int (*set_ecore_display)(Handle, DisplayType, void* window, int x, int y, int w, int h);
    int (*set_display_mode)(Handle, DisplayMode);
    int (*set_app_info)(Handle, const AppInfo*);
    int (*set_audio_stream_info)(Handle, AudioStreamInfo*);
    int (*set_video_stream_info)(Handle, VideoStreamInfo*);
    int (*submit_packet)(Handle, EsPacket*);
    int (*submit_eos_packet)(Handle, StreamType);
    int (*get_playing_time)(Handle, uint64_t* ms);
    int (*set_error_cb)(Handle, ErrorCb, void*);
    int (*set_buffer_status_cb)(Handle, BufferStatusCb, void*);
    int (*set_eos_cb)(Handle, EosCb, void*);
    int (*set_ready_to_prepare_cb)(Handle, ReadyToPrepareCb, void*);
    int (*set_prepare_async_done_cb)(Handle, PrepareDoneCb, void*);
    int (*set_resource_conflicted_cb)(Handle, ResourceConflictedCb, void*);
    int (*set_ready_to_seek_cb)(Handle, ReadyToSeekCb, void*);
    int (*set_seek_done_cb)(Handle, SeekDoneCb, void*);
    const char* (*get_error_string)(int);
};

// Loads libesplusplayer.so once. Returns nullptr (and logs) if the library or a symbol is missing.
const Api* api();

}  // namespace omega::esp
