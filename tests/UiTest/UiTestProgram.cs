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
                var uncheckedOk = form.CopyButtonText.Contains("2") && form.SelectionText.Contains("1 張未勾選");
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

                // 檔名規則視窗：保留原檔名、拍攝年月-原檔名、自訂文字-拍攝日期-流水號
                var cases = new List<(string Label, NamingOptions Options)> { ("8a_檔名規則_保留原檔名", new NamingOptions()) };
                var byMonth = new NamingOptions();
                byMonth.Apply(NamingOptions.Presets[0]);
                cases.Add(("8b_檔名規則_拍攝年月-原檔名", byMonth));
                var custom = new NamingOptions();
                custom.Apply(NamingOptions.Presets.Single(p => p.Name == "自訂文字-拍攝日期-流水號"));
                custom.CustomText = "日本旅遊";
                cases.Add(("8c_檔名規則_自訂文字-日期-流水號", custom));
                foreach (var (label, opts) in cases)
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
                    Shot(dlg, Path.Combine(outDir, label + ".png"));
                    log.Add($"{label} size={dlg.Size} 確認視窗說明：{MainForm.NamingLine(opts)}");
                    dlg.Close();
                }

                // 拖動排序：拍攝日期、原檔名 → 把「原檔名」拖到最上面；再點一下勾選「流水號」
                var dragOpts = new NamingOptions();
                dragOpts.Apply(NamingOptions.Presets[0]);
                using (var dlg = new NamingDialog(dragOpts) { StartPosition = FormStartPosition.Manual, Location = Origin + new Size(20, 20), TopMost = true })
                {
                    dlg.Show(form);
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
