# -*- coding: utf-8 -*-
"""
生成 GameWall 的品牌图标。

设计思路
--------
主题是「游戏墙」，所以图标要一眼看出是「游戏 + 合集」：

* 底盘：超椭圆（squircle）暗色板，深空蓝向近黑渐变，左上带一点冷光。
  游戏启动器基本都不走纯白底，深底盘在任务栏、开始菜单里辨识度最高。
* 描边：一圈青→蓝→紫的霓虹渐变细边，这是整套 UI 强调色的来源。
* 主体：大写的 **G**（Game 的首字母）。用几何方式现画而不是排字体，
  这样每一档尺寸的笔画粗细都能单独控制，16px 也不会糊成一团。
  字母本身是白到浅青的竖向渐变，右上方留一个 45° 斜切缺口，
  比规整的圆环更有「硬核」「机甲」的味道。

产物（写进 src/GameWall/Assets/）：
    GameWall.ico     多尺寸图标，exe 图标 + 托盘图标共用
    logo-256.png     界面顶栏品牌标识
    logo-1024.png    宣传 / 商店页面用的大图

用法：python tools/make-logo.py
"""

import math
import os

from PIL import Image, ImageDraw, ImageFilter

S = 1024                      # 超采样尺寸，最后缩到各档
U = S / 256.0                 # 256 的设计栅格里 1 个单位 = 多少像素

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "src", "GameWall", "Assets")

# ── 配色 ─────────────────────────────────────────────────────────────
C_PLATE_TOP = (0x1B, 0x22, 0x33)     # 底盘上沿
C_PLATE_BOT = (0x08, 0x0A, 0x10)     # 底盘下沿
C_RIM_A = (0xB4, 0xF6, 0xFF)         # 霓虹描边：浅青高光
C_RIM_B = (0x45, 0xDF, 0xFF)         # 霓虹描边：电光青
C_RIM_C = (0xFF, 0xB6, 0x4D)         # 霓虹描边：琥珀（不用紫 —— 青到紫是最典型的 AI 生成感配色）
C_GLYPH_TOP = (0xFF, 0xFF, 0xFF)     # 字母上沿
C_GLYPH_BOT = (0xB6, 0xE8, 0xFF)     # 字母下沿


def squircle(size, n=4.6, inset=0.0):
    """超椭圆遮罩。n 越大越接近方形，4.6 的观感接近现代 App 图标。"""
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    pad = size * inset
    half = (size - 2 * pad) / 2.0
    c = size / 2.0
    steps = 720
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        ct, st = math.cos(t), math.sin(t)
        x = c + half * math.copysign(abs(ct) ** (2.0 / n), ct)
        y = c + half * math.copysign(abs(st) ** (2.0 / n), st)
        pts.append((x, y))
    d.polygon(pts, fill=255)
    return mask


def vertical(size, top, bottom):
    """竖向线性渐变。"""
    img = Image.new("RGB", (1, size))
    px = img.load()
    for y in range(size):
        k = y / max(1, size - 1)
        px[0, y] = (round(top[0] + (bottom[0] - top[0]) * k),
                    round(top[1] + (bottom[1] - top[1]) * k),
                    round(top[2] + (bottom[2] - top[2]) * k))
    return img.resize((size, size), Image.NEAREST)


def tri_gradient(size, stops):
    """横向多段渐变，stops 是 [(位置0~1, 颜色), ...]。"""
    img = Image.new("RGB", (size, 1))
    px = img.load()
    for x in range(size):
        k = x / max(1, size - 1)
        for i in range(len(stops) - 1):
            p0, c0 = stops[i]
            p1, c1 = stops[i + 1]
            if p0 <= k <= p1:
                f = 0.0 if p1 == p0 else (k - p0) / (p1 - p0)
                px[x, 0] = (round(c0[0] + (c1[0] - c0[0]) * f),
                            round(c0[1] + (c1[1] - c0[1]) * f),
                            round(c0[2] + (c1[2] - c0[2]) * f))
                break
    return img.resize((size, size), Image.NEAREST)


def radial(size, center, radius, color, alpha_max):
    """柔光：中心 alpha_max，到 radius 处衰减到 0。"""
    img = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(img)
    cx, cy = center[0] * size, center[1] * size
    r = radius * size
    steps = 40
    for i in range(steps, 0, -1):
        k = i / steps
        a = int(alpha_max * (1 - k) ** 2)
        rr = r * k
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=a)
    tint = Image.new("RGB", (size, size), color)
    return tint, img


def glyph_mask(size, thick=0.196, gap=44):
    """
    画出大写 G 的遮罩。

    thick: 笔画厚度（相对边长）
    gap  : 右侧缺口的半角（度）

    构造顺序：外圆 → 挖内圆成环 → 挖掉右侧缺口 → 补上中间的横杠。
    横杠左端做 45° 斜切，这是「G」区别于「O」的关键，也是整枚图标
    最有硬朗感的地方。
    """
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)

    cx = cy = size / 2.0
    r_out = size * 0.352
    r_in = r_out - size * thick
    w = max(1, int(size * 0.004))

    # 外圆减内圆 = 圆环。用 difference 得到干净的环。
    ring = Image.new("L", (size, size), 0)
    ImageDraw.Draw(ring).ellipse([cx - r_out, cy - r_out, cx + r_out, cy + r_out], fill=255)
    hole = Image.new("L", (size, size), 0)
    ImageDraw.Draw(hole).ellipse([cx - r_in, cy - r_in, cx + r_in, cy + r_in], fill=255)
    ring = Image.composite(Image.new("L", (size, size), 0), ring, hole)

    # 右侧缺口：从 -gap 到 +gap 度的扇形挖空
    cut = Image.new("L", (size, size), 0)
    ImageDraw.Draw(cut).pieslice(
        [cx - r_out - w, cy - r_out - w, cx + r_out + w, cy + r_out + w],
        360 - gap, gap, fill=255)
    ring = Image.composite(Image.new("L", (size, size), 0), ring, cut)

    # 中间的横杠：右端顶到环的外沿，左端 45° 斜切收进环里
    bar_th = r_in * 0.92
    bar_top = cy - bar_th / 2.0
    bar_bot = cy + bar_th / 2.0
    bar_left = cx - r_in * 0.30
    bar_right = cx + r_out
    chamfer = bar_th * 0.55
    bar = Image.new("L", (size, size), 0)
    ImageDraw.Draw(bar).polygon(
        [(bar_left + chamfer, bar_top), (bar_right, bar_top),
         (bar_right, bar_bot), (bar_left, bar_bot)],
        fill=255)
    ring = Image.composite(bar, ring, bar)

    # 内圈再挖一次，保证横杠与环的接缝处不留额外厚度
    m.paste(ring, (0, 0))
    return m


def render():
    """256 设计栅格的完整版。"""
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # ① 外发光：整枚图标外面套一层冷色光晕，深色任务栏上会「亮」起来
    glow_mask = squircle(S, inset=0.055).filter(ImageFilter.GaussianBlur(S * 0.030))
    tint, _ = radial(S, (0.5, 0.5), 0.62, (0x4F, 0x8C, 0xFF), 150)
    base = Image.composite(tint.convert("RGBA"), base, glow_mask)

    # ② 底盘
    plate_mask = squircle(S, inset=0.055)
    plate = vertical(S, C_PLATE_TOP, C_PLATE_BOT).convert("RGBA")
    tint, a = radial(S, (0.28, 0.10), 0.70, (0x2C, 0x4A, 0x7A), 128)
    plate = Image.composite(tint.convert("RGBA"), plate, a)
    plate.putalpha(plate_mask)
    base = Image.alpha_composite(base, plate)

    # ③ 霓虹描边：把「圆角矩形轮廓」做成一张遮罩，再乘以三色渐变
    outer = squircle(S, inset=0.055)
    inner = squircle(S, inset=0.055 + 3.0 * U / S * 256 / 256 * 0.0094 + 0.0094)
    rim_mask = Image.composite(Image.new("L", (S, S), 0), outer, inner)
    rim_rgb = tri_gradient(S, [(0.0, C_RIM_A), (0.5, C_RIM_B), (1.0, C_RIM_C)])
    rim = rim_rgb.convert("RGBA")
    rim.putalpha(rim_mask.point(lambda v: int(v * 0.92)))
    base = Image.alpha_composite(base, rim)

    # ④ 字母 G：先画一层淡淡的同色投影，让它从底盘上「浮」起来
    gm = glyph_mask(S)
    shadow = gm.filter(ImageFilter.GaussianBlur(S * 0.012))
    sh = Image.new("RGBA", (S, S), (0x00, 0x1A, 0x33, 150))
    sh.putalpha(shadow.point(lambda v: int(v * 0.55)))
    base = Image.alpha_composite(base, sh)

    glyph = vertical(S, C_GLYPH_TOP, C_GLYPH_BOT).convert("RGBA")
    glyph.putalpha(gm)
    base = Image.alpha_composite(base, glyph)

    # ⑤ 字母上沿打一点高光，增加金属/玻璃质感
    hl = gm.point(lambda v: v)
    hl_box = hl.getbbox()
    if hl_box:
        band = Image.new("L", (S, S), 0)
        ImageDraw.Draw(band).rectangle(
            [hl_box[0], hl_box[1], hl_box[2], hl_box[1] + int((hl_box[3] - hl_box[1]) * 0.16)],
            fill=70)
        band = Image.composite(band, Image.new("L", (S, S), 0), hl)
        base = Image.alpha_composite(base, Image.merge("RGBA", (
            Image.new("L", (S, S), 255), Image.new("L", (S, S), 255),
            Image.new("L", (S, S), 255), band.filter(ImageFilter.GaussianBlur(S * 0.006)))))

    return base


def render_small():
    """
    16~24px 的简化版。

    这个尺寸下外发光、内高光、投影全都会糊成脏边，所以只留
    底盘 + 描边 + 加粗的字母，笔画也比大图更粗一档。
    """
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    plate_mask = squircle(S, inset=0.030)
    plate = vertical(S, C_PLATE_TOP, C_PLATE_BOT).convert("RGBA")
    plate.putalpha(plate_mask)
    base = Image.alpha_composite(base, plate)

    outer = squircle(S, inset=0.030)
    inner = squircle(S, inset=0.030 + 0.028)
    rim_mask = Image.composite(Image.new("L", (S, S), 0), outer, inner)
    rim_rgb = tri_gradient(S, [(0.0, C_RIM_A), (0.5, C_RIM_B), (1.0, C_RIM_C)])
    rim = rim_rgb.convert("RGBA")
    rim.putalpha(rim_mask)
    base = Image.alpha_composite(base, rim)

    gm = glyph_mask(S, thick=0.235, gap=40)
    glyph = vertical(S, C_GLYPH_TOP, C_GLYPH_BOT).convert("RGBA")
    glyph.putalpha(gm)
    base = Image.alpha_composite(base, glyph)
    return base


def make_ico(big, small, path, sizes=(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)):
    """手工拼 ICO：ICONDIR + ICONDIRENTRY×N + 每档 PNG 数据。"""
    import io

    entries = []
    for s in sizes:
        src = small if s <= 24 else big
        # 缩之前先轻微锐化，避免小尺寸发虚
        img = src.resize((s, s), Image.LANCZOS)
        if s <= 48:
            img = img.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=2))
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        entries.append((s, buf.getvalue()))

    count = len(entries)
    header = bytearray()
    header += (0).to_bytes(2, "little")      # reserved
    header += (1).to_bytes(2, "little")      # type = icon
    header += count.to_bytes(2, "little")

    offset = 6 + 16 * count
    dir_bytes = bytearray()
    data_bytes = bytearray()
    for s, data in entries:
        w = 0 if s >= 256 else s             # 256 在这一格里写 0
        h = 0 if s >= 256 else s
        dir_bytes += bytes([w, h, 0, 0])
        dir_bytes += (1).to_bytes(2, "little")   # planes
        dir_bytes += (32).to_bytes(2, "little")  # bpp
        dir_bytes += len(data).to_bytes(4, "little")
        dir_bytes += offset.to_bytes(4, "little")
        data_bytes += data
        offset += len(data)

    with open(path, "wb") as f:
        f.write(bytes(header) + bytes(dir_bytes) + bytes(data_bytes))


def main():
    os.makedirs(OUT, exist_ok=True)
    big = render()
    small = render_small()

    ico = os.path.join(OUT, "GameWall.ico")
    make_ico(big, small, ico)

    big.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "logo-256.png"))
    big.resize((1024, 1024), Image.LANCZOS).save(os.path.join(OUT, "logo-1024.png"))

    print("已生成：")
    for name in ("GameWall.ico", "logo-256.png", "logo-1024.png"):
        p = os.path.join(OUT, name)
        print("  %-16s %8d 字节" % (name, os.path.getsize(p)))


if __name__ == "__main__":
    main()
