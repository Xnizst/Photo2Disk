namespace Photo2Disk;

/// <summary>設定複製新照片時的檔名規則。</summary>
internal sealed class NamingDialog : Form
{
    private const string CustomPresetText = "（自訂）";

    private readonly NamingOptions options;
    private readonly ToolTip tip = new() { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };

    private readonly ToggleSwitch toggle = new("保留原檔名", "依規則改名");
    private readonly ComboBox cboPreset = new();
    private readonly ReorderCheckList lstParts = new();
    private readonly Button btnUp = Ui.MakeButton("上移", false);
    private readonly Button btnDown = Ui.MakeButton("下移", false);
    private readonly ComboBox cboDate = new();
    private readonly ComboBox cboSeparator = new();
    private readonly TextBox txtCustom = new();
    private readonly ComboBox cboDigits = new();
    private readonly Label lblDate = new();
    private readonly Label lblCustom = new();
    private readonly Label lblDigits = new();
    private readonly TableLayoutPanel partsRow = new();
    private readonly Label[] previewOld = new Label[FileNamer.Samples.Length];
    private readonly Label[] previewNew = new Label[FileNamer.Samples.Length];
    private readonly Button btnOk = Ui.MakeButton("確定", true);
    private readonly Button btnCancel = Ui.MakeButton("取消", false);
    private bool loading;

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
        Text = "檔名規則";
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
        root.Controls.Add(BuildModeSection());
        root.Controls.Add(BuildPartsRow());
        root.Controls.Add(BuildPreviewSection());
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ResumeLayout(false);
        PerformLayout();

        LoadOptions();
    }

    /// <summary>按「確定」後的規則。</summary>
    public NamingOptions Result => options;

    /// <summary>介面測試用。</summary>
    internal ReorderCheckList PartsList => lstParts;

    // ================================================================ 版面

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
            + "拍攝日期：依序取自照片記錄的拍攝時間、影片記錄的建立時間、原檔名裡的日期（例如 IMG_20240315_…），" + Environment.NewLine
            + "　　　　　都沒有時用檔案修改時間，比對結果會註明。" + Environment.NewLine
            + "原檔名：來源資料裡的檔名（不含副檔名）。" + Environment.NewLine
            + "自訂文字：例如「日本旅遊」，在右側輸入。" + Environment.NewLine
            + "流水號：依拍攝時間排序編號；其他文字相同的照片各自從 1 開始（例如每天從 001 開始）。" + Environment.NewLine
            + "相機／手機型號：照片有記錄才會加，影片通常沒有。" + Environment.NewLine
            + "照片／影片：依副檔名判斷。" + Environment.NewLine
            + "所在資料夾名稱：照片在來源資料中所在的資料夾，例如 Google 相簿匯出時的相簿名稱。",
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

        void Row(Label label, string text, Control control)
        {
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 0, 8, 8);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 8);
            grid.Controls.Add(label);
            grid.Controls.Add(control);
        }

        foreach (var cbo in new[] { cboDate, cboSeparator, cboDigits })
        {
            cbo.DropDownStyle = ComboBoxStyle.DropDownList;
            cbo.SelectedIndexChanged += (_, _) => OnControlsChanged();
        }
        cboDate.Items.AddRange(NamingOptions.DateFormats.Select(f => (object)f.Label).ToArray());
        cboSeparator.Items.AddRange(NamingOptions.Separators.Select(s => (object)s.Label).ToArray());
        cboDigits.Items.AddRange(NamingOptions.SequenceDigitChoices.Select(d => (object)$"{d} 位（{1.ToString("D" + d)}）").ToArray());
        txtCustom.PlaceholderText = "例如：日本旅遊";
        txtCustom.MaxLength = 40;
        txtCustom.TextChanged += (_, _) => OnControlsChanged();

        Row(lblDate, "日期格式", cboDate);
        Row(new Label(), "分隔符號", cboSeparator);
        Row(lblCustom, "自訂文字", txtCustom);
        Row(lblDigits, "流水號位數", cboDigits);
        section.Controls.Add(grid);
        return section;
    }

    private Control BuildPreviewSection()
    {
        var section = new SectionPanel("預覽",
            "範例：2024-03-15 用 iPhone 15 Pro 拍的兩張照片，以及隔天的一段影片，" + Environment.NewLine
            + "放在來源資料的「2024 日本旅遊」資料夾。實際檔名依每張照片記錄的拍攝時間而定。",
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
            toggle.Checked = options.Enabled;
            lstParts.SetItems(options.Parts.Select(p => new ReorderCheckList.Item
            {
                Tag = p.Kind,
                Text = NamingOptions.PartLabel(p.Kind),
                Checked = p.Enabled,
            }));
            cboDate.SelectedIndex = Array.FindIndex(NamingOptions.DateFormats, f => f.Format == options.DateFormat);
            cboSeparator.SelectedIndex = Array.FindIndex(NamingOptions.Separators, s => s.Value == options.Separator);
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
        options.Enabled = toggle.Checked;
        options.Parts = lstParts.Items
            .Select(p => new NamePart { Kind = (NamePartKind)p.Tag, Enabled = p.Checked })
            .ToList();
        if (cboDate.SelectedIndex >= 0)
        {
            options.DateFormat = NamingOptions.DateFormats[cboDate.SelectedIndex].Format;
        }
        if (cboSeparator.SelectedIndex >= 0)
        {
            options.Separator = NamingOptions.Separators[cboSeparator.SelectedIndex].Value;
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
        partsRow.Visible = options.Enabled;
        lblDate.Visible = cboDate.Visible = options.Uses(NamePartKind.Date);
        lblCustom.Visible = txtCustom.Visible = options.Uses(NamePartKind.CustomText);
        lblDigits.Visible = cboDigits.Visible = options.Uses(NamePartKind.Sequence);

        var preset = options.MatchingPreset();
        loading = true;
        cboPreset.SelectedIndex = preset is null
            ? cboPreset.Items.Count - 1
            : Array.IndexOf(NamingOptions.Presets, preset);
        loading = false;

        var names = FileNamer.AssignNames(options, FileNamer.Samples, _ => false);
        for (var i = 0; i < names.Count; i++)
        {
            previewNew[i].Text = names[i];
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
