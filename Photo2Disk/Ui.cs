using System.Drawing.Drawing2D;

namespace Photo2Disk;

/// <summary>集中定義顏色與字型。</summary>
internal static class Theme
{
    public static readonly Color Primary = ColorTranslator.FromHtml("#3B5B8C");
    public static readonly Color Teal = ColorTranslator.FromHtml("#0F766E");
    public static readonly Color TealHover = ColorTranslator.FromHtml("#0B5F58");
    public static readonly Color TealDisabled = ColorTranslator.FromHtml("#A7C7C3");
    public static readonly Color Amber = ColorTranslator.FromHtml("#D97706");
    public static readonly Color TextDark = ColorTranslator.FromHtml("#1F2937");
    public static readonly Color TextGray = ColorTranslator.FromHtml("#6B7280");
    public static readonly Color Border = ColorTranslator.FromHtml("#D1D5DB");
    public static readonly Color GridLine = ColorTranslator.FromHtml("#E5E7EB");
    public static readonly Color Surface = ColorTranslator.FromHtml("#F3F4F6");
    public static readonly Color RowAlt = ColorTranslator.FromHtml("#F9FAFB");
    public static readonly Color HeaderSub = ColorTranslator.FromHtml("#DCE4F0");
    public static readonly Color UpdateBack = ColorTranslator.FromHtml("#E6F2F1");
    public const string FontName = "Microsoft JhengHei UI";
}

internal static class Ui
{
    private static Icon? appIcon;

    /// <summary>程式圖示（內嵌資源 app.ico，由 tools/make_icon.py 產生）。</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (appIcon is null)
            {
                using var s = typeof(Ui).Assembly.GetManifestResourceStream("Photo2Disk.app.ico");
                appIcon = s is null ? null : new Icon(s);
            }
            return appIcon;
        }
    }

    /// <summary>自繪程式碼用：96 DPI 的像素數換算成目前 DPI。</summary>
    public static int Scaled(Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0);

    public static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(12, 3, 12, 3),
            MinimumSize = new Size(0, 34),
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        if (primary)
        {
            b.BackColor = Theme.Teal;
            b.ForeColor = Color.White;
            b.Font = new Font(Theme.FontName, 10f, FontStyle.Bold);
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.TealHover;
            b.FlatAppearance.MouseDownBackColor = Theme.TealHover;
            b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? Theme.Teal : Theme.TealDisabled;
        }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = Theme.TextDark;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Theme.Surface;
            b.FlatAppearance.MouseDownBackColor = Theme.Border;
            b.EnabledChanged += (_, _) =>
            {
                b.BackColor = b.Enabled ? Color.White : Theme.Surface;
                b.FlatAppearance.BorderColor = b.Enabled ? Theme.Border : Theme.GridLine;
            };
        }
        return b;
    }
}

/// <summary>有邊框與標題的區塊，標題旁可放資訊圖示。</summary>
internal sealed class SectionPanel : Panel
{
    private readonly string title;
    private readonly InfoIcon? info;
    private readonly Font titleFont = new(Theme.FontName, 10.5f, FontStyle.Bold);

    public SectionPanel(string title, string? infoText, ToolTip tip)
    {
        this.title = title;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        Padding = new Padding(14, 36, 14, 12);
        Margin = new Padding(0, 0, 0, 12);
        if (infoText is not null)
        {
            info = new InfoIcon();
            tip.SetToolTip(info, infoText);
            Controls.Add(info);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (info is not null)
        {
            var w = TextRenderer.MeasureText(title, titleFont).Width;
            info.Location = new Point(
                Padding.Left + w + Ui.Scaled(this, 2),
                (Padding.Top - info.Height) / 2 + Ui.Scaled(this, 2));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundedRect(r, Ui.Scaled(this, 8)))
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawPath(pen, path);
        }
        var titleRect = new Rectangle(Padding.Left, Ui.Scaled(this, 2), Width - Padding.Horizontal, Padding.Top);
        TextRenderer.DrawText(g, title, titleFont, titleRect, Theme.Primary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
        }
        base.Dispose(disposing);
    }

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>圓形「i」圖示，滑鼠移上去顯示說明。</summary>
internal sealed class InfoIcon : Control
{
    public InfoIcon()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(18, 18);
        Cursor = Cursors.Help;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var pen = new Pen(Theme.TextGray, Math.Max(1f, DeviceDpi / 96f)))
        {
            g.DrawEllipse(pen, r);
        }
        using var font = new Font("Arial", 8f, FontStyle.Bold);
        TextRenderer.DrawText(g, "i", font, new Rectangle(0, 0, Width, Height), Theme.TextGray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>撥桿開關：兩側都有文字，選中的一側粗體並用該側顏色，圓鈕滑動切換。</summary>
internal sealed class ToggleSwitch : Control
{
    private const int TrackW = 40;
    private const int TrackH = 20;
    private const int Gap = 8;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private bool isOn;
    private float knob;
    private Font boldFont;

    public ToggleSwitch(string offText, string onText)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        OffText = offText;
        OnText = onText;
        boldFont = new Font(Font, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = true;
        AutoSize = true;
        timer.Tick += (_, _) =>
        {
            var target = isOn ? 1f : 0f;
            knob += Math.Sign(target - knob) * 0.2f;
            if (Math.Abs(target - knob) < 0.2f)
            {
                knob = target;
                timer.Stop();
            }
            Invalidate();
        };
    }

    public string OffText { get; }
    public string OnText { get; }
    public Color OffColor { get; set; } = Theme.Primary;
    public Color OnColor { get; set; } = Theme.Teal;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => isOn;
        set
        {
            if (isOn == value)
            {
                return;
            }
            isOn = value;
            if (IsHandleCreated && Visible)
            {
                timer.Start();
            }
            else
            {
                knob = value ? 1 : 0;
            }
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int S(int px) => Ui.Scaled(this, px);

    private int LabelWidth(string text) => TextRenderer.MeasureText(text, boldFont).Width;

    private Rectangle TrackRect()
    {
        var x = LabelWidth(OffText) + S(Gap);
        return new Rectangle(x, (Height - S(TrackH)) / 2, S(TrackW), S(TrackH));
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textH = TextRenderer.MeasureText("撥", boldFont).Height;
        return new Size(LabelWidth(OffText) + S(Gap) * 2 + S(TrackW) + LabelWidth(OnText) + S(2),
            Math.Max(textH, S(TrackH)) + S(6));
    }

    protected override void OnFontChanged(EventArgs e)
    {
        boldFont.Dispose();
        boldFont = new Font(Font, FontStyle.Bold);
        base.OnFontChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var track = TrackRect();
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, OffText, isOn ? Font : boldFont,
            new Rectangle(0, 0, track.Left - S(Gap) + S(2), Height), isOn ? Theme.TextGray : OffColor, flags | TextFormatFlags.Right);
        TextRenderer.DrawText(g, OnText, isOn ? boldFont : Font,
            new Rectangle(track.Right + S(Gap), 0, Width - track.Right - S(Gap), Height), isOn ? OnColor : Theme.TextGray, flags);

        var color = Enabled ? (isOn ? OnColor : OffColor) : Theme.Border;
        using (var path = SectionPanel.RoundedRect(track, track.Height / 2))
        using (var brush = new SolidBrush(color))
        {
            g.FillPath(brush, path);
        }
        var d = track.Height - S(4);
        var kx = track.Left + S(2) + (int)Math.Round(knob * (track.Width - d - S(4)));
        using (var white = new SolidBrush(Color.White))
        {
            g.FillEllipse(white, kx, track.Top + S(2), d, d);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Border) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        var track = TrackRect();
        if (e.X < track.Left)
        {
            Checked = false;
        }
        else if (e.X > track.Right)
        {
            Checked = true;
        }
        else
        {
            Checked = !Checked;
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
        }
        else if (e.KeyCode == Keys.Left)
        {
            Checked = false;
        }
        else if (e.KeyCode == Keys.Right)
        {
            Checked = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            boldFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
