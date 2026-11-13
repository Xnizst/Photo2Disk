using System.Drawing.Drawing2D;

namespace Photo2Disk;

/// <summary>集中定義顏色與字型（2026-11-13 改用 GPT 設計稿的亮藍色系）。</summary>
internal static class Theme
{
    public static readonly Color Accent = ColorTranslator.FromHtml("#0B6BD8");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#0957B0");
    public static readonly Color AccentDisabled = ColorTranslator.FromHtml("#A9C8EE");
    public static readonly Color AccentLight = ColorTranslator.FromHtml("#DDEBFC");
    public static readonly Color AccentSoft = ColorTranslator.FromHtml("#F1F7FE");
    public static readonly Color Amber = ColorTranslator.FromHtml("#C2410C");
    public static readonly Color AmberLight = ColorTranslator.FromHtml("#FDEEDC");
    public static readonly Color Success = ColorTranslator.FromHtml("#16A34A");
    public static readonly Color SuccessLight = ColorTranslator.FromHtml("#DCF5E4");
    public static readonly Color SuccessText = ColorTranslator.FromHtml("#15803D");
    public static readonly Color Folder = ColorTranslator.FromHtml("#F5C24C");
    public static readonly Color ZipColor = ColorTranslator.FromHtml("#8B6FD6");
    public static readonly Color TextDark = ColorTranslator.FromHtml("#1F2937");
    public static readonly Color TextGray = ColorTranslator.FromHtml("#6B7280");
    public static readonly Color Border = ColorTranslator.FromHtml("#CBD3DE");
    public static readonly Color CheckBorder = ColorTranslator.FromHtml("#9AA6B6");
    public static readonly Color CardBorder = ColorTranslator.FromHtml("#E1E6EE");
    public static readonly Color GridLine = ColorTranslator.FromHtml("#E6E9EF");
    public static readonly Color Surface = ColorTranslator.FromHtml("#EEF1F6");
    public static readonly Color WindowBack = ColorTranslator.FromHtml("#EEF2F8");
    public static readonly Color TableHead = ColorTranslator.FromHtml("#F1F4F8");
    public static readonly Color RowAlt = ColorTranslator.FromHtml("#FAFBFD");
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

    public static Button MakeButton(string text, bool primary) => MakeButton(text, primary ? ButtonKind.Primary : ButtonKind.Normal);

    public static Button MakeButton(string text, ButtonKind kind)
    {
        var b = new RoundButton(kind)
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 3, 14, 3),
            MinimumSize = new Size(0, 34),
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            ForeColor = kind == ButtonKind.Primary ? Color.White : kind == ButtonKind.Normal ? Theme.TextDark : Theme.Accent,
        };
        if (kind != ButtonKind.Normal)
        {
            b.Font = new Font(Theme.FontName, 10.5f, kind == ButtonKind.Link ? FontStyle.Underline : FontStyle.Bold);
        }
        return b;
    }
}

/// <summary>圓角按鈕（YAN 2026-11-13 要求按鈕都要圓潤）。內建 Button 畫不出圓角，所以整個自己畫。</summary>
internal sealed class RoundButton : Button
{
    private const int Radius96 = 8;
    private bool hover;
    private bool pressed;

    public RoundButton(ButtonKind kind)
    {
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
    }

    public ButtonKind Kind { get; }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        if (mevent.Button == MouseButtons.Left)
        {
            pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        hover = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(Parent?.BackColor ?? Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var on = Enabled;
        Color? fill;
        Color? border;
        Color text;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = !on ? Theme.AccentDisabled : pressed || hover ? Theme.AccentHover : Theme.Accent;
                border = null;
                text = Color.White;
                break;
            case ButtonKind.Outline:
                fill = !on ? Color.White : pressed ? Theme.AccentLight : hover ? Theme.AccentSoft : Color.White;
                border = on ? Theme.Accent : Theme.AccentDisabled;
                text = on ? Theme.Accent : Theme.AccentDisabled;
                break;
            case ButtonKind.Link:
                fill = on && (hover || pressed) ? Theme.AccentLight : null;
                border = null;
                text = on ? Theme.Accent : Theme.AccentDisabled;
                break;
            default:
                fill = !on ? Theme.Surface : pressed ? Theme.AccentLight : hover ? Theme.AccentSoft : Color.White;
                border = on ? Theme.Border : Theme.GridLine;
                text = on ? ForeColor : Theme.TextGray;
                break;
        }
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Math.Min(Ui.Scaled(this, Radius96), Height / 2);
        using (var path = SectionPanel.RoundedRect(rect, radius))
        {
            if (fill is { } f)
            {
                using var brush = new SolidBrush(f);
                g.FillPath(brush, path);
            }
            if (border is { } bc)
            {
                using var pen = new Pen(bc);
                g.DrawPath(pen, path);
            }
        }
        if (Focused && ShowFocusCues && on)
        {
            var inset = Ui.Scaled(this, 3);
            using var focus = SectionPanel.RoundedRect(Rectangle.Inflate(rect, -inset, -inset), Math.Max(2, radius - inset));
            using var pen = new Pen(Kind == ButtonKind.Primary ? Color.White : Theme.Accent) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focus);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

internal enum ButtonKind
{
    Normal,
    Primary,
    Outline,
    Link,
}

/// <summary>
/// 白色圓角卡片，標題旁可放資訊圖示。主畫面用大標題（可加步驟編號、資料夾圖示、標題後的徽章），對話框用小標題。
/// 卡片本身底色是白色（子控制項才會繼承白底），圓角外側用父容器的底色補上。
/// </summary>
internal sealed class SectionPanel : Panel
{
    private readonly string title;
    private readonly InfoIcon? info;
    private readonly Font titleFont;
    private readonly bool large;
    private string? badge;

    public SectionPanel(string title, string? infoText, ToolTip tip, bool large = false)
    {
        this.title = title;
        this.large = large;
        titleFont = new Font(Theme.FontName, large ? 12.5f : 10.5f, FontStyle.Bold);
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        Padding = large ? new Padding(18, 48, 18, 14) : new Padding(14, 36, 14, 12);
        Margin = new Padding(0, 0, 0, 12);
        if (infoText is not null)
        {
            info = new InfoIcon();
            tip.SetToolTip(info, infoText);
            Controls.Add(info);
        }
    }

    /// <summary>視窗很矮時壓低主畫面卡片的標題列。</summary>
    public void SetCompact(bool compact)
    {
        if (!large)
        {
            return;
        }
        Padding = compact ? new Padding(Ui.Scaled(this, 18), Ui.Scaled(this, 38), Ui.Scaled(this, 18), Ui.Scaled(this, 8))
            : new Padding(Ui.Scaled(this, 18), Ui.Scaled(this, 48), Ui.Scaled(this, 18), Ui.Scaled(this, 14));
        PerformLayout();
        Invalidate();
    }

    /// <summary>標題前的步驟編號（主畫面的 1、2）。</summary>
    public string? Step { get; set; }

    /// <summary>標題前畫資料夾圖示。</summary>
    public bool FolderGlyph { get; set; }

    /// <summary>標題後的灰底小徽章，例如「2 個項目」。</summary>
    public string? Badge
    {
        get => badge;
        set
        {
            badge = value;
            Invalidate();
        }
    }

    private int TitleLeft => Padding.Left + (FolderGlyph ? Ui.Scaled(this, 32) : 0);

    private string FullTitle => Step is null ? title : Step + "  " + title;

    private int TitleBandHeight => Padding.Top - (large ? Ui.Scaled(this, 6) : 0);

    private Control? headerAction;

    /// <summary>放在標題列右側的按鈕（例如來源資料的「＋ 加入」）。</summary>
    public Control? HeaderAction
    {
        get => headerAction;
        set
        {
            if (headerAction is not null)
            {
                Controls.Remove(headerAction);
            }
            headerAction = value;
            if (value is not null)
            {
                Controls.Add(value);
            }
            PerformLayout();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (info is not null)
        {
            var w = TextRenderer.MeasureText(FullTitle, titleFont).Width;
            info.Location = new Point(TitleLeft + w + Ui.Scaled(this, 2), (TitleBandHeight - info.Height) / 2 + Ui.Scaled(this, 2));
        }
        if (headerAction is not null)
        {
            headerAction.Location = new Point(Width - Padding.Right - headerAction.Width,
                (TitleBandHeight - headerAction.Height) / 2 + Ui.Scaled(this, 2));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundedRect(r, Ui.Scaled(this, large ? 10 : 8)))
        {
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
            using var pen = new Pen(large ? Theme.CardBorder : Theme.Border);
            g.DrawPath(pen, path);
        }
        var band = TitleBandHeight;
        if (FolderGlyph)
        {
            var fh = Ui.Scaled(this, 18);
            Glyphs.Folder(g, new Rectangle(Padding.Left, (band - fh) / 2 + Ui.Scaled(this, 2), Ui.Scaled(this, 22), fh), Theme.Accent, false);
        }
        var titleRect = new Rectangle(TitleLeft, Ui.Scaled(this, 2), Width - TitleLeft - Padding.Right, band);
        TextRenderer.DrawText(g, FullTitle, titleFont, titleRect, large ? Theme.TextDark : Theme.Accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(badge))
        {
            var tw = TextRenderer.MeasureText(FullTitle, titleFont).Width;
            var x = TitleLeft + tw + Ui.Scaled(this, 10) + (info is null ? 0 : info.Width + Ui.Scaled(this, 4));
            var size = TextRenderer.MeasureText(badge, Font);
            var br = new Rectangle(x, Ui.Scaled(this, 2) + (band - size.Height - Ui.Scaled(this, 8)) / 2,
                size.Width + Ui.Scaled(this, 16), size.Height + Ui.Scaled(this, 8));
            using var bpath = RoundedRect(br, br.Height / 2);
            using var b = new SolidBrush(Theme.Surface);
            g.FillPath(b, bpath);
            TextRenderer.DrawText(g, badge, Font, br, Theme.TextGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
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
    public Color OffColor { get; set; } = Theme.TextGray;
    public Color OnColor { get; set; } = Theme.Accent;

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
