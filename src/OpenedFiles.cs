namespace QuickZip;

/// <summary>
/// Watches temporary copies of files opened from an archive, so edits made in other programs
/// can be written back. Changes are reported after a short quiet period, because editors
/// often save in several steps (write a temp file, rename, touch).
/// </summary>
internal sealed class OpenedFiles : IDisposable
{
    internal sealed class Entry(string stageRoot, string local, string name)
    {
        public string StageRoot { get; } = stageRoot;
        public string Local { get; } = local;
        public string Name { get; } = name;
        public string RelativePath => Path.GetRelativePath(StageRoot, Local);
        public (DateTime, long) Stamp { get; set; } = Read(local);

        public static (DateTime, long) Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
            }
            catch (IOException) { return default; }
        }
    }

    private readonly List<Entry> entries = [];
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer quiet = new() { Interval = 900 };
    private readonly Control owner;
    private bool reporting;

    /// <summary>Raised on the UI thread with files whose content changed since they were opened or last reported.</summary>
    public event Func<IReadOnlyList<Entry>, Task<bool>>? Changed;

    /// <summary>When this returns true, reporting waits (for example while another job runs).</summary>
    public Func<bool> Busy { get; set; } = () => false;

    public OpenedFiles(Control owner)
    {
        this.owner = owner;
        quiet.Tick += async (_, _) => await CheckAsync();
    }

    public bool Any => entries.Count > 0;

    public void Track(string stageRoot, string local, string name)
    {
        entries.RemoveAll(entry => string.Equals(entry.Local, local, StringComparison.OrdinalIgnoreCase));
        entries.Add(new Entry(stageRoot, local, name));
        if (watchers.ContainsKey(stageRoot))
            return;
        var watcher = new FileSystemWatcher(stageRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        FileSystemEventHandler poke = (_, _) => Poke();
        watcher.Changed += poke;
        watcher.Created += poke;
        watcher.Renamed += (_, _) => Poke();
        watcher.EnableRaisingEvents = true;
        watchers[stageRoot] = watcher;
    }

    private void Poke()
    {
        if (owner.IsDisposed || !owner.IsHandleCreated)
            return;
        try { owner.BeginInvoke(() =>
        {
            if (owner.IsDisposed)
                return;
            quiet.Stop();
            quiet.Start();
        }); }
        catch (InvalidOperationException) { }
    }

    public IReadOnlyList<Entry> Pending() =>
        entries.Where(entry => Entry.Read(entry.Local) is var now && now != default && now != entry.Stamp).ToArray();

    /// <summary>Remembers the reported revision only when the user declines or the write-back succeeds.</summary>
    public async Task<bool> CheckAsync(bool explicitAction = false)
    {
        quiet.Stop();
        if (reporting)
            return false;
        if (entries.Count == 0)
            return true;
        if (!explicitAction && Busy())
        {
            quiet.Start();
            return false;
        }
        var changes = entries.Select(entry => (Entry: entry, Stamp: Entry.Read(entry.Local)))
            .Where(change => change.Stamp != default && change.Stamp != change.Entry.Stamp).ToArray();
        if (changes.Length == 0 || Changed is null)
            return changes.Length == 0;
        reporting = true;
        try
        {
            if (!await Changed(changes.Select(change => change.Entry).ToArray()))
                return false;
            foreach (var change in changes)
                change.Entry.Stamp = change.Stamp;
            return Pending().Count == 0;
        }
        finally { reporting = false; }
    }

    /// <summary>Stops watching. The temporary copies themselves are owned and removed by the window.</summary>
    public void Clear()
    {
        quiet.Stop();
        entries.Clear();
        foreach (FileSystemWatcher watcher in watchers.Values)
            watcher.Dispose();
        watchers.Clear();
    }

    public void Dispose()
    {
        Clear();
        quiet.Dispose();
    }
}
