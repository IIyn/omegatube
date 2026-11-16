#!/usr/bin/env python3
"""Adds or updates an official plugin in the site's index (gh-pages: plugins/index.json), see docs/plugins.md.

Run it after trying the plugin on a TV, then commit and push the gh-pages worktree:

  tools/plugin-index/add_plugin.py https://github.com/someone/x-omegatube-plugin/releases/download/v1.2.0/plugin.zip
  tools/plugin-index/add_plugin.py --remove io.github.someone.x

The entry pins that exact file (url + sha256): the app installs it only if the download has that hash. Its id,
name, version, sdk and description come from the zip's plugin.json (and strings.json); its screenshot (plugin.json
"preview", or --preview) is copied into plugins/img/ so that neither the site nor the app loads third-party images.
"""
import argparse
import datetime
import hashlib
import io
import json
import os
import posixpath
import re
import sys
import urllib.request
import zipfile

SDK_MAJOR = 1
ID = re.compile(r"^[a-z0-9]+(\.[a-z0-9_-]+){1,6}$")
VERSION = re.compile(r"^\d{1,6}(\.\d{1,6}){0,3}([-+][0-9A-Za-z.-]{1,20})?$")
SDK = re.compile(r"^(\d{1,3})\.(\d{1,3})$")
IMAGE_EXT = (".png", ".jpg", ".jpeg", ".webp")
MAX_ZIP = 20 << 20


def fail(message):
    sys.exit("error: " + message)


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": "OmegaTube plugin index"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read(MAX_ZIP + 1)
    if len(data) > MAX_ZIP:
        fail("larger than 20 MB")
    return data


def package_files(zf):
    """plugin.json at the root, or in a single top folder (as the app accepts it): {relative name: ZipInfo}."""
    names = [i.filename for i in zf.infolist() if not i.is_dir()]
    if "plugin.json" in names:
        prefix = ""
    else:
        tops = {n.split("/", 1)[0] + "/" for n in names if n.count("/") == 1 and n.endswith("/plugin.json")}
        if len(tops) != 1:
            fail("no plugin.json in the zip")
        prefix = tops.pop()
    return {i.filename[len(prefix):]: i for i in zf.infolist() if not i.is_dir() and i.filename.startswith(prefix)}


def read_json(zf, files, name):
    if name not in files:
        return None
    return json.loads(zf.read(files[name]).decode("utf-8"))


def localized(strings, text, lang):
    if not isinstance(text, str) or not text.startswith("@"):
        return text if isinstance(text, str) else ""
    key = text[1:]
    for lang_key in (lang, "en"):
        value = ((strings or {}).get(lang_key) or {}).get(key)
        if isinstance(value, str):
            return value
    return key


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("url", nargs="?", help="the release asset of the validated version (never .../latest/...)")
    parser.add_argument("--site", default=os.path.join(os.path.dirname(__file__), "..", "..", "..", "omegatube-gh-pages"),
                        help="the gh-pages worktree (default: ../omegatube-gh-pages next to the repository)")
    parser.add_argument("--preview", help="a screenshot to use instead of the package's own")
    parser.add_argument("--homepage", help="the plugin's page (default: plugin.json homepage)")
    parser.add_argument("--remove", metavar="ID", help="remove this plugin from the index")
    args = parser.parse_args()

    index_path = os.path.join(os.path.abspath(args.site), "plugins", "index.json")
    if not os.path.isfile(index_path):
        fail("no plugins/index.json in " + args.site)
    with open(index_path, encoding="utf-8") as f:
        index = json.load(f)
    plugins = index.setdefault("plugins", [])

    if args.remove:
        kept = [p for p in plugins if p.get("id") != args.remove]
        if len(kept) == len(plugins):
            fail(args.remove + " is not in the index")
        index["plugins"] = kept
        write(index_path, index)
        print("removed " + args.remove + " (its image in plugins/img/ can be deleted)")
        return

    if not args.url or not args.url.startswith("https://"):
        fail("give the https:// address of the release zip")
    if "/latest/" in args.url:
        fail("pin a version: use the release's own download URL, not .../latest/...")

    data = download(args.url)
    sha256 = hashlib.sha256(data).hexdigest()
    zf = zipfile.ZipFile(io.BytesIO(data))
    files = package_files(zf)
    manifest = read_json(zf, files, "plugin.json") or {}
    strings = read_json(zf, files, "strings.json")

    plugin_id = manifest.get("id", "")
    version = manifest.get("version", "")
    sdk = manifest.get("sdk", "")
    if not ID.match(plugin_id) or plugin_id == "omega":
        fail("bad id in plugin.json: %r" % plugin_id)
    if not VERSION.match(version):
        fail("bad version in plugin.json: %r" % version)
    m = SDK.match(sdk)
    if not m or int(m.group(1)) != SDK_MAJOR:
        fail("sdk %r: this app reads SDK %d.x" % (sdk, SDK_MAJOR))

    name = localized(strings, manifest.get("name"), "en").strip()
    if not name:
        fail("no name in plugin.json")
    description = {lang: localized(strings, manifest.get("description"), lang).strip() for lang in ("en", "fr")}
    if description["fr"] == description["en"]:
        description = description["en"]

    entry = {
        "id": plugin_id,
        "name": name,
        "author": localized(strings, manifest.get("author"), "en").strip(),
        "description": description,
        "homepage": args.homepage or manifest.get("homepage", ""),
        "version": version,
        "sdk": sdk,
        "url": args.url,
        "sha256": sha256,
        "validated": datetime.date.today().isoformat(),
    }

    # Screenshot, copied next to the page.
    img_dir = os.path.join(os.path.dirname(index_path), "img")
    if args.preview:
        ext = os.path.splitext(args.preview)[1].lower()
        with open(args.preview, "rb") as f:
            image = f.read()
    else:
        preview = manifest.get("preview")
        image, ext = None, ""
        if isinstance(preview, str):
            preview = posixpath.normpath(preview)
            ext = os.path.splitext(preview)[1].lower()
            if preview in files and not preview.startswith(".."):
                image = zf.read(files[preview])
    if image is not None and ext in IMAGE_EXT:
        os.makedirs(img_dir, exist_ok=True)
        image_name = plugin_id + ext
        with open(os.path.join(img_dir, image_name), "wb") as f:
            f.write(image)
        entry["preview"] = "img/" + image_name
    else:
        print("warning: no screenshot (plugin.json \"preview\" or --preview)")

    old = next((p for p in plugins if p.get("id") == plugin_id), None)
    if old is not None:
        plugins[plugins.index(old)] = entry
        print("updated %s: %s -> %s" % (plugin_id, old.get("version"), version))
    else:
        plugins.append(entry)
        print("added %s %s" % (plugin_id, version))
    write(index_path, index)
    print("sha256 " + sha256)
    print("now review, commit and push the gh-pages worktree")


def write(path, index):
    index["format"] = 1
    index["plugins"].sort(key=lambda p: p.get("name", "").lower())
    with open(path, "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
