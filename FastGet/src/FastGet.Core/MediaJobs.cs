using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FastGet.Core;

internal sealed record MediaSegment(string Url, long? RangeStart = null, long? RangeLength = null, byte[]? Key = null, byte[]? Iv = null);

/// <summary>Shared logic for segmented streams: parallel fetch, resume by file, concat, ffmpeg mux.</summary>
public abstract class MediaJob(DownloadItem item, EngineContext ctx) : DownloadJob(item, ctx)
{
    long _doneBytes;
    int _doneCount;
    int _totalCount;

    protected string TempDir => TempDirFor(It);
    public static string TempDirFor(DownloadItem it) => Path.Combine(it.SaveDir, $".fastget-{it.Id:N}");

    public override void Refresh()
    {
        long done = Interlocked.Read(ref _doneBytes);
        int count = Volatile.Read(ref _doneCount);
        It.Downloaded = done;
        if (count > 0 && _totalCount > 0)
        {
            It.SizeEstimated = count < _totalCount;
            It.TotalSize = count >= _totalCount ? done : (long)(done / (double)count * _totalCount);
        }
    }

    protected async Task<(string Text, Uri Final)> GetTextAsync(string url, CancellationToken ct)
    {
        using var req = Net.Build(It, url, Ctx.Settings.UserAgent);
        using var resp = await Ctx.Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode) throw new FatalDownloadException($"Server returned {(int)resp.StatusCode} for the stream manifest");
        return (await resp.Content.ReadAsStringAsync(ct), resp.RequestMessage?.RequestUri ?? new Uri(url));
    }

    protected async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var req = Net.Build(It, url, Ctx.Settings.UserAgent);
        using var resp = await Ctx.Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode) throw new FatalDownloadException($"Server returned {(int)resp.StatusCode} for a stream key");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    protected void EnsureBaseName(string fallbackUrl)
    {
        if (string.IsNullOrEmpty(It.FileName))
            It.FileName = Path.GetFileNameWithoutExtension(FileNames.FromUrl(new Uri(fallbackUrl)));
        Ctx.NameResolved?.Invoke(It);
    }

    internal void BeginTracks(int totalSegments)
    {
        _totalCount = totalSegments;
        _doneBytes = 0;
        _doneCount = 0;
        It.AcceptRanges = false;
    }

    internal async Task<List<string>> DownloadTrackAsync(string name, IReadOnlyList<MediaSegment> segments, CancellationToken ct)
    {
        var dir = Path.Combine(TempDir, name);
        Directory.CreateDirectory(dir);
        var files = segments.Select((_, i) => Path.Combine(dir, $"{i:D6}.seg")).ToList();

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(It.MaxConnections, 1, 16), CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, segments.Count), options, async (i, token) =>
        {
            if (File.Exists(files[i]))
            {
                Interlocked.Add(ref _doneBytes, new FileInfo(files[i]).Length);
                Interlocked.Increment(ref _doneCount);
                return;
            }
            await FetchSegmentAsync(segments[i], files[i], token);
            Interlocked.Increment(ref _doneCount);
        });
        return files;
    }

    async Task FetchSegmentAsync(MediaSegment seg, string dest, CancellationToken ct)
    {
        var tmp = dest + ".tmp";
        for (int attempt = 0; ; attempt++)
        {
            long written = 0;
            try
            {
                using var req = Net.Build(It, seg.Url, Ctx.Settings.UserAgent);
                if (seg.RangeStart is long start)
                    req.Headers.Range = new RangeHeaderValue(start, seg.RangeLength is long len ? start + len - 1 : null);

                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(45));
                using var resp = await Ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token);
                if (IsFatalStatus(resp.StatusCode)) throw new FatalDownloadException($"Server returned {(int)resp.StatusCode} for a stream segment");
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Server returned {(int)resp.StatusCode}");

                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
                {
                    using var aes = seg.Key != null ? CreateAes(seg.Key, seg.Iv!) : null;
                    Stream output = file;
                    CryptoStream? crypto = null;
                    if (aes != null) output = crypto = new CryptoStream(file, aes.CreateDecryptor(), CryptoStreamMode.Write, leaveOpen: true);
                    try
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                        try
                        {
                            while (true)
                            {
                                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                                int n = await input.ReadAsync(buffer.AsMemory(0, 64 * 1024), readTimeout.Token);
                                if (n == 0) break;
                                await Ctx.Limiter.WaitAsync(n, ct);
                                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                                written += n;
                                Interlocked.Add(ref _doneBytes, n);
                                AddBytes(n);
                            }
                        }
                        finally { ArrayPool<byte>.Shared.Return(buffer); }
                    }
                    finally
                    {
                        if (crypto != null) await crypto.DisposeAsync();
                    }
                }
                File.Move(tmp, dest, true);
                return;
            }
            catch (Exception ex) when (!IsDiskFull(ex) && (ex is HttpRequestException or IOException or CryptographicException || (ex is OperationCanceledException && !ct.IsCancellationRequested)))
            {
                Interlocked.Add(ref _doneBytes, -written);
                if (attempt >= Ctx.Settings.MaxRetries) throw new FatalDownloadException($"Stream segment failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(15000, 500 * Math.Pow(2, attempt))), ct);
            }
        }
    }

    static Aes CreateAes(byte[] key, byte[] iv)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        return aes;
    }

    protected static async Task ConcatAsync(IEnumerable<string> parts, string output, CancellationToken ct)
    {
        await using var o = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        foreach (var part in parts)
        {
            await using var i = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await i.CopyToAsync(o, ct);
        }
    }

    protected async Task FinishAsync(List<string> video, List<string>? audio, string videoExt, string audioExt, CancellationToken ct)
    {
        It.Status = DownloadStatus.Merging;
        Refresh();

        var rawVideo = Path.Combine(TempDir, "video" + videoExt);
        await ConcatAsync(video, rawVideo, ct);
        string? rawAudio = null;
        if (audio != null)
        {
            rawAudio = Path.Combine(TempDir, "audio" + audioExt);
            await ConcatAsync(audio, rawAudio, ct);
        }

        // Names here are often page titles ("Mr. Robot"), so only strip a real media extension.
        var stem = It.FileName;
        if (Path.GetExtension(stem).ToLowerInvariant() is ".mp4" or ".ts" or ".m3u8" or ".mpd" or ".mkv" or ".webm" or ".m4a" or ".aac")
            stem = Path.GetFileNameWithoutExtension(stem);
        string? final = null;
        var ffmpeg = FindFfmpeg();

        if (ffmpeg != null && (rawAudio != null || videoExt == ".ts"))
        {
            var target = FileNames.Unique(It.SaveDir, stem + ".mp4");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", rawVideo };
            if (rawAudio != null) args.AddRange(new[] { "-i", rawAudio, "-map", "0:v:0", "-map", "1:a:0" });
            args.AddRange(new[] { "-c", "copy", target });
            if (await RunProcessAsync(ffmpeg, args, ct)) final = target;
            else TryDeleteFile(target);
        }

        if (final == null)
        {
            final = FileNames.Unique(It.SaveDir, stem + videoExt);
            File.Move(rawVideo, final);
            if (rawAudio != null)
            {
                File.Move(rawAudio, FileNames.Unique(It.SaveDir, stem + ".audio" + audioExt));
                It.Note = "audio saved separately (install ffmpeg to merge)";
            }
        }

        It.FinalPath = final;
        It.FileName = Path.GetFileName(final);
        It.TotalSize = new FileInfo(final).Length;
        It.Downloaded = It.TotalSize;
        It.SizeEstimated = false;
        try { Directory.Delete(TempDir, true); } catch { /* left for manual cleanup */ }
    }

    static void TryDeleteFile(string p) { try { File.Delete(p); } catch { } }

    public static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        // Checked directly because a running app doesn't see PATH changes from a new winget install.
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(winget)) return winget;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static async Task<bool> RunProcessAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var drainErr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var drainOut = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        await Task.WhenAll(drainErr, drainOut);
        return p.ExitCode == 0;
    }
}

public sealed partial class HlsJob(DownloadItem item, EngineContext ctx) : MediaJob(item, ctx)
{
    [GeneratedRegex("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)")]
    private static partial Regex AttrRegex();

    static Dictionary<string, string> Attrs(string s) =>
        AttrRegex().Matches(s).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'));

    readonly Dictionary<string, byte[]> _keys = new();

    public override async Task RunAsync(CancellationToken ct)
    {
        var (text, final) = await GetTextAsync(It.Url, ct);
        if (!text.TrimStart().StartsWith("#EXTM3U")) throw new FatalDownloadException("Not a valid HLS playlist");
        EnsureBaseName(It.Url);
        Directory.CreateDirectory(TempDir);

        Uri? audioUri = null;
        Uri videoUri = final;
        if (text.Contains("#EXT-X-STREAM-INF"))
        {
            (videoUri, audioUri) = PickVariant(text, final);
            (text, videoUri) = await GetTextAsync(videoUri.ToString(), ct);
        }

        var (video, fmp4) = await ParseMediaAsync(text, videoUri, ct);
        List<MediaSegment>? audio = null;
        bool audioFmp4 = false;
        if (audioUri != null)
        {
            var (audioText, audioFinal) = await GetTextAsync(audioUri.ToString(), ct);
            (audio, audioFmp4) = await ParseMediaAsync(audioText, audioFinal, ct);
        }

        BeginTracks(video.Count + (audio?.Count ?? 0));
        var videoFiles = await DownloadTrackAsync("v", video, ct);
        var audioFiles = audio != null ? await DownloadTrackAsync("a", audio, ct) : null;
        await FinishAsync(videoFiles, audioFiles, fmp4 ? ".mp4" : ".ts", audioFmp4 ? ".m4a" : ".aac", ct);
    }

    static (Uri Video, Uri? Audio) PickVariant(string master, Uri baseUri)
    {
        var lines = master.Split('\n').Select(l => l.Trim()).ToList();
        var audioGroups = new List<Dictionary<string, string>>();
        long best = -1;
        Uri? bestUri = null;
        string? bestGroup = null;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("#EXT-X-MEDIA:"))
            {
                var a = Attrs(lines[i][13..]);
                if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.ContainsKey("URI")) audioGroups.Add(a);
            }
            else if (lines[i].StartsWith("#EXT-X-STREAM-INF:"))
            {
                var a = Attrs(lines[i][18..]);
                long bw = long.TryParse(a.GetValueOrDefault("BANDWIDTH"), out var v) ? v : 0;
                int j = i + 1;
                while (j < lines.Count && (lines[j].Length == 0 || lines[j].StartsWith('#'))) j++;
                if (j < lines.Count && bw > best)
                {
                    best = bw;
                    bestUri = new Uri(baseUri, lines[j]);
                    bestGroup = a.GetValueOrDefault("AUDIO");
                }
            }
        }
        if (bestUri == null) throw new FatalDownloadException("HLS playlist has no playable variant");

        var group = audioGroups.Where(a => a.GetValueOrDefault("GROUP-ID") == bestGroup).ToList();
        var pick = group.FirstOrDefault(a => a.GetValueOrDefault("DEFAULT") == "YES") ?? group.FirstOrDefault();
        return (bestUri, pick != null ? new Uri(baseUri, pick["URI"]) : null);
    }

    async Task<(List<MediaSegment> Segments, bool Fmp4)> ParseMediaAsync(string text, Uri baseUri, CancellationToken ct)
    {
        if (!text.Contains("#EXT-X-ENDLIST")) throw new FatalDownloadException("Live streams are not supported");

        var result = new List<MediaSegment>();
        byte[]? key = null;
        byte[]? explicitIv = null;
        long sequence = 0;
        long? nextLength = null;
        long nextOffset = 0;
        bool fmp4 = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
            {
                long.TryParse(line[22..], out sequence);
            }
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var a = Attrs(line[11..]);
                var method = a.GetValueOrDefault("METHOD", "NONE");
                if (method == "NONE") { key = null; explicitIv = null; continue; }
                if (method != "AES-128") throw new FatalDownloadException("This stream is DRM-protected and can't be downloaded");
                key = await GetKeyAsync(new Uri(baseUri, a["URI"]).ToString(), ct);
                explicitIv = a.TryGetValue("IV", out var iv) ? Convert.FromHexString(iv.Replace("0x", "").Replace("0X", "").PadLeft(32, '0')) : null;
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var a = Attrs(line[11..]);
                fmp4 = true;
                long? start = null, len = null;
                if (a.TryGetValue("BYTERANGE", out var br)) (start, len) = ParseByteRange(br, 0);
                result.Add(new MediaSegment(new Uri(baseUri, a["URI"]).ToString(), start, len));
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                var (s, l) = ParseByteRange(line[17..], nextOffset);
                nextOffset = s;
                nextLength = l;
            }
            else if (!line.StartsWith('#'))
            {
                byte[]? iv = null;
                if (key != null)
                {
                    iv = explicitIv;
                    if (iv == null)
                    {
                        iv = new byte[16];
                        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
                    }
                }
                var uri = new Uri(baseUri, line).ToString();
                if (nextLength is long length)
                {
                    result.Add(new MediaSegment(uri, nextOffset, length, key, iv));
                    nextOffset += length;
                    nextLength = null;
                }
                else
                {
                    result.Add(new MediaSegment(uri, null, null, key, iv));
                }
                sequence++;
            }
        }
        if (result.Count == 0) throw new FatalDownloadException("HLS playlist contains no segments");
        return (result, fmp4);
    }

    static (long Start, long Length) ParseByteRange(string s, long defaultStart)
    {
        var p = s.Split('@');
        return (p.Length > 1 ? long.Parse(p[1], CultureInfo.InvariantCulture) : defaultStart, long.Parse(p[0], CultureInfo.InvariantCulture));
    }

    async Task<byte[]> GetKeyAsync(string url, CancellationToken ct)
    {
        if (_keys.TryGetValue(url, out var k)) return k;
        k = await GetBytesAsync(url, ct);
        if (k.Length != 16) throw new FatalDownloadException("This stream is DRM-protected and can't be downloaded");
        _keys[url] = k;
        return k;
    }
}

public sealed partial class DashJob(DownloadItem item, EngineContext ctx) : MediaJob(item, ctx)
{
    [GeneratedRegex(@"\$(RepresentationID|Number|Time|Bandwidth)(?:%0(\d+)d)?\$")]
    private static partial Regex TemplateRegex();

    static IEnumerable<XElement> Kids(XElement e, string n) => e.Elements().Where(x => x.Name.LocalName == n);
    static XElement? Kid(XElement? e, string n) => e == null ? null : Kids(e, n).FirstOrDefault();
    static string? Attr(XElement? e, string n) => e?.Attribute(n)?.Value;
    static long LongAttr(XElement? e, string n, long fallback) => long.TryParse(Attr(e, n), out var v) ? v : fallback;

    static Uri Apply(Uri b, XElement? e)
    {
        var t = Kid(e, "BaseURL")?.Value.Trim();
        return string.IsNullOrEmpty(t) ? b : new Uri(b, t);
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var (text, final) = await GetTextAsync(It.Url, ct);
        XElement mpd;
        try { mpd = XDocument.Parse(text).Root!; }
        catch (XmlException) { throw new FatalDownloadException("Not a valid DASH manifest"); }

        if (Attr(mpd, "type") == "dynamic") throw new FatalDownloadException("Live streams are not supported");
        EnsureBaseName(It.Url);
        Directory.CreateDirectory(TempDir);

        var period = Kid(mpd, "Period") ?? throw new FatalDownloadException("DASH manifest has no Period");
        double seconds = 0;
        var dur = Attr(period, "duration") ?? Attr(mpd, "mediaPresentationDuration");
        if (dur != null) seconds = XmlConvert.ToTimeSpan(dur).TotalSeconds;

        var baseUri = Apply(Apply(final, mpd), period);
        var (videoSet, videoRep) = PickBest(period, "video");
        var (audioSet, audioRep) = PickBest(period, "audio");
        if (videoRep == null && audioRep == null) throw new FatalDownloadException("DASH manifest has no playable tracks");

        var video = videoRep != null ? BuildSegments(baseUri, period, videoSet!, videoRep, seconds) : null;
        var audio = audioRep != null ? BuildSegments(baseUri, period, audioSet!, audioRep, seconds) : null;

        BeginTracks((video?.Count ?? 0) + (audio?.Count ?? 0));
        if (video != null)
        {
            var v = await DownloadTrackAsync("v", video, ct);
            var a = audio != null ? await DownloadTrackAsync("a", audio, ct) : null;
            await FinishAsync(v, a, ".mp4", ".m4a", ct);
        }
        else
        {
            var a = await DownloadTrackAsync("a", audio!, ct);
            await FinishAsync(a, null, ".m4a", ".m4a", ct);
        }
    }

    static (XElement? Set, XElement? Rep) PickBest(XElement period, string type)
    {
        XElement? bestSet = null, bestRep = null;
        long best = -1;
        foreach (var set in Kids(period, "AdaptationSet"))
        {
            foreach (var rep in Kids(set, "Representation"))
            {
                var kind = Attr(set, "contentType") ?? (Attr(set, "mimeType") ?? Attr(rep, "mimeType") ?? "").Split('/')[0];
                if (kind != type) continue;
                if (Kid(set, "ContentProtection") != null || Kid(rep, "ContentProtection") != null)
                    throw new FatalDownloadException("This stream is DRM-protected and can't be downloaded");
                long bw = LongAttr(rep, "bandwidth", 0);
                if (bw > best) { best = bw; bestSet = set; bestRep = rep; }
            }
        }
        return (bestSet, bestRep);
    }

    static List<MediaSegment> BuildSegments(Uri periodBase, XElement period, XElement set, XElement rep, double seconds)
    {
        var baseUri = Apply(Apply(periodBase, set), rep);
        var id = Attr(rep, "id") ?? "";
        var bandwidth = Attr(rep, "bandwidth") ?? "0";
        var result = new List<MediaSegment>();

        string Expand(string template, long number, long time) => TemplateRegex().Replace(template, m =>
        {
            long? v = m.Groups[1].Value switch { "Number" => number, "Time" => time, _ => null };
            if (v == null) return m.Groups[1].Value == "RepresentationID" ? id : bandwidth;
            return m.Groups[2].Success ? v.Value.ToString("D" + m.Groups[2].Value, CultureInfo.InvariantCulture) : v.Value.ToString(CultureInfo.InvariantCulture);
        }).Replace("$$", "$");

        var template = Kid(rep, "SegmentTemplate") ?? Kid(set, "SegmentTemplate") ?? Kid(period, "SegmentTemplate");
        if (template != null)
        {
            var init = Attr(template, "initialization");
            var media = Attr(template, "media") ?? throw new FatalDownloadException("DASH template has no media URL");
            long number = LongAttr(template, "startNumber", 1);
            long timescale = LongAttr(template, "timescale", 1);
            if (init != null) result.Add(new MediaSegment(new Uri(baseUri, Expand(init, 0, 0)).ToString()));

            var timeline = Kid(template, "SegmentTimeline");
            if (timeline != null)
            {
                long t = 0;
                foreach (var s in Kids(timeline, "S"))
                {
                    t = LongAttr(s, "t", t);
                    long d = LongAttr(s, "d", 0);
                    long r = LongAttr(s, "r", 0);
                    if (r < 0) r = d > 0 ? (long)Math.Ceiling((seconds * timescale - t) / d) - 1 : 0;
                    for (long i = 0; i <= r; i++)
                    {
                        result.Add(new MediaSegment(new Uri(baseUri, Expand(media, number++, t)).ToString()));
                        t += d;
                    }
                }
            }
            else
            {
                long duration = LongAttr(template, "duration", 0);
                if (duration <= 0 || seconds <= 0) throw new FatalDownloadException("Unsupported DASH manifest (no segment duration)");
                long count = (long)Math.Ceiling(seconds / (duration / (double)timescale));
                for (long i = 0; i < count; i++)
                    result.Add(new MediaSegment(new Uri(baseUri, Expand(media, number++, i * duration)).ToString()));
            }
            return result;
        }

        var list = Kid(rep, "SegmentList") ?? Kid(set, "SegmentList");
        if (list != null)
        {
            var init = Attr(Kid(list, "Initialization"), "sourceURL");
            if (init != null) result.Add(new MediaSegment(new Uri(baseUri, init).ToString()));
            foreach (var s in Kids(list, "SegmentURL"))
                if (Attr(s, "media") is { } m) result.Add(new MediaSegment(new Uri(baseUri, m).ToString()));
            return result;
        }

        result.Add(new MediaSegment(baseUri.ToString())); // single-file representation
        return result;
    }
}
