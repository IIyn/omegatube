# Host test: loading the core under Tizen 5.5 UEP (issue #15)

On Tizen 5.5 and older, Samsung's UEP refuses to map a native `.so` that is not Samsung-signed, even inside a
developer-signed package, so `dlopen("libomegacore.so")` by path fails and every `[DllImport]` throws
`DllNotFoundException`. The app works around this (`app/OmegaTube/NativeCore.cs`) by reading the library's
bytes and `dlopen`-ing an in-memory file descriptor (`memfd_create` + `/proc/self/fd/<n>`): no on-disk file
is mapped, so UEP has nothing to reject.

This test proves the mechanism on any Linux, so the fix can be validated without a Tizen 5.5 TV:

```
bash tests/memfd-load/run.sh
```

It builds a small library from the core's real `omega_version` / `omega_selftest` (so the C++ runtime,
threads and exceptions actually start from the memory image), then loads it both ways the managed resolver
does and checks each returns the version and a passing self-test.

Scope: UEP itself is a TV-only kernel check, so this cannot test UEP. One known risk remains: some Tizen 5.5
firmware builds may also gate the `/proc/self/fd` path. A real TV confirms this in one line of the bug report
(`core load route: memfd` means it worked; `memfd failed: ...` means the custom ELF loader fallback is
needed). Everything up to that kernel check is identical to the app and is covered here.
