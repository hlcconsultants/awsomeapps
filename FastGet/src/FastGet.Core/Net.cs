using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FastGet.Core;

public static class Net
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // byte ranges refer to the raw entity
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64,
        };
        return new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Builds a request carrying the browser context (cookies, referrer, UA, extra headers).
    /// Forces HTTP/1.1 so every parallel segment gets its own TCP connection (HTTP/2 would multiplex them onto one).
    /// </summary>
    public static HttpRequestMessage Build(DownloadItem it, string url, string? defaultUserAgent = null)
    {
        var uri = new Uri(url);
        string? basic = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
            uri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        }

        var req = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        req.Headers.TryAddWithoutValidation("User-Agent", it.UserAgent ?? defaultUserAgent ?? DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrEmpty(it.Referrer)) req.Headers.TryAddWithoutValidation("Referer", it.Referrer);
        if (!string.IsNullOrEmpty(it.Cookie)) req.Headers.TryAddWithoutValidation("Cookie", it.Cookie);
        if (basic != null) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        foreach (var (k, v) in it.Headers)
        {
            if (k.Equals("range", StringComparison.OrdinalIgnoreCase) || k.Equals("host", StringComparison.OrdinalIgnoreCase)
                || k.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        return req;
    }
}

public static class FileNames
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        var sb = new StringBuilder(name.Length);
        var bad = Path.GetInvalidFileNameChars();
        foreach (var c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0) return "download";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(s))) s = "_" + s;
        if (s.Length > 180)
        {
            var ext = Path.GetExtension(s);
            s = s[..(180 - ext.Length)] + ext;
        }
        return s;
    }

    public static string FromUrl(Uri uri)
    {
        var n = Path.GetFileName(uri.AbsolutePath);
        try { n = Uri.UnescapeDataString(n); } catch { }
        return Sanitize(string.IsNullOrEmpty(n) ? uri.Host : n);
    }

    public static string Unique(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 10000; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }
}

public sealed class FatalDownloadException(string message) : Exception(message);

public sealed class RangeRejectedException() : Exception("Server ignored the Range request");
