using System.Drawing.Drawing2D;

namespace Photo2Disk;

/// <summary>
/// 可勾選、可用滑鼠上下拖動排序的清單。
/// 點一下切換勾選；按住拖動超過一小段距離就改成排序，放開時移到藍綠色插入線的位置。
/// 鍵盤：上下鍵選取、空白鍵勾選、Ctrl＋上下鍵移動。
/// Windows 內建的 CheckedListBox 在按下滑鼠時就改選取與勾選，無法和拖動共存，所以自己畫。
/// </summary>
internal sealed class ReorderCheckList : Control
{
    private const int RowHeight96 = 26;

    public sealed class Item
    {
        public required object Tag { get; init; }
        public required string Text { get; init; }
        public bool Checked { get; set; }
    }

    private readonly List<Item> items = new();
    private int selectedIndex = -1;
    private int pressIndex = -1;
    private Point pressPoint;
    private bool dragging;
    private int insertIndex = -1;

    public ReorderCheckList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        TabStop = true;
    }

    /// <summary>勾選狀態或順序改變（使用者操作造成的）。</summary>
    public event EventHandler? ItemsChanged;

    public event EventHandler? SelectedIndexChanged;

    public IReadOnlyList<Item> Items => items;

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            value = items.Count == 0 ? -1 : Math.Clamp(value, -1, items.Count - 1);
            if (value == selectedIndex)
            {
                return;
            }
            selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsDragging => dragging;

    public void SetItems(IEnumerable<Item> newItems)
    {
        var selectedTag = selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex].Tag : null;
        items.Clear();
        items.AddRange(newItems);
        selectedIndex = selectedTag is null ? -1 : items.FindIndex(i => Equals(i.Tag, selectedTag));
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把 from 的項目移到 to（移動後所在的位置）。</summary>
    public void MoveItem(int from, int to)
    {
        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count || from == to)
        {
            return;
        }
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        selectedIndex = to;
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private int Row => Ui.Scaled(this, RowHeight96);

    private int S(int px) => Ui.Scaled(this, px);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Math.Max(proposedSize.Width, S(120)), Math.Max(items.Count, 1) * Row + 2);

    private int IndexAt(Point p)
    {
        if (p.Y < 1 || p.X < 0 || p.X >= Width)
        {
            return -1;
        }
        var i = (p.Y - 1) / Row;
        return i < items.Count ? i : -1;
    }

    /// <summary>滑鼠位置對應的插入位置（0～Count，表示插在第幾列之前）。</summary>
    private int InsertAt(int y) => Math.Clamp((y - 1 + Row / 2) / Row, 0, items.Count);

    // ---------------------------------------------------------------- 滑鼠

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        pressIndex = IndexAt(e.Location);
        pressPoint = e.Location;
        dragging = false;
        if (pressIndex >= 0)
        {
            SelectedIndex = pressIndex;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (pressIndex < 0 || (e.Button & MouseButtons.Left) == 0)
        {
            Cursor = IndexAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
            return;
        }
        if (!dragging && Math.Abs(e.Y - pressPoint.Y) >= SystemInformation.DragSize.Height)
        {
            dragging = true;
            Cursor = Cursors.SizeNS;
        }
        if (dragging)
        {
            var insert = InsertAt(e.Y);
            if (insert != insertIndex)
            {
                insertIndex = insert;
                Invalidate();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || pressIndex < 0)
        {
            return;
        }
        var from = pressIndex;
        var wasDragging = dragging;
        var insert = insertIndex;
        ResetDrag();
        if (wasDragging)
        {
            // 插在自己下方時，移除自己後位置要減一
            var to = insert > from ? insert - 1 : insert;
            MoveItem(from, to);
        }
        else if (IndexAt(e.Location) == from)
        {
            Toggle(from);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && dragging)
        {
            ResetDrag();
        }
    }

    private void ResetDrag()
    {
        pressIndex = -1;
        dragging = false;
        insertIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    private void Toggle(int index)
    {
        if (index < 0 || index >= items.Count)
        {
            return;
        }
        items[index].Checked = !items[index].Checked;
        Invalidate();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- 鍵盤

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var delta = e.KeyCode switch
        {
            Keys.Up => -1,
            Keys.Down => 1,
            _ => 0,
        };
        if (delta != 0)
        {
            if (e.Control && selectedIndex >= 0)
            {
                MoveItem(selectedIndex, selectedIndex + delta);
            }
            else
            {
                SelectedIndex = selectedIndex < 0 ? 0 : Math.Clamp(selectedIndex + delta, 0, items.Count - 1);
            }
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space)
        {
            Toggle(selectedIndex);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (selectedIndex < 0 && items.Count > 0)
        {
            SelectedIndex = 0;
        }
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    // ---------------------------------------------------------------- 繪製

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var row = Row;
        var box = S(14);
        for (var i = 0; i < items.Count; i++)
        {
            var r = new Rectangle(1, 1 + i * row, Width - 2, row);
            var dimmed = dragging && i == pressIndex;
            if (i == selectedIndex)
            {
                using var sel = new SolidBrush(dimmed ? Theme.Surface : Theme.AccentLight);
                g.FillRectangle(sel, r);
            }

            // 勾選框
            var bx = r.Left + S(6);
            var by = r.Top + (row - box) / 2;
            using (var path = SectionPanel.RoundedRect(new Rectangle(bx, by, box, box), S(3)))
            {
                if (items[i].Checked)
                {
                    using var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.AccentDisabled);
                    g.FillPath(fill, path);
                    using var pen = new Pen(Color.White, Math.Max(1.5f, S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLines(pen, new[]
                    {
                        new PointF(bx + box * 0.22f, by + box * 0.52f),
                        new PointF(bx + box * 0.42f, by + box * 0.72f),
                        new PointF(bx + box * 0.78f, by + box * 0.30f),
                    });
                }
                else
                {
                    using var pen = new Pen(Theme.TextGray, Math.Max(1f, S(1)));
                    g.DrawPath(pen, path);
                }
            }

            // 文字
            var textRect = new Rectangle(bx + box + S(8), r.Top, r.Width - box - S(40), row);
            TextRenderer.DrawText(g, items[i].Text, Font, textRect, dimmed ? Theme.TextGray : Theme.TextDark,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            // 右側拖動把手（兩排三點）
            using (var dot = new SolidBrush(Theme.Border))
            {
                var d = Math.Max(2, S(3));
                var gx = r.Right - S(16);
                var gy = r.Top + row / 2 - S(6);
                for (var c = 0; c < 2; c++)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        g.FillEllipse(dot, gx + c * S(5), gy + k * S(5), d, d);
                    }
                }
            }
        }

        // 拖動時的插入線
        if (dragging && insertIndex >= 0)
        {
            var y = 1 + insertIndex * row;
            var d = S(8);
            y = Math.Clamp(y, d / 2 + 2, Height - d / 2 - 3); // 最上、最下時往內縮，不和外框疊在一起
            using var pen = new Pen(Theme.Accent, Math.Max(3f, S(3)));
            g.DrawLine(pen, S(4) + d / 2, y, Width - S(4) - d / 2, y);
            using var brush = new SolidBrush(Theme.Accent);
            g.FillEllipse(brush, S(4), y - d / 2, d, d);
            g.FillEllipse(brush, Width - S(4) - d, y - d / 2, d, d);
        }

        using (var border = new Pen(Focused ? Theme.Accent : Theme.Border))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    // ---------------------------------------------------------------- 測試用

    /// <summary>介面測試：模擬滑鼠從 from 列拖到 targetY（相對於清單頂端的像素），stopBeforeRelease 時停在拖動中。</summary>
    internal void SimulateDrag(int from, int targetY, bool stopBeforeRelease)
    {
        var start = new Point(Width / 2, 1 + from * Row + Row / 2);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, start.X, (start.Y + targetY) / 2, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, start.X, targetY, 0));
        if (!stopBeforeRelease)
        {
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, start.X, targetY, 0));
        }
    }

    internal void SimulateClick(int index)
    {
        var p = new Point(Width / 2, 1 + index * Row + Row / 2);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
    }

    internal int RowPixels => Row;
}
