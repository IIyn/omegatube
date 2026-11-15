#!/usr/bin/env python3
"""Previews the home screen's layout off the TV, and checks it numerically.

The app's interface is C# on Tizen.NUI, which only runs on a TV (or the Tizen emulator, see
scripts/emulator.sh). This tool rebuilds the *layout* of the three uiStyle looks from the numbers in
the C# sources — Theme.cs, Card.cs, FeedGrid.cs — so a geometry change can be seen and measured on a
laptop before anything is signed and pushed to a TV.

The constants are parsed out of the sources rather than copied here: a preview that drifts from the
code it previews is worse than no preview. When a style's constants are missing (a style that is not
implemented yet) the run says so instead of guessing.

    tools/ui-preview/preview.py                        # omega, glorious, tube: PNGs + checks
    tools/ui-preview/preview.py --styles tube          # one style
    tools/ui-preview/preview.py --check --no-shot      # numbers only, no browser
    tools/ui-preview/preview.py --seed my-feed.json    # another seeded feed (same schema as seed.json)

Output goes to tools/ui-preview/out/ (git-ignored): <style>-home.png and <style>-grid.png, plus
<style>.html for the same thing in a browser. Exit code 1 when a check fails.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
UI = os.path.join(ROOT, "app", "OmegaTube", "Ui")

SCREEN_W, SCREEN_H = 1920, 1080          # the layout the whole app is drawn in (Theme.cs)
LINE = 1.45                              # Tizen's real line height (measured on the TV: 2 lines of 30px need >80px)
CHAR_W = 0.52                            # mean advance width as a multiple of the font size
MAX_STILL = (1280, 720)                  # the largest thumbnail the core asks YouTube for (hq720 / maxresdefault)
RES = os.path.join(ROOT, "app", "OmegaTube", "res", "images")
WIN_W, WIN_H = SCREEN_W, SCREEN_H          # the window the page is scaled into (Screen.FitToWindow)

CHROME = ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
          "/Applications/Chromium.app/Contents/MacOS/Chromium",
          shutil.which("google-chrome") or "", shutil.which("chromium") or ""]


# ---------------------------------------------------------------------------- constants, from the C#

DECL = re.compile(r"^\s*(?:public|private)\s+const\s+(?:int|float)\s+(.+?);\s*(?://.*)?$", re.M)
STATIC_INT = re.compile(r"^\s*(?:public|private)\s+static\s+int\s+(\w+)\s*=>\s*(.+?);\s*(?://.*)?$", re.M)
TERNARY = re.compile(r"^\s*Theme\.Tube\s*\?\s*(.+?)\s*:\s*(.+?)\s*$")


def _eval(expr, tube, env):
    """One C# arithmetic expression: numbers, Theme.* / bare constant names, `34f`, a Theme.Tube ?: ."""
    e = expr.strip()
    m = TERNARY.match(e)
    if m:
        e = m.group(1) if tube else m.group(2)
    e = re.sub(r"\b(\d+(?:\.\d+)?)f\b", r"\1", e)          # 34f -> 34
    e = e.replace("Theme.", "")
    for name, value in env.items():
        e = re.sub(r"\b%s\b" % re.escape(name), repr(value), e)
    if re.search(r"[A-Za-z_]", e):
        raise NameError("unknown name in: " + expr.strip())
    return eval(e, {"__builtins__": {}}, {})               # arithmetic only


def parse_consts():
    """{name: (omega, tube)} for every int/float constant and layout property in the UI sources."""
    out, env = {}, {}
    files = ["Theme.cs", "Card.cs", "FeedGrid.cs", "FeedRow.cs", "PlayerScreen.cs"]
    for tube in (False, True):
        local = {}
        for name in files:
            path = os.path.join(UI, name)
            if not os.path.exists(path):
                continue
            text = open(path, encoding="utf-8").read()
            for decl in DECL.findall(text):
                for part in decl.split(","):
                    if "=" not in part:
                        continue
                    key, expr = part.split("=", 1)
                    try:
                        local[key.strip()] = _eval(expr, tube, local)
                    except (NameError, SyntaxError):
                        pass        # a constant that reads something we do not model (rare, not layout)
            for key, expr in STATIC_INT.findall(text):
                try:
                    local[key] = _eval(expr, tube, local)
                except (NameError, SyntaxError):
                    pass
        for key, value in local.items():
            if key not in out:
                out[key] = [None, None]
            out[key][0 if not tube else 1] = value
    return out


# ---------------------------------------------------------------------------- the thumbnail mask

def mask_checks():
    """The card's alpha mask against the still it clips.

    Widgets.MaskedImage sets ImageVisualProperty.CropToMask, which crops the image to the *mask's pixel
    rectangle* before scaling it to the card. A mask smaller than the still therefore zooms the picture (the TV
    showed the top-left 400x225 corner of a 1280x720 still at ~3x, which is what "thumbnails look zoomed in"
    was), and only looked right while the core asked for a 320x180 file that the crop could not narrow.
    """
    lines, failed = [], []
    # Whatever masks Card.cs can draw a thumbnail through. A mask ending _hd is the Sharp path (a 1280x720 still), so
    # it has to cover that still; the small one is the Data-saver path, where the 320x180 file cannot be cropped, so
    # it is reported, not failed.
    names = set()
    card = open(os.path.join(UI, "Card.cs"), encoding="utf-8").read()
    for m in re.finditer(r'"(round_mask\w*)"', card):
        names.add(m.group(1))
    if not names:
        names = {"round_mask_hd"}
    for name in sorted(names):
        path = os.path.join(RES, name + ".png")
        if not os.path.exists(path):
            lines.append("FAIL  " + f"{name}.png missing — run tools/round-mask/make_mask.py")
            failed.append(f"{name}.png missing")
            continue
        from PIL import Image
        w, h = Image.open(path).size
        covers = w >= MAX_STILL[0] and h >= MAX_STILL[1]
        if name.endswith("_hd"):
            lines.append(("PASS  " if covers else "FAIL  ") +
                         f"{name} {w}x{h} covers the {MAX_STILL[0]}x{MAX_STILL[1]} still "
                         f"(CropToMask cannot crop it)")
            if not covers:
                failed.append(f"{name} {w}x{h} is smaller than the {MAX_STILL}: Sharp thumbnails would be zoomed")
        else:
            lines.append("      " + f"{name} {w}x{h} — the Data-saver path (a 320x180 still fits inside it)")
        aspect = w / h
        ok = abs(aspect - 16 / 9) < 0.01
        lines.append(("PASS  " if ok else "FAIL  ") + f"{name} is 16:9 (aspect {aspect:.3f})")
        if not ok:
            failed.append(f"{name} is not 16:9")
    return lines, failed


# ---------------------------------------------------------------------------- the styles

def style_model(style, consts):
    """The numbers one uiStyle draws with, or SystemExit when its constants are not in the sources."""
    def get(name, index=None):
        if name not in consts or consts[name][index if index is not None else 0] is None:
            raise SystemExit(f"uiStyle '{style}' needs {name} in app/OmegaTube/Ui/*.cs — not found.\n"
                             f"Implement the style first (Theme.cs), then preview it.")
        return consts[name][index if index is not None else 0]

    if style == "omega":
        m = dict(columns=get("Columns"), card_w=get("CardWidth"), gap=get("CardGap"),
                 info_h=get("CardInfoHeight"), row_h=get("RowHeight"), inset=get("FocusMargin"),
                 look="classic")
    elif style == "glorious":
        m = dict(columns=get("GridColumns"), card_w=get("GridCardWidth"), gap=get("GridGap"),
                 info_h=get("MetaRowH"), row_h=get("GloriousRowHeight"), inset=get("GloriousInset"),
                 look="glass")
    elif style == "tube":
        m = dict(columns=get("Columns", 1), card_w=get("TubeCardWidth"), gap=get("TubeCardGap"),
                 info_h=get("TubeCardInfoHeight"), row_h=get("TubeRowHeight"), inset=get("FocusMargin"),
                 look="tube")
    else:
        raise SystemExit(f"unknown style: {style}")

    m["style"] = style
    m["rail"] = get("TubeRailWidth", 1) if style == "tube" else get("RailWidth")
    m["focus_scale"] = get("FocusScale")
    m["title_size"] = get("BodySize") + (6 if style == "tube" else 0)
    m["meta_size"] = get("SmallSize") + (3 if style == "tube" else 0)
    m["strip_y"] = get("StripY")                 # a row's header sits at 0, its cards start here
    m["body_size"] = get("BodySize")
    m["small_size"] = get("SmallSize")
    m["card_info_h"] = get("CardInfoHeight")
    m["heading_size"] = get("TitleSize") - 4
    # The player: its chrome lifts so the suggestions row lands at a fixed screen y, and the row is sized for the
    # frame (570 + row <= 1080) — the Tube look draws its own smaller card there, with the type scaled down.
    idx = 1 if style == "tube" else 0
    if style == "tube":
        pw = get("TubePlayerCardWidth", idx)
        ph = int(pw * 9 / 16) + get("TubeCardInfoSmall", idx)
        prow = get("TubePlayerRowHeight", idx)
        plook, ptitle, pmeta, pinfo = "tube", get("BodySize", idx) + 2, get("SmallSize", idx), get("TubeCardInfoSmall", idx)
    else:
        pw = get("CardWidth")
        ph = int(pw * 9 / 16) + get("CardInfoHeight")
        prow = get("RowHeight")
        plook, ptitle, pmeta, pinfo = "classic", get("BodySize"), get("SmallSize"), get("CardInfoHeight")
    lift = get("ChromeLift", idx)
    m["player"] = dict(
        row_h=prow, lift=lift,
        show_y=get("SuggestionsY", idx) - lift, section_y=get("SectionY", idx) - lift,
        title_y=get("TitleY") - lift, track_y=get("TrackY") - lift, buttons_y=get("ButtonsY") - lift,
        card_w=pw, gap=get("TubeCardGap", 1) if style == "tube" else get("CardGap"), card_h=ph,
        look=plook, title_size=ptitle, meta_size=pmeta, info_h=pinfo,
        strip_y=get("StripY"))
    m["badge_size"] = get("SmallSize") - 2
    m["radius"] = get("CardRadius")
    m["thumb_h"] = int(m["card_w"] * 9 / 16)
    m["card_h"] = m["thumb_h"] + int(m["info_h"])
    return m


# ---------------------------------------------------------------------------- checks

def checks(m, seed):
    """The layout facts worth knowing before a build goes to a TV. Returns (lines, failed).

    Only geometry fails the run: a long title being ellipsised is the interface behaving, not a bug, so
    the text figures are reported as WARN — they are what to compare between looks.
    """
    lines, failed = [], []

    def check(ok, text):
        lines.append(("PASS  " if ok else "FAIL  ") + text)
        if not ok:
            failed.append(text)

    def warn(ok, text):
        lines.append(("PASS  " if ok else "WARN  ") + text)

    def note(text):
        lines.append("      " + text)

    check(abs(m["thumb_h"] - m["card_w"] * 9 / 16) < 1,
          f"thumbnail is 16:9 ({m['card_w']}x{m['thumb_h']})")
    check(m["row_h"] >= m["card_h"] + 8,
          f"row pitch {m['row_h']} clears the card {m['card_h']} ({(m['row_h'] - m['card_h'])}px spare)")
    zoomed = int(m["card_h"] * m["focus_scale"])
    check(m["row_h"] >= zoomed + 8,
          f"row pitch {m['row_h']} clears the focused card {zoomed}")
    # A row draws its heading at y=0 and its cards from StripY: the next row's heading must not land on the
    # previous row's title/channel line, which is what this catches.
    needs = m["strip_y"] + m["card_h"] + 16
    check(m["row_h"] >= needs,
          f"row pitch {m['row_h']} clears heading + cards ({m['strip_y']} + {m['card_h']} + 16 = {needs}; "
          f"{m['row_h'] - needs}px spare)")
    # How many cards a row actually shows. Omega/Tube rows start after the rail + 20 (HomeScreen.ContentX) + the
    # row's inset; Glorious positions its rows at x=0 with the nav as an overlay, so only its inset applies. The Tube
    # look is deliberately a 2-and-a-half wall, so its third card is expected to peek in from the edge.
    start = m["inset"] if m["style"] == "glorious" else m["rail"] + 20 + m["inset"]
    full = (SCREEN_W - start + m["gap"]) // (m["card_w"] + m["gap"])
    m["full_cards"] = full
    check(full == {"omega": 4, "glorious": 5, "tube": 2}[m["style"]], f"{full} full card(s) a row")
    if m["style"] == "tube":
        peek = SCREEN_W - (start + full * (m["card_w"] + m["gap"]))
        check(100 <= peek < m["card_w"], f"the next card peeks in by {peek}px")
    elif m["style"] == "glorious":
        note("glorious: rows start at the nav inset and run to the right edge by design (overlay nav)")
    else:
        right = start + full * m["card_w"] + (full - 1) * m["gap"]
        check(right <= SCREEN_W, f"{full} cards end at {right} ({SCREEN_W - right}px right margin)")
    # The grid (search, history) is its own layout: TubeColumns columns, which all have to fit.
    grid_w = (SCREEN_W - 2 * m["inset"] - (m["columns"] - 1) * m["gap"]) // m["columns"]
    check(m["inset"] * 2 + m["columns"] * grid_w + (m["columns"] - 1) * m["gap"] <= SCREEN_W,
          f"grid: {m['columns']} columns of {grid_w}px fit the width")

    # How much of a title survives: two lines at the title size, in the space the look gives it.
    inner = m["card_w"] - 2 * (14 if m["look"] == "tube" else 24)
    per_line = int(inner / (CHAR_W * m["title_size"]))
    capacity = per_line * (1 if m["look"] == "glass" else 2)
    longest = max((len(i["title"]) for i in seed["items"]), default=0)
    cut = [i for i in seed["items"] if len(i["title"]) > capacity]
    note(f"title: ~{per_line} chars a line, {capacity} over {1 if m['look'] == 'glass' else 2} line(s); "
         f"longest seeded title {longest}")
    warn(not cut, f"{len(cut)} of {len(seed['items'])} seeded title(s) exceed {capacity} chars and ellipsise"
                  + ("" if not cut else " -> " + ", ".join(f"#{seed['items'].index(i)}" for i in cut)))

    meta_per_line = int(inner / (CHAR_W * m["meta_size"]))
    meta_longest = max((len(" • ".join(x for x in (i["channel"], i["views"], i["published"]) if x))
                        for i in seed["items"]), default=0)
    warn(meta_longest <= meta_per_line,
         f"channel line: {meta_longest} chars longest of ~{meta_per_line} a line")

    # What the look puts under the thumbnail: the classic and tube looks carry the title there (the tube one adds
    # the channel and the views/age lines, as YouTube does), the glass look keeps the title on an overlay.
    if m["look"] == "glass":
        text_h = int(m["meta_size"] * LINE) + 16
    elif m["look"] == "tube":
        text_h = 2 * int(m["title_size"] * LINE) + 2 * int(m["meta_size"] * LINE) + 16
    else:
        text_h = 2 * int(m["title_size"] * LINE) + int(m["meta_size"] * LINE) + 16
    if m["look"] == "tube":
        check(text_h <= m["info_h"],
              f"text block {text_h}px fits the {m['info_h']}px reserved under the thumbnail")
    else:
        # Omega and Glorious are left as upstream has them; a too-short title box there ellipsises on one line.
        warn(text_h <= m["info_h"],
             f"text block {text_h}px in {m['info_h']}px (unchanged {m['style']} behaviour)")

    rows_on_screen = max(1, int((SCREEN_H - 120 + m["row_h"] // 2) // m["row_h"]))
    note(f"{rows_on_screen} row(s) of cards fit on the screen at {m['row_h']}px a row")
    return lines, failed


def checks_player(m):
    """The player's revealed state: the chrome lifts so the suggestions row shows, without moving the video's own
    controls off screen. This is what broke when the lift was derived from the home row pitch."""
    pl = m["player"]
    lines, failed = [], []

    def check(ok, text):
        lines.append(("PASS  " if ok else "FAIL  ") + text)
        if not ok:
            failed.append(text)

    check(pl["title_y"] >= 0, f"video title stays on screen when revealed (y={pl['title_y']})")
    check(pl["track_y"] >= 0, f"progress bar stays on screen (y={pl['track_y']})")
    bottom = pl["show_y"] + pl["row_h"]
    check(bottom <= SCREEN_H, f"suggestions row ends at {bottom} (screen {SCREEN_H})")
    check(pl["section_y"] >= bottom, f"description starts at {pl['section_y']}, below the row's {bottom}")
    # A row starts its cards at StripY, so a card taller than row - StripY spills into the next row.
    needs = pl["strip_y"] + pl["card_h"]
    check(needs <= pl["row_h"],
          f"cards ({pl['strip_y']} + {pl['card_h']} = {needs}) fit the {pl['row_h']}px row")
    # The bug this guards: deriving the player's lift/pitch from the home row made the reveal slide the video's
    # controls off screen. The player's row must stay independent of the wall's pitch.
    check(m["style"] != "tube" or pl["row_h"] != m["row_h"],
          f"the player's row ({pl['row_h']}px) is insulated from the home pitch ({m['row_h']}px)")
    return lines, failed


# ---------------------------------------------------------------------------- HTML

def esc(s):
    return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def card_html(item, m, focused):
    w, th, look = m["card_w"], m["thumb_h"], m["look"]
    scale = m["focus_scale"] if focused else 1
    badge = "LIVE" if item.get("live") else item.get("duration", "")
    badge_bg = "#e52d27" if item.get("live") else "rgba(0,0,0,.82)"
    pill = (f'<div class="pill" style="background:{badge_bg};font-size:{m["badge_size"]}px">{esc(badge)}</div>'
            if badge else "")
    progress = (f'<div class="progress" style="width:{int(w * item.get("watched", 0))}px"></div>'
                if item.get("watched", 0) > 0.01 else "")
    ring = 'box-shadow:0 0 0 3px #fff;' if (focused and look != "classic") else ""
    hue = (abs(hash(item["title"])) % 300)
    thumb = (f'<div class="thumb" style="width:{w}px;height:{th}px;border-radius:{m["radius"]}px;'
             f'background:linear-gradient(135deg,hsl({hue} 40% 32%),hsl({hue} 30% 18%));{ring}">'
             f'{pill}{progress}</div>')
    title_lines = 1 if look == "glass" else 2
    title = (f'<div class="title" style="font-size:{m["title_size"]}px;line-height:{LINE};'
             f'-webkit-line-clamp:{title_lines}">{esc(item["title"])}</div>')
    meta_text = " • ".join(x for x in (item["channel"], item["views"], item["published"]) if x)
    meta = f'<div class="meta" style="font-size:{m["meta_size"]}px;line-height:{LINE}">{esc(meta_text)}</div>'

    if look == "classic":
        body = (f'<div class="info" style="width:{w}px;height:{m["info_h"]}px;'
                f'background:{"#e3e3e3" if focused else "#1f1f1f"}">'
                f'<div style="color:{"#2a2a2a" if focused else "#f1f1f1"}">{title}</div>'
                f'<div style="color:{"#2a2a2a" if focused else "#aaaaaa"};margin-top:2px">{meta}</div></div>')
    elif look == "glass":
        body = (f'<div class="overlay" style="width:{w}px;position:relative;top:-{40}px;'
                f'{"display:block" if focused else "visibility:hidden"}">{title}</div>'
                f'<div style="height:{m["info_h"]}px">{meta}</div>')
    else:
        if look == "tube":
            # YouTube's three lines: title, channel, views/age.
            ch = (f'<div class="channel" style="font-size:{m["meta_size"]}px;'
                  f'line-height:{LINE}">{esc(item["channel"])}</div>')
            views = " • ".join(x for x in (item["views"], item["published"]) if x)
            vw = (f'<div class="views" style="font-size:{m["meta_size"]}px;line-height:{LINE}">{esc(views)}</div>')
            body = f'<div class="under" style="width:{w}px">{title}{ch}{vw}</div>'
        else:
            body = f'<div class="under" style="width:{w}px">{title}{meta}</div>'

    return (f'<div class="card" style="width:{w}px;height:{m["card_h"]}px;'
            f'transform:scale({scale});transform-origin:center;z-index:{9 if focused else 1}">'
            f'{thumb}{body}</div>')


def page_html(m, seed, view):
    cols, w, gap = m["columns"], m["card_w"], m["gap"]
    items = seed["items"]
    parts = []
    top = 96
    content_x = m["rail"] + 20          # HomeScreen.ContentX
    if view == "home":
        names = ["Recommended", "Thrillers and more", "Electronics components and more"]
        rows = [items[i:i + cols] for i in range(0, min(len(items), cols * 3), cols)]
        for r, row in enumerate(rows):
            y = top + r * m["row_h"]
            parts.append(f'<div class="heading" style="top:{y}px;left:{content_x + m["inset"]}px;'
                         f'font-size:{m["heading_size"]}px">{esc(names[r % len(names)])}</div>')
            # one card more than fits: the extra one is clipped by the canvas, which is YouTube's peek
            parts.append(f'<div class="row" style="top:{y + m["strip_y"]}px;left:{content_x + m["inset"]}px;'
                         f'gap:{gap}px">' + "".join(
                             card_html(it, m, focused=(r == 0 and c == 0))
                             for c, it in enumerate(row[:m.get("full_cards", cols) + 1])) + "</div>")
    elif view == "grid":
        rows = [items[i:i + cols] for i in range(0, len(items), cols)]
        for r, row in enumerate(rows):
            parts.append(f'<div class="row" style="top:{top + r * m["card_h"] + r * 40}px;left:{content_x + m["inset"]}px;'
                         f'gap:{gap}px">' + "".join(
                             card_html(it, m, focused=(r == 0 and c == 0)) for c, it in enumerate(row)) + "</div>")
    # The rail: icons only while the content has the focus, labels out when it is opened. Tube leaves the rows bare
    # with YouTube's light pill on the current one; Omega fills the current row with grey, so its rows show as pills.
    if m["style"] == "tube":
        rows = (f'<div class="navitem" style="width:{m["rail"] - 28}px;background:#e8e8e8"></div>'
                + "".join(f'<div class="navitem" style="width:{m["rail"] - 28}px;background:transparent"></div>'
                          for _ in range(5)))
    else:
        rows = (f'<div class="navitem" style="width:{m["rail"] - 28}px;background:#3a3a3a"></div>'
                + "".join(f'<div class="navitem" style="width:{m["rail"] - 28}px"></div>' for _ in range(5)))
    rail = (f'<div class="rail" style="width:{m["rail"]}px;background:{"#000" if m["style"] == "tube" else "#0b0b0b"}">'
            + rows + "</div>")
    if view == "player":
        pl = m["player"]
        parts.append(f'<div class="videoband" style="top:0;height:{SCREEN_H}px"></div>')
        parts.append(f'<div class="ctrl" style="top:{pl["title_y"]}px;left:80px;width:900px;height:40px"></div>')
        parts.append(f'<div class="ctrl" style="top:{pl["track_y"]}px;left:80px;width:1760px;height:6px"></div>')
        for i in range(4):
            parts.append(f'<div class="ctrl" style="top:{pl["buttons_y"]}px;left:{80 + i * 100}px;width:72px;height:72px;border-radius:36px"></div>')
        parts.append(f'<div class="heading" style="top:{pl["show_y"]}px;left:40px;font-size:{m["heading_size"]}px">'
                     f'Suggested videos</div>')
        row = items[:4]
        parts.append(f'<div class="row" style="top:{pl["show_y"] + pl["strip_y"]}px;left:{40 + m["inset"]}px;'
                     f'gap:{pl["gap"]}px">' + "".join(
                         # the player's row is always the classic card, whatever the interface style is
                         card_html(it, dict(m, look=pl["look"], card_w=pl["card_w"],
                                            thumb_h=int(pl["card_w"] * 9 / 16), card_h=pl["card_h"], gap=pl["gap"],
                                            info_h=pl["info_h"], title_size=pl["title_size"],
                                            meta_size=pl["meta_size"], radius=m["radius"]), focused=(c == 0))
                         for c, it in enumerate(row)) + "</div>")
        parts.append(f'<div class="sectionband" style="top:{pl["section_y"]}px"></div>')
    return f"""<!doctype html><meta charset="utf-8">
<style>
  * {{ box-sizing: border-box; }}
  html,body {{ margin:0; padding:0; background:#000; width:{WIN_W}px; height:{WIN_H}px;
               overflow:hidden; font-family:"Helvetica Neue",Helvetica,Arial,sans-serif;
               color:#f1f1f1; -webkit-font-smoothing:antialiased; }}
  .rail {{ position:absolute; left:0; top:0; height:{SCREEN_H}px; background:#0b0b0b; padding-top:40px;
           display:flex; flex-direction:column; align-items:center; gap:34px; }}
  .navitem {{ height:44px; border-radius:12px; background:#2a2a2a; }}
  .card {{ position:relative; }}
  .row {{ position:absolute; display:flex; align-items:flex-start; }}
  .thumb {{ position:relative; overflow:hidden; }}
  .pill {{ position:absolute; right:8px; bottom:10px; padding:1px 6px; border-radius:4px; color:#fff; }}
  .progress {{ position:absolute; left:0; bottom:0; height:5px; background:#e52d27; }}
  .title {{ display:-webkit-box; -webkit-box-orient:vertical; overflow:hidden; }}
  .meta {{ color:#aaa; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
  .channel {{ color:#c9c9c9; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
  .views {{ color:#aaa; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
  .info {{ border-radius:0 0 {m['radius']}px {m['radius']}px; padding:8px 12px; }}
  .overlay {{ background:rgba(20,20,20,.92); border-radius:10px; padding:8px 10px; }}
  .under {{ padding:12px 14px 0 14px; }}
  .hud {{ position:absolute; left:{m['rail'] + 40}px; top:18px; font-size:20px; color:#888;
          letter-spacing:.3px; }}
  .heading {{ position:absolute; color:#f1f1f1; font-weight:500; }}
  .videoband {{ position:absolute; left:0; width:{SCREEN_W}px; background:#101010; }}
  .ctrl {{ position:absolute; background:#222; border-radius:4px; }}
  .sectionband {{ position:absolute; left:{m.get('inset', 30) + 40}px; width:1200px; height:40px;
                  background:#2a2a2a; border-radius:8px; }}
</style>
<div id="page" style="position:absolute;left:0;top:0;width:{SCREEN_W}px;height:{SCREEN_H}px;
   transform:scale({min(WIN_W / SCREEN_W, WIN_H / SCREEN_H):.5f});transform-origin:top left">
<div class="hud">{m['style']} · {view} · {((SCREEN_W - (m['rail'] + 20 + m['inset']) + gap) // (w + gap))} a row
· card {w}x{m['card_h']} · thumb {w}x{m['thumb_h']}
 · gap {gap} · row {m['row_h']} · strip {m['strip_y']} · rail {m['rail']}</div>
{rail}
{''.join(parts)}
</div>
"""


# ---------------------------------------------------------------------------- main

def shot(html_path, png_path):
    chrome = next((c for c in CHROME if c and os.path.exists(c)), None)
    if not chrome:
        return False, "no Chrome/Chromium found: open the .html instead"
    cmd = [chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
           "--force-device-scale-factor=1", f"--window-size={WIN_W},{WIN_H}",
           "--virtual-time-budget=1500", f"--screenshot={png_path}", "file://" + html_path]
    r = subprocess.run(cmd, capture_output=True, text=True)
    ok = r.returncode == 0 and os.path.exists(png_path)
    return ok, ("" if ok else (r.stderr or r.stdout).strip().splitlines()[-1:] or ["chrome failed"])


def main():
    global WIN_W, WIN_H
    ap = argparse.ArgumentParser(description="Preview the OmegaTube home layout without a TV.")
    ap.add_argument("--styles", default="omega,glorious,tube")
    ap.add_argument("--seed", default=os.path.join(HERE, "seed.json"))
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--check", action="store_true", default=True)
    ap.add_argument("--no-check", dest="check", action="store_false")
    ap.add_argument("--no-shot", action="store_true")
    ap.add_argument("--views", default="home,grid,player")
    ap.add_argument("--screen", default=f"{SCREEN_W}x{SCREEN_H}",
                    help=f"window size to fit the {SCREEN_W}x{SCREEN_H} layout into (default: the layout size)")
    args = ap.parse_args()

    WIN_W, WIN_H = (int(v) for v in args.screen.lower().split("x"))
    fit = min(WIN_W / SCREEN_W, WIN_H / SCREEN_H)
    if (WIN_W, WIN_H) != (SCREEN_W, SCREEN_H):
        print(f"=== window {WIN_W}x{WIN_H}: the {SCREEN_W}x{SCREEN_H} page fits at scale {fit:.3f} "
              f"({int(SCREEN_W * fit)}x{int(SCREEN_H * fit)}, letterboxed) ===")
    seed = json.load(open(args.seed, encoding="utf-8"))
    consts = parse_consts()
    os.makedirs(args.out, exist_ok=True)

    print("=== thumbnail mask ===")
    mask_lines, mask_failed = mask_checks()
    print("\n".join(mask_lines))

    failed_all, skipped = [], []
    for style in [s.strip() for s in args.styles.split(",") if s.strip()]:
        try:
            m = style_model(style, consts)
        except SystemExit as e:
            skipped.append((style, str(e)))
            continue
        print(f"\n=== {style} ===")
        print(f"card {m['card_w']}x{m['card_h']} (thumb {m['card_w']}x{m['thumb_h']}, info {m['info_h']}) "
              f"· {m['columns']} a row · gap {m['gap']} · row {m['row_h']} · rail {m['rail']}")
        if args.check:
            lines, failed = checks(m, seed)
            print("\n".join(lines))
            failed_all += [f"{style}: {f}" for f in failed]
            print("      -- player, revealed --")
            plines, pfailed = checks_player(m)
            print("\n".join(plines))
            failed_all += [f"{style} player: {f}" for f in pfailed]
        for view in [v.strip() for v in args.views.split(",") if v.strip()]:
            html_path = os.path.join(args.out, f"{style}-{view}.html")
            open(html_path, "w", encoding="utf-8").write(page_html(m, seed, view))
            if args.no_shot:
                print(f"      -> {html_path}")
                continue
            png_path = os.path.join(args.out, f"{style}-{view}.png")
            ok, msg = shot(html_path, png_path)
            print(f"      -> {png_path}" if ok else f"      !! no screenshot: {msg}")

    for style, why in skipped:
        print(f"\n=== {style} ===\n      skipped: {why}")

    if mask_failed:
        failed_all += [f"mask: {f}" for f in mask_failed]
    if skipped and not failed_all:
        return 0
    if failed_all:
        print("\n" + "\n".join("FAIL  " + f for f in failed_all))
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
