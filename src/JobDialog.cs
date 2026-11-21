namespace QuickZip;

internal sealed record JobResult<T>(T? Value, Exception? Error, bool Cancelled);

internal static class ZipWindow
{
    public static readonly Icon? Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

    public static void Place(Form form, IWin32Window? owner, Point? anchor)
    {
        form.Icon = Icon;
        form.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        if (anchor is not Point screenPoint)
            return;

        form.StartPosition = FormStartPosition.Manual;
        form.Location = screenPoint;
        form.Load += (_, _) =>
        {
            Rectangle area = Screen.FromPoint(screenPoint).WorkingArea;
            int left = Math.Clamp(screenPoint.X + 12, area.Left, Math.Max(area.Left, area.Right - form.Width));
            int top = Math.Clamp(screenPoint.Y + 12, area.Top, Math.Max(area.Top, area.Bottom - form.Height));
            form.Location = new(left, top);
        };
    }
}

internal sealed class JobStatus : TableLayoutPanel
{
    private readonly Label status = new()
    {
        AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Margin = new Padding(4, 6, 8, 6) };
    private readonly Button cancel = new() { Text = "取消", Dock = DockStyle.Fill };
    private CancellationTokenSource? running;

    public JobStatus()
    {
        Dock = DockStyle.Fill;
        ColumnCount = 3;
        RowCount = 1;
        Margin = new Padding(0);
        ColumnStyles.Add(new(SizeType.Absolute, 150));
        ColumnStyles.Add(new(SizeType.Percent, 100));
        ColumnStyles.Add(new(SizeType.Absolute, 78));
        bar.AccessibleName = "归档任务进度";
        Controls.Add(status, 0, 0);
        Controls.Add(bar, 1, 0);
        Controls.Add(cancel, 2, 0);
        Visible = false;
        cancel.Click += (_, _) => Cancel();
    }

    public bool IsRunning => running is not null;

    public void Cancel()
    {
        if (running is null || running.IsCancellationRequested)
            return;
        cancel.Enabled = false;
        status.Text = "正在取消…";
        running.Cancel();
    }

    public async Task<JobResult<T>> RunAsync<T>(Func<IProgress<JobProgress>, CancellationToken, Task<T>> operation)
    {
        if (IsRunning)
            throw new InvalidOperationException("已有任务正在运行。");

        using var tokenSource = new CancellationTokenSource();
        running = tokenSource;
        Visible = true;
        cancel.Enabled = true;
        bar.Style = ProgressBarStyle.Marquee;
        status.Text = "正在准备…";

        var progress = new Progress<JobProgress>(update =>
        {
            if (running != tokenSource || tokenSource.IsCancellationRequested)
                return;
            status.Text = update.Percent is int percent ? $"{update.Status.TrimEnd('…')} {percent}%" : update.Status;
            var style = update.Percent.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (bar.Style != style)
                bar.Style = style;
            bar.Value = update.Percent ?? 0;
        });
        try
        {
            return new(await Task.Run(() => operation(progress, tokenSource.Token)), null, false);
        }
        catch (OperationCanceledException) { return new(default, null, true); }
        catch (Exception error) { return new(default, error, false); }
        finally
        {
            running = null;
            Visible = false;
        }
    }
}

internal sealed class JobDialog : Form
{
    private readonly JobStatus progress = new();
    private bool complete;

    private JobDialog(string title, IWin32Window? owner, Point? anchor)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = owner is null;
        ClientSize = new(475, 62);
        AccessibleName = title;
        Padding = new Padding(12, 12, 12, 12);
        BrowserAppearance.Apply(this);
        Controls.Add(progress);
        ZipWindow.Place(this, owner, anchor);
    }

    public static JobResult<T> Execute<T>(IWin32Window? owner, string title,
        Func<JobDialog, IProgress<JobProgress>, CancellationToken, Task<T>> operation, Point? anchor = null)
    {
        using var dialog = new JobDialog(title, owner, anchor);
        JobResult<T> result = new(default, null, true);
        dialog.FormClosing += (_, args) =>
        {
            if (dialog.complete)
                return;
            args.Cancel = true;
            dialog.progress.Cancel();
        };
        dialog.Shown += async (_, _) =>
        {
            result = await dialog.progress.RunAsync((progress, token) => operation(dialog, progress, token));
            dialog.complete = true;
            dialog.Close();
        };
        dialog.ShowDialog(owner);
        return result;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            progress.Cancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public ConflictAction AskConflict() => AskConflict(this);

    public static ConflictAction AskConflict(Control owner)
    {
        if (owner.InvokeRequired)
            return owner.Invoke(() => AskConflict(owner));

        DialogResult choice = MessageBox.Show(owner,
            "目标目录已有同名文件或文件夹。\n\n是：保留两份，为新项目加编号\n否：跳过同名项目\n取消：停止合并",
            "处理同名项目", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return choice switch
        {
            DialogResult.Yes => ConflictAction.KeepBoth,
            DialogResult.No => ConflictAction.Skip,
            _ => ConflictAction.Cancel
        };
    }

    public static void ShowError(IWin32Window? owner, Exception error) =>
        MessageBox.Show(owner, error.Message, "ZIP", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
