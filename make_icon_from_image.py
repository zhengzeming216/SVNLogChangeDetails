"""把一张方形原图转成 VSIX 需要的图标素材。

用法:
    python make_icon_from_image.py <原图路径>

产出（写入 SvnMethodLens.VsExt/Resources/）:
    logo.png     128x128  VSIX Icon（source.extension.vsixmanifest 的 <Icon>）
    preview.png  200x200  VSIX PreviewImage（<PreviewImage>）
    logo-256.png 256x256  备用（VS Marketplace 页面图标等）
    logo-90.png  90x90    备用（部分 VSIX v2 场景要求 90x90）

要点：
- 输入必须是正方形（脚本会自动居中裁成正方形，避免拉伸变形）；
- 圆角之外保持透明，否则在 VS 深色主题下会露出白色方块；
- 统一用 LANCZOS 重采样，缩小后做一次轻微锐化，保证小尺寸下边缘不糊。
"""
import os
import sys
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "SvnMethodLens.VsExt", "Resources")

TARGETS = [
    ("logo.png", 128),
    ("preview.png", 200),
    ("logo-256.png", 256),
    ("logo-90.png", 90),
]


def square(img):
    """居中裁成正方形（不拉伸）。"""
    w, h = img.size
    side = min(w, h)
    left = (w - side) // 2
    top = (h - side) // 2
    return img.crop((left, top, left + side, top + side))


def resize(img, size):
    small = img.resize((size, size), Image.LANCZOS)
    if size <= 256:
        # 缩小后轻微锐化，抵消重采样造成的发虚
        small = small.filter(ImageFilter.UnsharpMask(radius=1.0, percent=60, threshold=2))
    return small


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    src = sys.argv[1]
    if not os.path.isfile(src):
        print("找不到原图: " + src)
        return 1

    os.makedirs(RES, exist_ok=True)
    img = Image.open(src).convert("RGBA")
    img = square(img)
    print("原图 {} -> 裁成正方形 {}x{}".format(os.path.basename(src), img.width, img.height))

    for name, size in TARGETS:
        out = os.path.join(RES, name)
        resize(img, size).save(out, "PNG", optimize=True)
        print("  写出 {}  {}x{}  {} bytes".format(name, size, size, os.path.getsize(out)))

    print("完成。重新打包：build.ps1 会把这些资源带进 vsix。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
