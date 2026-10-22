"""生成 GameWall 应用图标（logo-1024/256.png + 多尺寸 GameWall.ico）。

设计：超椭圆底盘（蓝→靛渐变）+ 三张微旋转的封面卡 + 中央播放键。
直接运行 `python tools/make-logo.py`，产物写入 src/GameWall/Assets/。
依赖 Pillow：pip install pillow
"""
import math, struct, os
from io import BytesIO
from PIL import Image, ImageDraw, ImageFilter

S = 1024                 # 绘制用超采样尺寸
U = S / 256.0            # 以 256 为设计栅格

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   'src', 'GameWall', 'Assets')

# ── 配色：从品牌强调色 #4F8CFF 出发，向靛蓝收敛，避免发紫发粉 ──
C_TOP = (0x5C, 0x8C, 0xFF)
C_BOT = (0x6A, 0x52, 0xE8)
C_PLAY_A = (0x40, 0x74, 0xFF)
C_PLAY_B = (0x66, 0x46, 0xE0)


def squircle(size, n=4.6, inset=0.02):
    w = size
    pad = size * inset
    img = Image.new("L", (w, w), 0)
    px = img.load()
    cx = cy = w / 2.0
    r = (w - 2 * pad) / 2.0
    for y in range(w):
        dy = abs((y + 0.5 - cy) / r)
        dyn = dy ** n
        for x in range(w):
            dx = abs((x + 0.5 - cx) / r)
            if dx ** n + dyn <= 1.0:
                px[x, y] = 255
    return img


def linear(size, c1, c2, angle=122):
    img = Image.new("RGB", (size, size))
    px = img.load()
    a = math.radians(angle)
    dx, dy = math.cos(a), math.sin(a)
    proj = [x * dx + y * dy for x in (0, size) for y in (0, size)]
    lo, hi = min(proj), max(proj)
    span = (hi - lo) or 1
    for y in range(size):
        row = y * dy
        for x in range(size):
            t = (x * dx + row - lo) / span
            t = 0.0 if t < 0 else (1.0 if t > 1 else t)
            px[x, y] = (round(c1[0] + (c2[0] - c1[0]) * t),
                        round(c1[1] + (c2[1] - c1[1]) * t),
                        round(c1[2] + (c2[2] - c1[2]) * t))
    return img


def glow(size, center, radius, color, alpha_max=96):
    img = Image.new("L", (size, size), 0)
    px = img.load()
    cx, cy = center
    inv = 1.0 / radius
    for y in range(size):
        dy = (y - cy) * inv
        for x in range(size):
            d = math.hypot((x - cx) * inv, dy)
            if d < 1.0:
                px[x, y] = int(alpha_max * (1 - d) ** 2)
    layer = Image.new("RGBA", (size, size), color + (0,))
    layer.putalpha(img)
    return layer


def plate():
    base = linear(S, C_TOP, C_BOT).convert("RGBA")
    base.alpha_composite(glow(S, (0.32 * S, 0.14 * S), 0.68 * S, (255, 255, 255)))
    base.putalpha(squircle(S))
    # 内高光边：让图标在深色背景上也能看轮廓
    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle(
        (3 * U, 3 * U, 253 * U, 253 * U), radius=58 * U,
        outline=(255, 255, 255, 46), width=int(2.2 * U))
    em = squircle(S)
    edge.putalpha(Image.composite(edge.getchannel("A"), Image.new("L", (S, S), 0), em))
    base.alpha_composite(edge)
    return base


def tilted(w, h, rot, alpha, cx, cy, radius=15):
    layer = Image.new("RGBA", (int(w * U) + 12, int(h * U) + 12), (0, 0, 0, 0))
    ImageDraw.Draw(layer).rounded_rectangle(
        (6, 6, 6 + int(w * U) - 1, 6 + int(h * U) - 1), radius=radius * U,
        fill=(255, 255, 255, alpha))
    layer = layer.rotate(rot, resample=Image.BICUBIC, expand=True)
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    canvas.alpha_composite(layer, (int(cx * U - layer.width / 2),
                                   int(cy * U - layer.height / 2)))
    return canvas


def play_triangle(x0, y0, x1, y1, tip_x):
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).polygon(
        [(x0 * U, y0 * U), (x0 * U, y1 * U), (tip_x * U, (y0 + y1) / 2 * U)], fill=255)
    tri = linear(S, C_PLAY_A, C_PLAY_B, 28).convert("RGBA")
    tri.putalpha(mask)
    return tri


def render():
    base = plate()

    # 侧卡：先铺一层散投影
    sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle(
        (66 * U, 50 * U, 194 * U, 216 * U), radius=18 * U, fill=(0x14, 0x18, 0x42, 120))
    base.alpha_composite(sh.filter(ImageFilter.GaussianBlur(13 * U)))

    base.alpha_composite(tilted(74, 124, -23, 104, 90, 136))
    base.alpha_composite(tilted(74, 124, 23, 104, 166, 136))

    # 主卡投影（贴合卡形，比整组投影更真实）
    fs = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(fs).rounded_rectangle(
        (86 * U, 62 * U, 170 * U, 200 * U), radius=17 * U, fill=(0x10, 0x14, 0x3A, 150))
    base.alpha_composite(fs.filter(ImageFilter.GaussianBlur(8 * U)))

    front = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(front).rounded_rectangle(
        (88 * U, 58 * U, 168 * U, 196 * U), radius=16 * U, fill=(255, 255, 255, 253))
    base.alpha_composite(front)

    base.alpha_composite(play_triangle(112, 106, 112, 150, 148))
    return base


def render_small():
    """≤24px 简化版：单卡 + 更大的播放键，去掉侧卡与投影。"""
    base = linear(S, C_TOP, C_BOT).convert("RGBA")
    base.alpha_composite(glow(S, (0.32 * S, 0.14 * S), 0.68 * S, (255, 255, 255)))
    base.putalpha(squircle(S))
    front = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(front).rounded_rectangle(
        (50 * U, 44 * U, 206 * U, 212 * U), radius=28 * U, fill=(255, 255, 255, 253))
    base.alpha_composite(front)
    base.alpha_composite(play_triangle(92, 86, 92, 170, 170))
    return base


def make_ico(img, path, sizes=(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)):
    small = render_small()
    entries = []
    for s in sizes:
        src = small if s <= 24 else img
        px = src.resize((s, s), Image.LANCZOS)
        bio = BytesIO()
        px.save(bio, format="PNG", optimize=True)
        entries.append((s, bio.getvalue()))
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for s, data in entries:
        out += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s,
                           0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    open(path, "wb").write(out)
    return len(out)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    img = render()
    # 1024 / 256 两个 PNG 进仓库（界面里的品牌标识 & 高清母版）
    img.resize((1024, 1024), Image.LANCZOS).save(os.path.join(OUT, "logo-1024.png"))
    img.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "logo-256.png"))
    print("ico bytes =", make_ico(img, os.path.join(OUT, "GameWall.ico")))
    print("written to", OUT)
