"""以 DNG 的小尺寸內嵌預覽（相機的沖洗結果）當顏色參考，把 LibRaw 的全尺寸結果調成相機的顏色。

用法：python tools/color_match_experiment.py <DNG 資料夾> <LibRaw 轉檔結果資料夾> <輸出資料夾>
  LibRaw 結果檔名：<DNG 名稱>.jpg（ConvertProbe 產生）。
試三種轉換（都在預覽尺寸上擬合，再套到全尺寸）：
  M1 各色版直方圖對應：只看顏色分布，不需要畫面對齊
  M2 3×3 矩陣＋位移：線性色彩轉換
  M3 二次多項式（10 項）：可修正非線性的色調與飽和度
輸出：各方法的 ΔE（同 compare_render.py 的算法）與全尺寸 JPG，供肉眼檢查瑕疵。
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).parent))
from compare_render import embedded_preview, metrics  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def poly_terms(x: np.ndarray, order: int) -> np.ndarray:
    r, g, b = x[:, 0], x[:, 1], x[:, 2]
    one = np.ones_like(r)
    if order == 1:
        return np.stack([r, g, b, one], axis=1)
    return np.stack([r, g, b, r * r, g * g, b * b, r * g, r * b, g * b, one], axis=1)


def fit_poly(src: np.ndarray, ref: np.ndarray, order: int) -> np.ndarray:
    a = poly_terms(src, order)
    coef, *_ = np.linalg.lstsq(a, ref, rcond=None)
    return coef


def apply_poly(img: np.ndarray, coef: np.ndarray, order: int) -> np.ndarray:
    h, w, _ = img.shape
    out = np.empty_like(img, dtype=np.float32)
    flat = img.reshape(-1, 3)
    step = 1_000_000  # 分段計算，避免一次配置太多記憶體
    res = out.reshape(-1, 3)
    for i in range(0, flat.shape[0], step):
        res[i:i + step] = poly_terms(flat[i:i + step], order) @ coef
    return np.clip(out, 0, 1)


def hist_match_luts(src: np.ndarray, ref: np.ndarray) -> list[np.ndarray]:
    luts = []
    q = np.linspace(0, 1, 257)
    for c in range(3):
        s = np.quantile(src[..., c], q)
        r = np.quantile(ref[..., c], q)
        s = np.maximum.accumulate(s + np.arange(257) * 1e-6)  # 確保遞增
        luts.append(np.interp(np.arange(256) / 255, s, r))
    return luts


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 1
    dng_dir, lr_dir, out_dir = map(Path, sys.argv[1:])
    out_dir.mkdir(parents=True, exist_ok=True)
    for dng in sorted(dng_dir.glob("*.dng")):
        lr_path = lr_dir / f"{dng.stem}.jpg"
        ref_img = embedded_preview(dng)
        if ref_img is None or not lr_path.exists():
            continue
        full = Image.open(lr_path).convert("RGB")
        if ref_img.width >= full.width * 0.9:
            continue  # 全尺寸預覽直接沿用，不需要比對
        print(f"== {dng.stem}（預覽 {ref_img.width}×{ref_img.height}，全尺寸 {full.width}×{full.height}）")
        small = full.resize(ref_img.size, Image.LANCZOS)
        src = np.asarray(small, dtype=np.float32) / 255
        ref = np.asarray(ref_img, dtype=np.float32) / 255
        fullf = np.asarray(full, dtype=np.float32) / 255
        # 排除過曝與全黑的畫素，避免裁切區域扭曲擬合
        mask = (src.max(axis=-1) < 0.98) & (ref.max(axis=-1) < 0.98) & (src.min(axis=-1) > 0.02)
        s, r = src[mask], ref[mask]

        results: dict[str, np.ndarray] = {}
        luts = hist_match_luts(src, ref)
        results["M1_直方圖對應"] = np.stack([np.interp(fullf[..., c], np.arange(256) / 255, luts[c]) for c in range(3)], -1)
        results["M2_矩陣"] = apply_poly(fullf, fit_poly(s, r, 1), 1)
        results["M3_二次多項式"] = apply_poly(fullf, fit_poly(s, r, 2), 2)

        print(f"  {'LibRaw（目前）':<18}{metrics(full, ref_img)}")
        for name, arr in results.items():
            im = Image.fromarray((arr * 255 + 0.5).astype(np.uint8))
            im.save(out_dir / f"{dng.stem}__{name}.jpg", quality=92)
            print(f"  {name:<18}{metrics(im, ref_img)}")
    print("比對完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
