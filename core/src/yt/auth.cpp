#include "yt/auth.h"

#include <algorithm>
#include <ctime>
#include <fstream>
#include <sstream>

#include <nlohmann/json.hpp>

#include "net/http.h"
#include "util/log.h"
#include "util/remote_config.h"

namespace omega::yt {
namespace {

using json = nlohmann::json;

constexpr const char* kDeviceCodeUrl = "https://oauth2.googleapis.com/device/code";
constexpr const char* kTokenUrl = "https://oauth2.googleapis.com/token";
constexpr const char* kTvPage = "https://www.youtube.com/tv";

// Scope, user agent and the fallback client pair (used when the TV bundle cannot be read) come from
// remote-config.json, so a rotated client can be fixed without a new build.
std::string tv_user_agent() { return cfg::str({"oauth", "userAgent"}); }
std::string fallback_id() { return cfg::str({"oauth", "fallbackClientId"}); }
std::string fallback_secret() { return cfg::str({"oauth", "fallbackClientSecret"}); }

std::string form_escape(const std::string& s) {
    std::string out;
    for (unsigned char c : s) {
        if (isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') {
            out += static_cast<char>(c);
        } else {
            char hex[4];
            snprintf(hex, sizeof(hex), "%%%02X", c);
            out += hex;
        }
    }
    return out;
}

json post_form(const std::string& url, const std::string& body, int& status, std::string& error) {
    net::Request req;
    req.method = "POST";
    req.url = url;
    req.headers = {{"Content-Type", "application/x-www-form-urlencoded"}, {"User-Agent", tv_user_agent()}};
    req.body = body;
    net::Response res = net::fetch(req);
    status = res.status;
    if (!res.error.empty()) {
        error = res.error;
        return json();
    }
    json j = json::parse(res.body, nullptr, false);
    return j.is_discarded() ? json() : j;
}

std::string oauth_error(const json& j, int status) {
    if (j.is_object()) {
        std::string d = j.value("error_description", "");
        if (!d.empty()) return d;
        std::string e = j.value("error", "");
        if (!e.empty()) return e;
    }
    return "http " + std::to_string(status);
}

// "<script id="base-js" src="...">" of the TV page, then "clientId:"<id>",<x>:"<secret>"" in the bundle.
bool scrape_identity(std::string& id, std::string& secret, std::string& error) {
    net::Request req;
    req.url = kTvPage;
    req.headers = {{"User-Agent", tv_user_agent()}};
    net::Response page = net::fetch(req);
    if (!page.ok()) {
        error = "tv page: " + (page.error.empty() ? "http " + std::to_string(page.status) : page.error);
        return false;
    }
    size_t at = page.body.find("id=\"base-js\"");
    size_t src = at == std::string::npos ? at : page.body.find("src=\"", at);
    if (src == std::string::npos) {
        error = "base-js not found";
        return false;
    }
    src += 5;
    std::string url = page.body.substr(src, page.body.find('"', src) - src);
    if (url.compare(0, 2, "//") == 0) url = "https:" + url;
    else if (url.compare(0, 1, "/") == 0) url = "https://www.youtube.com" + url;
    req.url = url;
    net::Response bundle = net::fetch(req);
    if (!bundle.ok()) {
        error = "base-js: http " + std::to_string(bundle.status);
        return false;
    }
    const std::string marker = "clientId:\"";
    size_t c = bundle.body.find(marker);
    if (c == std::string::npos) {
        error = "clientId not found";
        return false;
    }
    size_t id_begin = c + marker.size(), id_end = bundle.body.find('"', id_begin);
    size_t key = bundle.body.find(":\"", id_end);
    if (id_end == std::string::npos || key == std::string::npos) {
        error = "client secret not found";
        return false;
    }
    size_t secret_begin = key + 2, secret_end = bundle.body.find('"', secret_begin);
    id = bundle.body.substr(id_begin, id_end - id_begin);
    secret = bundle.body.substr(secret_begin, secret_end - secret_begin);
    return !id.empty() && !secret.empty();
}

// Directory part of a path (with the trailing slash), or "" when there is none.
std::string dir_of(const std::string& path) {
    size_t slash = path.find_last_of('/');
    return slash == std::string::npos ? "" : path.substr(0, slash + 1);
}

json read_json_file(const std::string& path) {
    std::ifstream in(path);
    if (!in) return json();
    std::stringstream ss;
    ss << in.rdbuf();
    json j = json::parse(ss.str(), nullptr, false);
    return j.is_discarded() ? json() : j;
}

}  // namespace

void Auth::init(const std::string& store_path) {
    std::lock_guard<std::mutex> lock(mutex_);
    store_path_ = store_path;
    legacy_path_ = dir_of(store_path) + "auth.json";
    accounts_.clear();
    active_id_.clear();
    access_token_.clear();
    access_expires_at_ = 0;
    load_store_locked();
    OLOG("auth: %zu account(s) loaded", accounts_.size());
}

Account* Auth::active_locked() {
    for (auto& a : accounts_) {
        if (a.id == active_id_) return &a;
    }
    return nullptr;
}

std::string Auth::next_id_locked() const {
    int max = 0;
    for (const auto& a : accounts_) {
        try {
            max = std::max(max, std::stoi(a.id));
        } catch (...) {
        }
    }
    return std::to_string(max + 1);
}

void Auth::load_store_locked() {
    json j = read_json_file(store_path_);
    if (j.is_object() && j.contains("accounts") && j["accounts"].is_array()) {
        for (const auto& a : j["accounts"]) {
            if (!a.is_object()) continue;
            Account acc;
            acc.id = a.value("id", "");
            acc.refresh_token = a.value("refresh_token", "");
            acc.client_id = a.value("client_id", "");
            acc.client_secret = a.value("client_secret", "");
            acc.name = a.value("name", "");
            acc.handle = a.value("handle", "");
            acc.photo = a.value("photo", "");
            if (!acc.id.empty() && !acc.refresh_token.empty()) accounts_.push_back(acc);
        }
        active_id_ = j.value("active", "");
    } else if (j.is_object() && !j.value("refresh_token", std::string()).empty()) {
        // Legacy single-account format stored at store_path_ itself (e.g. the host tool's file): migrate in place.
        Account acc{"1", j.value("refresh_token", ""), j.value("client_id", ""), j.value("client_secret", ""), "", "", ""};
        accounts_.push_back(acc);
        active_id_ = acc.id;
        save_locked();
    } else {
        // No usable store: migrate a legacy auth.json next to it (the app's old single-account file).
        json lj = read_json_file(legacy_path_);
        if (lj.is_object() && !lj.value("refresh_token", std::string()).empty()) {
            Account acc{"1", lj.value("refresh_token", ""), lj.value("client_id", ""), lj.value("client_secret", ""), "", "", ""};
            accounts_.push_back(acc);
            active_id_ = acc.id;
            save_locked();
        }
    }
    if (active_locked() == nullptr) active_id_ = accounts_.empty() ? std::string() : accounts_.front().id;
}

void Auth::save_locked() {
    if (store_path_.empty()) return;
    std::ofstream out(store_path_, std::ios::trunc);
    json arr = json::array();
    for (const auto& a : accounts_) {
        arr.push_back({{"id", a.id},
                       {"refresh_token", a.refresh_token},
                       {"client_id", a.client_id},
                       {"client_secret", a.client_secret},
                       {"name", a.name},
                       {"handle", a.handle},
                       {"photo", a.photo}});
    }
    out << json{{"v", 2}, {"active", active_id_}, {"accounts", arr}}.dump();
}

bool Auth::ensure_identity(std::string& error) {
    if (!client_id_.empty()) return true;
    std::string scrape_error;
    if (scrape_identity(client_id_, client_secret_, scrape_error)) {
        OLOG("auth: TV client id from bundle");
    } else {
        OLOG("auth: %s, using fallback client id", scrape_error.c_str());
        client_id_ = fallback_id();
        client_secret_ = fallback_secret();
    }
    (void)error;
    return true;
}

bool Auth::begin(DeviceCode& out, std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!ensure_identity(error)) return false;
    int status = 0;
    json j = post_form(kDeviceCodeUrl, "client_id=" + form_escape(client_id_) + "&scope=" + form_escape(cfg::str({"oauth", "scope"})), status,
                       error);
    if (!error.empty()) return false;
    if (status != 200 || !j.is_object() || !j.contains("device_code")) {
        error = oauth_error(j, status);
        return false;
    }
    out.device_code = j.value("device_code", "");
    out.user_code = j.value("user_code", "");
    out.verification_url = j.value("verification_url", "https://www.google.com/device");
    out.interval_s = j.value("interval", 5);
    out.expires_in_s = j.value("expires_in", 1800);
    OLOG("auth: device code issued");  // not the code itself: logs end up in public bug reports
    return true;
}

PollResult Auth::poll(const DeviceCode& code, std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    int status = 0;
    json j = post_form(kTokenUrl,
                       "client_id=" + form_escape(client_id_) + "&client_secret=" + form_escape(client_secret_) +
                           "&device_code=" + form_escape(code.device_code) +
                           "&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code",
                       status, error);
    if (!error.empty()) return PollResult::Failed;
    if (status == 200 && j.is_object() && j.contains("refresh_token")) {
        Account acc;
        acc.id = next_id_locked();
        acc.refresh_token = j.value("refresh_token", "");
        acc.client_id = client_id_;
        acc.client_secret = client_secret_;
        accounts_.push_back(acc);
        active_id_ = acc.id;  // the newly added account becomes active
        access_token_ = j.value("access_token", "");
        access_expires_at_ = static_cast<int64_t>(time(nullptr)) + j.value("expires_in", 3600) - 60;
        save_locked();
        OLOG("auth: signed in (%zu account(s))", accounts_.size());
        return PollResult::Done;
    }
    std::string e = j.is_object() ? j.value("error", "") : "";
    if (e == "authorization_pending") return PollResult::Pending;
    if (e == "slow_down") return PollResult::SlowDown;
    if (e == "access_denied") return PollResult::Denied;
    if (e == "expired_token") return PollResult::Expired;
    error = oauth_error(j, status);
    return PollResult::Failed;
}

bool Auth::refresh_locked(std::string& error) {
    Account* a = active_locked();
    if (!a) {
        error = "signed out";
        return false;
    }
    std::string id = a->client_id.empty() ? fallback_id() : a->client_id;
    std::string secret = a->client_secret.empty() ? fallback_secret() : a->client_secret;
    int status = 0;
    json j = post_form(kTokenUrl,
                       "grant_type=refresh_token&refresh_token=" + form_escape(a->refresh_token) +
                           "&client_id=" + form_escape(id) + "&client_secret=" + form_escape(secret),
                       status, error);
    if (!error.empty()) return false;
    if (status == 200 && j.is_object() && j.contains("access_token")) {
        access_token_ = j.value("access_token", "");
        access_expires_at_ = static_cast<int64_t>(time(nullptr)) + j.value("expires_in", 3600) - 60;
        return true;
    }
    error = oauth_error(j, status);
    if (status == 400 || status == 401) {
        // invalid_grant: this account revoked access or the client rotated; drop it and fall back to the next.
        OLOG("auth: refresh rejected (%s), removing account", error.c_str());
        accounts_.erase(std::remove_if(accounts_.begin(), accounts_.end(),
                                       [&](const Account& x) { return x.id == active_id_; }),
                        accounts_.end());
        active_id_ = accounts_.empty() ? std::string() : accounts_.front().id;
        access_token_.clear();
        access_expires_at_ = 0;
        save_locked();
    }
    return false;
}

std::string Auth::access_token(std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    Account* a = active_locked();
    if (!a || a->refresh_token.empty()) return "";
    if (access_token_.empty() || static_cast<int64_t>(time(nullptr)) >= access_expires_at_) {
        if (!refresh_locked(error)) return "";
    }
    return access_token_;
}

bool Auth::signed_in() {
    std::lock_guard<std::mutex> lock(mutex_);
    Account* a = active_locked();
    return a && !a->refresh_token.empty();
}

void Auth::sign_out() {
    std::lock_guard<std::mutex> lock(mutex_);
    accounts_.erase(std::remove_if(accounts_.begin(), accounts_.end(),
                                   [&](const Account& x) { return x.id == active_id_; }),
                    accounts_.end());
    active_id_ = accounts_.empty() ? std::string() : accounts_.front().id;
    access_token_.clear();
    access_expires_at_ = 0;
    save_locked();
    OLOG("auth: signed out (%zu account(s) left)", accounts_.size());
}

std::vector<Account> Auth::accounts() {
    std::lock_guard<std::mutex> lock(mutex_);
    return accounts_;
}

std::string Auth::active_id() {
    std::lock_guard<std::mutex> lock(mutex_);
    return active_id_;
}

bool Auth::switch_to(const std::string& id) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& a : accounts_) {
        if (a.id == id) {
            active_id_ = id;
            access_token_.clear();
            access_expires_at_ = 0;
            save_locked();
            OLOG("auth: switched account");
            return true;
        }
    }
    return false;
}

void Auth::cache_profile(const std::string& id, const std::string& name, const std::string& handle, const std::string& photo) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& a : accounts_) {
        if (a.id == id) {
            a.name = name;
            a.handle = handle;
            a.photo = photo;
            save_locked();
            return;
        }
    }
}

Auth& shared_auth() {
    static Auth auth;
    return auth;
}

std::string prefilled_verification_url(const std::string& verification_url, const std::string& user_code) {
    std::string url = verification_url.empty() ? "https://www.google.com/device" : verification_url;
    if (url.find("://") == std::string::npos) url = "https://" + url;
    url += url.find('?') == std::string::npos ? '?' : '&';
    return url + "user_code=" + form_escape(user_code);
}

}  // namespace omega::yt
