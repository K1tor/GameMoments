using System.Windows;
using System.Windows.Controls;
using GameMoments.App.ViewModels;
using GameMoments.Core.Clips;

namespace GameMoments.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private static ClipRecord? ClipOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipRecord;

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) Vm.PlayClip(clip);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) Vm.RevealClip(clip);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } clip) return;
        var dialog = new RenameDialog("重命名片段", clip.FileName)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;

        if (!Vm.RenameClip(clip, dialog.Result, out var error))
        {
            MessageBox.Show(error ?? "重命名失败。", "重命名片段", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } clip) return;
        var result = MessageBox.Show(
            $"确定删除片段“{clip.FileName}”吗？\n\n文件会被移入回收站，可从回收站恢复。",
            "删除片段", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        if (Vm.DeleteClip(clip, out var error))
        {
            ClipList.SelectedItem = null;
        }
        else
        {
            MessageBox.Show(error ?? "删除失败。", "删除片段", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.RefreshClips();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Vm.OpenFolder(Vm.Settings.SaveFolder);
}

