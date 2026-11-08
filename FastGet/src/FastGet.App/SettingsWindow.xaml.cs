using System.Diagnostics;
using System.Globalization;
using System.Windows;
using FastGet.Core;
using Microsoft.Win32;

namespace FastGet.App;

public partial class SettingsWindow : Window
{
    readonly DownloadManager _manager;

    public SettingsWindow(DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;
        var s = manager.Settings;
        DirBox.Text = s.DownloadDir;
        CategoriesBox.IsChecked = s.UseCategories;
        ConcurrentBox.Text = s.MaxConcurrent.ToString();
        ConnectionsBox.Text = s.ConnectionsPerDownload.ToString();
        LimitBox.Text = s.SpeedLimitKBps.ToString();
        ScheduleBox.IsChecked = s.ScheduleEnabled;
        StartBox.Text = s.ScheduleStart.ToString(@"hh\:mm");
        StopBox.Text = s.ScheduleStop.ToString(@"hh\:mm");
        StopEnabledBox.IsChecked = s.ScheduleStopEnabled;
        AfterBox.ItemsSource = Enum.GetValues<AfterQueueAction>();
        AfterBox.SelectedItem = s.AfterQueue;
        ConfirmBox.IsChecked = s.ConfirmBrowserDownloads;
        ClipboardBox.IsChecked = s.WatchClipboard;
        TrayBox.IsChecked = s.MinimizeToTray;
        AutoStartBox.IsChecked = s.StartWithWindows;
        Activated += (_, _) => UpdateFfmpegStatus();
    }

    void UpdateFfmpegStatus()
    {
        bool found = MediaJob.FindFfmpeg() != null;
        FfmpegText.Text = found ? "ffmpeg found: videos save as MP4 with audio merged." : "ffmpeg not found: needed for MP4 output and merged audio.";
        FfmpegButton.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
    }

    void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("winget", "install --id Gyan.FFmpeg -e --source winget") { UseShellExecute = true }); }
        catch (Exception ex) { Fail("Couldn't start winget: " + ex.Message); }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DirBox.Text };
        if (dialog.ShowDialog(this) == true) DirBox.Text = dialog.FolderName;
    }

    void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DirBox.Text)) { Fail("Choose a download folder."); return; }
        if (!int.TryParse(ConcurrentBox.Text, out var concurrent) || concurrent is < 1 or > 16) { Fail("Simultaneous downloads must be 1–16."); return; }
        if (!int.TryParse(ConnectionsBox.Text, out var connections) || connections is < 1 or > 32) { Fail("Connections must be 1–32."); return; }
        if (!long.TryParse(LimitBox.Text, out var limit) || limit < 0) { Fail("Speed limit must be 0 or more."); return; }
        if (!TimeSpan.TryParseExact(StartBox.Text, @"h\:mm", CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParseExact(StopBox.Text, @"h\:mm", CultureInfo.InvariantCulture, out var stop))
        { Fail("Enter times as HH:mm, for example 02:30."); return; }

        var s = _manager.Settings;
        s.DownloadDir = DirBox.Text.Trim();
        s.UseCategories = CategoriesBox.IsChecked == true;
        s.MaxConcurrent = concurrent;
        s.ConnectionsPerDownload = connections;
        s.SpeedLimitKBps = limit;
        s.ScheduleEnabled = ScheduleBox.IsChecked == true;
        s.ScheduleStart = start;
        s.ScheduleStop = stop;
        s.ScheduleStopEnabled = StopEnabledBox.IsChecked == true;
        s.AfterQueue = (AfterQueueAction)AfterBox.SelectedItem;
        s.ConfirmBrowserDownloads = ConfirmBox.IsChecked == true;
        s.WatchClipboard = ClipboardBox.IsChecked == true;
        s.MinimizeToTray = TrayBox.IsChecked == true;
        s.StartWithWindows = AutoStartBox.IsChecked == true;

        NativeHostRegistrar.SetAutoStart(s.StartWithWindows);
        _manager.ApplySettings();
        App.Current.ConfigureClipboard();
        Close();
    }
}
