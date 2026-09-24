# Erzeugt die App-Symbole für Lernheft Studio (Surface) und Lernheft Stift (iPad).
from PIL import Image, ImageDraw, ImageFilter
import math

S = 1024

def background():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S))
    top, bottom = (58, 92, 255), (27, 40, 150)
    px = grad.load()
    for y in range(S):
        for x in range(S):
            t = min(1, max(0, (x * 0.35 + y * 0.65) / S))
            px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=230, fill=255)
    img.paste(grad, (0, 0), mask)
    return img

def curve(draw, pts, width, color):
    for i in range(len(pts) - 1):
        draw.line([pts[i], pts[i + 1]], fill=color, width=width)
    for p in pts:
        r = width / 2
        draw.ellipse((p[0] - r, p[1] - r, p[0] + r, p[1] + r), fill=color)

def swoosh(x0, y0, x1, amp, steps=80):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        x = x0 + (x1 - x0) * t
        y = y0 - amp * math.sin(t * math.pi * 1.25) * (1 - 0.35 * t)
        pts.append((x, y))
    return pts

def studio():
    img = background()
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((250, 196, 790, 858), radius=46, fill=(8, 14, 60, 120))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(28)))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((236, 170, 776, 830), radius=46, fill=(253, 253, 250, 255))
    for i in range(7):
        y = 318 + i * 68
        d.line((290, y, 722, y), fill=(190, 212, 232, 255), width=7)
    d.line((330, 200, 330, 800), fill=(236, 138, 128, 255), width=7)
    curve(d, swoosh(360, 560, 690, 190), 30, (255, 122, 69, 255))
    d.rounded_rectangle((372, 612, 610, 640), radius=14, fill=(33, 45, 70, 255))
    d.rounded_rectangle((372, 680, 540, 708), radius=14, fill=(33, 45, 70, 255))
    return img

def pad():
    img = background()
    d = ImageDraw.Draw(img)
    pts = []
    for i in range(121):
        t = i / 120
        x = 170 + (453 - 170) * t
        y = 735 - 300 * math.sin(t * math.pi) + (722 - 735) * t
        pts.append((x, y))
    curve(d, pts, 44, (255, 255, 255, 255))
    # Stift
    pen = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    p = ImageDraw.Draw(pen)
    p.rounded_rectangle((470, 120, 590, 640), radius=40, fill=(255, 255, 255, 255))
    p.polygon([(470, 610), (590, 610), (530, 760)], fill=(255, 255, 255, 255))
    p.polygon([(506, 700), (554, 700), (530, 760)], fill=(33, 45, 70, 255))
    p.rounded_rectangle((470, 120, 590, 200), radius=40, fill=(255, 122, 69, 255))
    pen = pen.rotate(-38, center=(530, 440), resample=Image.BICUBIC)
    shadow = pen.split()[3].filter(ImageFilter.GaussianBlur(20))
    sh = Image.new("RGBA", (S, S), (8, 14, 60, 0))
    sh.putalpha(shadow.point(lambda a: a * 0.45))
    img.alpha_composite(sh, (14, 22))
    img.alpha_composite(pen, (120, 30))
    return img

studio_img = studio()
studio_img.save("studio-1024.png")
studio_img.save("../windows/Studio.App/app.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
pad_img = pad()
flat = Image.new("RGB", (S, S), (27, 40, 150))
flat.paste(pad_img, (0, 0), pad_img)
full = pad()
# iOS will keine Transparenz: volles Quadrat ohne Rundung, iOS rundet selbst.
bg = Image.new("RGBA", (S, S))
px = bg.load()
top, bottom = (58, 92, 255), (27, 40, 150)
for y in range(S):
    for x in range(S):
        t = min(1, max(0, (x * 0.35 + y * 0.65) / S))
        px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
bg.alpha_composite(full)
bg.convert("RGB").save("pad-1024.png")
print("ok")
