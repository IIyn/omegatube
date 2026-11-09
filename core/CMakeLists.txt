cmake_minimum_required(VERSION 3.13)
project(omegacore C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(OMEGA_DEPS ${CMAKE_CURRENT_SOURCE_DIR}/../build/deps CACHE PATH "third-party sources (scripts/fetch-deps.sh)")
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# mbedTLS (static, PIC) for HTTPS.
set(ENABLE_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ENABLE_TESTING OFF CACHE BOOL "" FORCE)
set(GEN_FILES OFF CACHE BOOL "" FORCE)
set(MBEDTLS_FATAL_WARNINGS OFF CACHE BOOL "" FORCE)
set(USE_SHARED_MBEDTLS_LIBRARY OFF CACHE BOOL "" FORCE)
set(USE_STATIC_MBEDTLS_LIBRARY ON CACHE BOOL "" FORCE)
add_subdirectory(${OMEGA_DEPS}/mbedtls-3.6.7 mbedtls EXCLUDE_FROM_ALL)

# QuickJS-ng (static, PIC): runs the yt-dlp EJS solver on YouTube's player JS (fallback client).
set(OMEGA_QUICKJS ${OMEGA_DEPS}/quickjs-0.17.0)
add_library(omega_quickjs STATIC ${OMEGA_QUICKJS}/dtoa.c ${OMEGA_QUICKJS}/libregexp.c ${OMEGA_QUICKJS}/libunicode.c
            ${OMEGA_QUICKJS}/quickjs.c)
set_target_properties(omega_quickjs PROPERTIES C_STANDARD 11 C_VISIBILITY_PRESET hidden)
target_compile_definitions(omega_quickjs PRIVATE _GNU_SOURCE)
target_compile_options(omega_quickjs PRIVATE -O2 -w)
target_include_directories(omega_quickjs PUBLIC ${OMEGA_QUICKJS})
target_link_libraries(omega_quickjs PUBLIC m)

# The EJS solver bundles (embedded as C strings).
file(READ ${OMEGA_DEPS}/ejs-0.8.0/lib.min.js OMEGA_EJS_LIB)
file(READ ${OMEGA_DEPS}/ejs-0.8.0/core.min.js OMEGA_EJS_CORE)
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/ejs.cpp
     "extern const char omega_ejs_lib[];\nconst char omega_ejs_lib[] = R\"EJS(${OMEGA_EJS_LIB})EJS\";\n"
     "extern const char omega_ejs_core[];\nconst char omega_ejs_core[] = R\"EJS(${OMEGA_EJS_CORE})EJS\";\n")

# Mozilla CA bundle embedded as a C string.
file(READ ${OMEGA_DEPS}/cacert.pem OMEGA_CACERT)
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/cacert.cpp
     "extern const char omega_cacert_pem[];\nconst char omega_cacert_pem[] = R\"PEM(${OMEGA_CACERT})PEM\";\n")

# libwebp, decoder + demux only (static, PIC): turns YouTube's animated WebP previews into frames, since the
# Tizen 5.5 image loader shows animated WebP as a still. File set = libwebp's makefile.unix DEC/DEMUX/DSP_DEC/
# UTILS_DEC objects (the SIMD variants compile to nothing on targets without that SIMD).
set(OMEGA_WEBP ${OMEGA_DEPS}/libwebp-1.5.0)
set(OMEGA_WEBP_SRCS
    src/dec/alpha_dec.c src/dec/buffer_dec.c src/dec/frame_dec.c src/dec/idec_dec.c src/dec/io_dec.c
    src/dec/quant_dec.c src/dec/tree_dec.c src/dec/vp8_dec.c src/dec/vp8l_dec.c src/dec/webp_dec.c
    src/demux/anim_decode.c src/demux/demux.c
    src/dsp/alpha_processing.c src/dsp/alpha_processing_mips_dsp_r2.c src/dsp/alpha_processing_neon.c
    src/dsp/alpha_processing_sse2.c src/dsp/alpha_processing_sse41.c src/dsp/cpu.c src/dsp/dec.c
    src/dsp/dec_clip_tables.c src/dsp/dec_mips32.c src/dsp/dec_mips_dsp_r2.c src/dsp/dec_msa.c src/dsp/dec_neon.c
    src/dsp/dec_sse2.c src/dsp/dec_sse41.c src/dsp/filters.c src/dsp/filters_mips_dsp_r2.c src/dsp/filters_msa.c
    src/dsp/filters_neon.c src/dsp/filters_sse2.c src/dsp/lossless.c src/dsp/lossless_mips_dsp_r2.c
    src/dsp/lossless_msa.c src/dsp/lossless_neon.c src/dsp/lossless_sse2.c src/dsp/lossless_sse41.c
    src/dsp/rescaler.c src/dsp/rescaler_mips32.c src/dsp/rescaler_mips_dsp_r2.c src/dsp/rescaler_msa.c
    src/dsp/rescaler_neon.c src/dsp/rescaler_sse2.c src/dsp/upsampling.c src/dsp/upsampling_mips_dsp_r2.c
    src/dsp/upsampling_msa.c src/dsp/upsampling_neon.c src/dsp/upsampling_sse2.c src/dsp/upsampling_sse41.c
    src/dsp/yuv.c src/dsp/yuv_mips32.c src/dsp/yuv_mips_dsp_r2.c src/dsp/yuv_neon.c src/dsp/yuv_sse2.c
    src/dsp/yuv_sse41.c
    src/utils/bit_reader_utils.c src/utils/color_cache_utils.c src/utils/filters_utils.c src/utils/huffman_utils.c
    src/utils/palette.c src/utils/quant_levels_dec_utils.c src/utils/random_utils.c src/utils/rescaler_utils.c
    src/utils/thread_utils.c src/utils/utils.c)
list(TRANSFORM OMEGA_WEBP_SRCS PREPEND ${OMEGA_WEBP}/)
add_library(omega_webp STATIC ${OMEGA_WEBP_SRCS})
target_include_directories(omega_webp PUBLIC ${OMEGA_WEBP}/src PRIVATE ${OMEGA_WEBP})
target_compile_options(omega_webp PRIVATE -O2 -w -fvisibility=hidden)

# Single version source (repo VERSION file) and the default remote config, both embedded.
file(STRINGS ${CMAKE_CURRENT_SOURCE_DIR}/../VERSION OMEGA_VERSION LIMIT_COUNT 1)
file(READ ${CMAKE_CURRENT_SOURCE_DIR}/../remote-config.json OMEGA_REMOTE_CONFIG)
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/remote_config_default.cpp
     "extern const char omega_remote_config_default[];\nconst char omega_remote_config_default[] = R\"CFG(${OMEGA_REMOTE_CONFIG})CFG\";\n")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
             ${CMAKE_CURRENT_SOURCE_DIR}/../VERSION ${CMAKE_CURRENT_SOURCE_DIR}/../remote-config.json)
add_compile_definitions(OMEGA_VERSION="${OMEGA_VERSION}")

add_library(omegacore SHARED
    ${CMAKE_CURRENT_BINARY_DIR}/cacert.cpp
    ${CMAKE_CURRENT_BINARY_DIR}/remote_config_default.cpp
    src/util/remote_config.cpp
    ${CMAKE_CURRENT_BINARY_DIR}/ejs.cpp
    src/yt/innertube.cpp
    src/yt/js_solver.cpp
    src/yt/auth.cpp
    src/yt/sponsorblock.cpp
    src/yt/watch.cpp
    ${OMEGA_DEPS}/qrcodegen/qrcodegen.cpp
    src/core.cpp
    src/util/log.cpp
    src/util/glibc_compat.cpp
    src/net/http.cpp
    src/media/demuxer.cpp
    src/media/mp4.cpp
    src/media/webm.cpp
    src/media/webp_anim.cpp
    src/player/esplayer_api.cpp
    src/player/player.cpp
)
target_include_directories(omegacore PUBLIC include PRIVATE src ${OMEGA_DEPS})
# qrcodegen is included as <qrcodegen/qrcodegen.hpp>
target_compile_options(omegacore PRIVATE -Wall -Wextra -O2)
set_target_properties(omegacore PROPERTIES CXX_VISIBILITY_PRESET hidden)

find_package(Threads REQUIRED)
target_link_libraries(omegacore PRIVATE mbedtls mbedx509 mbedcrypto omega_quickjs omega_webp Threads::Threads ${CMAKE_DL_LIBS})

# The TV ships its own libstdc++; link ours statically so we never depend on its version.
target_link_libraries(omegacore PRIVATE -static-libstdc++ -static-libgcc -Wl,--no-undefined)
# Oldest supported firmware (Tizen 5.5) has glibc < 2.25: mbedTLS's explicit_bzero goes to our own copy.
target_link_libraries(omegacore PRIVATE -Wl,--wrap=explicit_bzero)

# Host-side tool to check the demuxer against local files (not shipped).
if(OMEGA_HOST_TOOLS)
    add_executable(mp4dump tools/mp4dump.cpp src/media/demuxer.cpp src/media/mp4.cpp src/media/webm.cpp)
    target_include_directories(mp4dump PRIVATE src)
    # Fetches a YouTube video's formats and downloads ranges with the same code the TV runs.
    add_executable(ytcheck tools/ytcheck.cpp src/media/demuxer.cpp src/media/mp4.cpp src/media/webm.cpp src/yt/innertube.cpp src/yt/js_solver.cpp ${CMAKE_CURRENT_BINARY_DIR}/ejs.cpp src/yt/auth.cpp src/yt/sponsorblock.cpp src/net/http.cpp src/util/log.cpp
                   src/util/remote_config.cpp ${CMAKE_CURRENT_BINARY_DIR}/cacert.cpp
                   ${CMAKE_CURRENT_BINARY_DIR}/remote_config_default.cpp)
    target_include_directories(ytcheck PRIVATE src ${OMEGA_DEPS})
    target_link_libraries(ytcheck PRIVATE mbedtls mbedx509 mbedcrypto omega_quickjs Threads::Threads)
endif()
