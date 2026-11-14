"""Generate windows/StickHand/assets/appicon.ico from the tray icon design.

The tray icon is drawn in code (AppHost.CreateTrayIcon): an orange disc with three
dark rounded "fingers". The application icon uses the same drawing so the taskbar,
the Start menu shortcut and the tray agree. Run from the repository root:

    python windows/scripts/make-appicon.py

Requires Pillow. The output is committed; this script only exists so the icon can be
regenerated instead of being an opaque binary.
"""

from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "StickHand" / "assets" / "appicon.ico"

# Same palette as AppHost.CreateTrayIcon.
DISC = (252, 163, 28, 255)
FINGER = (24, 24, 27, 255)

# Geometry on a 32-unit grid, copied from CreateTrayIcon.
DISC_BOX = (2, 2, 30, 30)
FINGERS = ((11, 22, 12), (16, 22, 8), (21, 22, 12))
STROKE = 5

SIZES = (16, 24, 32, 48, 64, 128, 256)
SUPERSAMPLE = 8


def render(size: int) -> Image.Image:
    """Draw at 8x and downscale, so the edges are antialiased at every size."""
    scale = size * SUPERSAMPLE
    unit = scale / 32.0
    image = Image.new("RGBA", (scale, scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.ellipse([v * unit for v in DISC_BOX], fill=DISC)

    width = STROKE * unit
    radius = width / 2
    for x, top, bottom in FINGERS:
        cx = x * unit
        draw.line([cx, top * unit, cx, bottom * unit], fill=FINGER, width=round(width))
        # Pillow has no round caps: a disc at each end does the same job.
        for y in (top, bottom):
            cy = y * unit
            draw.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=FINGER)

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    largest = render(max(SIZES))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    largest.save(OUT, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
