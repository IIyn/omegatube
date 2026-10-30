#include "yt/js_solver.h"

#include <quickjs.h>

#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>

#include "net/http.h"
#include "util/log.h"

// The EJS solver bundles, embedded at build time (CMakeLists.txt).
extern const char omega_ejs_lib[];
extern const char omega_ejs_core[];

namespace omega::yt {
namespace {

using Clock = std::chrono::steady_clock;

constexpr const char* kTvPage = "https://www.youtube.com/tv";
constexpr const char* kTvUserAgent =
    "Mozilla/5.0 (SMART-TV; Linux; Tizen 2.4.0) AppleWebKit/538.1 (KHTML, like Gecko) Version/2.4.0 TV Safari/538.1";

std::string exception_text(JSContext* ctx) {
    JSValue e = JS_GetException(ctx);
    const char* s = JS_ToCString(ctx, e);
    std::string out = s ? s : "unknown JS error";
    if (s) JS_FreeCString(ctx, s);
    JSValue stack = JS_IsObject(e) ? JS_GetPropertyStr(ctx, e, "stack") : JS_UNDEFINED;
    if (JS_IsString(stack)) {
        const char* st = JS_ToCString(ctx, stack);
        if (st) {
            out += " | " + std::string(st).substr(0, 300);
            JS_FreeCString(ctx, st);
        }
    }
    JS_FreeValue(ctx, stack);
    JS_FreeValue(ctx, e);
    return out;
}

// Evaluates `code` (global scope); out receives the result as a string. False with error on exception.
bool eval_string(JSContext* ctx, const std::string& code, const char* name, std::string* out, std::string& error) {
    JSValue v = JS_Eval(ctx, code.c_str(), code.size(), name, JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(v)) {
        error = std::string(name) + ": " + exception_text(ctx);
        return false;
    }
    if (out) {
        size_t len = 0;
        const char* s = JS_ToCStringLen(ctx, &len, v);
        if (!s) {
            JS_FreeValue(ctx, v);
            error = std::string(name) + ": result is not a string";
            return false;
        }
        out->assign(s, len);
        JS_FreeCString(ctx, s);
    }
    JS_FreeValue(ctx, v);
    return true;
}

void set_global_string(JSContext* ctx, const char* name, const std::string& value) {
    JSValue global = JS_GetGlobalObject(ctx);
    JS_SetPropertyStr(ctx, global, name, JS_NewStringLen(ctx, value.data(), value.size()));
    JS_FreeValue(ctx, global);
}

JSRuntime* new_runtime() {
    JSRuntime* rt = JS_NewRuntime();
    if (!rt) return nullptr;
    JS_SetMemoryLimit(rt, 384u << 20);
    JS_SetMaxStackSize(rt, 4u << 20);  // meriyah recurses deeply on the player; worker threads have 8 MB
    return rt;
}

std::string read_file(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return "";
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

bool write_file(const std::string& path, const std::string& data) {
    std::string tmp = path + ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out || !out.write(data.data(), static_cast<std::streamsize>(data.size()))) return false;
    }
    return std::rename(tmp.c_str(), path.c_str()) == 0;
}

int hex(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

std::string url_decode(const std::string& s) {
    std::string out;
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '%' && i + 2 < s.size() && hex(s[i + 1]) >= 0 && hex(s[i + 2]) >= 0) {
            out += static_cast<char>(hex(s[i + 1]) << 4 | hex(s[i + 2]));
            i += 2;
        } else {
            out += s[i] == '+' ? ' ' : s[i];
        }
    }
    return out;
}

std::string url_encode(const std::string& s) {
    static const char* digits = "0123456789ABCDEF";
    std::string out;
    for (unsigned char c : s) {
        if (isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') {
            out += static_cast<char>(c);
        } else {
            out += '%';
            out += digits[c >> 4];
            out += digits[c & 15];
        }
    }
    return out;
}

// key=value&... -> value of `key` (decoded), "" if absent.
std::string query_value(const std::string& query, const std::string& key) {
    size_t start = 0;
    while (start < query.size()) {
        size_t amp = query.find('&', start);
        if (amp == std::string::npos) amp = query.size();
        size_t eq = query.find('=', start);
        if (eq != std::string::npos && eq < amp && query.compare(start, eq - start, key) == 0)
            return url_decode(query.substr(eq + 1, amp - eq - 1));
        start = amp + 1;
    }
    return "";
}

// "/s/player/<id>/tv-player-ias.vflset/tv-player-ias.js" referenced by the TV page.
bool find_player(std::string& id, std::string& url, std::string& error) {
    net::Request req;
    req.url = kTvPage;
    req.headers = {{"User-Agent", kTvUserAgent}};
    net::Response page = net::fetch(req);
    if (!page.ok()) {
        error = "tv page: " + (page.error.empty() ? "http " + std::to_string(page.status) : page.error);
        return false;
    }
    const std::string marker = "/s/player/";
    for (size_t at = page.body.find(marker); at != std::string::npos; at = page.body.find(marker, at + 1)) {
        size_t end = page.body.find_first_of("\"'", at);
        if (end == std::string::npos) break;
        std::string path = page.body.substr(at, end - at);
        if (path.size() < 3 || path.compare(path.size() - 3, 3, ".js") != 0) continue;
        size_t id_begin = marker.size(), id_end = path.find('/', id_begin);
        if (id_end == std::string::npos) continue;
        id = path.substr(id_begin, id_end - id_begin);
        url = "https://www.youtube.com" + path;
        return true;
    }
    error = "player js not found on the TV page";
    return false;
}

int parse_sts(const std::string& js) {
    const std::string key = "signatureTimestamp:";
    size_t at = js.find(key);
    return at == std::string::npos ? 0 : atoi(js.c_str() + at + key.size());
}

}  // namespace

JsSolver::~JsSolver() { reset(); }

void JsSolver::reset() {
    if (ctx_) JS_FreeContext(ctx_);
    if (rt_) JS_FreeRuntime(rt_);
    ctx_ = nullptr;
    rt_ = nullptr;
}

bool JsSolver::preprocess(const std::string& player_js, std::string& preprocessed, std::string& error) {
    JSRuntime* rt = new_runtime();
    JSContext* ctx = rt ? JS_NewContext(rt) : nullptr;
    if (!ctx) {
        if (rt) JS_FreeRuntime(rt);
        error = "quickjs: out of memory";
        return false;
    }
    // lib.min.js defines `lib` (meriyah + astring); core.min.js expects them as globals.
    std::string setup = std::string(omega_ejs_lib) + "\nObject.assign(globalThis, lib);\n" + omega_ejs_core;
    bool ok = eval_string(ctx, setup, "ejs", nullptr, error);
    if (ok) {
        set_global_string(ctx, "_player", player_js);
        // A throwaway n challenge checks that the functions were really found.
        ok = eval_string(ctx,
                         "(() => { const r = jsc({type: 'player', player: _player, output_preprocessed: true,"
                         " requests: [{type: 'n', challenges: ['omegatubeprobe00']}]});"
                         " const n = r.responses[0];"
                         " if (n.type !== 'result') throw new Error('n: ' + n.error);"
                         " return r.preprocessed_player; })()",
                         "ejs-preprocess", &preprocessed, error);
    }
    JS_FreeContext(ctx);
    JS_FreeRuntime(rt);
    return ok;
}

bool JsSolver::load(const std::string& preprocessed, std::string& error) {
    reset();
    rt_ = new_runtime();
    ctx_ = rt_ ? JS_NewContext(rt_) : nullptr;
    if (!ctx_) {
        reset();
        error = "quickjs: out of memory";
        return false;
    }
    // Same instantiation as EJS's core: the preprocessed player fills _result.n / _result.sig.
    set_global_string(ctx_, "_pre", preprocessed);
    std::string kinds;
    if (!eval_string(ctx_,
                     "var _fns = {n: null, sig: null}; Function('_result', _pre)(_fns); delete globalThis._pre;"
                     " (typeof _fns.n) + ',' + (typeof _fns.sig)",
                     "ejs-load", &kinds, error)) {
        reset();
        return false;
    }
    if (kinds.compare(0, 8, "function") != 0) {
        error = "ejs-load: no n function (" + kinds + ")";
        reset();
        return false;
    }
    return true;
}

bool JsSolver::solve(const char* type, const std::string& challenge, std::string& out, std::string& error) {
    if (!ctx_) {
        error = "solver not loaded";
        return false;
    }
    // QuickJS measures its stack limit from the thread that created the runtime; each video asks from
    // another thread, whose stack can sit anywhere: without this, spurious "Maximum call stack size exceeded".
    JS_UpdateStackTop(rt_);
    JSValue global = JS_GetGlobalObject(ctx_);
    JSValue fns = JS_GetPropertyStr(ctx_, global, "_fns");
    JSValue fn = JS_GetPropertyStr(ctx_, fns, type);
    bool ok = false;
    if (!JS_IsFunction(ctx_, fn)) {
        error = std::string("no ") + type + " function";
    } else {
        JSValue arg = JS_NewStringLen(ctx_, challenge.data(), challenge.size());
        JSValue r = JS_Call(ctx_, fn, JS_UNDEFINED, 1, &arg);
        JS_FreeValue(ctx_, arg);
        if (JS_IsException(r)) {
            error = std::string(type) + ": " + exception_text(ctx_);
        } else if (const char* s = JS_ToCString(ctx_, r)) {
            out = s;
            JS_FreeCString(ctx_, s);
            ok = !out.empty();
            if (!ok) error = std::string(type) + ": empty result";
        }
        JS_FreeValue(ctx_, r);
    }
    JS_FreeValue(ctx_, fn);
    JS_FreeValue(ctx_, fns);
    JS_FreeValue(ctx_, global);
    return ok;
}

void Decipher::set_cache_dir(const std::string& dir) {
    std::lock_guard<std::mutex> lock(mutex_);
    cache_dir_ = dir;
}

bool Decipher::ensure(std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (solver_ && Clock::now() - checked_ < std::chrono::hours(1)) return true;
    std::string id, url;
    if (!find_player(id, url, error)) return solver_ != nullptr;  // keep using the known player offline
    checked_ = Clock::now();
    if (solver_ && id == player_id_) return true;

    auto t0 = Clock::now();
    std::string base = cache_dir_.empty() ? "" : cache_dir_ + "/player-" + id;
    std::string preprocessed = base.empty() ? "" : read_file(base + ".js");
    int sts = base.empty() ? 0 : atoi(read_file(base + ".sts").c_str());
    if (preprocessed.empty() || sts == 0) {
        net::Request req;
        req.url = url;
        req.headers = {{"User-Agent", kTvUserAgent}};
        net::Response js = net::fetch(req);
        if (!js.ok()) {
            error = "player js: " + (js.error.empty() ? "http " + std::to_string(js.status) : js.error);
            return false;
        }
        sts = parse_sts(js.body);
        OLOG("decipher: player %s (%zu bytes, sts %d), preprocessing...", id.c_str(), js.body.size(), sts);
        if (!JsSolver::preprocess(js.body, preprocessed, error)) return false;
        if (!base.empty() && (!write_file(base + ".js", preprocessed) || !write_file(base + ".sts", std::to_string(sts))))
            OLOG("decipher: could not cache %s", base.c_str());
    }
    auto solver = std::make_unique<JsSolver>();
    if (!solver->load(preprocessed, error)) return false;
    OLOG("decipher: player %s ready in %lld ms", id.c_str(),
         static_cast<long long>(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - t0).count()));
    solver_ = std::move(solver);
    player_id_ = id;
    sts_ = sts;
    n_cache_.clear();
    return true;
}

int Decipher::signature_timestamp() {
    std::lock_guard<std::mutex> lock(mutex_);
    return sts_;
}

bool Decipher::solve_cached(const char* type, const std::string& challenge, std::string& out, std::string& error) {
    bool is_n = std::string(type) == "n";
    if (is_n) {
        auto it = n_cache_.find(challenge);
        if (it != n_cache_.end()) {
            out = it->second;
            return true;
        }
    }
    if (!solver_ || !solver_->solve(type, challenge, out, error)) {
        if (!solver_) error = "decipher not ready";
        return false;
    }
    if (is_n) n_cache_[challenge] = out;
    return true;
}

bool Decipher::stream_url(const std::string& url, const std::string& signature_cipher, std::string& out,
                          std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    out = url;
    if (out.empty() && !signature_cipher.empty()) {
        out = query_value(signature_cipher, "url");
        std::string s = query_value(signature_cipher, "s");
        std::string sp = query_value(signature_cipher, "sp");
        std::string sig;
        if (out.empty() || s.empty() || !solve_cached("sig", s, sig, error)) {
            if (error.empty()) error = "bad signatureCipher";
            return false;
        }
        out += "&" + (sp.empty() ? std::string("signature") : sp) + "=" + url_encode(sig);
    }
    if (out.empty()) {
        error = "format without url";
        return false;
    }
    // Throttling parameter: n=<challenge> -> n=<answer>.
    size_t q = out.find('?');
    for (size_t at = q; at != std::string::npos; at = out.find('&', at + 1)) {
        if (out.compare(at + 1, 2, "n=") != 0) continue;
        size_t begin = at + 3, end = out.find('&', begin);
        std::string challenge = url_decode(out.substr(begin, end == std::string::npos ? std::string::npos : end - begin));
        std::string answer;
        if (!solve_cached("n", challenge, answer, error)) return false;
        out.replace(begin, (end == std::string::npos ? out.size() : end) - begin, url_encode(answer));
        break;
    }
    return true;
}

Decipher& shared_decipher() {
    static Decipher d;
    return d;
}

}  // namespace omega::yt
