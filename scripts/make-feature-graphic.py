"""
Generate the Play Console feature graphic (1024x500) for PoSoccer.

Same palette source as scripts/make-icons.py - the colours come from
Assets/Scripts/Agent_UIStyle.cs, so the store art, the app icon and the in-game
UI cannot drift apart.

Play's rules for this asset:
  - exactly 1024x500, PNG or JPEG, no alpha
  - it gets cropped and overlaid with a play button in some placements, so keep
    the important content away from the centre and off the edges
  - no screenshots-of-screenshots, no small text (it is displayed tiny)

Output: docs/store/feature_graphic_1024x500.png  (outside Assets/ so it is never
packed into the build)

Run:  .venv/Scripts/python.exe scripts/make-feature-graphic.py
"""
import math
import os
from PIL import Image, ImageDraw, ImageFont

SS = 3  # supersample, then LANCZOS down

# Agent_UIStyle.cs
BACKGROUND = (13, 23, 18)
ACCENT = (41, 140, 71)
BLUE_TEAM = (51, 128, 255)
RED_TEAM = (255, 64, 51)
WHITE = (255, 255, 255)

W, H = 1024, 500
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _font(size):
    """Best available bold sans; Play shows this small so exact face matters little."""
    for name in ("arialbd.ttf", "segoeuib.ttf", "calibrib.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def _pentagon(cx, cy, r, rot=0.0):
    return [(cx + r * math.cos(rot + i * 2 * math.pi / 5),
             cy + r * math.sin(rot + i * 2 * math.pi / 5)) for i in range(5)]


def draw_ball(d, cx, cy, r):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=WHITE,
              outline=BACKGROUND, width=max(1, int(r * 0.06)))
    core = r * 0.34
    d.polygon(_pentagon(cx, cy, core, -math.pi / 2), fill=BACKGROUND)
    for i in range(5):
        a = -math.pi / 2 + i * 2 * math.pi / 5 + math.pi / 5
        px, py = cx + math.cos(a) * r * 0.80, cy + math.sin(a) * r * 0.80
        d.polygon(_pentagon(px, py, r * 0.30, a + math.pi / 2), fill=BACKGROUND)
        d.line([(cx + math.cos(a) * core * 1.05, cy + math.sin(a) * core * 1.05),
                (px, py)], fill=BACKGROUND, width=max(1, int(r * 0.08)))


def draw_player(d, cx, cy, r, colour):
    """A player as the game draws them: coloured body, thick team-colour frame."""
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=colour,
              outline=WHITE, width=max(2, int(r * 0.18)))


def main():
    w, h = W * SS, H * SS
    img = Image.new("RGB", (w, h), ACCENT)
    d = ImageDraw.Draw(img)

    # Mown bands, vertical here so the 1024x500 letterbox reads as a pitch seen
    # side-on rather than a repeat of the portrait icon.
    bands = 9
    bw = w / bands
    darker = tuple(max(0, int(c * 0.87)) for c in ACCENT)
    for i in range(bands):
        if i % 2:
            d.rectangle([i * bw, 0, (i + 1) * bw, h], fill=darker)

    # Halfway line + centre circle.
    lw = max(1, int(h * 0.012))
    d.line([(w / 2, 0), (w / 2, h)], fill=WHITE, width=lw)
    cr = h * 0.26
    d.ellipse([w / 2 - cr, h / 2 - cr, w / 2 + cr, h / 2 + cr], outline=WHITE, width=lw)

    # Ball dead centre, players flanking. Play may overlay a play-button in the
    # middle, so the ball is the only thing there - losing it costs nothing.
    draw_ball(d, w / 2, h / 2, h * 0.15)
    draw_player(d, w * 0.34, h * 0.36, h * 0.085, BLUE_TEAM)
    draw_player(d, w * 0.30, h * 0.66, h * 0.085, BLUE_TEAM)
    draw_player(d, w * 0.68, h * 0.34, h * 0.085, RED_TEAM)
    draw_player(d, w * 0.72, h * 0.64, h * 0.085, RED_TEAM)

    # Wordmark low-left, clear of the centre overlay and the edges.
    title = "PoSoccer"
    f = _font(int(h * 0.20))
    tx, ty = int(w * 0.06), int(h * 0.72)
    # Drop shadow for legibility over the light pitch.
    d.text((tx + int(h * 0.012), ty + int(h * 0.012)), title, font=f, fill=BACKGROUND)
    d.text((tx, ty), title, font=f, fill=WHITE)

    out = os.path.join(ROOT, "docs", "store", "feature_graphic_1024x500.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    img.resize((W, H), Image.LANCZOS).save(out)

    im = Image.open(out)
    print(f"{os.path.relpath(out, ROOT)}  {im.size[0]}x{im.size[1]} {im.mode}")
    assert im.size == (W, H) and im.mode == "RGB", "Play requires exactly 1024x500, no alpha"


if __name__ == "__main__":
    main()
