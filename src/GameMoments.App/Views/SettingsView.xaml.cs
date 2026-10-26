using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameMoments.App.ViewModels;
using GameMoments.Core.Audio;
using GameMoments.Core.Hotkeys;
using GameMoments.Core.Platform;
using Microsoft.Win32;

namespace GameMoments.App.Views;

public partial class SettingsView : UserControl
{
    private sealed record OptionItem(object Value, string Text);

    private static readonly OptionItem[] ReplayOptions =
    [
        new(15, "15 秒"), new(30, "30 秒"), new(60, "60 秒"), new(120, "120 秒")
    ];

    private static readonly OptionItem[] PostOptions =
    [
        new(0, "立即结束"), new(5, "5 秒"), new(10, "10 秒")
    ];

    private static readonly OptionItem[] FpsOptions =
    [
        new(30, "30 fps"), new(60, "60 fps")
    ];

    private static readonly OptionItem[] QualityOptions =
    [
        new("low", "流畅（约 8 Mbps）"),
        new("medium", "标准（约 14 Mbps）"),
        new("high", "高质量（约 24 Mbps）")
    ];

    private static readonly OptionItem[] SourceOptions =
    [
        new("auto", "自动（优先桌面复制）"),
        new("ddagrab", "桌面复制（DDA / GPU）"),
        new("gdigrab", "GDI 捕获（兼容优先）")
    ];

    private static readonly OptionItem[] EncoderOptions =
    [
        new("auto", "自动（优先硬件编码）"),
        new("hardware", "优先硬件编码"),
        new("software", "仅软件编码（x264）")
    ];

    private readonly DispatcherTimer _statusTimer;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();

        FpsCombo.ItemsSource = FpsOptions;
        ReplayCombo.ItemsSource = ReplayOptions;
        PostCombo.ItemsSource = PostOptions;
        QualityCombo.ItemsSource = QualityOptions;
        SourceCombo.ItemsSource = SourceOptions;
        EncoderCombo.ItemsSource = EncoderOptions;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            SaveStatus.Text = string.Empty;
        };

        Loaded += (_, _) => SyncSelections();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) SyncSelections();
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void SyncSelections()
    {
        _loading = true;
        try
        {
            DisplayCombo.ItemsSource = Vm.Displays;
            DisplayCombo.SelectedItem = Vm.Displays.FirstOrDefault(d => d.Index == Vm.Settings.DisplayIndex)
                                        ?? Vm.Displays.FirstOrDefault();

            FpsCombo.SelectedItem = FpsOptions.FirstOrDefault(o => (int)o.Value == Vm.Settings.Fps) ?? FpsOptions[1];
            ReplayCombo.SelectedItem = ReplayOptions.FirstOrDefault(o => (int)o.Value == Vm.Settings.ReplaySeconds) ?? ReplayOptions[1];
            PostCombo.SelectedItem = PostOptions.FirstOrDefault(o => (int)o.Value == Vm.Settings.PostTriggerSeconds) ?? PostOptions[0];
            QualityCombo.SelectedItem = QualityOptions.FirstOrDefault(o => (string)o.Value == Vm.Settings.QualityPreset) ?? QualityOptions[1];
            SourceCombo.SelectedItem = SourceOptions.FirstOrDefault(o => (string)o.Value == Vm.Settings.CaptureSourcePreference) ?? SourceOptions[0];
            EncoderCombo.SelectedItem = EncoderOptions.FirstOrDefault(o => (string)o.Value == Vm.Settings.EncoderPreference) ?? EncoderOptions[0];

            SystemDeviceCombo.SelectedItem = FindDevice(Vm.SystemAudioDevices, Vm.Settings.SystemAudioDeviceId);
            MicDeviceCombo.SelectedItem = FindDevice(Vm.MicrophoneDevices, Vm.Settings.MicrophoneDeviceId);
        }
        finally
        {
            _loading = false;
        }
    }

    private static AudioDeviceInfo? FindDevice(IReadOnlyList<AudioDeviceInfo> devices, string? id)
    {
        if (devices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(id)) return devices[0];
        return devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) ?? devices[0];
    }

    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedItem is not DisplayInfo display) return;
        Vm.Settings.DisplayIndex = display.Index;
    }

    private void Fps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FpsCombo.SelectedItem is not OptionItem option) return;
        Vm.Settings.Fps = (int)option.Value;
    }

    private void Replay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ReplayCombo.SelectedItem is not OptionItem option) return;
        Vm.Settings.ReplaySeconds = (int)option.Value;
    }

    private void Post_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PostCombo.SelectedItem is not OptionItem option) return;
        Vm.Settings.PostTriggerSeconds = (int)option.Value;
    }

    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QualityCombo.SelectedItem is not OptionItem option) return;
        Vm.Settings.QualityPreset = (string)option.Value;
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceCombo.SelectedItem is not OptionItem option) return;
        Vm.Settings.CaptureSourcePreference = (string)option.Value;
    }

    private void Encoder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EncoderCombo.SelectedItem is not OptionItem option) return;
        Vm.Settings.EncoderPreference = (string)option.Value;
    }

    private void SystemDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SystemDeviceCombo.SelectedItem is not AudioDeviceInfo device) return;
        Vm.Settings.SystemAudioDeviceId = device.Id;
    }

    private void MicDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicDeviceCombo.SelectedItem is not AudioDeviceInfo device) return;
        Vm.Settings.MicrophoneDeviceId = device.Id;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var ok = await Vm.SaveSettingsAsync();
        if (!ok) return;
        SaveStatus.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
        _statusTimer.Stop();
        _statusTimer.Start();
        SyncSelections();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "恢复默认设置会覆盖当前的捕获、音频、保存目录与快捷键配置。\n是否继续？",
            "恢复默认设置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        Vm.ResetSettings();
        SyncSelections();
        SaveStatus.Text = "已恢复默认设置";
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show($"诊断包已导出：\n{path}\n\n其中包含系统与编码信息、设置（不含隐私内容）和最近日志。",
                "导出诊断包", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出诊断包失败：" + ex.Message, "导出诊断包", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择片段保存目录",
            InitialDirectory = Directory.Exists(Vm.Settings.SaveFolder) ? Vm.Settings.SaveFolder : Vm.Services.Paths.DataDirectory
        };

        if (dialog.ShowDialog() != true) return;

        if (!AppPaths.CanWrite(dialog.FolderName, out var error))
        {
            MessageBox.Show("该目录不可写：" + error, "保存目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Vm.Settings.SaveFolder = dialog.FolderName;
        FolderBox.Text = dialog.FolderName;
    }

    private void OpenSaveFolder_Click(object sender, RoutedEventArgs e) => Vm.OpenFolder(Vm.Settings.SaveFolder);

    private void KeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    private void SaveKey_PreviewKeyDown(object sender, KeyEventArgs e) => CaptureHotkey(e, true);

    private void RecordKey_PreviewKeyDown(object sender, KeyEventArgs e) => CaptureHotkey(e, false);

    private void CaptureHotkey(KeyEventArgs e, bool saveHotkey)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var hotkeyModifiers = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) hotkeyModifiers |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) hotkeyModifiers |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) hotkeyModifiers |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) hotkeyModifiers |= HotkeyModifiers.Win;

        if (hotkeyModifiers == HotkeyModifiers.None)
        {
            MessageBox.Show("快捷键至少需要一个修饰键（Ctrl / Alt / Shift / Win）。", "快捷键",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var gesture = new HotkeyGesture(hotkeyModifiers, virtualKey);
        if (!HotkeyGesture.TryParse(gesture.ToString(), out _, out var parseError))
        {
            MessageBox.Show("这个按键不能用于全局快捷键：" + parseError, "快捷键",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Vm.SetHotkey(saveHotkey, gesture, out var error))
        {
            MessageBox.Show(error ?? "快捷键设置失败。", "快捷键", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Vm.RaiseStaticProperties();
        SaveStatus.Text = "快捷键已更新，点击“保存设置”写入磁盘";
    }
}
