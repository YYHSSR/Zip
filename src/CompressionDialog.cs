namespace QuickZip;

internal sealed class CompressionDialog : Form
{
    private readonly string[] sources;
    private readonly ComboBox formats = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox levels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox output = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox encryptNames = new() { Text = "同时加密文件名", AutoSize = true };
    private readonly ArchiveFormat[] available;
    private readonly JobStatus jobStatus = new();
    private readonly Button start = new() { Text = "开始压缩", AutoSize = true, MinimumSize = new(96, 30) };
    private readonly Button browse = new() { Text = "浏览…", Dock = DockStyle.Fill };
    private readonly Button close = new() { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new(76, 30) };
    private string? lastSuggested;

    public CompressionDialog(string[] sources, bool preferOther = false)
    {
        this.sources = sources;
        available = ArchiveFormat.All.Where(format => !format.SingleFile || sources.Length == 1 && File.Exists(sources[0]))
            .ToArray();
        Text = "压缩文件";
        Font = SystemFonts.MessageBoxFont!;
        StartPosition = FormStartPosition.CenterParent;
        Icon = ZipWindow.Icon;
        MinimumSize = new(560, 360);
        ClientSize = new(670, 350);
        MaximizeBox = false;
        MinimizeBox = false;
        BrowserAppearance.Apply(this);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 3, RowCount = 8
        };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.Absolute, 88));
        layout.RowStyles.Add(new(SizeType.Absolute, 36));
        for (int i = 0; i < 5; i++)
            layout.RowStyles.Add(new(SizeType.Absolute, 34));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 42));

        var heading = new Label { Text = $"压缩 {sources.Length} 个项目", AutoSize = true, Font = new(Font, FontStyle.Bold) };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 3);
        AddField("格式", formats, 1);
        AddField("压缩级别", levels, 2);
        AddField("保存到", output, 3);
        AddField("密码", password, 4);
        layout.Controls.Add(encryptNames, 1, 5);

        jobStatus.Dock = DockStyle.None;
        jobStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        jobStatus.Height = 36;
        layout.Controls.Add(jobStatus, 0, 6);
        layout.SetColumnSpan(jobStatus, 3);
        browse.Click += (_, _) => Browse();
        layout.Controls.Add(browse, 2, 3);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(start);
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons, 0, 7);
        layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout);
        AcceptButton = start;
        CancelButton = close;

        formats.Items.AddRange(available);
        levels.Items.AddRange(["快速", "标准", "最高"]);
        levels.SelectedIndex = Math.Clamp(AppSettings.Current.CompressionLevel, 0, 2);
        formats.SelectedIndexChanged += (_, _) => FormatChanged();
        password.TextChanged += (_, _) => UpdateEncryption();
        formats.SelectedIndex = preferOther ? 3 : 0;
        start.Click += async (_, _) => await CompressAsync();
        FormClosing += (_, args) =>
        {
            if (!jobStatus.IsRunning)
                return;
            args.Cancel = true;
            jobStatus.Cancel();
        };

        void AddField(string name, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
    }

    private ArchiveFormat Format => (ArchiveFormat)formats.SelectedItem!;

    internal static string SuggestedPath(string[] items, ArchiveFormat format)
    {
        string parent = Path.GetDirectoryName(items[0]) ?? Path.GetPathRoot(items[0])!;
        string name = items.Length == 1 ? Path.GetFileName(items[0]) : Path.GetFileName(parent);
        if (items.Length == 1 && File.Exists(items[0]) && !format.SingleFile)
            name = Path.GetFileNameWithoutExtension(name);
        return Path.Combine(parent, (string.IsNullOrWhiteSpace(name) ? "压缩包" : name) + format.Extension);
    }

    private void FormatChanged()
    {
        string suggested = SuggestedPath(sources, Format);
        if (lastSuggested is not null && output.Text != lastSuggested)
        {
            string? previous = available.Select(f => f.Extension).OrderByDescending(ext => ext.Length)
                .FirstOrDefault(ext => output.Text.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
                output.Text = output.Text[..^previous.Length] + Format.Extension;
        }
        else
            output.Text = suggested;
        lastSuggested = suggested;
        levels.Enabled = Format.Type != "tar" || Format.Filter is not null;
        password.Enabled = Format.Type is "zip" or "7z";
        if (!password.Enabled)
            password.Clear();
        UpdateEncryption();
    }

    private void UpdateEncryption()
    {
        encryptNames.Enabled = Format == ArchiveFormat.SevenZip && password.TextLength > 0;
        if (!encryptNames.Enabled)
            encryptNames.Checked = false;
    }

    private void Browse()
    {
        using var picker = new SaveFileDialog
        {
            Title = "保存压缩包", Filter = $"{Format.Label}|*{Format.Extension}",
            AddExtension = false, OverwritePrompt = false, FileName = output.Text
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            output.Text = picker.FileName;
    }

    private async Task CompressAsync()
    {
        int[] presets = [1, 5, 9];
        string target = output.Text.Trim();
        if (target.Length == 0)
        {
            MessageBox.Show(this, "请输入保存路径。", "ZIP");
            return;
        }
        if (!target.EndsWith(Format.Extension, StringComparison.OrdinalIgnoreCase))
            target += Format.Extension;
        ArchiveFormat format = Format;
        int level = presets[levels.SelectedIndex];
        string? secret = password.TextLength == 0 ? null : password.Text;
        bool protectNames = encryptNames.Checked;
        start.Enabled = close.Enabled = formats.Enabled = browse.Enabled = output.Enabled = false;
        levels.Enabled = password.Enabled = encryptNames.Enabled = false;
        var result = await jobStatus.RunAsync((progress, token) =>
            ArchiveEngine.CompressAsync(sources, target, format, level, secret, protectNames, false, progress, token));
        start.Enabled = close.Enabled = formats.Enabled = browse.Enabled = output.Enabled = true;
        levels.Enabled = format.Type != "tar" || format.Filter is not null;
        password.Enabled = format.Type is "zip" or "7z";
        UpdateEncryption();
        if (result.Error is not null)
            JobDialog.ShowError(this, result.Error);
        else if (!result.Cancelled)
        {
            MessageBox.Show(this, $"已创建压缩包\n{result.Value}", "ZIP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
    }
}
