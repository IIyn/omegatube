#pragma once

#include <mutex>
#include <string>
#include <vector>

namespace omega::yt {

struct DeviceCode {
    std::string device_code, user_code, verification_url;
    int interval_s = 5;
    int expires_in_s = 1800;
};

enum class PollResult { Pending, SlowDown, Done, Denied, Expired, Failed };

// One signed-in Google account: its OAuth token, the client pair that issued it, and a cached profile for the
// account picker (so the UI need not hit the network to draw avatars).
struct Account {
    std::string id;  // stable, assigned on sign-in, persisted
    std::string refresh_token, client_id, client_secret;
    std::string name, handle, photo;  // cached profile (may be empty until first fetched)
};

// Google OAuth 2.0 device flow with the YouTube TV app's client (the only sign-in YouTube offers TVs).
// Approach from youtube-tv / ytps5-sponsorblock (C) 2026 BlackBearReloaded, GPL-3.0-or-later.
//
// Holds any number of accounts and an "active" one; all token operations use the active account. The app can
// list them, switch between them, and add/remove (GitHub issue: multi-account switching).
class Auth {
public:
    // Loads the account store from store_path (e.g. <data>/accounts.json), migrating a legacy single-account
    // auth.json (same dir, or store_path itself if it is in the old format).
    void init(const std::string& store_path);

    bool begin(DeviceCode& out, std::string& error);
    PollResult poll(const DeviceCode& code, std::string& error);  // on success, appends a new active account
    void sign_out();   // removes the active account; the next one (if any) becomes active
    bool signed_in();  // an active account with a token

    // A valid access token for the active account (refreshed near expiry); empty when signed out or on failure.
    std::string access_token(std::string& error);

    // --- Multiple accounts ---
    std::vector<Account> accounts();     // snapshot of all signed-in accounts
    std::string active_id();
    bool switch_to(const std::string& id);
    void cache_profile(const std::string& id, const std::string& name, const std::string& handle, const std::string& photo);

private:
    bool ensure_identity(std::string& error);
    bool refresh_locked(std::string& error);
    void load_store_locked();
    void save_locked();
    Account* active_locked();
    std::string next_id_locked() const;

    std::mutex mutex_;
    std::string store_path_, legacy_path_;
    std::string client_id_, client_secret_;    // pair the current session signs in with
    std::vector<Account> accounts_;
    std::string active_id_;
    std::string access_token_;        // cached for the active account
    int64_t access_expires_at_ = 0;   // unix seconds
};

Auth& shared_auth();

// The device page with the code pre-filled: the QR target, so a phone only has to confirm.
std::string prefilled_verification_url(const std::string& verification_url, const std::string& user_code);

}  // namespace omega::yt
