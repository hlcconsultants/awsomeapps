using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastGet.Core;

public enum AfterQueueAction { None, Exit, Sleep, Shutdown }

public sealed class AppSettings
{
    public string DownloadDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool UseCategories { get; set; } = true;
    public int MaxConcurrent { get; set; } = 3;
    public int ConnectionsPerDownload { get; set; } = 8;
    public long SpeedLimitKBps { get; set; } // 0 = unlimited
    public int MaxRetries { get; set; } = 8;
    public string UserAgent { get; set; } = Net.DefaultUserAgent;

    public bool ScheduleEnabled { get; set; }
    public TimeSpan ScheduleStart { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan ScheduleStop { get; set; } = TimeSpan.FromHours(6);
    public bool ScheduleStopEnabled { get; set; } = true;
    public AfterQueueAction AfterQueue { get; set; } = AfterQueueAction.None;

    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool WatchClipboard { get; set; } = true;
    public bool ConfirmBrowserDownloads { get; set; } = true;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastGet");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch { /* corrupt settings: fall back to defaults */ }
        return new();
    }

    public void Save() => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }
}

public static class Categories
{
    public static readonly string[] All = { "Compressed", "Documents", "Music", "Video", "Programs", "Images", "Other" };

    static readonly Dictionary<string, string> Map = BuildMap();

    static Dictionary<string, string> BuildMap()
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) m["." + e] = cat; }
        Add("Compressed", "zip rar 7z tar gz bz2 xz iso cab arj lzh z");
        Add("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt csv epub mobi");
        Add("Music", "mp3 wav flac aac ogg wma m4a opus aiff");
        Add("Video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts 3gp");
        Add("Programs", "exe msi msix appx apk dmg deb rpm bat jar");
        Add("Images", "jpg jpeg png gif bmp webp svg tif tiff ico heic");
        return m;
    }

    public static string For(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var c) ? c : "Other";
}
