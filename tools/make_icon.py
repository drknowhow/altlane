"""Draws Altlane's app and tray icons: three stacked bars in the first three group colors.

Each size is drawn on its own pixel grid rather than scaled down from 256, so the 16 and 20 px
tray sizes stay sharp. Run: python tools/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "Altlane" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
BG = (31, 35, 40, 255)
LIVE = [(217, 119, 69), (76, 154, 95), (58, 123, 213)]  # Agents, Terminals, Web
PAUSED = [(150, 154, 160), (120, 124, 130), (95, 99, 105)]


def draw(size: int, colors) -> Image.Image:
    # Supersample 4x, then downscale: smooth corners without losing the bar edges.
    k = 4
    s = size * k
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=round(s * 0.22), fill=BG)
    pad = s * 0.19
    bar_h = s * 0.155
    gap = (s - 2 * pad - 3 * bar_h) / 2
    # Widths shrink toward the top, like layers seen from the side.
    widths = [0.62, 0.78, 1.0]
    for i, (c, w) in enumerate(zip(colors, widths)):
        y0 = pad + i * (bar_h + gap)
        full = s - 2 * pad
        x0 = pad
        x1 = pad + full * w
        d.rounded_rectangle([x0, y0, x1, y0 + bar_h], radius=bar_h / 2.4, fill=c + (255,))
    return im.resize((size, size), Image.LANCZOS)


def save(name: str, colors) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    frames = [draw(n, colors) for n in SIZES]
    frames[-1].save(OUT / name, format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
    if colors is LIVE:
        frames[-1].save(OUT.parent.parent.parent / "docs" / "icon-256.png")


if __name__ == "__main__":
    save("altlane.ico", LIVE)
    save("altlane-paused.ico", PAUSED)
    print("wrote", OUT)
