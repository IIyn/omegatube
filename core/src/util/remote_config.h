#pragma once

#include <initializer_list>
#include <string>

#include <nlohmann/json.hpp>

namespace omega::cfg {

// Runtime parameters that track YouTube changes (InnerTube clients, OAuth fallback, endpoints).
// Layers: built-in default (repo's remote-config.json embedded at build time) < cached copy < copy
// fetched from GitHub at startup. Thread-safe.
void init(const std::string& cache_path);
bool refresh(std::string& error);  // network: fetch the repo's remote-config.json, cache it
nlohmann::json snapshot();

std::string str(std::initializer_list<const char*> path, const std::string& fallback = "");
int num(std::initializer_list<const char*> path, int fallback = 0);
nlohmann::json obj(std::initializer_list<const char*> path);

// User preferences forwarded by the shell (InnerTube hl/gl).
void set_locale(const std::string& language, const std::string& region);
std::string language();
std::string region();

// Latest GitHub release vs. this build: {"available":bool,"version","url","notes"} or {"error"}.
nlohmann::json check_update(const std::string& current_version);

}  // namespace omega::cfg
