# 照片備份過濾工具（Photo2Disk）

給 YAN 的同學用的 Windows 小工具（不是 YAN 的爸爸，YAN 已更正兩次）：從 Google 相簿網頁版下載照片（zip）後，比對外接硬碟裡已經有哪些，只把沒有的複製進去。整理照片由使用者自己手動做，程式只負責過濾。

## 位置與跨電腦開發（2026-11-13 移入外部資料）

- 專案位置：桌機 `E:\Google備份\Obsidian\外部資料\私人專案\本機專案\照片備份過濾工具\`，筆電 `G:\其他電腦\我的電腦\Google備份\Obsidian\外部資料\私人專案\本機專案\照片備份過濾工具\`。原本在筆電 `Desktop\Google _Photo2Disk\`。
- 文件在 vault 的 `日常生活\私人專案管理\本機專案\照片備份過濾工具\`（入口筆記、使用說明、開發手冊）。改了功能要同步更新使用說明。
- `Directory.Build.props` 把 `bin`、`obj` 移到本機 `%LOCALAPPDATA%\Photo2Disk_build\<專案名>\`，建置產物不進雲端、兩台電腦互不干擾。不要拿掉；在專案資料夾內看到 `bin`、`obj` 代表這個設定沒生效。
- 雲端同步需要時間：換電腦前確認 Google 雲端已同步完成，不要兩台同時開著改。
- 沒有 git，未上 GitHub（可上：沒有內網或私密資料；同學回報截圖在 `回報截圖\`，已列入 `.gitignore`）。YAN 2026-11-13 決定為了自動更新改成公開 repo，repo 名稱填進 `Updater.Repository` 前，程式不會檢查更新。第一次推上 GitHub 時照全域規則把資料夾移到 `私人專案\GitHub\`。

## 需求決策（2026-11-07）

- Google Photos Library API 自 2025-03-31 起不能讀取使用者整個相簿，所以不做自動同步。Takeout 自動化也評估過：相簿約 1 TB，送到雲端硬碟要再空出 1 TB，不可行，最後改成目前的比對工具。
- 使用者的相簿用「原始畫質」備份，硬碟裡既有的照片也是從 Google 下載的，所以用「檔案大小＋SHA-256」比對內容，不看檔名。
- 新照片一律放到 `<備份資料夾>\_待整理\yyyy-MM-dd\`，不提供其他位置。放在備份資料夾裡面，下次比對時才會算成「硬碟已有」。
- 只看硬碟現況：匯入過的照片若被刪除，下次下載到時會再匯入（YAN 2026-11-07 決定，不小心刪除時可以補回）。但要標示出來：匯入紀錄存在 `ImportHistory.cs` 的隱藏檔 `.photo2disk_history.tsv`（只附加），比對時新照片若在紀錄裡，狀態顯示「曾遺失」（複製後「已補回」），並註明當初匯入的日期與位置。只記錄本程式匯入的照片，使用本程式之前就在硬碟裡的照片被刪除不會標示。
- 來源重複要提示（YAN 2026-11-12 決定，取代 11-07「不顯示這批重複」）：同學回報資料夾 9,945 個檔案只複製了 9,568 張，原因是被隱藏的重複檔。現在比對結果多一個琥珀色「來源資料重複：N 張（只複製一份）」標籤（點一下切到該篩選），比對後跳出視窗列出前 8 組重複檔（`MainForm.DuplicateMessage`），顯示篩選多「來源資料重複」，「全部」與 CSV 也列出這些項目，每列註明與哪個檔案重複。引擎改成先判斷同一批內是否重複，再看硬碟有沒有，所以「新照片＋硬碟已有＋來源資料重複」＝來源資料的檔案數（略過的 0 位元組、`desktop.ini`、系統檔不算在內）。
- 介面用語（YAN 2026-11-12）：來源一律稱「需匯入的來源資料」「來源資料」，重複稱「來源資料重複」，不寫「從 Google 下載的檔案」（來源不一定是 Google 下載的 zip）。只有說明 zip 用法、Google 相簿編輯過的照片時才提到 Google。
- 下載檔案區只有「加入…」與「移除」兩個按鈕（YAN 2026-11-07 要求程式自動判斷資料夾或壓縮檔）。Windows 沒有可同時選檔案與資料夾的標準視窗，所以「加入…」用 `OpenFileDialog`（`CheckFileExists=false`、`ValidateNames=false`），檔名欄預設「加入這個資料夾」：選了檔案就加入檔案，維持預設文字按「開啟」就加入目前所在資料夾（`ResolveDialogSelection`）。坑：Windows 視窗會依篩選條件把預設文字補成「加入這個資料夾.zip」，`AddExtension=false` 也擋不住（2026-11-07 用 `tests\UiTest\DialogProbe.cs` 實際開視窗按開啟重現），所以判斷時忽略副檔名比對預設文字。加入下載、桌面、文件、使用者資料夾或磁碟根目錄時先確認。1.3.0 起清單每列右側有 ×，「移除」按鈕與 Ctrl+A／Delete 拿掉。
- 重複加入清單中已有的檔案時，清除舊結果並在狀態列提示重新比對（原本沒有提示，YAN 誤以為比對沒生效）。
- 複製時統一檔名（YAN 2026-11-13，同學要求「拍照實際年月-原檔名」）：只在「複製新照片」時改名，不改硬碟原有檔案（YAN 選定，維持只新增不修改）。主畫面「檔名規則…」開 `NamingDialog`，預設保留原檔名。可組合項目：拍攝日期（6 種格式）、原檔名、自訂文字、流水號、相機／手機型號、照片／影片、所在資料夾名稱，另有 6 組快速套用。拍攝日期依序取 EXIF → 影片 mvhd → 原檔名日期 → 檔案修改時間（YAN 選定），後兩者在結果清單註明。

## 建置

- `dotnet` 不在 PATH，用 `"C:\Program Files\dotnet\dotnet.exe"`（.NET 8 SDK，筆電 2026-11-07 用 winget 安裝，版本 8.0.427；桌機 8.0.425，2026-11-13 確認；`global.json` 要求 8.0.400 以上）。
- 發佈：在 `Photo2Disk\` 執行 `dotnet publish -c Release -o ..\publish`，產出 `publish\照片備份過濾工具.exe`（自包含單一 exe，約 70 MB）。
- 發佈新版給同學（自動更新）：改 `<Version>` → 專案根目錄執行 `python tools/make_release.py`，產出 `release\v<版本>\Photo2Disk.exe` 與 `.sha256` → 建立 Release（Tag `v<版本>`，說明欄寫更新內容）上傳這兩個檔案：桌機已裝 `gh` 並登入 Xnizst（2026-11-13），可用 `"C:\Program Files\GitHub CLI\gh.exe" release create v<版本> release/v<版本>/Photo2Disk.exe release/v<版本>/Photo2Disk.exe.sha256 --repo Xnizst/Photo2Disk --title v<版本> --notes "…"`；筆電沒有 `gh` 時用網頁建立。附件用英文檔名，因為 GitHub 會改寫非英文檔名；更新時沿用使用者電腦上原本的檔名。不要勾 pre-release（程式會略過），也不要漏掉 `.sha256`（沒有就不提示更新）。
- 版本號只寫在 `Photo2Disk.csproj` 的 `<Version>`。
- 介面改版（YAN 2026-11-13，參考 GPT 產生的設計稿，1.3.0）：YAN 選定改用設計稿的亮藍色系（`Theme.Accent` #0B6BD8，取代原本石板藍＋藍綠，這是本專案特例，其他工具仍照 Windows 桌面規則的配色），淺灰藍視窗底、白色圓角卡片；標題區有程式圖示與「照片只在本機處理」標示；備份位置與來源資料左右並排（步驟 1、2）；統計數字卡＋分頁＋搜尋；表格每列勾選框（預設全勾，`SourceItem.Excluded`，複製時略過未勾選）；狀態用膠囊標籤；最下方狀態列。YAN 選擇不顯示縮圖（zip 要解壓、HEIC 需要解碼器、數千張會拖慢）。按鈕全部是自繪圓角（`RoundButton`，YAN 要求「按鈕都要圓潤」）。來源資料的「＋ 加入」放在卡片標題列右側（`SectionPanel.HeaderAction`），兩張卡片高度才對齊。視窗矮時分級收合（`UpdateCompactLayout`，最小化時不動）：高度不到 700（96 DPI 換算）收起數字卡；不到 620 再收起標題區、備份位置說明，來源清單縮成一列、卡片標題列壓低、表格列高 28。表格固定欄寬與列高不會自動跟 DPI 放大，`ApplyGridMetrics` 在載入與 `OnDpiChanged` 時從 96 DPI 基準換算；分頁放不下時自動縮小間距，搜尋欄最多佔四分之一寬。設計稿與漏斗圖示原檔在專案根目錄（`GPT介面設計參考.png`、`photo-backup-filter-icon-light.png`），不進 git。
- 程式圖示：1.3.0 起改用 GPT 產生的漏斗圖示（YAN 選定），`python tools/make_icon.py --from tools/icon_source.png` 清掉幾乎透明的雜點、裁切置中後產生 `Photo2Disk/app.ico`。1.2.1 的自繪圖示（`make_icon.py A`）保留在腳本裡。產生 `Photo2Disk/app.ico`（16～256 像素），csproj 的 `ApplicationIcon` 給 exe 用，同一檔案以 `EmbeddedResource`（`Photo2Disk.app.ico`）給視窗標題列用（`Ui.AppIcon`）。`--preview` 產生比較圖 `tools/icon_preview.png`。

## 架構

- `DedupEngine.cs`：比對與複製的核心，不依賴介面。
  - 比對：列出來源（zip 直接讀、不解壓；資料夾內的 zip 也會展開），掃描備份資料夾，**只有大小相同的檔案才計算雜湊**，不必讀完整顆 1 TB 硬碟。
  - 分類：`New`、`AlreadyBackedUp`、`DuplicateInBatch`（來源資料重複）。
  - 複製：先寫 `.p2d-partial` 暫存檔，邊寫邊算雜湊並與比對時的結果核對，成功後才改名；取消或失敗時刪除暫存檔。檔名衝突加 ` (2)`，不覆蓋任何既有檔案。
  - 效能（相簿約 2 TB，預估 40～60 萬個檔案）：備份端依目錄順序單執行緒讀取（外接傳統硬碟平行讀會更慢）；下載端與備份端在不同磁碟時兩邊同時計算，同一個 zip 切成幾段各自開啟（最多 4 段）；同一顆磁碟時全部依序。
  - 評估過但不採用：先讀檔案開頭 64 KB 排除大小巧合。傳統硬碟每個候選檔多一次尋道（約 10 ms），1,000 個候選約多 10 秒，大於省下的約 4～5 秒（50 萬個檔案、下載 2,000 張時的大小巧合約多讀 500 MB）。
  - 複製流程（2026-11-13 改）：每張先寫成 `<GUID>.p2d-partial`，需要時讀拍攝資訊；全部完成、取消或出錯時，在 `finally` 依拍攝時間排序決定檔名（流水號才會照時間編）再改名，同時寫快取與匯入紀錄。程式異常結束留下的暫存檔，下次複製開始時刪除。
- `MediaMetadata.cs`：自己解析 JPEG／TIFF／PNG 的 EXIF（DateTimeOriginal、Model）、HEIC 的 Exif item（iinf＋iloc）、iPhone MOV 的 Apple keys（`com.apple.quicktime.creationdate`、`model`，優先）、MP4／MOV 的 mvhd（UTC 轉當地時間）、檔名日期，不用第三方套件。讀的是剛寫好的暫存檔，zip 不用再解壓一次。
- `Naming.cs`：`NamingOptions`（存在設定檔 `Naming`）、預設組合、`FileNamer.AssignNames`（流水號依其他文字分組各自編號、跳過已存在檔名；無流水號時撞名加 ` (2)`）。
- `NamingDialog.cs`：檔名規則視窗，撥桿切換、勾選清單排序、細項只顯示用得到的欄位、即時預覽。`Ui.cs` 的 `ToggleSwitch` 是撥桿元件。
- `ReorderCheckList.cs`：檔名組成清單（YAN 2026-11-13 要求支援滑鼠上下拖動）。自繪元件：點一下勾選、按住拖動排序（藍綠插入線）、Ctrl＋上下鍵移動。坑：內建 `CheckedListBox` 按下滑鼠時就改選取與勾選，和拖動衝突，所以不用它。
- `Updater.cs`：自動更新（YAN 2026-11-13）。啟動時查 GitHub「最新 Release」API（10 秒逾時，失敗安靜略過），有新版且不是使用者略過的版本時，主畫面標題下方顯示藍綠提示列（立即更新／略過這一版／更新內容）。按「立即更新」後主畫面霧化（：截圖縮小再放大模糊、蓋一層淺色並擋住操作），另開更新視窗（，YAN 2026-11-13 要求）：確認階段顯示版本、大小、更新內容，按「開始更新」才下載（同學確認後才執行），下載階段顯示圓角進度條、已下載量與剩餘時間，失敗時錯誤訊息顯示在同一個視窗；下載到 `<exe>.download` 並核對 SHA-256，執行中的 exe 改名成 `.old`（執行中不能覆寫但可以改名），新版放回原檔名後重新啟動；下次啟動在背景刪 `.old`。比對或複製進行中不能更新，下載中可按「取消」。設定 `CheckForUpdates`（預設 true，介面上沒有開關）、`SkippedVersion`。環境變數 `PHOTO2DISK_UPDATE_API` 可改 API 網址（測試用）。程式所在資料夾無法寫入時（例如放在 Program Files）會提示搬到自己的資料夾。
- `HashIndex.cs`：雜湊快取，存在備份資料夾根目錄的隱藏檔 `.photo2disk_index.tsv`（相對路徑、大小、修改時間、SHA-256）。大小或修改時間改變就重算；沒有變動時不重寫。
- `MainForm.cs`：介面（WinForms，`AutoScaleMode.Dpi`，以 96 DPI 座標撰寫）。表格的勾選框與狀態標籤在 `CellPainting` 自己畫（不用 `DataGridViewCheckBoxColumn`，虛擬模式下要處理編輯提交，也畫不出設計稿的樣式）。
- `Widgets.cs`：主畫面自繪元件：`PillLabel`、`SourceList`（來源清單，× 移除、滑鼠提示完整路徑、超過兩列捲動）、`StatTile`、`TabStrip`、`SearchBox`（也包住唯讀的備份路徑欄）、`StateLabel`、`Glyphs`（資料夾、zip、勾選框、完成圖示）。坑：視窗顯示前 `Control.Visible` 一律回傳 false，統計卡的顯示狀態要自己記錄。
- `Settings.cs`：記住上次的備份資料夾，存 `%APPDATA%\Photo2Disk\settings.json`；環境變數 `PHOTO2DISK_SETTINGS_DIR` 可改位置（測試用）。

## 測試

- `tests\CoreTests`：核心回歸測試（分類、跨 zip 重複、改名搬移後仍能比對、檔名衝突、損壞 zip、取消不留殘檔、快取失效；檔名規則：用程式產生帶 EXIF 的 JPEG〔兩種位元組順序〕、HEIC、MP4 驗證日期讀取，各種組成、流水號、撞名，以及改名後再比對仍是硬碟已有）。`dotnet run` 執行，最後印出「全部測試通過」。
- `tests\Benchmark`：大量照片效能測試。用 NTFS 稀疏檔模擬真實大小的照片（只寫開頭 4 KB），量目錄掃描、大小巧合與整體流程；量不到真實讀取速度。用法：`Benchmark.exe <測試根目錄> [檔案數] [下載張數] [下載資料夾]`。產生檔案每秒約 88 個（可能是防毒掃描），50 萬個要約 1.5 小時。
  - 2026-11-07 實測（4.3 萬個檔案、下載 2,000 張其中 1,000 張已有）：掃描 0.1 秒；優化前依序 47.5 秒（有快取 19.9 秒），優化後跨磁碟 32.8 秒（有快取 8.9 秒）。
- `tests\MetaProbe`：印出程式從檔案讀到的拍攝時間、來源與型號。用法：`MetaProbe.exe <檔案或資料夾> ...`。同學回報日期不對時，拿那個檔案來跑。
- `tests\Shared\FakeReleaseServer.cs`：本機 HTTP 伺服器，模擬 GitHub Release API 與下載，CoreTests 與 UiTest 共用。CoreTests 測新舊版判斷、缺雜湊檔、預先發行版、下載替換、雜湊不符、取消、連不上伺服器；UiTest 截提示列（`10a`）並替換暫存的假 exe（`10b`），不會動到測試程式本身。
- `tests\UiTest`：編譯主程式原始碼（排除 `Program.cs`），用模擬資料跑「比對 → 複製」並截圖，最後截三種檔名規則視窗（`8a～8c`）與拖動排序（`9a` 拖動中、`9b` 拖動後，用 `SimulateDrag` 模擬滑鼠）。測試時關掉比對後的重複提示與複製完成提示（`ShowDuplicateDialog`、`ShowCopyDoneDialog`），否則訊息框會卡住自動測試。用法：`UiTest.exe <截圖資料夾> [寬 高]`，設定檔導到暫存資料夾。

## 尚未驗證

- **Google 網頁版下載的檔案是否與硬碟裡先前下載的同一張照片逐位元相同**。這是整個比對方式的前提，需要用真實帳號下載同一張照片兩次比對雜湊。若不同（例如 Google 每次下載都改寫中繼資料），要改用拍攝時間＋影像內容比對。
- 在 Google 相簿裡編輯、旋轉過的照片，下載內容與原檔不同，會被當成新照片。
- 1.3.0 版面 2026-11-13 實測（桌機第二台直向螢幕暫時改縮放，`P2D_UITEST_SCREEN=2` 讓 UiTest 開在那台，寬高參數用 96 DPI 單位）：125%（864×576、864×1000）、150%（720×480、720×900）、主螢幕 1920×1080 開 150% 最大化（表格 4 列），全部功能測試通過，含檔名規則視窗與更新提示列。已知限制：1366×768 開 150%（480 高，非 Windows 預設）時表格一次只看得到 1 列，要捲動；檔名規則視窗約 750 寬，比 720 寬的直向螢幕寬（橫向筆電不會發生）。
- 2026-11-13 網路資料查證：
  - 拍攝日期讀取已用 metadata-extractor 專案的真實樣本（GitHub drewnoakes/metadata-extractor-images）驗證 20 個檔案：iPhone 6／8／XR／11 Pro 的 JPEG、HEIC（含 EXIF 放在檔尾的 HEIC），Pixel 2、OnePlus 8、Samsung Note 8、Galaxy S10（HEIC）、Sony 的照片，iPhone 的 MOV，Nokia 6.1、Sony 相機的 MP4。日期與機型全部和 metadata-extractor 的標準答案一致。樣本檔授權未確認，所以沒有放進專案，要重驗時用 `tests\MetaProbe` 重新下載比對。
  - 同時發現 iPhone MOV 的 mvhd 不可靠：一個樣本的 mvhd 是 2019-11-04，Apple 的 `com.apple.quicktime.creationdate` 卻是 2018-04-06（可能是編輯或轉存時間）。已改成優先讀 Apple 欄位（拍攝地當地時間，和照片 EXIF 同義，並讀機型）。依 Apple 文件，mvhd 規定存 UTC；Android 與相機的 MP4 沒有 Apple 欄位，仍用 mvhd 換算成這台電腦的時區，國外拍的影片可能差幾小時。
  - Google 網頁版下載是否逐位元相同：查不到可靠來源。搜尋摘要提到 Google issue tracker 有使用者回報網頁版下載與上傳檔相同（需要登入，無法讀原文），另一篇 2024 年的部落格作者只寫「我相信原始畫質下載與上傳時相同」，沒有實測。也有使用者回報 Google 改寫了下載影片內的時間。仍需真實帳號實測（見下一點）。
- 仍未驗證：Google 網頁版下載的照片與影片，內嵌的拍攝時間是否保留（網路上有影片時間被改寫的回報）。
- 真實外接硬碟的掃描與比對耗時尚未實測：Benchmark 只量到系統快取已有目錄資訊時的掃描速度，外接傳統硬碟剛接上時會比較慢；讀取速度以傳統硬碟約 100～150 MB/s 估算。
- 自動更新 2026-11-13 已對真正的 GitHub 驗證：v1.2.0 Release 發佈後，以 1.1.1 查詢得到新版、以 1.2.0 查詢不提示，下載替換後的檔案 SHA-256 與本機發佈檔相同。同日用 v1.2.1 Release 實測整段流程（桌機、設定導到暫存資料夾的 1.2.0 複本）：開啟後出現提示列、確認視窗顯示更新內容與 68.5 MB，按「是」後重新啟動成 1.2.1，exe 雜湊與 Release 相同、`.old` 已刪除、標題列有新圖示，Windows Defender 沒有攔截。尚未實測：同學的電腦。
