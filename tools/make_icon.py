"""Draws the WinExt4 icon (drive with an "ext4" badge) into a multi-size .ico."""
import sys
from PIL import Image, ImageDraw, ImageFont

def draw(size):
    s = size * 4  # supersample, then downscale for clean edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = s // 6
    # drive body
    d.rounded_rectangle([s * 0.06, s * 0.22, s * 0.94, s * 0.86], radius=r, fill=(43, 108, 176))
    d.rounded_rectangle([s * 0.06, s * 0.22, s * 0.94, s * 0.52], radius=r, fill=(66, 135, 205))
    # activity led
    d.ellipse([s * 0.76, s * 0.68, s * 0.84, s * 0.76], fill=(120, 230, 150))
    # "ext4" label
    try:
        font = ImageFont.truetype("segoeuib.ttf", int(s * 0.30))
    except OSError:
        font = ImageFont.load_default()
    text = "ext4"
    box = d.textbbox((0, 0), text, font=font)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((s - w) / 2 - box[0], s * 0.37 - h / 2 - box[1]), text, font=font, fill="white")
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
sizes = [16, 24, 32, 48, 64, 128, 256]
imgs = [draw(n) for n in sizes]
imgs[-1].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=imgs[:-1])
imgs[-1].save(out.replace(".ico", ".png"))
