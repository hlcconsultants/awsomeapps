using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace FastGet.Core;

public sealed class EngineContext
{
    public required HttpClient Http { get; init; }
    public required SpeedLimiter Limiter { get; init; }
    public required AppSettings Settings { get; init; }
    /// <summary>Called once the real file name is known, before anything is written.</summary>
    public Action<DownloadItem>? NameResolved { get; init; }
}

public abstract class DownloadJob(DownloadItem item, EngineContext ctx)
{
    protected readonly DownloadItem It = item;
    protected readonly EngineContext Ctx = ctx;
    long _bytes;

    protected void AddBytes(long n) => Interlocked.Add(ref _bytes, n);
    public long TakeBytes() => Interlocked.Exchange(ref _bytes, 0);
    public virtual void Refresh() { }
    public abstract Task RunAsync(CancellationToken ct);

    protected static bool IsDiskFull(Exception ex) => ex is IOException && (ex.HResult & 0xFFFF) is 112 or 39;

    protected static bool IsFatalStatus(HttpStatusCode code)
    {
        int c = (int)code;
        return c is >= 400 and < 500 and not 408 and not 425 and not 429;
    }
}

/// <summary>
/// Multi-connection HTTP download: the file is split into byte ranges fetched in parallel and written
/// in place. Idle connections steal the back half of the largest remaining range (dynamic segmentation).
/// </summary>
public sealed class HttpDownloadJob(DownloadItem item, EngineContext ctx) : DownloadJob(item, ctx)
{
    const long MinSegment = 1024 * 1024;
    const long MinSplit = 256 * 1024;
    const int BufferSize = 64 * 1024;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    readonly object _sync = new();
    Exception? _fatal;
    bool _eof;
    bool _forceSingle;
    int _liveWorkers;

    string PartPath => Path.Combine(It.SaveDir, $"{It.FileName}.{It.Id.ToString("N")[..8]}.fgpart");

    public static string PartPathFor(DownloadItem it) => Path.Combine(it.SaveDir, $"{it.FileName}.{it.Id.ToString("N")[..8]}.fgpart");

    public override void Refresh()
    {
        lock (It.Segments)
        {
            long sum = 0; int active = 0;
            foreach (var s in It.Segments)
            {
                sum += s.End >= 0 ? Math.Min(s.Position, s.End + 1) - s.Start : s.Position - s.Start;
                if (s.Active) active++;
            }
            It.Downloaded = sum;
            It.Connections = active;
        }
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(It.SaveDir);
        for (int pass = 0; ; pass++)
        {
            try { await RunOnceAsync(ct); return; }
            catch (RangeRejectedException) when (pass == 0)
            {
                _forceSingle = true;
                _fatal = null;
                lock (It.Segments) It.Segments.Clear();
            }
        }
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        var (oldTag, oldModified, oldTotal) = (It.ETag, It.LastModified, It.TotalSize);
        bool hadState;
        lock (It.Segments) hadState = It.Segments.Count > 0;

        await ProbeAsync(ct);
        if (_forceSingle) It.AcceptRanges = false;

        bool sameEntity = oldTotal == It.TotalSize
            && (oldTag == null || It.ETag == null || oldTag == It.ETag)
            && (oldModified == null || It.LastModified == null || oldModified == It.LastModified);
        bool resume = hadState && It.AcceptRanges && sameEntity && File.Exists(PartPath);

        if (!resume)
        {
            if (!hadState) Ctx.NameResolved?.Invoke(It);
            Directory.CreateDirectory(It.SaveDir);
            TryDelete(PartPath);
            InitSegments();
        }

        using (var fs = new FileStream(PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess))
        {
            if (It.TotalSize > 0 && fs.Length != It.TotalSize) fs.SetLength(It.TotalSize);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int workers = It.AcceptRanges ? Math.Max(1, It.MaxConnections) : 1;
            _liveWorkers = workers;
            var handle = fs.SafeFileHandle;
            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() => WorkerAsync(handle, cts), CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);
            ct.ThrowIfCancellationRequested();
            if (_fatal != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_fatal);

            if (_eof)
            {
                long size;
                lock (It.Segments) size = It.Segments[0].Position;
                It.TotalSize = size;
                fs.SetLength(size);
            }
            else
            {
                bool complete;
                lock (It.Segments) complete = It.Segments.All(s => s.Done);
                if (!complete) throw new IOException("Download ended with missing data");
            }
            fs.Flush(true);
        }

        Refresh();
        var final = FileNames.Unique(It.SaveDir, It.FileName);
        File.Move(PartPath, final);
        It.FinalPath = final;
        It.FileName = Path.GetFileName(final);
        if (DateTimeOffset.TryParse(It.LastModified, out var modified))
            try { File.SetLastWriteTimeUtc(final, modified.UtcDateTime); } catch { /* cosmetic */ }
    }

    async Task ProbeAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var req = Net.Build(It, It.Url, Ctx.Settings.UserAgent);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var resp = await Ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    It.TotalSize = resp.Content.Headers.ContentRange?.Length ?? -1;
                    It.AcceptRanges = It.TotalSize > 0;
                }
                else if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    It.TotalSize = resp.Content.Headers.ContentRange?.Length ?? 0;
                    It.AcceptRanges = false;
                }
                else if (resp.IsSuccessStatusCode)
                {
                    It.TotalSize = resp.Content.Headers.ContentLength ?? -1;
                    It.AcceptRanges = It.TotalSize > 0 && resp.Headers.AcceptRanges.Contains("bytes");
                }
                else if (IsFatalStatus(resp.StatusCode))
                {
                    throw new FatalDownloadException($"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
                else
                {
                    throw new HttpRequestException($"Server returned {(int)resp.StatusCode}");
                }

                It.ETag = resp.Headers.ETag?.Tag;
                It.LastModified = resp.Content.Headers.LastModified?.ToString("R");

                if (string.IsNullOrEmpty(It.FileName))
                {
                    var cd = resp.Content.Headers.ContentDisposition;
                    var name = cd?.FileNameStar ?? cd?.FileName?.Trim('"');
                    It.FileName = !string.IsNullOrWhiteSpace(name)
                        ? FileNames.Sanitize(Path.GetFileName(name.Replace('\\', '/')))
                        : FileNames.FromUrl(resp.RequestMessage?.RequestUri ?? new Uri(It.Url));
                }
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= Ctx.Settings.MaxRetries) throw new FatalDownloadException("Could not reach the server: " + ex.Message);
                await Task.Delay(Backoff(attempt), ct);
            }
        }
    }

    void InitSegments()
    {
        lock (It.Segments)
        {
            It.Segments.Clear();
            It.Downloaded = 0;
            if (It.TotalSize == 0) return;
            if (!It.AcceptRanges || It.TotalSize < 0)
            {
                It.Segments.Add(new Segment { Start = 0, End = It.TotalSize > 0 ? It.TotalSize - 1 : -1 });
                return;
            }
            int n = (int)Math.Clamp(It.TotalSize / MinSegment, 1, Math.Max(1, It.MaxConnections));
            long size = It.TotalSize / n;
            for (int i = 0; i < n; i++)
            {
                long start = i * size;
                It.Segments.Add(new Segment { Start = start, Position = start, End = i == n - 1 ? It.TotalSize - 1 : start + size - 1 });
            }
        }
    }

    Segment? TakeSegment()
    {
        lock (It.Segments)
        {
            if (_eof) return null;
            foreach (var s in It.Segments)
            {
                if (!s.Active && s.Remaining > 0) { s.Active = true; return s; }
            }
            if (!It.AcceptRanges) return null;

            Segment? victim = null;
            foreach (var s in It.Segments)
                if (s.Active && s.End >= 0 && s.Remaining > MinSplit * 2 && (victim == null || s.Remaining > victim.Remaining)) victim = s;
            if (victim == null) return null;

            long mid = victim.Position + victim.Remaining / 2;
            var piece = new Segment { Start = mid, Position = mid, End = victim.End, Active = true };
            victim.End = mid - 1;
            It.Segments.Add(piece);
            return piece;
        }
    }

    async Task WorkerAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var seg = TakeSegment();
                if (seg == null) return;
                bool keepGoing;
                try { keepGoing = await DownloadSegmentAsync(seg, handle, ct); }
                finally { lock (It.Segments) seg.Active = false; }
                if (!keepGoing) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_sync) _fatal ??= ex;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        finally
        {
            lock (_sync) _liveWorkers--;
        }
    }

    /// <summary>Returns false if this worker should retire (the server is limiting connections).</summary>
    async Task<bool> DownloadSegmentAsync(Segment seg, Microsoft.Win32.SafeHandles.SafeFileHandle handle, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (seg.Done) return true;
            if (!It.AcceptRanges) seg.Position = seg.Start;

            TimeSpan? retryAfter = null;
            try
            {
                using var req = Net.Build(It, It.Url, Ctx.Settings.UserAgent);
                if (It.AcceptRanges) req.Headers.Range = new RangeHeaderValue(seg.Position, seg.End);

                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(45));
                using var resp = await Ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token);

                if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    lock (_sync)
                    {
                        if (_liveWorkers > 1) return false; // back off: leave the range to the other connections
                    }
                    retryAfter = resp.Headers.RetryAfter?.Delta;
                    throw new HttpRequestException($"Server returned {(int)resp.StatusCode}");
                }
                if (It.AcceptRanges && resp.StatusCode == HttpStatusCode.OK) throw new RangeRejectedException();
                if (IsFatalStatus(resp.StatusCode)) throw new FatalDownloadException($"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Server returned {(int)resp.StatusCode}");

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        int want = BufferSize;
                        if (seg.End >= 0)
                        {
                            long rem = seg.End - seg.Position + 1;
                            if (rem <= 0) return true;
                            want = (int)Math.Min(want, rem);
                        }

                        readTimeout.CancelAfter(ReadTimeout);
                        int n = await stream.ReadAsync(buffer.AsMemory(0, want), readTimeout.Token);
                        if (n == 0) break;

                        await Ctx.Limiter.WaitAsync(n, ct);

                        if (seg.End >= 0) // the range may have been split while we were reading
                        {
                            long rem = seg.End - seg.Position + 1;
                            if (rem <= 0) return true;
                            if (n > rem) n = (int)rem;
                        }

                        RandomAccess.Write(handle, buffer.AsSpan(0, n), seg.Position);
                        seg.Position += n;
                        AddBytes(n);
                        attempt = 0;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (seg.End < 0)
                {
                    lock (It.Segments) _eof = true;
                    return true;
                }
                if (seg.Position <= seg.End) throw new IOException("Connection closed early");
                return true;
            }
            catch (Exception ex) when (!IsDiskFull(ex) && (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested)))
            {
                if (++attempt > Ctx.Settings.MaxRetries)
                    throw new FatalDownloadException($"Gave up after {Ctx.Settings.MaxRetries} retries: {ex.Message}");
                await Task.Delay(retryAfter ?? Backoff(attempt), ct);
            }
        }
    }

    static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, attempt)));

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
