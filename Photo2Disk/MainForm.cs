using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Photo2Disk;

public sealed class MainForm : Form
{
    private const string AppTitle = "照片備份過濾工具";

    private readonly ToolTip tip = new() { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };
    private readonly Settings settings = Settings.Load();

    private readonly TextBox txtBackup = new();
    private readonly ListBox lstSources = new();
    private readonly Button btnBrowseBackup = Ui.MakeButton("選擇資料夾…", false);
    private readonly Button btnAdd = Ui.MakeButton("加入…", false);
    private readonly Button btnRemove = Ui.MakeButton("移除", false);

    private readonly Button btnAnalyze = Ui.MakeButton("① 開始比對", true);
    private readonly Button btnCopy = Ui.MakeButton("② 複製新照片", true);
    private readonly Button btnNaming = Ui.MakeButton("檔名規則…", false);
    private readonly Button btnCancel = Ui.MakeButton("取消", false);
    private readonly ProgressBar progressBar = new();
    private readonly Label lblStatus = new();

    private readonly Label lblNew = new();
    private readonly Label lblExisting = new();
    private readonly Label lblMissing = new();
    private readonly Label lblDuplicate = new();
    private readonly Label lblHint = new();
    private readonly ComboBox cboFilter = new();
    private readonly DataGridView grid = new();
    private readonly Button btnExport = Ui.MakeButton("匯出清單…", false);
    private readonly Button btnOpenFolder = Ui.MakeButton("開啟待整理資料夾", false);

    private readonly Panel updateBar = new();
    private readonly Label lblUpdate = new();
    private readonly LinkLabel lnkUpdateNotes = new();
    private readonly Button btnUpdate = Ui.MakeButton("立即更新", true);
    private readonly Button btnSkipUpdate = Ui.MakeButton("略過這一版", false);
    private UpdateInfo? pendingUpdate;

    private AnalysisResult? analysis;
    private List<SourceItem> view = new();
    private CancellationTokenSource? cts;
    private bool busy;
    private bool closeAfterCancel;
    private string? lastOutputFolder;
    private string currentStage = "";
    private readonly Stopwatch stageWatch = new();
    private readonly Font boldCellFont = new(Theme.FontName, 10f, FontStyle.Bold);

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(Theme.FontName, 10f);
        ForeColor = Theme.TextDark;
        BackColor = Color.White;
        Text = AppTitle + " v" + VersionText;
        Icon = Ui.AppIcon ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 760);
        MinimumSize = new Size(820, 640);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 14, 16, 12),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildBackupSection(), 0, 0);
        body.Controls.Add(BuildSourcesSection(), 0, 1);
        body.Controls.Add(BuildActionRow(), 0, 2);
        body.Controls.Add(BuildResultsSection(), 0, 3);

        Controls.Add(body);
        Controls.Add(BuildUpdateBar());
        Controls.Add(BuildHeader());

        ResumeLayout(false);
        PerformLayout();

        if (!string.IsNullOrEmpty(settings.BackupFolder))
        {
            txtBackup.Text = settings.BackupFolder;
        }
        ResetResults();
        UpdateButtons();
    }

    // ================================================================ 版面

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            BackColor = Theme.Primary,
            Padding = new Padding(20, 10, 20, 10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Primary,
        };
        stack.Controls.Add(new Label
        {
            Text = AppTitle,
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font(Theme.FontName, 15f, FontStyle.Bold),
            Margin = new Padding(0),
        });
        stack.Controls.Add(new Label
        {
            Text = "比對需匯入的來源資料，只把外接硬碟裡還沒有的照片放進去",
            AutoSize = true,
            ForeColor = Theme.HeaderSub,
            Margin = new Padding(1, 2, 0, 0),
        });
        header.Controls.Add(stack);
        return header;
    }

    private Control BuildBackupSection()
    {
        var section = new SectionPanel("已備份照片的資料夾",
            "外接硬碟上放照片的資料夾，裡面的子資料夾也會一起比對。" + Environment.NewLine
            + "新照片會複製到這個資料夾裡的「_待整理\\日期」資料夾，整理時可以隨意搬移、改名，不影響下次比對。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        txtBackup.ReadOnly = true;
        txtBackup.TabStop = false;
        txtBackup.BackColor = Color.White;
        txtBackup.Dock = DockStyle.Fill;
        txtBackup.PlaceholderText = "請選擇外接硬碟上放照片的資料夾";
        txtBackup.Margin = new Padding(0, 4, 8, 0);
        txtBackup.AllowDrop = true;
        txtBackup.DragEnter += (_, e) => e.Effect = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Link : DragDropEffects.None;
        txtBackup.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0 && Directory.Exists(paths[0]))
            {
                SetBackupFolder(paths[0]);
            }
        };
        btnBrowseBackup.Margin = new Padding(0);
        btnBrowseBackup.Click += (_, _) => BrowseBackup();

        row.Controls.Add(txtBackup, 0, 0);
        row.Controls.Add(btnBrowseBackup, 1, 0);
        section.Controls.Add(row);
        return section;
    }

    private Control BuildSourcesSection()
    {
        var section = new SectionPanel("需匯入的來源資料",
            "可以加入資料夾或 zip 壓縮檔（例如從 Google 相簿下載的 zip），壓縮檔不用先解壓縮。" + Environment.NewLine
            + "按「加入…」：選壓縮檔（可一次選多個）；要加入整個資料夾時，進入該資料夾後直接按「開啟」。" + Environment.NewLine
            + "也可以把壓縮檔或資料夾直接拖曳到清單裡，程式會自動判斷。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        lstSources.Dock = DockStyle.Fill;
        lstSources.Height = 70;
        lstSources.IntegralHeight = false;
        lstSources.HorizontalScrollbar = true;
        lstSources.SelectionMode = SelectionMode.MultiExtended;
        lstSources.BorderStyle = BorderStyle.FixedSingle;
        lstSources.Margin = new Padding(0, 0, 8, 0);
        lstSources.AllowDrop = true;
        lstSources.DragEnter += (_, e) => e.Effect = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        lstSources.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddSources(paths);
            }
        };
        lstSources.SelectedIndexChanged += (_, _) => UpdateButtons();
        lstSources.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedSources();
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                for (var i = 0; i < lstSources.Items.Count; i++)
                {
                    lstSources.SetSelected(i, true);
                }
                e.SuppressKeyPress = true;
            }
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        foreach (var b in new[] { btnAdd, btnRemove })
        {
            b.MinimumSize = Size.Empty;
            b.AutoSize = false;
            b.Size = new Size(118, 32);
            b.Padding = new Padding(0);
            b.Margin = new Padding(0, b == btnAdd ? 0 : 6, 0, 0);
            buttons.Controls.Add(b);
        }
        btnAdd.Click += (_, _) => BrowseSources();
        btnRemove.Click += (_, _) => RemoveSelectedSources();
        tip.SetToolTip(btnAdd, "選壓縮檔；要加入整個資料夾時，進入該資料夾後直接按「開啟」");
        tip.SetToolTip(btnRemove, "移除清單中選取的項目（Ctrl+A 全選，Delete 鍵也可以移除）");

        row.Controls.Add(lstSources, 0, 0);
        row.Controls.Add(buttons, 1, 0);
        section.Controls.Add(row);
        return section;
    }

    private Control BuildActionRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var b in new[] { btnAnalyze, btnCopy })
        {
            b.AutoSize = false;
            b.Size = new Size(150, 38);
            b.Margin = new Padding(0, 0, 10, 0);
        }
        btnAnalyze.Click += async (_, _) => await AnalyzeAsync();
        btnCopy.Click += async (_, _) => await CopyAsync(confirm: true);
        tip.SetToolTip(btnAnalyze, "找出來源資料中，哪些已經在硬碟裡（只讀取，不會改動任何檔案）");
        tip.SetToolTip(btnCopy, "把硬碟裡還沒有的照片複製到「_待整理」資料夾");
        btnNaming.AutoSize = false;
        btnNaming.Size = new Size(110, 38);
        btnNaming.Margin = new Padding(0, 0, 10, 0);
        btnNaming.Click += (_, _) => EditNaming();
        UpdateNamingTip();

        var progressArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(4, 0, 10, 0),
            Height = 38,
        };
        progressArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        progressArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progressBar.Dock = DockStyle.Fill;
        progressBar.Maximum = 1000;
        progressBar.Margin = new Padding(0, 2, 0, 0);
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.AutoEllipsis = true;
        lblStatus.ForeColor = Theme.TextGray;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.Margin = new Padding(0);
        progressArea.Controls.Add(progressBar, 0, 0);
        progressArea.Controls.Add(lblStatus, 0, 1);

        btnCancel.AutoSize = false;
        btnCancel.Size = new Size(90, 38);
        btnCancel.Margin = new Padding(0);
        btnCancel.Click += (_, _) =>
        {
            cts?.Cancel();
            lblStatus.Text = "正在取消…";
        };

        row.Controls.Add(btnAnalyze, 0, 0);
        row.Controls.Add(btnCopy, 1, 0);
        row.Controls.Add(btnNaming, 2, 0);
        row.Controls.Add(progressArea, 3, 0);
        row.Controls.Add(btnCancel, 4, 0);
        return row;
    }

    private Control BuildResultsSection()
    {
        var section = new SectionPanel("比對結果",
            "新照片：硬碟裡還沒有，按「複製新照片」會放進待整理資料夾。" + Environment.NewLine
            + "硬碟已有：內容和硬碟裡某個檔案完全相同（就算檔名不同也算），不會複製。" + Environment.NewLine
            + "曾遺失：以前用這個程式匯入過，但現在硬碟裡找不到（可能被刪除或搬走），會再複製一次補回來。" + Environment.NewLine
            + "來源資料重複：同一張照片在來源資料裡出現好幾次，只會複製第一份；點一下張數可以看是哪些檔案。" + Environment.NewLine
            + "比對看的是檔案內容，在 Google 相簿編輯或旋轉過的照片會被當成新照片。" + Environment.NewLine
            + "依「檔名規則」改名時，找不到拍攝時間的照片會在「硬碟中對應的檔案」欄註明日期從哪裡來。",
            tip)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var summary = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        lblNew.ForeColor = Theme.Teal;
        lblNew.Font = new Font(Theme.FontName, 10.5f, FontStyle.Bold);
        lblExisting.ForeColor = Theme.TextGray;
        lblMissing.ForeColor = Theme.Amber;
        lblMissing.Font = new Font(Theme.FontName, 10.5f, FontStyle.Bold);
        lblDuplicate.ForeColor = Theme.Amber;
        lblDuplicate.Font = new Font(Theme.FontName, 10.5f, FontStyle.Bold | FontStyle.Underline);
        lblDuplicate.Cursor = Cursors.Hand;
        lblDuplicate.Visible = false;
        lblDuplicate.Click += (_, _) =>
        {
            if (cboFilter.Enabled)
            {
                cboFilter.SelectedIndex = FilterDuplicate;
            }
        };
        tip.SetToolTip(lblDuplicate, "同一張照片在來源資料裡出現好幾次，只會複製第一份。點一下列出是哪些檔案。");
        foreach (var l in new[] { lblNew, lblMissing, lblExisting, lblDuplicate })
        {
            l.AutoSize = true;
            l.Margin = new Padding(0, 0, 28, 0);
            summary.Controls.Add(l);
        }

        var gridHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        SetupGrid();
        lblHint.Dock = DockStyle.Fill;
        lblHint.TextAlign = ContentAlignment.MiddleCenter;
        lblHint.ForeColor = Theme.TextGray;
        lblHint.BackColor = Theme.Surface;
        gridHost.Controls.Add(grid);
        gridHost.Controls.Add(lblHint);
        lblHint.BringToFront();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lblFilter = new Label
        {
            Text = "顯示：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 4, 0),
        };
        cboFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        cboFilter.Items.AddRange(new object[] { "全部", "新照片", "曾遺失", "硬碟已有", "來源資料重複" });
        cboFilter.SelectedIndex = 0;
        cboFilter.Width = 130;
        cboFilter.Anchor = AnchorStyles.Left;
        cboFilter.Margin = new Padding(0);
        cboFilter.SelectedIndexChanged += (_, _) => RefreshGrid();
        btnExport.Anchor = AnchorStyles.Right;
        btnOpenFolder.Anchor = AnchorStyles.Right;
        btnOpenFolder.Margin = new Padding(0);
        btnExport.Click += (_, _) => ExportCsv();
        btnOpenFolder.Click += (_, _) => OpenOutputFolder();
        tip.SetToolTip(btnExport, "把比對結果存成 CSV 檔，可用 Excel 開啟");
        bottom.Controls.Add(lblFilter, 0, 0);
        bottom.Controls.Add(cboFilter, 1, 0);
        bottom.Controls.Add(btnExport, 3, 0);
        bottom.Controls.Add(btnOpenFolder, 4, 0);

        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(gridHost, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        section.Controls.Add(layout);
        return section;
    }

    private void SetupGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.VirtualMode = true;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.GridColor = Theme.GridLine;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Primary;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Primary;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(Theme.FontName, 10f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 32;
        grid.RowTemplate.Height = 26;
        grid.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#DCE4F0");
        grid.DefaultCellStyle.SelectionForeColor = Theme.TextDark;
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.RowAlt;
        grid.ShowCellToolTips = true;

        DataGridViewTextBoxColumn Col(string header, int width, bool fill = false, float weight = 100)
        {
            var c = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Width = width,
                MinimumWidth = 60,
            };
            if (fill)
            {
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                c.FillWeight = weight;
            }
            return c;
        }
        grid.Columns.Add(Col("狀態", 130));
        grid.Columns.Add(Col("檔名", 200, true, 90));
        var sizeCol = Col("大小", 90);
        sizeCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Columns.Add(sizeCol);
        grid.Columns.Add(Col("來源資料中的位置", 200, true, 110));
        grid.Columns.Add(Col("硬碟中對應的檔案", 200, true, 120));

        grid.CellValueNeeded += Grid_CellValueNeeded;
        grid.CellFormatting += Grid_CellFormatting;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = Directory.Exists(txtBackup.Text) ? btnAdd : btnBrowseBackup;
        Updater.CleanupOldInBackground(Environment.ProcessPath);
        if (settings.CheckForUpdates && AutoCheckUpdates)
        {
            _ = CheckForUpdateAsync();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        if (Width > area.Width || Height > area.Height)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    // ================================================================ 表格

    private void Grid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= view.Count)
        {
            return;
        }
        var it = view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => StatusText(it),
            1 => it.FileName,
            2 => DedupEngine.FormatSize(it.Size),
            3 => it.Location,
            4 => MatchText(it),
            _ => null,
        };
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= view.Count || e.CellStyle is null)
        {
            return;
        }
        var it = view[e.RowIndex];
        e.CellStyle.ForeColor = it.Status switch
        {
            ItemStatus.New when it.IsMissingBefore => Theme.Amber,
            ItemStatus.New => Theme.Teal,
            _ => Theme.TextGray,
        };
        e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
        if (it.Status == ItemStatus.New)
        {
            e.CellStyle.Font = boldCellFont;
        }
    }

    private static string StatusText(SourceItem it) => it.Status switch
    {
        ItemStatus.New when it.IsMissingBefore => it.CopiedTo is null ? "曾遺失" : "已補回",
        ItemStatus.New => it.CopiedTo is null ? "新照片" : "已複製",
        ItemStatus.AlreadyBackedUp => "硬碟已有",
        _ => "來源資料重複",
    };

    private string MatchText(SourceItem it)
    {
        return it.Status switch
        {
            ItemStatus.AlreadyBackedUp => RelativeToBackup(it.MatchedWith),
            ItemStatus.DuplicateInBatch => "與「" + it.MatchedWith + "」重複",
            ItemStatus.New when it.CopiedTo is not null =>
                (it.IsMissingBefore ? "已補回到 " : "已複製到 ") + RelativeToBackup(it.CopiedTo)
                + (it.NameNote is null ? "" : "（" + it.NameNote + "）"),
            ItemStatus.New when it.PreviousImport is { } prev =>
                $"{prev.ImportedAt:yyyy-MM-dd} 曾匯入到 {prev.RelativePath}，現在硬碟裡找不到",
            _ => "",
        };
    }

    private string RelativeToBackup(string? path)
    {
        if (path is null || analysis is null)
        {
            return path ?? "";
        }
        return Path.GetRelativePath(analysis.BackupRoot, path);
    }

    private void RefreshGrid()
    {
        grid.CurrentCell = null;
        grid.RowCount = 0;
        if (analysis is null)
        {
            view = new List<SourceItem>();
            return;
        }
        var all = analysis.Items;
        view = cboFilter.SelectedIndex switch
        {
            1 => all.Where(i => i.Status == ItemStatus.New).OrderBy(i => i.IsMissingBefore ? 0 : 1).ToList(),
            2 => all.Where(i => i.IsMissingBefore).ToList(),
            3 => all.Where(i => i.Status == ItemStatus.AlreadyBackedUp).ToList(),
            // 同一張照片的重複檔排在一起
            FilterDuplicate => all.Where(i => i.Status == ItemStatus.DuplicateInBatch)
                .OrderBy(i => i.MatchedWith, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => all.OrderBy(i => i.Status switch
            {
                ItemStatus.New when i.IsMissingBefore => 0,
                ItemStatus.New => 1,
                ItemStatus.AlreadyBackedUp => 2,
                _ => 3,
            }).ToList(),
        };
        grid.RowCount = view.Count;
        grid.Invalidate();
    }

    private const int FilterDuplicate = 4;

    private void ResetResults()
    {
        analysis = null;
        lastOutputFolder = null;
        RefreshGrid();
        lblNew.Text = "新照片：－";
        lblExisting.Text = "硬碟已有：－";
        lblMissing.Visible = false;
        lblDuplicate.Visible = false;
        lblHint.Text = "選好備份資料夾、加入來源資料後，按「① 開始比對」";
        lblHint.Visible = true;
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Value = 0;
        lblStatus.Text = "";
    }

    private void ShowSummary()
    {
        if (analysis is null)
        {
            return;
        }
        var newCount = analysis.Count(ItemStatus.New);
        var copied = analysis.Items.Count(i => i.CopiedTo is not null);
        lblNew.Text = copied > 0
            ? $"新照片：{newCount:N0} 張（已複製 {copied:N0} 張）"
            : $"新照片：{newCount:N0} 張（{DedupEngine.FormatSize(analysis.Bytes(ItemStatus.New))}）";
        lblExisting.Text = $"硬碟已有：{analysis.Count(ItemStatus.AlreadyBackedUp):N0} 張";
        var missing = analysis.Items.Count(i => i.IsMissingBefore);
        lblMissing.Text = $"其中曾遺失：{missing:N0} 張";
        lblMissing.Visible = missing > 0;
        var duplicates = analysis.Count(ItemStatus.DuplicateInBatch);
        lblDuplicate.Text = $"來源資料重複：{duplicates:N0} 張（只複製一份）";
        lblDuplicate.Visible = duplicates > 0;
        lblHint.Visible = analysis.Items.Count == 0;
        lblHint.Text = "來源資料裡沒有找到任何照片或影片";
        grid.Invalidate();
    }

    // ================================================================ 輸入

    private void BrowseBackup()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "選擇外接硬碟上放照片的資料夾",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (Directory.Exists(txtBackup.Text))
        {
            dlg.InitialDirectory = txtBackup.Text;
        }
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            SetBackupFolder(dlg.SelectedPath);
        }
    }

    internal void SetBackupFolder(string path)
    {
        txtBackup.Text = path;
        settings.BackupFolder = path;
        settings.Save();
        OnInputsChanged();
    }

    /// <summary>檔名欄的預設文字：使用者進入資料夾後直接按「開啟」，就代表要加入這個資料夾。</summary>
    internal const string FolderPlaceholder = "加入這個資料夾";

    private static string DownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// 同一個視窗選壓縮檔或資料夾（Windows 沒有可同時選兩種的標準視窗）：
    /// 選了檔案就加入檔案；檔名欄維持預設文字時加入目前所在的資料夾。
    /// </summary>
    private void BrowseSources()
    {
        using var dlg = CreateAddDialog(Directory.Exists(DownloadsFolder) ? DownloadsFolder : null);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var picked = ResolveDialogSelection(dlg.FileNames);
        var broad = picked.FirstOrDefault(IsBroadFolder);
        if (broad is not null)
        {
            var answer = MessageBox.Show(this,
                $"要加入整個「{Path.GetFileName(Path.TrimEndingDirectorySeparator(broad))}」資料夾嗎？" + Environment.NewLine
                + "裡面所有的照片和壓縮檔都會拿來比對。" + Environment.NewLine + Environment.NewLine
                + "如果只要加入某個壓縮檔，請按「取消」，再點選那個壓縮檔。",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                return;
            }
        }
        if (picked.Count > 0)
        {
            AddSources(picked);
        }
    }

    internal const string AddDialogTitle = "選擇壓縮檔；要加入整個資料夾時，進入該資料夾後直接按「開啟」";

    internal static OpenFileDialog CreateAddDialog(string? initialDirectory)
    {
        var dlg = new OpenFileDialog
        {
            Title = AddDialogTitle,
            Multiselect = true,
            CheckFileExists = false,
            ValidateNames = false,
            AddExtension = false, // 否則預設文字會被補成「加入這個資料夾.zip」
            FileName = FolderPlaceholder,
            Filter = "壓縮檔與照片|*.zip;*.jpg;*.jpeg;*.heic;*.png;*.gif;*.webp;*.mp4;*.mov|所有檔案|*.*",
        };
        if (initialDirectory is not null)
        {
            dlg.InitialDirectory = initialDirectory;
        }
        return dlg;
    }

    /// <summary>把開啟檔案視窗的結果轉成要加入的檔案或資料夾。</summary>
    internal static List<string> ResolveDialogSelection(IEnumerable<string> fileNames)
    {
        var result = new List<string>();
        foreach (var name in fileNames)
        {
            string? path = null;
            // 保險：就算視窗仍補上副檔名，也認得出是預設文字
            if (Path.GetFileName(name) == FolderPlaceholder
                || (Path.GetFileNameWithoutExtension(name) == FolderPlaceholder && !File.Exists(name)))
            {
                path = Path.GetDirectoryName(name);
            }
            else if (Directory.Exists(name) || File.Exists(name))
            {
                path = name;
            }
            if (path is not null && (Directory.Exists(path) || File.Exists(path))
                && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }
        return result;
    }

    /// <summary>下載、桌面、使用者資料夾這類可能放了無關檔案的大資料夾，加入前先確認。</summary>
    private static bool IsBroadFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var broad = new[]
        {
            DownloadsFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        return broad.Any(b => b.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(b), full, StringComparison.OrdinalIgnoreCase))
            || Path.GetPathRoot(full) == full + Path.DirectorySeparatorChar
            || string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase);
    }

    internal void AddSources(IEnumerable<string> paths)
    {
        var existing = lstSources.Items.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alreadyListed = new List<string>();
        foreach (var p in paths)
        {
            if (existing.Add(p))
            {
                lstSources.Items.Add(p);
            }
            else
            {
                alreadyListed.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(p)));
            }
        }
        OnInputsChanged();
        if (alreadyListed.Count > 0)
        {
            // 清單看起來沒變，要明確告訴使用者發生了什麼
            var names = string.Join("、", alreadyListed.Take(3)) + (alreadyListed.Count > 3 ? " 等" : "");
            lblStatus.Text = $"「{names}」已經在清單中，請按「① 開始比對」重新比對。";
        }
    }

    private void RemoveSelectedSources()
    {
        if (busy)
        {
            return;
        }
        foreach (var item in lstSources.SelectedItems.Cast<string>().ToList())
        {
            lstSources.Items.Remove(item);
        }
        OnInputsChanged();
    }

    private void OnInputsChanged()
    {
        if (analysis is not null)
        {
            ResetResults();
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var ready = Directory.Exists(txtBackup.Text) && lstSources.Items.Count > 0;
        btnBrowseBackup.Enabled = !busy;
        btnAdd.Enabled = !busy;
        btnNaming.Enabled = !busy;
        btnRemove.Enabled = !busy && lstSources.SelectedItems.Count > 0;
        btnAnalyze.Enabled = !busy && ready;
        btnCopy.Enabled = !busy && analysis is not null
            && analysis.Items.Any(i => i.Status == ItemStatus.New && i.CopiedTo is null);
        btnCancel.Enabled = busy;
        btnCancel.Visible = busy;
        btnExport.Enabled = !busy && analysis is not null && analysis.Items.Count > 0;
        btnOpenFolder.Enabled = lastOutputFolder is not null && Directory.Exists(lastOutputFolder);
        cboFilter.Enabled = analysis is not null;
        btnUpdate.Enabled = !busy;
        btnSkipUpdate.Enabled = !busy;
    }

    // ================================================================ 執行

    private IProgress<ProgressInfo> MakeProgress() => new Progress<ProgressInfo>(info =>
    {
        if (info.Stage != currentStage)
        {
            currentStage = info.Stage;
            stageWatch.Restart();
        }
        if (info.Total <= 0)
        {
            progressBar.Style = ProgressBarStyle.Marquee;
            lblStatus.Text = string.IsNullOrEmpty(info.Detail) ? info.Stage + "…" : $"{info.Stage}：{info.Detail}";
            return;
        }
        progressBar.Style = ProgressBarStyle.Continuous;
        var fraction = Math.Clamp((double)info.Done / info.Total, 0, 1);
        progressBar.Value = (int)(fraction * 1000);
        var text = $"{info.Stage} {fraction * 100:0}%（{DedupEngine.FormatSize(info.Done)} / {DedupEngine.FormatSize(info.Total)}）";
        var elapsed = stageWatch.Elapsed.TotalSeconds;
        if (elapsed > 3 && fraction > 0.01 && fraction < 1)
        {
            var remain = TimeSpan.FromSeconds(elapsed / fraction * (1 - fraction));
            text += "，剩約 " + FormatDuration(remain);
        }
        if (!string.IsNullOrEmpty(info.Detail))
        {
            text += "　" + Path.GetFileName(info.Detail);
        }
        lblStatus.Text = text;
    });

    /// <summary>直接填滿進度條（Windows 進度條預設有動畫，直接設最大值會停在九成多）。</summary>
    private void FillProgressBar()
    {
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Value = progressBar.Maximum;
        progressBar.Value = progressBar.Maximum - 1;
        progressBar.Value = progressBar.Maximum;
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes < 1)
        {
            return "不到 1 分鐘";
        }
        if (t.TotalHours < 1)
        {
            return $"{Math.Ceiling(t.TotalMinutes):0} 分鐘";
        }
        return $"{(int)t.TotalHours} 小時 {t.Minutes} 分鐘";
    }

    private void SetBusy(bool value)
    {
        busy = value;
        lstSources.Enabled = !value;
        UpdateButtons();
        UseWaitCursor = false;
    }

    internal async Task AnalyzeAsync()
    {
        if (busy)
        {
            return;
        }
        var backup = txtBackup.Text;
        var sources = lstSources.Items.Cast<string>().ToList();
        ResetResults();
        lblHint.Text = "比對中…";
        SetBusy(true);
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var progress = MakeProgress();
        try
        {
            var result = await Task.Run(() => DedupEngine.Analyze(backup, sources, progress, token));
            analysis = result;
            cboFilter.SelectedIndex = 0;
            RefreshGrid();
            ShowSummary();
            progressBar.Style = ProgressBarStyle.Continuous;
            FillProgressBar();
            var newCount = result.Count(ItemStatus.New);
            var missingCount = result.Items.Count(i => i.IsMissingBefore);
            var status = newCount > 0
                ? $"比對完成：找到 {newCount:N0} 張新照片，按「② 複製新照片」放進硬碟。"
                : "比對完成：來源資料裡的照片硬碟都已經有了，不需要複製。";
            if (missingCount > 0)
            {
                status = $"比對完成：找到 {newCount:N0} 張新照片，其中 {missingCount:N0} 張以前匯入過、現在硬碟裡找不到（標示為「曾遺失」）。";
            }
            var duplicateCount = result.Count(ItemStatus.DuplicateInBatch);
            if (duplicateCount > 0)
            {
                status += $"來源資料裡有 {duplicateCount:N0} 張重複。";
            }
            if (!result.IndexSaved)
            {
                status += "（無法寫入快取檔，下次比對會比較慢）";
            }
            lblStatus.Text = status;
            if (duplicateCount > 0 && ShowDuplicateDialog)
            {
                MessageBox.Show(this, DuplicateMessage(result), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (result.UnreadableFiles.Count > 0)
            {
                MessageBox.Show(this,
                    $"硬碟裡有 {result.UnreadableFiles.Count} 個檔案無法讀取，這些檔案沒有列入比對：" + Environment.NewLine
                    + string.Join(Environment.NewLine, result.UnreadableFiles.Take(10))
                    + (result.UnreadableFiles.Count > 10 ? Environment.NewLine + "…" : ""),
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ResetResults();
            lblStatus.Text = "已取消比對。";
        }
        catch (UserFacingException ex)
        {
            ResetResults();
            lblStatus.Text = "比對失敗。";
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResetResults();
            lblStatus.Text = "比對失敗。";
            MessageBox.Show(this, "讀取檔案時發生錯誤：" + Environment.NewLine + ex.Message
                + Environment.NewLine + Environment.NewLine + "請確認外接硬碟沒有被拔除。",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            FinishRun();
        }
    }

    internal async Task CopyAsync(bool confirm)
    {
        if (busy || analysis is null)
        {
            return;
        }
        var pending = analysis.Items.Where(i => i.Status == ItemStatus.New && i.CopiedTo is null).ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var outDir = DedupEngine.GetStagingFolder(analysis.BackupRoot, DateTime.Now);
        var naming = settings.Naming.Clone();
        if (confirm)
        {
            var answer = MessageBox.Show(this,
                $"要把 {pending.Count:N0} 張新照片（{DedupEngine.FormatSize(pending.Sum(i => i.Size))}）複製到：" + Environment.NewLine
                + outDir + Environment.NewLine + Environment.NewLine
                + (pending.Any(i => i.IsMissingBefore)
                    ? $"其中 {pending.Count(i => i.IsMissingBefore):N0} 張是以前匯入過、現在找不到的照片，會一起補回來。" + Environment.NewLine
                    : "")
                + (analysis.Count(ItemStatus.DuplicateInBatch) > 0
                    ? $"來源資料重複的 {analysis.Count(ItemStatus.DuplicateInBatch):N0} 張只複製一份，所以張數會比來源資料的檔案數少。" + Environment.NewLine
                    : "")
                + NamingLine(naming) + Environment.NewLine
                + "硬碟裡原本的檔案不會被修改或刪除。",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                return;
            }
        }

        SetBusy(true);
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var progress = MakeProgress();
        var current = analysis;
        try
        {
            var summary = await Task.Run(() => DedupEngine.CopyNew(current, progress, token, naming));
            lastOutputFolder = summary.OutputFolder;
            FillProgressBar();
            lblStatus.Text = $"完成：已複製 {summary.Copied:N0} 張（{DedupEngine.FormatSize(summary.Bytes)}）到 {summary.OutputFolder}";
            ShowSummary(); // 先更新清單再跳提示，提示開著時清單才不會停在複製前的狀態
            if (ShowCopyDoneDialog)
            {
                MessageBox.Show(this,
                    $"已複製 {summary.Copied:N0} 張新照片到：" + Environment.NewLine + summary.OutputFolder,
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "已取消複製。已經複製完成的照片會保留，再按一次「② 複製新照片」會繼續複製剩下的。";
            lastOutputFolder = outDir;
        }
        catch (UserFacingException ex)
        {
            lblStatus.Text = "複製失敗。";
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lblStatus.Text = "複製失敗。";
            lastOutputFolder = outDir;
            MessageBox.Show(this, "複製時發生錯誤：" + Environment.NewLine + ex.Message
                + Environment.NewLine + Environment.NewLine + "請確認外接硬碟沒有被拔除、空間足夠。已經複製完成的照片會保留。",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ShowSummary();
            FinishRun();
        }
    }

    private void FinishRun()
    {
        cts?.Dispose();
        cts = null;
        SetBusy(false);
        if (closeAfterCancel)
        {
            BeginInvoke(Close);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy)
        {
            var answer = MessageBox.Show(this, "還在處理中，確定要停止並關閉嗎？" + Environment.NewLine + "已經複製完成的照片會保留。",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            e.Cancel = true;
            if (answer == DialogResult.Yes)
            {
                closeAfterCancel = true;
                cts?.Cancel();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    // ================================================================ 自動更新

    private Control BuildUpdateBar()
    {
        updateBar.Dock = DockStyle.Top;
        updateBar.AutoSize = true;
        updateBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        updateBar.BackColor = Theme.UpdateBack;
        updateBar.Padding = new Padding(20, 6, 16, 6);
        updateBar.Visible = false;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.UpdateBack,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lblUpdate.Dock = DockStyle.Fill;
        lblUpdate.AutoEllipsis = true;
        lblUpdate.TextAlign = ContentAlignment.MiddleLeft;
        lblUpdate.ForeColor = Theme.Teal;
        lblUpdate.Font = new Font(Theme.FontName, 10f, FontStyle.Bold);
        lblUpdate.Margin = new Padding(0);
        lnkUpdateNotes.Text = "更新內容";
        lnkUpdateNotes.AutoSize = true;
        lnkUpdateNotes.Anchor = AnchorStyles.Left;
        lnkUpdateNotes.LinkColor = Theme.Teal;
        lnkUpdateNotes.ActiveLinkColor = Theme.TealHover;
        lnkUpdateNotes.Margin = new Padding(8, 0, 12, 0);
        lnkUpdateNotes.LinkClicked += (_, _) => ShowUpdateNotes();
        foreach (var b in new[] { btnUpdate, btnSkipUpdate })
        {
            b.MinimumSize = new Size(110, 32);
        }
        btnUpdate.Margin = new Padding(0, 0, 8, 0);
        btnSkipUpdate.Margin = new Padding(0);
        btnUpdate.Click += async (_, _) => await UpdateNowAsync(confirm: true);
        btnSkipUpdate.Click += (_, _) =>
        {
            if (pendingUpdate is not null)
            {
                settings.SkippedVersion = pendingUpdate.Version.ToString(3);
                settings.Save();
                lblStatus.Text = $"已略過 v{settings.SkippedVersion}，有更新的版本時會再提示。";
            }
            updateBar.Visible = false;
        };
        tip.SetToolTip(btnSkipUpdate, "這一版不再提示，之後有更新的版本時才會再提示。");
        row.Controls.Add(lblUpdate, 0, 0);
        row.Controls.Add(lnkUpdateNotes, 1, 0);
        row.Controls.Add(btnUpdate, 2, 0);
        row.Controls.Add(btnSkipUpdate, 3, 0);
        updateBar.Controls.Add(row);
        return updateBar;
    }

    private async Task CheckForUpdateAsync()
    {
        var current = Version.TryParse(VersionText, out var v) ? v : new Version(0, 0, 0);
        var info = await Updater.CheckAsync(current, CancellationToken.None);
        if (info is not null && info.Version.ToString(3) != settings.SkippedVersion && !IsDisposed)
        {
            ShowUpdateAvailable(info);
        }
    }

    internal void ShowUpdateAvailable(UpdateInfo info)
    {
        pendingUpdate = info;
        lblUpdate.Text = $"有新版本 v{info.Version.ToString(3)} 可以更新（目前 v{VersionText}）";
        lnkUpdateNotes.Visible = info.Notes.Length > 0;
        updateBar.Visible = true;
        UpdateButtons();
    }

    private void ShowUpdateNotes()
    {
        if (pendingUpdate is null)
        {
            return;
        }
        MessageBox.Show(this, NotesText(pendingUpdate), $"v{pendingUpdate.Version.ToString(3)} 更新內容",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string NotesText(UpdateInfo info)
    {
        var notes = info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        return notes.Length > 1500 ? notes[..1500] + "…" : notes;
    }

    /// <summary>下載新版、替換 exe、重新啟動。比對或複製進行中時不更新。</summary>
    internal async Task UpdateNowAsync(bool confirm, string? targetExe = null, bool restart = true)
    {
        var info = pendingUpdate;
        if (info is null || busy)
        {
            return;
        }
        var exe = targetExe ?? Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }
        if (confirm)
        {
            var text = $"要更新到 v{info.Version.ToString(3)} 嗎？" + Environment.NewLine
                + $"會下載約 {DedupEngine.FormatSize(info.Size)}，完成後程式自動重新開啟。" + Environment.NewLine
                + "設定（備份資料夾、檔名規則）會保留。";
            if (info.Notes.Length > 0)
            {
                text += Environment.NewLine + Environment.NewLine + "更新內容：" + Environment.NewLine + NotesText(info);
            }
            if (MessageBox.Show(this, text, AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
        }

        SetBusy(true);
        cts = new CancellationTokenSource();
        progressBar.Value = 0;
        lblStatus.Text = "正在下載新版本…";
        var progress = new Progress<double>(f =>
        {
            progressBar.Value = (int)Math.Clamp(f * progressBar.Maximum, 0, progressBar.Maximum);
            lblStatus.Text = $"正在下載新版本：{f:P0}";
        });
        try
        {
            await Updater.DownloadAndReplaceAsync(info, exe, progress, cts.Token);
            LastUpdateError = null;
            lblStatus.Text = $"已更新到 v{info.Version.ToString(3)}。";
            updateBar.Visible = false;
            pendingUpdate = null;
            SetBusy(false);
            if (restart)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Close();
            }
            return;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            lblStatus.Text = "已取消更新，目前版本不受影響。";
            LastUpdateError = "取消";
        }
        catch (UnauthorizedAccessException)
        {
            LastUpdateError = "沒有寫入權限";
            lblStatus.Text = "更新失敗：程式所在的資料夾無法寫入。";
            MessageBox.Show(this, "程式所在的資料夾無法寫入，無法更新。" + Environment.NewLine
                + "請把程式放到桌面或「文件」等自己的資料夾後再更新。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            LastUpdateError = ex.Message;
            lblStatus.Text = "更新失敗，目前版本不受影響。";
            MessageBox.Show(this, "更新失敗，目前版本不受影響，可以稍後再試。" + Environment.NewLine + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        progressBar.Value = 0;
        SetBusy(false);
        if (closeAfterCancel)
        {
            BeginInvoke(Close);
        }
    }

    /// <summary>介面測試時不連網檢查更新。</summary>
    internal static bool AutoCheckUpdates { get; set; } = true;

    internal string? LastUpdateError { get; private set; }

    internal bool UpdateBarVisible => updateBar.Visible;

    // ================================================================ 檔名規則

    private void EditNaming()
    {
        using var dlg = new NamingDialog(settings.Naming);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        settings.Naming = dlg.Result;
        settings.Save();
        UpdateNamingTip();
        lblStatus.Text = "檔名規則：" + settings.Naming.Describe();
    }

    private void UpdateNamingTip()
    {
        tip.SetToolTip(btnNaming, "設定複製進「_待整理」的照片要用什麼檔名" + Environment.NewLine
            + "目前：" + settings.Naming.Describe());
    }

    /// <summary>複製確認視窗中的檔名說明，附範例。</summary>
    internal static string NamingLine(NamingOptions naming)
    {
        if (!naming.IsActive)
        {
            return "檔名：保留原檔名（可按「檔名規則…」改成依拍攝日期等方式命名）。";
        }
        var sample = FileNamer.Samples[0];
        var example = FileNamer.AssignNames(naming, new[] { sample }, _ => false)[0];
        return $"檔名：依「{naming.Describe()}」改名，例如 {sample.OriginalName} → {example}";
    }

    // ================================================================ 匯出與開啟

    private void ExportCsv()
    {
        if (analysis is null)
        {
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Title = "匯出比對結果",
            Filter = "CSV 檔（可用 Excel 開啟）|*.csv",
            FileName = $"比對結果_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            using var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
            w.WriteLine("狀態,檔名,大小(位元組),來源資料中的位置,硬碟中對應的檔案");
            foreach (var it in analysis.Items)
            {
                w.WriteLine(string.Join(",",
                    Csv(StatusText(it)), Csv(it.FileName), it.Size.ToString(), Csv(it.Location), Csv(MatchText(it))));
            }
            lblStatus.Text = "已匯出：" + dlg.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "無法寫入檔案（可能正在用 Excel 開啟）：" + Environment.NewLine + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>比對完成後的重複提示：張數、前幾組重複的檔案。</summary>
    internal static string DuplicateMessage(AnalysisResult result)
    {
        var dups = result.Items.Where(i => i.Status == ItemStatus.DuplicateInBatch).ToList();
        const int MaxGroups = 8;
        var groups = dups.GroupBy(i => i.MatchedWith ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"來源資料裡有 {dups.Count:N0} 張照片重複（內容完全相同，檔名可能不同），只會複製一份。");
        sb.AppendLine($"所以複製的張數會比來源資料的檔案數少 {dups.Count:N0} 張。");
        sb.AppendLine();
        foreach (var g in groups.Take(MaxGroups))
        {
            sb.AppendLine("・" + g.Key);
            foreach (var d in g.Take(3))
            {
                sb.AppendLine("    重複：" + d.Location);
            }
            if (g.Count() > 3)
            {
                sb.AppendLine($"    …另外 {g.Count() - 3} 個");
            }
        }
        if (groups.Count > MaxGroups)
        {
            sb.AppendLine($"…另外 {groups.Count - MaxGroups:N0} 組");
        }
        sb.AppendLine();
        sb.Append("完整清單：下方「顯示」選「來源資料重複」，或按「匯出清單…」存成 Excel 可開的檔案。");
        return sb.ToString();
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private void OpenOutputFolder()
    {
        if (lastOutputFolder is not null && Directory.Exists(lastOutputFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lastOutputFolder + "\"") { UseShellExecute = true });
        }
    }

    internal AnalysisResult? CurrentAnalysis => analysis;

    /// <summary>介面測試時關掉比對後的重複提示視窗（視窗會卡住自動測試）。</summary>
    internal bool ShowDuplicateDialog { get; set; } = true;

    /// <summary>介面測試時關掉複製完成的提示視窗。</summary>
    internal bool ShowCopyDoneDialog { get; set; } = true;

    internal string DuplicateLabelText => lblDuplicate.Visible ? lblDuplicate.Text : "";

    internal void SelectFilter(int index) => cboFilter.SelectedIndex = index;

    internal string StatusLine => lblStatus.Text;

    internal int VisibleRowCount => grid.RowCount;

    internal static string VersionText =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
}
