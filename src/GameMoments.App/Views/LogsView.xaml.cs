using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameMoments.App.ViewModels;
using Microsoft.Win32;

namespace GameMoments.App.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Vm.ReloadLogs();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Vm.OpenFolder(Vm.Services.Paths.LogDirectory);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断包",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
            FileName = $"GameMoments-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Directory.Exists(desktop) ? desktop : Vm.Services.Paths.DataDirectory
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var path = Vm.ExportDiagnostics(dialog.FileName);
            MessageBox.Show($"诊断包已导出：\n{path}", "导出诊断包", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "导出诊断包", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
