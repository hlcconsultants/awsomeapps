using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FastGet.Core;

namespace FastGet.App;

public partial class MainWindow : Window
{
    readonly DownloadManager _manager;
    readonly ObservableCollection<DownloadItem> _items = new();
    readonly ICollectionView _view;

    public MainWindow(DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;

        _manager.ItemAdded += it => Dispatcher.BeginInvoke(() => { if (!_items.Contains(it)) _items.Add(it); });
        _manager.ItemRemoved += it => Dispatcher.BeginInvoke(() => _items.Remove(it));
        foreach (var it in _manager.Snapshot()) if (!_items.Contains(it)) _items.Add(it);

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = Matches;
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.AddedAt), ListSortDirection.Descending));
        Grid.ItemsSource = _view;

        Filters.ItemsSource = new[] { "All downloads", "Active", "Finished", "Unfinished" }.Concat(Categories.All).ToArray();
        Filters.SelectedIndex = 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
        Closing += OnClosing;
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_manager.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            App.Current.ExitApp();
        }
    }

    bool Matches(object o)
    {
        var it = (DownloadItem)o;
        var filter = Filters.SelectedItem as string ?? "All downloads";
        bool category = filter switch
        {
            "All downloads" => true,
            "Active" => it.IsActive || it.Status == DownloadStatus.Queued,
            "Finished" => it.IsCompleted,
            "Unfinished" => !it.IsCompleted,
            _ => it.Category == filter,
        };
        return category && (Search.Text.Length == 0 || it.Display.Contains(Search.Text, StringComparison.OrdinalIgnoreCase));
    }

    void UpdateStatus()
    {
        var all = _manager.Snapshot();
        Summary.Text = $"{all.Length} downloads · {all.Count(i => i.IsActive)} active · {all.Count(i => i.Status == DownloadStatus.Queued)} queued";
        var total = all.Where(i => i.IsActive).Sum(i => i.SpeedBps);
        Speed.Text = total > 0 ? Format.Bytes((long)total) + "/s" : "";
        if (Filters.SelectedIndex is 1 or 2 or 3) _view.Refresh();
    }

    IEnumerable<DownloadItem> Selected => Grid.SelectedItems.Cast<DownloadItem>().ToList();

    void Filters_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => _view?.Refresh();
    void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    void Add_Click(object sender, RoutedEventArgs e) => App.Current.ShowAdd();
    void Resume_Click(object sender, RoutedEventArgs e) { foreach (var i in Selected) _manager.Start(i); }
    void Pause_Click(object sender, RoutedEventArgs e) { foreach (var i in Selected) _manager.Pause(i); }
    void Restart_Click(object sender, RoutedEventArgs e) { foreach (var i in Selected) _manager.Restart(i); }
    void ResumeAll_Click(object sender, RoutedEventArgs e) => _manager.StartAll();
    void PauseAll_Click(object sender, RoutedEventArgs e) => _manager.PauseAll();
    void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_manager) { Owner = this }.ShowDialog();
    void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected(false);
    void RemoveDelete_Click(object sender, RoutedEventArgs e) => RemoveSelected(true);

    void RemoveSelected(bool deleteFiles)
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        if (deleteFiles && MessageBox.Show(this, $"Permanently delete {items.Count} downloaded file(s) from disk?", "FastGet",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var i in items) _manager.Remove(i, deleteFiles);
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelected(false);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected.FirstOrDefault();
        if (it?.FinalPath == null || !File.Exists(it.FinalPath)) return;
        try { Process.Start(new ProcessStartInfo(it.FinalPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FastGet"); }
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected.FirstOrDefault();
        if (it == null) return;
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (it.FinalPath != null && File.Exists(it.FinalPath)) psi.Arguments = $"/select,\"{it.FinalPath}\"";
        else if (Directory.Exists(it.SaveDir)) psi.Arguments = $"\"{it.SaveDir}\"";
        else return;
        Process.Start(psi);
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected.FirstOrDefault();
        if (it != null) Clipboard.SetText(it.Url);
    }

    void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected.FirstOrDefault();
        if (it == null) return;
        var prompt = new PromptWindow("Update link", "Paste a fresh link for this download:", it.Url) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try { _manager.UpdateUrl(it, prompt.Value); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "FastGet"); }
    }
}
