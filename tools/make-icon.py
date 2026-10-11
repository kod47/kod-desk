# Generează iconița KodDesk (src/KodDesk.App/Assets/kod-desk.ico + PNG-uri) cu Pillow.
# Utilizare: python tools/make-icon.py
import io, struct, sys
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "KodDesk.App" / "Assets"
S = 1024  # pânza de lucru; se micșorează pentru fiecare dimensiune

BLUE_TOP = (59, 130, 246)
BLUE_BOTTOM = (29, 78, 216)
WHITE = (255, 255, 255, 255)
AMBER = (245, 158, 11, 255)
AMBER_DARK = (180, 83, 9, 255)

def gradient_background(simple):
    bg = Image.new("RGBA", (S, S))
    px = bg.load()
    for y in range(S):
        t = y / (S - 1)
        c = tuple(round(BLUE_TOP[i] + (BLUE_BOTTOM[i] - BLUE_TOP[i]) * t) for i in range(3)) + (255,)
        for x in range(S):
            px[x, y] = c
    mask = Image.new("L", (S, S), 0)
    radius = 200 if not simple else 160
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=radius, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(bg, (0, 0), mask)
    return out

def arrow(d, tip, scale, fill, outline, width):
    # Cursor clasic, vârful în stânga-sus.
    pts = [(0, 0), (0, 100), (26, 76), (44, 114), (64, 105), (46, 68), (80, 68)]
    pts = [(tip[0] + x * scale, tip[1] + y * scale) for x, y in pts]
    d.polygon(pts, fill=fill, outline=outline, width=width)

def draw(simple):
    img = gradient_background(simple)
    d = ImageDraw.Draw(img)
    if simple:
        # Variantă îngroșată pentru 16-24 px: linii mai groase, cursor mai mare.
        d.rounded_rectangle((150, 200, 874, 690), radius=70, outline=WHITE, width=110)
        d.rectangle((452, 690, 572, 790), fill=WHITE)
        d.rounded_rectangle((300, 780, 724, 870), radius=40, fill=WHITE)
        arrow(d, (390, 270), 3.2, AMBER, AMBER_DARK, 0)
    else:
        d.rounded_rectangle((180, 230, 844, 670), radius=56, outline=WHITE, width=60)
        d.rectangle((470, 670, 554, 770), fill=WHITE)
        d.rounded_rectangle((340, 760, 684, 820), radius=30, fill=WHITE)
        arrow(d, (410, 300), 2.6, AMBER, AMBER_DARK, 10)
    return img

def ico_bytes(images):
    # ICO cu intrări PNG (suportat de Windows Vista+).
    entries, blobs = [], []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        blobs.append(buf.getvalue())
    offset = 6 + 16 * len(images)
    header = struct.pack("<HHH", 0, 1, len(images))
    for im, blob in zip(images, blobs):
        w, h = im.size
        entries.append(struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    return header + b"".join(entries) + b"".join(blobs)

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    full, simple = draw(False), draw(True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [(simple if s <= 24 else full).resize((s, s), Image.LANCZOS) for s in sizes]
    (OUT / "kod-desk.ico").write_bytes(ico_bytes(frames))
    full.resize((512, 512), Image.LANCZOS).save(OUT / "kod-desk-512.png")
    full.resize((256, 256), Image.LANCZOS).save(OUT / "kod-desk-256.png")
    # Previzualizare: toate dimensiunile pe un fundal deschis și unul întunecat.
    if len(sys.argv) > 1:
        prev = Image.new("RGBA", (40 + sum(s + 20 for s in sizes), 2 * 300), (243, 244, 246, 255))
        dark = Image.new("RGBA", (prev.width, 300), (31, 41, 55, 255))
        prev.paste(dark, (0, 300))
        x = 20
        for s, f in zip(sizes, frames):
            prev.alpha_composite(f, (x, 150 - s // 2))
            prev.alpha_composite(f, (x, 450 - s // 2))
            x += s + 20
        prev.save(sys.argv[1])
    print("Icon generated in", OUT)

main()
