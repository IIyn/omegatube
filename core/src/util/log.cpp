#include "util/log.h"

#include <arpa/inet.h>
#include <netdb.h>
#include <sys/socket.h>
#include <unistd.h>

#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>

namespace omega::log {
namespace {

constexpr size_t kRingSize = 200;

std::mutex g_mutex;
std::deque<std::string> g_ring;
int g_sock = -1;
sockaddr_in g_sink{};
const auto g_start = std::chrono::steady_clock::now();

}  // namespace

void init_udp(const std::string& host, int port) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_sock >= 0) close(g_sock);
    g_sock = socket(AF_INET, SOCK_DGRAM, 0);
    g_sink = {};
    g_sink.sin_family = AF_INET;
    g_sink.sin_port = htons(static_cast<uint16_t>(port));
    if (inet_pton(AF_INET, host.c_str(), &g_sink.sin_addr) != 1) {
        close(g_sock);
        g_sock = -1;
    }
}

void write(const char* fmt, ...) {
    char msg[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);

    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - g_start).count();
    char line[1100];
    int n = snprintf(line, sizeof(line), "[%7.3f] %s", ms / 1000.0, msg);

    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_sock >= 0)
        sendto(g_sock, line, static_cast<size_t>(n), MSG_DONTWAIT, reinterpret_cast<sockaddr*>(&g_sink), sizeof(g_sink));
    g_ring.emplace_back(line);
    if (g_ring.size() > kRingSize) g_ring.pop_front();
}

std::vector<std::string> tail(size_t max_lines) {
    std::lock_guard<std::mutex> lock(g_mutex);
    size_t start = g_ring.size() > max_lines ? g_ring.size() - max_lines : 0;
    return {g_ring.begin() + static_cast<long>(start), g_ring.end()};
}

}  // namespace omega::log
