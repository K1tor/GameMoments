using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using GameMoments.Core.Capture;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.App.Services;

/// <summary>应用级服务容器：路径、日志、设置、片段库、ffmpeg 与捕获引擎。</summary>
public sealed class AppServices
{
    public AppPaths Paths { get; private set; } = null!;
    public AppLogger Logger { get; private set; } = null!;
    public SettingsStore Store { get; private set; } = null!;
    public AppSettings Settings => Store.Current;
    public ClipLibrary Library { get; private set; } = null!;
    public ClipAssembler? Assembler { get; private set; }
    public FfmpegTools? Tools { get; private set; }
    public FfmpegCapabilities? Capabilities { get; private set; }
    public CaptureEngine? Engine { get; private set; }
    public string? SetupError { get; private set; }
    public string? AudioError { get; internal set; }

    public string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

    public void Initialize()
    {
        Paths = new AppPaths();
        Paths.CleanupStaleTemp(TimeSpan.FromHours(8));
        Logger = new AppLogger(Paths.LogDirectory);
        Store = new SettingsStore(Paths.SettingsFile, Paths.DefaultSaveDirectory, Logger);
        Store.Load();
        EnsureSaveFolder();

        Library = new ClipLibrary(new ClipIndexStore(Paths.ClipsIndexFile, Logger), Logger);
        Logger.Info("应用", "GameMoments 启动", $"版本 {Version}，数据目录 {Privacy.Redact(Paths.DataDirectory)}");
    }

    /// <summary>若配置的保存目录不可写，回退到数据目录下的 clips，并在界面与日志中说明。</summary>
    public void EnsureSaveFolder()
    {
        var folder = Settings.SaveFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Paths.DefaultSaveDirectory;
        }

        if (AppPaths.CanWrite(folder, out var error))
        {
            Settings.SaveFolder = folder;
            return;
        }

        var fallback = Path.Combine(Paths.DataDirectory, "clips");
        Directory.CreateDirectory(fallback);
        Settings.SaveFolder = fallback;
        Store.Save(Settings);
        Logger.Warn("保存目录", $"保存目录不可写，已改用 {Privacy.Redact(fallback)}", error);
    }

    public async Task DetectAsync()
    {
        SetupError = null;
        var result = await Task.Run(() =>
        {
            var tools = FfmpegTools.Locate(Settings.FfmpegPath, Paths.DataDirectory, Logger, out var error);
            if (tools is null)
            {
                return (Tools: (FfmpegTools?)null, Caps: (FfmpegCapabilities?)null, Error: error);
            }
            var caps = FfmpegCapabilities.Detect(tools, Logger);
            return (Tools: (FfmpegTools?)tools, Caps: (FfmpegCapabilities?)caps, Error: (string?)null);
        }).ConfigureAwait(true);

        Tools = result.Tools;
        Capabilities = result.Caps;
        SetupError = result.Error;

        if (Tools is null || Capabilities is null)
        {
            Logger.Error("FFmpeg", "未找到可用的 ffmpeg，捕获功能不可用", null, SetupError);
            return;
        }

        Assembler = new ClipAssembler(Tools, Paths, Logger);
        Engine = new CaptureEngine(Paths, Logger, Tools, Capabilities);
    }

    public void SaveSettings()
    {
        Store.Save(Settings);
        EnsureSaveFolder();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            if (Engine is not null)
            {
                await Engine.ShutdownAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("应用", "退出时停止捕获失败", ex);
        }
        finally
        {
            Logger.Info("应用", "GameMoments 退出");
            Logger.Dispose();
        }
    }
}
