using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using GameMoments.Core.Capture;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Gaming;
using GameMoments.Core.Logging;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.Cli;

/// <summary>
/// 智能录制全链路冒烟：用一个「假游戏进程」（把系统自带程序复制成 cs2.exe 后启动）
/// 触发真实进程检测 → 真实 ffmpeg 捕获 → 真实 HTTP 事件源接收合成 GSI 报文 →
/// 真实规则引擎判定 → 真实保存片段到磁盘。
/// 除「游戏本体」外，其余环节全部是生产代码路径；报文体为合成数据，不代表真实对局。
/// </summary>
internal static class SmartSmoke
{
    public static async Task<int> RunAsync(Dictionary<string, string?> options)
    {
        var dataDirectory = options.GetValueOrDefault("data-dir");
        var workspace = options.GetValueOrDefault("workdir")
                        ?? Path.Combine(Path.GetTempPath(), "GameMoments-smart-smoke");
        workspace = Path.GetFullPath(workspace);
        var clipsDirectory = Path.Combine(workspace, "clips");
        Directory.CreateDirectory(clipsDirectory);
        Directory.CreateDirectory(dataDirectory ?? Path.Combine(workspace, "data"));

        var paths = new AppPaths(dataDirectory ?? Path.Combine(workspace, "data"));
        var logger = new AppLogger(paths.LogDirectory);
        var port = options.TryGetValue("port", out var rawPort) && int.TryParse(rawPort, out var parsedPort) ? parsedPort : 48211;
        var holdSeconds = options.TryGetValue("hold", out var rawHold) && int.TryParse(rawHold, out var hold) ? hold : 3;

        var failures = new List<string>();
        Process? fake = null;
        SmartRecordingService? smart = null;
        CaptureEngine? engine = null;

        try
        {
            var tools = FfmpegTools.Locate(options.GetValueOrDefault("ffmpeg"), paths.DataDirectory, logger, out var toolError);
            if (tools is null)
            {
                Console.Error.WriteLine($"找不到 ffmpeg：{toolError}");
                return 2;
            }

            var capabilities = FfmpegCapabilities.Detect(tools, logger);
            var settings = BuildSettings(clipsDirectory, port);
            var warnings = settings.Normalize(clipsDirectory);
            foreach (var warning in warnings) Console.WriteLine($"设置修正：{warning}");

            var library = new ClipLibrary(new ClipIndexStore(paths.ClipsIndexFile, logger), logger);
            var assembler = new ClipAssembler(tools, paths, logger);
            engine = new CaptureEngine(paths, logger, tools, capabilities);

            Console.WriteLine("== 智能录制全链路冒烟（假游戏进程 + 真实捕获 + 真实事件源）==");
            Console.WriteLine($"工作目录      : {workspace}");
            Console.WriteLine($"保存目录      : {clipsDirectory}");
            Console.WriteLine($"模拟游戏进程  : cs2.exe（系统程序副本，仅用于触发检测）");
            Console.WriteLine();

            fake = StartFakeGame(workspace);
            if (fake is null)
            {
                Console.Error.WriteLine("无法启动假游戏进程。");
                return 2;
            }

            Console.WriteLine($"[1/4] 已启动假游戏进程 cs2.exe（PID {fake.Id}）");

            smart = new SmartRecordingService(logger, paths, library, () => settings, () => engine, () => assembler);
            smart.Notice += message => Console.WriteLine($"      提示：{message}");
            smart.Events.RecordAdded += record =>
                Console.WriteLine($"      事件：{record.KindLabel} · {record.Title} → {record.OutcomeText}（{record.Message}）");

            var (started, startError) = await smart.StartAsync();
            if (!started)
            {
                Console.Error.WriteLine($"智能录制启动失败：{startError}");
                return 2;
            }

            Console.WriteLine($"[2/4] 智能录制已启动：{smart.GsiEndpointText}；{smart.DetectionText}");
            Console.WriteLine($"      GSI 事件源：{smart.Cs2ConfigStatus?.Detail ?? "未检查"}");

            var ready = await WaitAsync(() => engine.IsRunning, TimeSpan.FromSeconds(40));
            Console.WriteLine($"[3/4] 捕获状态：{engine.State}（缓存 {engine.CacheSeconds:0.0} 秒）；检测：{smart.DetectionText}");
            if (!ready)
            {
                failures.Add("捕获未能在 40 秒内启动");
            }
            else
            {
                // 等缓存积累到事件前时长，保证事件前的分段存在。
                var need = settings.GetGameConfig(GameCatalog.Cs2GameId).PreSeconds + 4;
                await WaitAsync(() => engine.CacheSeconds >= need, TimeSpan.FromSeconds(need + 20));
                Console.WriteLine($"      缓存已积累 {engine.CacheSeconds:0.0} 秒（需要 ≥ {need} 秒）");

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var pushed = 0;
                foreach (var (name, payload, expected) in GsiCommands.BuildPayloadSteps())
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    try
                    {
                        using var response = await http.PostAsync($"http://127.0.0.1:{smart.GsiPort}/cs2", content);
                        Console.WriteLine($"      推送「{name}」→ HTTP {(int)response.StatusCode}；期望事件 {(expected.Count == 0 ? "无" : string.Join(", ", expected))}");
                        pushed++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"推送「{name}」失败：{ex.Message}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, holdSeconds)));
                }

                Console.WriteLine($"[4/4] 已推送 {pushed} 帧状态；等待事件片段落盘…");
                var savedDecisions = smart.Events.History.Count(r => r.Outcome == EventOutcome.Saved);
                var saved = await WaitAsync(
                    () => savedDecisions > 0 && engine.PendingSaves == 0 && library.All.Count >= savedDecisions,
                    TimeSpan.FromSeconds(120));
                Console.WriteLine($"      判定为「已触发保存」的事件 {savedDecisions} 个，已落盘 {library.All.Count} 个，待处理保存 {engine.PendingSaves} 个");

                Console.WriteLine();
                Console.WriteLine("== 事件记录 ==");
                foreach (var record in smart.Events.History.ToList())
                {
                    Console.WriteLine($"  {record.TimeText}  {record.KindLabel,-8}  {record.OutcomeText,-14}  {record.Message}");
                    if (record.HasClip) Console.WriteLine($"      片段：{record.ClipName}");
                }

                Console.WriteLine();
                Console.WriteLine("== 片段库 ==");
                var clips = library.All.ToList();
                foreach (var clip in clips)
                {
                    var exists = File.Exists(clip.FilePath);
                    Console.WriteLine($"  {clip.FileName}  {clip.DurationText}  {clip.SizeText}  {clip.ResolutionText}  游戏={clip.GameName ?? "-"}  事件={clip.EventKind ?? "-"}  存在={exists}");
                    if (!exists) failures.Add($"片段文件不存在：{clip.FilePath}");
                }

                var savedCount = smart.Events.History.Count(r => r.IsSaved);
                Console.WriteLine();
                Console.WriteLine($"保存请求数    : {savedCount}");
                Console.WriteLine($"片段数        : {clips.Count}");
                if (!saved) failures.Add("120 秒内事件片段未全部落盘");
                if (clips.Count == 0) failures.Add("片段库为空");
                if (clips.All(c => string.IsNullOrEmpty(c.GameName))) failures.Add("片段未携带游戏/事件标记");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"异常：{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            if (smart is not null)
            {
                try { await smart.StopAsync(); } catch { }
                smart.Dispose();
            }

            if (engine is not null)
            {
                try { await engine.StopAsync(saveManualRecording: false); } catch { }
                await engine.DisposeAsync();
            }

            if (fake is not null && !fake.HasExited)
            {
                try { fake.Kill(true); } catch { }
            }

            logger.Dispose();
        }

        if (failures.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("结果：全链路通过（假进程触发检测 → 真实捕获 → 真实 HTTP 事件 → 规则引擎 → 真实片段落盘）。");
            Console.WriteLine("说明：游戏本体是系统程序副本，报文是合成数据；真实 CS2 对局需要在本机启动游戏验证。");
            return 0;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("结果：冒烟未通过");
        foreach (var failure in failures) Console.Error.WriteLine("  - " + failure);
        return 1;
    }

    private static AppSettings BuildSettings(string clipsDirectory, int port)
    {
        var settings = new AppSettings
        {
            SaveFolder = clipsDirectory,
            FileNamePrefix = "Smoke",
            ReplaySeconds = 15,
            PostTriggerSeconds = 2,
            Fps = 30,
            QualityPreset = "low",
            CaptureSourcePreference = "auto",
            EncoderPreference = "auto",
            CaptureSystemAudio = false,
            CaptureMicrophone = false,
            SmartRecordingEnabled = true,
            SmartGsiPort = port,
            InstallGsiConfig = false,   // 冒烟测试绝不碰真实 Steam 目录
            AutoStartCapture = false,
            WholeRecordingMinutes = 0,
            WholeRecordingStopDelaySeconds = 2,
        };

        settings.CustomGameExecutables.Add("cs2.exe");
        settings.GameConfigs[GameCatalog.Cs2GameId] = new GameSettings
        {
            Enabled = true,
            Mode = GameModes.Events,
            PreSeconds = 8,
            PostSeconds = 3,
            CooldownSeconds = 3,
            MergeWindowSeconds = 4,
            MinConfidence = 0.8,
            AutoSaveEvents = true,
        };
        return settings;
    }

    private static Process? StartFakeGame(string workspace)
    {
        var source = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!File.Exists(source)) return null;

        var executable = Path.Combine(workspace, "cs2.exe");
        try
        {
            File.Copy(source, executable, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"复制假游戏程序失败：{ex.Message}");
            return null;
        }

        var info = new ProcessStartInfo(executable, "-n 600 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workspace,
        };
        return Process.Start(info);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }

        return condition();
    }
}
