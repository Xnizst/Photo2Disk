using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Photo2Disk;

/// <summary>
/// 更新視窗（YAN 2026-11-13：更新進度要獨立一個視窗，後方主畫面霧化）。
/// 三個階段：確認（版本、大小、更新內容）→ 下載進度 → 完成或錯誤。
/// </summary>
internal sealed class UpdateDialog : Form
{
    private readonly UpdateInfo info;
    private readonly Label lblTitle = new();
    private readonly Label lblSub = new();
    private readonly TextBox txtNotes = new();
    private readonly Panel notesHost = new();
    private readonly RoundProgress bar = new();
    private readonly Label lblProgress = new();
    private readonly Label lblHint = new();
    private readonly Button btnPrimary = Ui.MakeButton("開始更新", ButtonKind.Primary);
    private readonly Button btnSecondary = Ui.MakeButton("以後再說", false);
    private readonly Stopwatch watch = new();
    private TaskCompletionSource<bool>? confirmTcs;
    private readonly TaskCompletionSource closedTcs = new();
    private Phase phase = Phase.Confirm;

    private enum Phase
    {
        Confirm,
        Downloading,
        Done,
        Error,
    }

    public UpdateDialog(UpdateInfo info, string currentVersion)
    {
        this.info = info;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(Theme.FontName, 10f);
        ForeColor = Theme.TextDark;
        BackColor = Color.White;
        Text = "更新照片備份過濾工具";
        Icon = Ui.AppIcon ?? Icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // 視窗高度跟著內容（確認階段有更新內容，下載階段沒有），寬度固定
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 18);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(432, 0),
            MaximumSize = new Size(432, 0),
            ColumnCount = 2,
            RowCount = 6,
            Location = new Point(24, 20),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var logo = new PictureBox
        {
            Size = new Size(44, 44),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 14, 0),
        };
        if (Ui.AppIcon is { } icon)
        {
            using var big = new Icon(icon, 128, 128);
            logo.Image = big.ToBitmap();
        }
        var head = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        lblTitle.AutoSize = true;
        lblTitle.Font = new Font(Theme.FontName, 14f, FontStyle.Bold);
        lblTitle.Margin = new Padding(0);
        lblTitle.Text = $"有新版本 v{info.Version.ToString(3)}";
        lblSub.AutoSize = true;
        lblSub.ForeColor = Theme.TextGray;
        lblSub.Margin = new Padding(1, 2, 0, 0);
        lblSub.Text = $"目前 v{currentVersion}，下載約 {DedupEngine.FormatSize(info.Size)}";
        head.Controls.Add(lblTitle);
        head.Controls.Add(lblSub);

        // 更新內容（確認階段）
        notesHost.Dock = DockStyle.Fill;
        notesHost.Height = 130;
        notesHost.Margin = new Padding(0, 14, 0, 0);
        notesHost.Padding = new Padding(12, 10, 8, 10);
        notesHost.BackColor = Theme.AccentSoft;
        txtNotes.Dock = DockStyle.Fill;
        txtNotes.Multiline = true;
        txtNotes.ReadOnly = true;
        txtNotes.BorderStyle = BorderStyle.None;
        txtNotes.BackColor = Theme.AccentSoft;
        txtNotes.ScrollBars = ScrollBars.Vertical;
        txtNotes.TabStop = false;
        var notes = info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        txtNotes.Text = "更新內容：" + Environment.NewLine + (notes.Length > 0 ? notes : "（這一版沒有附說明）");
        notesHost.Controls.Add(txtNotes);

        // 下載進度（下載階段）
        bar.Dock = DockStyle.Fill;
        bar.Height = 12;
        bar.Margin = new Padding(0, 18, 0, 0);
        bar.Visible = false;
        lblProgress.AutoSize = true;
        lblProgress.Margin = new Padding(0, 10, 0, 0);
        lblProgress.Visible = false;
        lblHint.AutoSize = true;
        lblHint.ForeColor = Theme.TextGray;
        lblHint.Margin = new Padding(0, 6, 0, 0);
        lblHint.Text = "完成後會自動重新開啟，備份資料夾、檔名規則等設定都會保留。";
        lblHint.MaximumSize = new Size(430, 0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 16, 0, 0),
            WrapContents = false,
        };
        foreach (var b in new[] { btnPrimary, btnSecondary })
        {
            b.AutoSize = false;
            b.Size = new Size(120, 38);
        }
        btnPrimary.Margin = new Padding(0);
        btnSecondary.Margin = new Padding(0, 0, 10, 0);
        buttons.Controls.Add(btnPrimary);
        buttons.Controls.Add(btnSecondary);
        btnPrimary.Click += (_, _) => OnPrimary();
        btnSecondary.Click += (_, _) => OnSecondary();

        layout.Controls.Add(logo, 0, 0);
        layout.Controls.Add(head, 1, 0);
        layout.Controls.Add(notesHost, 0, 1);
        layout.SetColumnSpan(notesHost, 2);
        layout.Controls.Add(bar, 0, 2);
        layout.SetColumnSpan(bar, 2);
        layout.Controls.Add(lblProgress, 0, 3);
        layout.SetColumnSpan(lblProgress, 2);
        layout.Controls.Add(lblHint, 0, 4);
        layout.SetColumnSpan(lblHint, 2);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = btnPrimary;
    }

    /// <summary>使用者在下載中按「取消」或關閉視窗。</summary>
    public event EventHandler? CancelRequested;

    public Task WhenClosed => closedTcs.Task;

    /// <summary>置中顯示在主畫面上（非強制回應視窗，主畫面由霧化層擋住操作）。</summary>
    public void ShowOver(Form owner)
    {
        Owner = owner;
        CenterOnOwner();
        Show(owner);
        Activate();
    }

    private void CenterOnOwner()
    {
        if (Owner is { } o)
        {
            var b = o.Bounds;
            Location = new Point(b.Left + (b.Width - Width) / 2, b.Top + (b.Height - Height) / 2);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        CenterOnOwner(); // 換階段時高度會變，保持置中
    }

    public Task<bool> WaitForConfirmAsync()
    {
        confirmTcs = new TaskCompletionSource<bool>();
        btnPrimary.Focus();
        return confirmTcs.Task;
    }

    public void StartProgress()
    {
        phase = Phase.Downloading;
        lblTitle.Text = $"正在更新到 v{info.Version.ToString(3)}";
        notesHost.Visible = false;
        bar.Visible = true;
        lblProgress.Visible = true;
        btnPrimary.Visible = false;
        btnSecondary.Text = "取消";
        AcceptButton = null;
        CancelButton = btnSecondary;
        watch.Restart();
        SetProgress(0);
    }

    public void SetProgress(double fraction)
    {
        if (phase != Phase.Downloading)
        {
            return;
        }
        fraction = Math.Clamp(fraction, 0, 1);
        bar.Value = fraction;
        var done = (long)(info.Size * fraction);
        var text = $"已下載 {DedupEngine.FormatSize(done)} / {DedupEngine.FormatSize(info.Size)}（{fraction:P0}）";
        var elapsed = watch.Elapsed.TotalSeconds;
        if (elapsed > 2 && fraction is > 0.02 and < 1)
        {
            var remain = elapsed / fraction * (1 - fraction);
            text += remain < 60 ? $"，剩約 {Math.Ceiling(remain):0} 秒" : $"，剩約 {Math.Ceiling(remain / 60):0} 分鐘";
        }
        lblProgress.Text = text;
    }

    public void ShowDone()
    {
        phase = Phase.Done;
        bar.Value = 1;
        lblTitle.Text = $"已更新到 v{info.Version.ToString(3)}";
        lblProgress.Text = "下載完成，正在重新開啟程式…";
        btnSecondary.Visible = false;
    }

    public void ShowError(string message)
    {
        phase = Phase.Error;
        lblTitle.Text = "更新沒有完成";
        lblSub.Text = "目前版本不受影響，可以稍後再試。";
        bar.Visible = false;
        lblProgress.Visible = true;
        lblProgress.ForeColor = Theme.Amber;
        lblProgress.MaximumSize = new Size(430, 0);
        lblProgress.Text = message;
        lblHint.Visible = false;
        btnSecondary.Visible = false;
        btnPrimary.Visible = true;
        btnPrimary.Text = "關閉";
        AcceptButton = btnPrimary;
        btnPrimary.Focus();
    }

    private void OnPrimary()
    {
        if (phase == Phase.Confirm)
        {
            confirmTcs?.TrySetResult(true);
        }
        else if (phase == Phase.Error)
        {
            Close();
        }
    }

    private void OnSecondary()
    {
        if (phase == Phase.Confirm)
        {
            confirmTcs?.TrySetResult(false);
        }
        else if (phase == Phase.Downloading)
        {
            btnSecondary.Enabled = false;
            lblProgress.Text = "正在取消…";
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 下載中按右上角 × 等同取消，等下載停下來再由主畫面關閉視窗
        if (phase == Phase.Downloading && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            OnSecondary();
            return;
        }
        if (phase == Phase.Confirm)
        {
            confirmTcs?.TrySetResult(false);
        }
        base.OnFormClosing(e);
    }

    /// <summary>主畫面在下載結束後關閉視窗用（不視為使用者取消）。</summary>
    public void CloseQuietly()
    {
        phase = Phase.Done;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        closedTcs.TrySetResult();
    }

    internal string ProgressText => lblProgress.Text;
}

/// <summary>圓角進度條。</summary>
internal sealed class RoundProgress : Control
{
    private double value;

    public RoundProgress()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    public double Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = SectionPanel.RoundedRect(r, Math.Max(1, r.Height / 2)))
        using (var track = new SolidBrush(Theme.Surface))
        {
            g.FillPath(track, path);
        }
        var w = (int)Math.Round(r.Width * value);
        if (w >= r.Height)
        {
            using var path = SectionPanel.RoundedRect(new Rectangle(0, 0, w, r.Height), Math.Max(1, r.Height / 2));
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
        }
        else if (w > 0)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillEllipse(fill, 0, 0, r.Height, r.Height);
        }
    }
}

/// <summary>主畫面霧化層：截下目前畫面、模糊並蒙上一層淺色，蓋在所有控制項上面並擋住操作。</summary>
internal sealed class FrostLayer : IDisposable
{
    private readonly Form form;
    private readonly PictureBox overlay;
    private readonly Bitmap image;

    public FrostLayer(Form form)
    {
        this.form = form;
        var size = form.ClientSize;
        using (var shot = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height)))
        {
            using (var g = Graphics.FromImage(shot))
            {
                g.CopyFromScreen(form.PointToScreen(Point.Empty), Point.Empty, size);
            }
            image = Blur(shot);
        }
        overlay = new PictureBox
        {
            Image = image,
            SizeMode = PictureBoxSizeMode.Normal,
            Bounds = form.ClientRectangle,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = Theme.WindowBack,
            Cursor = Cursors.No,
        };
        form.Controls.Add(overlay);
        overlay.BringToFront();
        form.Update();
    }

    /// <summary>縮小再放大做模糊（分兩次縮小避免鋸齒），再蓋一層半透明的淺藍白色。</summary>
    internal static Bitmap Blur(Bitmap src)
    {
        Bitmap Resize(Bitmap b, int w, int h)
        {
            var r = new Bitmap(Math.Max(1, w), Math.Max(1, h));
            using var g = Graphics.FromImage(r);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attr = new ImageAttributes();
            attr.SetWrapMode(WrapMode.TileFlipXY); // 邊緣不要淡出成透明
            g.DrawImage(b, new Rectangle(0, 0, r.Width, r.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, attr);
            return r;
        }
        using var half = Resize(src, src.Width / 4, src.Height / 4);
        using var tiny = Resize(half, src.Width / 16, src.Height / 16);
        var result = Resize(tiny, src.Width, src.Height);
        using (var g = Graphics.FromImage(result))
        using (var veil = new SolidBrush(Color.FromArgb(120, 238, 242, 248)))
        {
            g.FillRectangle(veil, 0, 0, result.Width, result.Height);
        }
        return result;
    }

    public void Dispose()
    {
        if (!form.IsDisposed)
        {
            form.Controls.Remove(overlay);
        }
        overlay.Dispose();
        image.Dispose();
    }
}
