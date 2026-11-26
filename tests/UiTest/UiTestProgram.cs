using System.Drawing.Imaging;
using System.IO.Compression;

namespace Photo2Disk.UiTest;

// 介面測試：用模擬資料跑「比對 → 複製」，各階段截圖。
// 用法：UiTest.exe <截圖輸出資料夾> [視窗寬 視窗高]
// 設定檔導到暫存資料夾，不會動到正式設定。
internal static class UiTestProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "p2d_ui");
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(Path.GetTempPath(), "p2d_uitest_" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("PHOTO2DISK_SETTINGS_DIR", Path.Combine(work, "settings"));
        var log = new List<string>();

        var (backup, sources) = MakeData(work);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();
        MainForm.AutoCheckUpdates = false; // 不連真正的 GitHub
        var form = new MainForm();
        // 環境變數 P2D_UITEST_SCREEN=2：視窗開在非主要螢幕（用來測不同縮放）；寬高參數改用 96 DPI 單位，
        // 並放寬最小尺寸（程式在小螢幕上本來就會把最小尺寸縮到工作區大小）。
        var otherScreen = Environment.GetEnvironmentVariable("P2D_UITEST_SCREEN") == "2"
            ? Screen.AllScreens.FirstOrDefault(sc => !sc.Primary)
            : null;
        Origin = (otherScreen?.WorkingArea.Location ?? Point.Empty) + new Size(20, 20);
        Size? dipSize = null;
        if (args.Length >= 3)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = Origin;
            if (otherScreen is null)
            {
                form.Size = new Size(int.Parse(args[1]), int.Parse(args[2]));
            }
            else
            {
                dipSize = new Size(int.Parse(args[1]), int.Parse(args[2]));
            }
        }
        var exitCode = 0;
        form.Shown += async (_, _) =>
        {
            try
            {
                form.TopMost = true;
                if (dipSize is { } ds)
                {
                    form.WindowState = FormWindowState.Normal;
                    form.MinimumSize = Size.Empty;
                    var k = form.DeviceDpi / 96.0;
                    form.Size = new Size((int)(ds.Width * k), (int)(ds.Height * k));
                    await Settle();
                }

                // 「加入…」視窗結果的判斷：選檔案 → 檔案；檔名欄維持預設文字 → 所在資料夾；不存在 → 忽略
                var zipFile = sources[0];
                var dlFolder = Path.GetDirectoryName(zipFile)!;
                var resolved = MainForm.ResolveDialogSelection(new[]
                {
                    zipFile,
                    Path.Combine(dlFolder, MainForm.FolderPlaceholder),
                    Path.Combine(dlFolder, "不存在.zip"),
                    zipFile,
                });
                var resolveOk = resolved.Count == 2 && resolved[0] == zipFile && resolved[1] == dlFolder;
                log.Add($"加入判斷={resolveOk}（{string.Join(" | ", resolved)}）");
                if (!resolveOk)
                {
                    exitCode = 1;
                }

                // 實際打開視窗，在資料夾內直接按「開啟」→ 應加入該資料夾
                var probed = DialogProbe.Run(dlFolder, log);
                var probeOk = probed.Count == 1 && string.Equals(probed[0], dlFolder, StringComparison.OrdinalIgnoreCase);
                log.Add($"實際視窗加入資料夾={probeOk}");
                if (!probeOk)
                {
                    exitCode = 1;
                }

                await Settle();
                Shot(form, Path.Combine(outDir, "1_初始.png"));
                var settingsPrompts = new List<string>();
                var applyBackupSettings = false;
                form.ConfirmBackupSettings = msg =>
                {
                    settingsPrompts.Add(msg);
                    return applyBackupSettings;
                };
                form.SetBackupFolder(backup);
                form.AddSources(sources);
                await Settle();
                Shot(form, Path.Combine(outDir, "2_已選擇.png"));
                form.ShowDuplicateDialog = false;
                form.ShowCopyDoneDialog = false;
                await form.AnalyzeAsync();
                await Settle();
                Shot(form, Path.Combine(outDir, "3_比對完成.png"));
                var rows = form.VisibleRowCount;
                var dupLabel = form.DuplicateLabelText;
                log.Add($"比對後顯示列數={rows}（應為 10，重複的那張也列出） 重複標示={dupLabel}");
                if (rows != 10 || !dupLabel.Contains("1 張"))
                {
                    exitCode = 1;
                }
                var dupMsg = MainForm.DuplicateMessage(form.CurrentAnalysis!);
                File.WriteAllText(Path.Combine(outDir, "重複提示.txt"), dupMsg);
                form.SelectFilter(4);
                await Settle();
                Shot(form, Path.Combine(outDir, "3b_來源資料重複.png"));
                var dupRows = form.VisibleRowCount;
                log.Add($"篩選來源資料重複列數={dupRows}（應為 1） 提示內容含檔名={dupMsg.Contains("PXL_20251103_190233001(1).jpg")}");
                if (dupRows != 1 || !dupMsg.Contains("PXL_20251103_190233001(1).jpg"))
                {
                    exitCode = 1;
                }
                form.SelectFilter(0);

                // 修改後檔名：保留原檔名時和原檔名相同；套用「拍攝日期@原檔名」後馬上換成預計檔名
                var keepName = form.CellText(0, MainForm.NewNameColumn) == form.CellText(0, 1);
                var atNaming = new NamingOptions();
                atNaming.Apply(NamingOptions.Presets.Single(p => p.Name == "拍攝日期@原檔名"));
                form.ApplyNaming(atNaming);
                await Settle();
                var newNames = Enumerable.Range(0, 3).Select(r => form.CellText(r, MainForm.NewNameColumn)).ToList();
                var plannedOk = newNames.All(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{8}@"));
                form.HoverLocationButton(0, preview: true);
                await Settle();
                Shot(form, Path.Combine(outDir, "3d_修改後檔名與預覽按鈕.png"));
                log.Add($"修改後檔名：保留原檔名時相同={keepName} 套用拍攝日期@原檔名後={string.Join("、", newNames)}");
                if (!keepName || !plannedOk)
                {
                    exitCode = 1;
                }

                // 來源位置的兩個按鈕：資料夾 → 選取 zip；預覽 → zip 內的檔案解到暫存資料夾再開
                var shell = new List<(string Verb, string Path)>();
                form.ShellOverride = (verb, path) => shell.Add((verb, path));
                await form.ClickLocationButtonAsync(0, preview: false);
                await form.ClickLocationButtonAsync(0, preview: true);
                form.ShellOverride = null;
                var shellOk = shell.Count == 2
                              && shell[0] == ("select", sources[0])
                              && shell[1].Verb == "open" && File.Exists(shell[1].Path)
                              && Path.GetFileName(shell[1].Path) == form.CellText(0, 1)
                              && shell[1].Path.StartsWith(MainForm.PreviewRoot, StringComparison.OrdinalIgnoreCase);
                log.Add($"來源位置按鈕={shellOk}（{string.Join(" | ", shell.Select(s => s.Verb + " " + s.Path))}）");
                if (!shellOk)
                {
                    exitCode = 1;
                }

                // 搜尋：PXL 開頭的有 4 個（含重複的那個）
                form.SetSearch("PXL");
                await Settle();
                var searchRows = form.VisibleRowCount;
                form.SetSearch("");
                await Settle();
                // 勾選：新照片分頁取消第一列 → 複製按鈕變成 2 張，再勾回來
                form.SelectFilter(1);
                await Settle();
                form.SetChecked(0, false);
                await Settle();
                Shot(form, Path.Combine(outDir, "3c_取消勾選.png"));
                var uncheckedOk = form.CopyButtonText.Contains("2") && form.SelectionText.Contains("1 張未勾選")
                                  && form.CellText(0, MainForm.NewNameColumn) == ""; // 未勾選不會複製，沒有修改後檔名
                log.Add($"搜尋 PXL 列數={searchRows}（應為 4） 取消勾選後按鈕={form.CopyButtonText} 說明={form.SelectionText}");
                form.SetChecked(0, true);
                form.SelectFilter(0);
                if (searchRows != 4 || !uncheckedOk || !form.CopyButtonText.Contains("3"))
                {
                    exitCode = 1;
                }
                await form.CopyAsync(confirm: false);
                await Settle();
                Shot(form, Path.Combine(outDir, "4_複製完成.png"));
                var copied = Directory.EnumerateFiles(Path.Combine(backup, DedupEngine.StagingFolderName), "*", SearchOption.AllDirectories).Count();
                log.Add($"dpi={form.DeviceDpi} size={form.Size} compact={form.CompactLevel} copied={copied}");
                if (copied != 3)
                {
                    exitCode = 1;
                }

                // 備份資料夾的設定檔：一開始沒有不問；複製後改回預設設定再選一次 → 詢問並套用；設定相同也問
                var promptsBeforeCopy = settingsPrompts.Count;
                form.ApplyNaming(new NamingOptions());
                applyBackupSettings = true;
                form.SetBackupFolder(backup);
                var appliedOk = settingsPrompts.Count == promptsBeforeCopy + 1
                                && settingsPrompts[^1].Contains("目前的設定：")
                                && BackupSettingsFile.SameAs(form.CurrentNaming, atNaming);
                applyBackupSettings = false;
                form.SetBackupFolder(backup);
                var sameAskedOk = settingsPrompts.Count == promptsBeforeCopy + 2 && settingsPrompts[^1].Contains("和目前的設定相同");
                File.WriteAllText(Path.Combine(outDir, "套用設定詢問.txt"), string.Join(Environment.NewLine + "----" + Environment.NewLine, settingsPrompts));
                log.Add($"備份資料夾設定：複製前詢問次數={promptsBeforeCopy}（應為 0） 不同時詢問並套用={appliedOk} 相同也詢問={sameAskedOk}");
                if (promptsBeforeCopy != 0 || !appliedOk || !sameAskedOk)
                {
                    exitCode = 1;
                }

                // 重複選同一個壓縮檔：要有提示，重新比對後全部是硬碟已有
                form.AddSources(sources);
                await Settle();
                Shot(form, Path.Combine(outDir, "5_重複加入.png"));
                var hinted = form.StatusLine.Contains("已經在清單中") && form.CurrentAnalysis is null;
                log.Add($"重複加入提示={hinted} 狀態列={form.StatusLine}");
                await form.AnalyzeAsync();
                await Settle();
                Shot(form, Path.Combine(outDir, "6_重新比對.png"));
                var a = form.CurrentAnalysis;
                var allExisting = a is not null && a.Count(ItemStatus.New) == 0 && a.Count(ItemStatus.AlreadyBackedUp) == 9 && a.Count(ItemStatus.DuplicateInBatch) == 1;
                log.Add($"重新比對全部硬碟已有={allExisting}（新 {a?.Count(ItemStatus.New)}，已有 {a?.Count(ItemStatus.AlreadyBackedUp)}，重複 {a?.Count(ItemStatus.DuplicateInBatch)}）");
                if (!hinted || !allExisting)
                {
                    exitCode = 1;
                }

                // 刪掉一張已匯入的照片 → 重新比對要標示曾遺失
                var victim = Directory.EnumerateFiles(Path.Combine(backup, DedupEngine.StagingFolderName), "*.mp4", SearchOption.AllDirectories).First();
                File.Delete(victim);
                await form.AnalyzeAsync();
                await Settle();
                Shot(form, Path.Combine(outDir, "7_曾遺失.png"));
                var a2 = form.CurrentAnalysis;
                var missingOk = a2 is not null && a2.Items.Count(i => i.IsMissingBefore) == 1 && form.StatusLine.Contains("曾遺失");
                log.Add($"曾遺失標示={missingOk} 狀態列={form.StatusLine}");
                if (!missingOk)
                {
                    exitCode = 1;
                }

                // 更多設定視窗（分頁 0 資料夾、1 檔名、2 DNG 轉檔）：每個案例截相關的分頁，並檢查切換分頁時視窗大小不變
                var cases = new List<(string Label, NamingOptions Options, int[] Pages)>
                {
                    ("8a_更多設定_預設", new NamingOptions(), new[] { 0, 1, 2 }),
                };
                var byMonth = new NamingOptions { Folders = FolderMode.YearMonth };
                byMonth.Apply(NamingOptions.Presets[0]);
                cases.Add(("8b_更多設定_年份月份_拍攝年月-原檔名", byMonth, new[] { 0, 1 }));
                var custom = new NamingOptions();
                custom.Apply(NamingOptions.Presets.Single(p => p.Name == "自訂文字-拍攝日期-流水號"));
                custom.CustomText = "日本旅遊";
                cases.Add(("8c_更多設定_自訂文字-日期-流水號", custom, new[] { 1 }));
                cases.Add(("8d_更多設定_依複製日期", new NamingOptions { Folders = FolderMode.CopyDate }, new[] { 0 }));
                cases.Add(("8e_更多設定_DNG轉檔開啟", new NamingOptions { ConvertDng = true }, new[] { 2 }));
                var atName = new NamingOptions { Folders = FolderMode.YearThenMonth };
                atName.Apply(NamingOptions.Presets.Single(p => p.Name == "拍攝日期@原檔名"));
                cases.Add(("8f_更多設定_年份月份_拍攝日期@原檔名", atName, new[] { 0, 1 }));
                var customSep = new NamingOptions { Enabled = true, DateFormat = "yyyy.MM.dd", Separator = "#" };
                cases.Add(("8g_更多設定_句點日期_自訂分隔", customSep, new[] { 1 }));
                foreach (var (label, opts, pages) in cases)
                {
                    using var dlg = new NamingDialog(opts)
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = Origin + new Size(20, 20),
                        TopMost = true,
                    };
                    dlg.Show(form);
                    await Settle();
                    await Task.Delay(300); // 等撥桿動畫結束
                    var firstSize = dlg.Size;
                    foreach (var page in pages)
                    {
                        dlg.ShowPage(page);
                        await Settle();
                        Shot(dlg, Path.Combine(outDir, $"{label}_{page}{NamingDialog.PageNames[page]}.png"));
                        if (dlg.Size != firstSize)
                        {
                            log.Add($"{label} 切到「{NamingDialog.PageNames[page]}」時視窗大小改變：{firstSize} → {dlg.Size}");
                            exitCode = 1;
                        }
                    }
                    log.Add($"{label} size={dlg.Size} 確認視窗說明：{MainForm.FolderLine(opts)} {MainForm.NamingLine(opts)}");
                    dlg.Close();
                }

                // 預設開啟後才切到「依規則改名」（細項設定原本一開始隱藏，2026-11-26 曾因此錯位）
                using (var dlgToggle = new NamingDialog(new NamingOptions())
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = Origin + new Size(20, 20),
                    TopMost = true,
                })
                {
                    dlgToggle.Show(form);
                    await Settle();
                    dlgToggle.ShowPage(1);
                    await Settle();
                    var before = dlgToggle.Size;
                    dlgToggle.RenameToggle.Checked = true;
                    await Settle();
                    await Task.Delay(300);
                    Shot(dlgToggle, Path.Combine(outDir, "8h_更多設定_開啟後切到依規則改名.png"));
                    if (dlgToggle.Size != before)
                    {
                        log.Add($"切到依規則改名時視窗大小改變：{before} → {dlgToggle.Size}");
                        exitCode = 1;
                    }
                    dlgToggle.Close();
                }

                // 拖動排序：拍攝日期、原檔名 → 把「原檔名」拖到最上面；再點一下勾選「流水號」
                var dragOpts = new NamingOptions();
                dragOpts.Apply(NamingOptions.Presets[0]);
                using (var dlg = new NamingDialog(dragOpts) { StartPosition = FormStartPosition.Manual, Location = Origin + new Size(20, 20), TopMost = true })
                {
                    dlg.Show(form);
                    await Settle();
                    dlg.ShowPage(1);
                    await Settle();
                    await Task.Delay(300);
                    var list = dlg.PartsList;
                    list.SimulateDrag(1, 3, stopBeforeRelease: true);
                    await Settle();
                    Shot(dlg, Path.Combine(outDir, "9a_拖動中.png"));
                    var midDrag = list.IsDragging;
                    list.SimulateDrag(1, 3, stopBeforeRelease: false);
                    await Settle();
                    var order = string.Join(",", dlg.Result.ActiveParts);
                    var seqIndex = list.Items.ToList().FindIndex(i => (NamePartKind)i.Tag == NamePartKind.Sequence);
                    list.SimulateClick(seqIndex);
                    // 往下拖超過最後一列：移到最底
                    list.SimulateDrag(0, list.Height + 50, stopBeforeRelease: false);
                    await Settle();
                    Shot(dlg, Path.Combine(outDir, "9b_拖動後.png"));
                    var after = string.Join(",", dlg.Result.ActiveParts);
                    var lastKind = (NamePartKind)list.Items[^1].Tag;
                    var dragOk = midDrag && order == "OriginalName,Date" && after == "Date,Sequence,OriginalName"
                                 && lastKind == NamePartKind.OriginalName && !list.IsDragging;
                    log.Add($"拖動排序={dragOk}（拖動中={midDrag}，拖到最上面後={order}，勾流水號並拖到最底後={after}）");
                    if (!dragOk)
                    {
                        exitCode = 1;
                    }
                    dlg.Close();
                }

                // 自動更新：本機模擬 GitHub，有新版 → 提示列 → 下載替換（替換暫存的假 exe，不動測試程式本身）
                using (var server = new Photo2Disk.TestSupport.FakeReleaseServer())
                {
                    Environment.SetEnvironmentVariable("PHOTO2DISK_UPDATE_API", server.ApiUrl);
                    var newExe = new byte[4_000_000];
                    new Random(7).NextBytes(newExe);
                    server.SetRelease("v1.2.0", newExe, "- 新增自動更新" + Environment.NewLine + "- 修正檔名規則預覽");
                    var info = await Updater.CheckAsync(new Version(1, 1, 1), CancellationToken.None);
                    form.ShowUpdateAvailable(info!);
                    await Settle();
                    Shot(form, Path.Combine(outDir, "10a_有新版本.png"));
                    var fakeExe = Path.Combine(work, "app", "照片備份過濾工具.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(fakeExe)!);
                    File.WriteAllBytes(fakeExe, new byte[1000]);
                    // 更新視窗的確認階段（霧化＋更新內容）
                    using (new FrostLayer(form))
                    using (var confirmDlg = new UpdateDialog(info!, MainForm.VersionText))
                    {
                        confirmDlg.ShowOver(form);
                        await Settle();
                        ShotScreenArea(form.Bounds, Path.Combine(outDir, "10b_更新確認.png"));
                        confirmDlg.CloseQuietly();
                    }
                    // 更新視窗與霧化：下載前把進度設成 42% 截圖，完成時再截一張
                    form.OnUpdateDialogShown = async d =>
                    {
                        d.SetProgress(0.42);
                        await Settle();
                        ShotScreenArea(form.Bounds, Path.Combine(outDir, "10c_更新進度.png"));
                    };
                    var doneText = "";
                    form.OnUpdateDone = async d =>
                    {
                        await Settle();
                        doneText = d.ProgressText;
                        ShotScreenArea(form.Bounds, Path.Combine(outDir, "10d_更新完成.png"));
                    };
                    await form.UpdateNowAsync(confirm: false, targetExe: fakeExe, restart: false);
                    log.Add($"更新視窗完成文字={doneText}");
                    await Settle();
                    Shot(form, Path.Combine(outDir, "10b_更新完成.png"));
                    var replaced = File.ReadAllBytes(fakeExe).AsSpan().SequenceEqual(newExe) && File.Exists(fakeExe + ".old");
                    var updOk = info is not null && replaced && !form.UpdateBarVisible && form.LastUpdateError is null;
                    log.Add($"自動更新={updOk}（替換={replaced}，提示列隱藏={!form.UpdateBarVisible}，錯誤={form.LastUpdateError}，狀態列={form.StatusLine}）");
                    if (!updOk)
                    {
                        exitCode = 1;
                    }
                    Environment.SetEnvironmentVariable("PHOTO2DISK_UPDATE_API", null);
                }
            }
            catch (Exception ex)
            {
                log.Add("EXCEPTION " + ex);
                exitCode = 2;
            }
            finally
            {
                File.WriteAllLines(Path.Combine(outDir, "log.txt"), log);
                form.Close();
            }
        };
        Application.Run(form);
        try
        {
            Directory.Delete(work, true);
        }
        catch (IOException)
        {
        }
        return exitCode;
    }

    private static Point Origin = new(20, 20);

    private static async Task Settle()
    {
        Application.DoEvents();
        await Task.Delay(400);
        Application.DoEvents();
    }

    private static void ShotScreenArea(Rectangle b, string path)
    {
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private static void Shot(Form form, string path)
    {
        var b = form.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private static (string Backup, string[] Sources) MakeData(string work)
    {
        var rng = new Random(1);
        byte[] Bytes(int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }
        var backup = Path.Combine(work, "外接硬碟", "照片");
        var dl = Path.Combine(work, "下載");
        Directory.CreateDirectory(Path.Combine(backup, "2024 家庭旅遊"));
        Directory.CreateDirectory(dl);
        var existing = Enumerable.Range(0, 6).Select(_ => Bytes(rng.Next(200_000, 3_000_000))).ToList();
        for (var i = 0; i < existing.Count; i++)
        {
            File.WriteAllBytes(Path.Combine(backup, "2024 家庭旅遊", $"IMG_20240{i + 1}15_1030{i}.jpg"), existing[i]);
        }
        var n1 = Bytes(2_400_000);
        var n2 = Bytes(1_800_000);
        var n3 = Bytes(15_000_000);
        var zip = Path.Combine(dl, "Photos-001.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
            for (var i = 0; i < existing.Count; i++)
            {
                Add($"IMG_20240{i + 1}15_1030{i}.jpg", existing[i]);
            }
            Add("PXL_20251102_081512345.jpg", n1);
            Add("PXL_20251103_190233001.jpg", n2);
            Add("PXL_20251103_190233001(1).jpg", n2);
            Add("PXL_20251105_120000999.mp4", n3);
        }
        return (backup, new[] { zip });
    }
}
