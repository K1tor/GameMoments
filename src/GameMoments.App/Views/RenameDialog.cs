using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameMoments.App.Views;

/// <summary>轻量重命名对话框，避免额外依赖。</summary>
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;

    public RenameDialog(string title, string initialFileName)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)System.Windows.Application.Current.Resources["BgBrush"];
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = "新的文件名（保留 .mp4 扩展名，留空则取消）",
            Foreground = (Brush)System.Windows.Application.Current.Resources["MutedBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        _box = new TextBox
        {
            Text = initialFileName,
            Margin = new Thickness(0, 10, 0, 16),
            Style = System.Windows.Application.Current.Resources["{x:Type TextBox}"] as Style
        };
        _box.SelectAll();
        root.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(16, 7, 16, 7),
            Style = System.Windows.Application.Current.Resources["GhostButton"] as Style
        };
        cancel.Click += (_, _) => DialogResult = false;

        var ok = new Button
        {
            Content = "确定",
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(8, 0, 0, 0),
            Style = System.Windows.Application.Current.Resources["PrimaryButton"] as Style
        };
        ok.Click += (_, _) => Accept();

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _box.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Accept();
            else if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false;
        };
    }

    public string? Result { get; private set; }

    private void Accept()
    {
        var text = _box.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("文件名不能为空。", "重命名片段", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = text;
        DialogResult = true;
    }
}
