namespace QuickZip;

/// <summary>An address bar of clickable path segments, collapsing leading segments when space runs out.</summary>
internal sealed class Breadcrumb : Control
{
    private const int Gap = 18;
    private string[] segments = [];
    private (Rectangle Bounds, int Target, string Text)[] laidOut = [];
    private int hover = -1;
    private readonly Image chevron = BrowserAppearance.Glyph('', 12, BrowserAppearance.Muted);
    private Image? leading;

    /// <summary>Raised with the segment index the user clicked; 0 is the archive root.</summary>
    public event Action<int>? Navigate;

    public Breadcrumb()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Grouping;
        Cursor = Cursors.Default;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? LeadingIcon
    {
        get => leading;
        set { leading = value; Invalidate(); }
    }

    public void SetSegments(IReadOnlyList<string> parts)
    {
        segments = parts.ToArray();
        AccessibleName = "位置：" + string.Join(" / ", segments);
        hover = -1;
        Layout();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Layout();
    }

    private new void Layout()
    {
        int start = 10 + (leading is null ? 0 : leading.Width + 6);
        int available = Math.Max(0, Width - start - 8);
        int[] widths = segments.Select(part => TextRenderer.MeasureText(part, Font).Width + 8).ToArray();
        int ellipsis = TextRenderer.MeasureText("…", Font).Width + 8;

        // Keep the deepest segments; replace whatever does not fit at the front with "…".
        int first = segments.Length - 1;
        int used = widths.Length == 0 ? 0 : widths[^1];
        while (first > 0 && used + Gap + widths[first - 1] + (first - 1 > 0 ? Gap + ellipsis : 0) <= available)
            used += Gap + widths[--first];

        var result = new List<(Rectangle, int, string)>();
        int x = start;
        int height = Math.Max(0, Height - 8);
        if (first > 0)
        {
            result.Add((new Rectangle(x, 4, ellipsis, height), first - 1, "…"));
            x += ellipsis + Gap;
        }
        for (int index = Math.Max(first, 0); index < segments.Length; index++)
        {
            int width = Math.Min(widths[index], Math.Max(ellipsis, Width - x - 8));
            result.Add((new Rectangle(x, 4, width, height), index, segments[index]));
            x += width + Gap;
        }
        laidOut = result.ToArray();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics canvas = e.Graphics;
        canvas.Clear(BrowserAppearance.BackdropOf(Parent));
        var frame = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        BrowserAppearance.FillRounded(canvas, Enabled ? Color.White : BrowserAppearance.Sidebar, frame, 6);
        BrowserAppearance.StrokeRounded(canvas, BrowserAppearance.Border, frame, 6);
        if (leading is not null)
            canvas.DrawImage(leading, 10, (Height - leading.Height) / 2);

        for (int slot = 0; slot < laidOut.Length; slot++)
        {
            var (bounds, _, text) = laidOut[slot];
            bool last = slot == laidOut.Length - 1;
            if (slot == hover && !last)
                BrowserAppearance.FillRounded(canvas, BrowserAppearance.Hover, bounds, 4);
            TextRenderer.DrawText(canvas, text, Font, bounds, last ? BrowserAppearance.Ink : BrowserAppearance.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (!last)
                canvas.DrawImage(chevron, bounds.Right + (Gap - chevron.Width) / 2, (Height - chevron.Height) / 2);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int found = Array.FindIndex(laidOut, item => item.Bounds.Contains(e.Location));
        if (found == laidOut.Length - 1)
            found = -1;
        if (found != hover)
        {
            hover = found;
            Cursor = found >= 0 ? Cursors.Hand : Cursors.Default;
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
        if (e.Button == MouseButtons.Left && hover >= 0)
            Navigate?.Invoke(laidOut[hover].Target);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            chevron.Dispose();
        base.Dispose(disposing);
    }
}
