"""
Generate PoSoccer's Android launcher and store icons.

Deterministic, regenerable, and colour-matched to the game: every colour below is
read from Assets/Scripts/Agent_UIStyle.cs so the icon and the UI cannot drift.

Outputs
  Assets/Sprites/Icons/icon_foreground.png   432x432 RGBA  adaptive foreground
  Assets/Sprites/Icons/icon_background.png   432x432 RGB   adaptive background
  Assets/Sprites/Icons/icon_legacy.png       512x512 RGB   legacy / round fallback
  docs/store/icon_store_512.png              512x512 RGB   Play listing (NOT shipped)

The store icon deliberately lives outside Assets/ so it is never packed into the
build. Play requires 32-bit PNG with no alpha for it.

Adaptive icons are masked by the launcher: only the centre ~66% is guaranteed
visible, so the ball is sized to sit inside that safe zone.

Run:  .venv/Scripts/python.exe scripts/make-icons.py
"""
import math
import os
from PIL import Image, ImageDraw

SS = 4  # supersample factor; drawn large then LANCZOS-downsampled for clean edges

# Agent_UIStyle.cs, converted from linear 0-1 floats to 8-bit sRGB-ish ints.
BACKGROUND = (13, 23, 18)     # new(0.05f, 0.09f, 0.07f)
ACCENT = (41, 140, 71)        # new(0.16f, 0.55f, 0.28f)  - pitch green
BLUE_TEAM = (51, 128, 255)    # new(0.2f,  0.5f,  1f)
RED_TEAM = (255, 64, 51)      # new(1f,    0.25f, 0.2f)
WHITE = (255, 255, 255)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _pentagon(cx, cy, r, rot=0.0):
    """Five points, first vertex at `rot` radians (-pi/2 = pointing up)."""
    return [
        (cx + r * math.cos(rot + i * 2 * math.pi / 5),
         cy + r * math.sin(rot + i * 2 * math.pi / 5))
        for i in range(5)
    ]


def draw_pitch(size):
    """Background layer: the game's striped pitch, full bleed."""
    s = size * SS
    img = Image.new("RGB", (s, s), ACCENT)
    d = ImageDraw.Draw(img)

    # Alternating mown bands, matching the pitch look in SCN_Exhibition.
    bands = 7
    band_h = s / bands
    darker = tuple(max(0, int(c * 0.86)) for c in ACCENT)
    for i in range(bands):
        if i % 2:
            d.rectangle([0, i * band_h, s, (i + 1) * band_h], fill=darker)

    # Halfway line + centre circle, thin so they read as texture not clutter.
    lw = max(1, int(s * 0.012))
    d.line([(0, s / 2), (s, s / 2)], fill=(*WHITE, 255), width=lw)
    r = s * 0.20
    d.ellipse([s / 2 - r, s / 2 - r, s / 2 + r, s / 2 + r], outline=WHITE, width=lw)

    return img.resize((size, size), Image.LANCZOS)


def draw_ball(size, diameter_frac):
    """Foreground layer: a classic soccer ball on transparency."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    cx = cy = s / 2
    r = s * diameter_frac / 2

    # Body with a soft dark rim so it reads against a light pitch.
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=WHITE,
              outline=BACKGROUND, width=max(1, int(s * 0.010)))

    # Centre pentagon.
    core_r = r * 0.34
    d.polygon(_pentagon(cx, cy, core_r, -math.pi / 2), fill=BACKGROUND)

    # Five outer pentagons, clipped to the ball by drawing on a masked layer.
    outer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    od = ImageDraw.Draw(outer)
    for i in range(5):
        a = -math.pi / 2 + i * 2 * math.pi / 5 + math.pi / 5
        px = cx + math.cos(a) * r * 0.80
        py = cy + math.sin(a) * r * 0.80
        od.polygon(_pentagon(px, py, r * 0.30, a + math.pi / 2), fill=BACKGROUND)
        # Seam from the centre pentagon out to each outer patch.
        od.line([(cx + math.cos(a) * core_r * 1.05, cy + math.sin(a) * core_r * 1.05),
                 (px, py)], fill=BACKGROUND, width=max(1, int(s * 0.014)))

    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    img.paste(outer, (0, 0), Image.composite(mask, Image.new("L", (s, s), 0),
                                             outer.split()[3]))

    return img.resize((size, size), Image.LANCZOS)


def main():
    icons_dir = os.path.join(ROOT, "Assets", "Sprites", "Icons")
    store_dir = os.path.join(ROOT, "docs", "store")
    os.makedirs(icons_dir, exist_ok=True)
    os.makedirs(store_dir, exist_ok=True)

    # --- adaptive pair (432x432) -------------------------------------------
    bg = draw_pitch(432)
    bg.save(os.path.join(icons_dir, "icon_background.png"))

    # 0.52 keeps the ball inside the 66% safe zone the launcher mask guarantees.
    fg = draw_ball(432, 0.52)
    fg.save(os.path.join(icons_dir, "icon_foreground.png"))

    # --- legacy / round fallback (512x512) ---------------------------------
    legacy = draw_pitch(512).convert("RGBA")
    legacy.alpha_composite(draw_ball(512, 0.70))
    legacy.convert("RGB").save(os.path.join(icons_dir, "icon_legacy.png"))

    # --- Play Console store icon (512x512, no alpha) -----------------------
    store = draw_pitch(512).convert("RGBA")
    store.alpha_composite(draw_ball(512, 0.70))
    store.convert("RGB").save(os.path.join(store_dir, "icon_store_512.png"))

    for p in [os.path.join(icons_dir, "icon_background.png"),
              os.path.join(icons_dir, "icon_foreground.png"),
              os.path.join(icons_dir, "icon_legacy.png"),
              os.path.join(store_dir, "icon_store_512.png")]:
        im = Image.open(p)
        print(f"{os.path.relpath(p, ROOT):<46} {im.size[0]}x{im.size[1]} {im.mode}")


if __name__ == "__main__":
    main()
