namespace Photo2Disk;

/// <summary>「更多設定」視窗：複製新照片時的資料夾、檔名、DNG 轉檔，以上方分頁切換。</summary>
internal sealed class NamingDialog : Form
{
    private const string CustomPresetText = "（自訂）";

    private readonly NamingOptions options;
    private readonly ToolTip tip = new() { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };

    private readonly ToggleSwitch folderToggle = new("依複製日期", "依拍攝年份");
    private readonly ComboBox cboFolder = new();
    private readonly TableLayoutPanel folderDetail = new();
    private readonly ToggleSwitch toggle = new("保留原檔名", "依規則改名");
    private readonly ToggleSwitch convertToggle = new("不轉檔", "DNG 另存 JPG");
    private readonly ComboBox cboPreset = new();
    private readonly ReorderCheckList lstParts = new();
    private readonly Button btnUp = Ui.MakeButton("上移", false);
    private readonly Button btnDown = Ui.MakeButton("下移", false);
    private const string CustomSeparatorText = "自訂…";

    private readonly ComboBox cboPrecision = new();
    private readonly ComboBox cboDateStyle = new();
    private readonly ComboBox cboSeparator = new();
    private readonly TextBox txtSeparator = new();
    private readonly TextBox txtCustom = new();
    private readonly ComboBox cboDigits = new();
    private readonly Label lblPrecision = new();
    private readonly Label lblDateStyle = new();
    private readonly Label lblSeparatorCustom = new();
    private readonly Label lblCustom = new();
    private readonly Label lblDigits = new();
    private readonly TableLayoutPanel partsRow = new();
    private readonly Label[] previewOld = new Label[FileNamer.Samples.Length];
    private readonly Label[] previewNew = new Label[FileNamer.Samples.Length];
    private readonly Button btnOk = Ui.MakeButton("確定", true);
    private readonly Button btnCancel = Ui.MakeButton("取消", false);
    private bool loading;

    /// <summary>分隔符號選了「自訂…」。輸入的內容剛好是清單裡的符號時也維持自訂，避免輸入到一半選單自己跳走。</summary>
    private bool customSeparator;

    public NamingDialog(NamingOptions current)
    {
        options = current.Clone();
        options.Normalize();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(Theme.FontName, 10f);
        ForeColor = Theme.TextDark;
        BackColor = Color.White;
        Text = Title;
        Icon = Ui.AppIcon ?? Icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 14, 16, 12);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 720));

        // 上方分頁切換功能（YAN 2026-11-26：之後會放更多功能，改名與轉檔分開，點分頁選擇）
        pageTabs.Dock = DockStyle.Fill;
        pageTabs.Height = 36;
        pageTabs.Margin = new Padding(0, 0, 0, 12);
        pageTabs.Font = new Font(Theme.FontName, 10.5f);
        pageTabs.BackColor = BackColor;
        pageTabs.SetTabs(PageNames);
        pageTabs.SelectedIndexChanged += (_, _) => ShowPage(pageTabs.SelectedIndex);
        root.Controls.Add(pageTabs);

        pageFolder = Page(BuildFolderSection());
        pageName = Page(BuildModeSection(), BuildPartsRow());
        pageConvert = Page(BuildConvertSection());
        pageHost.ColumnCount = 1;
        pageHost.AutoSize = true;
        pageHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        pageHost.Dock = DockStyle.Fill;
        pageHost.Margin = new Padding(0);
        pageHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 每頁各占一列並明確指定位置（隱藏的列高度為 0）；放在同一格會被自動排列擠到別格
        for (var i = 0; i < Pages.Length; i++)
        {
            pageHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pageHost.Controls.Add(Pages[i], 0, i);
        }
        root.Controls.Add(pageHost);
        previewSection = BuildPreviewSection();
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ResumeLayout(false);
        PerformLayout();

        LoadOptions();
        ShowPage(0);
    }

    /// <summary>視窗標題，也是主畫面按鈕的文字（YAN 2026-11-26 由「檔名與資料夾」改名）。</summary>
    public const string Title = "更多設定";

    public static readonly string[] PageNames = { "資料夾", "檔名", "DNG 轉檔" };

    private readonly TabStrip pageTabs = new();
    private readonly TableLayoutPanel pageHost = new();
    private readonly Control pageFolder;
    private readonly Control pageName;
    private readonly Control pageConvert;
    private readonly Control previewSection;

    private Control[] Pages => new[] { pageFolder, pageName, pageConvert };

    /// <summary>一頁的內容：由上而下排列的區塊。</summary>
    private static Control Page(params Control[] sections)
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < sections.Length; i++)
        {
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(sections[i], 0, i);
        }
        return page;
    }

    /// <summary>切換分頁。</summary>
    internal void ShowPage(int index)
    {
        SuspendLayout();
        var pages = Pages;
        AttachPreview(pages[index]);
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visible = i == index;
        }
        if (pageTabs.SelectedIndex != index)
        {
            pageTabs.SelectedIndex = index;
        }
        ResumeLayout(true);
    }

    /// <summary>預覽只有一份，放在資料夾頁或檔名頁的最下方；DNG 轉檔頁不放。</summary>
    private void AttachPreview(Control page)
    {
        if (page == pageConvert || previewSection.Parent == page)
        {
            return;
        }
        var table = (TableLayoutPanel)page;
        previewSection.Parent?.Controls.Remove(previewSection);
        if (table.RowStyles.Count <= table.Controls.Count)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        table.Controls.Add(previewSection, 0, table.Controls.Count);
    }

    /// <summary>
    /// 分頁內容區固定成最高那一頁的高度（含預覽；檔名頁以「依規則改名」展開時計），
    /// 切換分頁或撥桿時視窗不會忽大忽小。
    /// </summary>
    private void FixPageHeight()
    {
        var current = pageTabs.SelectedIndex;
        partsRow.Visible = true;
        var width = pageHost.Width > 0 ? pageHost.Width : Ui.Scaled(this, 720);
        var max = 0;
        foreach (var page in Pages)
        {
            AttachPreview(page);
            max = Math.Max(max, page.GetPreferredSize(new Size(width, 0)).Height);
        }
        // 不能事先讀 partsRow.Visible 再還原：所在分頁隱藏時它一律回傳 false
        partsRow.Visible = options.Enabled;
        pageHost.MinimumSize = new Size(0, max);
        ShowPage(current);
    }

    /// <summary>按「確定」後的規則。</summary>
    public NamingOptions Result => options;

    /// <summary>介面測試用。</summary>
    internal ReorderCheckList PartsList => lstParts;

    /// <summary>介面測試用：「保留原檔名／依規則改名」撥桿。</summary>
    internal ToggleSwitch RenameToggle => toggle;

    // ================================================================ 版面

    private Control BuildFolderSection()
    {
        var root = DedupEngine.StagingFolderName;
        var section = new SectionPanel("資料夾",
            $"依複製日期：每次複製放進「{root}\\今天日期」（ex. {root}\\{DateTime.Now:yyyy-MM-dd}）。" + Environment.NewLine
            + $"依拍攝年份：依每張照片的拍攝日期分資料夾，「分層」可選：" + Environment.NewLine
            + $"　年份（ex. {root}\\2024）" + Environment.NewLine
            + $"　年份\\年份-月份（ex. {root}\\2024\\2024-03）" + Environment.NewLine
            + $"　年份\\月份（ex. {root}\\2024\\03）" + Environment.NewLine
            + "拍攝日期取自照片記錄的拍攝時間、影片記錄的建立時間或原檔名裡的日期；" + Environment.NewLine
            + $"都找不到的照片放進「{root}\\{NamingOptions.UnknownDateFolder}」，不用檔案修改時間（可能是下載當天）分類。" + Environment.NewLine
            + "資料夾都在備份資料夾裡，之後可以隨意搬移，不影響下次比對。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        folderToggle.Margin = new Padding(0, 0, 0, 2);
        folderToggle.CheckedChanged += (_, _) => OnControlsChanged();

        folderDetail.ColumnCount = 2;
        folderDetail.AutoSize = true;
        folderDetail.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        folderDetail.Margin = new Padding(0, 8, 0, 0);
        folderDetail.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderDetail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderDetail.Controls.Add(new Label
        {
            Text = "分層",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0),
        }, 0, 0);
        cboFolder.DropDownStyle = ComboBoxStyle.DropDownList;
        cboFolder.Dock = DockStyle.Fill;
        cboFolder.Margin = new Padding(0);
        cboFolder.Items.AddRange(NamingOptions.FolderLayers.Select(f => (object)f.Label).ToArray());
        cboFolder.SelectedIndexChanged += (_, _) => OnControlsChanged();
        tip.SetToolTip(cboFolder, "只分年份，或年份底下再依月份分資料夾（月份資料夾可以含年份或只有月份）");
        folderDetail.Controls.Add(cboFolder, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(folderToggle, 0, 0);
        folderDetail.Dock = DockStyle.Fill;
        layout.Controls.Add(folderDetail, 0, 1);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildModeSection()
    {
        var section = new SectionPanel("改名方式",
            "保留原檔名：和來源資料裡的檔名相同（撞名時加「 (2)」）。" + Environment.NewLine
            + "依規則改名：複製時依下方的組成重新命名。" + Environment.NewLine
            + "只影響這次複製進「_待整理」的照片，硬碟裡原有的檔案不會改名。" + Environment.NewLine
            + "比對看的是檔案內容，改名不影響下次比對。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        toggle.Margin = new Padding(0, 0, 0, 2);
        toggle.Dock = DockStyle.Top;
        toggle.CheckedChanged += (_, _) => OnControlsChanged();
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        host.Controls.Add(toggle);
        section.Controls.Add(host);
        return section;
    }

    /// <summary>DNG 轉檔開關（全寬一列），右側是第三方元件授權連結。</summary>
    private Control BuildConvertSection()
    {
        var section = new SectionPanel("DNG 轉檔",
            "開啟後，複製 DNG（RAW 照片）時另外存一份同檔名的 JPG 放在旁邊，DNG 原檔照常保留。" + Environment.NewLine
            + "顏色以拍攝裝置自己沖洗的結果為準：DNG 內有全尺寸預覽就直接沿用；" + Environment.NewLine
            + "只有小預覽時依它的顏色重建；都沒有時用一般 RAW 沖洗，顏色可能和相機不同。" + Environment.NewLine
            + "來源資料同一個資料夾已有同名 JPG（相機同時拍 JPG＋RAW）時不另外轉檔。" + Environment.NewLine
            + "每張約需 1～3 秒，轉出的 JPG 不影響下次比對。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
        };
        convertToggle.Margin = new Padding(0, 0, 0, 2);
        convertToggle.CheckedChanged += (_, _) => OnControlsChanged();
        var notices = new LinkLabel
        {
            Text = "第三方元件授權",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 4, 0, 0),
            LinkColor = Theme.Accent,
        };
        notices.LinkClicked += (_, _) => MessageBox.Show(this, ThirdPartyNotices.Text, "第三方元件授權",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(convertToggle, 0, 0);
        row.Controls.Add(notices, 1, 0);
        section.Controls.Add(row);
        return section;
    }

    /// <summary>「檔名組成」與「細項設定」左右並排，只在依規則改名時出現。</summary>
    private Control BuildPartsRow()
    {
        partsRow.Dock = DockStyle.Fill;
        partsRow.ColumnCount = 2;
        partsRow.RowCount = 1;
        partsRow.AutoSize = true;
        partsRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        partsRow.Margin = new Padding(0);
        partsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        partsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        partsRow.Controls.Add(BuildPartsSection(), 0, 0);
        partsRow.Controls.Add(BuildDetailSection(), 1, 0);
        return partsRow;
    }

    private Control BuildPartsSection()
    {
        var section = new SectionPanel("檔名組成",
            "點一下勾選要放進檔名的項目；按住項目上下拖動，或用「上移」「下移」調整順序。" + Environment.NewLine + Environment.NewLine
            + "拍攝日期：依序取自照片記錄的拍攝時間、影片記錄的建立時間、原檔名裡的日期（ex. IMG_20240315_…），" + Environment.NewLine
            + "　　　　　都沒有時用檔案修改時間，比對結果會註明。精度與日期內的分隔在右側選。" + Environment.NewLine
            + "原檔名：來源資料裡的檔名（不含副檔名）。" + Environment.NewLine
            + "自訂文字：在右側輸入（ex. 日本旅遊）。" + Environment.NewLine
            + "流水號：依拍攝時間排序編號；其他文字相同的照片各自從 1 開始（ex. 每天從 001 開始）。" + Environment.NewLine
            + "相機／手機型號：照片有記錄才會加，影片通常沒有。" + Environment.NewLine
            + "照片／影片：依副檔名判斷。" + Environment.NewLine
            + "所在資料夾名稱：照片在來源資料中所在的資料夾（ex. Google 相簿匯出時的相簿名稱）。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 6, 12),
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

        var presetRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8),
        };
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        presetRow.Controls.Add(new Label
        {
            Text = "快速套用：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 4, 0),
        }, 0, 0);
        cboPreset.DropDownStyle = ComboBoxStyle.DropDownList;
        cboPreset.Dock = DockStyle.Fill;
        cboPreset.Margin = new Padding(0);
        cboPreset.Items.AddRange(NamingOptions.Presets.Select(p => (object)p.Name).ToArray());
        cboPreset.Items.Add(CustomPresetText);
        cboPreset.SelectedIndexChanged += (_, _) =>
        {
            if (loading || cboPreset.SelectedIndex < 0 || cboPreset.SelectedIndex >= NamingOptions.Presets.Length)
            {
                return;
            }
            options.Apply(NamingOptions.Presets[cboPreset.SelectedIndex]);
            LoadOptions();
        };
        tip.SetToolTip(cboPreset, "選一組常用的檔名組成，選完還可以再調整");
        presetRow.Controls.Add(cboPreset, 1, 0);
        layout.Controls.Add(presetRow, 0, 0);
        layout.SetColumnSpan(presetRow, 2);

        lstParts.AutoSize = true;
        lstParts.Dock = DockStyle.Fill;
        lstParts.Margin = new Padding(0, 0, 8, 0);
        lstParts.ItemsChanged += (_, _) => OnControlsChanged();
        lstParts.SelectedIndexChanged += (_, _) => UpdateMoveButtons();
        tip.SetToolTip(lstParts, "點一下勾選；按住上下拖動可以調整順序");

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        foreach (var b in new[] { btnUp, btnDown })
        {
            b.MinimumSize = Size.Empty;
            b.AutoSize = false;
            b.Size = new Size(70, 32);
            b.Padding = new Padding(0);
            b.Margin = new Padding(0, b == btnUp ? 0 : 6, 0, 0);
            buttons.Controls.Add(b);
        }
        btnUp.Click += (_, _) => MoveSelected(-1);
        btnDown.Click += (_, _) => MoveSelected(1);
        layout.Controls.Add(lstParts, 0, 1);
        layout.Controls.Add(buttons, 1, 1);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildDetailSection()
    {
        var section = new SectionPanel("細項設定", null, tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(6, 0, 0, 12),
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 每列明確指定欄、列位置：自動排列會跳過隱藏的控制項，之後再顯示時標籤與下拉選單會錯位
        // （2026-11-26 YAN 回報：切到「依規則改名」後下拉選單跑到左欄、標籤跑到右欄）
        var rowIndex = 0;
        void Row(Label label, string text, Control control)
        {
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 0, 8, 8);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 8);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(label, 0, rowIndex);
            grid.Controls.Add(control, 1, rowIndex);
            rowIndex++;
        }

        foreach (var cbo in new[] { cboPrecision, cboDateStyle, cboSeparator, cboDigits })
        {
            cbo.DropDownStyle = ComboBoxStyle.DropDownList;
            cbo.SelectedIndexChanged += (_, _) => OnControlsChanged();
        }
        cboPrecision.Items.AddRange(NamingOptions.DatePrecisions.Select(p => (object)p.Label).ToArray());
        cboDateStyle.Items.AddRange(NamingOptions.DateStyles.Select(s => (object)NamingOptions.DateStyleLabel(s.Value, s.Name)).ToArray());
        cboSeparator.Items.AddRange(NamingOptions.Separators.Select(s => (object)s.Label).ToArray());
        cboSeparator.Items.Add(CustomSeparatorText);
        cboDigits.Items.AddRange(NamingOptions.SequenceDigitChoices.Select(d => (object)$"{d} 位（ex. {1.ToString("D" + d)}）").ToArray());
        tip.SetToolTip(cboPrecision, "檔名裡的拍攝日期要寫到年、月、日，或連時分秒一起寫");
        tip.SetToolTip(cboDateStyle, "年、月、日之間用什麼隔開；時分秒接在「_」後面（中文則寫成 14時30分22秒）");
        tip.SetToolTip(cboSeparator, "檔名組成的各項目之間用什麼隔開；選「自訂…」可以自己輸入（最多 5 個字）");
        txtSeparator.PlaceholderText = "ex. #";
        txtSeparator.MaxLength = NamingOptions.MaxSeparatorLength;
        txtSeparator.TextChanged += (_, _) => OnControlsChanged();
        tip.SetToolTip(txtSeparator, "不能用在檔名的字元（\\ / : * ? \" < > |）會自動拿掉；留空表示不隔開");
        txtCustom.PlaceholderText = "ex. 日本旅遊";
        txtCustom.MaxLength = 40;
        txtCustom.TextChanged += (_, _) => OnControlsChanged();

        Row(lblPrecision, "日期精度", cboPrecision);
        Row(lblDateStyle, "日期分隔", cboDateStyle);
        Row(new Label(), "分隔符號", cboSeparator);
        Row(lblSeparatorCustom, "自訂分隔", txtSeparator);
        Row(lblCustom, "自訂文字", txtCustom);
        Row(lblDigits, "流水號位數", cboDigits);
        section.Controls.Add(grid);
        return section;
    }

    private Control BuildPreviewSection()
    {
        var section = new SectionPanel("預覽",
            "ex. 2024-03-15 用 iPhone 15 Pro 拍的兩張照片，以及隔天的一段影片，" + Environment.NewLine
            + "放在來源資料的「2024 日本旅遊」資料夾。實際資料夾與檔名依每張照片記錄的拍攝時間而定。" + Environment.NewLine
            + "右側顯示的是「_待整理」底下的位置。",
            tip)
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < FileNamer.Samples.Length; i++)
        {
            previewOld[i] = new Label
            {
                Text = FileNamer.Samples[i].OriginalName,
                AutoSize = true,
                ForeColor = Theme.TextGray,
                Margin = new Padding(0, 0, 8, 6),
            };
            previewNew[i] = new Label
            {
                AutoSize = true,
                AutoEllipsis = true,
                ForeColor = Theme.TextDark,
                Font = new Font(Theme.FontName, 10f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 6),
            };
            table.Controls.Add(previewOld[i]);
            table.Controls.Add(new Label { Text = "→", AutoSize = true, ForeColor = Theme.TextGray, Margin = new Padding(0, 0, 8, 6) });
            table.Controls.Add(previewNew[i]);
        }
        section.Controls.Add(table);
        return section;
    }

    private Control BuildButtons()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        foreach (var b in new[] { btnCancel, btnOk })
        {
            b.AutoSize = false;
            b.Size = new Size(100, 36);
            b.Margin = new Padding(b == btnOk ? 0 : 10, 0, 0, 0);
        }
        btnCancel.DialogResult = DialogResult.Cancel;
        btnOk.Click += (_, _) =>
        {
            if (TryValidate(out var message))
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        row.Controls.Add(btnCancel);
        row.Controls.Add(btnOk);
        return row;
    }

    protected override void OnLoad(EventArgs e)
    {
        FixPageHeight();
        base.OnLoad(e);
        // 小螢幕放不下時改成可捲動
        var area = Screen.FromControl(this).WorkingArea;
        if (Height > area.Height)
        {
            AutoSize = false;
            AutoScroll = true;
            Height = area.Height;
            Top = area.Top;
        }
    }

    // ================================================================ 資料

    private void LoadOptions()
    {
        loading = true;
        try
        {
            folderToggle.Checked = options.ByShotDate;
            cboFolder.SelectedIndex = Math.Max(0, Array.FindIndex(NamingOptions.FolderLayers, f => f.Value == options.Folders));
            toggle.Checked = options.Enabled;
            convertToggle.Checked = options.ConvertDng;
            lstParts.SetItems(options.Parts.Select(p => new ReorderCheckList.Item
            {
                Tag = p.Kind,
                Text = NamingOptions.PartLabel(p.Kind),
                Checked = p.Enabled,
            }));
            NamingOptions.TryParseDateFormat(options.DateFormat, out var precision, out var style);
            cboPrecision.SelectedIndex = Array.FindIndex(NamingOptions.DatePrecisions, p => p.Value == precision);
            cboDateStyle.SelectedIndex = Array.FindIndex(NamingOptions.DateStyles, s => s.Value == style);
            var sep = Array.FindIndex(NamingOptions.Separators, s => s.Value == options.Separator);
            customSeparator = sep < 0;
            cboSeparator.SelectedIndex = customSeparator ? cboSeparator.Items.Count - 1 : sep;
            txtSeparator.Text = customSeparator ? options.Separator : "";
            cboDigits.SelectedIndex = Array.IndexOf(NamingOptions.SequenceDigitChoices, options.SequenceDigits);
            txtCustom.Text = options.CustomText;
        }
        finally
        {
            loading = false;
        }
        RefreshView();
    }

    private void OnControlsChanged()
    {
        if (loading)
        {
            return;
        }
        options.Folders = !folderToggle.Checked || cboFolder.SelectedIndex < 0
            ? FolderMode.CopyDate
            : NamingOptions.FolderLayers[cboFolder.SelectedIndex].Value;
        options.Enabled = toggle.Checked;
        options.ConvertDng = convertToggle.Checked;
        options.Parts = lstParts.Items
            .Select(p => new NamePart { Kind = (NamePartKind)p.Tag, Enabled = p.Checked })
            .ToList();
        if (cboPrecision.SelectedIndex >= 0 && cboDateStyle.SelectedIndex >= 0)
        {
            options.DateFormat = NamingOptions.BuildDateFormat(
                NamingOptions.DatePrecisions[cboPrecision.SelectedIndex].Value,
                NamingOptions.DateStyles[cboDateStyle.SelectedIndex].Value);
        }
        if (cboSeparator.SelectedIndex >= 0)
        {
            var wasCustom = customSeparator;
            customSeparator = cboSeparator.SelectedIndex >= NamingOptions.Separators.Length;
            if (customSeparator && !wasCustom && txtSeparator.Text.Length == 0)
            {
                // 剛切到自訂時，先帶入目前的符號當作起點
                loading = true;
                txtSeparator.Text = options.Separator;
                loading = false;
            }
            options.Separator = customSeparator
                ? NamingOptions.CleanSeparator(txtSeparator.Text)
                : NamingOptions.Separators[cboSeparator.SelectedIndex].Value;
        }
        if (cboDigits.SelectedIndex >= 0)
        {
            options.SequenceDigits = NamingOptions.SequenceDigitChoices[cboDigits.SelectedIndex];
        }
        options.CustomText = txtCustom.Text;
        RefreshView();
    }

    private void RefreshView()
    {
        SuspendLayout();
        folderDetail.Visible = options.ByShotDate;
        partsRow.Visible = options.Enabled;
        var usesDate = options.Uses(NamePartKind.Date);
        lblPrecision.Visible = cboPrecision.Visible = usesDate;
        lblDateStyle.Visible = cboDateStyle.Visible = usesDate;
        // 只寫年份時沒有分隔可選，保留選項但停用，切回年月時沿用原本的選擇
        lblDateStyle.Enabled = cboDateStyle.Enabled = cboPrecision.SelectedIndex > 0;
        lblSeparatorCustom.Visible = txtSeparator.Visible = customSeparator;
        lblCustom.Visible = txtCustom.Visible = options.Uses(NamePartKind.CustomText);
        lblDigits.Visible = cboDigits.Visible = options.Uses(NamePartKind.Sequence);

        var preset = options.MatchingPreset();
        loading = true;
        cboPreset.SelectedIndex = preset is null
            ? cboPreset.Items.Count - 1
            : Array.IndexOf(NamingOptions.Presets, preset);
        loading = false;

        var names = FileNamer.AssignNames(options, FileNamer.Samples, _ => false);
        var now = DateTime.Now;
        for (var i = 0; i < names.Count; i++)
        {
            previewNew[i].Text = options.SubFolderFor(FileNamer.Samples[i].Info, now) + "\\" + names[i];
        }
        UpdateMoveButtons();
        ResumeLayout(true);
    }

    private void UpdateMoveButtons()
    {
        var i = lstParts.SelectedIndex;
        btnUp.Enabled = i > 0;
        btnDown.Enabled = i >= 0 && i < lstParts.Items.Count - 1;
    }

    private void MoveSelected(int delta)
    {
        var i = lstParts.SelectedIndex;
        lstParts.MoveItem(i, i + delta); // 會觸發 ItemsChanged
    }

    private bool TryValidate(out string message)
    {
        message = "";
        if (!options.Enabled)
        {
            return true;
        }
        if (!options.ActiveParts.Any())
        {
            message = "請至少勾選一個檔名組成項目，或切回「保留原檔名」。";
            return false;
        }
        if (options.Uses(NamePartKind.CustomText) && FileNamer.Sanitize(options.CustomText, "").Length == 0)
        {
            message = "勾選了「自訂文字」，請在右側輸入要加在檔名裡的文字。";
            return false;
        }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
