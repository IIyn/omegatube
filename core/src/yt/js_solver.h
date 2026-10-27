#pragma once

#include <chrono>
#include <map>
#include <memory>
#include <mutex>
#include <string>

struct JSRuntime;
struct JSContext;

namespace omega::yt {

// QuickJS host for the yt-dlp EJS solver (github.com/yt-dlp/ejs): finds the n (throttling) and sig
// (signatureCipher) functions of a YouTube player JS. Not thread-safe: guard with a mutex.
class JsSolver {
public:
    JsSolver() = default;
    ~JsSolver();
    JsSolver(const JsSolver&) = delete;
    JsSolver& operator=(const JsSolver&) = delete;

    // Slow (parses the whole ~2.5 MB player): the preprocessed player, a few MB, to keep for load().
    static bool preprocess(const std::string& player_js, std::string& preprocessed, std::string& error);
    // Fast: instantiates the n / sig functions of a preprocessed player.
    bool load(const std::string& preprocessed, std::string& error);
    bool loaded() const { return ctx_ != nullptr; }
    // type: "n" or "sig".
    bool solve(const char* type, const std::string& challenge, std::string& out, std::string& error);

private:
    void reset();
    JSRuntime* rt_ = nullptr;
    JSContext* ctx_ = nullptr;
};

// Deciphering for the fallback player client (TV): tracks the current TV player version (youtube.com/tv),
// prepares its solver once per version (cached on disk: the full parse takes long on the TV), and fixes
// stream URLs. Thread-safe.
class Decipher {
public:
    // Directory for the preprocessed players (app data). Empty: no disk cache.
    void set_cache_dir(const std::string& dir);
    // Makes sure the solver matches the current player (re-checked at most hourly).
    bool ensure(std::string& error);
    // signatureTimestamp of the current player, for the player request (0 before ensure()).
    int signature_timestamp();
    // Builds a playable URL from a format's "url" or "signatureCipher" (s/sp/url), with n deciphered.
    bool stream_url(const std::string& url, const std::string& signature_cipher, std::string& out, std::string& error);

private:
    bool solve_cached(const char* type, const std::string& challenge, std::string& out, std::string& error);

    std::mutex mutex_;
    std::string cache_dir_;
    std::string player_id_;
    int sts_ = 0;
    std::chrono::steady_clock::time_point checked_{};
    std::unique_ptr<JsSolver> solver_;
    std::map<std::string, std::string> n_cache_;  // same n across all formats of a response
};

Decipher& shared_decipher();

}  // namespace omega::yt
