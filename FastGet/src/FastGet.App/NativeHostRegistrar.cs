using System.Text.Json;
using FastGet.Core;
using Microsoft.Win32;

namespace FastGet.App;

/// <summary>Per-user registration, so neither the Chrome bridge nor autostart needs admin rights.</summary>
public static class NativeHostRegistrar
{
    const string HostName = "com.fastget.host";
    // The unpacked development ID. Add the Chrome Web Store ID here once the store assigns one.
    static readonly string[] ExtensionIds = { "aecappmgflfoecmdgjeiblccfgkmibfp" };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Register()
    {
        var hostExe = Path.Combine(AppContext.BaseDirectory, "FastGet.Host.exe");
        var manifestPath = Path.Combine(AppSettings.DataDir, HostName + ".json");
        var manifest = new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "FastGet browser integration",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = ExtensionIds.Select(id => $"chrome-extension://{id}/").ToArray(),
        };
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}");
        key.SetValue("", manifestPath);
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("FastGet", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("FastGet", false);
    }
}
