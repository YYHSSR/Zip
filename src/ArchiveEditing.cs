namespace QuickZip;

/// <summary>Options that change an existing archive in place.</summary>
internal sealed record EditOptions(int? CodePage, string? Password, bool EncryptNames);

internal static partial class ArchiveEngine
{
    private static readonly Dictionary<string, string> WritableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = "zip", [".7z"] = "7z", [".tar"] = "tar", [".wim"] = "wim"
    };

    /// <summary>
    /// Archives that 7-Zip can update in place. Compressed TAR, single-stream, RAR, ISO and split archives
    /// are read-only here: changing them would mean rebuilding the whole file in a different format.
    /// </summary>
    public static bool CanModify(ArchiveSession session) =>
        WritableTypes.ContainsKey(Path.GetExtension(session.OriginalPath)) &&
        !ArchiveFormat.Compound.Any(ext => session.OriginalPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string WritableType(ArchiveSession session) =>
        CanModify(session) ? WritableTypes[Path.GetExtension(session.OriginalPath)]
            : throw new ArchiveException("这种格式的压缩包不支持直接修改。可以先解压，再重新压缩。");

    public static async Task EditTransactionAsync(ArchiveSession session, ArchiveContents contents, EditOptions options,
        bool writesFiles, Func<ArchiveSession, Task> edit, IProgress<JobProgress> progress, CancellationToken token)
    {
        string original = session.OriginalPath;
        token.ThrowIfCancellationRequested();
        contents.Revision.RequireCurrent(original);
        bool verifyPassword = writesFiles && (contents.HasEncryptedFiles || options.EncryptNames);
        if (verifyPassword && string.IsNullOrEmpty(options.Password))
            throw new ArchiveException("写入加密压缩包前，请输入原压缩包密码。", needsPassword: true);
        using var stage = TempDirectory.NextToOutput(Path.GetDirectoryName(original)!);
        using var working = new ArchiveSession(Path.Combine(stage.Path, Path.GetFileName(original)));
        await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            contents.Revision.RequireMatch(ArchiveRevision.Read(input.SafeFileHandle));
            if (verifyPassword)
            {
                await TestAsync(session, options.CodePage, options.Password, progress, token);
                contents.Revision.RequireCurrent(original);
            }

            progress.Report(new("正在准备编辑…"));
            await using (var output = new FileStream(working.OriginalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, token);

            await edit(working);
            contents.Revision.RequireCurrent(original);
            CopyZone(original, working.OriginalPath);
        }
        token.ThrowIfCancellationRequested();
        contents.Revision.RequireCurrent(original);
        File.Move(working.OriginalPath, original, overwrite: true);
    }

    private static List<string> EditArguments(string command, ArchiveSession session, EditOptions options, bool adding)
    {
        string type = WritableType(session);
        var args = new List<string> { command, "-t" + type, "-bsp1", "-spd" };
        if (options.CodePage.HasValue)
            args.Add("-mcp=" + options.CodePage.Value);
        if (!string.IsNullOrEmpty(options.Password))
        {
            if (adding && type == "zip" && (options.Password.Length > 99 || options.Password.Any(ch => ch is < ' ' or > '~')))
                throw new ArchiveException("7-Zip 引擎写入加密 ZIP 时只支持最多 99 个可打印 ASCII 字符的密码。");
            args.Add("-p" + options.Password);
            if (adding && type == "zip")
                args.Add("-mem=AES256");
            if (type == "7z" && options.EncryptNames)
                args.Add("-mhe=on");
        }
        return args;
    }

    /// <summary>Adds or replaces entries. Each relative path under <paramref name="root"/> is stored at that same path.</summary>
    public static async Task AddAsync(ArchiveSession session, string root, IReadOnlyList<string> relativePaths,
        EditOptions options, IProgress<JobProgress> progress, CancellationToken token)
    {
        if (relativePaths.Count == 0)
            return;
        var args = EditArguments("a", session, options, adding: true);
        using var lists = new TempDirectory();
        args.Add("-i@" + await WriteListAsync(lists.Path, relativePaths, token));
        args.AddRange(["--", session.OriginalPath]);
        await RunAsync(args, root, token, onLine: Report(progress, "正在更新压缩包…"));
    }

    public static async Task DeleteAsync(ArchiveSession session, IReadOnlyList<string> archivePaths, EditOptions options,
        IProgress<JobProgress> progress, CancellationToken token)
    {
        if (archivePaths.Count == 0)
            return;
        var args = EditArguments("d", session, options, adding: false);
        using var lists = new TempDirectory();
        args.Add("-i@" + await WriteListAsync(lists.Path, archivePaths, token));
        args.AddRange(["--", session.OriginalPath]);
        await RunAsync(args, null, token, onLine: Report(progress, "正在删除…"));
    }

    /// <summary>Renames entries; renaming a folder renames everything inside it too.</summary>
    public static async Task RenameAsync(ArchiveSession session, IReadOnlyList<(string From, string To)> pairs,
        EditOptions options, IProgress<JobProgress> progress, CancellationToken token)
    {
        if (pairs.Count == 0)
            return;
        if (pairs.Any(pair => pair.From.Contains('\n') || pair.To.Contains('\n')))
            throw new ArchiveException("名称不能包含换行符。");
        var args = EditArguments("rn", session, options, adding: false);
        args.AddRange(["--", session.OriginalPath]);
        foreach (var (from, to) in pairs)
            args.AddRange([from, to]);
        await RunAsync(args, null, token, onLine: Report(progress, "正在重命名…"));
    }

    /// <summary>
    /// Adds files and folders from disk into <paramref name="folder"/> inside the archive ("" is the root).
    /// Sources go in from where they are when possible; otherwise they are copied to a staging tree first.
    /// </summary>
    public static async Task AddFromDiskAsync(ArchiveSession session, IReadOnlyList<string> sources, string folder,
        ISet<string> rootNames, ISet<string> folderNames, EditOptions options, IProgress<JobProgress> progress,
        CancellationToken token)
    {
        string[] paths = sources.Select(Path.GetFullPath).Distinct(FileNames).ToArray();
        if (paths.Any(path => !Path.Exists(path)))
            throw new ArchiveException("至少有一个所选项目已不存在。");
        if (paths.Any(path => FileNames.Equals(path, session.OriginalPath)))
            throw new ArchiveException("不能把压缩包添加到它自己里面。");
        if (folder.Length > 0 && folder.Split('/', '\\').Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArchiveException("压缩包内的当前目录无法作为 Windows 文件路径。");

        // One 7-Zip call per source directory, so items keep their own names.
        foreach (var group in paths.GroupBy(path => Path.GetDirectoryName(path)!, FileNames))
        {
            string[] names = group.Select(Path.GetFileName).ToArray()!;
            bool direct = folder.Length == 0 ||
                names.All(name => !rootNames.Contains(name) && !folderNames.Contains(name));
            if (direct)
            {
                await AddAsync(session, group.Key, names, options, progress, token);
                if (folder.Length > 0)
                    await RenameAsync(session, names.Select(name => (name, $"{folder}\\{name}")).ToArray(),
                        options, progress, token);
                continue;
            }

            // A root name would collide with the rename trick, or the target already exists and must be replaced.
            using var stage = new TempDirectory();
            string target = Path.Combine(stage.Path, folder.Replace('/', '\\'));
            Directory.CreateDirectory(target);
            progress.Report(new("正在准备要添加的文件…"));
            foreach (string source in group)
                CopyTree(source, Path.Combine(target, Path.GetFileName(source)), token);
            await AddAsync(session, stage.Path, names.Select(name => $"{folder.Replace('/', '\\')}\\{name}").ToArray(),
                options, progress, token);
        }
    }

    private static void CopyTree(string source, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(source))
        {
            File.Copy(source, target);
            return;
        }
        Directory.CreateDirectory(target);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                continue;
            CopyTree(entry, Path.Combine(target, Path.GetFileName(entry)), token);
        }
    }
}
