"""產生程式圖示。

用法（在專案根目錄）：python tools/make_icon.py --from tools/icon_source.png（目前使用，2026-11-13 改用 GPT 產生的漏斗圖示）
或 python tools/make_icon.py [樣式]（舊的自繪圖示）
樣式：A（預設）、B、C；加 --preview 時三種都畫成一張比較圖到 tools/icon_preview.png。
輸出：Photo2Disk/app.ico（16、20、24、32、40、48、64、128、256 像素）。
以 1024 像素畫好再縮小；16～32 像素另外加粗線條，避免縮小後糊掉。
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
PRIMARY = (59, 91, 140)
TEAL = (15, 118, 110)
AMBER = (217, 119, 6)
WHITE = (255, 255, 255)
SKY = (220, 236, 234)
MOUNTAIN = (15, 118, 110)
MOUNTAIN2 = (94, 160, 152)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
S = 1024


def rounded_bg(d: ImageDraw.ImageDraw, color: tuple[int, int, int], inset: int = 40) -> None:
    d.rounded_rectangle([inset, inset, S - inset, S - inset], radius=200, fill=color)


def photo(d: ImageDraw.ImageDraw, box: tuple[int, int, int, int], bold: bool) -> None:
    """白框照片：天空、太陽、兩座山。"""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    pad = int(w * (0.09 if bold else 0.07))
    d.rounded_rectangle(box, radius=int(w * 0.08), fill=WHITE)
    ix0, iy0, ix1, iy1 = x0 + pad, y0 + pad, x1 - pad, y1 - pad
    d.rounded_rectangle([ix0, iy0, ix1, iy1], radius=int(w * 0.04), fill=SKY)
    iw, ih = ix1 - ix0, iy1 - iy0
    r = iw * (0.12 if bold else 0.1)
    cx, cy = ix0 + iw * 0.72, iy0 + ih * 0.3
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=AMBER)
    d.polygon([(ix0 + iw * 0.38, iy1), (ix0 + iw * 0.72, iy0 + ih * 0.45), (ix1, iy0 + ih * 0.75), (ix1, iy1)], fill=MOUNTAIN2)
    d.polygon([(ix0, iy1), (ix0, iy0 + ih * 0.7), (ix0 + iw * 0.3, iy0 + ih * 0.38), (ix0 + iw * 0.68, iy1)], fill=MOUNTAIN)


def badge_arrow(d: ImageDraw.ImageDraw, cx: float, cy: float, r: float, color: tuple[int, int, int], bold: bool) -> None:
    """圓形徽章＋往下的箭頭（匯入）。"""
    ring = r * 0.16
    d.ellipse([cx - r - ring, cy - r - ring, cx + r + ring, cy + r + ring], fill=WHITE)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)
    sw = r * (0.36 if bold else 0.28)
    d.rectangle([cx - sw / 2, cy - r * 0.55, cx + sw / 2, cy + r * 0.1], fill=WHITE)
    hw = r * (0.62 if bold else 0.55)
    d.polygon([(cx - hw, cy - r * 0.05), (cx + hw, cy - r * 0.05), (cx, cy + r * 0.6)], fill=WHITE)


def badge_check(d: ImageDraw.ImageDraw, cx: float, cy: float, r: float, color: tuple[int, int, int], bold: bool) -> None:
    ring = r * 0.16
    d.ellipse([cx - r - ring, cy - r - ring, cx + r + ring, cy + r + ring], fill=WHITE)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)
    w = int(r * (0.34 if bold else 0.26))
    d.line([(cx - r * 0.5, cy + r * 0.02), (cx - r * 0.12, cy + r * 0.4), (cx + r * 0.52, cy - r * 0.38)], fill=WHITE, width=w, joint="curve")


def draw(style: str, bold: bool) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if style == "A":
        # 藍綠底、一張照片、琥珀色匯入箭頭
        rounded_bg(d, TEAL)
        photo(d, (170, 210, 810, 720), bold)
        badge_arrow(d, 740, 740, 190, AMBER, bold)
    elif style == "B":
        # 石板藍底、兩張疊起來的照片、藍綠打勾（已過濾）
        rounded_bg(d, PRIMARY)
        back = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        bd = ImageDraw.Draw(back)
        bd.rounded_rectangle((230, 170, 850, 660), radius=50, fill=(200, 212, 230))
        img.alpha_composite(back.rotate(8, resample=Image.BICUBIC, center=(540, 415)))
        photo(d, (150, 260, 770, 760), bold)
        badge_check(d, 750, 750, 180, TEAL, bold)
    else:
        # 白底（藍框）、照片往下進到硬碟
        d.rounded_rectangle([40, 40, S - 40, S - 40], radius=200, fill=WHITE, outline=PRIMARY, width=36)
        photo(d, (250, 140, 774, 540), bold)
        d.polygon([(512 - 90, 590), (512 + 90, 590), (512, 690)], fill=AMBER)
        d.rounded_rectangle((180, 720, 844, 900), radius=50, fill=PRIMARY)
        d.ellipse((720, 785, 770, 835), fill=(120, 220, 160))
    return img


def build_ico(style: str) -> Path:
    big = draw(style, bold=False)
    bold = draw(style, bold=True)
    frames = [(bold if n <= 32 else big).resize((n, n), Image.LANCZOS) for n in SIZES]
    out = ROOT / "Photo2Disk" / "app.ico"
    frames[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
    return out


def preview() -> Path:
    sizes = [256, 64, 48, 32, 24, 16]
    cell_w = sum(sizes) + 20 * len(sizes) + 40
    sheet = Image.new("RGB", (cell_w, 3 * 300 + 40), (240, 240, 240))
    dk = Image.new("RGB", (cell_w, 3 * 300 + 40), (32, 32, 32))
    try:
        font = ImageFont.truetype("msjhbd.ttc", 40)
    except OSError:
        font = ImageFont.load_default()
    for row, style in enumerate("ABC"):
        x = 80
        for n in sizes:
            src = draw(style, bold=n <= 32)
            icon = src.resize((n, n), Image.LANCZOS)
            y = 20 + row * 300 + (256 - n) // 2 + 20
            sheet.paste(icon, (x, y), icon)
            x += n + 20
        ImageDraw.Draw(sheet).text((20, 20 + row * 300 + 120), style, fill=(30, 30, 30), font=font)
    out = ROOT / "tools" / "icon_preview.png"
    sheet.save(out)
    return out


def build_ico_from_png(src: Path) -> Path:
    """由外部提供的 PNG 產生圖示：清掉幾乎透明的雜點、裁到內容範圍、置中成正方形。"""
    im = Image.open(src).convert("RGBA")
    alpha = im.getchannel("A").point(lambda v: 0 if v <= 24 else v)
    im.putalpha(alpha)
    im = im.crop(alpha.getbbox())
    side = int(max(im.size) * 1.04)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(im, ((side - im.width) // 2, (side - im.height) // 2))
    frames = []
    for n in SIZES:
        f = square.resize((n, n), Image.LANCZOS)
        if n <= 32:
            # 小尺寸邊緣半透明處拉高對比，避免糊成一團
            f.putalpha(f.getchannel("A").point(lambda v: 0 if v < 40 else min(255, int(v * 1.3))))
        frames.append(f)
    out = ROOT / "Photo2Disk" / "app.ico"
    frames[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
    sheet = Image.new("RGBA", (sum(SIZES) + 20 * len(SIZES) + 20, 300), (240, 240, 240, 255))
    x = 20
    for n, f in zip(SIZES, frames):
        sheet.alpha_composite(f, (x, (300 - n) // 2))
        x += n + 20
    sheet.save(ROOT / "tools" / "icon_preview.png")
    return out


if __name__ == "__main__":
    if "--from" in sys.argv:
        print("成功：", build_ico_from_png(Path(sys.argv[sys.argv.index("--from") + 1])))
    elif "--preview" in sys.argv:
        print("成功：", preview())
    else:
        style = next((a for a in sys.argv[1:] if a in ("A", "B", "C")), "A")
        print("成功：", build_ico(style))
