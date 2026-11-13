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
    private readonly SourceList lstSources;
    private readonly SectionPanel sourcesCard;
    private readonly Button btnBrowseBackup = Ui.MakeButton("選擇資料夾…", false);
    private readonly Button btnAdd = Ui.MakeButton("＋ 加入", ButtonKind.Outline);

    private readonly Button btnAnalyze = Ui.MakeButton("▶  開始比對", ButtonKind.Primary);
    private readonly Button btnCopy = Ui.MakeButton("複製新照片", ButtonKind.Outline);
    private readonly Button btnNaming = Ui.MakeButton("檔名規則…", ButtonKind.Link);
    private readonly Button btnCancel = Ui.MakeButton("取消", false);
    private readonly ProgressBar progressBar = new();
    private readonly TableLayoutPanel progressArea = new();
    private readonly StateLabel lblState = new();
    private readonly Label lblStatus = new();
    private readonly Panel statusBar = new();
    private Color statusColor = Theme.TextGray;

    private readonly TableLayoutPanel tilesRow = new();
    private readonly StatTile tileNew = new() { Caption = "新照片", FillColor = Theme.AccentLight, NumberColor = Theme.Accent };
    private readonly StatTile tileExisting = new() { Caption = "硬碟已有", FillColor = Theme.Surface, NumberColor = Theme.TextDark };
    private readonly StatTile tileDuplicate = new() { Caption = "來源資料重複", FillColor = Theme.AmberLight, NumberColor = Theme.Amber };
    private readonly StatTile tileTotal = new() { Caption = "已掃描", FillColor = Theme.Surface, NumberColor = Theme.TextDark };
    private readonly TabStrip tabs = new();
    private readonly SearchBox search = new("搜尋檔名");
    private readonly Label lblSelection = new();
    private readonly Label lblSubtitle = new();
    private readonly Label lblBackupHint = new();
    private int compactLevel = -1;
    private Control? header;
    private readonly Label lblHint = new();
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
    private readonly Font pillFont = new(Theme.FontName, 9f, FontStyle.Bold);

    // 表格欄位
    private const int ColCheck = 0;
    private const int ColName = 1;
    private const int ColLocation = 2;
    private const int ColSize = 3;
    private const int ColStatus = 4;
    private const int ColMatch = 5;

    // 分頁（和舊版下拉選單的順序相同）
    private const int FilterAll = 0;
    private const int FilterNew = 1;
    private const int FilterMissing = 2;
    private const int FilterExisting = 3;
    private const int FilterDuplicate = 4;

    public MainForm()
    {
        lstSources = new SourceList(tip);
        sourcesCard = new SectionPanel("來源資料",
            "可以加入資料夾或 zip 壓縮檔（例如從 Google 相簿下載的 zip），壓縮檔不用先解壓縮。" + Environment.NewLine
            + "按右上角「＋ 加入」：選壓縮檔（可一次選多個）；要加入整個資料夾時，進入該資料夾後直接按「開啟」。" + Environment.NewLine
            + "也可以把壓縮檔或資料夾直接拖曳到清單裡，程式會自動判斷。滑鼠移到項目上會顯示完整路徑。",
            tip, large: true)
        {
            Step = "2",
            FolderGlyph = true,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(6, 0, 0, 0),
        };

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(Theme.FontName, 10f);
        ForeColor = Theme.TextDark;
        BackColor = Theme.WindowBack;
        Text = AppTitle + " v" + VersionText;
        Icon = Ui.AppIcon ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1060, 820);
        MinimumSize = new Size(860, 660);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18, 4, 18, 12),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildInputsRow(), 0, 0);
        body.Controls.Add(BuildActionRow(), 0, 1);
        body.Controls.Add(BuildResultsSection(), 0, 2);

        Controls.Add(body);
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildUpdateBar());
        header = BuildHeader();
        Controls.Add(header);

        ResumeLayout(false);
        PerformLayout();

        if (!string.IsNullOrEmpty(settings.BackupFolder))
        {
            txtBackup.Text = settings.BackupFolder;
        }
        ResetResults();
        UpdateSourcesBadge();
        UpdateButtons();
    }

    // ================================================================ 版面

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(20, 14, 20, 10),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = new PictureBox
        {
            Size = new Size(52, 52),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 14, 0),
            Anchor = AnchorStyles.Left,
        };
        if (Ui.AppIcon is { } icon)
        {
            using var big = new Icon(icon, 128, 128);
            logo.Image = big.ToBitmap();
        }
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        stack.Controls.Add(new Label
        {
            Text = AppTitle,
            AutoSize = true,
            ForeColor = Theme.TextDark,
            Font = new Font(Theme.FontName, 17f, FontStyle.Bold),
            Margin = new Padding(0),
        });
        lblSubtitle.Text = "比對需匯入的來源資料，只把外接硬碟裡還沒有的照片放進去";
        lblSubtitle.AutoSize = true;
        lblSubtitle.ForeColor = Theme.TextGray;
        lblSubtitle.Margin = new Padding(2, 2, 0, 0);
        stack.Controls.Add(lblSubtitle);
        var pill = new PillLabel
        {
            Text = "照片只在本機處理",
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            Font = new Font(Theme.FontName, 10f, FontStyle.Bold),
        };
        tip.SetToolTip(pill, "比對與複製都在這台電腦上進行，照片不會上傳到任何地方。" + Environment.NewLine
            + "只有開啟程式時會連到 GitHub 查詢有沒有新版本。");
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(stack, 1, 0);
        header.Controls.Add(pill, 2, 0);
        return header;
    }

    private Control BuildInputsRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.Controls.Add(BuildBackupSection(), 0, 0);
        row.Controls.Add(BuildSourcesSection(), 1, 0);
        return row;
    }

    private Control BuildBackupSection()
    {
        var section = new SectionPanel("備份位置",
            "外接硬碟上放照片的資料夾，裡面的子資料夾也會一起比對。" + Environment.NewLine
            + "新照片會複製到這個資料夾裡的「_待整理\\日期」資料夾，整理時可以隨意搬移、改名，不影響下次比對。",
            tip, large: true)
        {
            Step = "1",
            FolderGlyph = true,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 6, 0),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        txtBackup.ReadOnly = true;
        txtBackup.TabStop = false;
        txtBackup.PlaceholderText = "請選擇外接硬碟上放照片的資料夾";
        txtBackup.AllowDrop = true;
        txtBackup.TextChanged += (_, _) => tip.SetToolTip(txtBackup, txtBackup.Text);
        var backupField = new SearchBox(txtBackup, false)
        {
            Dock = DockStyle.Fill,
            Height = 34,
            Margin = new Padding(0, 0, 10, 0),
        };
        txtBackup.BackColor = Theme.TableHead;
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
        var hint = lblBackupHint;
        hint.Text = "用來檢查哪些照片已經備份；新照片會放進這裡的「_待整理」資料夾。";
        hint.AutoSize = false;
        hint.AutoEllipsis = true;
        hint.Dock = DockStyle.Fill;
        hint.Height = 26;
        hint.ForeColor = Theme.TextGray;
        hint.TextAlign = ContentAlignment.BottomLeft;
        hint.Margin = new Padding(0, 6, 0, 0);
        tip.SetToolTip(hint, hint.Text);
        layout.Controls.Add(backupField, 0, 0);
        layout.Controls.Add(btnBrowseBackup, 1, 0);
        layout.Controls.Add(hint, 0, 1);
        layout.SetColumnSpan(hint, 2);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildSourcesSection()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        lstSources.Dock = DockStyle.Fill;
        lstSources.Height = 70; // 兩列，超過時捲動
        lstSources.Margin = new Padding(0);
        lstSources.EmptyText = "按右上角「＋ 加入」，或把資料夾、zip 拖曳到這裡";
        lstSources.AllowDrop = true;
        lstSources.DragEnter += (_, e) => e.Effect = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        lstSources.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddSources(paths);
            }
        };
        lstSources.RemoveRequested += (_, path) => RemoveSource(path);

        // 「＋ 加入」放在卡片標題列右側，兩張卡片高度才對得齊，也把高度留給表格
        btnAdd.AutoSize = false;
        btnAdd.MinimumSize = Size.Empty;
        btnAdd.Size = new Size(84, 30);
        btnAdd.Padding = new Padding(0);
        btnAdd.Font = new Font(Theme.FontName, 10f, FontStyle.Bold);
        btnAdd.Click += (_, _) => BrowseSources();
        tip.SetToolTip(btnAdd, "加入資料夾或壓縮檔：選壓縮檔（可一次選多個）；要加入整個資料夾時，進入該資料夾後直接按「開啟」");
        sourcesCard.HeaderAction = btnAdd;

        layout.Controls.Add(lstSources, 0, 0);
        sourcesCard.Controls.Add(layout);
        return sourcesCard;
    }

    private Control BuildActionRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var b in new[] { btnAnalyze, btnCopy })
        {
            b.AutoSize = false;
            b.Size = new Size(160, 40);
            b.Margin = new Padding(0, 0, 12, 0);
        }
        btnAnalyze.Click += async (_, _) => await AnalyzeAsync();
        btnCopy.Click += async (_, _) => await CopyAsync(confirm: true);
        tip.SetToolTip(btnAnalyze, "找出來源資料中，哪些已經在硬碟裡（只讀取，不會改動任何檔案）");
        tip.SetToolTip(btnCopy, "把勾選的新照片複製到「_待整理」資料夾");
        btnNaming.AutoSize = false;
        btnNaming.Size = new Size(110, 40);
        btnNaming.Margin = new Padding(0);
        btnNaming.Click += (_, _) => EditNaming();
        UpdateNamingTip();

        // 右側：處理中顯示進度條與取消，完成後顯示狀態
        var right = new Panel { Dock = DockStyle.Fill, Height = 40, Margin = new Padding(12, 0, 0, 0) };
        progressArea.Dock = DockStyle.Fill;
        progressArea.ColumnCount = 2;
        progressArea.RowCount = 1;
        progressArea.Margin = new Padding(0);
        progressArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressArea.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressBar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        progressBar.Height = 10;
        progressBar.Maximum = 1000;
        progressBar.Margin = new Padding(0, 0, 10, 0);
        btnCancel.AutoSize = false;
        btnCancel.Size = new Size(90, 36);
        btnCancel.Margin = new Padding(0);
        btnCancel.Anchor = AnchorStyles.Right;
        btnCancel.Click += (_, _) =>
        {
            cts?.Cancel();
            lblStatus.Text = "正在取消…";
        };
        progressArea.Controls.Add(progressBar, 0, 0);
        progressArea.Controls.Add(btnCancel, 1, 0);
        progressArea.Visible = false;
        lblState.Dock = DockStyle.Fill;
        lblState.Font = new Font(Theme.FontName, 10.5f);
        right.Controls.Add(progressArea);
        right.Controls.Add(lblState);

        row.Controls.Add(btnAnalyze, 0, 0);
        row.Controls.Add(btnCopy, 1, 0);
        row.Controls.Add(btnNaming, 2, 0);
        row.Controls.Add(right, 3, 0);
        return row;
    }

    private Control BuildResultsSection()
    {
        var section = new SectionPanel("比對結果",
            "新照片：硬碟裡還沒有，按「複製新照片」會把勾選的照片放進待整理資料夾；取消勾選的不會複製。" + Environment.NewLine
            + "硬碟已有：內容和硬碟裡某個檔案完全相同（就算檔名不同也算），不會複製。" + Environment.NewLine
            + "曾遺失：以前用這個程式匯入過，但現在硬碟裡找不到（可能被刪除或搬走），會再複製一次補回來。" + Environment.NewLine
            + "來源資料重複：同一張照片在來源資料裡出現好幾次，只會複製第一份。" + Environment.NewLine
            + "比對看的是檔案內容，在 Google 相簿編輯或旋轉過的照片會被當成新照片。" + Environment.NewLine
            + "依「檔名規則」改名時，找不到拍攝時間的照片會在「硬碟中對應的檔案」欄註明日期從哪裡來。",
            tip, large: true)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 統計卡
        tilesRow.Dock = DockStyle.Fill;
        tilesRow.ColumnCount = 4;
        tilesRow.RowCount = 1;
        tilesRow.AutoSize = true;
        tilesRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        tilesRow.Margin = new Padding(0, 0, 0, 6);
        var tiles = new[] { tileNew, tileExisting, tileDuplicate, tileTotal };
        var targets = new[] { FilterNew, FilterExisting, FilterDuplicate, FilterAll };
        for (var i = 0; i < tiles.Length; i++)
        {
            tilesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var t = tiles[i];
            var target = targets[i];
            t.Dock = DockStyle.Fill;
            t.Height = 62;
            t.Margin = new Padding(i == 0 ? 0 : 6, 0, i == tiles.Length - 1 ? 0 : 6, 0);
            t.Click += (_, _) =>
            {
                if (analysis is not null)
                {
                    tabs.SelectedIndex = target;
                }
            };
            tilesRow.Controls.Add(t, i, 0);
        }
        tip.SetToolTip(tileNew, "硬碟裡還沒有的照片，點一下只列出這些");
        tip.SetToolTip(tileExisting, "內容和硬碟裡某個檔案完全相同，不會複製");
        tip.SetToolTip(tileDuplicate, "同一張照片在來源資料裡出現好幾次，只會複製第一份。點一下列出是哪些檔案");
        tip.SetToolTip(tileTotal, "來源資料中的照片與影片總數");

        // 分頁＋搜尋
        var tabRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 8),
        };
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tabs.Dock = DockStyle.Fill;
        tabs.Height = 36;
        tabs.Margin = new Padding(0, 0, 12, 0);
        tabs.Font = new Font(Theme.FontName, 10.5f);
        tabs.SetTabs(new[] { "全部", "新照片", "曾遺失", "硬碟已有", "來源資料重複" });
        tabs.SelectedIndexChanged += (_, _) => RefreshGrid();
        search.Size = new Size(240, 34);
        tabRow.SizeChanged += (_, _) =>
        {
            var want = Math.Min(Ui.Scaled(this, 240), Math.Max(Ui.Scaled(this, 150), tabRow.Width / 4));
            if (search.Width != want && IsHandleCreated)
            {
                BeginInvoke(() => search.Width = want);
            }
        };
        search.Anchor = AnchorStyles.Right;
        search.Margin = new Padding(0);
        search.TextChanged += (_, _) => RefreshGrid();
        tabRow.Controls.Add(tabs, 0, 0);
        tabRow.Controls.Add(search, 1, 0);

        var gridHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(1), BackColor = Theme.CardBorder };
        SetupGrid();
        lblHint.Dock = DockStyle.Fill;
        lblHint.TextAlign = ContentAlignment.MiddleCenter;
        lblHint.ForeColor = Theme.TextGray;
        lblHint.BackColor = Theme.AccentSoft;
        gridHost.Controls.Add(grid);
        gridHost.Controls.Add(lblHint);
        lblHint.BringToFront();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lblSelection.AutoSize = true;
        lblSelection.Anchor = AnchorStyles.Left;
        lblSelection.Margin = new Padding(0);
        lblSelection.ForeColor = Theme.TextDark;
        tip.SetToolTip(lblSelection, "按表格最左邊的方框勾選或取消；最上面的方框可以一次全選或全部取消。也可以選取多列後按空白鍵。");
        btnExport.Anchor = AnchorStyles.Right;
        btnOpenFolder.Anchor = AnchorStyles.Right;
        btnOpenFolder.Margin = new Padding(0);
        btnExport.Click += (_, _) => ExportCsv();
        btnOpenFolder.Click += (_, _) => OpenOutputFolder();
        tip.SetToolTip(btnExport, "把比對結果存成 CSV 檔，可用 Excel 開啟");
        bottom.Controls.Add(lblSelection, 0, 0);
        bottom.Controls.Add(btnExport, 1, 0);
        bottom.Controls.Add(btnOpenFolder, 2, 0);

        layout.Controls.Add(tilesRow, 0, 0);
        layout.Controls.Add(tabRow, 0, 1);
        layout.Controls.Add(gridHost, 0, 2);
        layout.Controls.Add(bottom, 0, 3);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildStatusBar()
    {
        statusBar.Dock = DockStyle.Bottom;
        statusBar.Height = 30;
        statusBar.BackColor = Theme.WindowBack;
        statusBar.Padding = new Padding(36, 0, 18, 0);
        statusBar.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using (var line = new Pen(Theme.CardBorder))
            {
                g.DrawLine(line, 0, 0, statusBar.Width, 0);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = Ui.Scaled(statusBar, 9);
            using var b = new SolidBrush(busy ? Theme.Accent : statusColor);
            g.FillEllipse(b, Ui.Scaled(statusBar, 20), (statusBar.Height - d) / 2, d, d);
        };
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.AutoEllipsis = true;
        lblStatus.ForeColor = Theme.TextDark;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.Margin = new Padding(0);
        lblStatus.TextChanged += (_, _) => tip.SetToolTip(lblStatus, lblStatus.Text);
        statusBar.Controls.Add(lblStatus);
        return statusBar;
    }

    /// <summary>狀態列左邊圓點的顏色：完成綠色、取消或失敗琥珀色、其他灰色（處理中固定主色）。</summary>
    private void SetStatusColor(Color color)
    {
        statusColor = color;
        statusBar.Invalidate();
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
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Theme.GridLine;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.TableHead;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextDark;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.TableHead;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.TextDark;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(Theme.FontName, 10f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 36;
        grid.RowTemplate.Height = 34;
        grid.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
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
        var check = Col("", 40);
        check.MinimumWidth = 40;
        check.Resizable = DataGridViewTriState.False;
        grid.Columns.Add(check);
        grid.Columns.Add(Col("檔案名稱", 200, true, 90));
        grid.Columns.Add(Col("來源資料中的位置", 200, true, 100));
        var sizeCol = Col("大小", 90);
        sizeCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Columns.Add(sizeCol);
        grid.Columns.Add(Col("狀態", 120));
        grid.Columns.Add(Col("硬碟中對應的檔案", 200, true, 120));

        grid.CellValueNeeded += Grid_CellValueNeeded;
        grid.CellPainting += Grid_CellPainting;
        grid.CellMouseClick += Grid_CellMouseClick;
        grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.ColumnIndex == ColCheck)
            {
                e.ToolTipText = e.RowIndex < 0 ? "全選或全部取消（目前列出的新照片）" : "勾選的新照片才會複製";
            }
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                ToggleRows(grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index));
                e.Handled = true;
            }
        };
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
        ApplyGridMetrics();
        UpdateCompactLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyGridMetrics();
        compactLevel = -1;
        UpdateCompactLayout();
    }

    /// <summary>
    /// 表格的固定欄寬、列高不會跟著 DPI 自動放大（150% 時「大小」欄會被截成「14.3 ...」），
    /// 所以每次都從 96 DPI 的基準值換算，不在現值上乘，避免重複放大。
    /// </summary>
    private void ApplyGridMetrics()
    {
        int S(int px) => Ui.Scaled(this, px);
        grid.ColumnHeadersHeight = S(36);
        grid.RowTemplate.Height = S(compactLevel == 2 ? 28 : 34);
        var fixedWidths = new Dictionary<int, int> { [ColCheck] = 40, [ColSize] = 96, [ColStatus] = 130 };
        foreach (DataGridViewColumn c in grid.Columns)
        {
            c.MinimumWidth = S(fixedWidths.TryGetValue(c.Index, out var fw) && c.Index == ColCheck ? 40 : 60);
            if (fixedWidths.TryGetValue(c.Index, out var w))
            {
                c.Width = S(w);
            }
        }
        RefreshGrid();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateCompactLayout();
    }

    /// <summary>
    /// 視窗矮時把高度留給表格（最小化時不動）：
    /// 第 1 級（不到 700）收起統計卡，張數仍在分頁上；
    /// 第 2 級（不到 620，例如 1366×768 筆電開 125%）再收起標題區、備份位置的說明，來源清單縮成一列（超過時捲動）。
    /// </summary>
    private void UpdateCompactLayout()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized || ClientSize.Height <= 0)
        {
            return;
        }
        var h = ClientSize.Height;
        var level = h < Ui.Scaled(this, 620) ? 2 : h < Ui.Scaled(this, 700) ? 1 : 0;
        if (level == compactLevel)
        {
            return;
        }
        compactLevel = level;
        BeginInvoke(() =>
        {
            SuspendLayout();
            tilesRow.Visible = level == 0;
            lblSubtitle.Visible = level < 2;
            if (header is not null)
            {
                header.Visible = level < 2; // 視窗標題列已有程式名稱
            }
            lblBackupHint.Visible = level < 2;
            lstSources.Height = Ui.Scaled(this, level < 2 ? 70 : 34);
            foreach (var card in AllSections(this))
            {
                card.SetCompact(level == 2);
            }
            ApplyGridMetrics();
            ResumeLayout(true);
        });
    }

    internal int CompactLevel => compactLevel;

    private static IEnumerable<SectionPanel> AllSections(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is SectionPanel sp)
            {
                yield return sp;
            }
            foreach (var inner in AllSections(c))
            {
                yield return inner;
            }
        }
    }

    // ================================================================ 表格

    private static bool IsSelectable(SourceItem it) => it.Status == ItemStatus.New && it.CopiedTo is null;

    private void Grid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= view.Count)
        {
            return;
        }
        var it = view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            ColName => it.FileName,
            ColLocation => it.Location,
            ColSize => DedupEngine.FormatSize(it.Size),
            ColStatus => StatusText(it),
            ColMatch => MatchText(it),
            _ => null,
        };
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.Graphics is null || (e.ColumnIndex != ColCheck && e.ColumnIndex != ColStatus))
        {
            return;
        }
        if (e.RowIndex >= view.Count)
        {
            return;
        }
        var g = e.Graphics;
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
        var b = e.CellBounds;
        if (e.ColumnIndex == ColCheck)
        {
            bool? state;
            if (e.RowIndex < 0)
            {
                var eligible = view.Where(IsSelectable).ToList();
                if (eligible.Count == 0)
                {
                    e.Handled = true;
                    return;
                }
                var on = eligible.Count(i => !i.Excluded);
                state = on == eligible.Count ? true : on == 0 ? false : null;
            }
            else
            {
                var it = view[e.RowIndex];
                if (!IsSelectable(it))
                {
                    e.Handled = true;
                    return;
                }
                state = !it.Excluded;
            }
            var s = Ui.Scaled(grid, 16);
            Glyphs.CheckBox(g, new Rectangle(b.Left + (b.Width - s) / 2, b.Top + (b.Height - s) / 2, s, s), state, grid);
        }
        else if (e.RowIndex >= 0)
        {
            var it = view[e.RowIndex];
            var text = StatusText(it);
            var (back, fore) = it.Status switch
            {
                ItemStatus.New when it.CopiedTo is not null => (Theme.SuccessLight, Theme.SuccessText),
                ItemStatus.New when it.IsMissingBefore => (Theme.AmberLight, Theme.Amber),
                ItemStatus.New => (Theme.AccentLight, Theme.Accent),
                ItemStatus.AlreadyBackedUp => (Theme.Surface, Theme.TextGray),
                _ => (Theme.AmberLight, Theme.Amber),
            };
            if ((e.State & DataGridViewElementStates.Selected) != 0 && back == Theme.AccentLight)
            {
                back = Color.White; // 選取列的底色和「新照片」標籤同色，改白底才看得出來
            }
            var size = TextRenderer.MeasureText(g, text, pillFont, Size.Empty, TextFormatFlags.NoPadding);
            var h = size.Height + Ui.Scaled(grid, 8);
            var pill = new Rectangle(b.Left + Ui.Scaled(grid, 8), b.Top + (b.Height - h) / 2, size.Width + Ui.Scaled(grid, 18), h);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = SectionPanel.RoundedRect(pill, Ui.Scaled(grid, 6)))
            using (var brush = new SolidBrush(back))
            {
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, text, pillFont, pill, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else
        {
            e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground);
        }
        e.Handled = true;
    }

    private void Grid_CellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex != ColCheck || e.Button != MouseButtons.Left || busy)
        {
            return;
        }
        if (e.RowIndex < 0)
        {
            var eligible = view.Where(IsSelectable).ToList();
            var check = eligible.Any(i => i.Excluded);
            foreach (var it in eligible)
            {
                it.Excluded = !check;
            }
            AfterSelectionChanged();
        }
        else
        {
            ToggleRows(new[] { e.RowIndex });
        }
    }

    /// <summary>切換多列的勾選：以第一個可勾選的列為準，全部改成相反狀態。</summary>
    private void ToggleRows(IEnumerable<int> rows)
    {
        if (busy)
        {
            return;
        }
        var items = rows.Where(r => r >= 0 && r < view.Count).Select(r => view[r]).Where(IsSelectable).ToList();
        if (items.Count == 0)
        {
            return;
        }
        var newExcluded = !items[0].Excluded;
        foreach (var it in items)
        {
            it.Excluded = newExcluded;
        }
        AfterSelectionChanged();
    }

    private void AfterSelectionChanged()
    {
        grid.Invalidate();
        UpdateSelectionLabel();
        UpdateButtons();
    }

    internal void SetChecked(int row, bool isChecked)
    {
        if (row >= 0 && row < view.Count && IsSelectable(view[row]))
        {
            view[row].Excluded = !isChecked;
            AfterSelectionChanged();
        }
    }

    private void UpdateSelectionLabel()
    {
        if (analysis is null)
        {
            lblSelection.Text = "";
            return;
        }
        var pending = analysis.Items.Where(IsSelectable).ToList();
        var on = pending.Where(i => !i.Excluded).ToList();
        if (pending.Count == 0)
        {
            lblSelection.Text = analysis.Items.Any(i => i.CopiedTo is not null) ? "新照片都已複製完成" : "沒有需要複製的照片";
            return;
        }
        lblSelection.Text = $"已勾選 {on.Count:N0} 張新照片（{DedupEngine.FormatSize(on.Sum(i => i.Size))}）"
            + (on.Count < pending.Count ? $"，{pending.Count - on.Count:N0} 張未勾選不會複製" : "");
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
            ItemStatus.New when it.Excluded => "未勾選，不會複製",
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
        IEnumerable<SourceItem> all = analysis.Items;
        var keyword = search.Text.Trim();
        if (keyword.Length > 0)
        {
            all = all.Where(i => i.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                 || i.Location.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        view = tabs.SelectedIndex switch
        {
            FilterNew => all.Where(i => i.Status == ItemStatus.New).OrderBy(i => i.IsMissingBefore ? 0 : 1).ToList(),
            FilterMissing => all.Where(i => i.IsMissingBefore).ToList(),
            FilterExisting => all.Where(i => i.Status == ItemStatus.AlreadyBackedUp).ToList(),
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
        if (analysis.Items.Count > 0)
        {
            lblHint.Visible = view.Count == 0;
            lblHint.Text = keyword.Length > 0 ? $"沒有檔名包含「{keyword}」的項目" : "這個分頁沒有項目";
        }
    }

    private void ResetResults()
    {
        analysis = null;
        lastOutputFolder = null;
        RefreshGrid();
        tileNew.SetValues("－", "");
        tileExisting.SetValues("－", "");
        tileDuplicate.SetValues("－", "");
        tileTotal.SetValues("－", "");
        SetDuplicateTileVisible(false);
        tabs.SetTab(FilterAll, "全部", true);
        tabs.SetTab(FilterNew, "新照片", true);
        tabs.SetTab(FilterMissing, "曾遺失", false);
        tabs.SetTab(FilterExisting, "硬碟已有", true);
        tabs.SetTab(FilterDuplicate, "來源資料重複", false);
        tabs.SelectedIndex = FilterAll;
        lblHint.Text = "選好備份位置、加入來源資料後，按「開始比對」";
        lblHint.Visible = true;
        lblSelection.Text = "";
        lblState.Text = "";
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Value = 0;
        lblStatus.Text = "";
        SetStatusColor(Theme.TextGray);
    }

    private bool duplicateTileShown;

    /// <summary>顯示或隱藏「來源資料重複」統計卡，其他卡平分寬度（視窗顯示前 Control.Visible 一律是 false，所以自己記錄）。</summary>
    private void SetDuplicateTileVisible(bool visible)
    {
        duplicateTileShown = visible;
        tileDuplicate.Visible = visible;
        var shown = visible ? 4 : 3;
        for (var i = 0; i < tilesRow.ColumnStyles.Count; i++)
        {
            tilesRow.ColumnStyles[i].Width = i == 2 && !visible ? 0 : 100f / shown;
        }
    }

    private void ShowSummary()
    {
        if (analysis is null)
        {
            return;
        }
        var newCount = analysis.Count(ItemStatus.New);
        var copied = analysis.Items.Count(i => i.CopiedTo is not null);
        var missing = analysis.Items.Count(i => i.IsMissingBefore);
        var existing = analysis.Count(ItemStatus.AlreadyBackedUp);
        var duplicates = analysis.Count(ItemStatus.DuplicateInBatch);
        var detail = copied > 0
            ? $"已複製 {copied:N0} 張"
            : DedupEngine.FormatSize(analysis.Bytes(ItemStatus.New)) + (missing > 0 ? $"，其中 {missing:N0} 張曾遺失" : "");
        tileNew.SetValues(newCount.ToString("N0"), detail);
        tileExisting.SetValues(existing.ToString("N0"), "內容相同，不會複製");
        tileDuplicate.SetValues(duplicates.ToString("N0"), "只複製一份，點一下查看");
        SetDuplicateTileVisible(duplicates > 0);
        tileTotal.SetValues(analysis.Items.Count.ToString("N0"), "照片與影片");
        tabs.SetTab(FilterAll, $"全部 {analysis.Items.Count:N0}", true);
        tabs.SetTab(FilterNew, $"新照片 {newCount:N0}", true);
        tabs.SetTab(FilterMissing, $"曾遺失 {missing:N0}", missing > 0);
        tabs.SetTab(FilterExisting, $"硬碟已有 {existing:N0}", true);
        tabs.SetTab(FilterDuplicate, $"來源資料重複 {duplicates:N0}", duplicates > 0);
        lblHint.Visible = analysis.Items.Count == 0 || view.Count == 0;
        if (analysis.Items.Count == 0)
        {
            lblHint.Text = "來源資料裡沒有找到任何照片或影片";
        }
        UpdateSelectionLabel();
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
        var list = lstSources.Paths.ToList();
        var existing = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alreadyListed = new List<string>();
        foreach (var p in paths)
        {
            if (existing.Add(p))
            {
                list.Add(p);
            }
            else
            {
                alreadyListed.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(p)));
            }
        }
        lstSources.SetPaths(list);
        OnInputsChanged();
        if (alreadyListed.Count > 0)
        {
            // 清單看起來沒變，要明確告訴使用者發生了什麼
            var names = string.Join("、", alreadyListed.Take(3)) + (alreadyListed.Count > 3 ? " 等" : "");
            lblStatus.Text = $"「{names}」已經在清單中，請按「開始比對」重新比對。";
        }
    }

    internal void RemoveSource(string path)
    {
        if (busy)
        {
            return;
        }
        lstSources.SetPaths(lstSources.Paths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList());
        OnInputsChanged();
    }

    private void OnInputsChanged()
    {
        if (analysis is not null)
        {
            ResetResults();
        }
        UpdateSourcesBadge();
        UpdateButtons();
    }

    private void UpdateSourcesBadge()
    {
        var n = lstSources.Paths.Count;
        sourcesCard.Badge = n > 0 ? $"{n} 個項目" : null;
    }

    private void UpdateButtons()
    {
        var ready = Directory.Exists(txtBackup.Text) && lstSources.Paths.Count > 0;
        btnBrowseBackup.Enabled = !busy;
        btnAdd.Enabled = !busy;
        btnNaming.Enabled = !busy;
        btnAnalyze.Enabled = !busy && ready;
        var selected = analysis?.Items.Count(i => IsSelectable(i) && !i.Excluded) ?? 0;
        btnCopy.Enabled = !busy && selected > 0;
        btnCopy.Text = selected > 0 ? $"複製新照片（{selected:N0}）" : "複製新照片";
        btnCancel.Enabled = busy;
        progressArea.Visible = busy;
        lblState.Visible = !busy;
        btnExport.Enabled = !busy && analysis is not null && analysis.Items.Count > 0;
        btnOpenFolder.Enabled = lastOutputFolder is not null && Directory.Exists(lastOutputFolder);

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
        statusBar.Invalidate();
        UseWaitCursor = false;
    }

    internal async Task AnalyzeAsync()
    {
        if (busy)
        {
            return;
        }
        var backup = txtBackup.Text;
        var sources = lstSources.Paths.ToList();
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
            tabs.SelectedIndex = FilterAll;
            RefreshGrid();
            ShowSummary();
            progressBar.Style = ProgressBarStyle.Continuous;
            FillProgressBar();
            var newCount = result.Count(ItemStatus.New);
            var missingCount = result.Items.Count(i => i.IsMissingBefore);
            var status = newCount > 0
                ? $"比對完成：找到 {newCount:N0} 張新照片，按「複製新照片」放進硬碟。"
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
            lblState.Text = "比對完成";
            SetStatusColor(Theme.Success);
            UpdateButtons();
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
            SetStatusColor(Theme.Amber);
        }
        catch (UserFacingException ex)
        {
            ResetResults();
            lblStatus.Text = "比對失敗。";
            SetStatusColor(Theme.Amber);
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResetResults();
            lblStatus.Text = "比對失敗。";
            SetStatusColor(Theme.Amber);
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
        var pending = analysis.Items.Where(i => IsSelectable(i) && !i.Excluded).ToList();
        var unchecked_ = analysis.Items.Count(i => IsSelectable(i) && i.Excluded);
        if (pending.Count == 0)
        {
            return;
        }
        var outDir = DedupEngine.GetStagingFolder(analysis.BackupRoot, DateTime.Now);
        var naming = settings.Naming.Clone();
        if (confirm)
        {
            var answer = MessageBox.Show(this,
                $"要把 {pending.Count:N0} 張勾選的新照片（{DedupEngine.FormatSize(pending.Sum(i => i.Size))}）複製到：" + Environment.NewLine
                + outDir + Environment.NewLine + Environment.NewLine
                + (unchecked_ > 0 ? $"另外 {unchecked_:N0} 張未勾選，這次不會複製。" + Environment.NewLine : "")
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
            lblState.Text = "複製完成";
            SetStatusColor(Theme.Success);
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
            lblStatus.Text = "已取消複製。已經複製完成的照片會保留，再按一次「複製新照片」會繼續複製剩下的。";
            SetStatusColor(Theme.Amber);
            lastOutputFolder = outDir;
        }
        catch (UserFacingException ex)
        {
            lblStatus.Text = "複製失敗。";
            SetStatusColor(Theme.Amber);
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lblStatus.Text = "複製失敗。";
            SetStatusColor(Theme.Amber);
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
        updateBar.BackColor = Theme.AccentLight;
        updateBar.Padding = new Padding(20, 6, 16, 6);
        updateBar.Visible = false;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.AccentLight,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lblUpdate.Dock = DockStyle.Fill;
        lblUpdate.AutoEllipsis = true;
        lblUpdate.TextAlign = ContentAlignment.MiddleLeft;
        lblUpdate.ForeColor = Theme.Accent;
        lblUpdate.Font = new Font(Theme.FontName, 10f, FontStyle.Bold);
        lblUpdate.Margin = new Padding(0);
        lnkUpdateNotes.Text = "更新內容";
        lnkUpdateNotes.AutoSize = true;
        lnkUpdateNotes.Anchor = AnchorStyles.Left;
        lnkUpdateNotes.LinkColor = Theme.Accent;
        lnkUpdateNotes.ActiveLinkColor = Theme.AccentHover;
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

    /// <summary>
    /// 下載新版、替換 exe、重新啟動。比對或複製進行中時不更新。
    /// 主畫面霧化，更新的確認、進度、結果都在獨立的更新視窗裡（YAN 2026-11-13）。
    /// </summary>
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

        using var frost = new FrostLayer(this);
        using var dlg = new UpdateDialog(info, VersionText);
        dlg.ShowOver(this);
        if (confirm && !await dlg.WaitForConfirmAsync())
        {
            dlg.CloseQuietly();
            return;
        }

        dlg.StartProgress();
        SetBusy(true);
        cts = new CancellationTokenSource();
        var token = cts;
        dlg.CancelRequested += (_, _) => token.Cancel();
        lblStatus.Text = "正在下載新版本…";
        var progress = new Progress<double>(f =>
        {
            if (!busy)
            {
                return; // 進度回報是非同步送達，最後一次可能晚於完成訊息，不能蓋掉它
            }
            dlg.SetProgress(f);
            lblStatus.Text = $"正在下載新版本：{f:P0}";
        });
        if (OnUpdateDialogShown is not null)
        {
            await OnUpdateDialogShown(dlg);
        }
        string? error = null;
        try
        {
            await Updater.DownloadAndReplaceAsync(info, exe, progress, token.Token);
            LastUpdateError = null;
            SetBusy(false);
            dlg.ShowDone();
            lblStatus.Text = $"已更新到 v{info.Version.ToString(3)}。";
            updateBar.Visible = false;
            pendingUpdate = null;
            if (OnUpdateDone is not null)
            {
                await OnUpdateDone(dlg);
            }
            await Task.Delay(restart ? 900 : 100);
            dlg.CloseQuietly();
            if (restart)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Close();
            }
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lblStatus.Text = "已取消更新，目前版本不受影響。";
            LastUpdateError = "取消";
        }
        catch (UnauthorizedAccessException)
        {
            LastUpdateError = "沒有寫入權限";
            lblStatus.Text = "更新失敗：程式所在的資料夾無法寫入。";
            error = "程式所在的資料夾無法寫入，所以沒辦法更新。" + Environment.NewLine
                + "請把程式放到桌面或「文件」等自己的資料夾後再更新。";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            LastUpdateError = ex.Message;
            lblStatus.Text = "更新失敗，目前版本不受影響。";
            error = "下載或替換時發生錯誤：" + Environment.NewLine + ex.Message;
        }
        SetBusy(false);
        if (error is null)
        {
            dlg.CloseQuietly();
        }
        else
        {
            dlg.ShowError(error);
            await dlg.WhenClosed;
        }
        if (closeAfterCancel)
        {
            BeginInvoke(Close);
        }
    }

    /// <summary>介面測試：更新視窗進入下載階段後、開始下載前呼叫（用來截圖）。</summary>
    internal Func<UpdateDialog, Task>? OnUpdateDialogShown { get; set; }

    /// <summary>介面測試：下載完成、視窗關閉前呼叫。</summary>
    internal Func<UpdateDialog, Task>? OnUpdateDone { get; set; }

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
            w.WriteLine("狀態,勾選,檔名,大小(位元組),來源資料中的位置,硬碟中對應的檔案");
            foreach (var it in analysis.Items)
            {
                w.WriteLine(string.Join(",",
                    Csv(StatusText(it)), IsSelectable(it) ? (it.Excluded ? "否" : "是") : "", Csv(it.FileName), it.Size.ToString(),
                    Csv(it.Location), Csv(MatchText(it))));
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
        sb.Append("完整清單：點「來源資料重複」分頁，或按「匯出清單…」存成 Excel 可開的檔案。");
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

    internal string DuplicateLabelText => analysis is not null && duplicateTileShown
        ? $"來源資料重複：{analysis.Count(ItemStatus.DuplicateInBatch):N0} 張（只複製一份）"
        : "";

    internal void SelectFilter(int index) => tabs.SelectedIndex = index;

    internal void SetSearch(string text) => search.Text = text;

    internal string SelectionText => lblSelection.Text;

    internal string CopyButtonText => btnCopy.Text;

    internal string StatusLine => lblStatus.Text;

    internal int VisibleRowCount => grid.RowCount;

    internal static string VersionText =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
}
