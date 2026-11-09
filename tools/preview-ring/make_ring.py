#!/usr/bin/env python3
"""Renders the video-preview countdown ring as a sprite sheet (app/OmegaTube/res/images/preview_ring.png).

Frame k (0..FRAMES-1) is an arc sweeping clockwise from 12 o'clock to (k+1)/FRAMES of the circle, with a gradient
along it (faint tail -> bright, round head) over a soft dark shadow for legibility on bright thumbnails; no
background disc. The last frame is the full ring, which the app also spins as the "still loading" state.
Tizen 5.5 has no vector arcs (and no CornerRadius), so the app steps PixelArea over these pre-drawn frames.

Usage: python3 tools/preview-ring/make_ring.py
"""
import os

import numpy as np
from PIL import Image, ImageFilter

FRAMES = 90            # 3 s at 30 fps
COLS = 10
SIZE = 64              # frame size in the sheet
SS = 4                 # supersampling for anti-aliasing
RADIUS = 23.0          # centre line of the stroke
STROKE = 5.0
TAIL_ALPHA = 0.10
SHADOW_ALPHA = 0.45

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "app", "OmegaTube", "res", "images", "preview_ring.png")


def frame(sweep_deg):
    n = SIZE * SS
    c = n / 2.0
    y, x = np.mgrid[0:n, 0:n].astype(np.float64) + 0.5
    dx, dy = x - c, y - c
    r = np.hypot(dx, dy)
    theta = (np.degrees(np.arctan2(dx, -dy)) + 360.0) % 360.0   # clockwise from 12 o'clock
    R, half = RADIUS * SS, STROKE * SS / 2.0

    band = np.abs(r - R) <= half
    on_arc = band & (theta <= sweep_deg)
    t = np.clip(theta / max(sweep_deg, 1e-6), 0.0, 1.0)
    alpha = np.where(on_arc, TAIL_ALPHA + (1.0 - TAIL_ALPHA) * t ** 1.6, 0.0)

    # Round caps: bright head, faint tail.
    def cap(angle_deg, value):
        a = np.radians(angle_deg)
        hx, hy = c + R * np.sin(a), c - R * np.cos(a)
        return np.where(np.hypot(x - hx, y - hy) <= half, value, 0.0)

    alpha = np.maximum(alpha, cap(sweep_deg, 1.0))
    alpha = np.maximum(alpha, cap(0.0, TAIL_ALPHA))

    white = Image.fromarray((alpha * 255).astype(np.uint8), "L")
    # Shadow: the stroke's footprint, widened and blurred, under the white arc.
    footprint = ((alpha > 0) * 255).astype(np.uint8)
    shadow = Image.fromarray(footprint, "L").filter(ImageFilter.MaxFilter(9)).filter(ImageFilter.GaussianBlur(3 * SS))
    shadow = shadow.point(lambda v: int(v * SHADOW_ALPHA))

    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    img.putalpha(shadow)
    top = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    top.putalpha(white)
    img = Image.alpha_composite(img, top)
    return img.resize((SIZE, SIZE), Image.LANCZOS)


def main():
    rows = (FRAMES + COLS - 1) // COLS
    sheet = Image.new("RGBA", (COLS * SIZE, rows * SIZE), (0, 0, 0, 0))
    for k in range(FRAMES):
        sheet.paste(frame(360.0 * (k + 1) / FRAMES), ((k % COLS) * SIZE, (k // COLS) * SIZE))
    sheet.save(os.path.normpath(OUT), optimize=True)
    print("wrote", os.path.normpath(OUT), sheet.size, f"{FRAMES} frames, {COLS}x{rows}")


if __name__ == "__main__":
    main()
