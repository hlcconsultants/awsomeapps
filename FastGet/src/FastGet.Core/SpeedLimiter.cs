using System.Diagnostics;

namespace FastGet.Core;

/// <summary>Global token-bucket bandwidth limiter shared by all connections.</summary>
public sealed class SpeedLimiter
{
    readonly object _lock = new();
    long _limit;
    double _tokens;
    long _last = Stopwatch.GetTimestamp();

    /// <summary>Bytes per second. 0 = unlimited.</summary>
    public long Limit
    {
        get => Interlocked.Read(ref _limit);
        set => Interlocked.Exchange(ref _limit, Math.Max(0, value));
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        long limit = Limit;
        if (limit <= 0) return;

        double delaySec;
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _last) / (double)Stopwatch.Frequency;
            _last = now;
            _tokens = Math.Min(limit, _tokens + elapsed * limit); // at most 1s burst
            _tokens -= bytes;
            delaySec = _tokens < 0 ? -_tokens / limit : 0;
        }
        if (delaySec > 0.001) await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
    }
}
