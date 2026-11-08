using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FastGet.Core;
using Xunit;

namespace FastGet.Tests;

sealed class TestServer : IDisposable
{
    readonly HttpListener _listener = new();
    int _concurrent;

    public string Base { get; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool Ranges { get; set; } = true;
    public int ChunkDelayMs { get; set; }
    public int MaxConcurrent;
    public long BytesServed;
    public int FailFirst;
    public Dictionary<string, (string Type, byte[] Body)> Routes { get; } = new();

    public TestServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Base);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        int now = Interlocked.Increment(ref _concurrent);
        int seen;
        while (now > (seen = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen) { }
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var res = ctx.Response;
            if (path == "/missing")
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }
            if (Routes.TryGetValue(path, out var route))
            {
                res.ContentType = route.Type;
                res.ContentLength64 = route.Body.Length;
                await res.OutputStream.WriteAsync(route.Body);
                res.Close();
                return;
            }

            var range = ctx.Request.Headers["Range"];
            bool ranged = Ranges && range != null;
            if (range != null && !range.EndsWith("0-0") && Interlocked.Decrement(ref FailFirst) >= 0)
            {
                res.StatusCode = 500;
                res.Close();
                return;
            }

            long start = 0, end = Data.Length - 1;
            if (ranged)
            {
                var p = range!["bytes=".Length..].Split('-');
                start = long.Parse(p[0]);
                if (p[1].Length > 0) end = long.Parse(p[1]);
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {start}-{end}/{Data.Length}";
            }
            res.Headers["ETag"] = "\"v1\"";
            res.ContentLength64 = end - start + 1;

            long pos = start;
            while (pos <= end)
            {
                int n = (int)Math.Min(64 * 1024, end - pos + 1);
                await res.OutputStream.WriteAsync(Data.AsMemory((int)pos, n));
                pos += n;
                Interlocked.Add(ref BytesServed, n);
                if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
            }
            res.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
        finally { Interlocked.Decrement(ref _concurrent); }
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}

public sealed class EngineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fastget-tests-" + Guid.NewGuid().ToString("N"));
    readonly TestServer _server = new();
    readonly DownloadManager _manager;

    public EngineTests()
    {
        Directory.CreateDirectory(_dir);
        _manager = new DownloadManager(new AppSettings { DownloadDir = _dir, UseCategories = false, MaxRetries = 4 }, Path.Combine(_dir, "state.json"));
    }

    public void Dispose()
    {
        _manager.Dispose();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    static byte[] RandomData(int size)
    {
        var b = new byte[size];
        new Random(42).NextBytes(b);
        return b;
    }

    static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    static Task<bool> Finished(DownloadItem it) => WaitAsync(() => it.Status is DownloadStatus.Completed or DownloadStatus.Error);

    static void AssertSameFile(byte[] expected, string path) =>
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    [Fact]
    public async Task Segmented_download_is_byte_identical_and_parallel()
    {
        _server.Data = RandomData(24 * 1024 * 1024);
        _server.ChunkDelayMs = 2;
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "file.bin", Connections = 8 });

        Assert.True(await Finished(it), "timed out");
        Assert.Equal(DownloadStatus.Completed, it.Status);
        AssertSameFile(_server.Data, it.FinalPath!);
        Assert.True(_server.MaxConcurrent >= 4, $"only {_server.MaxConcurrent} concurrent connections");
        Assert.Empty(Directory.GetFiles(_dir, "*.fgpart"));
    }

    [Fact]
    public async Task Server_without_ranges_falls_back_to_one_stream()
    {
        _server.Data = RandomData(3 * 1024 * 1024);
        _server.Ranges = false;
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "plain.bin" });

        Assert.True(await Finished(it), "timed out");
        Assert.Equal(DownloadStatus.Completed, it.Status);
        AssertSameFile(_server.Data, it.FinalPath!);
    }

    [Fact]
    public async Task Transient_server_errors_are_retried()
    {
        _server.Data = RandomData(4 * 1024 * 1024);
        _server.FailFirst = 3;
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "flaky.bin", Connections = 2 });

        Assert.True(await Finished(it), "timed out");
        Assert.Equal(DownloadStatus.Completed, it.Status);
        AssertSameFile(_server.Data, it.FinalPath!);
    }

    [Fact]
    public async Task Pause_and_resume_keeps_progress()
    {
        _server.Data = RandomData(16 * 1024 * 1024);
        _server.ChunkDelayMs = 20;
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "resume.bin", Connections = 4 });

        Assert.True(await WaitAsync(() => it.Downloaded > 4 * 1024 * 1024), "never made progress");
        _manager.Pause(it);
        Assert.True(await WaitAsync(() => it.Status == DownloadStatus.Paused), "never paused");
        var atPause = it.Downloaded;
        Assert.True(atPause > 0 && atPause < _server.Data.Length);

        _server.ChunkDelayMs = 0;
        _manager.Start(it);
        Assert.True(await Finished(it), "timed out");
        Assert.Equal(DownloadStatus.Completed, it.Status);
        AssertSameFile(_server.Data, it.FinalPath!);
        Assert.True(_server.BytesServed < _server.Data.Length * 1.5, $"re-downloaded too much: {_server.BytesServed}");
    }

    [Fact]
    public async Task Missing_file_reports_an_error()
    {
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "missing", FileName = "a.txt" });
        Assert.True(await Finished(it), "timed out");
        Assert.Equal(DownloadStatus.Error, it.Status);
        Assert.Contains("404", it.Error);
    }

    [Fact]
    public async Task Hls_aes128_stream_is_decrypted_and_joined()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var expected = new MemoryStream();
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n");
        for (int i = 0; i < 12; i++)
        {
            var plain = RandomData(50_000 + i);
            expected.Write(plain);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = SequenceIv(7 + i);
            _server.Routes[$"/hls/s{i}.ts"] = ("video/mp2t", aes.EncryptCbc(plain, aes.IV));
            playlist.Append($"#EXTINF:4.0,\ns{i}.ts\n");
        }
        playlist.Append("#EXT-X-ENDLIST\n");
        _server.Routes["/hls/key.bin"] = ("application/octet-stream", key);
        _server.Routes["/hls/index.m3u8"] = ("application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(playlist.ToString()));

        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "hls/index.m3u8", FileName = "Mr. Robot" });
        Assert.True(await Finished(it), "timed out");
        Assert.True(it.Status == DownloadStatus.Completed, it.Error);
        Assert.StartsWith("Mr. Robot", it.FileName);
        if (MediaJob.FindFfmpeg() == null) AssertSameFile(expected.ToArray(), it.FinalPath!);
        Assert.False(Directory.Exists(MediaJob.TempDirFor(it)));
    }

    static byte[] SequenceIv(long n)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), n);
        return iv;
    }

    [Fact]
    public async Task Drm_hls_is_refused()
    {
        _server.Routes["/drm.m3u8"] = ("application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n"));
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "drm.m3u8" });
        Assert.True(await Finished(it), "timed out");
        Assert.Equal(DownloadStatus.Error, it.Status);
        Assert.Contains("DRM", it.Error);
    }

    [Fact]
    public async Task Dash_template_segments_are_joined()
    {
        var init = Encoding.ASCII.GetBytes("INIT");
        var expected = new MemoryStream();
        expected.Write(init);
        _server.Routes["/d/v-init.mp4"] = ("video/mp4", init);
        for (int n = 1; n <= 5; n++)
        {
            var body = RandomData(10_000 * n);
            expected.Write(body);
            _server.Routes[$"/d/v-{n:D3}.m4s"] = ("video/mp4", body);
        }
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
              <Period><AdaptationSet contentType="video">
                <SegmentTemplate initialization="$RepresentationID$-init.mp4" media="$RepresentationID$-$Number%03d$.m4s" startNumber="1" timescale="1" duration="2"/>
                <Representation id="low" bandwidth="100"/><Representation id="v" bandwidth="900"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        _server.Routes["/d/manifest.mpd"] = ("application/dash+xml", Encoding.UTF8.GetBytes(mpd));

        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "d/manifest.mpd" });
        Assert.True(await Finished(it), "timed out");
        Assert.True(it.Status == DownloadStatus.Completed, it.Error);
        AssertSameFile(expected.ToArray(), it.FinalPath!);
    }

    [Fact]
    public void File_names_cannot_escape_the_folder()
    {
        var it = _manager.Add(new DownloadRequest { Url = _server.Base + "x", FileName = @"..\..\evil.exe", Start = false });
        Assert.Equal("evil.exe", it.FileName);
    }

    [Fact]
    public void Only_http_links_are_accepted()
    {
        Assert.Throws<ArgumentException>(() => _manager.Add(new DownloadRequest { Url = "file:///C:/Windows/win.ini" }));
    }
}
