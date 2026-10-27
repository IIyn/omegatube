#!/usr/bin/env python3
"""Dev probe: which InnerTube clients return playable stream URLs right now, and can they be
downloaded past the first megabytes? Mirrors the client set yt-dlp uses by default.

Usage: python3 -I tools/yt-probe/probe.py [videoId]
"""
import json
import re
import sys
import urllib.error
import urllib.request

VIDEO = sys.argv[1] if len(sys.argv) > 1 else "aqz-KE-bpKQ"
WEB_UA = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"

CLIENTS = {
    "visionos": ({"clientName": "VISIONOS", "clientVersion": "1.02", "deviceMake": "Apple",
                  "deviceModel": "RealityDevice17,1",
                  "userAgent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 "
                               "(KHTML, like Gecko) Version/26.0 Safari/605.1.15",
                  "osName": "visionOS", "osVersion": "26.5.23O471"}, 101),
    "tv_samsung": ({"clientName": "TVHTML5", "clientVersion": "5.20260707", "deviceMake": "Samsung",
                    "deviceModel": "UKS9800",
                    "userAgent": "Mozilla/5.0 (SMART-TV; Linux; Tizen 2.4.0) AppleWebKit/538.1 "
                                 "(KHTML, like Gecko) Version/2.4.0 TV Safari/538.1",
                    "osName": "Tizen", "osVersion": "2.4.0"}, 7),
}


def fetch(url, headers=None, data=None, timeout=20):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def visitor_data():
    page = fetch("https://www.youtube.com/", {"User-Agent": WEB_UA, "Accept-Language": "en-US"}).decode("utf-8", "replace")
    m = re.search(r'"VISITOR_DATA":"([^"]+)"', page)
    return m.group(1) if m else None


def signature_timestamp():
    page = fetch("https://www.youtube.com/tv", {"User-Agent": CLIENTS["tv_samsung"][0]["userAgent"]}).decode("utf-8", "replace")
    m = re.search(r'/s/player/[A-Za-z0-9_-]+/[^"\']*?\.js', page)
    if not m:
        return None, None
    js_url = "https://www.youtube.com" + m.group(0)
    js = fetch(js_url, {"User-Agent": WEB_UA}, timeout=60).decode("utf-8", "replace")
    sts = re.search(r'(?:signatureTimestamp|sts)\s*:\s*(\d{5})', js)
    return (int(sts.group(1)) if sts else None), js_url


def player(name, client, cid, visitor, sts):
    body = {"context": {"client": dict(client, hl="en", gl="US", visitorData=visitor)}, "videoId": VIDEO,
            "contentCheckOk": True, "racyCheckOk": True}
    if sts:
        body["playbackContext"] = {"contentPlaybackContext": {"html5Preference": "HTML5_PREF_WANTS",
                                                              "signatureTimestamp": sts}}
    headers = {"Content-Type": "application/json", "User-Agent": client["userAgent"],
               "X-YouTube-Client-Name": str(cid), "X-YouTube-Client-Version": client["clientVersion"],
               "Origin": "https://www.youtube.com"}
    if visitor:
        headers["X-Goog-Visitor-Id"] = visitor
    return json.loads(fetch("https://www.youtube.com/youtubei/v1/player?prettyPrint=false", headers,
                            json.dumps(body).encode()))


def range_test(fmt, ua):
    """Downloads 256 KB at the start, middle and end of the stream."""
    size = int(fmt.get("contentLength", 0))
    results = []
    for at in (0, size // 2, max(size - 262144, 0)):
        try:
            got = len(fetch(fmt["url"] + f"&range={at}-{at + 262143}", {"User-Agent": ua}, timeout=30))
            results.append(f"{at * 100 // max(size, 1)}%:OK")
        except urllib.error.HTTPError as e:
            results.append(f"{at * 100 // max(size, 1)}%:{e.code}")
    return " ".join(results)


def main():
    visitor = visitor_data()
    sts, js_url = signature_timestamp()
    print(f"video={VIDEO} visitorData={'yes' if visitor else 'NO'} signatureTimestamp={sts} player={js_url}")
    for name, (client, cid) in CLIENTS.items():
        try:
            r = player(name, client, cid, visitor, sts if name.startswith("tv") else None)
        except urllib.error.HTTPError as e:
            print(f"{name:11} HTTP {e.code}")
            continue
        ps = r.get("playabilityStatus", {})
        fmts = r.get("streamingData", {}).get("adaptiveFormats", [])
        direct = {f["itag"]: f for f in fmts if "url" in f}
        ciphered = [f for f in fmts if "signatureCipher" in f]
        print(f"{name:11} status={ps.get('status')} reason={ps.get('reason', '')!r} formats={len(fmts)} "
              f"direct={len(direct)} ciphered={len(ciphered)} itags={sorted({f['itag'] for f in fmts})}")
        for itag in (137, 299, 140, 251):
            if itag in direct:
                has_n = "&n=" in direct[itag]["url"]
                print(f"{'':11} itag {itag}: {range_test(direct[itag], client['userAgent'])}"
                      f"{'  (url has n= param: throttled until deciphered)' if has_n else ''}")
        with open(f"/tmp/omegatube-probe-{name}.json", "w") as f:
            json.dump(r, f)


if __name__ == "__main__":
    main()
