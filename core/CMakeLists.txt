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
    src/net/http.cpp
    src/media/demuxer.cpp
    src/media/mp4.cpp
    src/media/webm.cpp
    src/player/esplayer_api.cpp
    src/player/player.cpp
)
target_include_directories(omegacore PUBLIC include PRIVATE src ${OMEGA_DEPS})
# qrcodegen is included as <qrcodegen/qrcodegen.hpp>
target_compile_options(omegacore PRIVATE -Wall -Wextra -O2)
set_target_properties(omegacore PROPERTIES CXX_VISIBILITY_PRESET hidden)

find_package(Threads REQUIRED)
target_link_libraries(omegacore PRIVATE mbedtls mbedx509 mbedcrypto omega_quickjs Threads::Threads ${CMAKE_DL_LIBS})

# The TV ships its own libstdc++; link ours statically so we never depend on its version.
target_link_libraries(omegacore PRIVATE -static-libstdc++ -static-libgcc -Wl,--no-undefined)

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
