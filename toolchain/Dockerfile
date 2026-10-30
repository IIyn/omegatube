# Cross toolchain for Samsung Tizen 6.0 TVs (armv7l, softfp ABI, glibc 2.30).
# Debian buster: glibc 2.28 (< TV) so binaries stay compatible, gcc 8 for C++17.
# Also builds for the x86 (TV 7-9) and x86_64 (TV 10) emulators.
FROM debian:buster

RUN sed -i 's|deb.debian.org|archive.debian.org|g; s|security.debian.org|archive.debian.org|g; /buster-updates/d' /etc/apt/sources.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends g++-arm-linux-gnueabi g++-i686-linux-gnu g++ cmake make file \
 && rm -rf /var/lib/apt/lists/*
