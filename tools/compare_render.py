"""DNG 成像實驗：把 LibRaw 各參數組合的結果和 DNG 內嵌預覽（相機自己的沖洗結果）比較。

用法：python tools/compare_render.py <DNG 資料夾> <實驗輸出資料夾>
  實驗輸出資料夾由 `ConvertProbe.exe --variants` 產生（檔名：<DNG 名稱>__<做法>.png）。
另外對每個做法再試兩種後製：中等對比色調曲線（Adobe Camera Raw 的 Medium Contrast 控制點）、曲線＋飽和度 1.15。
指標：ΔE（CIE76，縮成 240 寬後逐畫素平均，越小越接近相機）、亮度、對比、飽和度。
"""
from __future__ import annotations

import io
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageCms, ImageEnhance

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TYPE_SIZE = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 10: 8, 13: 4}
CURVE_POINTS = [(0, 0), (32, 22), (64, 56), (128, 128), (192, 196), (255, 255)]


def read_ifds(b: bytes) -> list[dict[int, list[int]]]:
    e = "<" if b[:2] == b"II" else ">"

    def read(o: int) -> dict[int, list[int]]:
        n = struct.unpack(e + "H", b[o:o + 2])[0]
        r: dict[int, list[int]] = {}
        for i in range(n):
            p = o + 2 + i * 12
            tag, typ, cnt = struct.unpack(e + "HHI", b[p:p + 8])
            size = TYPE_SIZE.get(typ, 1) * cnt
            at = p + 8 if size <= 4 else struct.unpack(e + "I", b[p + 8:p + 12])[0]
            if typ in (3, 4, 13) and cnt <= 64:
                r[tag] = list(struct.unpack(e + ("H" if typ == 3 else "I") * cnt, b[at:at + size]))
        return r

    first = read(struct.unpack(e + "I", b[4:8])[0])
    return [first] + [read(s) for s in first.get(0x14A, [])]


def embedded_preview(dng: Path) -> Image.Image | None:
    """取 DNG 內最大的 JPEG 預覽（NewSubFileType = 1）。"""
    b = dng.read_bytes()
    best: tuple[int, bytes] | None = None
    for d in read_ifds(b):
        if d.get(0xFE, [None])[0] == 1 and d.get(0x103, [0])[0] in (6, 7) and 0x111 in d:
            w = d[0x100][0]
            if best is None or w > best[0]:
                best = (w, b"".join(b[o:o + c] for o, c in zip(d[0x111], d[0x117])))
    return Image.open(io.BytesIO(best[1])).convert("RGB") if best else None


def tone_curve(im: Image.Image) -> Image.Image:
    xs, ys = zip(*CURVE_POINTS)
    lut = np.clip(np.interp(np.arange(256), xs, ys), 0, 255).astype(np.uint8)
    # 線性內插的折線會有折角，用三點移動平均讓曲線平滑
    lut = np.convolve(np.pad(lut.astype(float), 1, mode="edge"), np.ones(3) / 3, mode="valid").astype(np.uint8)
    return im.point(list(lut) * 3)


SRGB = ImageCms.createProfile("sRGB")
LAB = ImageCms.createProfile("LAB")
TO_LAB = ImageCms.buildTransform(SRGB, LAB, "RGB", "LAB")


def to_lab(im: Image.Image) -> np.ndarray:
    # Pillow 的 LAB：L 為 0～255，a、b 以有號位元組（二補數）存放
    raw = np.asarray(ImageCms.applyTransform(im, TO_LAB))
    l_ = raw[..., 0].astype(float) * 100 / 255
    a_ = raw[..., 1].view(np.int8).astype(float)
    b_ = raw[..., 2].view(np.int8).astype(float)
    return np.stack([l_, a_, b_], axis=-1)


def metrics(im: Image.Image, ref: Image.Image) -> str:
    size = (240, round(240 * ref.height / ref.width))
    a, r = im.resize(size, Image.LANCZOS), ref.resize(size, Image.LANCZOS)
    la = to_lab(a)
    de = np.sqrt(((la - to_lab(r)) ** 2).sum(axis=-1)).mean()
    gray = np.asarray(a.convert("L")).astype(float)
    sat = np.asarray(a.convert("HSV"))[..., 1].mean()
    return (f"ΔE {de:5.1f}　亮度 {gray.mean():4.0f}　對比 {gray.std():3.0f}　飽和度 {sat:3.0f}"
            f"　色偏 a {la[..., 1].mean():+5.1f} b {la[..., 2].mean():+5.1f}")


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 1
    dng_dir, var_dir = Path(sys.argv[1]), Path(sys.argv[2])
    for dng in sorted(dng_dir.glob("*.dng")):
        variants = sorted(var_dir.glob(f"{dng.stem}__*.png"))
        ref = embedded_preview(dng)
        if not variants or ref is None:
            continue
        print(f"== {dng.stem}（參考：內嵌預覽 {ref.width}×{ref.height}）")
        print(f"  {'相機預覽':<26}{metrics(ref, ref)}")
        for v in variants:
            im = Image.open(v).convert("RGB")
            name = v.stem.split("__", 1)[1]
            curved = tone_curve(im)
            print(f"  {name:<26}{metrics(im, ref)}")
            print(f"  {name + '＋曲線':<26}{metrics(curved, ref)}")
            print(f"  {name + '＋曲線＋飽和度':<26}{metrics(ImageEnhance.Color(curved).enhance(1.15), ref)}")
    print("比較完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
