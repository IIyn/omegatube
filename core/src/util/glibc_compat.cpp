// Keeps libomegacore.so loadable on older TV firmware (Tizen 5.5 ships glibc < 2.25).
//
// The toolchain's glibc (Debian buster, 2.28) makes mbedTLS call explicit_bzero(), a GLIBC_2.25 symbol: the
// dynamic loader then refuses the whole library on those TVs (GitHub issue #15). The link redirects every
// explicit_bzero reference here (-Wl,--wrap=explicit_bzero, see CMakeLists.txt). scripts/build-core.sh
// rejects any other symbol newer than GLIBC_2.24. This matters for both load routes: the memfd route
// (NativeCore.cs) still relocates against the TV's glibc, so a newer symbol fails there too.

#include <cstring>

extern "C" __attribute__((visibility("hidden"))) void __wrap_explicit_bzero(void *buf, size_t len)
{
    std::memset(buf, 0, len);
    // Tells the compiler the zeroed memory is read afterwards, so the memset is never optimised out.
    __asm__ __volatile__("" : : "r"(buf) : "memory");
}

// --- Self-contained dynamic TLS (GitHub issue #15) -------------------------------------------------------
//
// The static libstdc++ pulls in one thread_local (C++ exception state, __cxa_eh_globals), so the library
// imports __tls_get_addr and carries a general/local-dynamic TLS block. On Tizen 5.5 the core is mapped by a
// hand-rolled in-memory loader (app/OmegaTube/NativeCore.cs) that cannot register a module with the kernel's
// TLS machinery, so glibc's __tls_get_addr (which walks the loader-built dtv) would not work for it. Supplying
// __tls_get_addr from managed code is not an option either: it is called on native threads the core spawns,
// where a reverse P/Invoke into the CLR is unsafe.
//
// So define __tls_get_addr here, hidden, backed by a pthread key. The link binds the library's own TLS access
// to this copy (no import, nothing exported to interpose on other modules), there is a single implementation
// for both load paths, and it runs entirely in native code. The core has exactly one TLS block, so the module
// id is ignored and only ti->offset matters; the block is zero-initialised, which is the correct initial
// value for __cxa_eh_globals.
#include <pthread.h>
#include <stdlib.h>

namespace {
pthread_key_t g_tls_key;
pthread_once_t g_tls_once = PTHREAD_ONCE_INIT;
void omega_tls_free(void *p) { free(p); }
void omega_tls_init() { pthread_key_create(&g_tls_key, omega_tls_free); }
// Generously larger than the module's PT_TLS size (a few bytes); covers any in-block offset.
constexpr size_t kOmegaTlsSize = 256;
}  // namespace

struct omega_tls_index {
    unsigned long module;
    unsigned long offset;
};

extern "C" __attribute__((visibility("hidden"))) void *__tls_get_addr(struct omega_tls_index *ti)
{
    pthread_once(&g_tls_once, omega_tls_init);
    void *block = pthread_getspecific(g_tls_key);
    if (!block) {
        block = calloc(1, kOmegaTlsSize);
        pthread_setspecific(g_tls_key, block);
    }
    return static_cast<char *>(block) + ti->offset;
}
