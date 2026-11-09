#!/usr/bin/env python3
"""Renders the fade behind the Glorious left nav (app/OmegaTube/res/images/nav_fade.png).

A black horizontal gradient: solid-ish at the left edge of the screen (where the nav icons and labels sit),
holding, then easing out smoothly to fully transparent on the right so it never shows an edge over the artwork.
Tizen 5.5's DALi gradient visual bands and shows seams, so it is pre-rendered, with a little noise dithering to
break up 8-bit steps. The app stretches it to the nav's width (it is only a horizontal ramp).

Usage: python3 tools/nav-fade/make_nav_fade.py
"""
import os

import numpy as np
from PIL import Image

W, H = 320, 32
PEAK = 0.80          # opacity at the screen edge
HOLD = 0.60          # fraction of the width kept at PEAK before easing out

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "app", "OmegaTube", "res", "images", "nav_fade.png")


def main():
    x = (np.arange(W) + 0.5) / W
    t = np.clip((x - HOLD) / (1.0 - HOLD), 0, 1)
    ease = t * t * t * (t * (t * 6 - 15) + 10)            # smootherstep: no visible start / end of the ramp
    alpha = PEAK * (1 - ease)
    rng = np.random.default_rng(7)
    noise = rng.uniform(-0.5, 0.5, size=(H, W))            # +-half a step of dither
    a8 = np.clip(alpha[None, :] * 255 + noise + 0.5, 0, 255).astype(np.uint8)
    img = np.zeros((H, W, 4), np.uint8)
    img[..., 3] = a8
    Image.fromarray(img, "RGBA").save(OUT, optimize=True)
    print("wrote", os.path.relpath(OUT))


if __name__ == "__main__":
    main()
