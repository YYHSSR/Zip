namespace QuickZip;

internal sealed class SettingsDialog : Form
{
    private readonly AppSettings settings = AppSettings.Current;
    private readonly CheckBox smart = Option("智能解压：压缩包里只有一个顶层文件夹或文件时，「解压到同名文件夹」不再多套一层");
    private readonly CheckBox openFolder = Option("解压完成后打开目标文件夹");
    private readonly ComboBox level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly CheckBox confirmRunnable = Option("打开压缩包中的程序或脚本前先确认");
    private readonly CheckBox writeBack = Option("在压缩包中打开的文件被修改后，询问是否更新到压缩包");

    public SettingsDialog()
    {
        Text = "设置";
        Font = SystemFonts.MessageBoxFont!;
        Icon = ZipWindow.Icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BrowserAppearance.Apply(this);

        var column = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(20, 14, 20, 16), Dock = DockStyle.Fill
        };
        level.Items.AddRange(["快速", "标准", "最高"]);
        var levelRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 4, 0, 0) };
        levelRow.Controls.AddRange([new Label { Text = "「压缩为其他格式」的默认级别", AutoSize = true, Margin = new(0, 7, 10, 0) }, level]);

        var defaults = new PillButton { Text = "设为默认压缩软件…", Size = new(168, 34), Margin = new(0, 4, 10, 0) };
        defaults.Click += (_, _) => FileAssociation.MakeDefault(this);
        var reassociate = new PillButton { Text = "修复文件关联", Size = new(124, 34), Margin = new(0, 4, 0, 0) };
        reassociate.Visible = !Native.IsPackaged;
        reassociate.Click += (_, _) =>
        {
            try
            {
                FileAssociation.Register();
                MessageBox.Show(this, "已重新注册 ZIP 的文件关联。", "ZIP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                JobDialog.ShowError(this, error);
            }
        };
        var systemRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0) };
        systemRow.Controls.AddRange([defaults, reassociate]);

        var ok = new PillButton { Text = "确定", Primary = true, Size = new(92, 34), Margin = new(8, 0, 0, 0), DialogResult = DialogResult.OK };
        var cancel = new PillButton { Text = "取消", Size = new(92, 34), Margin = new(0), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 20, 0, 0) };
        buttons.Controls.AddRange([ok, cancel]);

        column.Controls.AddRange([
            Heading("解压"), smart, openFolder,
            Heading("压缩"), levelRow,
            Heading("打开压缩包中的文件"), confirmRunnable, writeBack,
            Heading("系统"), systemRow,
            buttons]);
        buttons.Width = column.PreferredSize.Width - 40;
        Controls.Add(column);
        AcceptButton = ok;
        CancelButton = cancel;

        smart.Checked = settings.SmartExtract;
        openFolder.Checked = settings.OpenFolderAfterExtract;
        level.SelectedIndex = Math.Clamp(settings.CompressionLevel, 0, 2);
        confirmRunnable.Checked = settings.ConfirmRunnable;
        writeBack.Checked = settings.OfferWriteBack;
        ok.Click += (_, _) => Commit();
    }

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = BrowserAppearance.Accent, Margin = new(0, 14, 0, 4),
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold)
    };

    private static CheckBox Option(string text) => new() { Text = text, AutoSize = true, Margin = new(2, 3, 0, 3) };

    private void Commit()
    {
        settings.SmartExtract = smart.Checked;
        settings.OpenFolderAfterExtract = openFolder.Checked;
        settings.CompressionLevel = level.SelectedIndex;
        settings.ConfirmRunnable = confirmRunnable.Checked;
        settings.OfferWriteBack = writeBack.Checked;
        settings.Save();
    }
}

internal static class InputPrompt
{
    /// <summary>Asks for one line of text. Returns null when cancelled or unchanged.</summary>
    public static string? Ask(IWin32Window owner, string title, string label, string initial, Func<string, string?> validate)
    {
        using var dialog = new Form
        {
            Text = title, Font = SystemFonts.MessageBoxFont!, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new(420, 128), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Icon = ZipWindow.Icon
        };
        BrowserAppearance.Apply(dialog);
        var prompt = new Label { Text = label, Location = new(16, 14), AutoSize = true };
        var input = new TextBox { Location = new(16, 40), Width = 388, Text = initial };
        var error = new Label { Location = new(16, 70), AutoSize = true, ForeColor = Color.Firebrick };
        var ok = new Button { Text = "确定", Location = new(236, 88), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Location = new(322, 88), DialogResult = DialogResult.Cancel };
        input.TextChanged += (_, _) =>
        {
            string? problem = validate(input.Text.Trim());
            error.Text = problem ?? "";
            ok.Enabled = problem is null && input.Text.Trim() != initial;
        };
        dialog.Controls.AddRange([prompt, input, error, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        ok.Enabled = false;
        dialog.Shown += (_, _) =>
        {
            input.Focus();
            // Select the name without its extension, as Explorer does.
            int dot = initial.LastIndexOf('.');
            input.Select(0, dot > 0 ? dot : initial.Length);
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }
}
