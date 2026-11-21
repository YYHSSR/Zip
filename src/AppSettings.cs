using System.Text.Json;

namespace QuickZip;

/// <summary>Per-user preferences shared by every window in the process.</summary>
internal sealed class AppSettings
{
    private const int RecentLimit = 8;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZIP", "settings.json");
    private static AppSettings? current;

    public static AppSettings Current => current ??= Load();

    // Window
    public int[]? Bounds { get; set; }
    public bool Maximized { get; set; }
    public int Splitter { get; set; }
    public List<string> Recent { get; set; } = [];
    public bool AssociationHintDismissed { get; set; }

    // Extraction
    public bool SmartExtract { get; set; } = true;
    public bool OpenFolderAfterExtract { get; set; }

    // Compression: 0 fast, 1 standard, 2 maximum
    public int CompressionLevel { get; set; } = 1;

    // Opening files from inside archives
    public bool ConfirmRunnable { get; set; } = true;
    public bool OfferWriteBack { get; set; } = true;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Remember(string archive)
    {
        Recent.RemoveAll(item => string.Equals(item, archive, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, archive);
        if (Recent.Count > RecentLimit)
            Recent.RemoveRange(RecentLimit, Recent.Count - RecentLimit);
    }
}
