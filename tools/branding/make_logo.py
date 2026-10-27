#!/usr/bin/env python3
"""Renders the OmegaTube logo: a red rounded rectangle with a white omega.

Usage: python3 -I tools/branding/make_logo.py <font.ttf> <out_dir>
Writes omegatube.png (app icon, 512x512) and omegatube-rail.png (rail, 128x128).
"""
import sys

from PIL import Image, ImageDraw, ImageFont

RED = (229, 45, 39, 255)
WHITE = (255, 255, 255, 255)


def logo(size, font_path):
    scale = 4  # supersample for smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 16:11 rounded "screen", centred
    w, h = int(s * 0.92), int(s * 0.64)
    x0, y0 = (s - w) // 2, (s - h) // 2
    d.rounded_rectangle((x0, y0, x0 + w, y0 + h), radius=int(h * 0.26), fill=RED)
    font = ImageFont.truetype(font_path, int(h * 0.78))
    box = d.textbbox((0, 0), "Ω", font=font)
    tw, th = box[2] - box[0], box[3] - box[1]
    d.text((s / 2 - tw / 2 - box[0], s / 2 - th / 2 - box[1]), "Ω", font=font, fill=WHITE)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    font, out = sys.argv[1], sys.argv[2]
    logo(512, font).save(f"{out}/omegatube.png")
    logo(128, font).save(f"{out}/omegatube-rail.png")
