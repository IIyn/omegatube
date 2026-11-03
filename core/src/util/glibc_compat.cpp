// Keeps libomegacore.so loadable on older TV firmware (Tizen 5.5 ships glibc < 2.25).
//
// The toolchain's glibc (Debian buster, 2.28) makes mbedTLS call explicit_bzero(), a GLIBC_2.25 symbol: the
// dynamic loader then refuses the whole library on those TVs (GitHub issue #15). The link redirects every
// explicit_bzero reference here (-Wl,--wrap=explicit_bzero, see CMakeLists.txt). scripts/build-core.sh
// rejects any other symbol newer than GLIBC_2.24.

#include <cstring>

extern "C" __attribute__((visibility("hidden"))) void __wrap_explicit_bzero(void *buf, size_t len)
{
    std::memset(buf, 0, len);
    // Tells the compiler the zeroed memory is read afterwards, so the memset is never optimised out.
    __asm__ __volatile__("" : : "r"(buf) : "memory");
}
