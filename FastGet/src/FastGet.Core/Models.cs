using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FastGet.Core;

public enum DownloadStatus { Queued, Downloading, Merging, Paused, Completed, Error }

public enum DownloadKind { Http, Hls, Dash }

/// <summary>A byte range of the file. End is inclusive; -1 means "until EOF" (unknown length).</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Position { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : Math.Max(0, End - Position + 1);
    [JsonIgnore] public bool Done => End >= 0 && Position > End;
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name!);
    }

    string _fileName = "";
    DownloadStatus _status = DownloadStatus.Queued;
    string? _error;
    string? _note;
    long _total = -1;
    long _downloaded;
    double _speed;
    int _connections;

    // ---- persisted request description ----
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    public string SaveDir { get; set; } = "";
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookie { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public int MaxConnections { get; set; } = 8;
    public string Category { get; set; } = "Other";
    /// <summary>True when SaveDir is the base folder and the category subfolder is picked once the real name is known.</summary>
    public bool AutoFolder { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    // ---- persisted progress ----
    public string FileName { get => _fileName; set { Set(ref _fileName, value); Raise(nameof(Display)); } }
    public DownloadStatus Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Raise(nameof(Status)); Raise(nameof(StatusText)); Raise(nameof(IsActive)); Raise(nameof(IsCompleted)); }
    }
    public string? Error { get => _error; set { Set(ref _error, value); Raise(nameof(StatusText)); } }
    public string? Note { get => _note; set { Set(ref _note, value); Raise(nameof(StatusText)); } }
    public long TotalSize { get => _total; set { Set(ref _total, value); Raise(nameof(SizeText)); } }
    public long Downloaded { get => _downloaded; set => Set(ref _downloaded, value); }
    public bool AcceptRanges { get; set; }
    public bool SizeEstimated { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? FinalPath { get; set; }
    public List<Segment> Segments { get; set; } = new();

    // ---- runtime only ----
    [JsonIgnore] public double SpeedBps { get => _speed; set => _speed = value; }
    [JsonIgnore] public int Connections { get => _connections; set => _connections = value; }
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Downloading or DownloadStatus.Merging;
    [JsonIgnore] public bool IsCompleted => Status == DownloadStatus.Completed;
    [JsonIgnore] public string Display => string.IsNullOrEmpty(FileName) ? Url : FileName;
    [JsonIgnore] public double Percent => TotalSize > 0 ? Math.Min(100, Downloaded * 100.0 / TotalSize) : (IsCompleted ? 100 : 0);
    [JsonIgnore] public string SizeText => TotalSize > 0 ? (SizeEstimated ? "~" : "") + Format.Bytes(TotalSize) : "Unknown";
    [JsonIgnore] public string DoneText => Format.Bytes(Downloaded);
    [JsonIgnore] public string SpeedText => IsActive && _speed > 0 ? Format.Bytes((long)_speed) + "/s" : "";
    [JsonIgnore] public string EtaText => IsActive && _speed > 1 && TotalSize > 0 ? Format.Duration((TotalSize - Downloaded) / _speed) : "";
    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => Connections > 0 ? $"Downloading ({Connections})" : "Downloading",
        DownloadStatus.Merging => "Merging…",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => string.IsNullOrEmpty(Note) ? "Complete" : "Complete – " + Note,
        DownloadStatus.Error => "Error: " + Error,
        _ => ""
    };

    /// <summary>Re-raises the volatile (progress) properties so bound UI refreshes.</summary>
    public void RaiseProgress()
    {
        Raise(nameof(Downloaded)); Raise(nameof(DoneText)); Raise(nameof(Percent));
        Raise(nameof(SpeedText)); Raise(nameof(EtaText)); Raise(nameof(StatusText)); Raise(nameof(SizeText));
    }
}

public static class Format
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.##} {u[i]}";
    }

    public static string Duration(double seconds)
    {
        if (seconds < 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "";
        var t = TimeSpan.FromSeconds(Math.Min(seconds, 99 * 3600));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }
}
