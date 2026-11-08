// Proves the UEP work-around from NativeCore.cs (GitHub issue #15) without a Tizen TV.
//
// It loads libomegacore the two ways the managed resolver does:
//   1. "file path"  -> dlopen(path)                       (the route that works on Tizen 6.0+)
//   2. "memfd"      -> dlopen("/proc/self/fd/<n>")        (the route for Tizen 5.5, where UEP refuses a file)
// then calls omega_version() and omega_selftest() through each handle and checks the results. The memfd
// route is the exact sequence NativeCore.LoadFromMemory() runs: memfd_create -> write -> dlopen on the
// descriptor. UEP is a TV-only kernel check, so a real TV still has to confirm the memfd route is accepted
// (one line in the bug report, NativeCore.LoadRoute). Everything else is identical and is tested here.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

typedef const char* (*version_fn)(void);
typedef int (*selftest_fn)(void);

static int check(void* h, const char* route, const char* want_version) {
    if (!h) { fprintf(stderr, "[%s] dlopen failed: %s\n", route, dlerror()); return 1; }
    version_fn ver = (version_fn)dlsym(h, "omega_version");
    selftest_fn st = (selftest_fn)dlsym(h, "omega_selftest");
    if (!ver || !st) { fprintf(stderr, "[%s] dlsym failed: %s\n", route, dlerror()); return 1; }
    const char* v = ver();
    int rc = st();
    printf("[%s] omega_version()=\"%s\"  omega_selftest()=%d\n", route, v ? v : "(null)", rc);
    if (!v || strcmp(v, want_version) != 0) { fprintf(stderr, "[%s] version mismatch (want %s)\n", route, want_version); return 1; }
    if (rc != 0) { fprintf(stderr, "[%s] selftest did not return 0\n", route); return 1; }
    return 0;
}

// Mirrors NativeCore.LoadFromMemory: copy the .so bytes into an anonymous fd and dlopen that fd.
static void* load_from_memfd(const char* path) {
    FILE* f = fopen(path, "rb");
    if (!f) { perror("fopen"); return NULL; }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    char* buf = malloc(n);
    if (fread(buf, 1, n, f) != (size_t)n) { perror("fread"); fclose(f); free(buf); return NULL; }
    fclose(f);
    int fd = memfd_create("omegacore", MFD_CLOEXEC);
    if (fd < 0) { perror("memfd_create"); free(buf); return NULL; }
    long off = 0;
    while (off < n) {
        ssize_t w = write(fd, buf + off, n - off);
        if (w <= 0) { perror("write"); free(buf); close(fd); return NULL; }
        off += w;
    }
    free(buf);
    char fdpath[64];
    snprintf(fdpath, sizeof fdpath, "/proc/self/fd/%d", fd);
    void* h = dlopen(fdpath, RTLD_NOW);  // UEP (TV only) would reject a file here; a memfd has none
    close(fd);
    return h;
}

int main(int argc, char** argv) {
    if (argc < 3) { fprintf(stderr, "usage: %s <libomegacore.so> <expected-version>\n", argv[0]); return 2; }
    const char* path = argv[1];
    const char* want = argv[2];
    int fails = 0;
    fails += check(dlopen(path, RTLD_NOW), "file path", want);   // Tizen 6.0+ route
    fails += check(load_from_memfd(path), "memfd", want);         // Tizen 5.5 route (issue #15)
    if (fails) { fprintf(stderr, "\nFAIL: %d route(s) failed\n", fails); return 1; }
    printf("\nPASS: both load routes resolve the core and run its C++ runtime\n");
    return 0;
}
