using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickZip;

internal sealed record ArchiveFormat(string Label, string Extension, string Type, string? Filter = null,
    bool SingleFile = false)
{
    public override string ToString() => Label;

    public static readonly ArchiveFormat Zip = new("ZIP (.zip)", ".zip", "zip");
    public static readonly ArchiveFormat SevenZip = new("7z (.7z)", ".7z", "7z");
    public static readonly ArchiveFormat[] All =
    [
        Zip, SevenZip,
        new("TAR (.tar)", ".tar", "tar"),
        new("TAR + gzip (.tar.gz)", ".tar.gz", "tar", "gzip"),
        new("TAR + xz (.tar.xz)", ".tar.xz", "tar", "xz"),
        new("TAR + bzip2 (.tar.bz2)", ".tar.bz2", "tar", "bzip2"),
        new("gzip (.gz)", ".gz", "gzip", SingleFile: true),
        new("xz (.xz)", ".xz", "xz", SingleFile: true),
        new("bzip2 (.bz2)", ".bz2", "bzip2", SingleFile: true)
    ];

    public static readonly string[] Compound =
        [".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".tgz", ".txz", ".tbz", ".tbz2", ".tzst"];

    public static readonly string[] ReadExtensions =
    [
        ".zip", ".7z", ".rar", ".tar", ".tgz", ".txz", ".tbz", ".tbz2", ".tzst",
        ".gz", ".xz", ".bz2", ".zst", ".iso", ".cab", ".wim", ".deb", ".rpm", ".001"
    ];

    public static bool IsArchive(string path) =>
        ReadExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string BaseName(string path)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        string? extension = Compound.FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        string stem = extension is null ? Path.GetFileNameWithoutExtension(name) : name[..^extension.Length];
        return string.IsNullOrWhiteSpace(stem) || stem is "." or ".." ? "解压文件" : stem;
    }
}

internal sealed record ArchiveItem(string Path, bool Directory, long? Size, string Modified, bool Encrypted);
internal sealed record JobProgress(string Status, int? Percent = null);
internal enum ConflictAction { KeepBoth, Skip, Cancel }
internal sealed record ExtractResult(string Directory, int Skipped);
internal sealed class ArchiveException(string message, bool needsPassword = false) : IOException(message)
{
    public bool NeedsPassword { get; } = needsPassword;
}

internal sealed class ArchiveSession(string originalPath) : IDisposable
{
    private TempDirectory? expanded;
    private string? innerPath;

    public string OriginalPath { get; } = Path.GetFullPath(originalPath);

    private bool IsCompressedTar => ArchiveFormat.Compound.Any(ext =>
        OriginalPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public bool CanStreamTar => innerPath is null && IsCompressedTar && ArchiveEngine.ReadZone(OriginalPath) is null;

    public async Task<string> GetPathAsync(IProgress<JobProgress> progress, CancellationToken token)
    {
        if (!IsCompressedTar)
            return OriginalPath;
        if (innerPath is not null)
            return innerPath;

        progress.Report(new("正在解开 TAR 的压缩层…"));
        using var candidate = new TempDirectory();
        string path = Path.Combine(candidate.Path, ArchiveFormat.BaseName(OriginalPath) + ".tar");
        await ArchiveEngine.UnwrapAsync(OriginalPath, path, token);
        ArchiveEngine.CopyZone(OriginalPath, path);
        innerPath = path;
        expanded = candidate.Transfer();
        return innerPath;
    }

    public void Dispose() => expanded?.Dispose();
}

internal sealed class TempDirectory : IDisposable
{
    private bool ownsPath = true;
    public string Path { get; }

    public TempDirectory() => Path = Directory.CreateTempSubdirectory("QuickZip-").FullName;

    // Creating it under the output directory lets extraction finish with same-volume moves.
    public static TempDirectory NextToOutput(string parent)
    {
        var directory = new TempDirectory(System.IO.Path.Combine(parent, ".QuickZip-" + Guid.NewGuid().ToString("N")), true);
        Directory.CreateDirectory(directory.Path);
        return directory;
    }

    private TempDirectory(string path, bool _) => Path = path;

    public TempDirectory Transfer()
    {
        ownsPath = false;
        return new TempDirectory(Path, true);
    }

    public void Dispose()
    {
        if (!ownsPath)
            return;
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static partial class ArchiveEngine
{
    internal static string Executable { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "engine", "7z.exe");

    private static readonly UTF8Encoding LeUtf8 = new(false);
    private static readonly StringComparer FileNames = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> SingleStreams = new(StringComparer.OrdinalIgnoreCase)
        { "gzip", "xz", "bzip2", "zstd" };

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex ProgressLine();

    public static async Task<IReadOnlyList<ArchiveItem>> ListAsync(ArchiveSession session, int? codePage,
        string? password, IProgress<JobProgress> progress, CancellationToken token)
    {
        string path = await session.GetPathAsync(progress, token);
        progress.Report(new("正在读取文件列表…"));
        var items = new List<ArchiveItem>();
        bool inEntries = false;
        string? type = null;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        void FinishEntry()
        {
            string? name = properties.GetValueOrDefault("Path");
            if (string.IsNullOrEmpty(name) && properties.ContainsKey("Size") && type is not null && SingleStreams.Contains(type))
                name = ArchiveFormat.BaseName(path);
            if (name is not null)
            {
                string attributes = properties.GetValueOrDefault("Attributes", "");
                bool dir = properties.GetValueOrDefault("Folder") == "+" || attributes.StartsWith('D');
                long? size = long.TryParse(properties.GetValueOrDefault("Size"), out long value) ? value : null;
                items.Add(new(name, dir, size, properties.GetValueOrDefault("Modified", ""),
                    properties.GetValueOrDefault("Encrypted") == "+"));
            }
            properties.Clear();
        }

        await RunAsync(["l", "-slt", "-bsp0", .. ReadOptions(codePage, password), "--", path],
            null, token, onLine: line =>
            {
                if (!inEntries)
                {
                    inEntries = line == "----------";
                    if (line.StartsWith("Type = ", StringComparison.Ordinal))
                        type = line[7..];
                    return;
                }
                if (line.Length == 0)
                {
                    FinishEntry();
                    return;
                }
                int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                if (equals > 0)
                    properties[line[..equals]] = line[(equals + 3)..];
            });
        FinishEntry();
        return items;
    }

    public static async Task TestAsync(ArchiveSession session, int? codePage, string? password,
        IProgress<JobProgress> progress, CancellationToken token)
    {
        string path = await session.GetPathAsync(progress, token);
        await RunAsync(["t", "-bsp1", .. ReadOptions(codePage, password), "--", path], null, token,
            onLine: Report(progress, "正在测试完整性…"));
    }

    public static async Task ExtractToStageAsync(ArchiveSession session, string stage, int? codePage, string? password,
        IReadOnlyList<string>? selected, IProgress<JobProgress> progress, CancellationToken token)
    {
        var args = new List<string> { "x", "-y", "-aoa", "-bsp1", "-spod", "-snz", "-snl-", "-snh-", "-o" + stage };
        if (selected is null && session.CanStreamTar)
        {
            args.AddRange(["-ttar", "-siarchive.tar"]);
            await RunAsync(args, null, token, onLine: Report(progress, "正在解压 TAR…"),
                feed: (output, pipeToken) => UnwrapToStreamAsync(session.OriginalPath, output, pipeToken));
            VerifyExtractedTree(stage, token);
            return;
        }
        string path = await session.GetPathAsync(progress, token);
        args.AddRange(ReadOptions(codePage, password));
        bool singleStream = new[] { ".gz", ".xz", ".bz2", ".zst" }.Contains(Path.GetExtension(path), FileNames);
        using var selection = selected is null || singleStream ? null : new TempDirectory();
        if (selection is not null)
            args.Add("-i@" + await WriteListAsync(selection.Path, selected!, token));
        args.AddRange(["--", path]);
        await RunAsync(args, null, token, onLine: Report(progress, "正在解压…"));
        VerifyExtractedTree(stage, token);
    }

    public static async Task<ExtractResult> ExtractAsync(ArchiveSession session, string destination, bool newFolder,
        int? codePage, string? password, IReadOnlyList<string>? selected, IProgress<JobProgress> progress,
        Func<ConflictAction> conflict, CancellationToken token, bool smart = false)
    {
        destination = Path.GetFullPath(destination);
        if (newFolder)
        {
            string parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            using var stage = TempDirectory.NextToOutput(parent);
            await ExtractToStageAsync(session, stage.Path, codePage, password, selected, progress, token);
            token.ThrowIfCancellationRequested();

            // Smart extraction: an archive that already wraps everything in one item does not get a second folder.
            if (smart && Directory.GetFileSystemEntries(stage.Path) is [string only])
            {
                bool isDirectory = Directory.Exists(only);
                while (true)
                {
                    string target = FreeName(Path.Combine(parent, Path.GetFileName(only)), isDirectory);
                    try
                    {
                        if (isDirectory)
                            Directory.Move(only, target);
                        else
                            File.Move(only, target);
                        return new(isDirectory ? target : parent, 0);
                    }
                    catch (IOException) when (Path.Exists(target)) { }
                }
            }
            while (true)
            {
                string target = FreeName(destination, isDirectory: true);
                try
                {
                    Directory.Move(stage.Path, target);
                    return new(target, 0);
                }
                catch (IOException) when (Path.Exists(target)) { }
            }
        }

        Directory.CreateDirectory(destination);
        using var output = TempDirectory.NextToOutput(destination);
        await ExtractToStageAsync(session, output.Path, codePage, password, selected, progress, token);
        token.ThrowIfCancellationRequested();
        var entries = Directory.GetFileSystemEntries(output.Path);
        ConflictAction? choice = entries.Any(item => Path.Exists(Path.Combine(destination, Path.GetFileName(item))))
            ? conflict() : null;
        if (choice == ConflictAction.Cancel)
            throw new OperationCanceledException();
        int skipped = 0;
        foreach (string item in entries)
        {
            string target = Path.Combine(destination, Path.GetFileName(item));
            bool directory = Directory.Exists(item);
            while (true)
            {
                if (Path.Exists(target))
                {
                    progress.Report(new("正在处理同名文件…"));
                    choice ??= conflict();
                    if (choice == ConflictAction.Cancel)
                        throw new OperationCanceledException();
                    if (choice == ConflictAction.Skip)
                    {
                        skipped++;
                        break;
                    }
                    target = FreeName(target, directory);
                }
                try
                {
                    if (directory)
                        Directory.Move(item, target);
                    else
                        File.Move(item, target);
                    break;
                }
                catch (IOException) when (Path.Exists(target)) { }
            }
        }
        return new(destination, skipped);
    }

    public static async Task<string> CompressAsync(IReadOnlyList<string> sources, string output, ArchiveFormat format,
        int level, string? password, bool encryptNames, bool renameIfExists, IProgress<JobProgress> progress,
        CancellationToken token)
    {
        if (sources.Count == 0)
            throw new ArchiveException("请选择要压缩的文件或文件夹。");
        string[] paths = sources.Select(Path.GetFullPath).Distinct(FileNames).ToArray();
        string? sourceDir = Path.GetDirectoryName(paths[0]);
        if (sourceDir is null || paths.Any(path => !FileNames.Equals(sourceDir, Path.GetDirectoryName(path))))
            throw new ArchiveException("请从同一个目录选择要压缩的项目。");
        if (paths.Any(path => !Path.Exists(path)))
            throw new ArchiveException("至少有一个所选项目已不存在。");
        if (format == ArchiveFormat.Zip && password is not null &&
            (password.Length > 99 || password.Any(ch => ch is < ' ' or > '~')))
            throw new ArchiveException("7-Zip 引擎创建加密 ZIP 时只支持最多 99 个可打印 ASCII 字符。需要中文密码时，请选择 7z。");
        if (format.SingleFile && (paths.Length != 1 || !File.Exists(paths[0])))
            throw new ArchiveException("此格式每次只能压缩一个文件。");

        output = Path.GetFullPath(output);
        string outputDir = Path.GetDirectoryName(output)!;
        if (paths.Any(path => FileNames.Equals(path, output) ||
            (Directory.Exists(path) && output.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))))
            throw new ArchiveException("请将生成的压缩包保存到输入文件夹以外的位置。");

        Directory.CreateDirectory(outputDir);
        if (!renameIfExists && Path.Exists(output))
            throw new ArchiveException("输出位置已有同名项目。请选择其他文件名。");
        using var stage = TempDirectory.NextToOutput(outputDir);
        string list = await WriteListAsync(stage.Path, paths.Select(path => Path.GetFileName(path)), token);
        string target = Path.Combine(stage.Path, Path.GetFileName(output));
        if (format.Filter is not null)
        {
            string tarName = ArchiveFormat.BaseName(output) + ".tar";
            await RunAsync(["a", "-t" + format.Filter, "-si" + tarName, "-mx=" + level, "-bsp1", "--", target],
                stage.Path, token, onLine: Report(progress, "正在打包并压缩 TAR…"),
                feed: (stream, pipeToken) => RunAsync(
                    ["a", "-ttar", "-mtc=on", "-so", "-bso0", "-bsp0", "-spd", "-i@" + list, "--", tarName],
                    sourceDir, pipeToken, binaryOutput: stream));
        }
        else
        {
            var args = new List<string> { "a", "-t" + format.Type, "-bsp1", "-spd", "-i@" + list };
            if (format.Type != "tar")
                args.Add("-mx=" + level);
            if (format == ArchiveFormat.Zip)
                args.AddRange(["-mm=Deflate", "-mcu=on"]);
            if (!string.IsNullOrEmpty(password))
            {
                args.Add("-p" + password);
                if (format == ArchiveFormat.Zip)
                    args.Add("-mem=AES256");
                if (format == ArchiveFormat.SevenZip && encryptNames)
                    args.Add("-mhe=on");
            }
            args.AddRange(["--", target]);
            await RunAsync(args, sourceDir, token, onLine: Report(progress, "正在压缩…"));
        }

        token.ThrowIfCancellationRequested();
        while (true)
        {
            string final = renameIfExists ? FreeName(output, isDirectory: false) : output;
            try
            {
                File.Move(target, final);
                return final;
            }
            catch (IOException) when (renameIfExists && Path.Exists(final)) { }
        }
    }

    public static async Task UnwrapAsync(string source, string target, CancellationToken token)
    {
        await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await UnwrapToStreamAsync(source, stream, token);
    }

    private static Task UnwrapToStreamAsync(string source, Stream stream, CancellationToken token) =>
        RunAsync(["x", "-so", "-bso0", "-bsp0", "-y", "--", source], null, token, binaryOutput: stream);

    public static string? ReadZone(string source)
    {
        try { return File.ReadAllText(source + ":Zone.Identifier"); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static void CopyZone(string source, string target)
    {
        string? zone = ReadZone(source);
        if (zone is not null)
            File.WriteAllText(target + ":Zone.Identifier", zone);
    }

    private static void VerifyExtractedTree(string root, CancellationToken token)
    {
        var entries = new FileSystemEnumerable<FileAttributes>(root,
            (ref FileSystemEntry entry) => entry.Attributes,
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
        };
        foreach (FileAttributes attributes in entries)
        {
            token.ThrowIfCancellationRequested();
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveException("压缩包包含 Windows 无法安全提取的链接。");
        }
    }

    private static IEnumerable<string> ReadOptions(int? codePage, string? password)
    {
        yield return "-spd";
        if (codePage.HasValue)
            yield return "-mcp=" + codePage.Value;
        if (!string.IsNullOrEmpty(password))
            yield return "-p" + password;
    }

    private static async Task<string> WriteListAsync(string directory, IEnumerable<string> entries, CancellationToken token)
    {
        string list = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
        string[] safeEntries = entries.ToArray();
        if (safeEntries.Any(entry => entry.Contains('\r') || entry.Contains('\n')))
            throw new ArchiveException("所选项目含有无法作为单独条目提取的文件名。");
        await File.WriteAllLinesAsync(list, safeEntries, LeUtf8, token);
        return list;
    }

    private static string FreeName(string path, bool isDirectory)
    {
        if (!Path.Exists(path))
            return path;
        string ext = isDirectory ? "" : ArchiveFormat.Compound.FirstOrDefault(ext =>
            path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ?? Path.GetExtension(path);
        string stem = path[..(path.Length - ext.Length)];
        for (int number = 2; ; number++)
        {
            string candidate = $"{stem} ({number}){ext}";
            if (!Path.Exists(candidate))
                return candidate;
        }
    }

    private static Action<string> Report(IProgress<JobProgress> progress, string status)
    {
        progress.Report(new(status));
        int? previous = null;
        return line =>
        {
            Match match = ProgressLine().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) &&
                percent <= 100 && previous != percent)
            {
                previous = percent;
                progress.Report(new(status, percent));
            }
        };
    }

    private static async Task RunAsync(IEnumerable<string> args, string? workingDirectory,
        CancellationToken token, Action<string>? onLine = null, Stream? binaryOutput = null,
        Func<Stream, CancellationToken, Task>? feed = null)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Executable))
            throw new ArchiveException("缺少压缩引擎。请从完整的ZIP发布目录启动。");

        var config = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = LeUtf8, StandardErrorEncoding = LeUtf8
        };
        config.ArgumentList.Add("-sccUTF-8");
        foreach (string arg in args)
            config.ArgumentList.Add(arg);

        using var process = Process.Start(config) ?? throw new ArchiveException("无法启动压缩引擎。");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var recent = new Queue<string>();

        async Task ConsumeOutputAsync()
        {
            if (binaryOutput is not null)
            {
                await process.StandardOutput.BaseStream.CopyToAsync(binaryOutput, 256 * 1024, token);
                return;
            }
            using StreamReader output = process.StandardOutput;
            char[] buffer = new char[4096];
            var line = new StringBuilder();
            while (true)
            {
                int read = await output.ReadAsync(buffer.AsMemory(), token);
                if (read == 0)
                    break;
                for (int index = 0; index < read; index++)
                {
                    char ch = buffer[index];
                    if (ch is '\r' or '\n' or '\b')
                    {
                        if (ch != '\n' || index == 0 || buffer[index - 1] != '\r')
                            RecordLine(line.ToString());
                        line.Clear();
                    }
                    else
                        line.Append(ch);
                }
            }
            if (line.Length > 0)
                RecordLine(line.ToString());
        }

        void RecordLine(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (recent.Count == 12)
                    recent.Dequeue();
                recent.Enqueue(line);
            }
            onLine?.Invoke(line);
        }

        async Task<Exception?> SupplyInputAsync(CancellationToken pipeToken)
        {
            try
            {
                if (feed is not null)
                    await feed(process.StandardInput.BaseStream, pipeToken);
                return null;
            }
            catch (Exception error) { return error; }
            finally { process.StandardInput.BaseStream.Close(); }
        }

        Task readOutput = ConsumeOutputAsync();
        Task<string> readErrors = process.StandardError.ReadToEndAsync(token);
        using var pipe = feed is null ? null : CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<Exception?> inputError = SupplyInputAsync(pipe?.Token ?? token);
        try
        {
            await readOutput;
            await process.WaitForExitAsync(token);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (pipe is not null)
                await pipe.CancelAsync();
            await process.WaitForExitAsync();
            await inputError;
            throw;
        }

        string errors = await readErrors;
        Exception? producerError = await inputError;
        token.ThrowIfCancellationRequested();
        if (producerError is ArchiveException || process.ExitCode == 0 && producerError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(producerError);
        if (process.ExitCode == 0)
            return;
        string details = (errors + "\n" + string.Join("\n", recent)).Trim();
        bool needsPassword = new[] { "Enter password", "Wrong password", "Cannot open encrypted archive" }
            .Any(marker => details.Contains(marker, StringComparison.OrdinalIgnoreCase));
        string message = needsPassword ? "此压缩包需要密码，或者密码不正确。" : process.ExitCode == 1
            ? "压缩引擎报告有文件未能完整处理。" : "压缩引擎无法完成任务。请检查文件是否完整、分卷是否齐全。";
        throw new ArchiveException(message + (details.Length == 0 ? "" : "\n\n" + details[^Math.Min(details.Length, 1700)..]),
            needsPassword);
    }
}
