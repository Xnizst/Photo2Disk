using System.Drawing.Drawing2D;

namespace Photo2Disk;

/// <summary>膠囊形標籤（例如標題右上角的「照片只在本機處理」）。</summary>
internal sealed class PillLabel : Control
{
    public PillLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = true;
        PillColor = Theme.AccentLight;
        ForeColor = Theme.Accent;
    }

    public Color PillColor { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + Ui.Scaled(this, 28), t.Height + Ui.Scaled(this, 12));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (AutoSize)
        {
            Size = GetPreferredSize(Size.Empty);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = SectionPanel.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2))
        using (var brush = new SolidBrush(PillColor))
        {
            g.FillPath(brush, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>需匯入的來源資料清單：一列一個資料夾或壓縮檔，右側 × 移除，滑鼠移上去顯示完整路徑。</summary>
internal sealed class SourceList : Control
{
    private const int RowH96 = 32;
    private const int Gap96 = 6;

    private readonly List<string> paths = new();
    private readonly VScrollBar scroll = new() { Visible = false };
    private readonly ToolTip tip;
    private int hover = -1;
    private bool hoverClose;
    private string? tipPath;

    public SourceList(ToolTip tip)
    {
        this.tip = tip;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Controls.Add(scroll);
        scroll.Scroll += (_, _) => Invalidate();
        scroll.ValueChanged += (_, _) => Invalidate();
    }

    /// <summary>使用者按了某一列的 ×。</summary>
    public event EventHandler<string>? RemoveRequested;

    public IReadOnlyList<string> Paths => paths;

    public string EmptyText { get; set; } = "";

    private int Row => Ui.Scaled(this, RowH96);

    private int Gap => Ui.Scaled(this, Gap96);

    private int ContentHeight => paths.Count == 0 ? 0 : paths.Count * (Row + Gap) - Gap;

    public void SetPaths(IEnumerable<string> items)
    {
        paths.Clear();
        paths.AddRange(items);
        hover = -1;
        UpdateScroll();
        Invalidate();
    }

    private void UpdateScroll()
    {
        var overflow = ContentHeight - Height;
        scroll.Visible = overflow > 0;
        if (scroll.Visible)
        {
            scroll.SetBounds(Width - scroll.Width, 0, scroll.Width, Height);
            scroll.Minimum = 0;
            scroll.SmallChange = Row + Gap;
            scroll.LargeChange = Math.Max(1, Height);
            scroll.Maximum = ContentHeight;
            scroll.Value = Math.Min(scroll.Value, Math.Max(0, ContentHeight - Height + 1));
        }
        else
        {
            scroll.Value = 0;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (scroll.Visible)
        {
            var max = Math.Max(0, scroll.Maximum - scroll.LargeChange + 1);
            scroll.Value = Math.Clamp(scroll.Value - Math.Sign(e.Delta) * (Row + Gap), 0, max);
        }
    }

    private int RowWidth => Width - (scroll.Visible ? scroll.Width + Ui.Scaled(this, 4) : 0);

    private Rectangle RowRect(int i) => new(0, i * (Row + Gap) - scroll.Value, RowWidth - 1, Row);

    private Rectangle CloseRect(int i)
    {
        var r = RowRect(i);
        var s = Ui.Scaled(this, 22);
        return new Rectangle(r.Right - s - Ui.Scaled(this, 6), r.Top + (Row - s) / 2, s, s);
    }

    private int IndexAt(Point p)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            if (RowRect(i).Contains(p))
            {
                return i;
            }
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexAt(e.Location);
        var onClose = i >= 0 && CloseRect(i).Contains(e.Location);
        if (i != hover || onClose != hoverClose)
        {
            hover = i;
            hoverClose = onClose;
            Cursor = onClose ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        var path = i >= 0 ? (onClose ? "從清單移除" : paths[i]) : null;
        if (path != tipPath)
        {
            tipPath = path;
            tip.SetToolTip(this, path);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        hoverClose = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var i = IndexAt(e.Location);
        if (Enabled && e.Button == MouseButtons.Left && i >= 0 && CloseRect(i).Contains(e.Location))
        {
            RemoveRequested?.Invoke(this, paths[i]);
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (paths.Count == 0)
        {
            using var pen = new Pen(Theme.Border) { DashStyle = DashStyle.Dash };
            using (var path = SectionPanel.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Scaled(this, 6)))
            {
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, Theme.TextGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        for (var i = 0; i < paths.Count; i++)
        {
            var r = RowRect(i);
            if (r.Bottom < 0 || r.Top > Height)
            {
                continue;
            }
            using (var path = SectionPanel.RoundedRect(r, Ui.Scaled(this, 6)))
            {
                using var fill = new SolidBrush(i == hover && Enabled ? Theme.AccentSoft : Color.White);
                g.FillPath(fill, path);
                using var pen = new Pen(Theme.CardBorder);
                g.DrawPath(pen, path);
            }
            var isZip = File.Exists(paths[i]);
            var icon = new Rectangle(r.Left + Ui.Scaled(this, 12), r.Top + (Row - Ui.Scaled(this, 16)) / 2,
                Ui.Scaled(this, 20), Ui.Scaled(this, 16));
            if (isZip)
            {
                Glyphs.Zip(g, icon, this);
            }
            else
            {
                Glyphs.Folder(g, icon, Theme.Folder, true);
            }
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[i]));
            if (string.IsNullOrEmpty(name))
            {
                name = paths[i];
            }
            var close = CloseRect(i);
            var textRect = new Rectangle(icon.Right + Ui.Scaled(this, 10), r.Top, close.Left - icon.Right - Ui.Scaled(this, 14), Row);
            TextRenderer.DrawText(g, name, Font, textRect, Enabled ? Theme.TextDark : Theme.TextGray,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (i == hover && hoverClose && Enabled)
            {
                using var hb = new SolidBrush(Theme.GridLine);
                g.FillEllipse(hb, close);
            }
            var c = Ui.Scaled(this, 5);
            var cx = close.Left + close.Width / 2f;
            var cy = close.Top + close.Height / 2f;
            using var xp = new Pen(Theme.TextGray, Math.Max(1.5f, Ui.Scaled(this, 2) * 0.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(xp, cx - c, cy - c, cx + c, cy + c);
            g.DrawLine(xp, cx - c, cy + c, cx + c, cy - c);
        }
    }

    internal int RowPixels => Row;
}

/// <summary>比對結果上方的統計卡：大數字＋說明，可點擊切換分頁。</summary>
internal sealed class StatTile : Control
{
    private bool hover;

    public StatTile()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public string Number { get; set; } = "－";
    public string Caption { get; set; } = "";
    public string Detail { get; set; } = "";
    public Color FillColor { get; set; } = Theme.Surface;
    public Color NumberColor { get; set; } = Theme.TextDark;

    public void SetValues(string number, string detail)
    {
        Number = number;
        Detail = detail;
        Invalidate();
    }

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
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = SectionPanel.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Scaled(this, 8)))
        using (var brush = new SolidBrush(FillColor))
        {
            g.FillPath(brush, path);
            if (hover && Enabled)
            {
                using var pen = new Pen(NumberColor);
                g.DrawPath(pen, path);
            }
        }
        using var numFont = new Font(Theme.FontName, 18f, FontStyle.Bold);
        var x = Ui.Scaled(this, 16);
        var numSize = TextRenderer.MeasureText(g, Number, numFont, Size.Empty, TextFormatFlags.NoPadding);
        var detailH = Detail.Length > 0 ? TextRenderer.MeasureText("字", SmallFont).Height : 0;
        var top = (Height - numSize.Height - detailH) / 2;
        TextRenderer.DrawText(g, Number, numFont, new Point(x, top), NumberColor, TextFormatFlags.NoPadding);
        var capRect = new Rectangle(x + numSize.Width + Ui.Scaled(this, 8), top, Width - x - numSize.Width - Ui.Scaled(this, 12), numSize.Height);
        TextRenderer.DrawText(g, Caption, Font, capRect, Theme.TextDark,
            TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (Detail.Length > 0)
        {
            TextRenderer.DrawText(g, Detail, SmallFont, new Rectangle(x, top + numSize.Height + Ui.Scaled(this, 2), Width - x - Ui.Scaled(this, 8), detailH),
                Theme.TextGray, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    private static readonly Font SmallFont = new(Theme.FontName, 9f);
}

/// <summary>分頁標籤：選中的一頁用主色粗體並畫底線。</summary>
internal sealed class TabStrip : Control
{
    private readonly List<(string Text, bool Visible)> tabs = new();
    private int selected;
    private int hover = -1;
    private Font boldFont;

    public TabStrip()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        boldFont = new Font(Font, FontStyle.Bold);
    }

    public event EventHandler? SelectedIndexChanged;

    public void SetTabs(IEnumerable<string> texts)
    {
        tabs.Clear();
        tabs.AddRange(texts.Select(t => (t, true)));
        Invalidate();
    }

    public void SetTab(int index, string text, bool visible)
    {
        tabs[index] = (text, visible);
        if (!visible && selected == index)
        {
            SelectedIndex = 0;
        }
        Invalidate();
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (value < 0 || value >= tabs.Count || value == selected)
            {
                return;
            }
            selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        boldFont.Dispose();
        boldFont = new Font(Font, FontStyle.Bold);
        base.OnFontChanged(e);
    }

    private IEnumerable<(int Index, Rectangle Rect)> TabRects()
    {
        var x = 0;
        var visible = tabs.Where(t => t.Visible).ToList();
        var textW = visible.Sum(t => TextRenderer.MeasureText(t.Text, boldFont).Width);
        // 放不下時縮小左右間距（最少 4），避免最後一頁被截掉
        var pad = visible.Count == 0 ? 0
            : Math.Clamp((Width - textW) / (visible.Count * 2), Ui.Scaled(this, 4), Ui.Scaled(this, 14));
        for (var i = 0; i < tabs.Count; i++)
        {
            if (!tabs[i].Visible)
            {
                continue;
            }
            var w = TextRenderer.MeasureText(tabs[i].Text, boldFont).Width + pad * 2;
            yield return (i, new Rectangle(x, 0, w, Height));
            x += w;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = TabRects().Where(t => t.Rect.Contains(e.Location)).Select(t => t.Index).DefaultIfEmpty(-1).First();
        if (i != hover)
        {
            hover = i;
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!Enabled)
        {
            return;
        }
        foreach (var (i, r) in TabRects())
        {
            if (r.Contains(e.Location))
            {
                SelectedIndex = i;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var line = new Pen(Theme.GridLine))
        {
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
        foreach (var (i, r) in TabRects())
        {
            var isSel = i == selected;
            var color = !Enabled ? Theme.TextGray : isSel ? Theme.Accent : i == hover ? Theme.TextDark : Theme.TextGray;
            TextRenderer.DrawText(g, tabs[i].Text, isSel ? boldFont : Font, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (isSel && Enabled)
            {
                using var b = new SolidBrush(Theme.Accent);
                var h = Ui.Scaled(this, 3);
                g.FillRectangle(b, r.Left + Ui.Scaled(this, 6), Height - h, r.Width - Ui.Scaled(this, 12), h);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            boldFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>圓角外框的輸入欄。搜尋用時左側有放大鏡；也可包住既有的 TextBox（例如唯讀的路徑欄）。</summary>
internal sealed class SearchBox : Panel
{
    private readonly TextBox box;
    private readonly bool magnifier;

    public SearchBox(string placeholder) : this(new TextBox { PlaceholderText = placeholder }, true)
    {
    }

    public SearchBox(TextBox inner, bool magnifier)
    {
        box = inner;
        this.magnifier = magnifier;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Color.White;
        box.Margin = new Padding(0);
        box.Dock = DockStyle.None;
        box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Controls.Add(box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => box.Focus();
    }

    public TextBox Inner => box;

    public override string Text
    {
        get => box.Text;
#pragma warning disable CS8765
        set => box.Text = value;
#pragma warning restore CS8765
    }

    private int TextLeft => Ui.Scaled(this, magnifier ? 32 : 10);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        box.SetBounds(TextLeft, (Height - box.PreferredHeight) / 2 + 1, Width - TextLeft - Ui.Scaled(this, 8), box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.White);
        using (var path = SectionPanel.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Scaled(this, 6)))
        {
            using var fill = new SolidBrush(box.BackColor);
            g.FillPath(fill, path);
            using var pen = new Pen(box.Focused && !box.ReadOnly ? Theme.Accent : Theme.Border);
            g.DrawPath(pen, path);
        }
        if (!magnifier)
        {
            return;
        }
        var r = Ui.Scaled(this, 6);
        var cx = Ui.Scaled(this, 15);
        var cy = Height / 2 - Ui.Scaled(this, 1);
        using var p = new Pen(Theme.TextGray, Math.Max(1.5f, Ui.Scaled(this, 2) * 0.8f)) { EndCap = LineCap.Round };
        g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        g.DrawLine(p, cx + r * 0.7f, cy + r * 0.7f, cx + r * 1.5f, cy + r * 1.5f);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.GotFocus += (_, _) => Invalidate();
        e.Control.LostFocus += (_, _) => Invalidate();
    }
}

/// <summary>簡單的向量小圖示。</summary>
internal static class Glyphs
{
    /// <summary>資料夾：filled=true 為實心（清單用），false 為外框（卡片標題用）。</summary>
    public static void Folder(Graphics g, Rectangle r, Color color, bool filled)
    {
        var tabW = r.Width * 0.42f;
        var tabH = r.Height * 0.2f;
        using var path = new GraphicsPath();
        path.AddLine(r.Left, r.Top + tabH, r.Left, r.Bottom);
        path.AddLine(r.Left, r.Bottom, r.Right, r.Bottom);
        path.AddLine(r.Right, r.Bottom, r.Right, r.Top + tabH * 1.6f);
        path.AddLine(r.Right, r.Top + tabH * 1.6f, r.Left + tabW + tabH, r.Top + tabH * 1.6f);
        path.AddLine(r.Left + tabW + tabH, r.Top + tabH * 1.6f, r.Left + tabW, r.Top);
        path.AddLine(r.Left + tabW, r.Top, r.Left, r.Top);
        path.CloseFigure();
        if (filled)
        {
            using var b = new SolidBrush(color);
            g.FillPath(b, path);
        }
        else
        {
            using var p = new Pen(color, Math.Max(1.5f, r.Height / 9f)) { LineJoin = LineJoin.Round };
            g.DrawPath(p, path);
        }
    }

    public static void Zip(Graphics g, Rectangle r, Control c)
    {
        var body = new Rectangle(r.Left + r.Width / 8, r.Top - 1, r.Width * 3 / 4, r.Height + 2);
        using (var b = new SolidBrush(Theme.ZipColor))
        using (var path = SectionPanel.RoundedRect(body, Ui.Scaled(c, 2)))
        {
            g.FillPath(b, path);
        }
        // 中間的拉鍊
        using var w = new SolidBrush(Color.White);
        var zx = body.Left + body.Width / 2 - Ui.Scaled(c, 2);
        for (var y = body.Top + Ui.Scaled(c, 2); y < body.Bottom - Ui.Scaled(c, 5); y += Ui.Scaled(c, 4))
        {
            g.FillRectangle(w, zx, y, Ui.Scaled(c, 4), Ui.Scaled(c, 2));
        }
        g.FillRectangle(w, zx - 1, body.Bottom - Ui.Scaled(c, 6), Ui.Scaled(c, 4) + 2, Ui.Scaled(c, 4));
    }

    /// <summary>勾選框（表格用）：checked 為實心主色＋白勾，indeterminate 為一條橫線。</summary>
    public static void CheckBox(Graphics g, Rectangle r, bool? isChecked, Control c)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SectionPanel.RoundedRect(r, Ui.Scaled(c, 3));
        if (isChecked != false)
        {
            using var b = new SolidBrush(Theme.Accent);
            g.FillPath(b, path);
            using var p = new Pen(Color.White, Math.Max(1.6f, Ui.Scaled(c, 2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (isChecked == true)
            {
                g.DrawLines(p, new[]
                {
                    new PointF(r.Left + r.Width * 0.24f, r.Top + r.Height * 0.52f),
                    new PointF(r.Left + r.Width * 0.43f, r.Top + r.Height * 0.72f),
                    new PointF(r.Left + r.Width * 0.77f, r.Top + r.Height * 0.3f),
                });
            }
            else
            {
                g.DrawLine(p, r.Left + r.Width * 0.27f, r.Top + r.Height / 2f, r.Right - r.Width * 0.27f, r.Top + r.Height / 2f);
            }
        }
        else
        {
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
            using var p = new Pen(Theme.CheckBorder, Math.Max(1f, Ui.Scaled(c, 1) * 1.2f));
            g.DrawPath(p, path);
        }
    }

    /// <summary>綠色圓形打勾（比對完成）。</summary>
    public static void Done(Graphics g, Rectangle r, Color color, Control c)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(color);
        g.FillEllipse(b, r);
        using var p = new Pen(Color.White, Math.Max(1.6f, Ui.Scaled(c, 2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(p, new[]
        {
            new PointF(r.Left + r.Width * 0.27f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.44f, r.Top + r.Height * 0.68f),
            new PointF(r.Left + r.Width * 0.74f, r.Top + r.Height * 0.34f),
        });
    }
}

/// <summary>圖示＋文字的狀態標示（動作列右側的「比對完成」）。</summary>
internal sealed class StateLabel : Control
{
    public StateLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
    }

    public Color DotColor { get; set; } = Theme.Success;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }
        var textW = TextRenderer.MeasureText(Text, Font).Width;
        var d = Ui.Scaled(this, 20);
        var gap = Ui.Scaled(this, 8);
        var x = Width - textW - d - gap;
        Glyphs.Done(g, new Rectangle(x, (Height - d) / 2, d, d), DotColor, this);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + d + gap, 0, textW + 2, Height), Theme.TextDark,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
