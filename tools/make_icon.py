"""Draws the Room Acoustics 3D icon (a sound source radiating waves in a room, with one ray bouncing off the far wall) and writes it as
.ico (the usual Windows sizes) and .png into src/RoomAcoustics.Tray/Assets. Needs Pillow:  python tools/make_icon.py"""
import os
from PIL import Image, ImageDraw

S = 1024   # drawn large, then scaled down for smooth edges
HERE = os.path.dirname(os.path.abspath(__file__))


def background(top, bottom):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S))
    gp = grad.load()
    for y in range(S):
        t = y / (S - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        for x in range(S):
            gp[x, y] = c
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((24, 24, S - 24, S - 24), radius=190, fill=255)
    img.paste(grad, (0, 0), mask)
    return img


def room():
    img = background((48, 48, 56), (15, 15, 18))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((130, 190, S - 130, S - 190), radius=30, outline=(196, 181, 253, 255), width=26)
    sx, sy = 290, S // 2
    for i, r in enumerate((120, 215, 310)):
        a = 70 - i * 8
        d.arc((sx - r, sy - r, sx + r, sy + r), -a, a, fill=(167 + i * 15, 139 + i * 20, 250, 255 - i * 45), width=30)
    d.ellipse((sx - 44, sy - 44, sx + 44, sy + 44), fill=(52, 211, 153, 255))
    wx = S - 130 - 13
    ex, ey = 620, 700
    d.line([(sx + 40, sy - 20), (wx, 330), (ex, ey)], fill=(52, 211, 153, 255), width=16, joint="curve")
    d.ellipse((ex - 34, ey - 34, ex + 34, ey + 34), fill=(34, 211, 238, 255), outline=(15, 15, 18, 255), width=10)
    return img


def save(img, folder):
    out = os.path.join(HERE, "..", "src", folder, "Assets")
    os.makedirs(out, exist_ok=True)
    img.resize((256, 256), Image.LANCZOS).save(os.path.join(out, "app.png"))
    img.save(os.path.join(out, "app.ico"), sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
    print("wrote", os.path.normpath(out))


if __name__ == "__main__":
    save(room(), "RoomAcoustics.Tray")
