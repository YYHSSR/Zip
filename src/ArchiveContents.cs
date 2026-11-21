namespace QuickZip;

internal sealed class ArchiveFolder(string name, ArchiveFolder? parent)
{
    public string Name { get; } = name;
    public string Path { get; } = parent is null ? "" : parent.Path.Length == 0 ? name : parent.Path + "/" + name;
    public ArchiveFolder? Parent { get; } = parent;
    public Dictionary<string, ArchiveFolder> Folders { get; } = new(StringComparer.Ordinal);
    public List<ArchiveRow> Files { get; } = [];
    public ArchiveItem? Item { get; set; }

    public ArchiveRow AsRow() => new(Name, Path, Item, this);

    public IEnumerable<string> GetArchivePaths()
    {
        // Explicit directories are selectable by their encoded archive paths.
        // ZIP files can omit directory records, so include the member names for those directories.
        var pending = new Stack<ArchiveFolder>();
        pending.Push(this);
        while (pending.TryPop(out ArchiveFolder? folder))
        {
            if (folder.Item is { } entry)
            {
                yield return entry.Path;
                continue;
            }
            foreach (ArchiveRow file in folder.Files)
                yield return file.Item!.Path;
            foreach (ArchiveFolder descendant in folder.Folders.Values)
                pending.Push(descendant);
        }
    }
}

internal sealed record ArchiveRow(string Name, string Path, ArchiveItem? Item, ArchiveFolder? Folder)
{
    public string Type => Folder is not null ? "文件夹" :
        System.IO.Path.GetExtension(Name) is { Length: > 1 } extension ? extension[1..].ToUpperInvariant() : "文件";

    public IEnumerable<string> GetArchivePaths() =>
        Folder is null ? [Item!.Path] : Folder.GetArchivePaths();
}

internal sealed class ArchiveContents
{
    public ArchiveFolder Root { get; } = new("", null);
    public int TotalEntries { get; }
    public long TotalSize { get; }

    public ArchiveContents(IReadOnlyList<ArchiveItem> items, CancellationToken token)
    {
        TotalEntries = items.Count;
        foreach (ArchiveItem item in items)
        {
            token.ThrowIfCancellationRequested();
            if (!item.Directory)
                TotalSize += item.Size ?? 0;
            string[] components = item.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            int first = 0;
            while (first < components.Length && components[first] == ".")
                first++;
            if (first == components.Length)
                continue;
            ArchiveFolder folder = Root;
            int folders = components.Length - (item.Directory ? 0 : 1);
            for (int index = first; index < folders; index++)
            {
                string name = components[index];
                if (!folder.Folders.TryGetValue(name, out ArchiveFolder? child))
                    folder.Folders.Add(name, child = new(name, folder));
                folder = child;
            }
            if (item.Directory)
            {
                folder.Item ??= item;
                continue;
            }
            string nameInFolder = components[^1];
            string relativePath = folder.Path.Length == 0 ? nameInFolder : $"{folder.Path}/{nameInFolder}";
            folder.Files.Add(new(nameInFolder, relativePath, item, null));
        }
    }

    public IEnumerable<ArchiveRow> Search(string query)
    {
        var pending = new Stack<ArchiveFolder>();
        pending.Push(Root);
        while (pending.TryPop(out ArchiveFolder? folder))
        {
            if (folder.Parent is not null && folder.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                yield return folder.AsRow();
            foreach (ArchiveRow file in folder.Files)
            {
                if (file.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            foreach (ArchiveFolder child in folder.Folders.Values)
                pending.Push(child);
        }
    }
}
