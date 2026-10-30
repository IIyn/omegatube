#include "net/http.h"

#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <unistd.h>

#include <mbedtls/ctr_drbg.h>
#include <mbedtls/entropy.h>
#include <mbedtls/error.h>
#include <mbedtls/net_sockets.h>
#include <mbedtls/ssl.h>
#include <mbedtls/x509_crt.h>
#include <psa/crypto.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstring>
#include <mutex>
#include <thread>

#include "util/log.h"

extern const char omega_cacert_pem[];  // generated from the Mozilla CA bundle (see CMakeLists.txt)

namespace omega::net {
namespace {

bool iequals(const std::string& a, const std::string& b) {
    return a.size() == b.size() &&
           std::equal(a.begin(), a.end(), b.begin(), [](char x, char y) { return tolower(x) == tolower(y); });
}

bool is_redirect(int status) {
    return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
}

std::string tls_error(int rc) {
    char buf[128];
    mbedtls_strerror(rc, buf, sizeof(buf));
    return buf;
}

// Process-wide TLS configuration: CA chain, RNG and client defaults are shared by all connections.
struct TlsContext {
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    mbedtls_x509_crt ca;
    mbedtls_ssl_config conf;
    bool ok = false;

    TlsContext() {
        psa_crypto_init();
        mbedtls_entropy_init(&entropy);
        mbedtls_ctr_drbg_init(&drbg);
        mbedtls_x509_crt_init(&ca);
        mbedtls_ssl_config_init(&conf);
        const char* pers = "omegatube";
        int rc = mbedtls_ctr_drbg_seed(&drbg, mbedtls_entropy_func, &entropy,
                                       reinterpret_cast<const unsigned char*>(pers), strlen(pers));
        if (rc != 0) {
            OLOG("tls: drbg seed failed: %s", tls_error(rc).c_str());
            return;
        }
        rc = mbedtls_x509_crt_parse(&ca, reinterpret_cast<const unsigned char*>(omega_cacert_pem),
                                    strlen(omega_cacert_pem) + 1);
        if (rc < 0) {
            OLOG("tls: CA bundle parse failed: %s", tls_error(rc).c_str());
            return;
        }
        if (rc > 0) OLOG("tls: %d CA certificates skipped", rc);
        mbedtls_ssl_config_defaults(&conf, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM,
                                    MBEDTLS_SSL_PRESET_DEFAULT);
        mbedtls_ssl_conf_authmode(&conf, MBEDTLS_SSL_VERIFY_REQUIRED);
        mbedtls_ssl_conf_ca_chain(&conf, &ca, nullptr);
        mbedtls_ssl_conf_rng(&conf, mbedtls_ctr_drbg_random, &drbg);
        ok = true;
    }
};

TlsContext& tls() {
    static TlsContext ctx;
    return ctx;
}

int connect_tcp(const std::string& host, const std::string& port, std::string& error) {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* res = nullptr;
    if (int rc = getaddrinfo(host.c_str(), port.c_str(), &hints, &res); rc != 0) {
        error = std::string("dns ") + host + ": " + gai_strerror(rc);
        return -1;
    }
    int fd = -1;
    for (addrinfo* ai = res; ai; ai = ai->ai_next) {
        fd = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (fd < 0) continue;
        timeval tv{15, 0};
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
        int rcvbuf = 2 * 1024 * 1024;
        setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &rcvbuf, sizeof(rcvbuf));
        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));
        if (connect(fd, ai->ai_addr, ai->ai_addrlen) == 0) break;
        error = std::string("connect ") + host + ": " + strerror(errno);
        ::close(fd);
        fd = -1;
    }
    freeaddrinfo(res);
    return fd;
}

}  // namespace

class Transport {
public:
    virtual ~Transport() = default;
    virtual long send(const void* data, size_t n) = 0;  // <0 on error
    virtual long recv(void* data, size_t n) = 0;        // 0 on EOF, <0 on error
};

namespace {

class PlainTransport : public Transport {
public:
    explicit PlainTransport(int fd) : fd_(fd) {}
    ~PlainTransport() override { ::close(fd_); }
    long send(const void* data, size_t n) override { return ::send(fd_, data, n, MSG_NOSIGNAL); }
    long recv(void* data, size_t n) override {
        for (;;) {
            ssize_t r = ::recv(fd_, data, n, 0);
            if (r < 0 && errno == EINTR) continue;
            return r;
        }
    }

private:
    int fd_;
};

class TlsTransport : public Transport {
public:
    explicit TlsTransport(int fd) : fd_(fd) { mbedtls_ssl_init(&ssl_); }
    ~TlsTransport() override {
        mbedtls_ssl_free(&ssl_);
        ::close(fd_);
    }

    bool handshake(const std::string& host, std::string& error) {
        TlsContext& ctx = tls();
        if (!ctx.ok) {
            error = "tls unavailable";
            return false;
        }
        int rc = mbedtls_ssl_setup(&ssl_, &ctx.conf);
        if (rc == 0) rc = mbedtls_ssl_set_hostname(&ssl_, host.c_str());
        if (rc != 0) {
            error = "tls setup: " + tls_error(rc);
            return false;
        }
        mbedtls_ssl_set_bio(&ssl_, this, &TlsTransport::bio_send, &TlsTransport::bio_recv, nullptr);
        while ((rc = mbedtls_ssl_handshake(&ssl_)) != 0) {
            if (rc == MBEDTLS_ERR_SSL_WANT_READ || rc == MBEDTLS_ERR_SSL_WANT_WRITE) continue;
            error = "tls handshake " + host + ": " + tls_error(rc);
            uint32_t flags = mbedtls_ssl_get_verify_result(&ssl_);
            if (flags != 0) {
                char info[256];
                mbedtls_x509_crt_verify_info(info, sizeof(info), "", flags);
                error += std::string(" (") + info + ")";
            }
            return false;
        }
        return true;
    }

    long send(const void* data, size_t n) override {
        size_t sent = 0;
        while (sent < n) {
            int rc = mbedtls_ssl_write(&ssl_, static_cast<const unsigned char*>(data) + sent, n - sent);
            if (rc == MBEDTLS_ERR_SSL_WANT_READ || rc == MBEDTLS_ERR_SSL_WANT_WRITE) continue;
            if (rc < 0) return rc;
            sent += static_cast<size_t>(rc);
        }
        return static_cast<long>(sent);
    }

    long recv(void* data, size_t n) override {
        for (;;) {
            int rc = mbedtls_ssl_read(&ssl_, static_cast<unsigned char*>(data), n);
            if (rc == MBEDTLS_ERR_SSL_WANT_READ || rc == MBEDTLS_ERR_SSL_WANT_WRITE) continue;
            // TLS 1.3 servers send session tickets after the handshake; they are not application data.
            if (rc == MBEDTLS_ERR_SSL_RECEIVED_NEW_SESSION_TICKET) continue;
            if (rc == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) return 0;
            return rc;
        }
    }

private:
    static int bio_send(void* self, const unsigned char* buf, size_t len) {
        ssize_t r = ::send(static_cast<TlsTransport*>(self)->fd_, buf, len, MSG_NOSIGNAL);
        if (r < 0) return (errno == EINTR) ? MBEDTLS_ERR_SSL_WANT_WRITE : MBEDTLS_ERR_NET_SEND_FAILED;
        return static_cast<int>(r);
    }
    static int bio_recv(void* self, unsigned char* buf, size_t len) {
        ssize_t r = ::recv(static_cast<TlsTransport*>(self)->fd_, buf, len, 0);
        if (r < 0) return (errno == EINTR) ? MBEDTLS_ERR_SSL_WANT_READ : MBEDTLS_ERR_NET_RECV_FAILED;
        return static_cast<int>(r);
    }

    int fd_;
    mbedtls_ssl_context ssl_;
};

}  // namespace

bool Url::parse(const std::string& url, Url& out) {
    size_t at;
    if (url.compare(0, 8, "https://") == 0) {
        out.tls = true;
        at = 8;
    } else if (url.compare(0, 7, "http://") == 0) {
        out.tls = false;
        at = 7;
    } else {
        return false;
    }
    size_t slash = url.find_first_of("/?", at);
    std::string authority = url.substr(at, slash == std::string::npos ? std::string::npos : slash - at);
    out.path = slash == std::string::npos ? "/" : url.substr(slash);
    if (out.path[0] == '?') out.path = "/" + out.path;
    size_t colon = authority.rfind(':');
    out.host = authority.substr(0, colon);
    out.port = colon == std::string::npos ? (out.tls ? "443" : "80") : authority.substr(colon + 1);
    return !out.host.empty();
}

Connection::Connection() : buf_(64 * 1024) {}
Connection::~Connection() = default;

void Connection::close() {
    transport_.reset();
    reusable_ = false;
    host_key_.clear();
    buf_pos_ = buf_len_ = 0;
}

bool Connection::connect_to(const Url& url) {
    close();
    int fd = connect_tcp(url.host, url.port, error_);
    if (fd < 0) return false;
    if (url.tls) {
        auto t = std::make_unique<TlsTransport>(fd);
        if (!t->handshake(url.host, error_)) return false;
        transport_ = std::move(t);
    } else {
        transport_ = std::make_unique<PlainTransport>(fd);
    }
    host_key_ = (url.tls ? "s:" : "p:") + url.host + ":" + url.port;
    return true;
}

bool Connection::send(const Request& req) {
    error_.clear();
    Url url;
    if (!Url::parse(req.url, url)) {
        error_ = "bad url";
        return false;
    }
    std::string key = (url.tls ? "s:" : "p:") + url.host + ":" + url.port;
    if (!body_done_) close();  // previous body not consumed: cannot reuse

    std::string msg = req.method + " " + url.path + " HTTP/1.1\r\nHost: " + url.host + "\r\n";
    bool has_ua = false;
    for (const auto& h : req.headers) {
        msg += h.first + ": " + h.second + "\r\n";
        has_ua |= iequals(h.first, "User-Agent");
    }
    if (!has_ua) msg += "User-Agent: OmegaTube\r\n";
    if (!req.body.empty() || req.method == "POST") msg += "Content-Length: " + std::to_string(req.body.size()) + "\r\n";
    msg += "Connection: keep-alive\r\n\r\n";
    msg += req.body;

    // A kept-alive socket may have been closed by the server meanwhile: retry once on a fresh one.
    for (int attempt = 0; attempt < 2; ++attempt) {
        bool fresh = false;
        if (!transport_ || !reusable_ || key != host_key_) {
            if (!connect_to(url)) return false;
            fresh = true;
        }
        buf_pos_ = buf_len_ = 0;
        if (transport_->send(msg.data(), msg.size()) == static_cast<long>(msg.size()) && read_headers()) return true;
        close();
        if (fresh) {
            if (error_.empty()) error_ = "connection failed";
            return false;
        }
    }
    return false;
}

size_t Connection::raw_read(void* dst, size_t n) {
    if (buf_pos_ < buf_len_) {
        size_t got = std::min(n, buf_len_ - buf_pos_);
        memcpy(dst, buf_.data() + buf_pos_, got);
        buf_pos_ += got;
        return got;
    }
    if (!transport_) return 0;
    long r = transport_->recv(dst, n);
    if (r < 0) {
        error_ = "recv error " + std::to_string(r);
        return 0;
    }
    return static_cast<size_t>(r);
}

bool Connection::read_line(std::string& line) {
    line.clear();
    for (;;) {
        if (buf_pos_ == buf_len_) {
            if (!transport_) return false;
            long r = transport_->recv(buf_.data(), buf_.size());
            if (r <= 0) return false;
            buf_pos_ = 0;
            buf_len_ = static_cast<size_t>(r);
        }
        char c = static_cast<char>(buf_[buf_pos_++]);
        if (c == '\n') {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            return true;
        }
        line += c;
        if (line.size() > 16384) return false;
    }
}

bool Connection::read_headers() {
    std::string line;
    if (!read_line(line)) {
        error_ = "no response";
        return false;
    }
    status_ = 0;
    if (sscanf(line.c_str(), "HTTP/%*d.%*d %d", &status_) != 1) {
        error_ = "bad status line";
        return false;
    }
    headers_.clear();
    while (read_line(line) && !line.empty()) {
        size_t colon = line.find(':');
        if (colon == std::string::npos) continue;
        size_t v = line.find_first_not_of(" \t", colon + 1);
        headers_.emplace_back(line.substr(0, colon), v == std::string::npos ? "" : line.substr(v));
    }
    std::string cl = header("Content-Length");
    content_length_ = cl.empty() ? -1 : std::stoll(cl);
    chunked_ = header("Transfer-Encoding").find("chunked") != std::string::npos;
    reusable_ = !iequals(header("Connection"), "close") && (chunked_ || content_length_ >= 0);
    body_done_ = false;
    if (chunked_) {
        remaining_ = 0;
    } else {
        remaining_ = content_length_;
        if (remaining_ == 0) body_done_ = true;
    }
    return true;
}

bool Connection::next_chunk() {
    std::string line;
    if (remaining_ == 0 && !read_line(line)) return false;  // CRLF after previous chunk (not before the first)
    if (line.empty() && !read_line(line)) return false;
    remaining_ = static_cast<int64_t>(strtoll(line.c_str(), nullptr, 16));
    if (remaining_ == 0) {
        while (read_line(line) && !line.empty()) {
        }  // trailers
        body_done_ = true;
    }
    return true;
}

size_t Connection::read_some(void* dst, size_t n) {
    if (body_done_) return 0;
    if (chunked_ && remaining_ == 0) {
        if (!next_chunk() || body_done_) return 0;
    }
    if (remaining_ >= 0 && static_cast<int64_t>(n) > remaining_) n = static_cast<size_t>(remaining_);
    size_t got = raw_read(dst, n);
    if (got == 0) {
        body_done_ = true;
        reusable_ = false;
        return 0;
    }
    if (remaining_ >= 0) {
        remaining_ -= static_cast<int64_t>(got);
        if (remaining_ == 0 && !chunked_) body_done_ = true;
    }
    return got;
}

size_t Connection::read(void* dst, size_t n) {
    auto* out = static_cast<uint8_t*>(dst);
    size_t total = 0;
    while (total < n) {
        size_t got = read_some(out + total, n - total);
        if (got == 0) break;
        total += got;
    }
    return total;
}

std::string Connection::read_all(size_t limit) {
    std::string out;
    char tmp[16384];
    while (out.size() < limit) {
        size_t got = read_some(tmp, sizeof(tmp));
        if (got == 0) break;
        out.append(tmp, got);
    }
    return out;
}

std::string Connection::header(const std::string& name) const {
    for (const auto& h : headers_)
        if (iequals(h.first, name)) return h.second;
    return "";
}

Response fetch(const Request& req) {
    Response res;
    Connection conn;
    Request current = req;
    for (int redirects = 0;; ++redirects) {
        if (!conn.send(current) && !conn.send(current)) {  // one retry: TV Wi-Fi hiccups are common
            res.error = conn.error();
            return res;
        }
        res.status = conn.status();
        res.body = conn.read_all();
        std::string location = conn.header("Location");
        if (!is_redirect(res.status) || location.empty() || redirects == 5) break;
        current.url = location;
        if (res.status == 303) {
            current.method = "GET";
            current.body.clear();
        }
    }
    if (!conn.body_done() && !conn.error().empty()) res.error = conn.error();
    return res;
}

RangeReader::RangeReader(std::string url, Headers headers, uint64_t chunk_size, uint64_t start)
    : url_(std::move(url)), headers_(std::move(headers)), chunk_size_(chunk_size), pos_(start) {}

bool RangeReader::open_next() {
    if (total_ >= 0 && static_cast<int64_t>(pos_) >= total_) {
        eof_ = true;
        return false;
    }
    Request req;
    req.url = url_;
    req.headers = headers_;
    uint64_t end = pos_ + chunk_size_ - 1;
    if (total_ >= 0) end = std::min<uint64_t>(end, static_cast<uint64_t>(total_) - 1);
    req.headers.emplace_back("Range", "bytes=" + std::to_string(pos_) + "-" + std::to_string(end));

    int status = 0;
    for (int redirects = 0;; ++redirects) {
        last_status_ = 0;
        if (!conn_.send(req)) {
            error_ = conn_.error();
            return false;
        }
        status = conn_.status();
        last_status_ = status;
        if (!is_redirect(status)) break;
        // googlevideo redirects to another edge server; keep using the new URL for the following ranges.
        std::string location = conn_.header("Location");
        conn_.read_all(1024 * 1024);
        if (location.empty() || redirects == 5) {
            error_ = "redirect without usable Location";
            conn_.close();
            return false;
        }
        OLOG("range: http %d -> %.60s...", status, location.c_str());
        url_ = req.url = location;
    }
    if (status == 206) {
        std::string cr = conn_.header("Content-Range");  // bytes a-b/total
        size_t slash = cr.find('/');
        if (slash != std::string::npos && cr.compare(slash + 1, 1, "*") != 0) total_ = std::stoll(cr.substr(slash + 1));
    } else if (status == 200) {
        if (pos_ != 0) {
            error_ = "server ignored Range at offset " + std::to_string(pos_);
            conn_.close();
            return false;
        }
        sequential_ = true;  // server ignored Range: stream the whole body
        total_ = conn_.content_length();
    } else {
        error_ = "http " + std::to_string(status);
        conn_.close();
        return false;
    }
    open_ = true;
    return true;
}

bool RangeReader::recover() {
    if (sequential_) return false;  // no Range support: cannot resume mid-file
    constexpr int kMaxFailures = 40;  // ~3 minutes of retrying
    if (++failures_ > kMaxFailures) return false;
    if (hooks_.aborted && hooks_.aborted()) return false;
    bool refusal = last_status_ == 403 || last_status_ == 410;
    refusals_ = refusal ? refusals_ + 1 : 0;
    if (refusals_ == 1) first_refusal_ = std::chrono::steady_clock::now();
    if (hooks_.refusal_budget_ms > 0 && refusals_ > 1 &&
        std::chrono::steady_clock::now() - first_refusal_ > std::chrono::milliseconds(hooks_.refusal_budget_ms)) {
        OLOG("range: stream still refused (http %d) after %d retries, giving up", last_status_, refusals_ - 1);
        refused_ = true;
        return false;
    }
    if (hooks_.on_recovering) hooks_.on_recovering(true);
    OLOG("range: %s at byte %llu (http %d), retry %d", error_.c_str(), static_cast<unsigned long long>(pos_),
         last_status_, failures_);
    conn_.close();
    if ((last_status_ == 403 || last_status_ == 410) && hooks_.refresh_url) {
        std::string fresh = hooks_.refresh_url();
        if (!fresh.empty()) url_ = fresh;
    }
    // Refusals: short waits, a refused stream sometimes opens a few seconds later (seen with TVHTML5 URLs).
    int wait_ms = std::min(refusal ? 2000 : 5000, 250 << std::min(failures_, 5));
    for (int waited = 0; waited < wait_ms; waited += 100) {
        if (hooks_.aborted && hooks_.aborted()) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    error_.clear();
    return true;
}

size_t RangeReader::read(void* dst, size_t n) {
    auto* out = static_cast<uint8_t*>(dst);
    size_t total = 0;
    while (total < n && !eof_) {
        if (!open_ && !open_next()) {
            if (eof_ || !recover()) break;
            continue;
        }
        size_t got = conn_.read_some(out + total, n - total);
        if (got == 0) {
            open_ = false;
            if (sequential_) {
                eof_ = true;
            } else if (!conn_.body_done() || !conn_.error().empty()) {
                error_ = "range body ended early: " + conn_.error();
                if (!recover()) break;
            }
            continue;
        }
        if (failures_ > 0) {
            OLOG("range: recovered at byte %llu after %d retries", static_cast<unsigned long long>(pos_), failures_);
            failures_ = 0;
            refusals_ = 0;
            if (hooks_.on_recovering) hooks_.on_recovering(false);
        }
        total += got;
        pos_ += got;
    }
    return total;
}

LiveReader::LiveReader(std::string url, Headers headers, int64_t first_sequence)
    : url_(std::move(url)), headers_(std::move(headers)), seq_(first_sequence) {}

namespace {

// GET with redirects followed on `conn`; the original URL is kept by the caller (Location carries the sq).
bool send_following(Connection& conn, Request req, int& status) {
    for (int redirects = 0;; ++redirects) {
        status = 0;
        if (!conn.send(req)) return false;
        status = conn.status();
        if (!is_redirect(status)) return true;
        std::string location = conn.header("Location");
        conn.read_all(1024 * 1024);
        if (location.empty() || redirects == 5) return true;
        req.url = location;
    }
}

}  // namespace

int64_t LiveReader::head_sequence(const std::string& url, const Headers& headers, std::string& error) {
    Connection conn;
    Request req;
    req.url = url;
    req.headers = headers;
    req.headers.emplace_back("Range", "bytes=0-0");  // the head segment itself is not needed
    int status = 0;
    if (!send_following(conn, req, status)) {
        error = conn.error();
        return -1;
    }
    std::string head = conn.header("X-Head-Seqnum");
    conn.close();
    if (status / 100 != 2 || head.empty()) {
        error = "live head: http " + std::to_string(status) + (head.empty() ? ", no X-Head-Seqnum" : "");
        return -1;
    }
    return std::stoll(head);
}

bool LiveReader::open_segment() {
    Request req;
    req.url = url_ + (url_.find('?') == std::string::npos ? "?" : "&") + "sq=" + std::to_string(seq_);
    req.headers = headers_;
    // Resume a segment cut by a network error where it stopped.
    if (segment_pos_ > 0) req.headers.emplace_back("Range", "bytes=" + std::to_string(segment_pos_) + "-");
    if (!send_following(conn_, req, last_status_)) {
        error_ = conn_.error();
        return false;
    }
    if (last_status_ == 200 && segment_pos_ == 0) {
        open_ = true;
        return true;
    }
    if (last_status_ == 206 && segment_pos_ > 0) {
        open_ = true;
        return true;
    }
    error_ = "segment " + std::to_string(seq_) + ": http " + std::to_string(last_status_);
    conn_.close();
    return false;
}

bool LiveReader::retry(int max_failures) {
    if (++failures_ > max_failures) return false;
    if (hooks_.aborted && hooks_.aborted()) return false;
    // A 404 at the live edge is the normal wait for the next segment: only report longer outages.
    if (failures_ > 2 && hooks_.on_recovering) hooks_.on_recovering(true);
    if (last_status_ != 404 || failures_ > 2)
        OLOG("live: %s, retry %d", error_.c_str(), failures_);
    conn_.close();
    if ((last_status_ == 403 || last_status_ == 410) && hooks_.refresh_url) {
        std::string fresh = hooks_.refresh_url();
        if (!fresh.empty()) url_ = fresh;
    }
    // The server already held a 404 for several seconds; other errors back off.
    int wait_ms = last_status_ == 404 ? 1000 : std::min(5000, 250 << std::min(failures_, 5));
    for (int waited = 0; waited < wait_ms; waited += 100) {
        if (hooks_.aborted && hooks_.aborted()) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    error_.clear();
    return true;
}

size_t LiveReader::read(void* dst, size_t n) {
    auto* out = static_cast<uint8_t*>(dst);
    size_t total = 0;
    while (total < n && !eof_) {
        if (!open_ && !open_segment()) {
            // ~8 s per 404: give up after ~1 min without a new segment (broadcast over), ~3 min of network errors.
            if (!retry(last_status_ == 404 ? 7 : 40)) {
                OLOG("live: stream ends at segment %lld (%s)", static_cast<long long>(seq_), error_.c_str());
                eof_ = true;
            }
            continue;
        }
        size_t got = conn_.read_some(out + total, n - total);
        if (got == 0) {
            open_ = false;
            if (conn_.body_done() && conn_.error().empty()) {
                ++seq_;
                segment_pos_ = 0;
            } else {
                error_ = "segment body ended early: " + conn_.error();
                last_status_ = 0;
                if (!retry(40)) eof_ = true;
            }
            continue;
        }
        if (failures_ > 0) {
            if (failures_ > 2) {
                OLOG("live: recovered at segment %lld after %d retries", static_cast<long long>(seq_), failures_);
                if (hooks_.on_recovering) hooks_.on_recovering(false);
            }
            failures_ = 0;
        }
        total += got;
        pos_ += got;
        segment_pos_ += got;
    }
    return total;
}

}  // namespace omega::net
