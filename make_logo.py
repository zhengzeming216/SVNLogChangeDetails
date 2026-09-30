"""生成 SvnMethodLens 的 VSIX 图标与预览图。

设计：放大镜（Lens）审视代码行，被聚焦的方法行高亮 —— 表达
"SVN 方法级归属 / 谁改了这个方法" 这一主题。纯程序绘制，无外部素材。
"""
import os
from PIL import Image, ImageDraw

SS = 4  # 超采样倍数，用于抗锯齿
BASE = 512


def lerp(c1, c2, t):
    return tuple(int(round(a + (b - a) * t)) for a, b in zip(c1, c2))


def rounded_rect(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def build(size_out, path):
    S = size_out * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 背景：圆角方块 + 蓝紫渐变
    top, bottom = (91, 75, 214), (32, 110, 200)
    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(top, bottom, y / (S - 1)) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    # 代码行
    lines = [(0.62, 0.30), (0.80, 0.44), (0.44, 0.58), (0.70, 0.72)]
    x0 = int(S * 0.16)
    y_base = int(S * 0.26)
    gap = int(S * 0.14)
    h = int(S * 0.055)
    for i, (w, _) in enumerate(lines):
        y = y_base + i * gap
        wpx = int(S * w * 0.68)
        color = (255, 200, 90, 255) if i == 1 else (255, 255, 255, 90)  # 被聚焦的方法行高亮
        rounded_rect(d, [x0, y, x0 + wpx, y + h], radius=h // 2, fill=color)
        # 缩进层级：再画一小段更靠左的暗色行
        if i != 1:
            rounded_rect(d, [x0, y, x0 + int(wpx * 0.45), y + h],
                         radius=h // 2, fill=(255, 255, 255, 150))

    # 放大镜：右侧压在高亮行上
    cx = int(S * 0.66)
    cy = int(S * 0.42)
    r = int(S * 0.20)
    lw = int(S * 0.055)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(255, 255, 255, 250), width=lw)
    # 镜柄
    d.line([(cx + int(r * 0.72), cy + int(r * 0.72)),
            (cx + int(r * 1.35), cy + int(r * 1.35))],
           fill=(255, 255, 255, 250), width=int(S * 0.075))
    d.ellipse([cx - int(r * 0.55), cy - int(r * 0.55), cx + int(r * 0.55), cy + int(r * 0.55)],
              outline=(255, 255, 255, 90), width=int(S * 0.02))

    img = img.resize((size_out, size_out), Image.LANCZOS)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)
    print("wrote", path, img.size)


here = os.path.dirname(os.path.abspath(__file__))
res = os.path.join(here, "SvnMethodLens.VsExt", "Resources")
build(128, os.path.join(res, "logo.png"))
build(200, os.path.join(res, "preview.png"))
