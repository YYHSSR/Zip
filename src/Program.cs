using System.Diagnostics;

namespace QuickZip;

internal static class Program
{
    private static readonly HashSet<string> ShellActions = ["zip", "7z", "other", "here", "folder", "to", "open"];

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args is ["--associate"] or ["--unassociate"])
            {
                // Used by Shell-Integration.ps1 so the registry layout lives in one place.
                if (args[0] == "--associate")
                    FileAssociation.Register();
                else
                    FileAssociation.Unregister();
                return;
            }
            if (args is ["--set-default"])
            {
                FileAssociation.MakeDefault(null);
                return;
            }
            if ((args.Length is 3 or 5) && args[0] == "--shell" && ShellActions.Contains(args[1]))
            {
                string selection = Path.GetFullPath(args[2]);
                if (!string.Equals(Path.GetDirectoryName(selection), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                    StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(selection).StartsWith("QuickZip-shell-", StringComparison.Ordinal) ||
                    !Path.GetExtension(selection).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("右键操作的文件列表无效。");
                string[] paths = File.ReadAllLines(selection);
                File.Delete(selection);
                Point? anchor = args.Length == 5 && int.TryParse(args[3], out int x) && int.TryParse(args[4], out int y)
                    ? new Point(x, y) : null;
                RunShell(args[1], paths, anchor);
            }
            else if (args.Length >= 2 && args[0].StartsWith("--", StringComparison.Ordinal) &&
                ShellActions.Contains(args[0][2..]))
                RunShell(args[0][2..], args[1..]);
            else
                RunWindows(args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray());
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            JobDialog.ShowError(null, error);
        }
    }

    /// <summary>Opens archive windows, in the already running ZIP process when there is one.</summary>
    private static void RunWindows(string[] paths)
    {
        using Mutex claim = SingleInstance.Claim(out bool primary);
        if (!primary && SingleInstance.TrySend(paths))
            return;
        var manager = new WindowManager();
        if (paths.Length == 0)
            manager.Open(null);
        foreach (string path in paths)
            manager.Open(path);
        using var stop = new CancellationTokenSource();
        // Creating the first window installed the WinForms synchronization context on this thread.
        if (primary && SynchronizationContext.Current is { } ui)
        {
            SingleInstance.Listen(ui, received =>
            {
                if (received.Length == 0)
                    manager.Open(null);
                foreach (string path in received)
                    manager.Open(path);
            }, stop.Token);
        }
        Application.Run(manager);
        stop.Cancel();
    }

    private static void RunShell(string action, string[] paths, Point? anchor = null)
    {
        if (paths.Length == 0)
            return;
        switch (action)
        {
            case "open":
                RunWindows(paths);
                return;
            case "other":
                using (var dialog = new CompressionDialog(paths, preferOther: true))
                {
                    ZipWindow.Place(dialog, null, anchor);
                    dialog.ShowDialog();
                }
                return;
            case "zip":
            case "7z":
                ArchiveFormat format = action == "zip" ? ArchiveFormat.Zip : ArchiveFormat.SevenZip;
                var result = JobDialog.Execute(null, "压缩文件", (_, progress, token) =>
                    ArchiveEngine.CompressAsync(paths, CompressionDialog.SuggestedPath(paths, format),
                        format, action == "zip" ? 1 : 3, null, false, true, progress, token), anchor);
                if (result.Error is not null)
                    JobDialog.ShowError(null, result.Error);
                return;
        }

        string? destination = null;
        if (action == "to")
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "选择解压位置", UseDescriptionForTitle = true,
                InitialDirectory = Path.GetDirectoryName(paths[0]) ?? ""
            };
            if (picker.ShowDialog() != DialogResult.OK)
                return;
            destination = picker.SelectedPath;
        }
        ExtractMany(action == "folder", paths, destination, anchor);
    }

    /// <summary>
    /// Extracts every selected archive in one progress window. A failed or password-less archive is
    /// reported at the end instead of stopping the others.
    /// </summary>
    private static void ExtractMany(bool newFolder, string[] archives, string? destination, Point? anchor)
    {
        AppSettings settings = AppSettings.Current;
        var errors = new List<string>();
        int skipped = 0, succeeded = 0;
        string? lastDirectory = null;
        string title = archives.Length == 1 ? "解压文件" : $"解压 {archives.Length} 个压缩包";
        var result = JobDialog.Execute(null, title, async (dialog, progress, token) =>
        {
            string? lastPassword = null;
            for (int index = 0; index < archives.Length; index++)
            {
                string path = archives[index];
                string name = Path.GetFileName(path);
                IProgress<JobProgress> step = archives.Length == 1 ? progress : new StepProgress(progress, index, archives.Length, name);
                using var session = new ArchiveSession(path);
                string parent = Path.GetDirectoryName(path)!;
                string output = newFolder ? Path.Combine(parent, ArchiveFormat.BaseName(path)) : destination ?? parent;
                // Archives selected together often share a password, so the last one that worked is tried first.
                string? secret = lastPassword;
                bool asked = false;
                while (true)
                {
                    try
                    {
                        ExtractResult done = await ArchiveEngine.ExtractAsync(session, output, newFolder, null, secret, null,
                            step, dialog.AskConflict, token, smart: newFolder && settings.SmartExtract);
                        skipped += done.Skipped;
                        lastDirectory = done.Directory;
                        lastPassword = secret ?? lastPassword;
                        succeeded++;
                        break;
                    }
                    catch (ArchiveException error) when (error.NeedsPassword)
                    {
                        secret = dialog.Invoke(() => AskPassword(dialog, asked, anchor, archives.Length > 1 ? name : null));
                        asked = true;
                        if (secret is null)
                        {
                            errors.Add($"{name}\n未输入密码，已跳过。");
                            break;
                        }
                    }
                    catch (Exception error) when (error is ArchiveException or IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{name}\n{error.Message}");
                        break;
                    }
                }
            }
            return true;
        }, anchor);

        if (result.Error is not null)
            JobDialog.ShowError(null, result.Error);
        if (errors.Count > 0)
            JobDialog.ShowError(null, new ArchiveException(
                (archives.Length > 1 ? $"{succeeded} 个成功，{errors.Count} 个未完成：\n\n" : "") +
                string.Join("\n\n", errors.Take(5)) + (errors.Count > 5 ? $"\n\n另有 {errors.Count - 5} 个任务失败。" : "")));
        if (skipped > 0)
            MessageBox.Show($"已跳过 {skipped} 个同名项目。", "ZIP", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (settings.OpenFolderAfterExtract && succeeded > 0)
        {
            string? folder = archives.Length == 1 ? lastDirectory : destination ?? Path.GetDirectoryName(archives[0]);
            if (folder is not null && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>Shows "(2/5) name · status" and maps each archive's 0–100% onto one overall bar.</summary>
    private sealed class StepProgress(IProgress<JobProgress> inner, int index, int total, string name) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => inner.Report(new($"（{index + 1}/{total}）{name} · {value.Status}",
            (index * 100 + (value.Percent ?? 0)) / total));
    }

    internal static JobResult<T> WithPassword<T>(IWin32Window? owner, string title, string? initial,
        Func<string?, JobDialog, IProgress<JobProgress>, CancellationToken, Task<T>> start,
        Action<string?>? remember = null, Point? anchor = null) =>
        WithPasswordAsync(owner, initial, password => Task.FromResult(JobDialog.Execute(owner, title,
            (dialog, progress, token) => start(password, dialog, progress, token), anchor)), remember, anchor)
            .GetAwaiter().GetResult();

    internal static async Task<JobResult<T>> WithPasswordAsync<T>(IWin32Window? owner, string? initial,
        Func<string?, Task<JobResult<T>>> start, Action<string?>? remember = null, Point? anchor = null)
    {
        string? password = initial;
        while (true)
        {
            var result = await start(password);
            if (result.Error is not ArchiveException { NeedsPassword: true })
            {
                if (result.Error is null && !result.Cancelled)
                    remember?.Invoke(password);
                return result;
            }
            password = AskPassword(owner, password is not null, anchor);
            if (password is null)
                return new(default, null, true);
        }
    }

    private static string? AskPassword(IWin32Window? owner, bool retry, Point? anchor, string? archiveName = null)
    {
        using var dialog = new Form
        {
            Text = "压缩包密码", Font = SystemFonts.MessageBoxFont!, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new(380, 122), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
        };
        string subject = archiveName is null ? "压缩包" : $"「{archiveName}」";
        var prompt = new Label
        {
            Text = retry ? $"无法解密{subject}，请重新输入密码。" : $"请输入{subject}的密码。",
            Location = new(14, 12), Size = new(350, 20), AutoEllipsis = true
        };
        var input = new TextBox { Location = new(14, 38), Width = 350, UseSystemPasswordChar = true, AccessibleName = "密码" };
        var ok = new Button { Text = "确定", Location = new(206, 78), DialogResult = DialogResult.OK, Enabled = false };
        var cancel = new Button { Text = "取消", Location = new(289, 78), DialogResult = DialogResult.Cancel };
        BrowserAppearance.Apply(dialog);
        input.TextChanged += (_, _) => ok.Enabled = input.TextLength > 0;
        dialog.Controls.AddRange([prompt, input, ok, cancel]);
        ZipWindow.Place(dialog, owner, anchor);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}
