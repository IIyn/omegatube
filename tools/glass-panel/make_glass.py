#!/usr/bin/env python3
"""Renders the "glass" nine-patches of the Glorious theme into app/OmegaTube/res/images/.

Tizen 5.5 has no CornerRadius, Borderline or BoxShadow and no backdrop blur, so the glass look is baked into a
nine-patch: a very light translucent white fill, a thin (1 px), subtle rim for the edge reflection and a faint
highlight just under the top edge, with anti-aliased rounded corners. Everything is white with the alpha carrying
the look, so the app draws it untinted (or tints it, e.g. accent while a row is picked up).

Variants: glass (cards, panels; radius 22) and glass_pill (buttons, selected entries; radius 32), each as resting
glass and as a brighter "lit" glass for the focused entry.

Usage: python3 tools/glass-panel/make_glass.py
"""
import os

import numpy as np
from PIL import Image

SS = 4               # supersampling for anti-aliasing
RIM = 1.0            # rim width in px: a hairline

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "app", "OmegaTube", "res", "images")

WHITE = (255, 255, 255)
SMOKE = (14, 16, 22)     # the dark tint of the readable-on-anything variant

# name, radius, content size, fixed edge (the middle SIZE - 2 * FIXED stretches), fill, rim, top highlight, fill colour
VARIANTS = (
    ("glass9.9.png", 22.0, 64, 26, 0.045, 0.20, 0.035, WHITE),
    ("glass_lit9.9.png", 22.0, 64, 26, 0.15, 0.42, 0.06, WHITE),
    ("glass_pill9.9.png", 32.0, 72, 34, 0.045, 0.20, 0.035, WHITE),
    ("glass_pill_lit9.9.png", 32.0, 72, 34, 0.15, 0.42, 0.06, WHITE),
    # dark smoked glass for small floating controls over artwork (the collapsed nav chip)
    ("glass_dark9.9.png", 22.0, 64, 26, 0.58, 0.22, 0.05, SMOKE),
    # the focused card's title panel: dark smoked glass (81% opaque), no rim (the card's outline already frames it)
    ("card_info9.9.png", 14.0, 40, 18, 0.81, 0.0, 0.0, SMOKE),
    # a hairline outline only (no fill) drawn around the focused card; radius = Theme.CardRadius
    ("card_ring9.9.png", 14.0, 40, 18, 0.0, 0.90, 0.0, WHITE, 1.0),
)


def body(size, radius, fill, rim, top, color=WHITE, rim_w=RIM):
    n = size * SS
    ys, xs = np.mgrid[0:n, 0:n]
    px = (xs + 0.5) / SS
    py = (ys + 0.5) / SS
    # signed distance to the rounded rectangle (positive inside)
    qx = np.abs(px - size / 2.0) - (size / 2.0 - radius)
    qy = np.abs(py - size / 2.0) - (size / 2.0 - radius)
    outside = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - radius
    d = -outside
    inside = d >= 0
    rim_a = np.where(inside & (d < rim_w), rim, 0.0)
    # highlight: strongest right under the top rim, fading out over ~6 px, only on the upper half
    hi = np.where(inside & (d >= rim_w) & (py < size / 2.0), top * np.clip(1 - (py - rim_w) / 6.0, 0, 1), 0.0)
    fill_a = np.where(inside, fill, 0.0)
    # white rim + highlight over the fill colour: composite the two layers (straight alpha out)
    edge_a = 1 - (1 - rim_a) * (1 - hi)
    alpha = 1 - (1 - fill_a) * (1 - edge_a)
    safe = np.maximum(alpha, 1e-6)
    chans = [(color[k] * fill_a * (1 - edge_a) + 255.0 * edge_a) / safe for k in range(3)]
    # box downsample = anti-aliasing (colour weighted by alpha so edges stay clean)
    def down(a):
        return a.reshape(size, SS, size, SS).mean(axis=(1, 3))
    a_d = down(alpha)
    rgb = [down(c * alpha) / np.maximum(a_d, 1e-6) for c in chans]
    img = np.zeros((size, size, 4), np.uint8)
    for k in range(3):
        img[..., k] = np.clip(rgb[k] + 0.5, 0, 255).astype(np.uint8)
    img[..., 3] = np.clip(a_d * 255 + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(img, "RGBA")


def nine_patch(content, size, fixed):
    out = Image.new("RGBA", (size + 2, size + 2), (0, 0, 0, 0))
    out.paste(content, (1, 1))
    black = (0, 0, 0, 255)
    for i in range(1 + fixed, 1 + size - fixed):       # stretchable region (top / left) and content area (bottom / right)
        for xy in ((i, 0), (0, i), (i, size + 1), (size + 1, i)):
            out.putpixel(xy, black)
    return out


# Soft drop shadow behind a card: a blurred dark rounded rectangle. The card's rectangle sits SHADOW_MARGIN px in
# from each edge of the image, so the app draws it SHADOW_MARGIN px larger than the card on every side (and nudges
# it down a little). Pure Python blur (separable Gaussian) so no extra dependency.
SHADOW_SIZE = 136
SHADOW_MARGIN = 36
SHADOW_FIXED = 60
SHADOW_SIGMA = 11.0
SHADOW_ALPHA = 0.62
SHADOW_RADIUS = 14.0


def shadow():
    n = SHADOW_SIZE * SS
    ys, xs = np.mgrid[0:n, 0:n]
    px = (xs + 0.5) / SS
    py = (ys + 0.5) / SS
    half = SHADOW_SIZE / 2.0 - SHADOW_MARGIN
    qx = np.abs(px - SHADOW_SIZE / 2.0) - (half - SHADOW_RADIUS)
    qy = np.abs(py - SHADOW_SIZE / 2.0) - (half - SHADOW_RADIUS)
    dist = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - SHADOW_RADIUS
    shape = (dist <= 0).astype(np.float64).reshape(SHADOW_SIZE, SS, SHADOW_SIZE, SS).mean(axis=(1, 3))
    radius = int(SHADOW_SIGMA * 3)
    k = np.exp(-0.5 * (np.arange(-radius, radius + 1) / SHADOW_SIGMA) ** 2)
    k /= k.sum()
    blurred = np.apply_along_axis(lambda r: np.convolve(r, k, mode="same"), 1, shape)
    blurred = np.apply_along_axis(lambda c: np.convolve(c, k, mode="same"), 0, blurred)
    rng = np.random.default_rng(11)
    a = blurred * SHADOW_ALPHA * 255 + rng.uniform(-0.5, 0.5, blurred.shape)   # dither: no banding in the falloff
    img = np.zeros((SHADOW_SIZE, SHADOW_SIZE, 4), np.uint8)
    img[..., 3] = np.clip(a + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(img, "RGBA")


def corner_hole():
    """White outside a rounded rectangle (radius = Theme.CardRadius), clear inside. Tinted to the page colour and
    laid over a square sprite-sheet preview, it hides the square corners (the toolkit cannot mask a sprite sheet)."""
    size, radius = 40, 14.0
    n = size * SS
    ys, xs = np.mgrid[0:n, 0:n]
    px = (xs + 0.5) / SS
    py = (ys + 0.5) / SS
    qx = np.abs(px - size / 2.0) - (size / 2.0 - radius)
    qy = np.abs(py - size / 2.0) - (size / 2.0 - radius)
    dist = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - radius
    outside = (dist > 0).astype(np.float64).reshape(size, SS, size, SS).mean(axis=(1, 3))
    img = np.full((size, size, 4), 255, np.uint8)
    img[..., 3] = np.clip(outside * 255 + 0.5, 0, 255).astype(np.uint8)
    return nine_patch(Image.fromarray(img, "RGBA"), size, 18)


def main():
    corner_hole().save(os.path.join(OUT, "round_hole9.9.png"), optimize=True)
    print("wrote", os.path.relpath(os.path.join(OUT, "round_hole9.9.png")))
    nine_patch(shadow(), SHADOW_SIZE, SHADOW_FIXED).save(os.path.join(OUT, "card_shadow9.9.png"), optimize=True)
    print("wrote", os.path.relpath(os.path.join(OUT, "card_shadow9.9.png")))
    for name, radius, size, fixed, fill, rim, top, color, *opt in VARIANTS:
        path = os.path.join(OUT, name)
        rim_w = opt[0] if opt else RIM
        nine_patch(body(size, radius, fill, rim, top, color, rim_w), size, fixed).save(path, optimize=True)
        print("wrote", os.path.relpath(path))


if __name__ == "__main__":
    main()
