using System.Globalization;
using System.Text;
using System.Text.Json;
using GameMoments.Core.Audio;
using GameMoments.Core.Capture;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0].ToLowerInvariant()
            : "help";
        var options = ParseOptions(args.Skip(command == "help" && args.Length > 0 && !args[0].StartsWith("--") ? 0 : 1).ToArray());

        try
        {
            return command switch
            {
                "doctor" or "probe" => Doctor(options),
                "devices" => Devices(options),
                "clips" => Clips(options),
                "capture" => await CaptureAsync(options),
                "record" => await CaptureAsync(options, manual: true),
                "detect" => GsiCommands.Detect(options),
                "gsi-status" => GsiCommands.Status(options),
                "gsi-install" => GsiCommands.Install(options),
                "gsi-remove" => GsiCommands.Remove(options),
                "gsi-serve" => GsiCommands.Serve(options),
                "gsi-smoke" => await GsiCommands.SmokeAsync(options),
                "smart-smoke" => await SmartSmoke.RunAsync(options),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"命令失败：{ex}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
GameMoments 命令行工具

用法：
  GameMoments.Cli doctor  [--data-dir PATH] [--ffmpeg PATH]
  GameMoments.Cli devices [--data-dir PATH]
  GameMoments.Cli clips   [--data-dir PATH]
  GameMoments.Cli capture [选项]      即时回放保存测试
  GameMoments.Cli record  [选项]      完整录制测试
  GameMoments.Cli detect  [--custom EXE] [--data-dir PATH]
                                      真实扫描游戏进程并打印支持矩阵
  GameMoments.Cli gsi-status  [--port N] [--target DIR] [--data-dir PATH]
                                      查看 CS2 的 GSI 配置状态（真实路径）
  GameMoments.Cli gsi-install [--port N] [--target DIR] [--data-dir PATH]
                                      写入本程序专属的 CS2 GSI 配置文件（可逆）
  GameMoments.Cli gsi-remove  [--target DIR] [--data-dir PATH]
                                      删除本程序写入的 GSI 配置（不动其他文件）
  （--target DIR 可指定自定义的 cfg 目录，默认走 Steam 库真实路径）
  GameMoments.Cli gsi-serve   [--port N] [--seconds N] [--data-dir PATH]
                                      启动真实事件源监听，等待游戏推送
  GameMoments.Cli gsi-smoke   [--port N] [--data-dir PATH]
  GameMoments.Cli smart-smoke [--port N] [--workdir PATH] [--hold SECONDS] [--data-dir PATH]
                             全链路：假游戏进程触发检测 → 真实捕获 → 真实 HTTP 事件 → 自动保存片段
                                      用真实 HTTP 服务器 + 合成报文验证事件链路

capture / record 选项：
  --seconds N       启动后等待多少秒再触发保存（默认 replay+2）
  --replay N        回放缓存秒数（15/30/60/120，默认 15）
  --post N          触发后额外录制秒数（0/5/10，默认 0）
  --fps N           30 或 60（默认 60）
  --quality ID      low / medium / high（默认 high）
  --source ID       auto / ddagrab / gdigrab
  --encoder ID      auto / hardware / software
  --display N       显示器序号（默认 0）
  --save-dir PATH   片段保存目录
  --data-dir PATH   数据目录（日志/缓存/索引）
  --ffmpeg PATH     ffmpeg.exe 路径
  --no-audio        不录制系统声音
  --mic             同时录制麦克风
  --title TEXT      片段标题
  --json            以 JSON 输出结果
""");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        return Help();
    }

    private static int Doctor(Dictionary<string, string?> options)
    {
        var context = CreateContext(options, out var error);
        if (context is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var (paths, logger, tools, capabilities) = context.Value;
        Console.WriteLine("== 环境检查 ==");
        Console.WriteLine($"数据目录      : {paths.DataDirectory}");
        Console.WriteLine($"默认保存目录  : {paths.DefaultSaveDirectory}");
        Console.WriteLine($"ffmpeg        : {tools.FfmpegPath}");
        Console.WriteLine($"ffmpeg 版本   : {tools.Version}");
        Console.WriteLine($"硬件编码器    : {(capabilities.HasHardwareEncoder ? string.Join(", ", HardwareNames(capabilities)) : "未检测到")}");
        Console.WriteLine($"捕获后端      : {(capabilities.Ddagrab ? "ddagrab " : string.Empty)}{(capabilities.Gdigrab ? "gdigrab" : string.Empty)}");
        Console.WriteLine($"软件编码      : {(capabilities.Libx264 ? "libx264" : "无")}");
        Console.WriteLine();

        Console.WriteLine("== 显示器 ==");
        foreach (var display in Displays.Enumerate())
        {
            Console.WriteLine($"#{display.Index} {display.Label} {display.SizeText} 主屏={display.IsPrimary} 设备={display.DeviceName}");
        }
        Console.WriteLine();

        Console.WriteLine("== 音频设备 ==");
        PrintDevices(AudioDeviceKind.Render, "回放（系统声）");
        PrintDevices(AudioDeviceKind.Capture, "录制（麦克风）");
        Console.WriteLine();

        var saveFolder = options.GetValueOrDefault("save-dir") ?? paths.DefaultSaveDirectory;
        Console.WriteLine($"保存目录可写  : {AppPaths.CanWrite(saveFolder, out var writeError)} {writeError}");
        Console.WriteLine($"剩余空间      : {AppPaths.FormatBytes(AppPaths.GetFreeBytes(saveFolder) ?? 0)}");
        Console.WriteLine($"日志目录      : {paths.LogDirectory}");
        logger.Dispose();
        return 0;
    }

    private static int Devices(Dictionary<string, string?> options)
    {
        var context = CreateContext(options, out var error, requireFfmpeg: false);
        if (context is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        PrintDevices(AudioDeviceKind.Render, "回放（系统声）");
        PrintDevices(AudioDeviceKind.Capture, "录制（麦克风）");
        context.Value.Logger.Dispose();
        return 0;
    }

    private static int Clips(Dictionary<string, string?> options)
    {
        var context = CreateContext(options, out var error, requireFfmpeg: false);
        if (context is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var (paths, logger, _, _) = context.Value;
        var library = new ClipLibrary(new ClipIndexStore(paths.ClipsIndexFile, logger), logger);
        var clips = library.All;
        Console.WriteLine($"片段索引：{paths.ClipsIndexFile}（{clips.Count} 条）");
        foreach (var clip in clips)
        {
            Console.WriteLine($"{clip.CreatedText}  {clip.DurationText}  {clip.ResolutionText}  {clip.SizeText}  {clip.FileName}");
        }
        logger.Dispose();
        return 0;
    }

    private static async Task<int> CaptureAsync(Dictionary<string, string?> options, bool manual = false)
    {
        var context = CreateContext(options, out var error);
        if (context is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var (paths, logger, tools, capabilities) = context.Value;
        var settings = new AppSettings
        {
            SaveFolder = options.GetValueOrDefault("save-dir") ?? Path.Combine(paths.DataDirectory, "clips"),
            ReplaySeconds = IntOption(options, "replay", 15),
            PostTriggerSeconds = IntOption(options, "post", 0),
            Fps = IntOption(options, "fps", 60),
            QualityPreset = options.GetValueOrDefault("quality") ?? "high",
            CaptureSourcePreference = options.GetValueOrDefault("source") ?? "auto",
            EncoderPreference = options.GetValueOrDefault("encoder") ?? "auto",
            DisplayIndex = IntOption(options, "display", 0),
            CaptureSystemAudio = !options.ContainsKey("no-audio"),
            CaptureMicrophone = options.ContainsKey("mic"),
            FfmpegPath = options.GetValueOrDefault("ffmpeg"),
        };
        var warnings = settings.Normalize(paths.DefaultSaveDirectory);
        foreach (var warning in warnings) Console.WriteLine($"设置提示：{warning}");

        var displays = Displays.Enumerate();
        if (displays.Count == 0)
        {
            Console.Error.WriteLine("没有可用显示器。");
            return 2;
        }
        var display = displays.FirstOrDefault(d => d.Index == settings.DisplayIndex) ?? displays[0];

        var library = new ClipLibrary(new ClipIndexStore(paths.ClipsIndexFile, logger), logger);
        var assembler = new ClipAssembler(tools, paths, logger);
        var engine = new CaptureEngine(paths, logger, tools, capabilities);

        var lastLine = string.Empty;
        engine.Changed += () =>
        {
            var line = $"{engine.State} | {engine.StatusMessage}";
            if (line == lastLine) return;
            lastLine = line;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        };

        Console.WriteLine($"显示器：{display.Label} {display.SizeText}；保存目录：{settings.SaveFolder}");
        var started = await engine.StartAsync(settings, display, library, assembler);
        if (!started.Success)
        {
            Console.Error.WriteLine($"启动失败：{started.Error}");
            await engine.DisposeAsync();
            logger.Dispose();
            return 3;
        }
        Console.WriteLine($"捕获方式：{engine.Profile?.DisplayName}");

        var wait = IntOption(options, "seconds", 0);
        if (wait <= 0) wait = Math.Max(6, settings.ReplaySeconds + 2);
        var title = options.GetValueOrDefault("title");
        SaveOutcome outcome;

        if (manual)
        {
            var startResult = await engine.StartManualRecordingAsync();
            if (!startResult.Success)
            {
                Console.Error.WriteLine($"开始录制失败：{startResult.Error}");
                await engine.ShutdownAsync();
                logger.Dispose();
                return 4;
            }
            Console.WriteLine($"完整录制中，等待 {wait}s…");
            await Task.Delay(TimeSpan.FromSeconds(wait));
            outcome = await engine.StopManualRecordingAsync(title);
        }
        else
        {
            Console.WriteLine($"缓存建立中，等待 {wait}s 后触发保存…");
            for (var i = 0; i < wait; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"  {i + 1}/{wait}s 缓存 {engine.CacheSeconds:0.0}s 帧率 {engine.CapturedFps:0.0}");
            }
            outcome = await engine.SaveReplayAsync(title);
        }

        await engine.ShutdownAsync();
        logger.Dispose();

        if (!outcome.Success)
        {
            Console.Error.WriteLine($"保存失败：{outcome.Error}");
            return 5;
        }

        var clip = outcome.Clip!;
        if (options.ContainsKey("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("保存成功：");
            Console.WriteLine($"  文件   : {clip.FilePath}");
            Console.WriteLine($"  时长   : {clip.DurationText} ({clip.DurationSeconds:0.000}s)");
            Console.WriteLine($"  画面   : {clip.ResolutionText} @ {clip.FpsText}");
            Console.WriteLine($"  大小   : {clip.SizeText}");
            Console.WriteLine($"  音频   : {(clip.HasAudio ? clip.AudioSummary : "无")}");
            Console.WriteLine($"  缩略图 : {clip.ThumbnailPath ?? "无"}");
        }
        return 0;
    }

    private static (AppPaths Paths, AppLogger Logger, FfmpegTools Tools, FfmpegCapabilities Capabilities)? CreateContext(
        Dictionary<string, string?> options,
        out string? error,
        bool requireFfmpeg = true)
    {
        error = null;
        var dataDirectory = options.GetValueOrDefault("data-dir");
        var paths = new AppPaths(dataDirectory);
        var logger = new AppLogger(paths.LogDirectory);

        if (!requireFfmpeg)
        {
            return (paths, logger, null!, null!);
        }

        var tools = FfmpegTools.Locate(options.GetValueOrDefault("ffmpeg"), paths.DataDirectory, logger, out var locateError);
        if (tools is null)
        {
            error = locateError;
            logger.Dispose();
            return null;
        }

        var capabilities = FfmpegCapabilities.Detect(tools, logger);
        return (paths, logger, tools, capabilities);
    }

    private static void PrintDevices(AudioDeviceKind kind, string title)
    {
        Console.WriteLine($"-- {title} --");
        try
        {
            var devices = AudioDeviceService.Enumerate(kind);
            if (devices.Count == 0)
            {
                Console.WriteLine("  （没有设备）");
                return;
            }
            foreach (var device in devices)
            {
                Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.DisplayName}  [{device.Id[..Math.Min(8, device.Id.Length)]}…]");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  枚举失败：{ex.Message}");
        }
    }

    private static IEnumerable<string> HardwareNames(FfmpegCapabilities capabilities)
    {
        if (capabilities.Nvenc) yield return "h264_nvenc";
        if (capabilities.Amf) yield return "h264_amf";
        if (capabilities.Qsv) yield return "h264_qsv";
        if (capabilities.MediaFoundation) yield return "h264_mf";
    }

    private static int IntOption(Dictionary<string, string?> options, string key, int fallback)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var body = arg[2..];
            var equals = body.IndexOf('=');
            if (equals >= 0)
            {
                result[body[..equals]] = body[(equals + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[body] = args[++i];
            }
            else
            {
                result[body] = "true";
            }
        }
        return result;
    }
}
