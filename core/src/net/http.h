#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace omega::net {

using Headers = std::vector<std::pair<std::string, std::string>>;

struct Request {
    std::string method = "GET";
    std::string url;  // http:// or https://
    Headers headers;
    std::string body;
};

struct Url {
    bool tls = false;
    std::string host, port, path;  // path includes the query string
    static bool parse(const std::string& url, Url& out);
};

class Transport;

// One HTTP/1.1 connection (plain or TLS) that can carry several requests to the same host
// (keep-alive). A response body must be fully read (or the connection closed) before the next request.
class Connection {
public:
    Connection();
    ~Connection();
    Connection(const Connection&) = delete;
    Connection& operator=(const Connection&) = delete;

    // Sends `req` (reusing the socket when host/port/tls match and the server kept it alive) and reads
    // the response headers. Returns false on transport error (see error()); HTTP errors are a status().
    bool send(const Request& req);
    // Reads up to n body bytes (returns 0 at end of body or on error).
    size_t read_some(void* dst, size_t n);
    // Reads exactly n body bytes unless the body ends first.
    size_t read(void* dst, size_t n);
    std::string read_all(size_t limit = 64 * 1024 * 1024);
    void close();

    int status() const { return status_; }
    int64_t content_length() const { return content_length_; }
    std::string header(const std::string& name) const;  // case-insensitive, "" if absent
    bool body_done() const { return body_done_; }
    const std::string& error() const { return error_; }

private:
    bool connect_to(const Url& url);
    bool read_headers();
    size_t raw_read(void* dst, size_t n);  // from buffer, then transport
    bool read_line(std::string& line);
    bool next_chunk();

    std::unique_ptr<Transport> transport_;
    std::string host_key_;
    bool reusable_ = false;

    std::vector<uint8_t> buf_;
    size_t buf_pos_ = 0, buf_len_ = 0;

    int status_ = 0;
    Headers headers_;
    int64_t content_length_ = -1;
    bool chunked_ = false;
    int64_t remaining_ = -1;  // bytes left in body (or current chunk when chunked)
    bool body_done_ = true;
    std::string error_;
};

struct Response {
    int status = 0;
    std::string body;
    std::string error;  // transport error, empty if the exchange completed
    bool ok() const { return error.empty() && status >= 200 && status < 300; }
};

// Convenience one-shot exchange (new connection each time).
Response fetch(const Request& req);

// Forward-only reader over a (possibly huge) remote file, fetched as successive byte ranges on one
// kept-alive connection. Falls back to a single sequential GET when the server ignores ranges.
// Network failures are retried at the same position with backoff; 403/410 (expired stream URL) asks
// the refresh_url hook for a fresh URL first.
class RangeReader {
public:
    struct Hooks {
        std::function<bool()> aborted;             // stop retrying (seek / player teardown)
        std::function<std::string()> refresh_url;  // new URL for an expired one ("" = none)
        std::function<void(bool)> on_recovering;  // true while retrying, false once data flows again
    };
    RangeReader(std::string url, Headers headers, uint64_t chunk_size = 4 * 1024 * 1024, uint64_t start = 0);

    size_t read(void* dst, size_t n);
    uint64_t position() const { return pos_; }
    int64_t total_size() const { return total_; }
    const std::string& url() const { return url_; }  // after redirects
    void set_hooks(Hooks hooks) { hooks_ = std::move(hooks); }
    const std::string& error() const { return error_; }

private:
    bool open_next();
    bool recover();  // false: give up

    std::string url_;
    Headers headers_;
    uint64_t chunk_size_;
    Connection conn_;
    uint64_t pos_ = 0;
    int64_t total_ = -1;
    bool sequential_ = false;
    bool open_ = false;
    bool eof_ = false;
    std::string error_;
    int last_status_ = 0;
    int failures_ = 0;  // consecutive
    Hooks hooks_;
};

}  // namespace omega::net
