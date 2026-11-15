#!/usr/bin/env python3
"""Renders the rounded-rectangle alpha mask of the card thumbnails into app/OmegaTube/res/images/.

Tizen 5.5 has no CornerRadius, so a card's thumbnail is clipped by an alpha mask (Widgets.MaskedImage:
ImageVisualProperty.AlphaMaskURL + CropToMask). CropToMask crops the image to the mask's own pixel rectangle,
which means the mask has to be at least as big as the largest picture drawn through it: with the old 400x225
mask and YouTube's 1280x720 stills the TV showed the top-left 400x225 corner scaled up — every thumbnail came
out zoomed roughly 3x (only the small 320x180 mqdefault was safe, because there the crop clamped to the whole
image).

So the Tube cards draw through `round_mask_hd` (1280x720, the size of the still they request) and the other
looks keep the small `round_mask` their smaller cards have always used. Radius 30 in a 1280-wide mask is 15 on
a 660-wide card, which is YouTube's corner on a card that size.

Usage: python3 tools/round-mask/make_mask.py            # both masks
       python3 tools/round-mask/make_mask.py --size 1280x720 --radius 30 --name round_mask_hd
"""
import argparse
import os

from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT = os.path.join(ROOT, "app", "OmegaTube", "res", "images")

SS = 4   # supersample, then downscale: anti-aliased corners without extra dependencies


def mask(width, height, radius):
    """White, fully opaque inside a rounded rectangle, transparent outside (alpha only matters)."""
    big = Image.new("L", (width * SS, height * SS), 0)
    ImageDraw.Draw(big).rounded_rectangle(
        (0, 0, width * SS - 1, height * SS - 1), radius=radius * SS, fill=255)
    alpha = big.resize((width, height), Image.LANCZOS)
    out = Image.new("RGBA", (width, height), (255, 255, 255, 0))
    out.putalpha(alpha)
    return out


def main():
    ap = argparse.ArgumentParser(description="Render the cards' rounded alpha masks.")
    ap.add_argument("--size", default=None, help="WxH (default: both masks the app ships)")
    ap.add_argument("--radius", type=int, default=None)
    ap.add_argument("--name", default=None, help="file name without .png")
    args = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)
    if args.size:
        w, h = (int(v) for v in args.size.lower().split("x"))
        name = args.name or f"round_mask_{w}x{h}"
        radius = args.radius or 14
        mask(w, h, radius).save(os.path.join(OUT, name + ".png"))
        print(f"{name}.png  {w}x{h}  radius {radius}")
        return

    # The small mask (Omega/Glorious, 400x225, r14) is the one the app has always shipped — left untouched. The
    # Tube look needs its own at the source resolution, because CropToMask crops to the mask's pixel rectangle.
    small = os.path.join(OUT, "round_mask.png")
    if not os.path.exists(small):
        mask(400, 225, 14).save(small)
        print("round_mask.png  400x225  radius 14  (was missing, written)")
    else:
        print("round_mask.png  400x225  (kept as shipped)")
    mask(1280, 720, 30).save(os.path.join(OUT, "round_mask_hd.png"))
    print("round_mask_hd.png  1280x720  radius 30")


if __name__ == "__main__":
    main()
