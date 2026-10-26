using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GameMoments.App.Services;

namespace GameMoments.App;

public partial class App : System.Windows.Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => TryLogFatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryLogFatal(args.Exception);
            args.SetObserved();
        };

        try
        {
            _services = new AppServices();
            _services.Initialize();
            var window = new MainWindow(_services);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            TryLogFatal(ex);
            MessageBox.Show(
                "GameMoments 无法启动：\n\n" + ex.Message +
                "\n\n请检查数据目录权限，或查看 %LOCALAPPDATA%\\GameMoments\\logs 下的日志。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLogFatal(e.Exception);
        MessageBox.Show(
            "发生未处理的错误，录制可能已停止：\n\n" + e.Exception.Message +
            "\n\n应用会继续运行，建议在“日志与诊断”中导出诊断包。",
            "GameMoments 错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void TryLogFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            _services?.Logger.Error("应用", "未处理异常", ex);
        }
        catch
        {
            // 日志系统自身异常不能再次抛出。
        }
    }
}
