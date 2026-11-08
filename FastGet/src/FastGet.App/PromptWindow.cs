using System.Windows;
using System.Windows.Controls;

namespace FastGet.App;

/// <summary>Minimal one-line text prompt.</summary>
public sealed class PromptWindow : Window
{
    readonly TextBox _box;

    public string Value => _box.Text.Trim();

    public PromptWindow(string title, string label, string value)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.FindResource("Base");

        _box = new TextBox { Text = value, Style = (Style)Application.Current.FindResource("Field"), Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.FindResource("Tool") };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;
    }
}
