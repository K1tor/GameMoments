using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GameMoments.App.Services;
using GameMoments.App.ViewModels;
using GameMoments.App.Views;
using GameMoments.Core.Clips;
using GameMoments.Core.Hotkeys;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GameMoments.App;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeySaveId = 0x4A01;
    private const int HotkeyRecordId = 0x4A02;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _snackTimer;
    private readonly System.Collections.Generic.Dictionary<string, FrameworkElement> _pages = new();

    private WinForms.NotifyIcon? _tray;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _exiting;
    private bool _forceClose;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _viewModel = new MainViewModel(services, Dispatcher);
        DataContext = _viewModel;

        _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
        _snackTimer.Tick += (_, _) =>
        {
            _snackTimer.Stop();
            Snackbar.Visibility = Visibility.Collapsed;
        };

        _viewModel.ClipSaved += OnClipSaved;
        _viewModel.Notice += ShowSnack;
        _viewModel.HotkeysChanged += RegisterHotkeys;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        ShowPage("home");
        CreateTrayIcon();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        RegisterHotkeys();
        UpdateTrayText();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        // Win11 深色标题栏；失败无副作用。
        var enabled = 1;
        try
        {
            DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch
        {
            // 旧系统不支持该属性。
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == HotkeySaveId)
            {
                handled = true;
                _ = Dispatcher.InvokeAsync(async () => await _viewModel.SaveReplayAsync());
            }
            else if (id == HotkeyRecordId)
            {
                handled = true;
                _ = Dispatcher.InvokeAsync(async () => await _viewModel.ToggleRecordAsync());
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>注册全局快捷键；失败原因写入界面与日志，绝不让功能失效而无提示。</summary>
    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        HotkeyRegistrar.Unregister(_hwnd, HotkeySaveId);
        HotkeyRegistrar.Unregister(_hwnd, HotkeyRecordId);

        var errors = new System.Collections.Generic.List<string>();
        if (HotkeyGesture.TryParse(_services.Settings.SaveHotkey, out var save, out var saveError) && save is not null)
        {
            if (!HotkeyRegistrar.TryRegister(_hwnd, HotkeySaveId, save, out var error)) errors.Add(error ?? "保存快捷键注册失败。");
        }
        else
        {
            errors.Add("保存快捷键无效：" + saveError);
        }

        if (HotkeyGesture.TryParse(_services.Settings.ToggleRecordingHotkey, out var record, out var recordError) && record is not null)
        {
            if (!HotkeyRegistrar.TryRegister(_hwnd, HotkeyRecordId, record, out var error)) errors.Add(error ?? "录制快捷键注册失败。");
        }
        else
        {
            errors.Add("录制快捷键无效：" + recordError);
        }

        _viewModel.HotkeyErrorText = string.Join("  ", errors);
        if (errors.Count > 0)
        {
            _services.Logger.Warn("快捷键", "全局快捷键未能全部注册", _viewModel.HotkeyErrorText);
            ShowSnack("快捷键注册失败：" + string.Join("；", errors));
        }
        else
        {
            _services.Logger.Info("快捷键", "全局快捷键已注册",
                $"{_services.Settings.SaveHotkey} / {_services.Settings.ToggleRecordingHotkey}");
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page }) ShowPage(page);
    }

    /// <summary>供页面内按钮跳转导航。</summary>
    public void Navigate(string page)
    {
        switch (page)
        {
            case "library": NavLibrary.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
            case "logs": NavLogs.IsChecked = true; break;
            default: NavHome.IsChecked = true; break;
        }
        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        // XAML 中的 IsChecked="True" 会在 InitializeComponent 期间触发 Checked，
        // 此时 PageHost 尚未赋值，直接忽略；构造末尾会主动调用 ShowPage("home")。
        if (PageHost is null || PageTitle is null) return;

        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "library" => new LibraryView(),
                "settings" => new SettingsView(),
                "logs" => new LogsView(),
                _ => new HomeView()
            };
            view.DataContext = _viewModel;
            _pages[page] = view;
        }

        PageHost.Content = view;
        PageTitle.Text = page switch
        {
            "library" => "片段库",
            "settings" => "设置",
            "logs" => "日志与诊断",
            _ => "首页"
        };

        if (page == "library") _viewModel.RefreshClips();
        if (page == "settings") _viewModel.RefreshDevices();
    }

    private async void SaveReplay_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveReplayAsync();

    private void FirstRunSettings_Click(object sender, RoutedEventArgs e)
    {
        NavSettings.IsChecked = true;
        _viewModel.CompleteFirstRun();
    }

    private void FirstRunStart_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CompleteFirstRun();
        ShowPage("home");
    }

    private void OnClipSaved(ClipRecord clip)
    {
        ShowSnack($"已保存 {clip.FileName} · {clip.DurationText} · {clip.SizeText}");
        if (_tray is not null && _services.Settings.ShowTrayNotifications && _services.Settings.ShowSaveNotifications)
        {
            try
            {
                _tray.ShowBalloonTip(2500, "精彩时刻已保存", $"{clip.FileName}\n{clip.DurationText} · {clip.SizeText}", WinForms.ToolTipIcon.Info);
            }
            catch
            {
                // 通知失败不影响保存结果。
            }
        }
        UpdateTrayText();
    }

    private void ShowSnack(string message)
    {
        SnackbarText.Text = message;
        Snackbar.Visibility = Visibility.Visible;
        _snackTimer.Stop();
        _snackTimer.Start();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _services.Settings.MinimizeToTray)
        {
            Hide();
            ShowSnack("已最小化到托盘，录制仍在继续。");
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;

        if (!_forceClose && _services.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            ShowSnack("已隐藏到托盘，录制仍在继续；右键托盘图标可退出。");
            return;
        }

        e.Cancel = true;
        _exiting = true;
        try
        {
            var engine = _viewModel.Engine;
            if (engine is not null && engine.IsRunning && engine.IsManualRecording)
            {
                var outcome = await engine.StopManualRecordingAsync();
                if (!outcome.Success)
                {
                    _services.Logger.Warn("退出", "退出前保存完整录制失败", outcome.Error);
                }
            }
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("退出", "退出清理失败", ex);
        }
        finally
        {
            DisposeTray();
            if (_hwndSource is not null)
            {
                if (_hwnd != IntPtr.Zero)
                {
                    HotkeyRegistrar.Unregister(_hwnd, HotkeySaveId);
                    HotkeyRegistrar.Unregister(_hwnd, HotkeyRecordId);
                }
                _hwndSource.RemoveHook(WndProc);
            }
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void CreateTrayIcon()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            var openItem = new WinForms.ToolStripMenuItem("打开 GameMoments");
            openItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
            var saveItem = new WinForms.ToolStripMenuItem("保存精彩时刻");
            saveItem.Click += (_, _) => Dispatcher.InvokeAsync(async () => await _viewModel.SaveReplayAsync());
            var captureItem = new WinForms.ToolStripMenuItem("开始 / 停止捕获");
            captureItem.Click += (_, _) => Dispatcher.InvokeAsync(async () => await _viewModel.ToggleCaptureAsync());
            var exitItem = new WinForms.ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);
            menu.Items.Add(openItem);
            menu.Items.Add(saveItem);
            menu.Items.Add(captureItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray = new WinForms.NotifyIcon
            {
                Icon = CreateAppIcon(),
                Text = "GameMoments 精彩时刻",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("托盘", "创建托盘图标失败", ex.Message);
        }
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        try
        {
            var engine = _viewModel.Engine;
            _tray.Text = engine is null
                ? "GameMoments 精彩时刻"
                : engine.IsManualRecording
                    ? "GameMoments · 完整录制中"
                    : engine.IsRunning
                        ? $"GameMoments · 缓存 {engine.CacheSeconds:0} 秒"
                        : "GameMoments · 未启动";
        }
        catch
        {
            // 托盘文本长度受限，失败忽略。
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _forceClose = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("退出", "关闭窗口失败", ex);
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void DisposeTray()
    {
        if (_tray is null) return;
        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch
        {
            // 退出阶段忽略。
        }
        finally
        {
            _tray = null;
        }
    }

    private static Drawing.Icon CreateAppIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(229, 72, 77));
            graphics.FillEllipse(background, 0, 0, 31, 31);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            var play = new[]
            {
                new Drawing.PointF(12, 9),
                new Drawing.PointF(24, 16),
                new Drawing.PointF(12, 23)
            };
            graphics.FillPolygon(white, play);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
