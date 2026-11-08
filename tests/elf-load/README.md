# Host test: the custom ELF loader for Tizen 5.5 (issue #15)

On Tizen 5.5 and older, Samsung's UEP refuses to make an unsigned library's segments executable when they are
mapped from a file or fd, so `dlopen` (by path or via memfd) fails with *"failed to map segment"*. It does,
however, allow **anonymous** executable memory. OmegaTube's work-around (`app/OmegaTube/ElfLoader.cs`) maps the
core's `PT_LOAD` segments into anonymous memory itself, applies the relocations, resolves imports via `dlsym`,
sets each segment's real permissions, and runs the C++ constructors.

This test runs that exact algorithm (a C mirror, `loader.c`) against the **real cross-built core** under
`qemu-arm`, so the loader is regression-covered without a Tizen TV:

```sh
sudo apt-get install -y gcc-arm-linux-gnueabi qemu-user-static
scripts/fetch-deps.sh && scripts/build-core.sh      # -> app/OmegaTube/lib/libomegacore.so (ARM)
bash tests/elf-load/run.sh
```

It passes when the loader resolves the core and `omega_selftest()` returns 0 — i.e. the relocations, Thumb
interworking, the native `__tls_get_addr` (`core/src/util/glibc_compat.cpp`) and the C++ constructors all work
from an anonymous mapping.

Scope: UEP itself is a TV-only kernel check and cannot be exercised here; a real Tizen 5.5 TV confirms the
final piece (the bug report's `core load route: custom-elf`). Everything up to that check is covered. Soft-float
ARM is used throughout to match the core's ABI.
