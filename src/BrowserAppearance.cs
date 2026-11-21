using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace QuickZip;

internal static class BrowserAppearance
{
    private static bool Plain => SystemInformation.HighContrast;

    public static Color Surface => Plain ? SystemColors.Window : Color.FromArgb(251, 250, 253);
    public static Color Sidebar => Plain ? SystemColors.Window : Color.FromArgb(243, 241, 248);
    public static Color Border => Plain ? SystemColors.WindowText : Color.FromArgb(226, 222, 234);
    public static Color Ink => Plain ? SystemColors.WindowText : Color.FromArgb(36, 31, 49);
    public static Color Muted => Plain ? SystemColors.GrayText : Color.FromArgb(104, 97, 120);
    public static Color Accent => Plain ? SystemColors.Highlight : Color.FromArgb(98, 72, 196);
    public static Color AccentText => Plain ? SystemColors.HighlightText : Color.White;
    public static Color AccentSoft => Plain ? SystemColors.Window : Color.FromArgb(236, 231, 251);
    public static Color Hover => Plain ? SystemColors.Highlight : Color.FromArgb(232, 228, 243);
    public static Color Pressed => Plain ? SystemColors.Highlight : Color.FromArgb(220, 214, 238);

    private static readonly string IconFont =
        FontFamily.Families.Any(family => family.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Image Glyph(char symbol, int size, Color? color = null)
    {
        using var font = new Font(IconFont, size * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel);
        var bitmap = new Bitmap(size, size);
        using Graphics canvas = Graphics.FromImage(bitmap);
        canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color ?? Ink);
        using var alignment = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        canvas.DrawString(symbol.ToString(), font, brush, new RectangleF(0, 1, size, size), alignment);
        return bitmap;
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics canvas, Color color, RectangleF bounds, float radius)
    {
        using GraphicsPath path = Rounded(bounds, radius);
        using var brush = new SolidBrush(color);
        var smoothing = canvas.SmoothingMode;
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.FillPath(brush, path);
        canvas.SmoothingMode = smoothing;
    }

    public static void StrokeRounded(Graphics canvas, Color color, RectangleF bounds, float radius)
    {
        using GraphicsPath path = Rounded(bounds, radius);
        using var pen = new Pen(color);
        var smoothing = canvas.SmoothingMode;
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.DrawPath(pen, path);
        canvas.SmoothingMode = smoothing;
    }

    /// <summary>The first solid background behind a control, for painting rounded corners over.</summary>
    public static Color BackdropOf(Control? control)
    {
        for (; control is not null; control = control.Parent)
        {
            if (control.BackColor.A == 255)
                return control.BackColor;
        }
        return Surface;
    }

    /// <summary>Applies the shared window chrome: surface colors and a matching Windows 11 caption.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Surface;
        form.ForeColor = Ink;
        if (Plain)
            return;
        form.HandleCreated += (_, _) => Native.TintCaption(form, Surface, Ink);
        if (form.IsHandleCreated)
            Native.TintCaption(form, Surface, Ink);
    }

    public static Image EntryIcon(bool folder, int size)
    {
        var bitmap = new Bitmap(size, size);
        using Graphics canvas = Graphics.FromImage(bitmap);
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.ScaleTransform(size / 16f, size / 16f);
        using var fill = new SolidBrush(Plain ? SystemColors.Window : folder
            ? Color.FromArgb(228, 218, 248) : Color.White);
        using var outline = new Pen(Plain ? SystemColors.WindowText : Color.FromArgb(119, 103, 152));
        PointF[] silhouette = folder
            ? [new(1.4f, 4.5f), new(6.1f, 4.5f), new(7.3f, 6), new(14.4f, 6), new(14.4f, 13.3f), new(1.4f, 13.3f)]
            : [new(3.5f, 1.6f), new(9.3f, 1.6f), new(12.5f, 4.8f), new(12.5f, 14.4f), new(3.5f, 14.4f)];
        canvas.FillPolygon(fill, silhouette);
        canvas.DrawPolygon(outline, silhouette);
        if (!folder)
        {
            canvas.DrawLines(outline, [new PointF(9.3f, 1.6f), new PointF(9.3f, 4.8f), new PointF(12.5f, 4.8f)]);
            canvas.DrawLine(outline, 6, 8, 10, 8);
            canvas.DrawLine(outline, 6, 10.8f, 10, 10.8f);
        }
        return bitmap;
    }
}

/// <summary>Flat toolbar and menu look: no gradients, soft rounded hover and pressed states.</summary>
internal sealed class FlatRenderer : ToolStripProfessionalRenderer
{
    public FlatRenderer() : base(new FlatColors()) => RoundedEdges = false;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBackground(e);
            return;
        }
        using var brush = new SolidBrush(e.BackColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
            base.OnRenderToolStripBorder(e);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => PaintItem(e);
    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => PaintItem(e);

    protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
    {
        PaintItem(e);
        if (e.Item is ToolStripSplitButton split)
        {
            Rectangle arrow = split.DropDownButtonBounds;
            if (split.Selected || split.Pressed)
            {
                using var pen = new Pen(BrowserAppearance.Border);
                e.Graphics.DrawLine(pen, arrow.Left, arrow.Top + 6, arrow.Left, arrow.Bottom - 6);
            }
            DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, split, arrow, BrowserAppearance.Muted, ArrowDirection.Down));
        }
    }

    private static void PaintItem(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown || !e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed))
            return;
        bool pressed = e.Item.Pressed || e.Item is ToolStripButton { Checked: true };
        var bounds = new RectangleF(1, 3, e.Item.Width - 2, e.Item.Height - 6);
        BrowserAppearance.FillRounded(e.Graphics, pressed ? BrowserAppearance.Pressed : BrowserAppearance.Hover, bounds, 5);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown)
            e.TextColor = e.Item.Enabled ? BrowserAppearance.Ink : BrowserAppearance.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderSeparator(e);
            return;
        }
        using var pen = new Pen(BrowserAppearance.Border);
        int x = e.Item.Width / 2;
        e.Graphics.DrawLine(pen, x, 9, x, e.Item.Height - 9);
    }

    private sealed class FlatColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => BrowserAppearance.Hover;
        public override Color MenuItemBorder => BrowserAppearance.Hover;
        public override Color MenuBorder => BrowserAppearance.Border;
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color SeparatorDark => BrowserAppearance.Border;
        public override Color SeparatorLight => Color.White;
    }
}

/// <summary>A rounded, owner-drawn button for primary and secondary actions.</summary>
internal sealed class PillButton : Button
{
    private bool hovering;
    private bool pressing;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; init; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? Glyph { get; init; }

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = pressing = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressing = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressing = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics canvas = e.Graphics;
        canvas.Clear(BrowserAppearance.BackdropOf(Parent));
        Color fill = Primary
            ? pressing ? ControlPaint.Dark(BrowserAppearance.Accent, 0.05f) :
              hovering ? ControlPaint.Light(BrowserAppearance.Accent, 0.15f) : BrowserAppearance.Accent
            : pressing ? BrowserAppearance.Pressed : hovering ? BrowserAppearance.Hover : Color.White;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        BrowserAppearance.FillRounded(canvas, fill, bounds, 6);
        if (!Primary)
            BrowserAppearance.StrokeRounded(canvas, BrowserAppearance.Border, bounds, 6);
        if (Focused && ShowFocusCues)
            BrowserAppearance.StrokeRounded(canvas, BrowserAppearance.Ink, RectangleF.Inflate(bounds, -2, -2), 4);

        Color text = Primary ? BrowserAppearance.AccentText : BrowserAppearance.Ink;
        Size label = TextRenderer.MeasureText(canvas, Text, Font);
        int glyph = Glyph is null ? 0 : Glyph.Width + 8;
        int x = (Width - label.Width - glyph) / 2;
        if (Glyph is not null)
            canvas.DrawImage(Glyph, x, (Height - Glyph.Height) / 2);
        TextRenderer.DrawText(canvas, Text, Font, new Rectangle(x + glyph, 0, label.Width, Height), text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}
