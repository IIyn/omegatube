#pragma once

#include <string>
#include <vector>

namespace omega::log {

// Sends every line to a UDP sink (dev PC) in addition to the in-memory ring.
void init_udp(const std::string& host, int port);
// Also writes every line to `path` (bug reports). The previous session's file is kept as `path`.prev.
void init_file(const std::string& path);
void write(const char* fmt, ...) __attribute__((format(printf, 1, 2)));
std::vector<std::string> tail(size_t max_lines);

}  // namespace omega::log

#define OLOG(...) ::omega::log::write(__VA_ARGS__)
