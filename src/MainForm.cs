using System.Diagnostics;

namespace QuickZip;

internal sealed class MainForm : Form
{
    private sealed record EncodingOption(string Label, int? CodePage)
    {
        public override string ToString() => Label;
    }

    // Files that run code when opened. Opening them from an archive asks first, as Explorer does for downloads.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".lnk", ".hta", ".cpl", ".reg", ".jar", ".appx", ".msix"
    };

    private readonly ListView files = new()
    {
        View = View.Details, VirtualMode = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        FullRowSelect = true, HideSelection = false, MultiSelect = true, ShowItemToolTips = true
    };
    private readonly TreeView folders = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false,
        ShowLines = false, ShowRootLines = true, FullRowSelect = true, HotTracking = true
    };
    private readonly ImageList entryIcons = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly ShellIcons shellIcons;
    private readonly Dictionary<ArchiveFolder, TreeNode> folderNodes = [];
    private readonly System.Windows.Forms.Timer filterDelay = new() { Interval = 150 };
    private readonly ComboBox encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox search = new() { PlaceholderText = "搜索压缩包  Ctrl+F", BorderStyle = BorderStyle.None };
    private readonly Panel searchBox = new() { Dock = DockStyle.Fill, Margin = new(6, 4, 0, 4), Padding = new(30, 0, 8, 0) };
    private readonly Breadcrumb location = new() { Dock = DockStyle.Fill, Margin = new(6, 4, 0, 4) };
    private readonly ToolTip pathTooltip = new() { ShowAlways = true };
    private readonly ToolStripStatusLabel count = new();
    private readonly ToolStripStatusLabel selectionCount = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly ToolStripButton selected = new("解压所选");
    private readonly ToolStripSplitButton extract = new("解压");
    private readonly ToolStripMenuItem extractHere = new("解压到当前文件夹");
    private readonly ToolStripMenuItem extractFolder = new("解压到同名文件夹");
    private readonly ToolStripButton test = new("校验");
    private readonly ToolStripDropDownButton add = new("添加");
    private readonly ToolStripButton remove = new("删除");
    private readonly OpenedFiles opened;
    private bool writable;
    private bool namesEncrypted;
    private bool draggingOut;
    private bool closeChecked;
    private readonly ToolStripDropDownButton more = new() { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripMenuItem makeDefault = new("设为默认压缩软件…");
    private readonly ToolStripMenuItem reveal = new("在资源管理器中显示压缩包");
    private readonly Button up = new() { Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new(0, 4, 0, 4) };
    private readonly WelcomePanel welcome;
    private readonly SplitContainer browser = new()
    {
        Dock = DockStyle.Fill, Size = new(900, 430), SplitterDistance = 220,
        Panel1MinSize = 140, Panel2MinSize = 340, SplitterWidth = 1, Margin = new Padding(0)
    };
    private readonly List<TempDirectory> staged = [];
    private readonly JobStatus jobStatus = new();
    private readonly TableLayoutPanel navigation = new() { Dock = DockStyle.Fill, ColumnCount = 4, Margin = new(0) };
    private readonly AppSettings settings = AppSettings.Current;
    private ArchiveSession? archive;
    private ArchiveContents? contents;
    private ArchiveFolder? currentFolder;
    private ArchiveRow[] visibleFiles = [];
    private string? password;
    private int? codePage;
    private string? lastExtracted;
    private bool loading;
    private bool synchronizingTree;
    private int sortColumn;
    private bool sortDescending;
    private bool showingSearch;
    private static readonly string[] ColumnTitles = ["名称", "类型", "大小", "修改时间"];

    public MainForm(string? initialPath)
    {
        Text = "ZIP";
        Icon = ZipWindow.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont!;
        BrowserAppearance.Apply(this);
        ClientSize = new(1040, 660);
        MinimumSize = new(760, 460);
        AllowDrop = true;
        KeyPreview = true;
        RestorePlacement();

        int iconSize = SystemInformation.SmallIconSize.Width;
        entryIcons.ImageSize = new(iconSize, iconSize + Math.Max(8, iconSize / 2));
        shellIcons = new ShellIcons(entryIcons);
        welcome = new WelcomePanel(Font);
        opened = new OpenedFiles(this) { Busy = () => jobStatus.IsRunning || !CanFocus };
        opened.Changed += WriteBackAsync;
        Activated += async (_, _) => await opened.CheckAsync();

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new(10, 6, 10, 0) };
        page.RowStyles.Add(new(SizeType.Absolute, 46));
        page.RowStyles.Add(new(SizeType.Absolute, 42));
        page.RowStyles.Add(new(SizeType.Percent, 100));
        page.RowStyles.Add(new(SizeType.Absolute, 0));
        page.RowStyles.Add(new(SizeType.Absolute, 28));
        Controls.Add(page);

        // Toolbar
        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, AutoSize = false, Padding = new(0),
            Font = Font, BackColor = BrowserAppearance.Surface, ForeColor = BrowserAppearance.Ink,
            ImageScalingSize = new(20, 20), Renderer = new FlatRenderer(), Margin = new(0)
        };
        var open = new ToolStripButton("打开");
        var compress = new ToolStripDropDownButton("压缩");
        ConfigureAction(open, '', "打开压缩包（Ctrl+O）");
        ConfigureAction(compress, '', "将文件或文件夹压缩成归档");
        ConfigureAction(extract, '', "选择位置并解压全部文件（Ctrl+E）");
        ConfigureAction(selected, '', "解压选中的文件或文件夹");
        ConfigureAction(test, '', "校验压缩包的完整性");
        ConfigureAction(add, '', "向当前文件夹添加文件（也可以直接拖进来）");
        ConfigureAction(remove, '', "从压缩包中删除所选项目（Delete）");
        ConfigureAction(more, '', "更多");
        add.DropDownItems.Add("添加文件…", null, (_, _) => ChooseFilesToAdd(folders: false));
        add.DropDownItems.Add("添加文件夹…", null, (_, _) => ChooseFilesToAdd(folders: true));
        remove.Click += async (_, _) => await DeleteSelectedAsync();
        more.DisplayStyle = ToolStripItemDisplayStyle.Image;
        more.AccessibleName = "更多";
        more.ShowDropDownArrow = false;
        compress.DropDownItems.Add("选择文件…", null, (_, _) => ChooseFiles());
        compress.DropDownItems.Add("选择文件夹…", null, (_, _) => ChooseFolder());
        extract.DropDownItems.Add("选择位置…", null, async (_, _) => await ExtractItemsAsync(false));
        extract.DropDownItems.AddRange([new ToolStripSeparator(), extractHere, extractFolder]);
        extractHere.Click += async (_, _) => await ExtractBesideAsync(newFolder: false);
        extractFolder.Click += async (_, _) => await ExtractBesideAsync(newFolder: true);
        makeDefault.Click += (_, _) => FileAssociation.MakeDefault(this);
        reveal.Click += (_, _) => RevealArchive();
        more.DropDownItems.AddRange([
            new ToolStripMenuItem("设置…", BrowserAppearance.Glyph('', 16), (_, _) => ShowSettings()),
            makeDefault, reveal, new ToolStripSeparator(),
            new ToolStripMenuItem("关于 ZIP", null, (_, _) => ShowAbout())]);
        more.DropDownOpening += (_, _) => reveal.Enabled = archive is not null;
        toolbar.Items.AddRange([open, compress, new ToolStripSeparator(), extract, selected, test,
            new ToolStripSeparator(), add, remove, more]);
        page.Controls.Add(toolbar, 0, 0);

        // Navigation row: up, breadcrumb, encoding, search
        navigation.RowCount = 1;
        navigation.RowStyles.Add(new(SizeType.Percent, 100));
        navigation.ColumnStyles.Add(new(SizeType.Absolute, 34));
        navigation.ColumnStyles.Add(new(SizeType.Percent, 100));
        navigation.ColumnStyles.Add(new(SizeType.Absolute, 0));
        navigation.ColumnStyles.Add(new(SizeType.Absolute, 240));
        up.Image = BrowserAppearance.Glyph('', 16);
        up.FlatAppearance.BorderSize = 0;
        up.FlatAppearance.MouseOverBackColor = BrowserAppearance.Hover;
        up.FlatAppearance.MouseDownBackColor = BrowserAppearance.Pressed;
        up.AccessibleName = "上一级";
        up.Click += (_, _) => GoUp();
        pathTooltip.SetToolTip(up, "上一级（Alt+↑ 或 Backspace）");
        location.Font = Font;
        location.LeadingIcon = BrowserAppearance.Glyph('', 16, BrowserAppearance.Accent);
        location.Navigate += NavigateToDepth;
        encoding.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        encoding.Margin = new(6, 8, 0, 0);
        encoding.AccessibleName = "ZIP 文件名编码";
        encoding.Items.AddRange(new EncodingOption[]
        {
            new("默认编码", null), new("简体中文 · GB18030", 54936), new("简体中文 · GBK", 936),
            new("繁体中文 · Big5", 950), new("日语 · Shift-JIS", 932), new("韩语 · CP949", 949),
            new("UTF-8", 65001), new("西欧 · CP437", 437)
        });
        encoding.SelectedIndex = 0;
        pathTooltip.SetToolTip(encoding, "文件名显示为乱码时，换一种编码试试");
        BuildSearchBox();
        navigation.Controls.Add(up, 0, 0);
        navigation.Controls.Add(location, 1, 0);
        navigation.Controls.Add(encoding, 2, 0);
        navigation.Controls.Add(searchBox, 3, 0);
        page.Controls.Add(navigation, 0, 1);

        // Folder tree
        folders.ImageList = entryIcons;
        folders.BackColor = BrowserAppearance.Sidebar;
        folders.ForeColor = BrowserAppearance.Ink;
        folders.AccessibleName = "压缩包目录";
        folders.ItemHeight = entryIcons.ImageSize.Height + 2;
        folders.Indent = 16;
        folders.HandleCreated += (_, _) => Native.UseExplorerTheme(folders);
        folders.BeforeExpand += (_, args) => { if (args.Node is { } node) PopulateTreeNode(node); };
        folders.AfterSelect += (_, args) =>
        {
            if (!synchronizingTree && args.Node?.Tag is ArchiveFolder folder)
                Navigate(folder);
        };

        // File list
        files.SmallImageList = entryIcons;
        files.ForeColor = BrowserAppearance.Ink;
        files.AccessibleName = "压缩包中的文件";
        files.Columns.Add(ColumnTitles[0], 340);
        files.Columns.Add(ColumnTitles[1], 140);
        files.Columns.Add(ColumnTitles[2], 96, HorizontalAlignment.Right);
        files.Columns.Add(ColumnTitles[3], 150);
        files.HandleCreated += (_, _) =>
        {
            Native.UseExplorerTheme(files);
            Native.SetSortArrow(files, sortColumn, sortDescending);
        };
        files.ColumnClick += (_, args) =>
        {
            sortDescending = sortColumn == args.Column && !sortDescending;
            sortColumn = args.Column;
            RefreshFileList();
        };
        files.RetrieveVirtualItem += (_, args) =>
        {
            if ((uint)args.ItemIndex >= (uint)visibleFiles.Length)
            {
                args.Item = new ListViewItem("");
                return;
            }
            var entry = visibleFiles[args.ItemIndex];
            var (image, type) = Describe(entry);
            if (entry.Item?.Encrypted == true)
                type += " · 加密";
            string name = showingSearch ? entry.Path : entry.Name;
            args.Item = new ListViewItem([name, type, FormatSize(entry.Folder is null ? entry.Item?.Size : null),
                FormatDate(entry.Item?.Modified)], image) { ToolTipText = entry.Path };
        };
        files.SelectedIndexChanged += (_, _) => UpdateButtons();
        files.VirtualItemsSelectionRangeChanged += (_, _) => UpdateButtons();
        files.ItemDrag += (_, _) => DragToExplorer();
        files.ItemActivate += (_, _) => ActivateSelection();
        files.KeyDown += (_, args) =>
        {
            if (args.Control && args.KeyCode == Keys.A)
            {
                Native.SelectAll(files);
                UpdateButtons();
                args.SuppressKeyPress = true;
            }
            if (args.KeyCode == Keys.Back)
            {
                GoUp();
                args.SuppressKeyPress = true;
            }
            if (args.KeyCode == Keys.Delete && remove.Enabled)
            {
                _ = DeleteSelectedAsync();
                args.SuppressKeyPress = true;
            }
            if (args.KeyCode == Keys.F2 && writable)
            {
                _ = RenameSelectedAsync();
                args.SuppressKeyPress = true;
            }
        };

        var listMenu = new ContextMenuStrip { Renderer = new FlatRenderer() };
        var openItem = (ToolStripMenuItem)listMenu.Items.Add("打开", null, (_, _) => ActivateSelection());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        var openWith = listMenu.Items.Add("打开方式…", null, (_, _) => OpenSelectedFile(chooseApp: true));
        listMenu.Items.Add(new ToolStripSeparator());
        listMenu.Items.Add("解压所选…", BrowserAppearance.Glyph('', 16), async (_, _) => await ExtractItemsAsync(true));
        listMenu.Items.Add("复制名称", null, (_, _) => CopyNames());
        var editSeparator = new ToolStripSeparator();
        var renameItem = new ToolStripMenuItem("重命名", null, async (_, _) => await RenameSelectedAsync()) { ShortcutKeyDisplayString = "F2" };
        var deleteItem = new ToolStripMenuItem("删除", BrowserAppearance.Glyph('', 16), async (_, _) => await DeleteSelectedAsync())
            { ShortcutKeyDisplayString = "Del" };
        listMenu.Items.AddRange([editSeparator, renameItem, deleteItem]);
        listMenu.Opening += (_, args) =>
        {
            ArchiveRow[] rows = SelectedRows();
            openItem.Visible = rows.Length == 1;
            openWith.Visible = rows is [{ Folder: null }];
            editSeparator.Visible = deleteItem.Visible = writable;
            renameItem.Visible = writable && rows.Length == 1 && !showingSearch;
            args.Cancel = rows.Length == 0 || jobStatus.IsRunning;
        };
        files.ContextMenuStrip = listMenu;

        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = BrowserAppearance.Sidebar, Padding = new(6, 0, 2, 4), RowCount = 2, ColumnCount = 1 };
        sidebar.RowStyles.Add(new(SizeType.Absolute, 32));
        sidebar.RowStyles.Add(new(SizeType.Percent, 100));
        sidebar.Controls.Add(new Label { Text = "目录", Dock = DockStyle.Fill, ForeColor = BrowserAppearance.Muted,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new(6, 0, 0, 0), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        sidebar.Controls.Add(folders, 0, 1);
        browser.BackColor = BrowserAppearance.Border;
        browser.Panel1.Controls.Add(sidebar);
        browser.Panel2.BackColor = Color.White;
        browser.Panel2.Controls.Add(files);
        if (settings.Splitter > 0)
            browser.SplitterDistance = settings.Splitter;

        var content = new Panel { Dock = DockStyle.Fill, BackColor = BrowserAppearance.Border, Margin = new(0, 4, 0, 0), Padding = new(1) };
        welcome.OpenRequested += ChooseArchive;
        welcome.CompressRequested += ChooseFiles;
        welcome.RecentRequested += OpenArchive;
        welcome.DefaultRequested += () => FileAssociation.MakeDefault(this);
        welcome.HintDismissed += () => settings.AssociationHintDismissed = true;
        content.Controls.Add(browser);
        content.Controls.Add(welcome);
        page.Controls.Add(content, 0, 2);

        var status = new StatusStrip
        {
            Dock = DockStyle.Fill, SizingGrip = false, Renderer = new FlatRenderer(),
            BackColor = BrowserAppearance.Surface, ForeColor = BrowserAppearance.Muted, Margin = new(0)
        };
        count.ForeColor = BrowserAppearance.Muted;
        count.LinkColor = BrowserAppearance.Accent;
        count.ActiveLinkColor = BrowserAppearance.Accent;
        count.LinkBehavior = LinkBehavior.HoverUnderline;
        count.Click += (_, _) =>
        {
            if (count.IsLink && lastExtracted is not null && Directory.Exists(lastExtracted))
                Process.Start(new ProcessStartInfo(lastExtracted) { UseShellExecute = true })?.Dispose();
        };
        status.Items.AddRange([count, selectionCount]);
        page.Controls.Add(jobStatus, 0, 3);
        page.Controls.Add(status, 0, 4);
        jobStatus.VisibleChanged += (_, _) => page.RowStyles[3].Height = jobStatus.Visible ? 38 : 0;
        FormClosing += (_, args) =>
        {
            if (jobStatus.IsRunning)
            {
                args.Cancel = true;
                jobStatus.Cancel();
                return;
            }
            if (!closeChecked && settings.OfferWriteBack && opened.Pending().Count > 0)
            {
                // Ask about unsaved edits first, then close for real.
                args.Cancel = true;
                BeginInvoke(async () =>
                {
                    if (!await opened.CheckAsync(explicitAction: true))
                        return;
                    closeChecked = true;
                    Close();
                });
                return;
            }
            SavePlacement();
        };

        open.Click += (_, _) => ChooseArchive();
        selected.Click += async (_, _) => await ExtractItemsAsync(true);
        extract.ButtonClick += async (_, _) => await ExtractItemsAsync(false);
        test.Click += (_, _) => Test();
        search.TextChanged += (_, _) =>
        {
            filterDelay.Stop();
            if (!loading)
                filterDelay.Start();
        };
        filterDelay.Tick += (_, _) =>
        {
            filterDelay.Stop();
            RefreshFileList();
        };
        encoding.SelectedIndexChanged += (_, _) => ChangeEncoding();
        DragEnter += (_, args) =>
        {
            if (args.Data?.GetDataPresent(DataFormats.FileDrop) == true && !jobStatus.IsRunning && !draggingOut)
            {
                args.Effect = DragDropEffects.Copy;
                welcome.Dropping = true;
            }
        };
        DragLeave += (_, _) => welcome.Dropping = false;
        DragDrop += (_, args) =>
        {
            welcome.Dropping = false;
            if (draggingOut)
                return;
            if (args.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                // With an editable archive open, dropping files adds them to the folder being shown.
                if (writable && browser.Visible && browser.RectangleToScreen(browser.ClientRectangle).Contains(args.X, args.Y))
                    BeginInvoke(async () => await AddFromDiskAsync(paths, confirm: true));
                else if (paths.Length == 1 && File.Exists(paths[0]) && ArchiveFormat.IsArchive(paths[0]))
                    OpenArchive(paths[0]);
                else
                    ShowCompression(paths);
            }
        };
        KeyDown += (_, args) =>
        {
            if (args.Control && args.KeyCode == Keys.O)
            {
                ChooseArchive();
                args.SuppressKeyPress = true;
            }
            if (args.Control && args.KeyCode == Keys.E && extract.Enabled)
            {
                _ = ExtractItemsAsync(false);
                args.SuppressKeyPress = true;
            }
            if (args.Control && args.KeyCode == Keys.F && search.Enabled)
            {
                search.Focus();
                search.SelectAll();
                args.SuppressKeyPress = true;
            }
            if (args.KeyCode == Keys.Escape && search.Focused && search.TextLength > 0)
            {
                search.Clear();
                args.SuppressKeyPress = true;
            }
            if (args.Alt && args.KeyCode == Keys.Up)
            {
                GoUp();
                args.SuppressKeyPress = true;
            }
        };
        FormClosed += (_, _) =>
        {
            opened.Dispose();
            archive?.Dispose();
            filterDelay.Dispose();
            pathTooltip.Dispose();
            entryIcons.Dispose();
            foreach (TempDirectory directory in staged)
                directory.Dispose();
        };
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialPath))
                OpenArchive(initialPath);
        };
        RefreshFileList();
    }

    private void BuildSearchBox()
    {
        var glyph = BrowserAppearance.Glyph('', 14, BrowserAppearance.Muted);
        searchBox.BackColor = Color.White;
        search.BackColor = Color.White;
        search.Dock = DockStyle.None;
        search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        search.AccessibleName = "搜索压缩包内容";
        searchBox.Controls.Add(search);
        searchBox.Resize += (_, _) =>
        {
            search.Width = Math.Max(10, searchBox.ClientSize.Width - 40);
            search.Location = new(30, (searchBox.ClientSize.Height - search.Height) / 2);
        };
        searchBox.Click += (_, _) => search.Focus();
        search.GotFocus += (_, _) => searchBox.Invalidate();
        search.LostFocus += (_, _) => searchBox.Invalidate();
        search.EnabledChanged += (_, _) =>
        {
            search.BackColor = searchBox.BackColor = search.Enabled ? Color.White : BrowserAppearance.Sidebar;
            searchBox.Invalidate();
        };
        searchBox.Paint += (_, e) =>
        {
            e.Graphics.Clear(BrowserAppearance.Surface);
            var frame = new RectangleF(0.5f, 0.5f, searchBox.Width - 1.5f, searchBox.Height - 1.5f);
            BrowserAppearance.FillRounded(e.Graphics, searchBox.BackColor, frame, 6);
            BrowserAppearance.StrokeRounded(e.Graphics, search.Focused ? BrowserAppearance.Accent : BrowserAppearance.Border, frame, 6);
            e.Graphics.DrawImage(glyph, 10, (searchBox.Height - glyph.Height) / 2);
        };
    }

    private static void ConfigureAction(ToolStripItem action, char glyph, string help)
    {
        action.ToolTipText = help;
        action.Image = BrowserAppearance.Glyph(glyph, 20);
        action.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        action.Padding = new(8, 2, 8, 2);
        action.Margin = new(0, 1, 2, 1);
    }

    private (int Image, string Type) Describe(ArchiveRow entry) =>
        entry.Folder is not null ? shellIcons.Folder : shellIcons.File(entry.Name);

    // ── Window placement ───────────────────────────────────────────────

    private void RestorePlacement()
    {
        if (settings.Bounds is not [int x, int y, int width, int height])
            return;
        var bounds = new Rectangle(x, y, Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));
        if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
            return;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (settings.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void SavePlacement()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.Bounds = [bounds.X, bounds.Y, bounds.Width, bounds.Height];
        settings.Maximized = WindowState == FormWindowState.Maximized;
        settings.Splitter = browser.SplitterDistance;
        settings.Save();
    }

    // ── Folder tree ────────────────────────────────────────────────────

    private void ResetBrowser(ArchiveContents next, string restoreFolder = "")
    {
        contents = next;
        var restore = next.Root;
        foreach (string part in restoreFolder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!restore.Folders.TryGetValue(part, out ArchiveFolder? child))
                break;
            restore = child;
        }

        folders.BeginUpdate();
        synchronizingTree = true;
        try
        {
            folders.Nodes.Clear();
            folderNodes.Clear();
            TreeNode root = CreateTreeNode(next.Root);
            folders.Nodes.Add(root);
            root.Expand();
            SelectTreeFolder(restore);
        }
        finally
        {
            synchronizingTree = false;
            folders.EndUpdate();
        }
        currentFolder = restore;
        RefreshFileList();
    }

    private TreeNode CreateTreeNode(ArchiveFolder folder)
    {
        int image = shellIcons.Folder.Index;
        var node = new TreeNode(folder.Parent is null ? Path.GetFileName(archive?.OriginalPath) ?? "根目录" : folder.Name)
        {
            Tag = folder, ImageIndex = image, SelectedImageIndex = image
        };
        if (folder.Folders.Count > 0)
            node.Nodes.Add(new TreeNode());
        folderNodes.Add(folder, node);
        return node;
    }

    private void PopulateTreeNode(TreeNode node)
    {
        if (node.Tag is not ArchiveFolder folder || node.Nodes.Count != 1 || node.Nodes[0].Tag is not null)
            return;
        node.Nodes.Clear();
        foreach (var child in folder.Folders.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            node.Nodes.Add(CreateTreeNode(child));
    }

    private void SelectTreeFolder(ArchiveFolder folder)
    {
        var path = new Stack<ArchiveFolder>();
        for (ArchiveFolder? ancestor = folder; ancestor is not null; ancestor = ancestor.Parent)
            path.Push(ancestor);
        while (path.TryPop(out ArchiveFolder? ancestor))
        {
            TreeNode node = folderNodes[ancestor];
            if (path.Count > 0)
            {
                PopulateTreeNode(node);
                node.Expand();
            }
            else
            {
                folders.SelectedNode = node;
                node.EnsureVisible();
            }
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────

    private void ActivateSelection()
    {
        ArchiveRow[] rows = SelectedRows();
        if (rows is not [var row])
            return;
        if (row.Folder is { } folder)
            Navigate(folder);
        else
            OpenSelectedFile(chooseApp: false);
    }

    private void GoUp()
    {
        if (search.TextLength > 0)
        {
            search.Clear();
            filterDelay.Stop();
            RefreshFileList();
        }
        else if (currentFolder?.Parent is { } parent)
            Navigate(parent);
    }

    private void NavigateToDepth(int depth)
    {
        if (currentFolder is null)
            return;
        if (showingSearch)
        {
            GoUp();
            return;
        }
        var chain = new List<ArchiveFolder>();
        for (ArchiveFolder? folder = currentFolder; folder is not null; folder = folder.Parent)
            chain.Insert(0, folder);
        if ((uint)depth < (uint)chain.Count)
            Navigate(chain[depth]);
    }

    private void Navigate(ArchiveFolder folder)
    {
        loading = true;
        synchronizingTree = true;
        try
        {
            currentFolder = folder;
            search.Clear();
            filterDelay.Stop();
            SelectTreeFolder(folder);
        }
        finally
        {
            loading = false;
            synchronizingTree = false;
        }
        RefreshFileList();
    }

    // ── Opening and compressing ────────────────────────────────────────

    private void ChooseArchive()
    {
        if (jobStatus.IsRunning)
            return;
        using var picker = new OpenFileDialog
        {
            Title = "打开压缩包",
            Filter = $"常见压缩包|{string.Join(';', ArchiveFormat.ReadExtensions.Select(ext => "*" + ext))}|所有文件|*.*"
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            OpenArchive(picker.FileName);
    }

    private void ChooseFiles()
    {
        using var picker = new OpenFileDialog { Title = "选择要压缩的文件", Multiselect = true, Filter = "所有文件|*.*" };
        if (picker.ShowDialog(this) == DialogResult.OK)
            ShowCompression(picker.FileNames);
    }

    private void ChooseFolder()
    {
        using var picker = new FolderBrowserDialog { Description = "选择要压缩的文件夹", UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) == DialogResult.OK)
            ShowCompression([picker.SelectedPath]);
    }

    private void ShowCompression(string[] sources)
    {
        if (jobStatus.IsRunning)
            return;
        using var dialog = new CompressionDialog(sources);
        dialog.ShowDialog(this);
    }

    /// <summary>True when this window shows <paramref name="path"/>.</summary>
    public bool Shows(string path) =>
        archive is not null && string.Equals(archive.OriginalPath, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the window shows the start screen and is free to take an archive.</summary>
    public bool IsIdle => archive is null && !jobStatus.IsRunning && CanFocus && !IsDisposed;

    public void OpenPath(string path) => OpenArchive(path);

    private void OpenArchive(string path)
    {
        if (jobStatus.IsRunning)
            return;
        if (opened.Pending().Count > 0)
        {
            // Settle edits to the current archive before replacing it.
            BeginInvoke(async () =>
            {
                if (await opened.CheckAsync(explicitAction: true))
                    OpenArchive(path);
            });
            return;
        }
        if (!File.Exists(path))
        {
            settings.Recent.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            JobDialog.ShowError(this, new ArchiveException($"找不到文件：\n{path}"));
            RefreshFileList();
            return;
        }
        var candidate = new ArchiveSession(path);
        string? secret = null;
        var result = Program.WithPassword<ArchiveContents>(this, "打开压缩包", null,
            (attempt, _, progress, token) => ArchiveEngine.ReadContentsAsync(candidate, null, attempt, progress, token),
            accepted => secret = accepted);
        if (result.Cancelled || result.Error is not null)
        {
            candidate.Dispose();
            if (result.Error is not null)
                JobDialog.ShowError(this, result.Error);
            return;
        }

        opened.Clear();
        archive?.Dispose();
        archive = candidate;
        password = secret;
        // Listing needed the password, so file names are encrypted; keep them that way when editing.
        namesEncrypted = secret is not null;
        writable = ArchiveEngine.CanModify(candidate);
        codePage = null;
        lastExtracted = null;
        Text = Path.GetFileName(candidate.OriginalPath);
        settings.Remember(candidate.OriginalPath);
        settings.Save();
        loading = true;
        encoding.SelectedIndex = 0;
        search.Clear();
        loading = false;
        ResetBrowser(result.Value!);
        files.Focus();
    }

    private void ChangeEncoding()
    {
        if (archive is null || loading || encoding.SelectedItem is not EncodingOption choice)
            return;
        ArchiveSession session = archive;
        var result = JobDialog.Execute(this, "重新读取文件列表",
            (_, progress, token) => ArchiveEngine.ReadContentsAsync(session, choice.CodePage, password, progress, token));
        if (!result.Cancelled && result.Error is null)
        {
            codePage = choice.CodePage;
            ResetBrowser(result.Value!, currentFolder?.Path ?? "");
            return;
        }
        loading = true;
        encoding.SelectedItem = encoding.Items.Cast<EncodingOption>().First(item => item.CodePage == codePage);
        loading = false;
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
    }

    // ── List ───────────────────────────────────────────────────────────

    private void RefreshFileList()
    {
        if (archive is not { } session || contents is not { } catalogue || currentFolder is not { } folder)
        {
            browser.Visible = false;
            welcome.ShowRecent(settings.Recent, !settings.AssociationHintDismissed && !FileAssociation.IsRegistered);
            welcome.Visible = true;
            location.SetSegments([]);
            count.Text = "就绪";
            UpdateButtons();
            return;
        }

        string query = search.Text.Trim();
        showingSearch = query.Length > 0;
        IEnumerable<ArchiveRow> rows = showingSearch ? catalogue.Search(query) :
            folder.Folders.Values.Select(item => item.AsRow()).Concat(folder.Files);
        visibleFiles = rows.ToArray();
        Array.Sort(visibleFiles, CompareRows);
        files.BeginUpdate();
        files.SelectedIndices.Clear();
        files.VirtualListSize = visibleFiles.Length;
        files.EndUpdate();
        Native.SetSortArrow(files, sortColumn, sortDescending);
        if (visibleFiles.Length > 0)
            files.EnsureVisible(0);
        welcome.Visible = false;
        browser.Visible = true;

        string archiveName = Path.GetFileName(session.OriginalPath);
        var segments = new List<string> { archiveName };
        if (showingSearch)
            segments.Add($"搜索“{query}”");
        else if (folder.Path.Length > 0)
            segments.AddRange(folder.Path.Split('/'));
        location.SetSegments(segments);
        pathTooltip.SetToolTip(location, session.OriginalPath + (folder.Path.Length == 0 ? "" : "\n" + folder.Path));

        count.IsLink = false;
        count.ForeColor = BrowserAppearance.Muted;
        count.Text = showingSearch ? $"找到 {visibleFiles.Length:N0} 项" :
            $"{visibleFiles.Length:N0} 项  ·  共 {catalogue.TotalEntries:N0} 个条目  ·  {Summary(session.OriginalPath, catalogue)}";
        UpdateButtons();
    }

    private static string Summary(string path, ArchiveContents catalogue)
    {
        long packed = 0;
        try { packed = new FileInfo(path).Length; }
        catch (IOException) { }
        long unpacked = catalogue.TotalSize;
        if (unpacked <= 0 || packed <= 0)
            return $"压缩包 {FormatSize(packed)}";
        string summary = $"压缩包 {FormatSize(packed)} · 解压后 {FormatSize(unpacked)}";
        // Tiny or already-compressed content can make the archive larger; a ratio would only confuse then.
        return packed < unpacked ? $"{summary}（节省 {100 - packed * 100d / unpacked:0}%）" : summary;
    }

    private int CompareRows(ArchiveRow left, ArchiveRow right)
    {
        int group = (left.Folder is null).CompareTo(right.Folder is null);
        if (group != 0)
            return group;
        var text = StringComparer.CurrentCultureIgnoreCase;
        int order = sortColumn switch
        {
            1 => text.Compare(Describe(left).Type, Describe(right).Type),
            2 => Nullable.Compare(left.Item?.Size, right.Item?.Size),
            3 => StringComparer.Ordinal.Compare(left.Item?.Modified, right.Item?.Modified),
            _ => NaturalCompare(left.Name, right.Name)
        };
        if (order == 0)
            order = NaturalCompare(left.Path, right.Path);
        return sortDescending ? -Math.Sign(order) : order;
    }

    // Sorts "第2集" before "第10集", as Explorer does.
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string left, string right);
    private static int NaturalCompare(string left, string right) => StrCmpLogicalW(left, right);

    private void UpdateButtons()
    {
        bool opened = archive is not null;
        bool ready = opened && !jobStatus.IsRunning;
        int countSelected = files.SelectedIndices.Count;
        selected.Enabled = ready && countSelected > 0;
        extract.Enabled = ready;
        test.Enabled = ready;
        add.Enabled = ready && writable;
        remove.Enabled = ready && writable && countSelected > 0;
        add.ToolTipText = opened && !writable ? "这种格式的压缩包不支持直接修改" : "向当前文件夹添加文件（也可以直接拖进来）";
        up.Enabled = opened && (currentFolder?.Parent is not null || search.TextLength > 0);
        search.Enabled = opened;
        location.Enabled = opened;
        if (archive is { } session)
        {
            extractFolder.Text = $"解压到「{ArchiveFormat.BaseName(session.OriginalPath)}\\」";
            extractHere.Text = $"解压到当前文件夹（{Path.GetFileName(Path.GetDirectoryName(session.OriginalPath))}）";
        }
        bool zipNames = archive?.OriginalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
        encoding.Visible = zipNames;
        int encodingWidth = zipNames ? 170 : 0;
        if (navigation.ColumnStyles[2].Width != encodingWidth)
            navigation.ColumnStyles[2].Width = encodingWidth;
        encoding.Enabled = ready && zipNames;
        selectionCount.Text = countSelected > 0 ? $"已选 {countSelected:N0} 项" : "";
    }

    private ArchiveRow[] SelectedRows() => files.SelectedIndices.Cast<int>()
        .Where(index => (uint)index < (uint)visibleFiles.Length).Select(index => visibleFiles[index]).ToArray();

    private static string[] ArchivePaths(IEnumerable<ArchiveRow> rows) =>
        rows.SelectMany(row => row.GetArchivePaths()).Distinct(StringComparer.Ordinal).ToArray();

    private void CopyNames()
    {
        string[] names = SelectedRows().Select(row => row.Name).ToArray();
        if (names.Length > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, names));
    }

    // ── Extraction ─────────────────────────────────────────────────────

    private async Task ExtractItemsAsync(bool onlySelected)
    {
        if (jobStatus.IsRunning || archive is not { } session)
            return;
        string[]? selection = onlySelected ? ArchivePaths(SelectedRows()) : null;
        if (selection is { Length: 0 })
            return;
        using var picker = new FolderBrowserDialog
        {
            Description = "选择解压位置", UseDescriptionForTitle = true,
            InitialDirectory = Path.GetDirectoryName(session.OriginalPath)!
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        await RunExtractionAsync(session, picker.SelectedPath, false, selection);
    }

    private async Task ExtractBesideAsync(bool newFolder)
    {
        if (jobStatus.IsRunning || archive is not { } session)
            return;
        string parent = Path.GetDirectoryName(session.OriginalPath)!;
        string target = newFolder ? Path.Combine(parent, ArchiveFormat.BaseName(session.OriginalPath)) : parent;
        await RunExtractionAsync(session, target, newFolder, null);
    }

    private async Task RunExtractionAsync(ArchiveSession session, string destination, bool newFolder, string[]? selection)
    {
        var result = await Program.WithPasswordAsync<ExtractResult>(this, password, secret =>
        {
            var running = jobStatus.RunAsync((progress, token) => ArchiveEngine.ExtractAsync(session, destination, newFolder,
                codePage, secret, selection, progress, () => JobDialog.AskConflict(this), token,
                smart: newFolder && settings.SmartExtract));
            UpdateButtons();
            return running;
        }, accepted => password = accepted);
        UpdateButtons();
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
        else if (!result.Cancelled)
        {
            ExtractResult completed = result.Value!;
            lastExtracted = completed.Directory;
            count.Text = (completed.Skipped == 0 ? $"✓ 已解压到 {completed.Directory}" :
                $"✓ 已解压到 {completed.Directory}，跳过 {completed.Skipped} 个同名项目") + "  ·  点击打开";
            count.IsLink = true;
            if (settings.OpenFolderAfterExtract && Directory.Exists(completed.Directory))
                Process.Start(new ProcessStartInfo(completed.Directory) { UseShellExecute = true })?.Dispose();
        }
    }

    private void Test()
    {
        if (archive is not { } session)
            return;
        var result = Program.WithPassword<bool>(this, "测试压缩包", password,
            async (secret, _, progress, token) =>
            {
                await ArchiveEngine.TestAsync(session, codePage, secret, progress, token);
                return true;
            }, accepted => password = accepted);
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
        else if (!result.Cancelled)
            MessageBox.Show(this, "可读取的条目均通过完整性检查。", "ZIP",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Extracts the selected entries to a private temporary folder that lives until the window closes.</summary>
    private (string Root, string[] Files)? Stage(string title, ArchiveRow[] rows)
    {
        if (archive is not { } session)
            return null;
        string[] paths = ArchivePaths(rows);
        if (paths.Length == 0)
            return null;
        var temp = new TempDirectory();
        var result = Program.WithPassword<bool>(this, title, password,
            async (secret, _, progress, token) =>
            {
                await ArchiveEngine.ExtractToStageAsync(session, temp.Path, codePage, secret, paths, progress, token);
                return true;
            }, accepted => password = accepted);
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
        if (result.Cancelled || result.Error is not null)
        {
            temp.Dispose();
            return null;
        }

        var local = new List<string>();
        string root = Path.TrimEndingDirectorySeparator(temp.Path) + Path.DirectorySeparatorChar;
        foreach (ArchiveRow row in rows)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(temp.Path, row.Item?.Path ?? row.Path)); }
            catch (ArgumentException) { continue; }
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.Exists(candidate))
                local.Add(candidate);
        }
        staged.Add(temp);
        return (temp.Path, local.ToArray());
    }

    private void OpenSelectedFile(bool chooseApp)
    {
        if (jobStatus.IsRunning || SelectedRows() is not [{ Folder: null } row])
            return;
        if (!chooseApp && settings.ConfirmRunnable && RunnableExtensions.Contains(Path.GetExtension(row.Name)) &&
            MessageBox.Show(this, $"「{row.Name}」是可执行文件，打开后会直接运行。\n\n只运行你信任来源的程序。要继续吗？",
                "运行压缩包中的程序", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (Stage("正在打开文件", [row]) is not (string stageRoot, [string local]))
            return;
        bool watching = writable && settings.OfferWriteBack;
        if (watching)
            opened.Track(stageRoot, local, row.Name);
        try
        {
            try
            {
                if (chooseApp)
                    OpenWith(local);
                else
                    Process.Start(new ProcessStartInfo(local)
                    {
                        UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(local)!
                    })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception error) when (!chooseApp && error.NativeErrorCode == 1155)
            {
                OpenWith(local);
            }
            count.IsLink = false;
            count.Text = watching ? $"已打开 {row.Name} · 保存修改后会询问是否更新到压缩包"
                : $"已打开 {row.Name}（临时副本，修改不会写回压缩包）";
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            if (error.NativeErrorCode != 1223)
                JobDialog.ShowError(this, error);
        }
    }

    private static void OpenWith(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"));
        start.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }

    private void DragToExplorer()
    {
        if (jobStatus.IsRunning || archive is null)
            return;
        if (Stage("准备拖出的文件", SelectedRows()) is not (_, { Length: > 0 } drop))
            return;
        draggingOut = true;
        try { DoDragDrop(new DataObject(DataFormats.FileDrop, drop), DragDropEffects.Copy); }
        finally { draggingOut = false; }
    }

    // ── Editing the archive ────────────────────────────────────────────

    private EditOptions Options(string? secret) => new(codePage, secret, namesEncrypted);

    /// <summary>Runs one change against the open archive with the inline progress bar, then reloads the listing.</summary>
    private async Task<bool> ModifyAsync(
        Func<ArchiveSession, EditOptions, IProgress<JobProgress>, CancellationToken, Task> change, bool writesFiles = false)
    {
        if (jobStatus.IsRunning || archive is not { } session || contents is not { } viewed || !writable)
            return false;
        var result = await Program.WithPasswordAsync<bool>(this, password, secret =>
        {
            var options = Options(secret);
            var running = jobStatus.RunAsync(async (progress, token) =>
            {
                await ArchiveEngine.EditTransactionAsync(session, viewed, options, writesFiles,
                    stagedArchive => change(stagedArchive, options, progress, token), progress, token);
                return true;
            });
            UpdateButtons();
            return running;
        }, accepted => password = accepted);
        UpdateButtons();
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
        if (result.Cancelled || result.Error is not null)
            return false;
        await ReloadAsync(session);
        return true;
    }

    private async Task ReloadAsync(ArchiveSession session)
    {
        string folder = currentFolder?.Path ?? "";
        var result = await jobStatus.RunAsync((progress, token) =>
            ArchiveEngine.ReadContentsAsync(session, codePage, password, progress, token));
        UpdateButtons();
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
        else if (!result.Cancelled && archive == session)
            ResetBrowser(result.Value!, folder);
    }

    private async Task<bool> WriteBackAsync(IReadOnlyList<OpenedFiles.Entry> changed)
    {
        if (archive is not { } session)
            return false;
        string names = string.Join("\n", changed.Take(6).Select(entry => "· " + entry.Name)) +
            (changed.Count > 6 ? $"\n…以及另外 {changed.Count - 6} 个文件" : "");
        if (MessageBox.Show(this, $"以下文件已在其他程序中修改：\n\n{names}\n\n要把修改更新到「{Path.GetFileName(session.OriginalPath)}」吗？",
                "更新压缩包", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return true;
        bool done = await ModifyAsync(async (working, options, progress, token) =>
        {
            foreach (var group in changed.GroupBy(entry => entry.StageRoot, StringComparer.OrdinalIgnoreCase))
                await ArchiveEngine.AddAsync(working, group.Key, group.Select(entry => entry.RelativePath).ToArray(),
                    options, progress, token);
        }, writesFiles: true);
        if (done)
        {
            count.IsLink = false;
            count.Text = $"✓ 已更新压缩包中的 {changed.Count} 个文件";
        }
        return done;
    }

    private void ChooseFilesToAdd(bool folders)
    {
        if (!writable || jobStatus.IsRunning)
            return;
        string[]? picked = null;
        if (folders)
        {
            using var picker = new FolderBrowserDialog { Description = "选择要添加的文件夹", UseDescriptionForTitle = true, Multiselect = true };
            if (picker.ShowDialog(this) == DialogResult.OK)
                picked = picker.SelectedPaths;
        }
        else
        {
            using var picker = new OpenFileDialog { Title = "选择要添加的文件", Multiselect = true, Filter = "所有文件|*.*" };
            if (picker.ShowDialog(this) == DialogResult.OK)
                picked = picker.FileNames;
        }
        if (picked is { Length: > 0 })
            _ = AddFromDiskAsync(picked, confirm: false);
    }

    private async Task AddFromDiskAsync(string[] sources, bool confirm)
    {
        if (archive is not { } session || contents is not { } catalogue || currentFolder is not { } folder)
            return;
        if (sources.Select(Path.GetFullPath).Any(path =>
            string.Equals(path, session.OriginalPath, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(path) && session.OriginalPath.StartsWith(
                Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
        {
            JobDialog.ShowError(this, new ArchiveException("不能把压缩包本身或包含它的目录添加到压缩包。"));
            return;
        }
        var rootNames = new HashSet<string>(catalogue.Root.Folders.Keys.Concat(catalogue.Root.Files.Select(file => file.Name)),
            StringComparer.OrdinalIgnoreCase);
        var folderNames = new HashSet<string>(folder.Folders.Keys.Concat(folder.Files.Select(file => file.Name)),
            StringComparer.OrdinalIgnoreCase);
        string where = folder.Path.Length == 0 ? Path.GetFileName(session.OriginalPath)
            : $"{Path.GetFileName(session.OriginalPath)} › {folder.Path.Replace("/", " › ")}";
        string[] replaced = sources.Select(Path.GetFileName).Where(name => name is not null && folderNames.Contains(name)).ToArray()!;
        if (confirm || replaced.Length > 0)
        {
            string message = $"把 {sources.Length} 个项目添加到「{where}」？" +
                (replaced.Length > 0 ? $"\n\n将替换同名项目：{string.Join("、", replaced.Take(5))}{(replaced.Length > 5 ? " 等" : "")}" : "");
            if (MessageBox.Show(this, message, "添加到压缩包", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }
        if (await ModifyAsync((working, options, progress, token) => ArchiveEngine.AddFromDiskAsync(working, sources,
                folder.Path, rootNames, folderNames, options, progress, token), writesFiles: true))
        {
            count.IsLink = false;
            count.Text = $"✓ 已添加 {sources.Length} 个项目";
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (!writable || jobStatus.IsRunning || archive is not { } session)
            return;
        ArchiveRow[] rows = SelectedRows();
        string[] paths = ArchivePaths(rows);
        if (paths.Length == 0)
            return;
        string what = rows.Length == 1 ? $"「{rows[0].Name}」" : $"这 {rows.Length} 个项目";
        if (MessageBox.Show(this, $"从压缩包中永久删除{what}？\n\n此操作无法撤销。", "删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (await ModifyAsync((working, options, progress, token) => ArchiveEngine.DeleteAsync(working, paths, options, progress, token)))
        {
            count.IsLink = false;
            count.Text = $"✓ 已删除 {rows.Length} 个项目";
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (!writable || jobStatus.IsRunning || showingSearch || archive is not { } session ||
            SelectedRows() is not [var row] || currentFolder is not { } folder)
            return;
        var siblings = new HashSet<string>(folder.Folders.Keys.Concat(folder.Files.Select(file => file.Name)),
            StringComparer.OrdinalIgnoreCase);
        char[] invalid = Path.GetInvalidFileNameChars();
        string? name = InputPrompt.Ask(this, "重命名", row.Folder is null ? "新的文件名：" : "新的文件夹名：", row.Name, text =>
            text.Length == 0 ? "名称不能为空。" :
            text.IndexOfAny(invalid) >= 0 ? "名称不能包含 \\ / : * ? \" < > | 等字符。" :
            text is "." or ".." ? "名称无效。" :
            !string.Equals(text, row.Name, StringComparison.OrdinalIgnoreCase) && siblings.Contains(text) ? "这个文件夹里已有同名项目。" :
            null);
        if (name is null)
            return;
        string from = row.Folder is { } renamed ? renamed.Item?.Path ?? renamed.Path.Replace('/', '\\') : row.Item!.Path;
        string parent = folder.Path.Replace('/', '\\');
        string to = parent.Length == 0 ? name : parent + "\\" + name;
        if (await ModifyAsync((working, options, progress, token) => ArchiveEngine.RenameAsync(working, [(from, to)], options, progress, token)))
        {
            count.IsLink = false;
            count.Text = $"✓ 已重命名为 {name}";
        }
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog();
        dialog.ShowDialog(this);
        RefreshFileList();
    }

    private void RevealArchive()
    {
        if (archive is { } session && File.Exists(session.OriginalPath))
            Process.Start("explorer.exe", $"/select,\"{session.OriginalPath}\"")?.Dispose();
    }

    private void ShowAbout()
    {
        string version = Application.ProductVersion.Split('+')[0];
        MessageBox.Show(this, $"ZIP {version}\n\n压缩引擎：7-Zip（Igor Pavlov），按 LGPL、BSD 和 unRAR 许可使用。",
            "关于 ZIP", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Formatting ─────────────────────────────────────────────────────

    private static string FormatDate(string? modified)
    {
        if (string.IsNullOrEmpty(modified))
            return "";
        return modified.Length > 16 ? modified[..16] : modified;
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is not long size)
            return "";
        if (size < 1024)
            return $"{size} B";
        if (size < 1024 * 1024)
            return $"{size / 1024d:N1} KB";
        if (size < 1024L * 1024 * 1024)
            return $"{size / (1024d * 1024):N1} MB";
        return $"{size / (1024d * 1024 * 1024):N2} GB";
    }
}
