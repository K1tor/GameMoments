using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GameMoments.Core.Gaming;
using GameMoments.App.ViewModels;

namespace GameMoments.App.Views;

public partial class SmartView : UserControl
{
    private bool _loadedOnce;

    public SmartView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.UpdateSmartFromService();
        Vm.RefreshEvents();
        if (_loadedOnce) return;
        _loadedOnce = true;
    }

    private async void ToggleSmart_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await RunGuardedAsync(() => Vm.ToggleSmartAsync());
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await RunGuardedAsync(() => Vm.RefreshSmartAsync());
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        string message = await RunGuardedAsync(() => Vm.RepairEventSourceAsync()) ?? "修复未完成。";
        GsiStatusText.Text = message;
    }

    private async void StopWhole_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await RunGuardedAsync(() => Vm.StopWholeRecordingAsync());
    }

    private async void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        BookmarkResult result = await RunGuardedAsync(() => Vm.BookmarkNowAsync()) ?? new BookmarkResult(false, "操作未完成。");
        GsiStatusText.Text = result.Success ? $"已保存书签：{result.Message}" : result.Message;
    }

    private async void SaveEvent_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if ((sender as FrameworkElement)?.Tag is not SmartEventRecord record) return;
        await RunGuardedAsync(() => Vm.SaveEventFromCacheAsync(record));
    }

    private void OpenEventClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmartEventRecord record) return;
        string? path = record.ClipPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            GsiStatusText.Text = "片段文件不存在，可能已被移动或删除。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GsiStatusText.Text = $"打开片段失败：{ex.Message}";
        }
    }

    private void FalsePositive_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if ((sender as FrameworkElement)?.Tag is not SmartEventRecord record) return;
        Vm.MarkFalsePositive(record, true);
        GsiStatusText.Text = "已标记为误触发，可用于排查事件规则。";
    }

    private void ClearEvents_Click(object sender, RoutedEventArgs e)
    {
        Vm?.ClearEvents();
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        string text = CustomGameBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            GsiStatusText.Text = "请先填写进程名，例如 mygame.exe。";
            return;
        }

        string message = Vm.AddCustomGame(text);
        GsiStatusText.Text = message;
        if (message.StartsWith("已添加", StringComparison.Ordinal))
        {
            CustomGameBox.Text = string.Empty;
        }
    }

    private void RemoveCustom_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if ((sender as FrameworkElement)?.Tag is not string entry) return;
        GsiStatusText.Text = Vm.RemoveCustomGame(entry);
    }

    private void OpenGsiPath_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        string path = Vm.GsiConfigPathText;
        if (string.IsNullOrWhiteSpace(path))
        {
            GsiStatusText.Text = "还没有解析到配置路径，请先点“修复事件源”。";
            return;
        }

        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            GsiStatusText.Text = "配置目录不存在，可能尚未安装过 GSI 配置。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GsiStatusText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private void UninstallGsi_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        MessageBoxResult confirm = MessageBox.Show(
            "将删除本程序写入的 gamestate_integration_gamemoments.cfg。其他软件的配置文件不会被动到。继续吗？",
            "卸载 GSI 配置",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        GsiStatusText.Text = Vm.UninstallGsiConfig();
    }

    private static async Task<T?> RunGuardedAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // 页面按钮不应把异常抛到 UI 线程；具体错误已由视图模型写入状态文案。
        }
    }
}
