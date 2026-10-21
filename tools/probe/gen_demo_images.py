"""生成图集演示用的图片（3 张 900x1200）。

为什么需要：推荐流接口只返回视频，扫 48 条 images 字段一条都没有，
图集 UI 只能靠本地注入数据验证。跑这个脚本生成图片后，
用 `--demo-image` 启动应用就能看到图集效果。

用法: python tools/probe/gen_demo_images.py [输出目录]
默认输出到项目 tmp/demo/
"""
import os
import sys

from PIL import Image, ImageDraw

out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    "tmp", "demo")
os.makedirs(out_dir, exist_ok=True)

colors = [(196, 60, 80), (60, 120, 196), (70, 160, 110)]

for i, (r, g, b) in enumerate(colors, 1):
    img = Image.new("RGB", (900, 1200), (r, g, b))
    draw = ImageDraw.Draw(img)

    # 横条纹，方便一眼看出是第几张
    for y in range(0, 1200, 60):
        draw.rectangle([0, y, 900, y + 30],
                       fill=(min(r + 25, 255), min(g + 25, 255), min(b + 25, 255)))

    draw.rectangle([60, 500, 840, 700], fill=(20, 20, 24))
    draw.text((330, 580), f"IMAGE  {i} / 3", fill=(255, 255, 255))

    path = os.path.join(out_dir, f"img{i}.png")
    img.save(path)
    print("saved", path)
