using System.Windows;
using FastGet.Core;
using Microsoft.Win32;

namespace FastGet.App;

public partial class AddDownloadWindow : Window
{
    readonly DownloadManager _manager;
    readonly AddRequest? _request;

    public AddDownloadWindow(DownloadManager manager, AddRequest? request)
    {
        InitializeComponent();
        _manager = manager;
        _request = request;
        UrlBox.Text = request?.Url ?? "";
        NameBox.Text = request?.FileName ?? "";
        ConnBox.ItemsSource = new[] { 1, 2, 4, 8, 12, 16, 24, 32 };
        ConnBox.SelectedItem = manager.Settings.ConnectionsPerDownload;
        if (ConnBox.SelectedItem == null) ConnBox.SelectedItem = 8;
        Loaded += (_, _) => { UrlBox.Focus(); UrlBox.SelectAll(); };
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _manager.Settings.DownloadDir };
        if (dialog.ShowDialog(this) == true) DirBox.Text = dialog.FolderName;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    void Start_Click(object sender, RoutedEventArgs e) => Submit(true);
    void Later_Click(object sender, RoutedEventArgs e) => Submit(false);

    void Submit(bool start)
    {
        var url = UrlBox.Text.Trim();
        if (_manager.FindActiveByUrl(url) != null &&
            MessageBox.Show(this, "This link is already in your list. Add it again?", "FastGet", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            _manager.Add(new DownloadRequest
            {
                Url = url,
                FileName = NameBox.Text.Trim(),
                SaveDir = string.IsNullOrWhiteSpace(DirBox.Text) ? null : DirBox.Text.Trim(),
                Connections = ConnBox.SelectedItem as int?,
                Start = start,
                Referrer = _request?.Referrer,
                Cookie = _request?.Cookie,
                UserAgent = _request?.UserAgent,
                Headers = _request?.Headers,
                Kind = _request?.Url == url ? _request?.Kind : null,
            });
            Close();
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
