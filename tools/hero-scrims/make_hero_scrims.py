#!/usr/bin/env python3
"""Renders the two darkening layers of the Glorious hero backdrop (app/OmegaTube/res/images/hero_left.png, hero_bottom.png).

Both are black with the gradient in the alpha channel and are stretched over the whole 1920x1080 screen by the app:
  hero_left   - solid at the left edge, easing out to clear by the middle (keeps the title legible);
  hero_bottom - dark across the lower band under the rows, clear in the upper screen.
They were DALi gradient visuals; those band on the TV and cost a full-screen shader pass each, so they are
pre-rendered (with a little noise dither to break up 8-bit steps), like the other scrims.

Usage: python3 tools/hero-scrims/make_hero_scrims.py
"""
import os

import numpy as np
from PIL import Image

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "app", "OmegaTube", "res", "images")


def ramp(t, stops):
    """Piecewise-linear alpha over t in 0..1 from (offset, alpha) stops."""
    xs, ys = zip(*stops)
    return np.interp(t, xs, ys)


def save(alpha, name, seed):
    rng = np.random.default_rng(seed)
    a8 = np.clip(alpha * 255 + rng.uniform(-0.5, 0.5, alpha.shape) + 0.5, 0, 255).astype(np.uint8)
    img = np.zeros(alpha.shape + (4,), np.uint8)
    img[..., 3] = a8
    path = os.path.join(OUT, name)
    Image.fromarray(img, "RGBA").save(path, optimize=True)
    print("wrote", os.path.relpath(path))


def main():
    # left: x = 0 (screen edge) .. 1 (right edge); alpha 1.0 -> 0.55 at 28% -> 0 at 50%
    w, h = 480, 16
    t = (np.arange(w) + 0.5) / w
    save(np.tile(ramp(t, [(0, 1.0), (0.28, 0.55), (0.5, 0.0), (1.0, 0.0)]), (h, 1)), "hero_left.png", 3)
    # bottom: 0 = bottom edge .. 1 = top edge; alpha 0.92 -> 0.15 at the middle -> 0 at the top
    w, h = 16, 270
    t = 1.0 - (np.arange(h) + 0.5) / h
    save(np.tile(ramp(t, [(0, 0.92), (0.5, 0.15), (1.0, 0.0)])[:, None], (1, w)), "hero_bottom.png", 5)


if __name__ == "__main__":
    main()
