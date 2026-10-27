#pragma once

#include <mutex>
#include <string>

namespace omega::yt {

struct DeviceCode {
    std::string device_code, user_code, verification_url;
    int interval_s = 5;
    int expires_in_s = 1800;
};

enum class PollResult { Pending, SlowDown, Done, Denied, Expired, Failed };

// Google OAuth 2.0 device flow with the YouTube TV app's client (the only sign-in YouTube offers TVs).
// Approach from youtube-tv / ytps5-sponsorblock (C) 2026 BlackBearReloaded, GPL-3.0-or-later.
class Auth {
public:
    // Loads the saved refresh token (if any) from token_path.
    void init(const std::string& token_path);

    bool begin(DeviceCode& out, std::string& error);
    PollResult poll(const DeviceCode& code, std::string& error);
    void sign_out();
    bool signed_in();

    // A valid access token (refreshed when close to expiry); empty when signed out or on failure.
    std::string access_token(std::string& error);

private:
    bool ensure_identity(std::string& error);
    bool refresh_locked(std::string& error);
    void save_locked();

    std::mutex mutex_;
    std::string path_;
    std::string client_id_, client_secret_;    // pair the current session signs in with
    std::string token_client_id_, token_client_secret_;  // pair that issued the saved token
    std::string refresh_token_, access_token_;
    int64_t access_expires_at_ = 0;  // unix seconds
};

Auth& shared_auth();

// The device page with the code pre-filled: the QR target, so a phone only has to confirm.
std::string prefilled_verification_url(const std::string& verification_url, const std::string& user_code);

}  // namespace omega::yt
