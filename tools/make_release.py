"""產生 GitHub Release 要上傳的檔案。

用法（在專案根目錄）：python tools/make_release.py
1. 讀 Photo2Disk.csproj 的 <Version>
2. dotnet publish 到 publish/
3. 複製成 release/v<版本>/Photo2Disk.exe，並寫 Photo2Disk.exe.sha256
之後到 GitHub 網頁建立 Release（Tag 用 v<版本>），把這兩個檔案拖進去，說明欄寫更新內容。
GitHub 會改寫非英文的附件檔名，所以附件用英文檔名；程式更新時會沿用使用者電腦上原本的檔名。
"""
import hashlib
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"


def main() -> int:
    csproj = (ROOT / "Photo2Disk" / "Photo2Disk.csproj").read_text(encoding="utf-8")
    match = re.search(r"<Version>([\d.]+)</Version>", csproj)
    if not match:
        print("失敗：csproj 裡找不到 <Version>")
        return 1
    version = match.group(1)

    exe = ROOT / "publish" / "照片備份過濾工具.exe"
    result = subprocess.run(
        [DOTNET, "publish", "-c", "Release", "-o", str(ROOT / "publish")],
        cwd=ROOT / "Photo2Disk", capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if result.returncode != 0 or not exe.exists():
        print(result.stdout[-3000:], result.stderr[-3000:])
        print("失敗：dotnet publish 沒有成功（發佈版 exe 開著時無法覆寫，請先關閉）")
        return 1

    out = ROOT / "release" / f"v{version}"
    out.mkdir(parents=True, exist_ok=True)
    target = out / "Photo2Disk.exe"
    shutil.copy2(exe, target)
    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    (out / "Photo2Disk.exe.sha256").write_text(f"{digest}  Photo2Disk.exe\n", encoding="ascii")

    print(f"成功：v{version}")
    print(f"  {target}（{target.stat().st_size / 1024 / 1024:.1f} MB）")
    print(f"  {out / 'Photo2Disk.exe.sha256'}")
    print(f"  SHA-256：{digest}")
    print(f"下一步：GitHub → Releases → Draft a new release，Tag 填 v{version}，上傳上面兩個檔案後按 Publish release。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
