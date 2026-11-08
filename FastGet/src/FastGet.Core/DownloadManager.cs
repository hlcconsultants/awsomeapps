using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastGet.Core;

public sealed class DownloadRequest
{
    public required string Url { get; init; }
    public string? FileName { get; init; }
    public string? SaveDir { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public string? Cookie { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public DownloadKind? Kind { get; init; }
    public int? Connections { get; init; }
    public bool Start { get; init; } = true;
}

/// <summary>Owns the download list: queueing, scheduling, bandwidth, persistence.</summary>
public sealed class DownloadManager : IDisposable
{
    enum StopReason { Pause, Requeue, Remove }

    sealed class Running(DownloadJob job)
    {
        public DownloadJob Job { get; } = job;
        public CancellationTokenSource Cts { get; } = new();
        public StopReason Reason { get; set; } = StopReason.Pause;
        public bool DeleteFile { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    readonly object _lock = new();
    readonly List<DownloadItem> _items = new();
    readonly Dictionary<Guid, Running> _running = new();
    readonly HashSet<Guid> _forced = new();
    readonly HttpClient _http = Net.CreateClient();
    readonly SpeedLimiter _limiter = new();
    readonly EngineContext _ctx;
    readonly Timer _tick;
    readonly string _statePath;
    long _lastTick = Environment.TickCount64;
    int _ticks;
    bool _dirty;
    bool _windowOpen;
    bool _completedSinceIdle;
    bool _disposed;

    public AppSettings Settings { get; }
    public event Action<DownloadItem>? ItemAdded;
    public event Action<DownloadItem>? ItemRemoved;
    public event Action<DownloadItem>? ItemCompleted;
    public event Action<DownloadItem>? ItemFailed;
    public event Action<AfterQueueAction>? QueueFinished;

    public DownloadManager(AppSettings settings, string? statePath = null)
    {
        Settings = settings;
        _statePath = statePath ?? Path.Combine(AppSettings.DataDir, "downloads.json");
        _ctx = new EngineContext { Http = _http, Limiter = _limiter, Settings = settings, NameResolved = ApplyCategory };
        _limiter.Limit = settings.SpeedLimitKBps * 1024;
        _windowOpen = IsWindowOpen(DateTime.Now);
        Load();
        _tick = new Timer(_ => Tick(), null, 500, 500);
    }

    public DownloadItem[] Snapshot() { lock (_lock) return _items.ToArray(); }

    public double TotalSpeed { get { lock (_lock) return _running.Keys.Sum(id => _items.Find(i => i.Id == id)?.SpeedBps ?? 0); } }

    public DownloadItem? FindActiveByUrl(string url)
    {
        lock (_lock) return _items.Find(i => i.Url == url && i.Status != DownloadStatus.Completed);
    }

    public DownloadItem Add(DownloadRequest r)
    {
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only http and https links are supported");

        var name = string.IsNullOrWhiteSpace(r.FileName) ? "" : FileNames.Sanitize(Path.GetFileName(r.FileName.Replace('\\', '/')));
        var item = new DownloadItem
        {
            Url = uri.ToString(),
            FileName = name,
            Kind = r.Kind ?? Detect(uri),
            Referrer = r.Referrer,
            UserAgent = r.UserAgent,
            Cookie = r.Cookie,
            Headers = r.Headers ?? new(),
            MaxConnections = Math.Clamp(r.Connections ?? Settings.ConnectionsPerDownload, 1, 32),
            SaveDir = r.SaveDir ?? Settings.DownloadDir,
            AutoFolder = r.SaveDir == null && Settings.UseCategories,
            Status = r.Start ? DownloadStatus.Queued : DownloadStatus.Paused,
        };
        lock (_lock) { _items.Add(item); _dirty = true; }
        ItemAdded?.Invoke(item);
        Pump();
        return item;
    }

    static DownloadKind Detect(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return ext switch { ".m3u8" => DownloadKind.Hls, ".mpd" => DownloadKind.Dash, _ => DownloadKind.Http };
    }

    void ApplyCategory(DownloadItem it)
    {
        var name = it.Kind == DownloadKind.Http ? it.FileName : it.FileName + ".mp4";
        it.Category = Categories.For(name);
        if (it.AutoFolder)
        {
            it.SaveDir = Path.Combine(it.SaveDir, it.Category);
            it.AutoFolder = false;
        }
    }

    public void Start(DownloadItem it)
    {
        lock (_lock)
        {
            if (it.IsActive || it.Status == DownloadStatus.Completed) return;
            it.Error = null;
            it.Status = DownloadStatus.Queued;
            _forced.Add(it.Id);
            _dirty = true;
        }
        Pump();
    }

    public void Pause(DownloadItem it)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(it.Id, out var r)) { r.Reason = StopReason.Pause; r.Cts.Cancel(); }
            else if (it.Status == DownloadStatus.Queued) { it.Status = DownloadStatus.Paused; _forced.Remove(it.Id); }
            _dirty = true;
        }
    }

    public void StartAll() { foreach (var i in Snapshot()) if (i.Status is DownloadStatus.Paused or DownloadStatus.Error) Start(i); }

    public void PauseAll() { foreach (var i in Snapshot()) if (i.IsActive || i.Status == DownloadStatus.Queued) Pause(i); }

    /// <summary>Discards progress and downloads again.</summary>
    public void Restart(DownloadItem it)
    {
        lock (_lock)
        {
            if (_running.ContainsKey(it.Id)) return;
            CleanupPartial(it);
            lock (it.Segments) it.Segments.Clear();
            it.Downloaded = 0;
            it.TotalSize = -1;
            it.ETag = it.LastModified = null;
            it.CompletedAt = null;
            it.Note = null;
            it.RaiseProgress();
        }
        it.Status = DownloadStatus.Paused;
        Start(it);
    }

    /// <summary>Points an expired download at a fresh link while keeping its progress.</summary>
    public void UpdateUrl(DownloadItem it, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only http and https links are supported");
        lock (_lock) { it.Url = uri.ToString(); _dirty = true; }
    }

    public void Remove(DownloadItem it, bool deleteFile)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(it.Id, out var r))
            {
                r.Reason = StopReason.Remove;
                r.DeleteFile = deleteFile;
                r.Cts.Cancel();
                return;
            }
            FinishRemove(it, deleteFile);
        }
        ItemRemoved?.Invoke(it);
        Pump();
    }

    void FinishRemove(DownloadItem it, bool deleteFile)
    {
        _items.Remove(it);
        _forced.Remove(it.Id);
        _dirty = true;
        CleanupPartial(it);
        if (deleteFile && it.FinalPath != null)
            try { File.Delete(it.FinalPath); } catch { /* in use: leave it */ }
    }

    static void CleanupPartial(DownloadItem it)
    {
        try
        {
            if (string.IsNullOrEmpty(it.SaveDir)) return;
            if (!string.IsNullOrEmpty(it.FileName))
            {
                var part = HttpDownloadJob.PartPathFor(it);
                if (File.Exists(part)) File.Delete(part);
            }
            var temp = MediaJob.TempDirFor(it);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
        catch { /* best effort */ }
    }

    public void ApplySettings()
    {
        _limiter.Limit = Settings.SpeedLimitKBps * 1024;
        Settings.Save();
        Reschedule();
        Pump();
    }

    bool IsWindowOpen(DateTime now)
    {
        if (!Settings.ScheduleEnabled) return true;
        var t = now.TimeOfDay;
        var (a, b) = (Settings.ScheduleStart, Settings.ScheduleStop);
        if (!Settings.ScheduleStopEnabled) return t >= a;
        return a <= b ? t >= a && t < b : t >= a || t < b;
    }

    void Reschedule()
    {
        bool open = IsWindowOpen(DateTime.Now);
        bool closing;
        lock (_lock)
        {
            closing = _windowOpen && !open && Settings.ScheduleStopEnabled;
            _windowOpen = open;
            if (closing)
            {
                _forced.Clear();
                foreach (var r in _running.Values) { r.Reason = StopReason.Requeue; r.Cts.Cancel(); }
            }
        }
        if (open) Pump();
    }

    void Pump()
    {
        var toStart = new List<(DownloadItem Item, Running Run)>();
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var it in _items.Where(i => i.Status == DownloadStatus.Queued).OrderBy(i => i.AddedAt))
            {
                if (_running.Count >= Settings.MaxConcurrent) break;
                if (!_windowOpen && !_forced.Contains(it.Id)) continue;
                DownloadJob job = it.Kind switch
                {
                    DownloadKind.Hls => new HlsJob(it, _ctx),
                    DownloadKind.Dash => new DashJob(it, _ctx),
                    _ => new HttpDownloadJob(it, _ctx),
                };
                var run = new Running(job);
                _running[it.Id] = run;
                _forced.Remove(it.Id);
                it.Status = DownloadStatus.Downloading;
                it.Error = null;
                toStart.Add((it, run));
            }
            if (toStart.Count > 0) _dirty = true;
        }
        foreach (var (it, run) in toStart) _ = Task.Run(() => ExecuteAsync(it, run));
    }

    async Task ExecuteAsync(DownloadItem it, Running run)
    {
        DownloadStatus final;
        bool removed = false, failed = false;
        try
        {
            await run.Job.RunAsync(run.Cts.Token);
            final = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            final = run.Reason == StopReason.Requeue ? DownloadStatus.Queued : DownloadStatus.Paused;
            removed = run.Reason == StopReason.Remove;
        }
        catch (Exception ex)
        {
            it.Error = ex.Message;
            final = DownloadStatus.Error;
            failed = true;
        }

        try { run.Job.Refresh(); } catch { /* state is still saved below */ }
        it.SpeedBps = 0;
        it.Connections = 0;

        bool fireFinished = false;
        lock (_lock)
        {
            _running.Remove(it.Id);
            run.Cts.Dispose();
            if (removed)
            {
                FinishRemove(it, run.DeleteFile);
            }
            else
            {
                it.Status = final;
                if (final == DownloadStatus.Completed)
                {
                    it.CompletedAt = DateTime.Now;
                    it.Cookie = null; // no longer needed; don't keep login cookies on disk
                    it.Downloaded = it.TotalSize > 0 ? it.TotalSize : it.Downloaded;
                    _completedSinceIdle = true;
                }
            }
            _dirty = true;

            bool idle = _running.Count == 0 && !_items.Any(i => i.Status == DownloadStatus.Queued);
            if (idle && _completedSinceIdle)
            {
                _completedSinceIdle = false;
                fireFinished = Settings.AfterQueue != AfterQueueAction.None;
            }
        }

        it.RaiseProgress();
        if (removed) ItemRemoved?.Invoke(it);
        else if (final == DownloadStatus.Completed) ItemCompleted?.Invoke(it);
        else if (failed) ItemFailed?.Invoke(it);

        Save();
        Pump();
        if (fireFinished) QueueFinished?.Invoke(Settings.AfterQueue);
    }

    void Tick()
    {
        long now = Environment.TickCount64;
        double dt = Math.Max(0.05, (now - Interlocked.Exchange(ref _lastTick, now)) / 1000.0);

        (DownloadItem Item, Running Run)[] active;
        lock (_lock)
        {
            if (_disposed) return;
            active = _running.Select(kv => (_items.Find(i => i.Id == kv.Key)!, kv.Value)).Where(p => p.Item1 != null).ToArray();
        }

        foreach (var (it, run) in active)
        {
            double inst = run.Job.TakeBytes() / dt;
            it.SpeedBps = it.SpeedBps <= 0 ? inst : it.SpeedBps * 0.7 + inst * 0.3;
            try { run.Job.Refresh(); } catch { /* transient while the job is mutating */ }
            it.RaiseProgress();
        }
        if (active.Length > 0) _dirty = true;

        int ticks = ++_ticks;
        if (ticks % 30 == 0) Reschedule();
        if (ticks % 6 == 0 && _dirty) Save();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var items = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(_statePath), Json);
            if (items == null) return;
            foreach (var i in items)
                if (i.IsActive) i.Status = DownloadStatus.Paused; // interrupted by shutdown; resumable from saved segments
            _items.AddRange(items);
        }
        catch { /* unreadable state: start with an empty list rather than refuse to open */ }
    }

    readonly object _saveLock = new();

    public void Save()
    {
        lock (_saveLock)
        {
            _dirty = false;
            var parts = new List<string>();
            foreach (var it in Snapshot())
                lock (it.Segments) parts.Add(JsonSerializer.Serialize(it, Json));
            try { AtomicFile.WriteAllText(_statePath, "[" + string.Join(",", parts) + "]"); }
            catch (IOException) { _dirty = true; }
        }
    }

    public void Dispose()
    {
        Running[] running;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            running = _running.Values.ToArray();
            foreach (var r in running) { r.Reason = StopReason.Pause; r.Cts.Cancel(); }
        }
        _tick.Dispose();
        SpinWait.SpinUntil(() => { lock (_lock) return _running.Count == 0; }, TimeSpan.FromSeconds(3));
        Save();
        _http.Dispose();
    }
}
