#pragma once

#include <chrono>
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

// Forward-only byte stream of a media track, as the demuxers consume it.
class StreamSource {
public:
    struct Hooks {
        std::function<bool()> aborted;             // stop retrying (seek / player teardown)
        std::function<std::string()> refresh_url;  // new URL for an expired one ("" = none)
        std::function<void(bool)> on_recovering;  // true while retrying, false once data flows again
        // > 0: give up once 403/410 (fresh URLs included) have lasted that long. An expired URL is cured by
        // the first refresh; TVHTML5 URLs are often refused for ~10 s after being issued, then open; a stream
        // still refused after that is YouTube blocking us (bot flag): retrying for minutes only keeps the
        // spinner up.
        int refusal_budget_ms = 0;
    };
    virtual ~StreamSource() = default;

    virtual size_t read(void* dst, size_t n) = 0;
    virtual uint64_t position() const = 0;
    virtual int64_t total_size() const = 0;  // -1: unknown / unbounded
    virtual const std::string& url() const = 0;  // to reopen the stream later (after redirects)
    virtual const std::string& error() const = 0;
    // Gave up because the server kept refusing the stream (Hooks::max_refusals).
    virtual bool refused() const { return false; }
    void set_hooks(Hooks hooks) { hooks_ = std::move(hooks); }

protected:
    Hooks hooks_;
};

// Forward-only reader over a (possibly huge) remote file, fetched as successive byte ranges on one
// kept-alive connection. Falls back to a single sequential GET when the server ignores ranges.
// Network failures are retried at the same position with backoff; 403/410 (expired stream URL) asks
// the refresh_url hook for a fresh URL first.
class RangeReader : public StreamSource {
public:
    RangeReader(std::string url, Headers headers, uint64_t chunk_size = 4 * 1024 * 1024, uint64_t start = 0);

    size_t read(void* dst, size_t n) override;
    uint64_t position() const override { return pos_; }
    int64_t total_size() const override { return total_; }
    const std::string& url() const override { return url_; }
    const std::string& error() const override { return error_; }
    bool refused() const override { return refused_; }

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
    int refusals_ = 0;  // consecutive 403/410
    std::chrono::steady_clock::time_point first_refusal_;
    bool refused_ = false;
};

// A YouTube live track: numbered, self-contained fMP4 segments (url&sq=N, each ftyp+moov+moof+mdat with
// tfdt) read back to back as one byte stream. At the live edge the server holds a request for a segment
// that does not exist yet and answers 404: we retry until it appears, and end the stream (EOF) when no
// new segment shows up for ~1 min (broadcast over).
class LiveReader : public StreamSource {
public:
    LiveReader(std::string url, Headers headers, int64_t first_sequence);
    // Sequence number of the newest segment (X-Head-Seqnum), -1 on failure.
    static int64_t head_sequence(const std::string& url, const Headers& headers, std::string& error);

    size_t read(void* dst, size_t n) override;
    uint64_t position() const override { return pos_; }
    int64_t total_size() const override { return -1; }
    const std::string& url() const override { return url_; }
    const std::string& error() const override { return error_; }
    int64_t sequence() const { return seq_; }

private:
    bool open_segment();
    bool retry(int max_failures);  // false: give up

    std::string url_;  // without sq
    Headers headers_;
    Connection conn_;
    int64_t seq_;
    uint64_t pos_ = 0, segment_pos_ = 0;  // stream offset, offset inside the current segment
    bool open_ = false, eof_ = false;
    int last_status_ = 0, failures_ = 0;
    std::string error_;
};

}  // namespace omega::net
