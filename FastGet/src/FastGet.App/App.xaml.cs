using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FastGet.Core;
using Forms = System.Windows.Forms;

namespace FastGet.App;

public partial class App : Application
{
    Mutex? _mutex;
    IpcServer? _ipc;
    Forms.NotifyIcon? _tray;
    MainWindow? _main;
    DispatcherTimer? _clipboard;
    string _lastClip = "";

    public DownloadManager Manager { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--register")) // run by the installer so Chrome works before the first launch
        {
            try { NativeHostRegistrar.Register(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\FastGet.SingleInstance", out bool first);
        if (!first)
        {
            try { Task.Run(() => IpcClient.SendAsync(new IpcMessage { Cmd = "show" }, TimeSpan.FromSeconds(2))).Wait(3000); }
            catch { /* the running instance is busy; nothing more to do */ }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        Manager = new DownloadManager(settings);
        Manager.ItemCompleted += it => Notify("Download complete", it.FileName);
        Manager.ItemFailed += it => Notify("Download failed", $"{it.Display}: {it.Error}");
        Manager.QueueFinished += a => Dispatcher.BeginInvoke(() => OnQueueFinished(a));

        try { NativeHostRegistrar.Register(); }
        catch (Exception ex) { Debug.WriteLine(ex); } // the app still works without browser integration

        _main = new MainWindow(Manager);
        BuildTray();
        _ipc = new IpcServer(HandleIpcAsync);
        _ipc.Start();
        ConfigureClipboard();

        if (!e.Args.Contains("--minimized")) ShowMain();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    Task<IpcReply> HandleIpcAsync(IpcMessage m)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        switch (m.Cmd)
        {
            case "ping":
                return Task.FromResult(new IpcReply { Ok = true, Version = version });
            case "show":
                Dispatcher.BeginInvoke(ShowMain);
                return Task.FromResult(new IpcReply { Ok = true, Version = version });
            case "add":
                if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    return Task.FromResult(new IpcReply { Ok = false, Error = "Unsupported link" });
                Dispatcher.BeginInvoke(() => AcceptBrowserDownload(m));
                return Task.FromResult(new IpcReply { Ok = true, Version = version });
            default:
                return Task.FromResult(new IpcReply { Ok = false, Error = "Unknown command" });
        }
    }

    void AcceptBrowserDownload(IpcMessage m)
    {
        var request = new AddRequest(m.Url!, m.FileName, m.Referrer, m.Cookie, m.UserAgent, m.Headers,
            Enum.TryParse<DownloadKind>(m.Kind, true, out var k) ? k : null);

        if (Manager.Settings.ConfirmBrowserDownloads)
        {
            ShowAdd(request);
            return;
        }
        try
        {
            Manager.Add(new DownloadRequest
            {
                Url = request.Url, FileName = request.FileName, Referrer = request.Referrer, Cookie = request.Cookie,
                UserAgent = request.UserAgent, Headers = request.Headers, Kind = request.Kind,
            });
        }
        catch (ArgumentException ex) { Notify("Could not add download", ex.Message); }
    }

    public void ShowAdd(AddRequest? request = null) => new AddDownloadWindow(Manager, request).Show();

    void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open FastGet", null, (_, _) => ShowMain());
        menu.Items.Add("Add URL…", null, (_, _) => ShowAdd());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Resume all", null, (_, _) => Manager.StartAll());
        menu.Items.Add("Pause all", null, (_, _) => Manager.PauseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Text = "FastGet",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    void Notify(string title, string text) => Dispatcher.BeginInvoke(() =>
    {
        if (string.IsNullOrEmpty(text) || _tray == null) return;
        _tray.ShowBalloonTip(4000, title, text.Length > 200 ? text[..200] : text, Forms.ToolTipIcon.Info);
    });

    public void ConfigureClipboard()
    {
        _clipboard?.Stop();
        _clipboard = null;
        if (!Manager.Settings.WatchClipboard) return;

        try { _lastClip = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.COMException) { }

        _clipboard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _clipboard.Tick += (_, _) => CheckClipboard();
        _clipboard.Start();
    }

    void CheckClipboard()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (System.Runtime.InteropServices.COMException) { return; } // another app holds the clipboard

        if (text == _lastClip) return;
        _lastClip = text;
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        if (Categories.For(uri.AbsolutePath) is "Compressed" or "Programs" or "Video" or "Music") ShowAdd(new AddRequest(text));
    }

    void OnQueueFinished(AfterQueueAction action)
    {
        switch (action)
        {
            case AfterQueueAction.Exit:
                ExitApp();
                break;
            case AfterQueueAction.Sleep:
                Forms.Application.SetSuspendState(Forms.PowerState.Suspend, false, false);
                break;
            case AfterQueueAction.Shutdown:
                // A one-minute delay leaves time for "shutdown /a".
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false });
                Notify("Shutting down", "All downloads finished. Windows will shut down in 60 seconds.");
                break;
        }
    }

    public void ExitApp()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _clipboard?.Stop();
        _ipc?.Dispose();
        Manager?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
        Shutdown();
    }
}

public sealed record AddRequest(
    string Url, string? FileName = null, string? Referrer = null, string? Cookie = null, string? UserAgent = null,
    Dictionary<string, string>? Headers = null, DownloadKind? Kind = null);
