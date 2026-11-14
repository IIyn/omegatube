#!/usr/bin/env python3
"""Renders two images of the player (app/OmegaTube/res/images/): stall_ring.png, the buffering ring, and
playhead_dot.png, the round head of the progress bar (Tizen 5.5 has no CornerRadius, so a plain rounded View is square).

stall_ring.png:

A faint full circle with a bright 110 degree arc (round ends) starting at 12 o'clock. The app draws it around the
playhead dot of the progress bar and turns it; Tizen 5.5 has no vector arcs, so it is an image.

Usage: python3 tools/stall-ring/make_stall_ring.py
"""
import os

import numpy as np
from PIL import Image

SIZE = 96
SS = 4                  # supersampling for anti-aliasing
RADIUS = 41.0           # centre line of the stroke
STROKE = 8.0
ARC_DEG = 110.0
TRACK_ALPHA = 0.28

IMAGES = os.path.join(os.path.dirname(__file__), "..", "..", "app", "OmegaTube", "res", "images")
OUT = os.path.join(IMAGES, "stall_ring.png")
DOT = os.path.join(IMAGES, "playhead_dot.png")


def main():
    n = SIZE * SS
    ys, xs = np.mgrid[0:n, 0:n]
    px = (xs + 0.5) / SS - SIZE / 2.0
    py = (ys + 0.5) / SS - SIZE / 2.0
    r = np.hypot(px, py)
    ring = np.abs(r - RADIUS) <= STROKE / 2.0
    # angle clockwise from 12 o'clock
    theta = (np.degrees(np.arctan2(px, -py)) + 360.0) % 360.0
    arc = ring & (theta <= ARC_DEG)
    for deg in (0.0, ARC_DEG):                       # round caps: discs at both ends of the arc
        a = np.radians(deg)
        cx, cy = RADIUS * np.sin(a), -RADIUS * np.cos(a)
        arc |= np.hypot(px - cx, py - cy) <= STROKE / 2.0
    alpha = np.where(arc, 1.0, np.where(ring, TRACK_ALPHA, 0.0))
    alpha = alpha.reshape(SIZE, SS, SIZE, SS).mean(axis=(1, 3))
    img = np.full((SIZE, SIZE, 4), 255, np.uint8)
    img[..., 3] = np.clip(alpha * 255 + 0.5, 0, 255).astype(np.uint8)
    Image.fromarray(img, "RGBA").save(OUT, optimize=True)
    print("wrote", os.path.relpath(OUT))
    # filled white circle (tinted by the app), anti-aliased
    d = np.hypot(px, py) <= SIZE / 2.0 - 1.0
    a = d.reshape(SIZE, SS, SIZE, SS).mean(axis=(1, 3))
    dot = np.full((SIZE, SIZE, 4), 255, np.uint8)
    dot[..., 3] = np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8)
    Image.fromarray(dot, "RGBA").save(DOT, optimize=True)
    print("wrote", os.path.relpath(DOT))


if __name__ == "__main__":
    main()
