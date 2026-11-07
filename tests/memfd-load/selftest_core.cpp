// A minimal stand-in for libomegacore.so used only by the host load test (tests/memfd-load).
// omega_version() and omega_selftest() are copied verbatim from core/src/core.cpp, so the test exercises
// the real C++ runtime start-up (std::thread, exceptions, std::string) from a memory image. The full core
// is cross-compiled for the TV by scripts/build-core.sh; this file is never shipped.
#include <stdexcept>
#include <string>
#include <thread>

#ifndef OMEGA_VERSION
#define OMEGA_VERSION "0.0.0-test"
#endif

namespace {
constexpr const char* kVersion = OMEGA_VERSION;
}

extern "C" __attribute__((visibility("default"))) const char* omega_version(void) { return kVersion; }

extern "C" __attribute__((visibility("default"))) int omega_selftest(void) {
    int result = 1;
    std::thread worker([&result] {
        try {
            std::string s = "omega";
            s += "tube";
            if (s.size() != 9) throw std::runtime_error("string");
            result = 0;
        } catch (...) {
            result = 2;
        }
    });
    worker.join();
    return result;
}
