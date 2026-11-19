"""
Generates the printable colouring pages (templates) and the simulated "photo of a coloured page"
images used for the sample button and the automated tests.

    python Tools/make_assets.py

Outputs go to Assets/StreamingAssets (shipped inside the WebGL build).
"""
import math, os, random
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "StreamingAssets")
os.makedirs(os.path.join(OUT, "templates"), exist_ok=True)

SS = 3                      # supersampling
PW, PH = 1240, 1754         # A4 @ 150 dpi
LINE = 7

# ----------------------------------------------------------------------------- fish designs
# Each design draws on a 1000x600 canvas (head on the right) and returns the colourable seed points.

def poly(d, pts, w=LINE):
    d.line([(x * SS, y * SS) for x, y in pts + [pts[0]]], fill=0, width=w * SS, joint="curve")
    for x, y in pts:
        d.ellipse([x * SS - w * SS / 2, y * SS - w * SS / 2, x * SS + w * SS / 2, y * SS + w * SS / 2], fill=0)

def ell(d, cx, cy, a, b, w=LINE):
    d.ellipse([(cx - a) * SS, (cy - b) * SS, (cx + a) * SS, (cy + b) * SS], outline=0, width=w * SS)

def curve(d, pts, w=LINE):
    d.line([(x * SS, y * SS) for x, y in pts], fill=0, width=w * SS, joint="curve")

def fish_classic(d):
    # fins/tail first (white filled so the body overlaps them)
    for pts in ([(250, 300), (60, 140), (110, 300), (60, 460)],            # tail
                [(420, 190), (520, 60), (650, 185)],                         # dorsal
                [(480, 410), (560, 530), (650, 415)]):                       # pelvic
        d.polygon([(x * SS, y * SS) for x, y in pts], fill=255)
        poly(d, pts)
    d.ellipse([200 * SS, 150 * SS, 840 * SS, 450 * SS], fill=255)
    ell(d, 520, 300, 320, 150)
    # stripes
    for x in (400, 520):
        h = 150 * math.sqrt(max(0, 1 - ((x - 520) / 320) ** 2))
        curve(d, [(x - 18 * math.sin(i / 8 * math.pi), 300 - h + 2 * h * i / 8) for i in range(9)])
    # gill + eye + mouth
    curve(d, [(640 + 30 * math.sin(i / 8 * math.pi), 215 + 170 * i / 8) for i in range(9)])
    ell(d, 740, 265, 26, 26); d.ellipse([(740 - 11) * SS, (265 - 11) * SS, (740 + 11) * SS, (265 + 11) * SS], fill=0)
    curve(d, [(835, 305), (800, 325), (770, 320)])
    seeds = {"tail": [(130, 230), (130, 370)], "dorsal": (545, 115), "pelvic": (560, 450),
             "b1": (300, 300), "b2": (460, 300), "b3": (580, 300), "head": (720, 340)}
    return seeds

def fish_round(d):
    for pts in ([(260, 300), (70, 110), (150, 300), (70, 490)],
                [(470, 160), (540, 50), (610, 150)],
                [(430, 430), (400, 540), (520, 440)]):
        d.polygon([(x * SS, y * SS) for x, y in pts], fill=255); poly(d, pts)
    d.ellipse([200 * SS, 100 * SS, 840 * SS, 500 * SS], fill=255)
    ell(d, 520, 300, 320, 200)
    for cx, cy, r in ((380, 250, 40), (450, 360, 32), (560, 230, 36), (610, 360, 44), (320, 340, 26)):
        ell(d, cx, cy, r, r)
    ell(d, 730, 240, 34, 34); d.ellipse([(730 - 14) * SS, (240 - 14) * SS, (730 + 14) * SS, (240 + 14) * SS], fill=0)
    ell(d, 800, 330, 14, 10)
    seeds = {"tail": [(135, 200), (135, 400)], "dorsal": (540, 120), "pelvic": (440, 480), "body": (500, 160),
             "d1": (380, 250), "d2": (450, 360), "d3": (560, 230), "d4": (610, 360), "d5": (320, 340)}
    return seeds

def fish_long(d):
    for pts in ([(240, 300), (40, 200), (110, 300), (40, 400)],
                [(330, 235), (470, 120), (560, 225)],
                [(580, 360), (660, 460), (700, 350)]):
        d.polygon([(x * SS, y * SS) for x, y in pts], fill=255); poly(d, pts)
    body = [(240, 300), (330, 225), (560, 205), (760, 235), (880, 300), (760, 370), (560, 395), (330, 375)]
    d.polygon([(x * SS, y * SS) for x, y in body], fill=255); poly(d, body)
    for x in (360, 470, 580, 690):
        curve(d, [(x - 10, 225 + (x - 330) * 0.0), (x + 12, 300), (x - 10, 385)])
    ell(d, 800, 280, 20, 20); d.ellipse([(800 - 9) * SS, (280 - 9) * SS, (800 + 9) * SS, (280 + 9) * SS], fill=0)
    seeds = {"tail": [(105, 250), (105, 350)], "dorsal": (470, 190), "pelvic": (640, 400), "s0": (300, 300),
             "s1": (420, 300), "s2": (530, 300), "s3": (640, 300), "s4": (770, 330)}
    return seeds

DESIGNS = {"fish_template_1": fish_classic, "fish_template_2": fish_round, "fish_template_3": fish_long}

PALETTES = {
    "fish_template_1": {"tail": (60, 120, 230), "dorsal": (250, 210, 40), "pelvic": (250, 210, 40),
                        "b1": (250, 140, 30), "b2": (235, 50, 60), "b3": (250, 140, 30), "head": (250, 120, 20)},
    "fish_template_2": {"tail": (150, 60, 200), "dorsal": (40, 190, 120), "pelvic": (40, 190, 120), "body": (250, 200, 60),
                        "d1": (230, 60, 110), "d2": (60, 150, 230), "d3": (230, 60, 110), "d4": (60, 150, 230), "d5": (255, 255, 255)},
    "fish_template_3": {"tail": (230, 70, 50), "dorsal": (40, 150, 90), "pelvic": (40, 150, 90), "s0": (70, 160, 230),
                        "s1": (250, 220, 60), "s2": (70, 160, 230), "s3": (250, 220, 60), "s4": (70, 160, 230)},
}


def render_fish(name):
    """Return (line-art RGB image 1000x600 at SS scale, seeds)."""
    img = Image.new("L", (1000 * SS, 600 * SS), 255)
    d = ImageDraw.Draw(img)
    seeds = DESIGNS[name](d)
    return img, seeds


def make_page(name, colour):
    art, seeds = render_fish(name)
    art = art.convert("RGB")
    if colour:
        rnd = random.Random(5)
        for key, col in PALETTES[name].items():
            pts = seeds[key] if isinstance(seeds[key], list) else [seeds[key]]
            for x, y in pts:
                ImageDraw.floodfill(art, (x * SS, y * SS), col, thresh=40)
        arr = np.asarray(art).astype(np.float32)
        # crayon texture: only on coloured (non grey) pixels
        mx, mn = arr.max(2), arr.min(2)
        coloured = ((mx - mn) > 25)[..., None]
        n8 = np.clip(128 + np.random.RandomState(1).normal(0, 60, arr.shape[:2]), 0, 255).astype(np.uint8)
        noise = (np.asarray(Image.fromarray(n8).filter(ImageFilter.GaussianBlur(1.5))).astype(np.float32) - 128) / 128
        arr = np.where(coloured, np.clip(arr * (1 + 0.35 * noise[..., None]), 0, 255), arr)
        art = Image.fromarray(arr.astype(np.uint8))
    art = art.resize((1000, 600), Image.LANCZOS)
    page = Image.new("RGB", (PW, PH), (255, 255, 255))
    page.paste(art, ((PW - 1000) // 2, 560))
    d = ImageDraw.Draw(page)
    try:
        font = ImageFont.truetype("arial.ttf", 54); small = ImageFont.truetype("arial.ttf", 30)
    except Exception:
        font = small = ImageFont.load_default()
    d.text((PW // 2, 230), "Colour me in!", fill=(40, 40, 40), font=font, anchor="mm")
    d.text((PW // 2, 300), "Then scan me with the Magic Colouring Aquarium", fill=(90, 90, 90), font=small, anchor="mm")
    d.text((PW // 2, 1500), "Tip: keep the whole page on a plain table and colour inside the lines", fill=(120, 120, 120), font=small, anchor="mm")
    return page


# ----------------------------------------------------------------------------- simulated photo
def find_coeffs(dst, src):
    """PIL wants coefficients mapping output (dst) coords -> input (src) coords."""
    A, B = [], []
    for (x, y), (u, v) in zip(dst, src):
        A.append([x, y, 1, 0, 0, 0, -u * x, -u * y]); B.append(u)
        A.append([0, 0, 0, x, y, 1, -v * x, -v * y]); B.append(v)
    return np.linalg.solve(np.array(A, dtype=np.float64), np.array(B, dtype=np.float64))


def make_photo(page, quad, size=(1600, 1200), seed=0, tint=(1.0, 0.97, 0.90), light=(0.82, 1.06), bg="wood"):
    rs = np.random.RandomState(seed)
    W, H = size
    # background: wooden table / grey desk
    y = np.linspace(0, 1, H)[:, None]; x = np.linspace(0, 1, W)[None, :]
    if bg == "wood":
        grain = 0.5 + 0.5 * np.sin(y * 260 + 6 * np.sin(x * 5 + y * 20) + rs.normal(0, 0.15, (H, W)))
        base = np.stack([150 + 40 * grain, 100 + 30 * grain, 60 + 20 * grain], -1)
    else:
        base = np.stack([105 + 0 * y + 0 * x] * 3, -1) + rs.normal(0, 3, (H, W, 3))
    bgimg = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8))

    src = [(0, 0), (PW, 0), (PW, PH), (0, PH)]
    coeffs = find_coeffs(quad, src)
    warped = page.transform(size, Image.PERSPECTIVE, coeffs, Image.BICUBIC)
    mask = Image.new("L", (PW, PH), 255).transform(size, Image.PERSPECTIVE, coeffs, Image.BILINEAR)
    # soft shadow
    sh = mask.filter(ImageFilter.GaussianBlur(14)).point(lambda v: int(v * 0.45))
    bgimg.paste((0, 0, 0), (10, 14), sh)
    bgimg.paste(warped, (0, 0), mask)
    arr = np.asarray(bgimg).astype(np.float32)
    # lighting gradient + warm tint + noise
    grad = light[0] + (light[1] - light[0]) * (0.6 * x + 0.4 * y)
    arr *= grad[..., None]
    arr *= np.array(tint, dtype=np.float32)[None, None, :]
    arr += rs.normal(0, 4, arr.shape)
    img = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.7))
    return img


def main():
    for name in DESIGNS:
        make_page(name, colour=False).save(os.path.join(OUT, "templates", name + ".png"))
    quads = [
        [(430, 120), (1180, 170), (1130, 1090), (360, 1040)],     # slight perspective
        [(300, 200), (1000, 90), (1230, 980), (470, 1130)],       # rotated ~12 deg
        [(500, 70), (1150, 140), (1250, 1130), (400, 1060)],      # stronger perspective
    ]
    cfgs = [dict(seed=1, bg="wood"), dict(seed=2, bg="grey", tint=(0.97, 1.0, 0.95)), dict(seed=3, bg="wood", light=(0.7, 1.1))]
    for i, name in enumerate(DESIGNS):
        page = make_page(name, colour=True)
        page.save(os.path.join(ROOT, "Tools", f"coloured_{name}.png"))
        make_photo(page, quads[i], **cfgs[i]).save(os.path.join(OUT, f"test_photo_{i+1}.jpg"), quality=88)
    import shutil
    shutil.copy(os.path.join(OUT, "test_photo_1.jpg"), os.path.join(OUT, "sample_photo.jpg"))
    print("assets written to", OUT)


if __name__ == "__main__":
    main()


