# Third-party notices

OmegaTube reuses the following work. Thanks to their authors.

| What | Where | License |
| --- | --- | --- |
| [ytps5-sponsorblock](https://github.com/animatian112/ytps5-sponsorblock) (C) 2026 BlackBearReloaded and contributors: approach for the TV sign-in (device flow, TV client id from the YouTube TV bundle), InnerTube client choices and the SmartTube-style UI layout | `core/src/yt/auth.cpp`, `core/src/yt/innertube.cpp`, `core/src/yt/watch.cpp` (watch-history pings, ported), `app/OmegaTube/Ui/` | GPL-3.0-or-later |
| [SmartTube](https://github.com/yuliskov/SmartTube) (C) 2020-present yuliskov: interface the UI follows, and the icons | `app/OmegaTube/res/icons/` (see `LICENSE-SmartTube.txt` there) | MIT |
| [Mbed TLS](https://github.com/Mbed-TLS/mbedtls) 3.6 | fetched by `scripts/fetch-deps.sh`, linked into the core | Apache-2.0 |
| [nlohmann/json](https://github.com/nlohmann/json) 3.12 | fetched by `scripts/fetch-deps.sh` | MIT |
| [QR Code generator](https://github.com/nayuki/QR-Code-generator) by Project Nayuki | fetched by `scripts/fetch-deps.sh` | MIT |
| Mozilla CA certificate bundle, as packaged by [curl](https://curl.se/docs/caextract.html) | fetched by `scripts/fetch-deps.sh`, embedded in the core | MPL-2.0 |
| [SponsorBlock](https://sponsor.ajay.app) segment data | queried at runtime | CC BY-NC-SA 4.0 (data) |

The omega logo (`tools/branding/make_logo.py`) is rendered with the DejaVu Sans Bold font (Bitstream Vera / DejaVu license).

YouTube is a trademark of Google LLC. OmegaTube is an independent project, not affiliated with Google or YouTube.
