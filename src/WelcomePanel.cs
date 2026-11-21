namespace QuickZip;

/// <summary>The start screen: a drop target, the two main actions, recent archives and the default-app hint.</summary>
internal sealed class WelcomePanel : Panel
{
    private readonly FlowLayoutPanel column = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent
    };
    private readonly FlowLayoutPanel recent = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new(0, 6, 0, 0), BackColor = Color.Transparent
    };
    private readonly Label recentTitle;
    private readonly Panel hint = new() { Height = 40, Margin = new(0, 22, 0, 0), BackColor = Color.Transparent };
    private readonly Image tile = BrowserAppearance.Glyph('', 40, BrowserAppearance.Accent);
    private readonly ToolTip tips = new();
    private bool dropping;

    public event Action? OpenRequested;
    public event Action? CompressRequested;
    public event Action<string>? RecentRequested;
    public event Action? DefaultRequested;
    public event Action? HintDismissed;

    public WelcomePanel(Font font)
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        BackColor = Color.White;
        Font = font;

        var icon = new Panel { Size = new(76, 76), Margin = new(0, 0, 0, 18), BackColor = Color.Transparent };
        icon.Paint += (_, e) =>
        {
            e.Graphics.Clear(BrowserAppearance.BackdropOf(this));
            BrowserAppearance.FillRounded(e.Graphics, BrowserAppearance.AccentSoft, new RectangleF(0, 0, 75, 75), 18);
            e.Graphics.DrawImage(tile, (76 - tile.Width) / 2, (76 - tile.Height) / 2);
        };
        var title = new Label
        {
            Text = "拖放压缩包到这里", AutoSize = true, ForeColor = BrowserAppearance.Ink,
            Font = new Font(font.FontFamily, font.SizeInPoints * 1.65f, FontStyle.Bold), Margin = new(0, 0, 0, 6)
        };
        var subtitle = new Label
        {
            Text = "支持 ZIP、7z、RAR、TAR、GZ、XZ、ISO、CAB 等格式 · 拖入普通文件即可压缩",
            AutoSize = true, ForeColor = BrowserAppearance.Muted, Margin = new(0, 0, 0, 22)
        };
        var open = new PillButton
        {
            Text = "打开压缩包", Primary = true, Size = new(136, 38), Margin = new(0, 0, 10, 0),
            Glyph = BrowserAppearance.Glyph('', 16, BrowserAppearance.AccentText)
        };
        var compress = new PillButton
        {
            Text = "压缩文件…", Size = new(124, 38), Margin = new(0),
            Glyph = BrowserAppearance.Glyph('', 16)
        };
        open.Click += (_, _) => OpenRequested?.Invoke();
        compress.Click += (_, _) => CompressRequested?.Invoke();
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0), BackColor = Color.Transparent };
        actions.Controls.AddRange([open, compress]);

        recentTitle = new Label
        {
            Text = "最近打开", AutoSize = true, ForeColor = BrowserAppearance.Muted, Margin = new(0, 30, 0, 0),
            Font = new Font(font, FontStyle.Bold)
        };

        var hintText = new LinkLabel
        {
            Text = "让 ZIP 成为压缩包的默认打开方式，双击即可浏览。设为默认…",
            AutoSize = true, Location = new(14, 11), LinkColor = BrowserAppearance.Accent,
            ActiveLinkColor = BrowserAppearance.Accent, LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent
        };
        hintText.LinkArea = new LinkArea(hintText.Text.Length - 5, 5);
        hintText.LinkClicked += (_, _) => DefaultRequested?.Invoke();
        var dismiss = new LinkLabel
        {
            Text = "不再提示", AutoSize = true, LinkColor = BrowserAppearance.Muted, ActiveLinkColor = BrowserAppearance.Ink,
            LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        dismiss.LinkClicked += (_, _) => { hint.Visible = false; HintDismissed?.Invoke(); Recenter(); };
        hint.Controls.AddRange([hintText, dismiss]);
        hint.Width = hintText.PreferredWidth + dismiss.PreferredWidth + 48;
        dismiss.Location = new(hint.Width - dismiss.PreferredWidth - 14, 11);
        hint.Paint += (_, e) =>
        {
            e.Graphics.Clear(BrowserAppearance.BackdropOf(this));
            var box = new RectangleF(0.5f, 0.5f, hint.Width - 1.5f, hint.Height - 1.5f);
            BrowserAppearance.FillRounded(e.Graphics, BrowserAppearance.AccentSoft, box, 8);
        };

        column.Controls.AddRange([icon, title, subtitle, actions, recentTitle, recent, hint]);
        Controls.Add(column);
        Resize += (_, _) => Recenter();
        column.SizeChanged += (_, _) => Recenter();
    }

    public void ShowRecent(IEnumerable<string> archives, bool offerDefault)
    {
        SuspendLayout();
        foreach (Control control in recent.Controls.Cast<Control>().ToArray())
            control.Dispose();
        recent.Controls.Clear();
        foreach (string path in archives.Where(File.Exists).Take(5))
        {
            var link = new LinkLabel
            {
                Text = Path.GetFileName(path), AutoSize = true, Margin = new(0, 4, 0, 0), Tag = path,
                LinkColor = BrowserAppearance.Ink, ActiveLinkColor = BrowserAppearance.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent
            };
            var folder = new Label
            {
                Text = Path.GetDirectoryName(path), AutoSize = false, AutoEllipsis = true,
                Size = new(Math.Max(120, 460 - link.PreferredWidth), link.PreferredHeight),
                ForeColor = BrowserAppearance.Muted, Margin = new(6, 4, 0, 0), BackColor = Color.Transparent
            };
            folder.MouseEnter += (_, _) => tips.SetToolTip(folder, path);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0), BackColor = Color.Transparent };
            row.Controls.AddRange([link, folder]);
            link.LinkClicked += (_, _) => RecentRequested?.Invoke(path);
            recent.Controls.Add(row);
        }
        recentTitle.Visible = recent.Visible = recent.Controls.Count > 0;
        hint.Visible = offerDefault;
        ResumeLayout();
        Recenter();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dropping
    {
        get => dropping;
        set { if (dropping != value) { dropping = value; Invalidate(); } }
    }

    private void Recenter()
    {
        Size size = column.PreferredSize;
        column.Location = new(Math.Max(16, (ClientSize.Width - size.Width) / 2),
            Math.Max(16, (ClientSize.Height - size.Height) / 2 - 10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var zone = new RectangleF(14.5f, 14.5f, ClientSize.Width - 30, ClientSize.Height - 30);
        if (zone.Width <= 0 || zone.Height <= 0)
            return;
        if (dropping)
            BrowserAppearance.FillRounded(e.Graphics, Color.FromArgb(248, 246, 254), zone, 14);
        using var pen = new Pen(dropping ? BrowserAppearance.Accent : BrowserAppearance.Border, dropping ? 2 : 1.4f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash, DashPattern = [5, 4]
        };
        using var path = BrowserAppearance.Rounded(zone, 14);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            tile.Dispose();
        base.Dispose(disposing);
    }
}
